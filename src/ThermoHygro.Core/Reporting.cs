using System;
using System.Collections.Generic;

namespace ThermoHygro.Core;

/// <summary>报表粒度。规则：1 天=分钟；2～31 天=小时；更长=每日。</summary>
public enum ReportGranularity
{
    Minute = 0,
    Hour = 1,
    Day = 2
}

/// <summary>查询范围预设。</summary>
public enum ReportScope
{
    Last30Days = 0,
    Day = 1,
    Month = 2,
    Custom = 3
}

/// <summary>
/// 一次历史查询的完整条件（含截止点与稳定版本）。
/// 屏幕显示、CSV 导出、PDF 导出必须使用同一份 ReportQuery，不允许各自重新查询。
/// </summary>
public sealed class ReportQuery
{
    public string DeviceId { get; init; } = "";
    /// <summary>报表时区的开始日期（含）。按该时区分桶。</summary>
    public DateTime StartLocal { get; init; }
    /// <summary>报表时区的结束次日（不含）。筛选内部即 [开始, 结束次日)。</summary>
    public DateTime EndLocalExclusive { get; init; }
    public ReportScope Scope { get; init; } = ReportScope.Last30Days;
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    /// <summary>报表时区在查询时刻的 UTC 偏移（分钟），用于数据库侧分桶。</summary>
    public int UtcOffsetMinutes { get; init; } = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
    public ReportGranularity Granularity { get; init; } = ReportGranularity.Minute;
    /// <summary>查询截止点（UTC）：只统计该时刻之前已提交的数据，导出不混入新到达数据。</summary>
    public DateTimeOffset CutoffUtc { get; init; }
    /// <summary>快照稳定版本号：同一快照内所有产物共享，便于核对一致性。</summary>
    public int SnapshotVersion { get; init; } = 1;

    public static ReportGranularity GranularityFor(double totalDays) =>
        totalDays <= 1.001 ? ReportGranularity.Minute
        : totalDays <= 31.001 ? ReportGranularity.Hour
        : ReportGranularity.Day;

    public double TotalDays => (EndLocalExclusive - StartLocal).TotalDays;

    /// <summary>生成规范化的查询指纹，用于核对屏幕/数据库/导出是否同一快照。</summary>
    public string Fingerprint =>
        $"{DeviceId}|{StartLocal:yyyy-MM-ddTHH:mm:ss}|{EndLocalExclusive:yyyy-MM-ddTHH:mm:ss}|"
        + $"{TimeZoneId}|{UtcOffsetMinutes}|{Granularity}|{CutoffUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}|v{SnapshotVersion}";

    public string GranularityText => Granularity switch
    {
        ReportGranularity.Minute => "分钟",
        ReportGranularity.Hour => "小时",
        _ => "每日"
    };

    public string ScopeText => Scope switch
    {
        ReportScope.Day => "按日",
        ReportScope.Month => "按月",
        ReportScope.Custom => "自定义区间",
        _ => "近 30 天"
    };
}

/// <summary>一个分桶（分钟/小时/日）的汇总点。缺测一律为 null，绝不以 0 代替。</summary>
public sealed class HistoryPoint
{
    public DateTimeOffset BucketStartUtc { get; init; }
    /// <summary>该桶在报表时区的起始时刻（显示用）。</summary>
    public DateTime BucketStartLocal { get; init; }
    public DateTime BucketEndLocal { get; init; }

    public double? TemperatureAvg { get; init; }
    public double? TemperatureMin { get; init; }
    public double? TemperatureMax { get; init; }
    public DateTimeOffset? TemperatureMinAtUtc { get; init; }
    public DateTimeOffset? TemperatureMaxAtUtc { get; init; }
    public long TemperatureValidCount { get; init; }

    public double? HumidityAvg { get; init; }
    public double? HumidityMin { get; init; }
    public double? HumidityMax { get; init; }
    public DateTimeOffset? HumidityMinAtUtc { get; init; }
    public DateTimeOffset? HumidityMaxAtUtc { get; init; }
    public long HumidityValidCount { get; init; }

    public long AttemptCount { get; init; }
    public long MissedSlots { get; init; }
    public long TempOutOfRangeCount { get; init; }
    public long HumidityOutOfRangeCount { get; init; }
    public long TempMissing { get; init; }
    public long HumidityMissing { get; init; }

    /// <summary>本桶覆盖的阈值版本数；大于 1 表示跨阈值切分。</summary>
    public int ThresholdVersionCount { get; init; } = 1;
    /// <summary>本桶是否存在已配置的阈值。未配置的历史不追溯标红。</summary>
    public bool ThresholdConfigured { get; init; }

    /// <summary>该桶完全无有效样本（整段缺测）。图表必须在此断线，不插值伪造。</summary>
    public bool IsGap => TemperatureValidCount == 0 && HumidityValidCount == 0;

    /// <summary>部分缺测（一个指标有效、另一个无）。</summary>
    public bool IsPartialGap =>
        (TemperatureValidCount > 0) != (HumidityValidCount > 0);

