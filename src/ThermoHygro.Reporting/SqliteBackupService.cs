using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ThermoHygro.Core;

namespace ThermoHygro.Reporting;

/// <summary>
/// 本地库备份与恢复。
/// 关键约定：
///  * 使用 SQLite 在线备份接口（Connection.BackupDatabase），不直接复制主文件，避免漏掉 WAL 内容；
///  * 保留策略：先成功备份并校验通过，才清理超出份数的旧备份；校验失败或删除失败都不影响已有备份；
///  * 恢复：调用方必须已停止采集与同步；在临时路径校验后才替换现库；替换前自动备份现库。
/// </summary>
public sealed class SqliteBackupService : IBackupService
{
    private readonly string _dbPath;
    public string DatabasePath => _dbPath;

    /// <summary>默认保留最近 30 份成功备份。</summary>
    public int RetentionCount { get; set; } = 30;

    public SqliteBackupService(string databasePath) => _dbPath = databasePath;

    private string DefaultDirectory => Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", "Backups");

    public async Task<(BackupEntry? Entry, string Error)> BackupAsync(string? targetDirectory = null, CancellationToken ct = default)
    {
        var dir = targetDirectory ?? DefaultDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(dir, $"history-{stamp}.db");
            var tmp = target + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);

            // 写入端连接可能仍在池中持有数据库文件，不清池会导致备份报“文件被占用”。
            SqliteConnection.ClearAllPools();

            // 在线备份：包含 WAL 中已提交内容
            // 连接必须完全关闭后才可对 .tmp 做校验/移动；
            // Pooling=False 保证 Dispose 立即关闭底层句柄。
            {
                using var src = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
                using var dst = new SqliteConnection($"Data Source={tmp};Pooling=False");
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
            }
            SqliteConnection.ClearAllPools();

            // 校验新备份（结构 + 可读性）后才承认成功
            var verify = await VerifyAsync(tmp, ct);
            if (!verify.Ok)
            {
                TryDelete(tmp);
                return (null, "新备份校验未通过：" + verify.Detail + "。未删除任何旧备份。");
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);

