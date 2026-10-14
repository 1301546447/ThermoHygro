using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ThermoHygro.Core;

/// <summary>
/// 统一历史查询。屏幕、CSV、PDF 都必须经由本接口取得同一份 ReportSnapshot，
/// 禁止各自重新查询导致图表与统计来自不同时间点。
/// </summary>
public interface IHistoryQuery
{
    /// <summary>取出快照（一次数据库查询，含分桶汇总、事件、统计、阈值说明）。</summary>
    Task<ReportSnapshot> TakeSnapshotAsync(ReportQuery query, CancellationToken ct = default);

    /// <summary>列出已有数据的可用日期范围（用于界面提示）。</summary>
    Task<(DateTime? FirstLocal, DateTime? LastLocal)> GetDataRangeAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>导出结果。只有 Success 为真才允许声称导出成功；半成品必须删除。</summary>
public sealed class ExportResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public string? Error { get; init; }
    public long RowCount { get; init; }
    public bool Canceled { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

/// <summary>CSV 导出（汇总表与事件表分别成文件）。</summary>
public interface ICsvExporter
{
    Task<ExportResult> ExportAsync(ReportSnapshot snapshot, string outputDirectory,
        IProgress<int>? progress = null, CancellationToken ct = default);
}

/// <summary>PDF 报告导出（A4 横向、封面 + 逐月页、中文可复制）。</summary>
public interface IPdfReporter
{
    Task<ExportResult> ExportAsync(ReportSnapshot snapshot, string outputFilePath,
        IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>中文字体可用性说明（用于界面如实显示，不静默降级为乱码）。</summary>
    string FontStatus { get; }
}

/// <summary>备份条目。</summary>
public sealed class BackupEntry
{
    public string FilePath { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public long SizeBytes { get; init; }
    public bool Verified { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>备份校验结果。</summary>
public sealed class BackupVerifyResult
{
    public bool Ok { get; init; }
    public string Detail { get; init; } = "";
    public long SampleCount { get; init; }
    public long MinuteCount { get; init; }
    public long EventCount { get; init; }
    public long ThresholdCount { get; init; }
    /// <summary>数据库结构版本；高于本程序支持时拒绝写入。</summary>
    public long SchemaVersion { get; init; }
}

/// <summary>恢复结果。</summary>
public sealed class RestoreResult
{
    public bool Ok { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>恢复后与远端可能不同步的记录数（可识别，不虚构已恢复）。</summary>
    public long UnsyncedCount { get; init; }
}

/// <summary>
/// 本地库备份与恢复。使用 SQLite 在线备份接口（不直接复制主文件以免漏掉 WAL）。
/// </summary>
public interface IBackupService
{
    /// <summary>当前库路径。</summary>
    string DatabasePath { get; }

    /// <summary>执行一次在线备份；成功返回备份文件与校验结果。</summary>
    Task<(BackupEntry? Entry, string Error)> BackupAsync(string? targetDirectory = null, CancellationToken ct = default);

    /// <summary>校验一个备份文件是否可读且结构完整。</summary>
    Task<BackupVerifyResult> VerifyAsync(string backupFilePath, CancellationToken ct = default);

    /// <summary>列出备份目录中的备份（按时间倒序）。</summary>
    Task<IReadOnlyList<BackupEntry>> ListAsync(string? directory = null, CancellationToken ct = default);

    /// <summary>
    /// 清理过期备份：先校验新备份成功，再删除超出保留份数的旧备份。
    /// 校验失败或删除失败都不影响已有的旧备份。
    /// </summary>
    Task<(int Deleted, string Detail)> CleanupAsync(string? directory = null, CancellationToken ct = default);

    /// <summary>
    /// 恢复：调用方必须已停止采集与同步；在临时路径校验后再替换现库。
    /// 替换前自动备份现库。
    /// </summary>
    Task<RestoreResult> RestoreAsync(string backupFilePath, CancellationToken ct = default);

    /// <summary>保留份数（默认 30）。</summary>
    int RetentionCount { get; set; }
}