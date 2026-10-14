using System;
using System.Threading;
using System.Threading.Tasks;
using ThermoHygro.Core;

namespace ThermoHygro.Infrastructure;

/// <summary>可验证的时间来源（便于测试时钟跳变）。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>本地保存结果。只有 Saved=true 才允许界面显示"已保存"。</summary>
public sealed class SaveOutcome
{
    public bool Saved { get; init; }
    public long SampleId { get; init; }
    public string? Error { get; init; }
}

/// <summary>本地存储接口。</summary>
public interface ILocalStore : IDisposable
{
    string DatabasePath { get; }
    StorageState State { get; }
    string LastError { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task<SaveOutcome> AppendSampleAsync(DeviceIdentity device, ThresholdSnapshot thresholds, ReadingGroup group, CancellationToken ct = default);
    Task<long> CountSamplesAsync(CancellationToken ct = default);
    Task<long> CountMinutesAsync(CancellationToken ct = default);
    /// <summary>最近 N 分钟的分钟汇总（用于近 1 小时趋势）。</summary>
    Task<System.Collections.Generic.List<MinutePoint>> QueryRecentMinutesAsync(int minutes, CancellationToken ct = default);
    /// <summary>批量写入监控事件（超限/恢复/缺测/重连/存储故障等）。</summary>
    Task<int> AppendEventsAsync(System.Collections.Generic.IReadOnlyList<MonitorEvent> events, CancellationToken ct = default);
    /// <summary>删除超过保留天数的逐次样本（分钟汇总保留）；返回删除条数。</summary>
    Task<int> CleanupSamplesAsync(int retentionDays, CancellationToken ct = default);
    /// <summary>最近 N 条监控事件（用于界面追溯）。</summary>
    Task<System.Collections.Generic.List<MonitorEvent>> QueryRecentEventsAsync(int count, CancellationToken ct = default);
}

/// <summary>远端同步接口（本地先存，远端后传；断网时本地不受影响）。</summary>
public interface IRemoteSink
{
    RemoteSyncState State { get; }
    long PendingCount { get; }
    string LastError { get; }
    int Retry();
}

/// <summary>远端同步目标（SQL Server）配置。</summary>
public sealed class RemoteSyncOptions
{
    /// <summary>连接字符串（不写日志、不写配置明文）。</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>目标身份：服务器/实例/数据库的组合，用于切换目标时区分待传项。</summary>
    public string DestinationIdentity { get; set; } = "";
    public int BatchSize { get; set; } = 200;
    public int SyncIntervalSeconds { get; set; } = 30;
}

/// <summary>近 1 小时趋势的单个数据点。</summary>
public sealed class MinutePoint
{
    public DateTimeOffset MinuteStartUtc { get; init; }
    public double? TemperatureAvg { get; init; }
    public double? HumidityAvg { get; init; }
    public int TempValidCount { get; init; }
    public int HumidityValidCount { get; init; }
}