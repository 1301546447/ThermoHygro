using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Protocols;

namespace ThermoHygro.App;

/// <summary>开发验证入口：隔离数据目录、模拟通信、实际 WPF 窗口与按钮，不接触现场配置或串口。</summary>
public static class DesktopSmokeCheck
{
    public static int Run(string directory)
    {
        var lines = new List<string>();
        int failures = 0;
        void Check(string name, bool ok)
        {
            if (!ok) failures++;
            var line = $"[{(ok ? "PASS" : "FAIL")}] {name}";
            lines.Add(line); Console.WriteLine(line);
        }
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("THERMOHYGRO_HOME", directory);
        MainWindow? main = null;
        try
        {
            var cfg = ConfigStore.CreateDefault();
            var a = Sensor("模拟冷藏区", 1, 2, 8);
            var b = Sensor("模拟室内", 2, 20, 30);
            cfg.Sensors = new() { a, b }; cfg.SelectedSensorId = a.Device.DeviceId;
            cfg.SqliteFilePath = Path.Combine(directory, "history.db");
            cfg.WizardCompleted = true; cfg.Overlay.Enabled = false;
            ConfigStore.NormalizeSensors(cfg);
            using var service = new MultiSensorAcquisitionService(_ => new SmokeTransport());
            main = new MainWindow(cfg, service)
            {
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 1150, Height = 920
            };
            main.Show();
            Wait(() => main.IsLoaded);
            main.StartAcquisition();
            Wait(() => cfg.Sensors.All(sensor => service.SnapshotFor(sensor.Device.DeviceId).Sequence >= 2));
            Pump();
            Check("实际窗口可载入并显示两个传感器", main.SensorOverviewGrid.Items.Count == 2 && main.TempCardValue.Text.Contains("4.0"));
            Render(main, Path.Combine(directory, "实时监测_模拟.png"));

            main.SensorCombo.SelectedValue = b.Device.DeviceId; Pump();
            Check("切换当前传感器后读数、地址和维护字段一致", cfg.SelectedSensorId == b.Device.DeviceId && main.TempCardValue.Text.Contains("25.0") && main.SlaveAddrBox.Text == "2" && main.DeviceNameBox.Text == "模拟室内");
            Check("切换显示对象不停止其他传感器", service.IsRunning && service.SnapshotFor(a.Device.DeviceId).Sequence >= 2);

            main.SensorCombo.SelectedValue = a.Device.DeviceId; Pump();
            main.DeviceNameBox.Text = "模拟冷库改名"; Invoke(main, "SaveDevice_Click");
            Wait(() => service.SnapshotFor(a.Device.DeviceId).Sequence >= 1); Pump();
            Check("实际保存按钮保持原固定编号", cfg.Device.DeviceId == a.Device.DeviceId && cfg.Device.DisplayName == "模拟冷库改名" && ConfigStore.Load().Device.DisplayName == "模拟冷库改名");
            main.SensorTempLow.Text = "2"; main.SensorTempHigh.Text = "8";
            Invoke(main, "SaveSensorThresholds_Click");
            Wait(() => service.SnapshotFor(a.Device.DeviceId).Sequence >= 1);
            Check("维护界面可保存独立上下限", cfg.Thresholds.TemperatureLow == 2 && cfg.Thresholds.TemperatureHigh == 8 && cfg.Sensors.Single(sensor => sensor.Device.DeviceId == b.Device.DeviceId).Thresholds.TemperatureHigh == 30);

            main.HistorySensorCombo.SelectedValue = b.Device.DeviceId; main.HistoryScopeCombo.SelectedIndex = 2;
            Invoke(main, "QueryHistory_Click"); Wait(() => !Busy(main));
            var snapshot = Field(main, "_snapshot") as ReportSnapshot;
            Check("实际历史查询按钮无跨线程错误且按传感器隔离", snapshot?.DeviceDisplayName == "模拟室内" && snapshot.Stats.TemperatureAvg == 25 && main.HistoryGrid.Items.Count > 0);
            main.SelectTab(1); Pump(); Render(main, Path.Combine(directory, "历史报表_模拟.png"));
            Invoke(main, "ExportCsv_Click"); Wait(() => !Busy(main));
            Check("实际 CSV 导出按钮可完成", Directory.GetFiles(Path.Combine(directory, "Exports"), "*.csv").Length == 2 && main.HistorySummary.Text.Contains("已导出"));
            Invoke(main, "ExportPdf_Click"); Wait(() => !Busy(main));
            Check("实际 PDF 导出按钮可完成", Directory.GetFiles(Path.Combine(directory, "Exports"), "*.pdf").Length == 1 && main.HistorySummary.Text.Contains("已导出"));

            main.SelectTab(2); Pump(); Render(main, Path.Combine(directory, "传感器维护_模拟.png"));
            Invoke(main, "AddSensor_Click"); Pump();
            var addedId = cfg.SelectedSensorId;
            Check("增加按钮建立停用草稿并分配空闲地址", cfg.Sensors.Count == 3 && !ConfigStore.SelectedSensor(cfg).Enabled && cfg.Serial.SlaveAddress == 3);
            main.DeviceNameBox.Text = "模拟新增点"; main.SensorEnabledCheck.IsChecked = true;
            Invoke(main, "SaveDevice_Click"); Wait(() => service.SnapshotFor(addedId).Sequence >= 1);
            Check("新传感器启用后可独立记录", cfg.Sensors.Single(sensor => sensor.Device.DeviceId == addedId).Enabled && service.SnapshotFor(addedId).Storage == StorageState.Ok);
            Invoke(main, "RemoveSensor_Click"); Pump();
            Check("移除按钮保留历史设备查询入口", cfg.Sensors.Count == 2 && main.HistorySensorCombo.Items.Cast<DeviceIdentity>().Any(device => device.DeviceId == addedId && device.DisplayName.Contains("历史设备")));

            // 重复地址必须在界面保存时被拦截，且原采集配置保持可用。
            main.SlaveAddrBox.Text = "2"; Invoke(main, "SaveSerial_Click");
            Check("界面拒绝重复地址且不改原配置", main.SerialFeedback.Text.Contains("重复") && cfg.Serial.SlaveAddress == 1 && service.IsRunning);
            main.SlaveAddrBox.Text = "abc"; Invoke(main, "SaveSerial_Click");
            Check("非法地址文本不会悄悄变成地址 1", main.SerialFeedback.Text.Contains("整数") && cfg.Serial.SlaveAddress == 1);
            main.SlaveAddrBox.Text = "1";

            main.Close(); Pump();
            Check("关闭主窗口后采集继续，托盘模式可重新显示", !main.IsVisible && service.IsRunning);
            main.Show(); Pump(); Check("隐藏后窗口可再次打开", main.IsVisible);
            main.PauseAcquisition(); Pump();
            Check("暂停后实时读数不继续冒充当前值", !service.IsRunning && main.TempCardValue.Text.Contains("--"));
            Check("退出释放采集和本地库", main.ShutdownForExit().Length == 0 && !service.IsRunning);
            main.Close();
        }
        catch (Exception ex)
        {
            Check("桌面验证异常：" + ex, false);
            try { main?.ShutdownForExit(); main?.Close(); } catch { }
        }
        lines.Add($"=== 桌面结果：{lines.Count - failures} 通过，{failures} 失败 ===");
        File.WriteAllLines(Path.Combine(directory, "ui-check.txt"), lines);
        return failures == 0 ? 0 : 1;
    }

