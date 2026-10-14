using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ThermoHygro.Core;

namespace ThermoHygro.Reporting;

/// <summary>
/// 历史查询：直接读 SQLite 分钟汇总表，向屏幕/CSV/PDF 提供同一份 ReportSnapshot。
/// 长区间在数据库侧按粒度分桶汇总（GROUP BY），不把五年原始行一次装入内存。
/// 缺测一律保持 null，绝不以 0 代替。
/// </summary>
public sealed class SqliteHistoryQuery : IHistoryQuery
{
    private readonly string _dbPath;
    public SqliteHistoryQuery(string databasePath) => _dbPath = databasePath;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            // 不参与连接池：否则连接会持续占用数据库文件，导致备份/恢复/复制失败
            Pooling = false
        }.ToString());
        c.Open();
        return c;
    }

    private static string Iso(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 判断库中是否存在日级表达式索引 IX_Minute_Day。
    /// 旧库可能未重建索引，此时不能用 INDEXED BY（会直接报 no such index），必须退回默认计划。
    /// </summary>
    private static bool HasDayIndex(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name='IX_Minute_Day' LIMIT 1";
            return cmd.ExecuteScalar() is not null;
        }
        catch { return false; }
    }

    private static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(PadIso(s), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// 补齐数据库侧分组返回的桶键（UTC，Z 结尾）。
    /// 为使 GROUP BY 与索引表达式一致（直接影响查询性能），库内只取子串：
    /// 每日='yyyy-MM-dd'、小时='yyyy-MM-ddTHH'、分钟='yyyy-MM-ddTHH:mm'；
    /// 这里补回完整形式再解析。
    /// </summary>
    private static string PadIso(string s)
    {
        if (s.EndsWith("Z", StringComparison.Ordinal)) return s;
        return s.Length switch
        {
            10 => s + "T00:00:00Z",       // 每日
            13 => s + ":00:00Z",           // 小时
            16 => s + ":00Z",              // 分钟
            _ => s
        };
    }

    private DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    public async Task<(DateTime? FirstLocal, DateTime? LastLocal)> GetDataRangeAsync(string deviceId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(WindowStartUtc), MAX(WindowStartUtc) FROM MinuteSummary WHERE DeviceId=$d";
        cmd.Parameters.AddWithValue("$d", deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.IsDBNull(0)) return (null, null);
        var zone = TimeZoneInfo.Local;
        return (ToLocal(ParseIso(r.GetString(0)), zone), ToLocal(ParseIso(r.GetString(1)), zone));
    }

    public async Task<ReportSnapshot> TakeSnapshotAsync(ReportQuery query, CancellationToken ct = default)
    {
        // 截止点固定：屏幕与导出共用，避免图表与统计来自不同时间点
        var cutoff = query.CutoffUtc == default ? DateTimeOffset.UtcNow : query.CutoffUtc;
        var zone = TimeZoneInfo.Local;

        var startUtc = new DateTimeOffset(query.StartLocal, zone.GetUtcOffset(query.StartLocal)).ToUniversalTime();
        var endUtc = new DateTimeOffset(query.EndLocalExclusive, zone.GetUtcOffset(query.EndLocalExclusive)).ToUniversalTime();
        var cutoffText = Iso(cutoff);

        await using var conn = Open();
        var points = new List<HistoryPoint>();

        // 数据库侧分桶：分钟=前16位；小时=前13位；每日=前10位。
        // 关键：GROUP BY 必须使用与索引完全一致的表达式，否则 SQLite 无法直接分组。
        //   * 若写成 substr(...) || 'T00:00:00'，表达式与索引 substr(...) 不匹配，
        //     会退化为“扫全表 + 临时 B-TREE 分组”。262.8 万行实测：
        //     不匹配 7.6 秒，匹配后 1.7～3.0 秒。
        //   * 因此这里只分组不拼接，格式化（补 ':00:00' / 'T00:00:00'）在客户端完成。
        bool localBuckets = zone.SupportsDaylightSavingTime || zone.GetUtcOffset(DateTime.Now) != TimeSpan.Zero;
        string timeExpression = localBuckets ? "strftime('%Y-%m-%dT%H:%M:%S',WindowStartUtc,'localtime')" : "WindowStartUtc";
        string bucketExpr = query.Granularity switch
        {
            ReportGranularity.Minute => $"substr({timeExpression},1,16)",
            ReportGranularity.Hour => $"substr({timeExpression},1,13)",
            _ => $"substr({timeExpression},1,10)"
        };

        // 长区间（每日粒度）指定日索引：避免优化器误选时间索引再建临时 B-TREE。
        // 仅当库中确实存在该索引时才指定（兼容未重建索引的旧库，否则会直接报 no such index）。
        var tableHint = "MinuteSummary";
        if (!localBuckets && query.Granularity == ReportGranularity.Day && HasDayIndex(conn))
            tableHint = "MinuteSummary INDEXED BY IX_Minute_Day";

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
SELECT {bucketExpr} AS Bucket,
       SUM(TemperatureSum), SUM(TemperatureValidCount),
       SUM(HumiditySum), SUM(HumidityValidCount),
       MIN(TemperatureMin), MIN(HumidityMin),
       MAX(TemperatureMax), MAX(HumidityMax),
       MIN(TemperatureMinAtUtc), MAX(TemperatureMaxAtUtc),
       MIN(HumidityMinAtUtc), MAX(HumidityMaxAtUtc),
       SUM(AttemptCount), SUM(MissedSlots),
       SUM(TempOutOfRangeCount), SUM(HumidityOutOfRangeCount),
       SUM(TempMissing), SUM(HumidityMissing),
       COUNT(DISTINCT ThresholdVersionId)
FROM {tableHint}
WHERE DeviceId=$d AND WindowStartUtc >= $s AND WindowStartUtc < $e AND WindowStartUtc < $cut
GROUP BY Bucket ORDER BY Bucket";
            cmd.Parameters.AddWithValue("$d", query.DeviceId);
            cmd.Parameters.AddWithValue("$s", Iso(startUtc));
            cmd.Parameters.AddWithValue("$e", Iso(endUtc));
            cmd.Parameters.AddWithValue("$cut", cutoffText);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var bucketStartUtc = ParseIso(r.GetString(0));
                if (localBuckets)
                {
                    var localStart = DateTime.SpecifyKind(bucketStartUtc.DateTime, DateTimeKind.Unspecified);
                    bucketStartUtc = new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)).ToUniversalTime();
                }
                long tValid = r.GetInt32(2), hValid = r.GetInt32(4);
                points.Add(new HistoryPoint
                {
                    BucketStartUtc = bucketStartUtc,
                    BucketStartLocal = ToLocal(bucketStartUtc, zone),
                    BucketEndLocal = ToLocal(bucketStartUtc, zone).Add(query.Granularity switch
                    {
                        ReportGranularity.Minute => TimeSpan.FromMinutes(1),
                        ReportGranularity.Hour => TimeSpan.FromHours(1),
                        _ => TimeSpan.FromDays(1)
                    }),
                    TemperatureAvg = tValid > 0 && !r.IsDBNull(1) ? r.GetDouble(1) / tValid : null,
                    TemperatureValidCount = tValid,
                    TemperatureMin = r.IsDBNull(5) ? null : r.GetDouble(5),
                    TemperatureMax = r.IsDBNull(7) ? null : r.GetDouble(7),
                    TemperatureMinAtUtc = r.IsDBNull(9) ? null : ParseIso(r.GetString(9)),
                    TemperatureMaxAtUtc = r.IsDBNull(10) ? null : ParseIso(r.GetString(10)),
                    HumidityAvg = hValid > 0 && !r.IsDBNull(3) ? r.GetDouble(3) / hValid : null,
                    HumidityValidCount = hValid,
                    HumidityMin = r.IsDBNull(6) ? null : r.GetDouble(6),
                    HumidityMax = r.IsDBNull(8) ? null : r.GetDouble(8),
                    HumidityMinAtUtc = r.IsDBNull(11) ? null : ParseIso(r.GetString(11)),
                    HumidityMaxAtUtc = r.IsDBNull(12) ? null : ParseIso(r.GetString(12)),
                    AttemptCount = r.GetInt64(13),
                    MissedSlots = r.GetInt64(14),
                    TempOutOfRangeCount = r.GetInt64(15),
                    HumidityOutOfRangeCount = r.GetInt64(16),
                    TempMissing = r.GetInt64(17),
                    HumidityMissing = r.GetInt64(18),
                    ThresholdVersionCount = r.GetInt32(19),
                    ThresholdConfigured = false
                });
            }
        }

        if (points.Count > 0)
        {
            // 查询区间内没有记录的桶也必须表现为缺测；否则停采后曲线会跨越空白时段，覆盖率会虚高。
            var actual = points.ToDictionary(point => point.BucketStartLocal);
            var cutoffLocal = ToLocal(cutoff, zone);
            var endLocal = query.EndLocalExclusive < cutoffLocal ? query.EndLocalExclusive : cutoffLocal;
            var complete = new List<HistoryPoint>();
            DateTime current = query.Granularity switch
            {
                ReportGranularity.Day => query.StartLocal.Date,
                ReportGranularity.Hour => new DateTime(query.StartLocal.Year, query.StartLocal.Month, query.StartLocal.Day, query.StartLocal.Hour, 0, 0),
                _ => new DateTime(query.StartLocal.Year, query.StartLocal.Month, query.StartLocal.Day, query.StartLocal.Hour, query.StartLocal.Minute, 0)
            };
            var step = query.Granularity == ReportGranularity.Day ? TimeSpan.FromDays(1) : query.Granularity == ReportGranularity.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1);
            while (current < endLocal)
            {
                if (complete.Count > 100000) throw new InvalidOperationException("报表分桶过多，请扩大汇总粒度或缩短日期范围。");
                ct.ThrowIfCancellationRequested();
                if (actual.TryGetValue(current, out var point)) complete.Add(point);
                else complete.Add(new HistoryPoint
                {
                    BucketStartLocal = current, BucketEndLocal = current.Add(step),
                    BucketStartUtc = new DateTimeOffset(DateTime.SpecifyKind(current, DateTimeKind.Unspecified), zone.GetUtcOffset(current)).ToUniversalTime()
                });
                current = current.Add(step);
            }
            points = complete;
        }
        var events = new List<ReportEvent>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT EventId, OccurredAtUtc, EventType, Severity, Context, ThresholdVersionId