            var fi = new FileInfo(target);
            var entry = new BackupEntry
            {
                FilePath = target,
                CreatedUtc = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero),
                SizeBytes = fi.Length,
                Verified = true,
                Note = $"样本 {verify.SampleCount} / 分钟 {verify.MinuteCount} / 事件 {verify.EventCount} / 阈值 {verify.ThresholdCount}"
            };
            return (entry, "");
        }
        catch (Exception ex)
        {
            return (null, "备份失败：" + ex.Message);
        }
    }

    public async Task<BackupVerifyResult> VerifyAsync(string backupFilePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(backupFilePath))
                return new BackupVerifyResult { Ok = false, Detail = "备份文件不存在。" };

            // Pooling=False：否则校验连接会退回连接池并持续占用文件，
            // 使后续 File.Move 报“文件被另一个进程使用”。
            await using var conn = new SqliteConnection($"Data Source={backupFilePath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync(ct);

            async Task<long> CountAsync(string table)
            {
                await using var c = conn.CreateCommand();
                c.CommandText = $"SELECT COUNT(*) FROM {table}";
                return Convert.ToInt64(await c.ExecuteScalarAsync(ct) ?? 0L, CultureInfo.InvariantCulture);
            }

            // 结构完整性 + 逻辑完整性
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "PRAGMA integrity_check";
                var r = Convert.ToString(await c.ExecuteScalarAsync(ct) ?? "", CultureInfo.InvariantCulture);
                if (!string.Equals(r, "ok", StringComparison.OrdinalIgnoreCase))
                    return new BackupVerifyResult { Ok = false, Detail = "integrity_check 返回：" + r };
            }

            long schema = 0;
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT MAX(Version) FROM SchemaVersion";
                var v = await c.ExecuteScalarAsync(ct);
                schema = v is null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            if (schema > 1)
                return new BackupVerifyResult { Ok = false, Detail = $"备份结构版本 {schema} 高于本程序支持版本 1，拒绝使用。" };

            return new BackupVerifyResult
            {
                Ok = true,
                Detail = "integrity_check 通过，结构版本 " + schema,
                SchemaVersion = schema,
                SampleCount = await CountAsync("SampleJournal"),
                MinuteCount = await CountAsync("MinuteSummary"),
                EventCount = await CountAsync("MonitorEvent"),
                ThresholdCount = await CountAsync("ThresholdVersion")
            };
        }
        catch (Exception ex)
        {
            return new BackupVerifyResult { Ok = false, Detail = "校验异常：" + ex.Message };
        }
    }

    public Task<IReadOnlyList<BackupEntry>> ListAsync(string? directory = null, CancellationToken ct = default)
    {
        var dir = directory ?? DefaultDirectory;
        var list = new List<BackupEntry>();
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<BackupEntry>>(list);
        foreach (var f in Directory.GetFiles(dir, "history-*.db").OrderByDescending(x => x))
        {
            var fi = new FileInfo(f);
            list.Add(new BackupEntry
            {
                FilePath = f,
                CreatedUtc = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero),
                SizeBytes = fi.Length,
                Verified = false,
                Note = "未校验（可点“验证备份”确认）"
            });
        }
        return Task.FromResult<IReadOnlyList<BackupEntry>>(list);
    }

    public async Task<(int Deleted, string Detail)> CleanupAsync(string? directory = null, CancellationToken ct = default)
    {
        var dir = directory ?? DefaultDirectory;
        var all = (await ListAsync(dir, ct)).OrderByDescending(b => b.FilePath).ToList();
        if (all.Count <= RetentionCount)
            return (0, $"当前 {all.Count} 份，未超过保留上限 {RetentionCount} 份，无需清理。");

        int deleted = 0, failed = 0;
        foreach (var b in all.Skip(RetentionCount))
        {
            // 删除前再次校验：只删“确实可读”的旧备份；删除失败不影响已有备份
            var v = await VerifyAsync(b.FilePath, ct);
            if (!v.Ok) { failed++; continue; }
            try { File.Delete(b.FilePath); deleted++; }
            catch { failed++; }
        }
        return (deleted, $"删除 {deleted} 份过期备份（保留最近 {RetentionCount} 份），{failed} 份未能删除。");
    }

    public async Task<RestoreResult> RestoreAsync(string backupFilePath, CancellationToken ct = default)
    {
        try
        {
            // 1. 先在临时路径校验，不合格绝不替换现库
            var staged = Path.Combine(Path.GetTempPath(), "thg-restore-" + Guid.NewGuid().ToString("N")[..8] + ".db");
            File.Copy(backupFilePath, staged, true);
            var verify = await VerifyAsync(staged, ct);
            if (!verify.Ok)
            {
                TryDelete(staged);
                return new RestoreResult { Ok = false, Detail = "待恢复备份校验未通过，已放弃恢复：" + verify.Detail };
            }

            // 2. 先备份现库（失败则中止，避免无法回退）
            var (backup, err) = await BackupAsync(null, ct);
            if (backup is null)
            {
                TryDelete(staged);
                return new RestoreResult { Ok = false, Detail = "恢复前备份现库失败，已中止恢复：" + err };
            }

            // 3. 替换现库（连同 -wal/-shm 一起处理，避免旧 WAL 污染新库）
            SqliteConnection.ClearAllPools();
            var wal = _dbPath + "-wal";
            var shm = _dbPath + "-shm";
            TryDelete(wal);
            TryDelete(shm);
            File.Copy(staged, _dbPath, true);
            TryDelete(staged);

            // 4. 统计恢复后可能未同步的记录（如实提示，不虚构已同步）
            long unsynced = 0;
            try
            {
                await using var conn2 = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
                await conn2.OpenAsync(ct);
                await using var cmd = conn2.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Outbox WHERE Confirmed=0";
                unsynced = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L, CultureInfo.InvariantCulture);
            }
            catch { /* 统计失败不影响恢复结论 */ }

            return new RestoreResult
            {
                Ok = true,
                Detail = $"已从 {Path.GetFileName(backupFilePath)} 恢复；恢复前现库已备份到 {Path.GetFileName(backup.FilePath)}。"
                    + $"恢复点：样本 {verify.SampleCount} / 分钟 {verify.MinuteCount} / 事件 {verify.EventCount} / 阈值 {verify.ThresholdCount}。",
                UnsyncedCount = unsynced
            };
        }
        catch (Exception ex)
        {
            return new RestoreResult { Ok = false, Detail = "恢复失败：" + ex.Message };
        }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}