    private static SensorConfiguration Sensor(string name, int address, double low, double high) => new()
    {
        Device = new DeviceIdentity { DisplayName = name, Location = "开发验证用模拟数据" },
        Serial = new SerialSettings { PortName = "COM20", SlaveAddress = address, PollIntervalSeconds = 1, ReadTimeoutMs = 200 },
        Thresholds = new ThresholdSnapshot { VersionId = ThresholdVersions.Next(), TemperatureLow = low, TemperatureHigh = high }
    };
    private static object? Field(MainWindow main, string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(main);
    private static bool Busy(MainWindow main) => (bool)(Field(main, "_busy") ?? false);
    private static void Invoke(MainWindow main, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, new object[] { main, new RoutedEventArgs() });
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        if (!condition()) throw new TimeoutException("桌面验证等待超时。");
        Pump();
    }
    private static void Render(MainWindow main, string path)
    {
        main.UpdateLayout();
        var content = (FrameworkElement)main.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class SmokeTransport : ISerialTransport
    {
        public bool IsOpen { get; private set; }
        public string PortName => "COM20";
        public string Open(string name, SerialSettings settings) { IsOpen = true; return ""; }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
        public void PurgeInput() { }
        public async Task<TransportResult> RequestAsync(byte[] request, int expectedLength, byte address, int timeoutMs, CancellationToken ct)
        {
            await Task.Delay(20, ct);
            int raw = request[3] == 5 ? 500 : address == 1 ? 40 : 250;
            var frame = new byte[] { address, 3, 2, (byte)(raw >> 8), (byte)raw, 0, 0 };
            ushort crc = ModbusCrc.Compute(frame.AsSpan(0, 5)); frame[5] = (byte)crc; frame[6] = (byte)(crc >> 8);
            return new() { Success = true, Frame = frame };
        }
    }
}