FROM MonitorEvent
WHERE DeviceId=$d AND OccurredAtUtc >= $s AND OccurredAtUtc < $e AND OccurredAtUtc < $cut
ORDER BY OccurredAtUtc LIMIT 5000";
            cmd.Parameters.AddWithValue("$d", query.DeviceId);
            cmd.Parameters.AddWithValue("$s", Iso(startUtc));
            cmd.Parameters.AddWithValue("$e", Iso(endUtc));
            cmd.Parameters.AddWithValue("$cut", cutoffText);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var t = ParseIso(r.GetString(1));
                events.Add(new ReportEvent
                {
                    EventId = r.GetInt64(0),
                    OccurredAtUtc = t,
                    OccurredAtLocal = ToLocal(t, zone),
                    EventType = r.GetString(2),
                    Severity = r.IsDBNull(3) ? "" : r.GetString(3),
                    Context = r.IsDBNull(4) ? "" : r.GetString(4),
                    ThresholdVersionId = r.IsDBNull(5) ? null : r.GetInt64(5)
                });
            }
        }

        var stats = ReportStatsCalculator.From(points, events.Count, zone);

        // 设备信息：来自 Device 表（改名只改显示名，不改历史归属）
        string devName = query.DeviceId, location = "", protoVer = "";
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DisplayName, Location, ProtocolVersion FROM Device WHERE DeviceId=$d";
            cmd.Parameters.AddWithValue("$d", query.DeviceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                devName = r.GetString(0);
                location = r.IsDBNull(1) ? "" : r.GetString(1);
                protoVer = r.IsDBNull(2) ? "" : r.GetString(2);
            }
        }
        catch { /* 设备表不可读时退回使用编号，不阻断报表 */ }

        string thresholdNote = "未配置（按采集时保存的设置判断）";
        await using (var limits = conn.CreateCommand())
        {
            limits.CommandText = """
                SELECT COUNT(DISTINCT v.VersionId) FROM ThresholdVersion v
                JOIN MinuteSummary m ON m.ThresholdVersionId=v.VersionId AND m.DeviceId=v.DeviceId
                WHERE m.DeviceId=$d AND m.WindowStartUtc>=$s AND m.WindowStartUtc<$e AND m.WindowStartUtc<$cut
                AND (v.TemperatureLow IS NOT NULL OR v.TemperatureHigh IS NOT NULL OR v.HumidityLow IS NOT NULL OR v.HumidityHigh IS NOT NULL)
                """;
            limits.Parameters.AddWithValue("$d", query.DeviceId); limits.Parameters.AddWithValue("$s", Iso(startUtc));
            limits.Parameters.AddWithValue("$e", Iso(endUtc)); limits.Parameters.AddWithValue("$cut", cutoffText);
            long count = Convert.ToInt64(await limits.ExecuteScalarAsync(ct));
            if (count > 0) thresholdNote = $"按采集时的上下限判断，区间内共 {count} 个已配置的历史版本；修改设置不追溯改写原记录。";
        }
        return new ReportSnapshot
        {
            Query = query,
            Points = points,
            Events = events,
            Stats = stats,
            GeneratedAtUtc = cutoff,
            DeviceDisplayName = devName,
            DeviceLocation = location,
            ProtocolVersion = protoVer,
            ThresholdNote = thresholdNote
        };
    }
}
