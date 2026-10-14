using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThermoHygro.Core;
using Microsoft.Data.SqlClient;

namespace ThermoHygro.Infrastructure;

/// <summary>
/// 配置持久化。默认存于 %LocalAppData%\ThermoHygro\config.json。
/// 保存失败必须明确返回错误，不能静默丢弃用户设置。
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string DefaultDirectory
    {
        get
        {
            var specified = Environment.GetEnvironmentVariable("THERMOHYGRO_HOME");
            return string.IsNullOrWhiteSpace(specified)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermoHygro")
                : Path.GetFullPath(specified);
        }
    }

    public static string DefaultConfigPath => Path.Combine(DefaultDirectory, "config.json");

    public static string DefaultDatabasePath => Path.Combine(DefaultDirectory, "Data", "history.db");

    public static AppConfiguration Load(string? path = null)
    {
        var p = path ?? DefaultConfigPath;
        try
        {
            if (!File.Exists(p)) return CreateDefault();
            var json = File.ReadAllText(p);
            var cfg = JsonSerializer.Deserialize<AppConfiguration>(json, Opts);
            if (cfg is null) return CreateDefault();
            cfg.Serial ??= new SerialSettings();
            cfg.Device ??= new DeviceIdentity();
            cfg.Overlay ??= new OverlaySettings();
            cfg.Thresholds ??= new ThresholdSnapshot { VersionId = 1 };
            if (string.IsNullOrWhiteSpace(cfg.SqliteFilePath)) cfg.SqliteFilePath = DefaultDatabasePath;
            NormalizeSensors(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            // 配置损坏：不改写用户文件，返回默认值让用户可以重新配置
            var cfg = CreateDefault();
            cfg.ConfigurationReadError = $"无法读取原配置，原文件已保留：{p}。请先修复或备份后移开原配置文件，再重新配置。原因：{ex.Message}";
            return cfg;
        }
    }

    public static AppConfiguration CreateDefault()
    {
        var cfg = new AppConfiguration { SqliteFilePath = DefaultDatabasePath };
        NormalizeSensors(cfg);
        return cfg;
    }

    public static void NormalizeSensors(AppConfiguration cfg)
    {
        if (cfg.SchemaVersion > 2) throw new InvalidDataException("配置来自更高版本，请使用对应版本打开，原配置不能降级覆盖。");
        cfg.Sensors ??= new();
        if (cfg.Sensors.Count == 0)
            cfg.Sensors.Add(new SensorConfiguration { Device = cfg.Device, Serial = cfg.Serial, Thresholds = cfg.Thresholds });
        var versions = new HashSet<long>();
        foreach (var sensor in cfg.Sensors)
        {
            sensor.Device ??= new(); sensor.Serial ??= new(); sensor.Thresholds ??= new() { VersionId = ThresholdVersions.Next() };
            if (!versions.Add(sensor.Thresholds.VersionId))
                sensor.Thresholds = ThresholdVersions.Copy(sensor.Thresholds, ThresholdVersions.Next());
        }
        cfg.SchemaVersion = 2;
        SelectSensor(cfg, cfg.SelectedSensorId);
    }

    public static SensorConfiguration SelectedSensor(AppConfiguration cfg) =>
        cfg.Sensors.First(s => s.Device.DeviceId == cfg.SelectedSensorId);

    public static void SelectSensor(AppConfiguration cfg, string deviceId)
    {
        var sensor = cfg.Sensors.FirstOrDefault(s => s.Device.DeviceId == deviceId) ?? cfg.Sensors.First();
        cfg.SelectedSensorId = sensor.Device.DeviceId;
        cfg.Device = sensor.Device; cfg.Serial = sensor.Serial; cfg.Thresholds = sensor.Thresholds;
    }

    public static void CommitSelectedSensor(AppConfiguration cfg)
    {
        var sensor = cfg.Sensors.FirstOrDefault(s => s.Device.DeviceId == cfg.SelectedSensorId);
        if (sensor is null) return;
        sensor.Device = cfg.Device; sensor.Serial = cfg.Serial; sensor.Thresholds = cfg.Thresholds;
        cfg.SelectedSensorId = sensor.Device.DeviceId;
    }

    /// <summary>保存配置。返回空字符串表示成功，否则为失败原因。</summary>
    public static string Save(AppConfiguration cfg, string? path = null)
    {
        if (cfg.ConfigurationReadError.Length > 0) return cfg.ConfigurationReadError;
        var p = path ?? DefaultConfigPath;
        try
        {
            CommitSelectedSensor(cfg);
            NormalizeSensors(cfg);
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = p + ".tmp";
            // SQL 身份密码只保留在当前进程；不能把输入框里的密码写入公开工程或本地明文配置。
            var saved = JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(cfg, Opts), Opts)!;
            if (!string.IsNullOrWhiteSpace(saved.SqlServerConnection))
            {
                var connection = new SqlConnectionStringBuilder(saved.SqlServerConnection);
                connection.Remove("Password");
                saved.SqlServerConnection = connection.ConnectionString;
            }
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Opts));
            // 先写临时文件再替换，避免写一半损坏原配置
            if (File.Exists(p)) File.Replace(tmp, p, null);
            else File.Move(tmp, p);
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return $"配置目录无写权限，设置未保存：{p}";
        }
        catch (IOException ex)
        {
            return $"配置保存失败（可能磁盘已满）：{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"配置保存失败：{ex.GetType().Name}：{ex.Message}";
        }
    }

    /// <summary>只保存悬浮窗设置（悬浮窗改设置时用，避免覆盖其它配置）。</summary>
    public static string SaveOverlay(OverlaySettings overlay, string? path = null)
    {
        var cfg = Load(path);
        cfg.Overlay = overlay;
        return Save(cfg, path);
    }
}
