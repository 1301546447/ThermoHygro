using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThermoHygro.Core;

namespace ThermoHygro.Reporting;

/// <summary>
/// CSV 导出：UTF-8 带 BOM（中文 Excel 可直接双击打开），汇总与事件分别成文件。
/// 规范转义（引号/逗号/换行），文本字段防公式注入（= + - @ 开头加单引号前缀），
/// 负温度等真实数值仍保持数值形态（不套单引号）。
/// 先写临时文件，成功后替换，失败不留下冒充成功半成品。
/// </summary>
public sealed class CsvExporter : ICsvExporter
{
    public Task<ExportResult> ExportAsync(ReportSnapshot snapshot, string outputDirectory,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // 不把 ct 直接交给 Task.Run：否则已取消时会抛出 TaskCanceledException，
        // 调用方无法区分“取消”与“失败”。由内部主动检查并转为 Canceled 结果。
        return Task.Run(() =>
        {
            if (ct.IsCancellationRequested)
                return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };
            var files = new List<string>();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var summaryPath = Path.Combine(outputDirectory, $"温湿度汇总-{stamp}.csv");
                var eventPath = Path.Combine(outputDirectory, $"温湿度事件-{stamp}.csv");

                WriteSummary(snapshot, summaryPath, progress, ct);
                if (ct.IsCancellationRequested)
                {
                    TryDelete(summaryPath);
                    return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };
                }
                files.Add(summaryPath);

                WriteEvents(snapshot, eventPath, ct);
                if (ct.IsCancellationRequested)
                {
                    foreach (var f in files) TryDelete(f);
                    TryDelete(eventPath);
                    return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };
                }
                files.Add(eventPath);

                return new ExportResult
                {
                    Success = true,
                    FilePath = summaryPath,
                    RowCount = snapshot.Points.Count,
                    Files = files
                };
            }
            catch (OperationCanceledException)
            {
                foreach (var f in files) TryDelete(f);
                return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };
            }
            catch (Exception ex)
            {
                foreach (var f in files) TryDelete(f);
                return new ExportResult { Success = false, Error = ex.Message };
            }
        });
    }

    private static void WriteSummary(ReportSnapshot s, string path, IProgress<int>? progress, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        var zone = TimeZoneInfo.Local;
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true)) { NewLine = "\r\n" })
        {
            w.WriteLine("# 温湿度监测记录报表（汇总）");
            w.WriteLine($"# 设备,{Esc(s.DeviceDisplayName)},{Esc(s.DeviceLocation)}");
            w.WriteLine($"# 区间,{Esc(s.RangeText)}");
            w.WriteLine($"# 粒度,{Esc(s.Query.GranularityText)}");
            w.WriteLine($"# 时区,{Esc(s.Query.TimeZoneId)} (UTC{(s.Query.UtcOffsetMinutes >= 0 ? "+" : "")}{s.Query.UtcOffsetMinutes / 60})");
            w.WriteLine($"# 查询截止点(UTC),{s.Query.CutoffUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"# 生成时间(本地),{s.GeneratedAtUtc.ToOffset(TimeSpan.FromMinutes(s.Query.UtcOffsetMinutes)):yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"# 快照指纹,{Esc(s.Fingerprint)}");
            w.WriteLine($"# 阈值说明,{Esc(s.ThresholdNote.Length == 0 ? "未配置（不追溯标红）" : s.ThresholdNote)}");
            w.WriteLine($"# 缺测说明,缺测为空值（不是 0）；整段无数据的桶标记为“整段缺测”，图表断线不插值。");
            w.WriteLine();
            w.WriteLine("桶开始时间,粒度,温度均值(℃),温度最小(℃),温度最大(℃),温度有效次数,温度极值发生时间,"
                + "湿度均值(%RH),湿度最小(%RH),湿度最大(%RH),湿度有效次数,湿度极值发生时间,"
                + "尝试次数,错过时隙,温度超限次数,湿度超限次数,温度缺测,湿度缺测,质量说明");

            int i = 0;
            foreach (var p in s.Points)
            {
                ct.ThrowIfCancellationRequested();
                var tminAt = p.TemperatureMinAtUtc.HasValue
                    ? TimeZoneInfo.ConvertTime(p.TemperatureMinAtUtc.Value, zone).ToString("yyyy-MM-dd HH:mm:ss") : "";
                var hmaxAt = p.HumidityMaxAtUtc.HasValue
                    ? TimeZoneInfo.ConvertTime(p.HumidityMaxAtUtc.Value, zone).ToString("yyyy-MM-dd HH:mm:ss") : "";
                w.WriteLine(string.Join(",",
                    p.BucketStartLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                    s.Query.GranularityText,
                    Num(p.TemperatureAvg), Num(p.TemperatureMin), Num(p.TemperatureMax), p.TemperatureValidCount.ToString(),
                    tminAt,
                    Num(p.HumidityAvg), Num(p.HumidityMin), Num(p.HumidityMax), p.HumidityValidCount.ToString(),
                    hmaxAt,
                    p.AttemptCount, p.MissedSlots, p.TempOutOfRangeCount, p.HumidityOutOfRangeCount,
                    p.TempMissing, p.HumidityMissing,
                    Esc(p.QualityText)));
                progress?.Report(++i);
            }
        }
        Replace(tmp, path);
    }

    private static void WriteEvents(ReportSnapshot s, string path, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true)) { NewLine = "\r\n" })
        {
            w.WriteLine("# 温湿度监测记录报表（事件）");
            w.WriteLine($"# 设备,{Esc(s.DeviceDisplayName)},区间,{Esc(s.RangeText)},共 {s.Events.Count} 条");
            w.WriteLine();
            w.WriteLine("发生时间,事件类型,严重程度,上下文");
            foreach (var e in s.Events)
            {
                ct.ThrowIfCancellationRequested();
                w.WriteLine(string.Join(",", e.OccurredAtLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                    Esc(e.EventTypeText), Esc(e.Severity), Esc(e.Context)));
            }
        }
        Replace(tmp, path);
    }

    /// <summary>数值：null（缺测）输出空串；负数保持数值形态，不加单引号。</summary>
    private static string Num(double? v) =>
        v.HasValue ? v.Value.ToString("0.####", CultureInfo.InvariantCulture) : "";

    /// <summary>文本转义 + 防公式注入：以 = + - @ 或制表符开头的文本加前置单引号。</summary>
    private static string Esc(string? text)
    {
        var s = text ?? "";
        if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' || s[0] == '\t'))
            s = "'" + s;
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static void Replace(string tmp, string target)
    {
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { /* 清理失败不影响结果判定 */ }
    }
}