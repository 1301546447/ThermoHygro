using Microsoft.Data.Sqlite;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Protocols;
using ThermoHygro.Reporting;

namespace ThermoHygro.Tests;

public static class MultiSensorVerification
{
    public static async Task RunAsync(string root, Action<string, bool, string> check)
    {
        var legacy = new AppConfiguration
        {
            SchemaVersion = 1, Device = new DeviceIdentity { DeviceId = "legacy-device", DisplayName = "原传感器" },
            Serial = new SerialSettings { PortName = "COM20", SlaveAddress = 7 },
            Thresholds = new ThresholdSnapshot { VersionId = 17, TemperatureHigh = 30 }
        };
        var file = Path.Combine(root, "legacy-config.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(legacy));
        var migrated = ConfigStore.Load(file);
        check("旧单传感器配置自动迁移，编号及地址不变", migrated.Sensors.Count == 1 && migrated.SelectedSensorId == "legacy-device" && migrated.Sensors[0].Serial.SlaveAddress == 7 && migrated.Thresholds.VersionId == 17, "保留原历史归属");
        check("迁移后保存及重载保留同一编号", ConfigStore.Save(migrated, file).Length == 0 && ConfigStore.Load(file).Device.DeviceId == "legacy-device", "");

        var first = Sensor("冷藏区", "COM20", 1, 8);
        var second = Sensor("室内", "com20", 2, 30);
        var third = Sensor("另一串口", "COM21", 1, 30);
        var list = new List<SensorConfiguration> { first, second, third };
        check("同串口多地址及不同串口相同地址合法", SensorConfigurationRules.Validate(list) == "", "");
        var duplicate = list.Select(sensor => sensor.Clone()).ToList(); duplicate[1].Serial.SlaveAddress = 1;
        check("同串口重复地址被拒绝", SensorConfigurationRules.Validate(duplicate).Contains("重复"), "");
        duplicate[1].Serial.SlaveAddress = 2; duplicate[1].Serial.BaudRate = 19200;
        check("同串口不同线路参数被拒绝", SensorConfigurationRules.Validate(duplicate).Contains("必须一致"), "");
        duplicate[1].Enabled = false;
        check("停用的传感器不争用总线", SensorConfigurationRules.Validate(duplicate) == "", "");
        check("非法数值阈值被拒绝", SensorConfigurationRules.ValidateThresholds(new() { TemperatureHigh = double.NaN }).Length > 0, "");

        var cfg = new AppConfiguration { Sensors = list, SelectedSensorId = first.Device.DeviceId };
        ConfigStore.NormalizeSensors(cfg);
        cfg.SqlServerConnection = "Server=(localdb)\\MSSQLLocalDB;Database=example;User ID=test;Password=password-test-only-456;TrustServerCertificate=True";
        var configPath = Path.Combine(root, "multi-config.json");
        check("多设备配置可保存", ConfigStore.Save(cfg, configPath).Length == 0, "");
        var loaded = ConfigStore.Load(configPath);
        check("多设备配置往返保留独立编号和地址", loaded.Sensors.Count == 3 && loaded.Sensors.Select(sensor => sensor.Device.DeviceId).SequenceEqual(list.Select(sensor => sensor.Device.DeviceId)) && loaded.Sensors[1].Serial.SlaveAddress == 2, "");
        check("数据库密码不写入配置明文", !File.ReadAllText(configPath).Contains("password-test-only-456") && cfg.SqlServerConnection.Contains("password-test-only-456"), "当前进程保留，落盘剔除密码");
        second.Thresholds = first.Thresholds;
        ConfigStore.NormalizeSensors(cfg);
        check("各传感器不会混用阈值版本编号", first.Thresholds.VersionId != second.Thresholds.VersionId && second.Thresholds.TemperatureHigh == first.Thresholds.TemperatureHigh, "");
        second.Thresholds = new ThresholdSnapshot { VersionId = ThresholdVersions.Next(), TemperatureHigh = 30 };

        var path = Path.Combine(root, "multi-sensor.db");
        using var store = new SqliteLocalStore(path);
        await store.InitializeAsync();
        var tracker = new RequestTracker();
        var transports = new List<SimulatedBus>();
        using var service = new MultiSensorAcquisitionService(port =>
        {
            var bus = new SimulatedBus(port, tracker) { FailedAddress = port.Equals("COM20", StringComparison.OrdinalIgnoreCase) ? (byte)2 : null };
            transports.Add(bus); return bus;
        });
        service.AttachStore(store);
        service.SelectedSensorId = first.Device.DeviceId;
        check("多传感器采集启动", service.Start(list) == "", "");
        await WaitUntil(() => list.All(sensor => service.SnapshotFor(sensor.Device.DeviceId).Sequence >= 2), 7000);
        check("同一串口仅有一个物理拥有者", transports.Count == 2 && transports.All(bus => bus.OpenCount == 1), $"物理端口 {transports.Count}，打开次数 {string.Join('/', transports.Select(bus => bus.OpenCount))}");
        check("同串口收发不会交错，不同串口可并行", transports.All(bus => bus.MaxConcurrent == 1) && tracker.MaxConcurrent >= 2, $"总并行峰值 {tracker.MaxConcurrent}");
        var blocked = false;
        try { await service.TestReadAsync(first.Serial, CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
        check("运行期间测试读取被拒绝，避免争用", blocked, "");
        service.Pause();
        var requestCount = transports.Sum(bus => bus.RequestCount);
        await Task.Delay(250);
        check("暂停全部采集后任务停止并释放全部端口", !service.IsRunning && transports.All(bus => !bus.IsOpen) && transports.Sum(bus => bus.RequestCount) == requestCount, "");

        var cold = await store.QueryRecentMinutesForDeviceAsync(60, first.Device.DeviceId);
        var room = await store.QueryRecentMinutesForDeviceAsync(60, second.Device.DeviceId);
        var other = await store.QueryRecentMinutesForDeviceAsync(60, third.Device.DeviceId);
        check("各传感器趋势互不混合", cold.Count > 0 && cold.All(point => point.TemperatureAvg == 5) && other.Count > 0 && other.All(point => point.TemperatureAvg == 25), "同地址不同串口分别为 5℃ 与 25℃");
        check("整轮失败仍保存缺测分钟，不伪造零值", room.Count > 0 && room.All(point => point.TemperatureAvg is null && point.HumidityAvg is null && point.TempValidCount == 0), $"缺测分钟 {room.Count}");

        var missing = await new SqliteHistoryQuery(path).TakeSnapshotAsync(Query(second.Device.DeviceId));
        check("缺测报表保留尝试次数和无效值", missing.Stats.AttemptCount >= 2 && missing.Points.All(point => point.TemperatureAvg is null && point.HumidityAvg is null), "");
        var initialVersion = first.Thresholds.VersionId;
        first.Device.DisplayName = "冷库改名";
        first.Serial.SlaveAddress = 3;
        first.Thresholds = new ThresholdSnapshot { VersionId = ThresholdVersions.Next(), TemperatureHigh = 10, EffectiveFromUtc = DateTimeOffset.UtcNow };
        var priorSamples = await store.CountSamplesAsync();
        check("改名改地址及阈值后重新启动", service.Start(list) == "", "固定编号不变");
        await WaitUntil(() => service.SnapshotFor(first.Device.DeviceId).Sequence >= 2, 7000);
        service.Pause();
        var directory = await store.QueryDevicesAsync();
        check("改名后原编号仍在同一个设备目录中", directory.Count(device => device.DeviceId == first.Device.DeviceId) == 1 && directory.Single(device => device.DeviceId == first.Device.DeviceId).DisplayName == "冷库改名", "");
        var report = await new SqliteHistoryQuery(path).TakeSnapshotAsync(Query(first.Device.DeviceId));
        check("改地址后历史连续，包含原值与新值", report.DeviceDisplayName == "冷库改名" && report.Stats.TemperatureMin == 5 && report.Stats.TemperatureMax == 15 && await store.CountSamplesAsync() > priorSamples, "5℃ 与 15℃ 归属同一编号");
        check("64 位阈值事件可完整查询", report.Events.Any(ev => ev.ThresholdVersionId == first.Thresholds.VersionId), $"版本 {first.Thresholds.VersionId}");
        check("报表阈值说明来自历史配置", report.ThresholdNote.Contains("2 个已配置"), report.ThresholdNote);
        await using (var connection = new SqliteConnection(store.ConnectionString))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TemperatureHigh FROM ThresholdVersion WHERE VersionId=$v";
            cmd.Parameters.AddWithValue("$v", initialVersion);
            check("新上下限不改写原历史阈值", Convert.ToDouble(await cmd.ExecuteScalarAsync()) == 8, "原上限仍为 8℃");
        }
        list.Remove(second);
        service.ConfigureIdle(list);
        check("移除传感器后原历史仍可查询", (await new SqliteHistoryQuery(path).TakeSnapshotAsync(Query(second.Device.DeviceId))).Points.Count > 0 && (await store.QueryDevicesAsync()).Any(device => device.DeviceId == second.Device.DeviceId), "");

        var sample = await service.TestReadAsync(first.Serial, CancellationToken.None);
        check("暂停后可单独测试当前地址", sample.Temperature.Value == 15 && !transports.Last().IsOpen, "测试完成释放端口");

        var pdf = Path.Combine(root, "多传感器_冷库报表.pdf");
        var exported = await new PdfReporter().ExportAsync(report, pdf);
        check("真实 SQLite 历史可导出中文多设备报表", exported.Success && File.Exists(pdf) && new FileInfo(pdf).Length > 5000, pdf);
        var missingPdf = Path.Combine(root, "多传感器_缺测报表.pdf");
        check("整轮缺测报表可导出", (await new PdfReporter().ExportAsync(missing, missingPdf)).Success, missingPdf);
        check("中文报表字体随程序集内置", new PdfReporter().FontStatus.Contains("随程序"), "不依赖现场机器安装中文字体");
        foreach (var sensor in list) sensor.Enabled = false;
        service.Start(list);
        check("停用全部传感器后停止采集", !service.IsRunning, "");
    }

    public static SensorConfiguration Sensor(string name, string port, int address, double high) => new()
    {
        Device = new DeviceIdentity { DisplayName = name, Location = "自动验证（模拟传感器）" },
        Serial = new SerialSettings { PortName = port, SlaveAddress = address, ReadTimeoutMs = 200, PollIntervalSeconds = 1 },
        Thresholds = new ThresholdSnapshot { VersionId = ThresholdVersions.Next(), TemperatureHigh = high, EffectiveFromUtc = DateTimeOffset.UtcNow }
    };
    public static ReportQuery Query(string device) => new()
    {
        DeviceId = device, StartLocal = DateTime.Today, EndLocalExclusive = DateTime.Today.AddDays(1),
        CutoffUtc = DateTimeOffset.UtcNow.AddSeconds(1), Granularity = ReportGranularity.Minute
    };
    private static async Task WaitUntil(Func<bool> condition, int timeout)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(50);
        if (!condition()) throw new TimeoutException("多传感器验证等待超时。");
    }

    public sealed class RequestTracker
    {
        private int _active, _max;
        public int MaxConcurrent => _max;
        public void Enter() { int active = Interlocked.Increment(ref _active); SetMax(ref _max, active); }
        public void Leave() => Interlocked.Decrement(ref _active);
        internal static void SetMax(ref int field, int value) { int current; do { current = field; if (current >= value) return; } while (Interlocked.CompareExchange(ref field, value, current) != current); }
    }
    public sealed class SimulatedBus(string port, RequestTracker tracker) : ISerialTransport
    {
        private int _active, _max;
        public byte? FailedAddress { get; set; }
        public int OpenCount { get; private set; }
        public int RequestCount { get; private set; }
        public int MaxConcurrent => _max;
        public bool IsOpen { get; private set; }
        public string PortName => port;
        public string Open(string portName, SerialSettings settings)
        {
            if (IsOpen) return "模拟总线被重复打开";
            OpenCount++; IsOpen = true; return "";
        }
        public void Close() => IsOpen = false;
        public void PurgeInput() { }
        public async Task<TransportResult> RequestAsync(byte[] request, int length, byte address, int timeout, CancellationToken ct)
        {
            RequestCount++;
            int concurrent = Interlocked.Increment(ref _active); RequestTracker.SetMax(ref _max, concurrent); tracker.Enter();
            try
            {
                await Task.Delay(35, ct);
                if (FailedAddress == address) return new() { ErrorKind = "Timeout", Error = "模拟地址无回复" };
                int raw = request[3] == 5 ? 500 : (port.Equals("COM21", StringComparison.OrdinalIgnoreCase) ? 250 : address == 1 ? 50 : address == 3 ? 150 : 250);
                var frame = new byte[] { address, 3, 2, (byte)(raw >> 8), (byte)raw, 0, 0 };
                var crc = ModbusCrc.Compute(frame.AsSpan(0, 5));
                frame[5] = (byte)crc; frame[6] = (byte)(crc >> 8);
                return new() { Success = true, Frame = frame };
            }
            finally { tracker.Leave(); Interlocked.Decrement(ref _active); }
        }
        public void Dispose() => Close();
    }
}
