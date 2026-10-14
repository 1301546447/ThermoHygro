using System;
using System.Collections.Generic;

namespace ThermoHygro.Core;

/// <summary>监控事件类型（写入 MonitorEvent 表，供报表与追溯）。</summary>
public static class MonitorEventTypes
{
    public const string TempOutOfRange = "温度超限";
    public const string TempRecovered = "温度恢复";
    public const string HumidityOutOfRange = "湿度超限";
    public const string HumidityRecovered = "湿度恢复";
    public const string MetricMissing = "指标缺测";
    public const string MetricRecovered = "指标恢复";
    public const string Disconnected = "通讯中断";
    public const string Reconnected = "通讯恢复";
    public const string StorageFault = "存储故障";
    public const string StorageRecovered = "存储恢复";
    public const string DeviceOffline = "设备离线";
    public const string ClockJump = "时钟跳变";
    public const string ThresholdChanged = "阈值变更";
    public const string Cleanup = "样本清理";
    public const string RemoteSyncFault = "远端同步失败";
    public const string RemoteSyncRecovered = "远端同步恢复";
}

public static class MonitorEventSeverity
{
    public const string Info = "信息";
    public const string Warning = "警告";
    public const string Error = "错误";
}

/// <summary>一条监控事件。Severity 与 Context 直接落库，便于人工追溯。</summary>
public sealed class MonitorEvent
{
    public long EventId { get; init; }
    public string DeviceId { get; init; } = "";
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string EventType { get; init; } = "";
    public string Severity { get; init; } = MonitorEventSeverity.Info;
    public string? Context { get; init; }
    public long? ThresholdVersionId { get; init; }
}

/// <summary>
/// 事件产生器：把"连续状态"转换为"状态变化事件"，避免每分钟重复记录。
/// 只有状态发生跨越时才产生事件（进超限→一条；恢复→一条）。
/// </summary>
public sealed class EventDetector
{
    private bool _tempOut, _humOut, _tempMissing, _humMissing, _disconnected, _storageFault, _remoteFault;
    private DateTimeOffset? _lastAttemptUtc;
    private bool _primed;

