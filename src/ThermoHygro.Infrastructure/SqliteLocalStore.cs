using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ThermoHygro.Core;

namespace ThermoHygro.Infrastructure;

/// <summary>
/// SQLite 本地可靠存储。样本先写入逐次日志，同一事务内更新分钟汇总，
/// 只有提交成功才认为"已保存"；缺测保持 NULL 不写 0。
/// </summary>
public sealed class SqliteLocalStore : ILocalStore
{
    private readonly string _path;
    private readonly string _conn;
    private readonly SemaphoreSlim _sampleWrites = new(1, 1);

    public SqliteLocalStore(string databasePath)
    {
        _path = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 不参与连接池：单机采集场景下并发量小，而池内连接会持续占用
            // 数据库文件，导致备份/恢复/复制等外部操作失败。
            Pooling = false
        }.ToString();
    }

    public string DatabasePath => _path;

    /// <summary>SQLite 连接字符串（供备份/同步/维护使用；Pooling=False 保证外部文件操作可行）。</summary>
    public string ConnectionString => _conn;
    public StorageState State { get; private set; } = StorageState.Unknown;
    public string LastError { get; private set; } = "";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var script = SchemaLoader.Load("schema.sqlite.sql");
            await using var cn = new SqliteConnection(_conn);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = script;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO SchemaVersion(Module,Version,ScriptDigest,AppliedAtUtc) " +
                                  "VALUES('core',1,'m4-baseline',$t) ON CONFLICT(Module) DO NOTHING";
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            State = StorageState.Ok;
            LastError = "";
        }
        catch (Exception ex)
        {
            State = StorageState.Fault;
            LastError = DescribeStorageError(ex);
            throw;
        }
    }

    /// <summary>把存储异常翻译成用户可理解的说明，绝不把磁盘满说成正常。</summary>
    public static string DescribeStorageError(Exception ex)
    {
        var m = ex.Message;
        if (m.Contains("disk is full", StringComparison.OrdinalIgnoreCase))
            return "磁盘已满，本地未保存（当前读数不在记录，请清理磁盘后重试）";
        if (m.Contains("readonly", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("read-only", StringComparison.OrdinalIgnoreCase))
            return "本地数据库只读或目录无写权限，本地未保存";
        if (m.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("not a database", StringComparison.OrdinalIgnoreCase))
            return "本地数据库已损坏，原文件已保留（请从备份恢复）";
        if (m.Contains("locked", StringComparison.OrdinalIgnoreCase))
            return "本地数据库被占用，本次保存失败";
        return $"本地保存失败：{m}";
    }

    public async Task<SaveOutcome> AppendSampleAsync(DeviceIdentity device, ThresholdSnapshot thresholds,
        ReadingGroup group, CancellationToken ct = default)
    {
        await _sampleWrites.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cn = new SqliteConnection(_conn);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            using var tx = cn.BeginTransaction();

            await UpsertDeviceAsync(cn, tx, device, ct).ConfigureAwait(false);
            await EnsureThresholdAsync(cn, tx, device, thresholds, ct).ConfigureAwait(false);

            long sampleId;
            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO SampleJournal(
                      DeviceId,SessionId,SequenceNo,
                      TemperatureDegC,TemperatureQuality,TemperatureReason,
                      HumidityPercent,HumidityQuality,HumidityReason,
                      ThresholdVersionId,AttemptTimeUtc,TimeZoneOffsetMinutes,Aggregated)
                    VALUES($d,$s,$seq,$tv,$tq,$tr,$hv,$hq,$hr,$th,$at,$tz,0);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$d", device.DeviceId);
                cmd.Parameters.AddWithValue("$s", (object?)null ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$seq", 0);
                AddMetric(cmd, "$tv", "$tq", "$tr", group.Temperature);
                AddMetric(cmd, "$hv", "$hq", "$hr", group.Humidity);
                cmd.Parameters.AddWithValue("$th", thresholds.VersionId);
                cmd.Parameters.AddWithValue("$at", group.AttemptTimeUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$tz", (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes);
                sampleId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            await UpdateMinuteAsync(cn, tx, device, thresholds, group, ct).ConfigureAwait(false);
            await MarkConsumedAsync(cn, tx, sampleId, ct).ConfigureAwait(false);
            // 同事务内入队待传项：保证"本地已保存⇒必定会尝试同步"（断网也不丢）
            await EnqueueOutboxAsync(cn, tx, device.DeviceId, "sample", sampleId.ToString(),
                BuildSamplePayload(device, thresholds, group, sampleId), ct).ConfigureAwait(false);
            // 分钟汇总同样入队（远端按 DeviceId+WindowStartUtc 幂等合并）
            await EnqueueOutboxAsync(cn, tx, device.DeviceId, "minute", MinuteKey(device.DeviceId, group.AttemptTimeUtc) + "|" + thresholds.VersionId,
                BuildMinutePayload(device, thresholds, group), ct).ConfigureAwait(false);

            tx.Commit();
            State = StorageState.Ok;
            LastError = "";
            return new SaveOutcome { Saved = true, SampleId = sampleId };
        }
        catch (Exception ex)
        {
            State = StorageState.Fault;
            LastError = DescribeStorageError(ex);
            return new SaveOutcome { Saved = false, Error = LastError };
        }
        finally { _sampleWrites.Release(); }
    }

    /// <summary>修改显示信息立即写入设备目录，即使传感器暂停也能按新名称查询。</summary>
    public async Task SaveDeviceAsync(DeviceIdentity device, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = cn.BeginTransaction();
        await UpsertDeviceAsync(cn, tx, device, ct).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task<List<DeviceIdentity>> QueryDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<DeviceIdentity>();
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT DeviceId,DisplayName,COALESCE(Location,''),ProtocolId,COALESCE(ProtocolVersion,'') FROM Device ORDER BY DisplayName,DeviceId";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            devices.Add(new DeviceIdentity { DeviceId = reader.GetString(0), DisplayName = reader.GetString(1), Location = reader.GetString(2), ProtocolId = reader.GetString(3), ProtocolVersion = reader.GetString(4) });
        return devices;
    }

    /// <summary>
    /// 把待传项写入 Outbox（同事务内调用）。
    /// 采用 UPSERT 语义：同一 (类型,键) 只保留一行；内容变化时重置为待传。
    /// </summary>
    private static async Task EnqueueOutboxAsync(SqliteConnection cn, SqliteTransaction tx,
        string deviceId, string recordType, string recordKey, string payload, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Outbox(DestinationId,RecordType,RecordKey,Payload,AttemptCount,Confirmed,CreatedAtUtc)
            SELECT NULL,$t,$k,$p,0,0,$now
            WHERE NOT EXISTS (SELECT 1 FROM Outbox WHERE RecordType=$t AND RecordKey=$k);
            UPDATE Outbox SET Payload=$p, Confirmed=0, CreatedAtUtc=$now, LastError=NULL
            WHERE RecordType=$t AND RecordKey=$k;
            """;
        cmd.Parameters.AddWithValue("$t", recordType);
        cmd.Parameters.AddWithValue("$k", recordKey);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>样本的待传载荷（JSON）。字段名与远端列一致，便于直接映射。</summary>
    private static string BuildSamplePayload(DeviceIdentity device, ThresholdSnapshot th, ReadingGroup g, long sampleId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["SampleId"] = sampleId,
            ["DeviceId"] = device.DeviceId,
            ["SessionId"] = 0,
            ["Sequence"] = 0,
            ["ThresholdVersionId"] = th.VersionId,
            ["TemperatureValue"] = ValueOrNull(g.Temperature),
            ["TemperatureQuality"] = g.Temperature.Quality.ToString(),
            ["TemperatureReason"] = g.Temperature.Reason,
            ["HumidityValue"] = ValueOrNull(g.Humidity),
            ["HumidityQuality"] = g.Humidity.Quality.ToString(),
            ["HumidityReason"] = g.Humidity.Reason,
            ["AttemptTimeUtc"] = g.AttemptTimeUtc.ToUniversalTime().ToString("O"),
            ["ReceivedTimeUtc"] = now.ToString("O"),
            ["TimeZoneOffsetMinutes"] = (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static object? ValueOrNull(MetricReading m) =>
        m.Value.HasValue && (m.Quality == MetricQuality.Good || m.Quality == MetricQuality.OutOfRange)
            ? m.Value.Value : null;

    private static DateTimeOffset MinuteStartOf(DateTimeOffset at) => new(
        at.UtcDateTime.Year, at.UtcDateTime.Month, at.UtcDateTime.Day,
        at.UtcDateTime.Hour, at.UtcDateTime.Minute, 0, TimeSpan.Zero);

    private static string MinuteKey(string deviceId, DateTimeOffset at) =>
        $"{deviceId}|{MinuteStartOf(at).ToString("O")}";

    /// <summary>
    /// 分钟汇总的待传载荷：只存定位键，不存聚合值。
    /// 原因：同步时必须读取本地 MinuteSummary 的***最新***聚合值；
    /// 若存入单次样本值，后到的样本会把远端整分钟汇总覆盖为单值（数据错误）。
    /// </summary>
    private static string BuildMinutePayload(DeviceIdentity device, ThresholdSnapshot th, ReadingGroup g)
    {
        var start = MinuteStartOf(g.AttemptTimeUtc);
        var payload = new Dictionary<string, object?>
        {
            ["DeviceId"] = device.DeviceId,
            ["WindowStartUtc"] = start.ToString("O"),
            ["ThresholdVersionId"] = th.VersionId
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    /// <summary>阈值版本入队（阈值变更时调用）。</summary>
    public async Task EnqueueThresholdAsync(DeviceIdentity device, ThresholdSnapshot th, CancellationToken ct = default)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["VersionId"] = th.VersionId,
            ["DeviceId"] = device.DeviceId,
            ["TemperatureLow"] = th.TemperatureLow,
            ["TemperatureHigh"] = th.TemperatureHigh,
            ["HumidityLow"] = th.HumidityLow,
            ["HumidityHigh"] = th.HumidityHigh,
            ["EffectiveFromUtc"] = th.EffectiveFromUtc.ToUniversalTime().ToString("O"),
            ["Source"] = th.Source
        });
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = cn.BeginTransaction();
        await EnqueueOutboxAsync(cn, tx, device.DeviceId, "threshold", th.VersionId.ToString(), payload, ct).ConfigureAwait(false);
        tx.Commit();
    }

    private static void AddMetric(SqliteCommand cmd, string v, string q, string r, MetricReading m)
    {
        bool hasValue = m.Value.HasValue &&
                        (m.Quality == MetricQuality.Good || m.Quality == MetricQuality.OutOfRange);
        cmd.Parameters.AddWithValue(v, hasValue ? m.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(q, (int)m.Quality);
        cmd.Parameters.AddWithValue(r, (object?)m.Reason ?? DBNull.Value);
    }

    private static async Task UpsertDeviceAsync(SqliteConnection cn, SqliteTransaction tx,
        DeviceIdentity device, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Device(DeviceId,DisplayName,Location,ProtocolId,ProtocolVersion,CreatedAtUtc)
            VALUES($id,$name,$loc,$pid,$pv,$t)
            ON CONFLICT(DeviceId) DO UPDATE SET DisplayName=$name, Location=$loc
            WHERE DisplayName<>$name OR COALESCE(Location,'')<>$loc
            """;
        cmd.Parameters.AddWithValue("$id", device.DeviceId);
        cmd.Parameters.AddWithValue("$name", device.DisplayName);
        cmd.Parameters.AddWithValue("$loc", device.Location ?? "");
        cmd.Parameters.AddWithValue("$pid", device.ProtocolId);
        cmd.Parameters.AddWithValue("$pv", device.ProtocolVersion ?? "");
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        int changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (changed > 0 || await NeedsMetadataAsync(cn, tx, "device", device.DeviceId, ct).ConfigureAwait(false))
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                device.DeviceId, device.DisplayName, device.Location, device.ProtocolId, device.ProtocolVersion,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            });
            await EnqueueOutboxAsync(cn, tx, device.DeviceId, "device", device.DeviceId, payload, ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureThresholdAsync(SqliteConnection cn, SqliteTransaction tx,
        DeviceIdentity device, ThresholdSnapshot th, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ThresholdVersion(VersionId,DeviceId,TemperatureLow,TemperatureHigh,HumidityLow,HumidityHigh,EffectiveFromUtc,Source)
            SELECT $vid,$id,$tl,$tH,$hl,$hh,$t,$src
            WHERE NOT EXISTS (SELECT 1 FROM ThresholdVersion WHERE VersionId=$vid)
            """;
        cmd.Parameters.AddWithValue("$vid", th.VersionId);
        cmd.Parameters.AddWithValue("$id", device.DeviceId);
        cmd.Parameters.AddWithValue("$tl", (object?)th.TemperatureLow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tH", (object?)th.TemperatureHigh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hl", (object?)th.HumidityLow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hh", (object?)th.HumidityHigh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", th.EffectiveFromUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$src", th.Source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var owner = cn.CreateCommand();
        owner.Transaction = tx;
        owner.CommandText = "SELECT DeviceId FROM ThresholdVersion WHERE VersionId=$version";
        owner.Parameters.AddWithValue("$version", th.VersionId);
        if (!string.Equals(Convert.ToString(await owner.ExecuteScalarAsync(ct).ConfigureAwait(false)), device.DeviceId, StringComparison.Ordinal))
            throw new InvalidOperationException("阈值版本编号已属于其他传感器，不能混用历史上下限。请重新保存当前传感器的上下限。");
        if (await NeedsMetadataAsync(cn, tx, "threshold", th.VersionId.ToString(), ct).ConfigureAwait(false))
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                th.VersionId, DeviceId = device.DeviceId, th.TemperatureLow, th.TemperatureHigh,
                th.HumidityLow, th.HumidityHigh, EffectiveFromUtc = th.EffectiveFromUtc.ToUniversalTime().ToString("O"), th.Source
            });
            await EnqueueOutboxAsync(cn, tx, device.DeviceId, "threshold", th.VersionId.ToString(), payload, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> NeedsMetadataAsync(SqliteConnection cn, SqliteTransaction tx, string type, string key, CancellationToken ct)
    {
        await using var command = cn.CreateCommand();
        command.Transaction = tx; command.CommandText = "SELECT 1 FROM Outbox WHERE RecordType=$type AND RecordKey=$key LIMIT 1";
        command.Parameters.AddWithValue("$type", type); command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
    }

    /// <summary>更新当前分钟窗口。缺测样本不产生数值，只增加缺测标记。</summary>
    private static async Task UpdateMinuteAsync(SqliteConnection cn, SqliteTransaction tx,
        DeviceIdentity device, ThresholdSnapshot th, ReadingGroup g, CancellationToken ct)
    {
        var start = new DateTimeOffset(g.AttemptTimeUtc.UtcDateTime.Year, g.AttemptTimeUtc.UtcDateTime.Month,
            g.AttemptTimeUtc.UtcDateTime.Day, g.AttemptTimeUtc.UtcDateTime.Hour, g.AttemptTimeUtc.UtcDateTime.Minute, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);

        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        // SessionId 用空串而不是 NULL：唯一键 (DeviceId,WindowStartUtc,ThresholdVersionId,SessionId)
        // 中的 NULL 在 SQLite 里互不相等，会让 ON CONFLICT 永不触发，
        // 同一分钟每来一个样本就新增一行、均值退化为单次样本值。
        // 空串在两引擎中语义一致（SQL Server 的唯一索引把 NULL 视为相等，行为原本就不同）。
        cmd.CommandText = """
            INSERT INTO MinuteSummary(
              DeviceId,WindowStartUtc,WindowEndUtc,ThresholdVersionId,SessionId,
              TemperatureSum,TemperatureValidCount,TemperatureMin,TemperatureMinAtUtc,TemperatureMax,TemperatureMaxAtUtc,
              HumiditySum,HumidityValidCount,HumidityMin,HumidityMinAtUtc,HumidityMax,HumidityMaxAtUtc,
              AttemptCount,MissedSlots,TempOutOfRangeCount,HumidityOutOfRangeCount,TempMissing,HumidityMissing)
            VALUES($d,$ws,$we,$th,$sess,
              $ts,$tvc,$tmin,$tminAt,$tmax,$tmaxAt,
              $hs,$hvc,$hmin,$hminAt,$hmax,$hmaxAt,
              1,0,$toor,$hoor,$tmiss,$hmiss)
            ON CONFLICT(DeviceId,WindowStartUtc,ThresholdVersionId,SessionId) DO UPDATE SET
              -- 用 COALESCE 包住待累加值：缺测时参数是 NULL，而 SQL 里 NULL + 数字 = NULL，
              -- 会把整分钟已累计的总和与计数抹掉（例如一分钟内一次湿度失败就让湿度均值永远为空）。
              -- 累加一律按“缺测不参与计算”，不是按 0 参与。
              TemperatureSum = COALESCE(TemperatureSum,0) + COALESCE($ts,0),
              TemperatureValidCount = TemperatureValidCount + $tvc,
              TemperatureMin = CASE WHEN $tvc>0 AND (TemperatureMin IS NULL OR $tmin < TemperatureMin) THEN $tmin ELSE TemperatureMin END,
              TemperatureMinAtUtc = CASE WHEN $tvc>0 AND (TemperatureMin IS NULL OR $tmin < TemperatureMin) THEN $tminAt ELSE TemperatureMinAtUtc END,
              TemperatureMax = CASE WHEN $tvc>0 AND (TemperatureMax IS NULL OR $tmax > TemperatureMax) THEN $tmax ELSE TemperatureMax END,
              TemperatureMaxAtUtc = CASE WHEN $tvc>0 AND (TemperatureMax IS NULL OR $tmax > TemperatureMax) THEN $tmaxAt ELSE TemperatureMaxAtUtc END,
              HumiditySum = COALESCE(HumiditySum,0) + COALESCE($hs,0),
              HumidityValidCount = HumidityValidCount + $hvc,
              HumidityMin = CASE WHEN $hvc>0 AND (HumidityMin IS NULL OR $hmin < HumidityMin) THEN $hmin ELSE HumidityMin END,
              HumidityMinAtUtc = CASE WHEN $hvc>0 AND (HumidityMin IS NULL OR $hmin < HumidityMin) THEN $hminAt ELSE HumidityMinAtUtc END,
              HumidityMax = CASE WHEN $hvc>0 AND (HumidityMax IS NULL OR $hmax > HumidityMax) THEN $hmax ELSE HumidityMax END,
              HumidityMaxAtUtc = CASE WHEN $hvc>0 AND (HumidityMax IS NULL OR $hmax > HumidityMax) THEN $hmaxAt ELSE HumidityMaxAtUtc END,
              AttemptCount = AttemptCount + 1,
              TempOutOfRangeCount = TempOutOfRangeCount + $toor,
              HumidityOutOfRangeCount = HumidityOutOfRangeCount + $hoor,
              TempMissing = TempMissing + $tmiss,
              HumidityMissing = HumidityMissing + $hmiss
            """;
        cmd.Parameters.AddWithValue("$d", device.DeviceId);
        cmd.Parameters.AddWithValue("$ws", start.ToString("O"));
        cmd.Parameters.AddWithValue("$we", end.ToString("O"));
        cmd.Parameters.AddWithValue("$th", th.VersionId);
        // 空串而非 NULL：见上方 INSERT 处的说明（NULL 会使唯一键失效）
        cmd.Parameters.AddWithValue("$sess", "");

        bool tOk = g.Temperature.Value.HasValue;
        bool hOk = g.Humidity.Value.HasValue;
        cmd.Parameters.AddWithValue("$ts", tOk ? g.Temperature.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$tvc", tOk ? 1 : 0);
        cmd.Parameters.AddWithValue("$tmin", tOk ? g.Temperature.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$tminAt", tOk ? g.AttemptTimeUtc.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$tmax", tOk ? g.Temperature.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$tmaxAt", tOk ? g.AttemptTimeUtc.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$hs", hOk ? g.Humidity.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$hvc", hOk ? 1 : 0);
        cmd.Parameters.AddWithValue("$hmin", hOk ? g.Humidity.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$hminAt", hOk ? g.AttemptTimeUtc.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$hmax", hOk ? g.Humidity.Value!.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$hmaxAt", hOk ? g.AttemptTimeUtc.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$toor", g.Temperature.Quality == MetricQuality.OutOfRange ? 1 : 0);
        cmd.Parameters.AddWithValue("$hoor", g.Humidity.Quality == MetricQuality.OutOfRange ? 1 : 0);
        cmd.Parameters.AddWithValue("$tmiss", tOk ? 0 : 1);
        cmd.Parameters.AddWithValue("$hmiss", hOk ? 0 : 1);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task MarkConsumedAsync(SqliteConnection cn, SqliteTransaction tx, long sampleId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE SampleJournal SET Aggregated=1 WHERE SampleId=$id;
            INSERT INTO AggregateCheckpoint(CheckpointId,LastConsumedSampleId,UpdatedAtUtc)
            VALUES(1,$id,$t)
            ON CONFLICT(CheckpointId) DO UPDATE SET LastConsumedSampleId=$id, UpdatedAtUtc=$t;
            """;
        cmd.Parameters.AddWithValue("$id", sampleId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> CountSamplesAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SampleJournal";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<long> CountMinutesAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MinuteSummary";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<List<MinutePoint>> QueryRecentMinutesAsync(int minutes, CancellationToken ct = default)
        => await QueryRecentMinutesForDeviceAsync(minutes, null, ct).ConfigureAwait(false);

    public async Task<List<MinutePoint>> QueryRecentMinutesForDeviceAsync(int minutes, string? deviceId, CancellationToken ct = default)
    {
        var list = new List<MinutePoint>();
        var since = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT WindowStartUtc,
                   CASE WHEN SUM(TemperatureValidCount)>0
                        THEN SUM(COALESCE(TemperatureSum,0))/SUM(TemperatureValidCount) END,
                   CASE WHEN SUM(HumidityValidCount)>0
                        THEN SUM(COALESCE(HumiditySum,0))/SUM(HumidityValidCount) END,
                   SUM(TemperatureValidCount), SUM(HumidityValidCount)
            FROM MinuteSummary WHERE WindowStartUtc >= $since AND ($device IS NULL OR DeviceId=$device)
            GROUP BY WindowStartUtc ORDER BY WindowStartUtc
            """;
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$device", (object?)deviceId ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MinutePoint
            {
                MinuteStartUtc = DateTimeOffset.Parse(r.GetString(0)),
                TemperatureAvg = r.IsDBNull(1) ? null : r.GetDouble(1),
                HumidityAvg = r.IsDBNull(2) ? null : r.GetDouble(2),
                TempValidCount = r.GetInt32(3),
                HumidityValidCount = r.GetInt32(4)
            });
        }
        return list;
    }

    /// <summary>批量写入监控事件。事件只记录状态***变化***，不重复刷屏。</summary>
    public async Task<int> AppendEventsAsync(IReadOnlyList<MonitorEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return 0;
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO MonitorEvent(DeviceId,OccurredAtUtc,EventType,Severity,Context,ThresholdVersionId)
            VALUES($d,$o,$t,$s,$c,$v)
            """;
        var pd = cmd.Parameters.Add("$d", SqliteType.Text);
        var po = cmd.Parameters.Add("$o", SqliteType.Text);
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var ps = cmd.Parameters.Add("$s", SqliteType.Text);
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pv = cmd.Parameters.Add("$v", SqliteType.Integer);
        int n = 0;
        foreach (var e in events)
        {
            pd.Value = e.DeviceId;
            po.Value = e.OccurredAtUtc.ToUniversalTime().ToString("O");
            pt.Value = e.EventType;
            ps.Value = (object?)e.Severity ?? DBNull.Value;
            pc.Value = (object?)e.Context ?? DBNull.Value;
            pv.Value = e.ThresholdVersionId.HasValue ? e.ThresholdVersionId.Value : DBNull.Value;
            n += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        // 事件也入待传队列（远端不可达时本地先行，恢复后自动补传）
        await using (var ev = cn.CreateCommand())
        {
            ev.Transaction = tx;
            ev.CommandText = """
                INSERT INTO Outbox(DestinationId,RecordType,RecordKey,Payload,AttemptCount,Confirmed,CreatedAtUtc)
                SELECT NULL,'event',$k,$p,0,0,$now
                WHERE NOT EXISTS (SELECT 1 FROM Outbox WHERE RecordType='event' AND RecordKey=$k AND DestinationId IS NULL)
                """;
            var k = ev.Parameters.Add("$k", SqliteType.Text);
            var p = ev.Parameters.Add("$p", SqliteType.Text);
            ev.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            foreach (var e in events)
            {
                k.Value = $"{e.DeviceId}|{e.OccurredAtUtc.ToUniversalTime():O}|{e.EventType}";
                p.Value = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["DeviceId"] = e.DeviceId,
                    ["OccurredAtUtc"] = e.OccurredAtUtc.ToUniversalTime().ToString("O"),
                    ["EventType"] = e.EventType,
                    ["Severity"] = e.Severity,
                    ["Context"] = e.Context ?? "",
                    ["ThresholdVersionId"] = e.ThresholdVersionId
                });
                await ev.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return n;
    }

    /// <summary>待传（未确认）项数量。</summary>
    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Outbox WHERE Confirmed=0";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 删除超过保留天数的逐次样本。
    /// 只删已聚合（Aggregated=1）的样本，未聚合样本必须先完成分钟汇总，
    /// 否则会丢数据；分钟汇总长期保留，不受此清理影响。
    /// </summary>
    public async Task<int> CleanupSamplesAsync(int retentionDays, CancellationToken ct = default)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "DELETE FROM SampleJournal WHERE Aggregated=1 AND AttemptTimeUtc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>最近事件（界面追溯用，倒序）。</summary>
    public async Task<List<MonitorEvent>> QueryRecentEventsAsync(int count, CancellationToken ct = default)
    {
        var list = new List<MonitorEvent>();
        await using var cn = new SqliteConnection(_conn);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT EventId,DeviceId,OccurredAtUtc,EventType,Severity,Context,ThresholdVersionId
            FROM MonitorEvent ORDER BY EventId DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", count);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MonitorEvent
            {
                EventId = r.GetInt64(0),
                DeviceId = r.GetString(1),
                OccurredAtUtc = DateTimeOffset.Parse(r.GetString(2)),
                EventType = r.GetString(3),
                Severity = r.IsDBNull(4) ? MonitorEventSeverity.Info : r.GetString(4),
                Context = r.IsDBNull(5) ? null : r.GetString(5),
                ThresholdVersionId = r.IsDBNull(6) ? null : r.GetInt64(6)
            });
        }
        return list;
    }

    /// <summary>
    /// 释放本地存储：清空 SQLite 连接池，否则外部操作（备份、恢复、复制文件）
    /// 会因数据库文件仍被池内连接占用而失败。
    /// </summary>
    public void Dispose() => SqliteConnection.ClearAllPools();
}

/// <summary>从嵌入资源或旁置文件加载 SQL 脚本。</summary>
public static class SchemaLoader
{
    private static readonly Dictionary<string, string> Cache = new();

    public static string Load(string fileName)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(fileName, out var cached)) return cached;
        }
        var asm = typeof(SchemaLoader).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        string text;
        if (name is not null)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var sr = new StreamReader(s);
            text = sr.ReadToEnd();
        }
        else
        {
            // 回退：源码树内旁置查找（开发期运行）
            var dir = AppContext.BaseDirectory;
            var candidates = new List<string>();
            for (int i = 0; i < 6 && dir is not null; i++)
            {
                candidates.Add(Path.Combine(dir, "Sql", fileName));
                candidates.Add(Path.Combine(dir, "src", "ThermoHygro.Infrastructure", "Sql", fileName));
                dir = Path.GetDirectoryName(dir);
            }
            var hit = candidates.FirstOrDefault(File.Exists);
            text = hit is not null ? File.ReadAllText(hit) : "";
        }
        lock (Cache) Cache[fileName] = text;
        return text;
    }
}
