using System;
using System.Data;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using ThermoHygro.Core;

namespace ThermoHygro.Infrastructure;

/// <summary>
/// SQL Server 远端同步（Outbox 模式）。
///
/// 设计要点（对应验收 B07/B08/B10）：
/// ①本地优先：所有数据先写 SQLite；即使远端不可达，本地记录与界面均不受影响；
/// ②待传队列：待传项落在 Outbox 表，按 (DestinationId, RecordType, RecordKey) 唯一去重，
///   同一记录不会因为重试而重复写入（重放幂等）；
/// ③目标身份：Outbox.DestinationId 绑定目标身份；切换目标后旧待传项不会误投到新库；
/// ④确认后才标记：只有远端提交成功才置 Confirmed=1，失败保留并记录 LastError；
/// ⑤凭据不外泄：连接字符串只在内存使用，不写日志、不写配置文件。
/// </summary>
public sealed class SqlServerRemoteSink : IRemoteSink, IDisposable
{
    private readonly string _sqliteConnection;
    private readonly RemoteSyncOptions _options;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private RemoteSyncState _state = RemoteSyncState.Idle;
    private long _pending;
    private long _written;
    private string _lastError = "";
    private int _retryRequested;
    private DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;

    public SqlServerRemoteSink(string sqliteConnectionString, RemoteSyncOptions options)
    {
        _sqliteConnection = sqliteConnectionString;
        _options = options;
    }

    public RemoteSyncState State { get { lock (_gate) return _state; } }
    public long PendingCount { get { lock (_gate) return _pending; } }
    public string LastError { get { lock (_gate) return _lastError; } }
    /// <summary>已成功传输到远端的记录数（本进程内）。</summary>
    public long TransferredCount { get { lock (_gate) return _written; } }