    /// <summary>比较本轮结果与上轮状态，返回需要写入的事件列表。</summary>
    public List<MonitorEvent> Detect(
        string deviceId,
        ReadingGroup group,
        bool roundOk,
        StorageState storage,
        RemoteSyncState remote,
        long thresholdVersionId)
    {
        var now = group.AttemptTimeUtc;
        var list = new List<MonitorEvent>();
        var t = group.Temperature;
        var h = group.Humidity;

        // 时钟跳变：本轮时间早于上轮（回拨）或跳跃超过 30 分钟
        if (_lastAttemptUtc.HasValue)
        {
            var delta = now - _lastAttemptUtc.Value;
            if (delta < TimeSpan.FromMinutes(-1) || delta > TimeSpan.FromMinutes(30))
                list.Add(New(deviceId, now, MonitorEventTypes.ClockJump, MonitorEventSeverity.Warning,
                    $"上轮 {_lastAttemptUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} → 本轮 {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}（间隔 {delta.TotalMinutes:F1} 分钟）",
                    thresholdVersionId));
        }
        _lastAttemptUtc = now;

        // 温度超限
        if (t.Quality == MetricQuality.OutOfRange)
        {
            if (!_tempOut)
                list.Add(New(deviceId, now, MonitorEventTypes.TempOutOfRange, MonitorEventSeverity.Warning,
                    $"温度 {t.Value:F1} ℃ 超出阈值 {t.Reason}", thresholdVersionId));
            _tempOut = true;
        }
        else if (t.Quality is MetricQuality.Good)
        {
            if (_tempOut)
                list.Add(New(deviceId, now, MonitorEventTypes.TempRecovered, MonitorEventSeverity.Info,
                    $"温度回到阈值内：{t.Value:F1} ℃", thresholdVersionId));
            _tempOut = false;
        }

        // 湿度超限
        if (h.Quality == MetricQuality.OutOfRange)
        {
            if (!_humOut)
                list.Add(New(deviceId, now, MonitorEventTypes.HumidityOutOfRange, MonitorEventSeverity.Warning,
                    $"湿度 {h.Value:F1} %RH 超出阈值 {h.Reason}", thresholdVersionId));
            _humOut = true;
        }
        else if (h.Quality is MetricQuality.Good)
        {
            if (_humOut)
                list.Add(New(deviceId, now, MonitorEventTypes.HumidityRecovered, MonitorEventSeverity.Info,
                    $"湿度回到阈值内：{h.Value:F1} %RH", thresholdVersionId));
            _humOut = false;
        }

        // 指标缺测（仅在有采集能力时判定；不支持的能力不算缺测）
        bool tMissing = t.Quality is MetricQuality.Failed or MetricQuality.Stale;
        bool hMissing = h.Quality is MetricQuality.Failed or MetricQuality.Stale;
        if (tMissing != _tempMissing)
        {
            list.Add(New(deviceId, now, tMissing ? MonitorEventTypes.MetricMissing : MonitorEventTypes.MetricRecovered,
                tMissing ? MonitorEventSeverity.Warning : MonitorEventSeverity.Info,
                "温度" + (tMissing ? $"缺测：{t.Reason ?? "读取失败"}" : "恢复读取"), thresholdVersionId));
            _tempMissing = tMissing;
        }
        if (hMissing != _humMissing)
        {
            list.Add(New(deviceId, now, hMissing ? MonitorEventTypes.MetricMissing : MonitorEventTypes.MetricRecovered,
                hMissing ? MonitorEventSeverity.Warning : MonitorEventSeverity.Info,
                "湿度" + (hMissing ? $"缺测：{h.Reason ?? "读取失败"}" : "恢复读取"), thresholdVersionId));
            _humMissing = hMissing;
        }

        // 通讯中断 / 恢复
        bool disconnected = !roundOk || group.Temperature.Quality == MetricQuality.Failed && group.Humidity.Quality == MetricQuality.Failed;
        if (!_primed)
        {
            // 首轮就失败也要报告一次：从"未知"到"中断"本身是需要追溯的事件
            if (disconnected)
                list.Add(New(deviceId, now, MonitorEventTypes.Disconnected, MonitorEventSeverity.Error,
                    "首次读取即失败，进入重连退避", thresholdVersionId));
            _disconnected = disconnected; _storageFault = storage is StorageState.Fault;
            _remoteFault = remote is RemoteSyncState.Error; _primed = true;
        }
        else
        {
            if (disconnected != _disconnected)
            {
                list.Add(New(deviceId, now, disconnected ? MonitorEventTypes.Disconnected : MonitorEventTypes.Reconnected,
                    disconnected ? MonitorEventSeverity.Error : MonitorEventSeverity.Info,
                    disconnected ? "连续读取失败，进入重连退避" : "通讯已恢复", thresholdVersionId));
                _disconnected = disconnected;
            }
            bool sf = storage is StorageState.Fault;
            if (sf != _storageFault)
            {
                list.Add(New(deviceId, now, sf ? MonitorEventTypes.StorageFault : MonitorEventTypes.StorageRecovered,
                    sf ? MonitorEventSeverity.Error : MonitorEventSeverity.Info,
                    sf ? "本地保存失败（读数仍可查看）" : "本地保存已恢复", thresholdVersionId));
                _storageFault = sf;
            }
            bool rf = remote is RemoteSyncState.Error;
            if (rf != _remoteFault)
            {
                list.Add(New(deviceId, now, rf ? MonitorEventTypes.RemoteSyncFault : MonitorEventTypes.RemoteSyncRecovered,
                    rf ? MonitorEventSeverity.Warning : MonitorEventSeverity.Info,
                    rf ? "远端同步失败（本地仍持续留存）" : "远端同步已恢复", thresholdVersionId));
                _remoteFault = rf;
            }
        }
        return list;
    }

    /// <summary>切换本地库后调用：把存储故障记忆复位，避免新库被旧库的故障状态污染。</summary>
    public void ResetStorageState() => _storageFault = false;

    /// <summary>阈值变更事件（由界面在保存新阈值时调用）。</summary>
    public static MonitorEvent ThresholdChanged(string deviceId, ThresholdSnapshot th, string detail) =>
        New(deviceId, DateTimeOffset.UtcNow, MonitorEventTypes.ThresholdChanged, MonitorEventSeverity.Info,
            $"{detail}｜版本 {th.VersionId}", th.VersionId);

    /// <summary>清理事件。</summary>
    public static MonitorEvent CleanupEvent(string deviceId, int deleted, int retentionDays) =>
        New(deviceId, DateTimeOffset.UtcNow, MonitorEventTypes.Cleanup, MonitorEventSeverity.Info,
            $"删除 {deleted} 条超过 {retentionDays} 天的逐次样本（分钟汇总不受影响）", null);

    private static MonitorEvent New(string deviceId, DateTimeOffset at, string type, string severity, string? context, long? versionId) => new()
    {
        DeviceId = deviceId,
        OccurredAtUtc = at,
        EventType = type,
        Severity = severity,
        Context = context,
        ThresholdVersionId = versionId
    };
}