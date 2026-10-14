using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;

namespace ThermoHygro.App;

public static partial class SerialCheck
{
    private static int RunLateRound(string portA, string portB)
    {
        var output = new StringBuilder("实际采集轮次的迟到回复隔离验证，对端为模拟器。\n");
        int passed = 0, failed = 0;
        void Check(string name, bool ok)
        {
            if (ok) passed++; else failed++;
            output.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
        }
        Process? mock = null;
        try
        {
            var simulator = ResolveMockSensorPath();
            if (simulator.Length == 0) throw new FileNotFoundException("请先构建模拟器。");
            mock = Process.Start(new ProcessStartInfo(simulator, $"{portB} late 30 1") { UseShellExecute = false, CreateNoWindow = true });
            Thread.Sleep(1200);
            using var transport = new SerialTransport();
            using var service = new AcquisitionService(transport, new SystemClock());
            var group = service.TestReadAsync(new SerialSettings { PortName = portA, SlaveAddress = 1, ReadTimeoutMs = 3000 }, CancellationToken.None).GetAwaiter().GetResult();
            Check("温度延迟4秒时记录超时缺测", group.Temperature.Quality == MetricQuality.Failed && group.Temperature.Value is null);
            Check("后续湿度读取50%，不把23.5℃迟到温度当作湿度", group.Humidity.Quality == MetricQuality.Good && group.Humidity.Value == 50);
            Check("单轮保持两个指标各自质量", group.AnySuccess && group.Temperature.Value is null);
        }
        catch (Exception ex) { Check("验证异常：" + ex.Message, false); }
        finally
        {
            try { if (mock is { HasExited: false }) { mock.Kill(true); mock.WaitForExit(3000); } mock?.Dispose(); } catch { }
        }
        Finish(output, passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static int RunMulti(string portA, string portB)
    {
        var output = new StringBuilder("同串口多地址验证；对端为模拟传感器，属于虚拟串口证据。\n");
        int passed = 0, failed = 0;
        void Check(string name, bool ok)
        {
            if (ok) passed++; else failed++;
            string line = $"[{(ok ? "PASS" : "FAIL")}] {name}";
            output.AppendLine(line); Console.WriteLine(line);
        }
        string directory = Path.Combine(Path.GetTempPath(), "ThermoHygroSerialMulti-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? mock = null;
        try
        {
            var simulator = ResolveMockSensorPath();
            if (simulator.Length == 0) throw new FileNotFoundException("请先构建 tools/MockSensor。");
            mock = Process.Start(new ProcessStartInfo(simulator, $"{portB} multi 30 1,2") { UseShellExecute = false, CreateNoWindow = true });
            Thread.Sleep(1200);
            using var store = new SqliteLocalStore(Path.Combine(directory, "history.db"));
            store.InitializeAsync().GetAwaiter().GetResult();
            SensorConfiguration Sensor(string name, int address) => new()
            {
                Device = new DeviceIdentity { DisplayName = name, Location = "虚拟串口模拟" },
                Serial = new SerialSettings { PortName = portA, SlaveAddress = address, PollIntervalSeconds = 1, ReadTimeoutMs = 1000 }
            };
            var a = Sensor("模拟地址1", 1); var b = Sensor("模拟地址2", 2);
            using var service = new MultiSensorAcquisitionService(); service.AttachStore(store);
            Check("同一个真实虚拟串口可启动两个地址", service.Start(new[] { a, b }).Length == 0);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && (service.SnapshotFor(a.Device.DeviceId).Sequence < 2 || service.SnapshotFor(b.Device.DeviceId).Sequence < 2)) Thread.Sleep(100);
            Check("地址1独立读取4℃", service.SnapshotFor(a.Device.DeviceId).Reading.Temperature.Value == 4);
            Check("地址2独立读取25℃", service.SnapshotFor(b.Device.DeviceId).Reading.Temperature.Value == 25);
            Check("两个地址分别读取50%湿度", service.SnapshotFor(a.Device.DeviceId).Reading.Humidity.Value == 50 && service.SnapshotFor(b.Device.DeviceId).Reading.Humidity.Value == 50);
            service.Pause();
            Check("两个地址样本均已真实入库", store.QueryRecentMinutesForDeviceAsync(60, a.Device.DeviceId).GetAwaiter().GetResult().All(point => point.TemperatureAvg == 4) && store.QueryRecentMinutesForDeviceAsync(60, b.Device.DeviceId).GetAwaiter().GetResult().All(point => point.TemperatureAvg == 25) && store.CountSamplesAsync().GetAwaiter().GetResult() >= 4);
            using var reopened = new SerialTransport();
            Check("暂停后端口已释放并可重新打开", reopened.Open(portA, a.Serial).Length == 0);
        }
        catch (Exception ex) { Check("验证异常：" + ex.Message, false); }
        finally
        {
            try { if (mock is { HasExited: false }) { mock.Kill(true); mock.WaitForExit(3000); } mock?.Dispose(); } catch { }
            // 本次独占、已解析的临时目录；不清理其他工具或历史数据库。
            try { Directory.Delete(directory, true); } catch { }
        }
        Finish(output, passed, failed);
        return failed == 0 ? 0 : 1;
    }
}
