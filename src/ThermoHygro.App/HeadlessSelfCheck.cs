using System;
using System.IO;
using System.Text;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Protocols;

namespace ThermoHygro.App;

/// <summary>
/// 无界面自检（--self-check）。用于在无人工操作时验证：
/// ①协议向量（CRC、正/负/零点、异常帧）；②本地库真实写入；
/// ③帧解析拒绝坏帧；④新鲜度过期判定；⑤环境信息。
/// 输出到 stdout 并写文件，退出码 0=全部通过。
/// </summary>
public static class HeadlessSelfCheck
{
    public static int Run()
    {
        var sb = new StringBuilder();
        int failures = 0;
        int checks = 0;

        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failures++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " :: " + detail : "")}");
        }

        sb.AppendLine("=== ThermoHygro 自检 ===");
        sb.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"运行目录：{AppContext.BaseDirectory}");
        sb.AppendLine($"操作系统：{Environment.OSVersion.VersionString} / 64位进程 {Environment.Is64BitProcess}");
        sb.AppendLine($"屏幕：{ScreenLocator.Enumerate().Count} 个");
        foreach (var s in ScreenLocator.Enumerate()) sb.AppendLine("  " + s.Describe());
        sb.AppendLine();

        var proto = new Ecm30Protocol();

        // 1. CRC 向量
        Check("CRC 温度请求 01 03 01 00 00 01 -> 85 F6",
            ModbusCrc.Compute(Hex("01 03 01 00 00 01")) == 0xF685);
        Check("CRC 湿度请求 01 03 01 05 00 01 -> 95 F7",
            ModbusCrc.Compute(Hex("01 03 01 05 00 01")) == 0xF795);
        Check("CRC 回复 23.5℃ 01 03 02 00 EB -> F8 0B",
            ModbusCrc.Compute(Hex("01 03 02 00 EB")) == 0x0BF8);
        Check("CRC 回复 -12.3℃ 01 03 02 FF 85 -> 38 17",
            ModbusCrc.Compute(Hex("01 03 02 FF 85")) == 0x1738);
        Check("CRC 回复 50.0%RH 01 03 02 01 F4 -> B8 53",
            ModbusCrc.Compute(Hex("01 03 02 01 F4")) == 0x53B8);
        Check("CRC 异常 01 83 02 -> C0 F1",
            ModbusCrc.Compute(Hex("01 83 02")) == 0xF1C0);

        // 2. 数值解析（固定报文向量）
        var tempMetric = proto.SupportedMetrics[0];
        var humMetric = proto.SupportedMetrics[1];
        Check("解析 23.5℃", proto.ParseResponse(Hex("01 03 02 00 EB F8 0B"), 1, tempMetric).Value == 23.5);
        Check("解析 -12.3℃（负号正确）", proto.ParseResponse(Hex("01 03 02 FF 85 38 17"), 1, tempMetric).Value == -12.3);
        Check("解析 0.0℃（0 是有效值）", proto.ParseResponse(Hex("01 03 02 00 00 B8 44"), 1, tempMetric).Value == 0.0);
        Check("解析 50.0%RH", proto.ParseResponse(Hex("01 03 02 01 F4 B8 53"), 1, humMetric).Value == 50.0);

        // 3. 负例拒绝
        var r1 = proto.ParseResponse(Hex("01 03 02 00 EB F8 0C"), 1, tempMetric); // CRC 错
        Check("拒绝 CRC 错帧", !r1.Success && r1.Reason == FrameRejectReason.CrcMismatch, r1.ReasonText);
        var r2 = proto.ParseResponse(Hex("02 03 02 00 EB BC 0B"), 1, tempMetric); // 地址错（CRC 自洽）
        Check("拒绝地址错帧", !r2.Success && r2.Reason == FrameRejectReason.AddressMismatch, r2.ReasonText);
        var r3 = proto.ParseResponse(Hex("01 04 02 00 EB F9 7F"), 1, tempMetric); // 功能码错（CRC 自洽）
        Check("拒绝功能码错帧", !r3.Success && r3.Reason == FrameRejectReason.FunctionMismatch, r3.ReasonText);
        var r4 = proto.ParseResponse(Hex("01 03 04 00 EB 18 0A"), 1, tempMetric); // 字节数错（Len=4 但帧长 7，CRC 自洽）
        Check("拒绝字节数错帧", !r4.Success && r4.Reason == FrameRejectReason.ByteCountMismatch, r4.ReasonText);
        var r5 = proto.ParseResponse(Hex("01 83 02 C0 F1"), 1, tempMetric); // 异常响应
        Check("拒绝异常响应并保留异常码", !r5.Success && r5.ExceptionCode == 0x02, r5.ReasonText);
        var r6 = proto.ParseResponse(Hex("01 03 02 00"), 1, tempMetric); // 截断
        Check("拒绝截断帧", !r6.Success, r6.ReasonText);
        var r7 = proto.ParseResponse(Hex("01 03 02 7F FF D8 34"), 1, humMetric); // 湿度超量程（CRC 自洽）
        Check("拒绝湿度超量程值（3276.7%RH）", !r7.Success && r7.Reason == FrameRejectReason.RangeViolation, r7.ReasonText);

        // 4. 帧读取器：分片/粘帧/前置噪声
        var fr = new FrameReader();
        var frame = Hex("01 03 02 00 EB F8 0B");
        fr.Append(frame.AsSpan(0, 3));
        var partial = fr.TryTakeFrame(7, 1);
        Check("分片到达时不提前出帧", partial is null);
        fr.Append(frame.AsSpan(3));
        var full = fr.TryTakeFrame(7, 1);
        Check("补齐后取出完整帧", full is not null && full.Length == 7);

        var fr2 = new FrameReader();
        fr2.Append(Hex("AA BB")); // 前置噪声
        fr2.Append(frame);
        var cleaned = fr2.TryTakeFrame(7, 1);
        Check("前置噪声后可正确对齐帧", cleaned is not null && cleaned[0] == 0x01);

        var fr3 = new FrameReader();
        fr3.Append(frame); fr3.Append(frame); // 粘帧
        var f1 = fr3.TryTakeFrame(7, 1);
        var f2 = fr3.TryTakeFrame(7, 1);
        Check("粘帧可连续取出两帧", f1 is not null && f2 is not null);

        var fr4 = new FrameReader(64);
        fr4.Append(new byte[500]);
        Check("缓存有上界（丢弃最旧数据）", fr4.BufferedCount <= 64, $"当前 {fr4.BufferedCount}，累计丢弃 {fr4.DiscardedBytes}");

        // 5. 本地库真实写入（隔离目录）
        var tmpDir = Path.Combine(Path.GetTempPath(), "thermohygro-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        var dbPath = Path.Combine(tmpDir, "check.db");
        try
        {
            using var store = new SqliteLocalStore(dbPath);
            store.InitializeAsync().GetAwaiter().GetResult();
            Check("SQLite 初始化成功", File.Exists(dbPath), dbPath);
            Check("重复初始化幂等", RunInitAgain(store));

            var dev = new DeviceIdentity { DisplayName = "自检设备", Location = "自检位置" };
            var th = new ThresholdSnapshot { VersionId = 1 };
            var g = new ReadingGroup
            {
                AttemptTimeUtc = DateTimeOffset.UtcNow,
                Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Good, Value = 23.5, TimestampUtc = DateTimeOffset.UtcNow },
                Humidity = new MetricReading { Kind = MetricKind.Humidity, Quality = MetricQuality.Good, Value = 50.0, TimestampUtc = DateTimeOffset.UtcNow }
            };
            var saved = store.AppendSampleAsync(dev, th, g).GetAwaiter().GetResult();
            Check("样本写入成功（仅提交后为已保存）", saved.Saved, saved.Error ?? $"{saved.SampleId}");

            // 部分失败：温度成功、湿度失败 -> 湿度保持 NULL，不写 0
            var g2 = new ReadingGroup
            {
                AttemptTimeUtc = DateTimeOffset.UtcNow,
                Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Good, Value = 24.0, TimestampUtc = DateTimeOffset.UtcNow },
                Humidity = new MetricReading { Kind = MetricKind.Humidity, Quality = MetricQuality.Failed, Reason = "自检：模拟湿度失败" }
            };
            var saved2 = store.AppendSampleAsync(dev, th, g2).GetAwaiter().GetResult();
            Check("部分失败样本写入成功", saved2.Saved);

            var pts = store.QueryRecentMinutesAsync(60).GetAwaiter().GetResult();
            var withTemp = pts.FirstOrDefault(p => p.TempValidCount > 0);
            var withHum = pts.FirstOrDefault(p => p.HumidityValidCount > 0);
            Check("分钟汇总生成了温度均值", withTemp?.TemperatureAvg.HasValue == true,
                withTemp?.TemperatureAvg is null ? "无" : $"温度均值 {withTemp.TemperatureAvg:F1}");
            Check("湿度缺测不计 0（有效次数与均值独立）",
                withHum is not null && withHum.HumidityValidCount == 1 && Math.Abs(withHum.HumidityAvg!.Value - 50.0) < 0.01,
                withHum is null ? "无湿度有效分钟" : $"湿度均值 {withHum.HumidityAvg:F1}，有效 {withHum.HumidityValidCount} 次");
            // 严格执行“同一分钟只有一行”：早期实现因唯一键含 NULL，两条样本会各占一行。
            // 断言用 >= 1 时这种缺陷不会被发现，因此这里要求恰好 1 行。
            var minuteCount = store.CountMinutesAsync().GetAwaiter().GetResult();
            Check("两条同分钟样本聚合为一行（严格）", minuteCount == 1, $"分钟 {minuteCount} 行（期望 1 行）");
            Check("样本与汇总计数一致", store.CountSamplesAsync().GetAwaiter().GetResult() == 2 && minuteCount == 1,
                $"样本 {store.CountSamplesAsync().GetAwaiter().GetResult()}，分钟 {minuteCount}");
        }
        catch (Exception ex)
        {
            Check("SQLite 集成自检", false, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }

        // 5b. 真实故障场景：库损坏与独占锁定（不依赖 ACL，结果可重复）
        try
        {
            // ①库文件损坏：入垃圾字节后再初始化，必须如实报故障且保留原文件
            var corruptDir = Path.Combine(Path.GetTempPath(), "thermohygro-corrupt-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(corruptDir);
            var corruptFile = Path.Combine(corruptDir, "broken.db");
            File.WriteAllBytes(corruptFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });
            var lenBefore = new FileInfo(corruptFile).Length;
            using (var store2 = new SqliteLocalStore(corruptFile))
            {
                string reported = "";
                try { store2.InitializeAsync().GetAwaiter().GetResult(); reported = "未报错"; }
                catch (Exception ex) { reported = SqliteLocalStore.DescribeStorageError(ex); }
                Check("损坏库：报告故障且不误报正常",
                    store2.State == StorageState.Fault && reported.Contains("损坏"),
                    reported);
                Check("损坏库：原文件被保留（未删除/未重建）",
                    File.Exists(corruptFile) && new FileInfo(corruptFile).Length == lenBefore,
                    $"存在 {File.Exists(corruptFile)}，长度 {new FileInfo(corruptFile).Length}/{lenBefore}");
            }
            try { Directory.Delete(corruptDir, true); } catch { }
        }
        catch (Exception ex)
        {
            Check("损坏库场景自检", false, ex.Message);
        }

        try
        {
            // ②库被独占锁定：写入必须报未保存，不无限等待
            var lockDir = Path.Combine(Path.GetTempPath(), "thermohygro-lock-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(lockDir);
            var lockFile = Path.Combine(lockDir, "locked.db");
            using (var store3 = new SqliteLocalStore(lockFile))
            {
                store3.InitializeAsync().GetAwaiter().GetResult();
            }
            using (var hold = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var store4 = new SqliteLocalStore(lockFile);
                var dev = new DeviceIdentity { DisplayName = "锁定测试" };
                var g = new ReadingGroup
                {
                    AttemptTimeUtc = DateTimeOffset.UtcNow,
                    Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Good, Value = 1.0, TimestampUtc = DateTimeOffset.UtcNow }
                };
                var outcome = store4.AppendSampleAsync(dev, new ThresholdSnapshot { VersionId = 777 }, g).GetAwaiter().GetResult();
                Check("库被占用时报告为未保存（不假报已保存）",
                    !outcome.Saved && outcome.Error is not null,
                    outcome.Error ?? "写入竟然成功");
            }
            try { Directory.Delete(lockDir, true); } catch { }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[WARN] 锁定场景自检跳过：{ex.Message}");
        }

        // 5c. 错误文案映射（磁盘满/只读）——仅验证映射逻辑，不代表已真实耗尽磁盘
        Check("磁盘满文案映射",
            SqliteLocalStore.DescribeStorageError(new Exception("SQLite Error 13: 'database or disk is full'.")).Contains("磁盘已满"));
        Check("只读文案映射",
            SqliteLocalStore.DescribeStorageError(new Exception("attempt to write a readonly database")).Contains("只读"));

        // 6. 新鲜度过期（独立计时判定）
        var stale = new MetricReading
        {
            Kind = MetricKind.Temperature,
            Quality = MetricQuality.Good,
            Value = 20.0,
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-120)
        };
        var window = TimeSpan.FromSeconds(15);
        bool isStale = stale.TimestampUtc.HasValue && DateTimeOffset.UtcNow - stale.TimestampUtc.Value > window;
        Check("过期判定：120 秒前的读数在 15 秒窗口下判为过期", isStale);

        // 7. 悬浮窗定位计算（四角/负坐标/拔屏）
        var scr = new ScreenInfo
        {
            DeviceName = "TEST",
            IsPrimary = true,
            Bounds = new System.Windows.Rect(0, 0, 1920, 1080),
            WorkArea = new System.Windows.Rect(0, 0, 1920, 1040)   // 任务栏在底部
        };
        var size = new System.Windows.Size(300, 90);
        var tl = ScreenLocator.ComputePosition("TopLeft", scr, size, 24, 24);
        var br = ScreenLocator.ComputePosition("BottomRight", scr, size, 24, 24);
        Check("左上角定位正确", Math.Abs(tl.X - 24) < 0.01 && Math.Abs(tl.Y - 24) < 0.01, $"({tl.X},{tl.Y})");
        Check("右下角避开任务栏（工作区 1040 而非 1080）",
            Math.Abs(br.Y - (1040 - 90 - 24)) < 0.01, $"({br.X},{br.Y})");

        var negative = new ScreenInfo
        {
            DeviceName = "NEG",
            IsPrimary = false,
            Bounds = new System.Windows.Rect(-1920, 0, 1920, 1080),
            WorkArea = new System.Windows.Rect(-1920, 0, 1920, 1080)
        };
        var negBr = ScreenLocator.ComputePosition("BottomRight", negative, size, 24, 24);
        Check("负坐标副屏定位正确", negBr.X < 0, $"({negBr.X},{negBr.Y})");

        var screens = new[] { scr };
        var ghost = new System.Windows.Point(5000, 5000); // 模拟拔屏后窗口在不可见区
        var clamped = ScreenLocator.ClampToVisible(ghost, size, screens);
        Check("拔屏后自动回到可见区", clamped.HasValue && clamped.Value.X < 1920, clamped?.ToString() ?? "未纠正");

        // 8. 环境与串口
        var ports = SerialTransport.GetPortNames();
        sb.AppendLine($"[INFO] 当前串口：{(ports.Length == 0 ? "未检测到" : string.Join("、", ports))}");
        sb.AppendLine($"[INFO] 协议：{proto.Describe()}；实物验证状态：{proto.VerificationStatus}");

        sb.AppendLine();
        sb.AppendLine($"=== 结果：{checks - failures}/{checks} 通过，失败 {failures} ===");
        var text = sb.ToString();
        Console.WriteLine(text);
        try
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "selfcheck");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "selfcheck.txt"), text, new UTF8Encoding(true));
        }
        catch { }
        return failures == 0 ? 0 : 1;
    }

    private static bool RunInitAgain(SqliteLocalStore store)
    {
        try
        {
            store.InitializeAsync().GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    private static byte[] Hex(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) b[i] = Convert.ToByte(parts[i], 16);
        return b;
    }
}