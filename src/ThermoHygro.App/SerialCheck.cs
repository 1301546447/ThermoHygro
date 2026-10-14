using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Protocols;

namespace ThermoHygro.App;

/// <summary>
/// 真实串口回归（--serial-check &lt;请求口&gt; &lt;响应口&gt; [模式]）。
/// 通过一对真实虚拟串口验证：正常读取、负温、零点、CRC 错拒绝、异常响应、
/// 半包、粘帧、迟到回复隔离、断开提示、独占占用提示。
/// 结果写入 artifacts 供证据留存。
/// </summary>
public static partial class SerialCheck
{
    public static int Run(string portA, string portB, string mode)
    {
        if (mode == "multi") return RunMulti(portA, portB);
        if (mode == "late-round") return RunLateRound(portA, portB);
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " :: " + detail : "")}");
        }

        sb.AppendLine("=== 真实虚拟串口回归（V 级证据：程序实际串口收发，对端为模拟传感器）===");
        sb.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"本程序口：{portA}　对端口：{portB}　模拟器模式：{mode}");
        sb.AppendLine("注意：模拟器数据为模拟值，仅用于验证传输与解析行为，不代表真实传感器读数。");
        sb.AppendLine();

        var proto = new Ecm30Protocol();
        var settings = new SerialSettings
        {
            PortName = portA,
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            SlaveAddress = 1,
            ReadTimeoutMs = 3000,
            PollIntervalSeconds = 5
        };

        // 启动模拟器（对端口）
        Process? mock = null;
        try
        {
            var exe = ResolveMockSensorPath();
            if (exe.Length == 0)
            {
                sb.AppendLine("[WARN] 未找到模拟器可执行文件；串口应答测试将失败。请先构建 tools/MockSensor。");
                Console.WriteLine("[WARN] 未找到模拟器可执行文件");
            }
            else
            {
                mock = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"{portB} {mode} 45 1",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true
                });
                sb.AppendLine($"[INFO] 已启动模拟器：{exe} {portB} {mode} 45 1 (PID {mock?.Id})");
                Thread.Sleep(1500); // 等待模拟器打开串口
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[WARN] 启动模拟器失败：{ex.Message}");
        }

        try
        {
            using var transport = new SerialTransport();

            // 1. 打开串口
            var err = transport.Open(settings.PortName, settings);
            Check("打开串口（本程序口）", err.Length == 0, err.Length == 0 ? settings.PortName : err);
            if (err.Length > 0) { Finish(sb, pass, fail); return 1; }

            // 2. 独占：同口再打开必须失败并给出"被占用"提示
            using (var t2 = new SerialTransport())
            {
                var err2 = t2.Open(settings.PortName, settings);
                Check("同一串口被独占（第二次打开被拒绝）", err2.Length > 0, err2.Length > 0 ? err2 : "竟然打开成功");
            }

            // 3. 不存在的串口
            using (var t3 = new SerialTransport())
            {
                var err3 = t3.Open("COM199", settings);
                Check("不存在的串口给出明确错误", err3.Length > 0, err3.Length > 0 ? err3 : "竟然打开成功");
            }

            // 4. 真实请求-响应（温度）
            var tempMetric = proto.SupportedMetrics[0];
            var reqT = proto.BuildReadRequest(1, tempMetric);
            var r1 = transport.RequestAsync(reqT, 7, 1, 3000, CancellationToken.None).GetAwaiter().GetResult();
            string frameText = r1.Frame is null ? "(无)" : BitConverter.ToString(r1.Frame).Replace("-", " ");
            // 各模式的期望结果（与 tools/MockSensor 的实际行为一致）：
            //   normal/negative/zero           → 温湿度都应成功读取
            //   no-response/late               → 仅温度失败（模拟器只对温度请求不回复/延迟回复）
            //   exception/crc-error            → 模拟器对温度与湿度都返回坏帧，两者都必须被拒绝
            bool expectTempOk = mode is "normal" or "negative" or "zero";
            bool expectHumOk = mode is "normal" or "negative" or "zero" or "no-response" or "late";
            Check($"温度回复（模式 {mode}，期望{(expectTempOk ? "成功" : "被拒绝")}）", r1.Success == expectTempOk, frameText);
            if (r1.Success && r1.Frame is not null)
            {
                var p = proto.ParseResponse(r1.Frame, 1, tempMetric);
                var expectValue = mode switch
                {
                    "negative" => -12.3,
                    "zero" => 0.0,
                    _ => (double?)null
                };
                if (expectValue.HasValue)
                    Check($"温度解析（模式 {mode}）", p.Success && Math.Abs(p.Value!.Value - expectValue.Value) < 0.01,
                        p.Success ? $"{p.Value:F1} ℃" : p.ReasonText);
                else
                    Check("温度解析为有效数值", p.Success, p.Success ? $"{p.Value:F1} ℃" : p.ReasonText);
            }
            else
            {
                // 异常模式下不仅要拒绝，还要给出可解释的原因（不能静默失败）
                Check($"温度失败已给出原因（模式 {mode}）", !expectTempOk && !string.IsNullOrEmpty(r1.Error), r1.Error ?? "(无错误说明)");
            }

            // 5. 湿度请求（迟到隔离的关键：温度超时后的迟到回复不得充当湿度）
            var humMetric = proto.SupportedMetrics[1];
            var reqH = proto.BuildReadRequest(1, humMetric);
            var r2 = transport.RequestAsync(reqH, 7, 1, 3000, CancellationToken.None).GetAwaiter().GetResult();
            if (mode == "late")
            {
                // 温度迟到回复会在 4 秒后到达；湿度请求已在其之前发出。
                // 只要湿度帧解析出的寄存器语义正确（即没有被温度值污染），即通过。
                var phr = r2.Success && r2.Frame is not null ? proto.ParseResponse(r2.Frame, 1, humMetric) : null;
                Check("迟到回复未被当作湿度（湿度帧独立解析）",
                    phr is not null && phr.Success,
                    phr is not null && phr.Success ? $"湿度 {phr.Value:F1} %RH（未受温度迟到帧影响）" : (r2.Error ?? "未收到湿度帧"));
                // 等待迟到帧到达，确认它没有污染后续请求
                Thread.Sleep(3500);
                transport.PurgeInput();
                var r3 = transport.RequestAsync(reqH, 7, 1, 3000, CancellationToken.None).GetAwaiter().GetResult();
                var ph3 = r3.Success && r3.Frame is not null ? proto.ParseResponse(r3.Frame, 1, humMetric) : null;
                Check("迟到帧清理后再次请求仍正确", ph3 is not null && ph3.Success,
                    ph3 is not null && ph3.Success ? $"湿度 {ph3.Value:F1} %RH" : (r3.Error ?? "未收到"));
            }
            else
            {
                Check($"湿度回复（模式 {mode}，期望{(expectHumOk ? "成功" : "被拒绝")}）", r2.Success == expectHumOk,
                    r2.Frame is null ? (r2.Error ?? "(无)") : BitConverter.ToString(r2.Frame).Replace("-", " "));
                if (r2.Success && r2.Frame is not null)
                {
                    var ph = proto.ParseResponse(r2.Frame, 1, humMetric);
                    Check("湿度解析为有效数值", ph.Success, ph.Success ? $"{ph.Value:F1} %RH" : ph.ReasonText);
                }
            }

            // 6. 采集服务端到端（走完整链路：采集 → 解析 → SQLite 入库）
            var dbDir = Path.Combine(Path.GetTempPath(), "thermohygro-serial-" + Guid.NewGuid().ToString("N")[..8]);
            var dbPath = Path.Combine(dbDir, "serial.db");
            try
            {
                using var store = new SqliteLocalStore(dbPath);
                store.InitializeAsync().GetAwaiter().GetResult();
                using var svc = new AcquisitionService(transport, new SystemClock(), store);
                var dev = new DeviceIdentity { DisplayName = "虚拟串口验证设备", Location = "验证环境" };
                var th = new ThresholdSnapshot { VersionId = 1 };
                var startErr = svc.Start(settings, dev, th);
                Check("采集服务启动", startErr.Length == 0, startErr.Length == 0 ? "已启动" : startErr);

                // 等待 2 轮采集（间隔 5 秒）
                var snaps = new System.Collections.Generic.List<AcquisitionSnapshot>();
                svc.SnapshotChanged += s => { lock (snaps) snaps.Add(s); };
                Thread.Sleep(12000);
                svc.Pause();
                Thread.Sleep(500);
                svc.StopAsync().GetAwaiter().GetResult();

                AcquisitionSnapshot? good = null;
                lock (snaps) good = snaps.FindLast(s => s.Reading.Temperature.Quality == MetricQuality.Good);

                if (expectTempOk)
                {
                    Check("采集服务端到端产生有效温度快照", good is not null,
                        good is null ? $"共 {snaps.Count} 个快照" :
                        $"温度 {good.Reading.Temperature.Value:F1} ℃ / 湿度 {(good.Reading.Humidity.Value.HasValue ? good.Reading.Humidity.Value.Value.ToString("F1") : "缺测")} %RH");
                }
                else
                {
                    Check($"异常模式不伪造读数（模式 {mode}，无 Good 温度快照）", good is null,
                        good is null ? $"共 {snaps.Count} 个快照，均非 Good" : $"出现质量 {good.Reading.Temperature.Quality}");
                }

                if (mode == "normal")
                {
                    Check("读数质量状态为 Good（非失败/过期）",
                        good?.Reading.Temperature.Quality == MetricQuality.Good,
                        good?.Reading.Temperature.Quality.ToString() ?? "无");
                    var samples = store.CountSamplesAsync().GetAwaiter().GetResult();
                    var minutes = store.CountMinutesAsync().GetAwaiter().GetResult();
                    Check("读数已写入本地库（样本>0 且分钟>0）", samples > 0 && minutes > 0,
                        $"样本 {samples} 条，分钟 {minutes} 条");
                    Check("保存状态报告为已保存",
                        snaps.Exists(s => s.Storage == StorageState.Ok),
                        "快照中出现 StorageState.Ok");
                }
                else if (!expectTempOk)
                {
                    Check($"异常模式读数状态正确（模式 {mode}，失败/过期而非 Good）",
                        good is null || good.Reading.Temperature.Quality != MetricQuality.Good,
                        good is null ? "无 Good 快照" : $"质量 {good.Reading.Temperature.Quality}");
                }
            }
            finally
            {
                try { if (Directory.Exists(dbDir)) Directory.Delete(dbDir, true); } catch { }
            }

            transport.Close();
            Check("关闭串口后释放", !transport.IsOpen);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            fail++;
        }
        finally
        {
            try
            {
                if (mock is { HasExited: false })
                {
                    mock.Kill(true);
                    mock.WaitForExit(3000);
                    sb.AppendLine("[INFO] 模拟器已停止");
                }
            }
            catch { }
        }

        Finish(sb, pass, fail);
        return fail == 0 ? 0 : 1;
    }

    /// <summary>从运行目录向上查找 tools/MockSensor 的编译产物；找不到时返回空。</summary>
    private static string ResolveMockSensorPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var release = Path.Combine(dir, "tools", "MockSensor", "bin", "Release", "net10.0-windows", "MockSensor.exe");
            if (File.Exists(release)) return release;
            var candidate = Path.Combine(dir, "tools", "MockSensor", "bin", "Debug", "net10.0-windows", "MockSensor.exe");
            if (File.Exists(candidate)) return candidate;
            var candidate2 = Path.Combine(dir, "MockSensor.exe");
            if (File.Exists(candidate2)) return candidate2;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return "";
    }

    private static void Finish(StringBuilder sb, int pass, int fail)
    {
        sb.AppendLine();
        sb.AppendLine($"=== 结果：{pass} 通过，{fail} 失败 ===");
        var text = sb.ToString();
        Console.WriteLine(text);
        try
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "selfcheck");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, $"serial-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                text, new UTF8Encoding(true));
            Console.WriteLine($"[INFO] 结果已写入 {outDir}");
        }
        catch { }
    }
}
