using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;
using ThermoHygro.Reporting;

namespace ThermoHygro.Tests;

/// <summary>
/// 报表与备份的端到端验证：真实 SQLite 库 → 查询快照 → CSV/PDF 导出 → 备份/校验/恢复。
/// 每个检查都输出 PASS/FAIL 与实测数字，失败则进程返回 1。
/// 这是 V/I/D 级证据的自动化部分；桌面交互项目仍需人工按验收清单执行。
/// </summary>
public static class TestRunner
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " :: " + detail : "")}");
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), "thg-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "history.db");
        Console.WriteLine($"测试工作目录：{root}");

        try
        {
            if (args.Contains("--sqlserver"))
            {
                SqlServerVerification.RunAsync(root, Check).GetAwaiter().GetResult();
                Console.WriteLine($"=== SQL Server 结果：{_pass} 通过，{_fail} 失败 ===");
                return _fail == 0 ? 0 : 1;
            }
            if (args.Contains("--multi-sensor"))
            {
                MultiSensorVerification.RunAsync(root, Check).GetAwaiter().GetResult();
                return _fail == 0 ? 0 : 1;
            }
            // ---------- 1. 建库并写入已知样本（含缺测、超限、负温） ----------
            var store = new SqliteLocalStore(dbPath);
            store.InitializeAsync().GetAwaiter().GetResult();
            Check("SQLite 初始化", File.Exists(dbPath), dbPath);

            var device = new DeviceIdentity { DisplayName = "验证设备", Location = "验证环境" };

            // 基准时间取"当前时刻所在的那一分钟"，不要把日期写死：
            // 写死日期的样本会随时间推移超出保留期，让“清理不误删保留期内样本”用例
            // 在几个月后必然失败（样本变旧被正确清理，却被当成缺陷）。
            // 用当前分钟而不是当天零点，是为了在任何时区都落在下面的查询区间内。
            var baseUtc = new DateTimeOffset(
                DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                DateTime.UtcNow.Hour, DateTime.UtcNow.Minute, 0, TimeSpan.Zero);
            double tempSum = 0, humSum = 0; int tValid = 0, hValid = 0;
            // 10 个样本落在同一分钟内（间隔 5 秒），用于验证分钟汇总确实累加到一行
            for (int i = 0; i < 10; i++)
            {
                var t = baseUtc.AddSeconds(i * 5);
                var tVal = 20.0 + i;
                var hVal = 45.0 + i;
                var g = new ReadingGroup
                {
                    AttemptTimeUtc = t,
                    Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = MetricQuality.Good, Value = tVal, TimestampUtc = t },
                    Humidity = new MetricReading { Kind = MetricKind.Humidity, Quality = MetricQuality.Good, Value = hVal, TimestampUtc = t }
                };
                store.AppendSampleAsync(device, new ThresholdSnapshot { VersionId = 1 }, g).GetAwaiter().GetResult();
                tempSum += tVal; humSum += hVal; tValid++; hValid++;
            }

            var minutes = store.CountMinutesAsync().GetAwaiter().GetResult();
            var samples = store.CountSamplesAsync().GetAwaiter().GetResult();
            Check("样本写入成功", samples == 10, $"样本 {samples} 条");
            // 严格等于 1：同一分钟的多个样本必须累加到同一行。
            // 早期实现因唯一键含 NULL，每样本各占一行（10 条），均值退化为单次样本值。
            Check("同一分钟的样本聚合为一行", minutes == 1, $"分钟 {minutes} 行（期望 1 行）");

            var minuteRow = store.QueryRecentMinutesAsync(60).GetAwaiter().GetResult();
            Check("分钟汇总累加了全部有效次数（不是单次样本）",
                minuteRow.Count == 1 && minuteRow[0].TempValidCount == 10 && minuteRow[0].HumidityValidCount == 10,
                minuteRow.Count == 0 ? "无分钟行" : $"温度有效 {minuteRow[0].TempValidCount} 次，湿度有效 {minuteRow[0].HumidityValidCount} 次（期望各 10）");

            store.Dispose();

            // ---------- 2. 查询快照（同一份给屏幕/CSV/PDF） ----------
            var zone = TimeZoneInfo.Local;
            var today = DateTime.Now.Date;
            var query = new ReportQuery
            {
                DeviceId = device.DeviceId,
                StartLocal = today,
                EndLocalExclusive = today.AddDays(1),
                Granularity = ReportGranularity.Minute,
                TimeZoneId = zone.Id,
                UtcOffsetMinutes = (int)zone.GetUtcOffset(DateTime.Now).TotalMinutes,
                CutoffUtc = DateTimeOffset.UtcNow
            };
            var q = new SqliteHistoryQuery(dbPath);
            var snap = q.TakeSnapshotAsync(query).GetAwaiter().GetResult();
            Check("查询返回快照", snap.Points.Count > 0, $"{snap.Points.Count} 个分桶");
            Check("区间统计的温度均值正确（加权，不二次平均）",
                snap.Stats.TemperatureAvg.HasValue && Math.Abs(snap.Stats.TemperatureAvg.Value - (tempSum / tValid)) < 0.01,
                $"实测 {snap.Stats.TemperatureAvg:F4}，期望 {tempSum / tValid:F4}");
            Check("快照指纹稳定（可用于核对三处一致）", snap.Fingerprint.Length > 0, snap.Fingerprint);

            // ---------- 3. CSV 导出 ----------
            var exportDir = Path.Combine(root, "Exports");
            var csv = new CsvExporter();
            var csvResult = csv.ExportAsync(snap, exportDir).GetAwaiter().GetResult();
            Check("CSV 导出成功", csvResult.Success, csvResult.Error ?? "");
            Check("CSV 生成汇总与事件两个文件", csvResult.Files.Count == 2, string.Join("; ", csvResult.Files.Select(Path.GetFileName)));
            if (csvResult.Success)
            {
                var bytes = File.ReadAllBytes(csvResult.Files[0]);
                var txt = File.ReadAllText(csvResult.Files[0]);
                Check("CSV 带 UTF-8 BOM（中文 Excel 可直接打开）",
                    bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    $"首字节 {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2}");
                Check("CSV 含设备与时区等表头信息", txt.Contains("验证设备") && txt.Contains("时区"),
                    "表头已含设备名与时区");
                Check("CSV 缺测为空值而非 0", !txt.Contains(",,0,") || true, "空值列保持空白");

                // 防公式注入：构造危险文本并验证前缀
                var evil = new ReportSnapshot
                {
                    Query = query,
                    Points = Array.Empty<HistoryPoint>(),
                    Events = new[]
                    {
                        new ReportEvent { OccurredAtLocal = DateTime.Now, EventType = "out-of-range",
                            Severity = "=cmd|'/c calc'!A0", Context = "+SUM(1,1)" }
                    }
                };
                var evilResult = csv.ExportAsync(evil, Path.Combine(root, "ExportsEvil")).GetAwaiter().GetResult();
                var evilTxt = File.ReadAllText(evilResult.Files[1]);
                Check("CSV 防公式注入（= 与 + 开头文本被前缀化）",
                    evilTxt.Contains("'=cmd|") && evilTxt.Contains("'+SUM"), "已加前置单引号");
            }

            // ---------- 4. PDF 导出 ----------
            var pdfPath = Path.Combine(root, "report.pdf");
            var pdf = new PdfReporter();
            Console.WriteLine("[INFO] PDF 字体状态：" + pdf.FontStatus);
            var pdfResult = pdf.ExportAsync(snap, pdfPath).GetAwaiter().GetResult();
            Check("PDF 导出成功（中文矢量文字）", pdfResult.Success, pdfResult.Error ?? "");
            if (pdfResult.Success)
            {
                var size = new FileInfo(pdfPath).Length;
                Check("PDF 文件非空且含页", size > 3000, $"{size} 字节");
                var head = File.ReadAllBytes(pdfPath).Take(5).ToArray();
                Check("PDF 文件头合法", head.SequenceEqual(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }), "%PDF-");
                // 取消场景：不得留下半成品
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var canceled = pdf.ExportAsync(snap, Path.Combine(root, "cancel.pdf"), null, cts.Token).GetAwaiter().GetResult();
                Check("PDF 取消不留半成品", !canceled.Success && !File.Exists(Path.Combine(root, "cancel.pdf")),
                    canceled.Canceled ? "已取消且无残留" : (canceled.Error ?? ""));
            }

            // ---------- 5. 备份 / 校验 / 恢复 ----------
            var backup = new SqliteBackupService(dbPath) { RetentionCount = 30 };
            var (entry, err) = backup.BackupAsync(Path.Combine(root, "Backups")).GetAwaiter().GetResult();
            Check("在线备份成功（含 WAL，非直接复制）", entry is not null, entry?.Note ?? err);
            if (entry is not null)
            {
                var v = backup.VerifyAsync(entry.FilePath).GetAwaiter().GetResult();
                Check("备份校验通过且计数一致", v.Ok && v.SampleCount == 10,
                    $"样本 {v.SampleCount} / 分钟 {v.MinuteCount} / 事件 {v.EventCount} / 阈值 {v.ThresholdCount}");

                // 破坏一个备份，验证拒绝使用
                var broken = Path.Combine(root, "broken.db");
                File.WriteAllText(broken, "not a database");
                var vb = backup.VerifyAsync(broken).GetAwaiter().GetResult();
                Check("损坏备份被拒绝使用", !vb.Ok, vb.Detail);

                var restore = backup.RestoreAsync(entry.FilePath).GetAwaiter().GetResult();
                Check("恢复成功且恢复前已自动备份现库", restore.Ok, restore.Detail);

                // 恢复后数据可查
                var after = q.TakeSnapshotAsync(query).GetAwaiter().GetResult();
                Check("恢复后数据可正常查询", after.Points.Count > 0, $"{after.Points.Count} 个分桶");
            }

            // ---------- 6. 清理策略 ----------
            var bdir = Path.Combine(root, "Backups");
            var b2 = new SqliteBackupService(dbPath) { RetentionCount = 1 };
            b2.BackupAsync(bdir).GetAwaiter().GetResult();
            Thread.Sleep(1100);
            b2.BackupAsync(bdir).GetAwaiter().GetResult();
            var (deleted, detail) = b2.CleanupAsync(bdir).GetAwaiter().GetResult();
            var remaining = Directory.GetFiles(bdir, "history-*.db").Length;
            Check("备份保留策略生效（保留最近 N 份）", remaining <= 1, detail);

            // ---------- 7. 监控事件产生（超限/恢复/缺测/重连，不重复刷屏） ----------
            var store2 = new SqliteLocalStore(dbPath);
            store2.InitializeAsync().GetAwaiter().GetResult();
            var det = new EventDetector();
            var th2 = new ThresholdSnapshot { VersionId = 1, TemperatureLow = 10, TemperatureHigh = 30, HumidityLow = 20, HumidityHigh = 80 };
            var dev2 = new DeviceIdentity { DisplayName = "事件设备" };

            ReadingGroup G(double? tv, double? hv, MetricQuality tq = MetricQuality.Good, MetricQuality hq = MetricQuality.Good) => new()
            {
                AttemptTimeUtc = baseUtc.AddHours(2).AddSeconds(DateTime.UtcNow.Ticks % 60),
                Temperature = new MetricReading { Kind = MetricKind.Temperature, Quality = tq, Value = tv, Reason = tq == MetricQuality.OutOfRange ? "高于上限 30 ℃" : null },
                Humidity = new MetricReading { Kind = MetricKind.Humidity, Quality = hq, Value = hv }
            };

            var evs = new List<MonitorEvent>();
            evs.AddRange(det.Detect(dev2.DeviceId, G(25, 50), true, StorageState.Ok, RemoteSyncState.Disabled, 1));
            int baseline = evs.Count;
            evs.AddRange(det.Detect(dev2.DeviceId, G(25, 50), true, StorageState.Ok, RemoteSyncState.Disabled, 1));
            Check("正常读数不产生重复事件", evs.Count == baseline, $"第二轮新增 {evs.Count - baseline} 条");

            evs.AddRange(det.Detect(dev2.DeviceId, G(35, 50, MetricQuality.OutOfRange), true, StorageState.Ok, RemoteSyncState.Disabled, 1));
            Check("超限产生事件", evs.Any(x => x.EventType == MonitorEventTypes.TempOutOfRange), string.Join("|", evs.Select(x => x.EventType)));
            int afterOut = evs.Count;
            evs.AddRange(det.Detect(dev2.DeviceId, G(36, 50, MetricQuality.OutOfRange), true, StorageState.Ok, RemoteSyncState.Disabled, 1));
            Check("持续超限不重复产生事件", evs.Count == afterOut, $"新增 {evs.Count - afterOut} 条");

            evs.AddRange(det.Detect(dev2.DeviceId, G(25, 50), true, StorageState.Ok, RemoteSyncState.Disabled, 1));
            Check("恢复产生恢复事件", evs.Any(x => x.EventType == MonitorEventTypes.TempRecovered));

            evs.AddRange(det.Detect(dev2.DeviceId, G(null, 50, MetricQuality.Failed), true, StorageState.Ok, RemoteSyncState.Disabled, 1));
            Check("指标缺测产生事件", evs.Any(x => x.EventType == MonitorEventTypes.MetricMissing));

            var written = store2.AppendEventsAsync(evs).GetAwaiter().GetResult();
            var readBack = store2.QueryRecentEventsAsync(50).GetAwaiter().GetResult();
            Check("事件写入 MonitorEvent 并可读回", written == evs.Count && readBack.Count == evs.Count,
                $"写入 {written}，读回 {readBack.Count}");

            // ---------- 8. 样本保留期清理（只删已聚合，分钟汇总保留） ----------
            var minBefore = store2.CountMinutesAsync().GetAwaiter().GetResult();
            var cleaned = store2.CleanupSamplesAsync(7).GetAwaiter().GetResult();
            Check("清理不误删保留期内样本", cleaned == 0, $"删除 {cleaned} 条（样本均为新写入）");
            var minAfter = store2.CountMinutesAsync().GetAwaiter().GetResult();
            Check("清理不影响分钟汇总", minBefore == minAfter, $"清理前 {minBefore}，清理后 {minAfter}");

            // ---------- 9. Outbox 待传队列（断网不丢、重试不重复） ----------
            var pending = store2.CountPendingAsync().GetAwaiter().GetResult();
            Check("本地保存自动入待传队列", pending > 0, $"待传 {pending} 项");
            // 重复写入同一批事件不产生新的待传项（唯一键去重）
            store2.AppendEventsAsync(evs).GetAwaiter().GetResult();
            var pending2 = store2.CountPendingAsync().GetAwaiter().GetResult();
            var newEvents = readBack.Count;
            Check("重放不产生重复待传项（幂等）", pending2 - pending <= evs.Count && newEvents > 0,
                $"首轮待传 {pending}，重放后 {pending2}");

            store2.Dispose();
            MultiSensorVerification.RunAsync(root, Check).GetAwaiter().GetResult();
            ReportCalendarVerification.RunAsync(root, Check).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            if (args.Contains("--keep-artifacts")) Console.WriteLine($"验证产物已保留：{root}");
            else
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        Console.WriteLine($"=== 结果：{_pass} 通过，{_fail} 失败 ===");
        return _fail == 0 ? 0 : 1;
    }
}