    public string QualityText
    {
        get
        {
            if (IsGap) return "整段缺测";
            var parts = new List<string>();
            if (TemperatureValidCount == 0) parts.Add("温度缺测");
            if (HumidityValidCount == 0) parts.Add("湿度缺测");
            if (MissedSlots > 0) parts.Add($"错过时隙 {MissedSlots}");
            return parts.Count == 0 ? "正常" : string.Join("；", parts);
        }
    }

    public string FormatLocal(string fmt = "yyyy-MM-dd HH:mm") => BucketStartLocal.ToString(fmt);
}

/// <summary>可追溯的事件记录。</summary>
public sealed class ReportEvent
{
    public long EventId { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public DateTime OccurredAtLocal { get; init; }
    /// <summary>事件类型：out-of-range / recovery / missing / reconnect / config-change / clock-change / storage-fault。</summary>
    public string EventType { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Context { get; init; } = "";
    public long? ThresholdVersionId { get; init; }

    public string EventTypeText => EventType switch
    {
        // 事件类型以 MonitorEventTypes 的中文常量为准（写入库的即为中文）。
        // 同时兼容早期文档中出现过的英文写法，避免旧库记录显示原始串。
        MonitorEventTypes.TempOutOfRange or "out-of-range" => "温度超限",
        MonitorEventTypes.TempRecovered or "recovery" => "温度恢复",
        MonitorEventTypes.HumidityOutOfRange => "湿度超限",
        MonitorEventTypes.HumidityRecovered => "湿度恢复",
        MonitorEventTypes.MetricMissing or "missing" => "指标缺测",
        MonitorEventTypes.MetricRecovered => "指标恢复",
        MonitorEventTypes.Disconnected or "reconnect" => "通讯中断",
        MonitorEventTypes.Reconnected => "通讯恢复",
        MonitorEventTypes.StorageFault or "storage-fault" => "存储故障",
        MonitorEventTypes.StorageRecovered => "存储恢复",
        MonitorEventTypes.ClockJump or "clock-change" => "时钟跳变",
        MonitorEventTypes.ThresholdChanged or "threshold-change" or "config-change" => "阈值变更",
        MonitorEventTypes.Cleanup => "样本清理",
        MonitorEventTypes.RemoteSyncFault => "远端同步失败",
        MonitorEventTypes.RemoteSyncRecovered => "远端同步恢复",
        _ => EventType
    };
}

/// <summary>区间统计（跨桶按总和/有效次数加权，不对已四舍五入的均值再平均）。</summary>
public sealed class ReportStats
{
    public double? TemperatureAvg { get; init; }
    public double? TemperatureMin { get; init; }
    public double? TemperatureMax { get; init; }
    public DateTimeOffset? TemperatureMinAtUtc { get; init; }
    public DateTimeOffset? TemperatureMaxAtUtc { get; init; }
    public long TemperatureValidCount { get; init; }

    public double? HumidityAvg { get; init; }
    public double? HumidityMin { get; init; }
    public double? HumidityMax { get; init; }
    public DateTimeOffset? HumidityMinAtUtc { get; init; }
    public DateTimeOffset? HumidityMaxAtUtc { get; init; }
    public long HumidityValidCount { get; init; }

    public long BucketCount { get; init; }
    public long GapBucketCount { get; init; }
    public long PartialGapBucketCount { get; init; }
    public long AttemptCount { get; init; }
    public long MissedSlots { get; init; }
    public long TempOutOfRangeCount { get; init; }
    public long HumidityOutOfRangeCount { get; init; }
    public long TempMissing { get; init; }
    public long HumidityMissing { get; init; }
    public long EventCount { get; init; }

    /// <summary>有温湿度数据的时间覆盖率（按分桶计）。</summary>
    public double CoverageRatio { get; init; }
}

/// <summary>
/// 一次查询的不可变快照。屏幕、CSV、PDF 全部消费同一个实例，
/// 保证"屏幕/数据库/导出"三处一致；新数据到达需重新查询，不混用。
/// </summary>
public sealed class ReportSnapshot
{
    public ReportQuery Query { get; init; } = new();
    public IReadOnlyList<HistoryPoint> Points { get; init; } = Array.Empty<HistoryPoint>();
    public IReadOnlyList<ReportEvent> Events { get; init; } = Array.Empty<ReportEvent>();
    public ReportStats Stats { get; init; } = new();
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string DeviceDisplayName { get; init; } = "";
    public string DeviceLocation { get; init; } = "";
    public string ProtocolVersion { get; init; } = "";

    /// <summary>阈值说明：未配置时不标红、不追溯。</summary>
    public string ThresholdNote { get; init; } = "";
    public ThresholdSnapshot? Thresholds { get; init; }

    /// <summary>快照指纹（含查询条件、截止点、版本号）。</summary>
    public string Fingerprint => Query.Fingerprint;

    /// <summary>供界面/导出显示的区间标题。</summary>
    public string RangeText =>
        $"{Query.StartLocal:yyyy-MM-dd} ～ {Query.EndLocalExclusive.AddDays(-1):yyyy-MM-dd}";
}