    /// <summary>手动触发一次重试（界面"立即重试"用）。</summary>
    public int Retry()
    {
        Interlocked.Exchange(ref _retryRequested, 1);
        lock (_gate) if (_state == RemoteSyncState.Error) _state = RemoteSyncState.Pending;
        return 0;
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        // 先同步刷新一次待传计数：避免界面在同步首次完成前把 0 当成“无待传”
        try { RefreshPendingAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => LoopAsync(cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts; var loop = _loop;
        _cts = null; _loop = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            try { if (loop is not null) await loop.ConfigureAwait(false); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (moved, healthy) = await FlushOnceAsync(ct).ConfigureAwait(false);
                if (moved == 0 && healthy)
                {
                    lock (_gate)
                    {
                        _lastError = "";
                        _state = _pending > 0 ? RemoteSyncState.Pending : RemoteSyncState.Idle;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _state = RemoteSyncState.Error;
                    // 连接字符串可能含敏感信息：只保留异常消息，不回显连接串
                    _lastError = Trim(ex.Message);
                }
            }

            var wait = TimeSpan.FromSeconds(Math.Max(5, _options.SyncIntervalSeconds));
            if (Interlocked.Exchange(ref _retryRequested, 0) == 1) wait = TimeSpan.FromSeconds(1);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>确保远端存在目标身份记录；返回（本次处理记录数, 远端是否可达）。</summary>
    private async Task<(int Moved, bool Healthy)> FlushOnceAsync(CancellationToken ct)
    {
        var dest = _options.DestinationIdentity;
        if (string.IsNullOrWhiteSpace(dest) || string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            lock (_gate) { _state = RemoteSyncState.Disabled; _pending = 0; }
            return (0, true);
        }

        // 取出待传项（未确认）
        var items = new List<(long Id, string Type, string Key, string Payload, string Revision)>();
        await using (var lite = new Microsoft.Data.Sqlite.SqliteConnection(_sqliteConnection))
        {
            await lite.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = lite.CreateCommand();
            cmd.CommandText = """
                SELECT OutboxId, RecordType, RecordKey, Payload, CreatedAtUtc FROM Outbox
                WHERE Confirmed=0 AND (DestinationId IS NULL OR DestinationId=$d)
                ORDER BY OutboxId LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$d", dest);
            cmd.Parameters.AddWithValue("$n", Math.Max(1, _options.BatchSize));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                items.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4)));
        }

        if (items.Count == 0)
        {
            // 不要直接写 0：查询为空也可能是身份不匹配，必须查真实未确认数；并做连通性探测
            await RefreshPendingAsync(ct).ConfigureAwait(false);
            bool healthy = await ProbeAsync(ct).ConfigureAwait(false);
            if (healthy) lock (_gate) { _lastError = ""; _state = _pending > 0 ? RemoteSyncState.Pending : RemoteSyncState.Idle; }
            return (0, healthy);
        }

        // 一次连接内完成：注册目标身份 → 逐条 upsert → 标记确认
        await using var remote = new SqlConnection(_options.ConnectionString);
        await remote.OpenAsync(ct).ConfigureAwait(false);

        await using (var reg = remote.CreateCommand())
        {
            reg.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.SyncDestination WHERE DestinationId=@d)
                INSERT INTO dbo.SyncDestination(DestinationId,Kind,DestinationIdentity,Enabled,CreatedAtUtc)
                VALUES(@d,'sqlserver',@ident,1,SYSUTCDATETIME())
                """;
            reg.Parameters.AddWithValue("@d", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dest))).ToLowerInvariant());
            reg.Parameters.AddWithValue("@ident", dest);
            await reg.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int ok = 0;
        foreach (var it in items)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await WriteOneAsync(remote, it.Type, it.Payload, ct).ConfigureAwait(false);
                await MarkAsync(it.Id, dest, true, null, it.Revision, ct).ConfigureAwait(false);
                ok++;
                lock (_gate) { _written++; }
            }
            catch (Exception ex)
            {
                // 单条失败不阻断整批；保留待传并记录原因
                await MarkAsync(it.Id, dest, false, Trim(ex.Message), it.Revision, ct).ConfigureAwait(false);
                lock (_gate) { _lastError = Trim(ex.Message); _state = RemoteSyncState.Error; }
                break;
            }
        }

        await RefreshPendingAsync(ct).ConfigureAwait(false);
        return (items.Count, true);
    }

