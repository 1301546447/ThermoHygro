using System;

namespace ThermoHygro.Core;

/// <summary>指标（读数通道）。温度与湿度分别维护质量状态，不合并为一个"正常/异常"。</summary>
public enum MetricKind
{
    Temperature = 0,
    Humidity = 1
}

/// <summary>
/// 单指标质量状态。协议未提供的指标为 NotSupported，与"支持但本次失败"分别表示。
/// </summary>
public enum MetricQuality
{
    /// <summary>协议未提供此项指标。</summary>
    NotSupported = 0,
    /// <summary>支持，但尚未读取。</summary>
    Unknown = 1,
    /// <summary>本次读取成功。</summary>
    Good = 2,
    /// <summary>本次读取失败（有失败原因）。</summary>
    Failed = 3,
    /// <summary>成功值已超过新鲜度窗口，不再是本次读数。</summary>
    Stale = 4,
    /// <summary>数值超出配置的上下限（属于指标状态，不是通信失败）。</summary>
    OutOfRange = 5
}

/// <summary>整体采集器连接状态。超限不在此枚举内。</summary>
public enum CollectorState
{
    NotConfigured = 0,
    Disconnected = 1,
    Connecting = 2,
    Acquiring = 3,
    PartiallyMissing = 4,
    Reconnecting = 5,
    Paused = 6,
    StorageFault = 7
}

/// <summary>本地保存状态。</summary>
public enum StorageState
{
    Unknown = 0,
    Ok = 1,
    Fault = 2
}

/// <summary>远端同步状态。</summary>
public enum RemoteSyncState
{
    Disabled = 0,
    Idle = 1,
    Pending = 2,
    Error = 3
}

/// <summary>单个指标的读数快照。</summary>
public sealed class MetricReading
{
    public MetricKind Kind { get; init; }
    public MetricQuality Quality { get; init; } = MetricQuality.Unknown;
    /// <summary>本次读数；未知/失败/过期时为 null，绝不用旧值补齐。</summary>
    public double? Value { get; init; }
    /// <summary>该读数实际对应的采集时间（UTC）。</summary>
    public DateTimeOffset? TimestampUtc { get; init; }
    /// <summary>失败或状态原因（中文，面向用户）。</summary>
    public string? Reason { get; init; }
    /// <summary>若保留上一次有效值用于灰显，这里是它的时间。</summary>
    public DateTimeOffset? LastGoodTimestampUtc { get; init; }
    public double? LastGoodValue { get; init; }

    public static MetricReading NotSupported(MetricKind k) => new() { Kind = k, Quality = MetricQuality.NotSupported };
    public static MetricReading Unread(MetricKind k) => new() { Kind = k, Quality = MetricQuality.Unknown };
}

/// <summary>一组温湿度读数（同一轮采集）。</summary>
public sealed class ReadingGroup
{
    public DateTimeOffset AttemptTimeUtc { get; init; }
    public MetricReading Temperature { get; init; } = MetricReading.Unread(MetricKind.Temperature);
    public MetricReading Humidity { get; init; } = MetricReading.Unread(MetricKind.Humidity);
    public bool AnySuccess => Temperature.Quality is MetricQuality.Good or MetricQuality.OutOfRange
                           || Humidity.Quality is MetricQuality.Good or MetricQuality.OutOfRange;
}

/// <summary>阈值版本（不可变；修改产生新版本号，不追溯改写历史）。</summary>
public sealed class ThresholdSnapshot
{
    public long VersionId { get; init; }
    public double? TemperatureLow { get; init; }
    public double? TemperatureHigh { get; init; }
    public double? HumidityLow { get; init; }
    public double? HumidityHigh { get; init; }
    public DateTimeOffset EffectiveFromUtc { get; init; }
    public string Source { get; init; } = "用户设置";

    /// <summary>阈值是否已配置（初始为空表示未配置，不标红历史）。</summary>
    public bool IsConfigured =>
        TemperatureLow.HasValue || TemperatureHigh.HasValue || HumidityLow.HasValue || HumidityHigh.HasValue;

    public bool IsOutOfRange(MetricKind kind, double value)
    {
        if (kind == MetricKind.Temperature)
        {
            if (TemperatureLow.HasValue && value < TemperatureLow.Value) return true;
            if (TemperatureHigh.HasValue && value > TemperatureHigh.Value) return true;
        }
        else
        {
            if (HumidityLow.HasValue && value < HumidityLow.Value) return true;
            if (HumidityHigh.HasValue && value > HumidityHigh.Value) return true;
        }
        return false;
    }
}

/// <summary>串口与协议参数（用户可见的设置项）。</summary>
public sealed class SerialSettings
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public int SlaveAddress { get; set; } = 1;
    public int ReadTimeoutMs { get; set; } = 3000;
    public int PollIntervalSeconds { get; set; } = 5;

    public SerialSettings Clone() => new()
    {
        PortName = PortName,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
        SlaveAddress = SlaveAddress,
        ReadTimeoutMs = ReadTimeoutMs,
        PollIntervalSeconds = PollIntervalSeconds
    };

    public string Describe() =>
        $"{PortName} · {BaudRate} {DataBits}{(Parity == "None" ? "N" : Parity[..1].ToUpperInvariant())}{(StopBits == "One" ? "1" : StopBits == "Two" ? "2" : "1.5")} · 地址 {SlaveAddress}";
}

