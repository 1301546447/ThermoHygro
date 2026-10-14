namespace ThermoHygro.Core;

/// <summary>串口名大小写不影响同一总线的判断；同一总线地址唯一，线路参数必须一致。</summary>
public static class SensorConfigurationRules
{
    public static string ValidateSerial(SerialSettings serial)
    {
        if (string.IsNullOrWhiteSpace(serial.PortName)) return "请填写串口名称（例如 COM12）。";
        if (serial.BaudRate <= 0) return "波特率必须大于 0。";
        if (serial.DataBits is < 5 or > 8) return "数据位必须在 5～8 之间。";
        if (serial.Parity is not ("None" or "Even" or "Odd" or "Mark" or "Space")) return "校验位设置无效。";
        if (serial.StopBits is not ("One" or "Two" or "OnePointFive")) return "停止位设置无效。";
        if (serial.SlaveAddress is < 1 or > 247) return "传感器地址必须在 1～247 之间。";
        if (serial.ReadTimeoutMs is < 200 or > 30000) return "读取超时必须在 200～30000 毫秒之间。";
        if (serial.PollIntervalSeconds is < 1 or > 60) return "采集间隔必须在 1～60 秒之间。";
        return "";
    }

    public static bool SameLine(SerialSettings a, SerialSettings b) =>
        string.Equals(a.PortName.Trim(), b.PortName.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.BaudRate == b.BaudRate && a.DataBits == b.DataBits
        && a.Parity == b.Parity && a.StopBits == b.StopBits;

    public static string ValidateThresholds(ThresholdSnapshot th)
    {
        var values = new[] { th.TemperatureLow, th.TemperatureHigh, th.HumidityLow, th.HumidityHigh };
        if (values.Any(v => v.HasValue && !double.IsFinite(v.Value))) return "上下限必须为有效数字。";
        if (th.TemperatureLow >= th.TemperatureHigh) return "温度下限必须小于上限。";
        if (th.HumidityLow >= th.HumidityHigh) return "湿度下限必须小于上限。";
        if (th.HumidityLow is < 0 or > 100 || th.HumidityHigh is < 0 or > 100) return "湿度上下限应在 0～100 %RH。";
        return "";
    }

    public static string Validate(IReadOnlyList<SensorConfiguration> sensors)
    {
        if (sensors.Count == 0) return "请至少保留一个传感器。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in sensors)
        {
            if (string.IsNullOrWhiteSpace(sensor.Device.DeviceId) || !ids.Add(sensor.Device.DeviceId)) return "传感器编号为空或重复。";
            var name = sensor.Device.DisplayName.Trim();
            if (name.Length == 0 || name.Length > 80) return "传感器名称应为 1～80 个字符。";
            if (!names.Add(name)) return $"传感器名称重复：{name}。请用不同名称区分位置。";
            if (sensor.Device.ProtocolId != "ecm30") return $"{name}：当前仅支持 ECM-30。";
            string error = ValidateThresholds(sensor.Thresholds);
            if (error.Length > 0) return $"{name}：{error}";
            // 未启用的草稿允许暂不填写串口，启用后再做完整线路校验。
            if (!sensor.Enabled) continue;
            error = ValidateSerial(sensor.Serial);
            if (error.Length > 0) return $"{name}：{error}";
        }
        foreach (var bus in sensors.Where(s => s.Enabled).GroupBy(s => s.Serial.PortName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var first = bus.First();
            if (bus.Any(s => !SameLine(first.Serial, s.Serial))) return $"串口 {bus.Key} 上的波特率、数据位、校验位和停止位必须一致。";
            if (bus.GroupBy(s => s.Serial.SlaveAddress).Any(group => group.Count() > 1)) return $"串口 {bus.Key} 存在重复的传感器地址。请先在实物上设置不同地址。";
        }
        return "";
    }
}