    /// <summary>按记录类型写入远端对应表。使用 MERGE 保证重放幂等（B08）。</summary>
    private async Task WriteOneAsync(SqlConnection remote, string type, string payloadJson, CancellationToken ct)
    {
        var p = Payload.Parse(payloadJson);
        await using var cmd = remote.CreateCommand();
        switch (type)
        {
            case "device":
                cmd.CommandText = """
                    MERGE dbo.Device AS target USING (SELECT @dev AS DeviceId) AS source ON target.DeviceId=source.DeviceId
                    WHEN MATCHED THEN UPDATE SET DisplayName=@name,Location=@loc
                    WHEN NOT MATCHED THEN INSERT(DeviceId,DisplayName,Location,ProtocolId,ProtocolVersion,CreatedAtUtc)
                    VALUES(@dev,@name,@loc,@pid,@pv,@created);
                    """;
                cmd.Parameters.AddWithValue("@dev", p.Get("DeviceId"));
                cmd.Parameters.AddWithValue("@name", p.Get("DisplayName"));
                cmd.Parameters.AddWithValue("@loc", p.Get("Location"));
                cmd.Parameters.AddWithValue("@pid", p.Get("ProtocolId"));
                cmd.Parameters.AddWithValue("@pv", p.Get("ProtocolVersion"));
                cmd.Parameters.AddWithValue("@created", p.Get("CreatedAtUtc"));
                break;
            case "sample":
                // 幂等键用本地样本号（RecordKey），与远端自增主键解耦
                cmd.CommandText = """
                    MERGE dbo.SampleJournal AS t
                    USING (SELECT @localId AS LocalRecordKey) AS s ON t.LocalRecordKey = s.LocalRecordKey
                    WHEN NOT MATCHED THEN INSERT
                      (LocalRecordKey,DeviceId,SessionId,SequenceNo,
                       TemperatureDegC,TemperatureQuality,TemperatureReason,
                       HumidityPercent,HumidityQuality,HumidityReason,
                       ThresholdVersionId,AttemptTimeUtc,TimeZoneOffsetMinutes,Aggregated)
                      VALUES(@localId,@dev,@sess,@seq,
                             @tval,@tq,@tr,@hval,@hq,@hr,
                             @tv,@at,@tz,0);
                    """;
                cmd.Parameters.AddWithValue("@localId", p.Get("SampleId"));
                cmd.Parameters.AddWithValue("@dev", p.Get("DeviceId"));
                cmd.Parameters.AddWithValue("@sess", p.Get("SessionId"));
                cmd.Parameters.AddWithValue("@seq", p.GetLong("Sequence"));
                cmd.Parameters.AddWithValue("@tv", p.GetLong("ThresholdVersionId"));
                cmd.Parameters.AddWithValue("@tval", p.GetDoubleNull("TemperatureValue"));
                cmd.Parameters.AddWithValue("@tq", p.GetQualityCode("TemperatureQuality"));
                cmd.Parameters.AddWithValue("@tr", p.Get("TemperatureReason"));
                cmd.Parameters.AddWithValue("@hval", p.GetDoubleNull("HumidityValue"));
                cmd.Parameters.AddWithValue("@hq", p.GetQualityCode("HumidityQuality"));
                cmd.Parameters.AddWithValue("@hr", p.Get("HumidityReason"));
                cmd.Parameters.AddWithValue("@at", p.Get("AttemptTimeUtc"));
                cmd.Parameters.AddWithValue("@tz", p.GetInt("TimeZoneOffsetMinutes"));
                break;

            case "minute":
                // 从本地 MinuteSummary 读取***最新***聚合值（不可用入队时的单次样本值，否则会覆盖为单值）
                var version = p.GetLongNull("ThresholdVersionId") is long number ? number : (long?)null;
                var m = await ReadMinuteAsync(p.Get("DeviceId") as string ?? "", p.Get("WindowStartUtc") as string ?? "", version, ct).ConfigureAwait(false);
                if (m is null)
                {
                    // 本地已无该分钟（可能已被清理）：视为已处理，不阻断队列
                    return;
                }
                cmd.CommandText = """
                    MERGE dbo.MinuteSummary AS t
                    USING (SELECT @dev AS DeviceId, @ws AS WindowStartUtc) AS s
                      ON t.DeviceId = s.DeviceId AND t.WindowStartUtc = s.WindowStartUtc AND t.ThresholdVersionId=@tv
                    WHEN MATCHED THEN UPDATE SET
                      WindowEndUtc=@we, ThresholdVersionId=@tv,
                      TemperatureSum=@ts, TemperatureValidCount=@tc, TemperatureMin=@tmin, TemperatureMinAtUtc=@tminAt,
                      TemperatureMax=@tmax, TemperatureMaxAtUtc=@tmaxAt,
                      HumiditySum=@hs, HumidityValidCount=@hc, HumidityMin=@hmin, HumidityMinAtUtc=@hminAt,
                      HumidityMax=@hmax, HumidityMaxAtUtc=@hmaxAt,
                      AttemptCount=@ac, TempOutOfRangeCount=@toor, HumidityOutOfRangeCount=@hoor,
                      TempMissing=@tmiss, HumidityMissing=@hmiss
                    WHEN NOT MATCHED THEN INSERT
                      (DeviceId,WindowStartUtc,WindowEndUtc,ThresholdVersionId,SessionId,
                       TemperatureSum,TemperatureValidCount,TemperatureMin,TemperatureMinAtUtc,TemperatureMax,TemperatureMaxAtUtc,
                       HumiditySum,HumidityValidCount,HumidityMin,HumidityMinAtUtc,HumidityMax,HumidityMaxAtUtc,
                       AttemptCount,MissedSlots,TempOutOfRangeCount,HumidityOutOfRangeCount,TempMissing,HumidityMissing)
                      VALUES(@dev,@ws,@we,@tv,NULL,
                             @ts,@tc,@tmin,@tminAt,@tmax,@tmaxAt,
                             @hs,@hc,@hmin,@hminAt,@hmax,@hmaxAt,
                             @ac,0,@toor,@hoor,@tmiss,@hmiss);
                    """;
                cmd.Parameters.AddWithValue("@dev", m.DeviceId);
                cmd.Parameters.AddWithValue("@ws", m.WindowStartUtc);
                cmd.Parameters.AddWithValue("@we", m.WindowEndUtc);
                cmd.Parameters.AddWithValue("@tv", (object?)m.ThresholdVersionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", (object?)m.TemperatureSum ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tc", m.TemperatureValidCount);
                cmd.Parameters.AddWithValue("@tmin", (object?)m.TemperatureMin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tminAt", (object?)m.TemperatureMinAtUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tmax", (object?)m.TemperatureMax ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tmaxAt", (object?)m.TemperatureMaxAtUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hs", (object?)m.HumiditySum ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hc", m.HumidityValidCount);
                cmd.Parameters.AddWithValue("@hmin", (object?)m.HumidityMin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hminAt", (object?)m.HumidityMinAtUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hmax", (object?)m.HumidityMax ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hmaxAt", (object?)m.HumidityMaxAtUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ac", m.AttemptCount);
                cmd.Parameters.AddWithValue("@toor", m.TempOutOfRangeCount);
                cmd.Parameters.AddWithValue("@hoor", m.HumidityOutOfRangeCount);
                cmd.Parameters.AddWithValue("@tmiss", m.TempMissing);
                cmd.Parameters.AddWithValue("@hmiss", m.HumidityMissing);
                break;

            case "event":
                cmd.CommandText = """
                    IF NOT EXISTS (SELECT 1 FROM dbo.MonitorEvent WHERE DeviceId=@dev AND OccurredAtUtc=@at AND EventType=@t)
                    INSERT INTO dbo.MonitorEvent(DeviceId,OccurredAtUtc,EventType,Severity,Context,ThresholdVersionId)
                    VALUES(@dev,@at,@t,@sev,@ctx,@tv)
                    """;
                cmd.Parameters.AddWithValue("@dev", p.Get("DeviceId"));
                cmd.Parameters.AddWithValue("@at", p.Get("OccurredAtUtc"));
                cmd.Parameters.AddWithValue("@t", p.Get("EventType"));
                cmd.Parameters.AddWithValue("@sev", p.Get("Severity"));
                cmd.Parameters.AddWithValue("@ctx", p.Get("Context"));
                cmd.Parameters.AddWithValue("@tv", p.GetLongNull("ThresholdVersionId"));
                break;

            case "threshold":
                cmd.CommandText = """
                    IF NOT EXISTS (SELECT 1 FROM dbo.ThresholdVersion WHERE VersionId=@id)
                    INSERT INTO dbo.ThresholdVersion
                      (VersionId,DeviceId,TemperatureLow,TemperatureHigh,HumidityLow,HumidityHigh,EffectiveFromUtc,Source)
                    VALUES(@id,@dev,@tl,@th,@hl,@hh,@ef,@src)
                    """;
                cmd.Parameters.AddWithValue("@id", p.GetLong("VersionId"));
                cmd.Parameters.AddWithValue("@dev", p.Get("DeviceId"));
                cmd.Parameters.AddWithValue("@tl", p.GetDoubleNull("TemperatureLow"));
                cmd.Parameters.AddWithValue("@th", p.GetDoubleNull("TemperatureHigh"));
                cmd.Parameters.AddWithValue("@hl", p.GetDoubleNull("HumidityLow"));
                cmd.Parameters.AddWithValue("@hh", p.GetDoubleNull("HumidityHigh"));
                cmd.Parameters.AddWithValue("@ef", p.Get("EffectiveFromUtc"));
                cmd.Parameters.AddWithValue("@src", p.Get("Source"));
                break;

            default:
                throw new InvalidOperationException($"未知的记录类型：{type}");
        }
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task MarkAsync(long outboxId, string dest, bool confirmed, string? error, string revision, CancellationToken ct)
    {
        await using var lite = new Microsoft.Data.Sqlite.SqliteConnection(_sqliteConnection);
        await lite.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = lite.CreateCommand();
        cmd.CommandText = """
            UPDATE Outbox SET Confirmed=$c, AttemptCount=AttemptCount+1,
                   DestinationId=COALESCE(DestinationId,$d), LastError=$e
            WHERE OutboxId=$id AND CreatedAtUtc=$revision
            """;
        cmd.Parameters.AddWithValue("$c", confirmed ? 1 : 0);
        cmd.Parameters.AddWithValue("$d", dest);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", outboxId);
        cmd.Parameters.AddWithValue("$revision", revision);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshPendingAsync(CancellationToken ct)
    {
        await using var lite = new Microsoft.Data.Sqlite.SqliteConnection(_sqliteConnection);
        await lite.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = lite.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Outbox WHERE Confirmed=0";
        var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        lock (_gate)
        {
            _pending = n;
            if (_state != RemoteSyncState.Error)
                _state = n > 0 ? RemoteSyncState.Pending : RemoteSyncState.Idle;
        }
    }

    /// <summary>限制错误文本长度，避免把长连接串信息带入日志。</summary>
    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    /// <summary>
    /// 无待传项时的连通性探测：每 60 秒最多一次。
    /// 目的：连接字符串写错时，界面不能因为"无待传"就显示成一切正常（“不误报已同步”的反面）。
    /// </summary>
    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastProbeUtc < TimeSpan.FromSeconds(60)) return true;
        _lastProbeUtc = DateTimeOffset.UtcNow;
        try
        {
            await using var cn = new SqlConnection(_options.ConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _state = RemoteSyncState.Error;
                _lastError = "远端不可达：" + Trim(ex.Message);
            }
            return false;
        }
    }

    /// <summary>远端写入前，从本地读该分钟的***最新***完整聚合值。</summary>
    private async Task<MinuteRow?> ReadMinuteAsync(string deviceId, string windowStartUtc, long? version, CancellationToken ct)
    {
        if (deviceId.Length == 0 || windowStartUtc.Length == 0) return null;
        await using var lite = new Microsoft.Data.Sqlite.SqliteConnection(_sqliteConnection);
        await lite.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = lite.CreateCommand();
        cmd.CommandText = """
            SELECT DeviceId,WindowStartUtc,MIN(WindowEndUtc),MAX(ThresholdVersionId),
                   CASE WHEN SUM(TemperatureValidCount)>0 THEN SUM(COALESCE(TemperatureSum,0)) END,SUM(TemperatureValidCount),
                   MIN(TemperatureMin),
                   (SELECT TemperatureMinAtUtc FROM MinuteSummary m2
                     WHERE m2.DeviceId=$d AND m2.WindowStartUtc=$ws AND m2.TemperatureMin IS NOT NULL AND ($version IS NULL OR m2.ThresholdVersionId=$version)
                     ORDER BY m2.TemperatureMin LIMIT 1),
                   MAX(TemperatureMax),
                   (SELECT TemperatureMaxAtUtc FROM MinuteSummary m3
                     WHERE m3.DeviceId=$d AND m3.WindowStartUtc=$ws AND m3.TemperatureMax IS NOT NULL AND ($version IS NULL OR m3.ThresholdVersionId=$version)
                     ORDER BY m3.TemperatureMax DESC LIMIT 1),
                   CASE WHEN SUM(HumidityValidCount)>0 THEN SUM(COALESCE(HumiditySum,0)) END,SUM(HumidityValidCount),
                   MIN(HumidityMin),
                   (SELECT HumidityMinAtUtc FROM MinuteSummary m4
                     WHERE m4.DeviceId=$d AND m4.WindowStartUtc=$ws AND m4.HumidityMin IS NOT NULL AND ($version IS NULL OR m4.ThresholdVersionId=$version)
                     ORDER BY m4.HumidityMin LIMIT 1),
                   MAX(HumidityMax),
                   (SELECT HumidityMaxAtUtc FROM MinuteSummary m5
                     WHERE m5.DeviceId=$d AND m5.WindowStartUtc=$ws AND m5.HumidityMax IS NOT NULL AND ($version IS NULL OR m5.ThresholdVersionId=$version)
                     ORDER BY m5.HumidityMax DESC LIMIT 1),
                   SUM(AttemptCount),SUM(TempOutOfRangeCount),SUM(HumidityOutOfRangeCount),
                   SUM(TempMissing),SUM(HumidityMissing)
            FROM MinuteSummary WHERE DeviceId=$d AND WindowStartUtc=$ws AND ($version IS NULL OR ThresholdVersionId=$version)
            """;
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$ws", windowStartUtc);
        cmd.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        T? N<T>(int i) where T : struct => r.IsDBNull(i) ? (T?)null : r.GetFieldValue<T>(i);
        return new MinuteRow
        {
            DeviceId = r.GetString(0),
            WindowStartUtc = r.GetString(1),
            WindowEndUtc = r.GetString(2),
            ThresholdVersionId = r.IsDBNull(3) ? null : r.GetInt64(3),
            TemperatureSum = N<double>(4),
            TemperatureValidCount = r.GetInt32(5),
            TemperatureMin = N<double>(6),
            TemperatureMinAtUtc = r.IsDBNull(7) ? null : r.GetString(7),
            TemperatureMax = N<double>(8),
            TemperatureMaxAtUtc = r.IsDBNull(9) ? null : r.GetString(9),
            HumiditySum = N<double>(10),
            HumidityValidCount = r.GetInt32(11),
            HumidityMin = N<double>(12),
            HumidityMinAtUtc = r.IsDBNull(13) ? null : r.GetString(13),
            HumidityMax = N<double>(14),
            HumidityMaxAtUtc = r.IsDBNull(15) ? null : r.GetString(15),
            AttemptCount = r.GetInt32(16),
            TempOutOfRangeCount = r.GetInt32(17),
            HumidityOutOfRangeCount = r.GetInt32(18),
            TempMissing = r.GetInt32(19),
            HumidityMissing = r.GetInt32(20)
        };
    }

    private sealed class MinuteRow
    {
        public string DeviceId { get; init; } = "";
        public string WindowStartUtc { get; init; } = "";
        public string WindowEndUtc { get; init; } = "";
        public long? ThresholdVersionId { get; init; }
        public double? TemperatureSum { get; init; }
        public int TemperatureValidCount { get; init; }
        public double? TemperatureMin { get; init; }
        public string? TemperatureMinAtUtc { get; init; }
        public double? TemperatureMax { get; init; }
        public string? TemperatureMaxAtUtc { get; init; }
        public double? HumiditySum { get; init; }
        public int HumidityValidCount { get; init; }
        public double? HumidityMin { get; init; }
        public string? HumidityMinAtUtc { get; init; }
        public double? HumidityMax { get; init; }
        public string? HumidityMaxAtUtc { get; init; }
        public int AttemptCount { get; init; }
        public int TempOutOfRangeCount { get; init; }
        public int HumidityOutOfRangeCount { get; init; }
        public int TempMissing { get; init; }
        public int HumidityMissing { get; init; }
    }

    /// <summary>
    /// 从连接字符串推导目标身份：服务器/实例/数据库的组合。
    /// 不含用户名密码，可安全写入日志与 Outbox。
    /// 切换目标库后身份不同，旧待传项不会被误投到新库（对应验收 B10）。
    /// </summary>
    public static string DeriveIdentity(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            var server = string.IsNullOrWhiteSpace(b.DataSource) ? "(未知服务器)" : b.DataSource;
            var db = string.IsNullOrWhiteSpace(b.InitialCatalog) ? "(默认数据库)" : b.InitialCatalog;
            return $"sqlserver://{server}/{db}";
        }
        catch
        {
            // 解析失败时退化为稳定短哈希，绝不回显原文（可能含密码）
            using var sha = System.Security.Cryptography.SHA256.Create();
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(connectionString ?? ""));
            return "sqlserver://unknown/" + Convert.ToHexString(h)[..12];
        }
    }

    /// <summary>测试连接（不抛异常，返回是否可用与原因）。</summary>
    public static async Task<(bool Ok, string Detail)> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var cn = new SqlConnection(connectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT DB_NAME()";
            var db = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            return (true, $"已连接，当前数据库：{db}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public void Dispose() => _cts?.Cancel();

    /// <summary>Payload 解析助手：容忍缺字段（返回空/DBNull），不因单个字段缺失而整条失败。</summary>
    private sealed class Payload
    {
        private readonly Dictionary<string, System.Text.Json.JsonElement> _map;
        private Payload(Dictionary<string, System.Text.Json.JsonElement> map) => _map = map;

        public static Payload Parse(string json)
        {
            var map = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject()) map[prop.Name] = prop.Value.Clone();
            }
            return new Payload(map);
        }

        private System.Text.Json.JsonElement? Find(string k) => _map.TryGetValue(k, out var v) ? v : null;

        public object Get(string k)
        {
            var v = Find(k);
            if (v is null || v.Value.ValueKind == System.Text.Json.JsonValueKind.Null) return DBNull.Value;
            return v.Value.ValueKind == System.Text.Json.JsonValueKind.String ? v.Value.GetString() ?? (object)DBNull.Value
                 : v.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? v.Value.GetDouble()
                 : v.Value.ToString();
        }

        public object GetDoubleNull(string k)
        {
            var v = Find(k);
            if (v is null || v.Value.ValueKind != System.Text.Json.JsonValueKind.Number) return DBNull.Value;
            return v.Value.GetDouble();
        }

        public double GetDouble(string k)
        {
            var v = Find(k);
            return v is not null && v.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? v.Value.GetDouble() : 0;
        }

        public long GetLong(string k)
        {
            var v = Find(k);
            return v is not null && v.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? v.Value.GetInt64() : 0;
        }

        public object GetLongNull(string k)
        {
            var v = Find(k);
            if (v is null || v.Value.ValueKind != System.Text.Json.JsonValueKind.Number) return DBNull.Value;
            return v.Value.GetInt64();
        }

        public int GetInt(string k)
        {
            var v = Find(k);
            return v is not null && v.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? v.Value.GetInt32() : 0;
        }

        /// <summary>质量码：载荷存的是枚举名（如 Good），远端列为 INT，需转换。</summary>
        public object GetQualityCode(string k)
        {
            var v = Find(k);
            if (v is null || v.Value.ValueKind == System.Text.Json.JsonValueKind.Null) return DBNull.Value;
            var s = v.Value.ValueKind == System.Text.Json.JsonValueKind.String ? v.Value.GetString() : v.Value.ToString();
            if (string.IsNullOrEmpty(s)) return DBNull.Value;
            return Enum.TryParse<MetricQuality>(s, ignoreCase: true, out var q) ? (int)q : (object)DBNull.Value;
        }
    }
}