/// <summary>设备与位置标识（DeviceId 稳定，不随显示名/串口/电脑名变化）。</summary>
public sealed class DeviceIdentity
{
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "未命名设备";
    public string Location { get; set; } = "";
    public string ProtocolId { get; init; } = "ecm30";
    public string ProtocolVersion { get; init; } = "ECM-30 Modbus RTU";
    /// <summary>实物验证状态：如实显示，不冒充已验证。</summary>
    public string VerificationStatus { get; init; } = "未经实物验证（依据既有实现与协议文献重建）";
}

/// <summary>
/// 悬浮窗显示设置。透明背景与文字透明度分开；位置以工作区定位并做可见性纠正。
/// </summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 0.0;   // 默认全透明背景
    public double TextOpacity { get; set; } = 1.0;        // 默认不透明数字
    public string TemperatureColor { get; set; } = "#E53935";
    public string HumidityColor { get; set; } = "#1E88E5";
    public string TextColor { get; set; } = "#FFFFFF";
    public double FontSize { get; set; } = 32;            // 16～96
    public string Corner { get; set; } = "BottomRight";   // 四角
    public string DisplayDeviceName { get; set; } = "";   // 空 = 主显示器
    public double MarginX { get; set; } = 24;
    public double MarginY { get; set; } = 24;
    public bool ClickThrough { get; set; } = true;        // 默认鼠标穿透
    public bool ShowDeviceLabel { get; set; } = true;
    public bool ShowStatusText { get; set; } = true;
    public bool ShowHumidity { get; set; } = true;
    public bool ShowTemperature { get; set; } = true;
    public bool ShadowEnabled { get; set; } = true;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();
}

/// <summary>应用级配置（可持久化）。</summary>
public sealed class AppConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    /// <summary>每个实物传感器各自维护名称、地址、位置和阈值。旧版单设备配置会迁移到此列表。</summary>
    public List<SensorConfiguration> Sensors { get; set; } = new();
    public string SelectedSensorId { get; set; } = "";
    // 保留旧字段用于兼容 0.4 配置及首次向导；始终与当前选中的传感器同步。
    public SerialSettings Serial { get; set; } = new();
    public DeviceIdentity Device { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public ThresholdSnapshot Thresholds { get; set; } = new() { VersionId = 1 };
    /// <summary>保存方式：sqlite（本机，推荐）或 sqlserver（同时同步）。</summary>
    public string StorageMode { get; set; } = "sqlite";
    public string SqliteFilePath { get; set; } = "";
    public string SqlServerConnection { get; set; } = "";
    public bool AutoStartAcquisition { get; set; } = false;  // 默认关闭
    public bool StartWithWindows { get; set; } = false;      // 默认关闭
    public bool WizardCompleted { get; set; } = false;
    public bool CloseToTrayHintShown { get; set; } = false;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigurationReadError { get; set; } = "";
}

public sealed class SensorConfiguration
{
    public DeviceIdentity Device { get; set; } = new();
    public SerialSettings Serial { get; set; } = new();
    public ThresholdSnapshot Thresholds { get; set; } = new() { VersionId = ThresholdVersions.Next() };
    public bool Enabled { get; set; } = true;
    public string SelectionLabel => $"{Device.DisplayName} · {Serial.PortName} / 地址 {Serial.SlaveAddress}" + (Enabled ? "" : "（停用）");

    public SensorConfiguration Clone() => new()
    {
        Device = new DeviceIdentity
        {
            DeviceId = Device.DeviceId, DisplayName = Device.DisplayName, Location = Device.Location,
            ProtocolId = Device.ProtocolId, ProtocolVersion = Device.ProtocolVersion,
            VerificationStatus = Device.VerificationStatus
        },
        Serial = Serial.Clone(), Thresholds = Thresholds, Enabled = Enabled
    };
}

/// <summary>阈值表使用全局编号；各传感器修改阈值时必须产生不同编号。</summary>
public static class ThresholdVersions
{
    private static long _last = DateTime.UtcNow.Ticks;
    public static long Next()
    {
        while (true)
        {
            long old = Interlocked.Read(ref _last);
            long next = Math.Max(DateTime.UtcNow.Ticks, old + 1);
            if (Interlocked.CompareExchange(ref _last, next, old) == old) return next;
        }
    }

    public static ThresholdSnapshot Copy(ThresholdSnapshot value, long version) => new()
    {
        VersionId = version, TemperatureLow = value.TemperatureLow, TemperatureHigh = value.TemperatureHigh,
        HumidityLow = value.HumidityLow, HumidityHigh = value.HumidityHigh,
        EffectiveFromUtc = value.EffectiveFromUtc, Source = value.Source
    };
}
