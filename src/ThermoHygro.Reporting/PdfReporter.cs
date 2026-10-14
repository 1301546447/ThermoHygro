using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ThermoHygro.Core;

namespace ThermoHygro.Reporting;

/// <summary>
/// 中文字体解析器。PDFsharp 6.x 要求解析器为每种风格提供稳定的 face 名称，
/// 且 GetFont 必须返回可用字体字节（否则 XFont 构造会抛 NullReferenceException）。
/// 因此这里把常规/粗体分别映射到实际存在的字体文件，缺失时退到可用字体。
/// </summary>
public sealed class SystemCjkFontResolver : IFontResolver
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly byte[]? BundledFont = ReadBundledFont();

    private static byte[]? ReadBundledFont()
    {
        using var stream = typeof(SystemCjkFontResolver).Assembly.GetManifestResourceStream("ThermoHygro.Reporting.Fonts.CjkRegular.ttf");
        if (stream is null) return null;
        using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
    }

    private const string RegularFace = "cjk-regular";
    private const string BoldFace = "cjk-bold";

    public string Status { get; }

    public SystemCjkFontResolver()
    {
        if (BundledFont is not null)
        {
            Status = "已使用随程序提供的中文字体（开放字体许可证，无需另外安装字体，PDF 文字可复制、可搜索）";
            return;
        }
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string? regular = null, bold = null;
        var found = new List<string>();

        foreach (var (name, file) in new (string, string)[]
        {
            ("黑体", "simhei.ttf"),          // 标准 TTF，解析最可靠
            ("等线", "Deng.ttf"),             // 标准 TTF
            ("宋体", "simsun.ttc"),
            ("微软雅黑", "msyh.ttc"),
            ("微软雅黑 Bold", "msyhbd.ttc"),
        })
        {
            var path = Path.Combine(fontsDir, file);
            if (!File.Exists(path)) continue;
            found.Add(name);
            if (file.Contains("bd", StringComparison.OrdinalIgnoreCase) && bold is null) bold = path;
            else if (regular is null) regular = path;
        }

        if (regular is not null) _files[RegularFace] = regular;
        if (bold is not null) _files[BoldFace] = bold;

        // 没有专门粗体时用常规字体兼任，保证 XFont(bold) 也能构造成功
        if (_files.ContainsKey(RegularFace) && !_files.ContainsKey(BoldFace))
            _files[BoldFace] = _files[RegularFace];

        Status = _files.Count > 0
            ? "已使用系统中文字体：" + string.Join("、", found) + "（PDF 文字可复制、可搜索）"
            : "未找到可用中文字体，PDF 中文可能无法正确显示；请安装微软雅黑或宋体后重试。";
    }

    public bool HasFont => BundledFont is not null || _files.Count > 0;

    /// <summary>解析器使用的 face 名（PDFsharp 必须能通过它取到字节）。</summary>
    public string DefaultFaceName => RegularFace;

    public byte[]? GetFont(string faceName)
    {
        if (BundledFont is not null) return BundledFont;
        if (!_files.TryGetValue(faceName, out var path))
            path = _files.TryGetValue(RegularFace, out var r) ? r
                 : _files.Values.FirstOrDefault();
        if (path is null || !File.Exists(path)) return null;

        // TTC（字体集合）内部的表偏移基于整个文件，不能简单截取，否则解析必然失败；
        // 因此仅对真正的 TTC 做完整提取，TTF 原样返回。
        var bytes = File.ReadAllBytes(path);
        return Path.GetExtension(path).Equals(".ttc", StringComparison.OrdinalIgnoreCase)
            ? TtcUtil.ExtractFirstFont(bytes)
            : bytes;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (!HasFont) return null;
        return new FontResolverInfo(bold && _files.ContainsKey(BoldFace) ? BoldFace : RegularFace);
    }
}

/// <summary>
/// 从 TTC（字体集合）中提取第一个字体，重建为可独立解析的 TTF/OTF。
/// 不能只按偏移截取：TTC 内每个字体的表记录里存的是相对整个集合文件的绝对偏移，
/// 直接截取会让所有表位置错位，解析必然失败。因此这里重建表目录并重排表数据。
/// 非 TTC 数据原样返回。
/// </summary>
internal static class TtcUtil
{
    public static byte[] ExtractFirstFont(byte[] data)
    {
        // TTC 头：'ttcf' + 主次版本 + 字体数 + 各字体表目录偏移
        if (data.Length < 16 || data[0] != 0x74 || data[1] != 0x74 || data[2] != 0x63 || data[3] != 0x66)
            return data;

        try
        {
            int numFonts = ReadInt32(data, 8);
            if (numFonts <= 0 || data.Length < 12 + numFonts * 4) return data;
            int fontOffset = ReadInt32(data, 12);
            if (fontOffset <= 0 || fontOffset >= data.Length) return data;

            int numTables = ReadUInt16(data, fontOffset + 4);
            if (numTables <= 0 || fontOffset + 12 + numTables * 16 > data.Length) return data;

            // 收集表记录（tag/校验和/原偏移/长度）
            var records = new (uint Tag, uint Checksum, int Offset, int Length)[numTables];
            for (int i = 0; i < numTables; i++)
            {
                int rec = fontOffset + 12 + i * 16;
                uint tag = ReadUInt32(data, rec);
                uint checksum = ReadUInt32(data, rec + 4);
                int offset = ReadInt32(data, rec + 8);
                int length = ReadInt32(data, rec + 12);
                if (offset < 0 || length < 0 || offset + length > data.Length) return data;
                records[i] = (tag, checksum, offset, length);
            }

            // 新布局：sfnt 头 + 表目录，随后依次存放各表数据（4 字节对齐）
            int dirSize = 12 + numTables * 16;
            int total = dirSize;
            foreach (var r in records) total += (r.Length + 3) & ~3;
            var result = new byte[total];

            // 复制 sfnt 头（版本与搜索参数保持原值）
            Buffer.BlockCopy(data, fontOffset, result, 0, 12);

            int cursor = dirSize;
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                WriteUInt32(result, rec, records[i].Tag);
                WriteUInt32(result, rec + 4, records[i].Checksum);
                WriteUInt32(result, rec + 8, (uint)cursor);
                WriteUInt32(result, rec + 12, (uint)records[i].Length);
                Buffer.BlockCopy(data, records[i].Offset, result, cursor, records[i].Length);
                cursor += (records[i].Length + 3) & ~3;
            }
            return result;
        }
        catch
        {
            // 提取失败时原样返回：由上层把“字体不可用”如实暴露，不静默产出乱码
            return data;
        }
    }

    private static int ReadUInt16(byte[] d, int i) => (d[i] << 8) | d[i + 1];

    private static uint ReadUInt32(byte[] d, int i) =>
        ((uint)d[i] << 24) | ((uint)d[i + 1] << 16) | ((uint)d[i + 2] << 8) | d[i + 3];

    private static void WriteUInt32(byte[] d, int i, uint v)
    {
        d[i] = (byte)(v >> 24); d[i + 1] = (byte)(v >> 16);
        d[i + 2] = (byte)(v >> 8); d[i + 3] = (byte)v;
    }

    private static int ReadInt32(byte[] d, int i) => (int)ReadUInt32(d, i);
}

/// <summary>
/// PDF 报告：A4 横向，封面/总览 + 逐月页，含设备地点、区间、生成时间、查询截止点、时区、页码、
/// 统计与缺测说明；文字为矢量文本（可选择复制），曲线按数据实际范围缩放。
/// 先写临时文件，成功后替换；失败或取消不留下半成品。
/// </summary>
public sealed class PdfReporter : IPdfReporter
{
    private static readonly object Gate = new();
    /// <summary>进程内实际生效的解析器。GlobalFontSettings.FontResolver 只能设置一次，
    /// 界面显示的字体状态必须取这一份，否则会与实际出字用的字体不一致。</summary>
    private static SystemCjkFontResolver? _active;

    private readonly SystemCjkFontResolver _resolver;

    /// <summary>实际生效解析器的状态（若尚未有实例导出过 PDF，则为当前实例的状态）。</summary>
    public string FontStatus => (_active ?? _resolver).Status;

    public PdfReporter()
    {
        _resolver = new SystemCjkFontResolver();
    }

    public Task<ExportResult> ExportAsync(ReportSnapshot snapshot, string outputFilePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // 同理：不把 ct 交给 Task.Run，避免已取消时抛 TaskCanceledException
        return Task.Run(() =>
        {
            if (ct.IsCancellationRequested)
                return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };
            var tmp = outputFilePath + ".tmp.pdf";
            try
            {
                if (!_resolver.HasFont)
                    return new ExportResult { Success = false, Error = _resolver.Status };

                lock (Gate)
                {
                    if (_active is null)
                    {
                        GlobalFontSettings.FontResolver = _resolver;
                        _active = _resolver;
                    }
                }
                // 所有实例的 face 名都是同一组常量，出字始终可用；
                // 但若生效的解析器没有字体，必须如实失败而不是产出中文乱码的 PDF。
                if (!_active!.HasFont)
                    return new ExportResult { Success = false, Error = _active.Status };

                using var doc = new PdfDocument();
                doc.Info.Title = "温湿度监测记录报表";
                doc.Info.Subject = snapshot.RangeText;

                WriteCover(doc, snapshot);
                WriteMonthlyPages(doc, snapshot, progress, ct);

                if (ct.IsCancellationRequested)
                    return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };

                doc.Save(tmp);
                if (File.Exists(outputFilePath)) File.Delete(outputFilePath);
                File.Move(tmp, outputFilePath);

                return new ExportResult
                {
                    Success = true,
                    FilePath = outputFilePath,
                    RowCount = snapshot.Points.Count,
                    Files = new[] { outputFilePath }
                };
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                return new ExportResult { Success = false, Canceled = true, Error = "已取消导出。" };
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                return new ExportResult { Success = false, Error = ex.Message };
            }
        });
    }

    private const double W = 842;   // A4 横向宽 (pt)
    private const double H = 595;   // A4 横向高
    private const double Margin = 42;

    // 不使用 XBrushes 静态属性：其在部分运行时下静态初始化失败（NullReferenceException），
    // 统一用显式画刷，行为可预测。
    private static XBrush Black => new XSolidBrush(XColor.FromArgb(20, 20, 20));
    private static XBrush Gray => new XSolidBrush(XColor.FromArgb(120, 120, 120));
    private static XBrush DimGray => new XSolidBrush(XColor.FromArgb(90, 90, 90));
    private static XBrush Red => new XSolidBrush(XColor.FromArgb(178, 34, 34));

    private XFont Font(double size) =>
        new(_resolver.DefaultFaceName, size, XFontStyleEx.Regular);

    private void WriteCover(PdfDocument doc, ReportSnapshot s)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(W); page.Height = XUnit.FromPoint(H);
        using var gfx = XGraphics.FromPdfPage(page);

        double y = 96;
        gfx.DrawString("温湿度监测记录报表", Font(28), Black,
            new XRect(0, y, W, 40), XStringFormats.TopCenter);
        y += 52;
        gfx.DrawString($"{s.DeviceDisplayName}　{s.DeviceLocation}", Font(14), DimGray,
            new XRect(0, y, W, 24), XStringFormats.TopCenter);

        y += 60;
        var lines = new List<string>
        {
            $"统计区间：{s.RangeText}",
            $"数据粒度：{s.Query.GranularityText}",
            $"报表时区：{s.Query.TimeZoneId}（UTC{(s.Query.UtcOffsetMinutes >= 0 ? "+" : "")}{s.Query.UtcOffsetMinutes / 60}）",
            $"查询截止点（UTC）：{s.Query.CutoffUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}",
            $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"阈值说明：{(s.ThresholdNote.Length == 0 ? "未配置（旧记录不追溯标红）" : s.ThresholdNote)}",
            "",
            "【区间统计】",
            $"温度：均值 {Fmt(s.Stats.TemperatureAvg)} ℃，最低 {Fmt(s.Stats.TemperatureMin)} ℃，最高 {Fmt(s.Stats.TemperatureMax)} ℃，有效样本 {s.Stats.TemperatureValidCount} 次",
            $"湿度：均值 {Fmt(s.Stats.HumidityAvg)} %RH，最低 {Fmt(s.Stats.HumidityMin)} %RH，最高 {Fmt(s.Stats.HumidityMax)} %RH，有效样本 {s.Stats.HumidityValidCount} 次",
            $"分桶数 {s.Stats.BucketCount}；整段缺测 {s.Stats.GapBucketCount}；部分缺测 {s.Stats.PartialGapBucketCount}；时间覆盖率 {s.Stats.CoverageRatio:P1}",
            $"超限次数：温度 {s.Stats.TempOutOfRangeCount}，湿度 {s.Stats.HumidityOutOfRangeCount}；事件 {s.Stats.EventCount} 条；错过时隙 {s.Stats.MissedSlots}",
            "",
            "【缺测说明】",
            "缺测记录为空值，不以 0 代替；整段无数据的时段在曲线中断线，不做插值伪造。",
            "统计采用总和÷有效次数加权，不对已舍入的均值再次平均。",
        };

        foreach (var line in lines)
        {
            var f = line.StartsWith("【") ? Font(12.5) : Font(10.5);
            gfx.DrawString(line, f, Black, new XRect(Margin, y, W - Margin * 2, 16), XStringFormats.TopLeft);
            y += line.StartsWith("【") ? 22 : (line.Length == 0 ? 10 : 17);
        }
    }

    private void WriteMonthlyPages(PdfDocument doc, ReportSnapshot s, IProgress<int>? progress, CancellationToken ct)
    {
        // 按自然月分组，每月一页；无数据也生成页并明确标注（不伪装成正常曲线）
        var groups = s.Points
            .GroupBy(p => new { p.BucketStartLocal.Year, p.BucketStartLocal.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .ToList();

        if (groups.Count == 0)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(W); page.Height = XUnit.FromPoint(H);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{s.DeviceDisplayName} — {s.RangeText}", Font(13), Black,
                new XRect(Margin, 22, W - Margin * 2, 20), XStringFormats.TopLeft);
            gfx.DrawString("所选区间内没有任何记录（空结果，不伪造正常曲线）。", Font(11), Red,
                new XRect(Margin, H / 2 - 10, W - Margin * 2, 20), XStringFormats.TopLeft);
            gfx.DrawString("第 1 页（共 1 页）", Font(8.5), Gray,
                new XRect(0, H - 26, W - Margin, 14), XStringFormats.TopRight);
            progress?.Report(1);
            return;
        }

        int pageNo = 2;
        int done = 0;
        foreach (var grp in groups)
        {
            ct.ThrowIfCancellationRequested();
            var pts = grp.OrderBy(p => p.BucketStartLocal).ToList();
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(W); page.Height = XUnit.FromPoint(H);
            using var gfx = XGraphics.FromPdfPage(page);

            int year = pts.Count > 0 ? pts[0].BucketStartLocal.Year : s.Query.StartLocal.Year;
            int month = pts.Count > 0 ? pts[0].BucketStartLocal.Month : s.Query.StartLocal.Month;

            gfx.DrawString($"{s.DeviceDisplayName} — {year} 年 {month} 月", Font(13), Black,
                new XRect(Margin, 22, W - Margin * 2, 20), XStringFormats.TopLeft);
            gfx.DrawString($"{s.RangeText}｜粒度 {s.Query.GranularityText}｜快照 {s.Query.CutoffUtc.ToUniversalTime():yyyy-MM-dd HH:mm} UTC",
                Font(8.5), DimGray, new XRect(Margin, 40, W - Margin * 2, 14), XStringFormats.TopLeft);

            if (pts.Count == 0 || pts.All(p => p.IsGap))
            {
                gfx.DrawString("本月无有效数据（整段缺测，未绘制曲线，也不以 0 填补）。", Font(11), Red,
                    new XRect(Margin, H / 2 - 10, W - Margin * 2, 20), XStringFormats.TopLeft);
            }
            else
            {
                DrawChart(gfx, pts, 80, H * 0.30, true);
                DrawChart(gfx, pts, 80 + H * 0.39, H * 0.30, false);
            }

            gfx.DrawString($"第 {pageNo} 页（共 {groups.Count + 1} 页）", Font(8.5), Gray,
                new XRect(0, H - 26, W - Margin, 14), XStringFormats.TopRight);
            gfx.DrawString("缺测断线；统计按总和÷有效次数加权；文字可复制。", Font(8), Gray,
                new XRect(Margin, H - 26, W - Margin * 2, 14), XStringFormats.TopLeft);

            pageNo++;
            progress?.Report(++done);
        }
    }

    /// <summary>均值与极值、采集时间刻度；单点明确标记，缺测处断线。</summary>
    private void DrawChart(XGraphics gfx, List<HistoryPoint> pts, double top, double height, bool isTemp)
    {
        double left = Margin + 34;
        double right = W - Margin - 8;
        double width = right - left;

        double? min = null, max = null;
        foreach (var p in pts)
        {
            var lo = isTemp ? p.TemperatureMin : p.HumidityMin;
            var hi = isTemp ? p.TemperatureMax : p.HumidityMax;
            if (lo.HasValue && (min is null || lo.Value < min.Value)) min = lo;
            if (hi.HasValue && (max is null || hi.Value > max.Value)) max = hi;
        }
        if (min is null || max is null)
        {
            gfx.DrawString((isTemp ? "温度" : "湿度") + "：没有有效数据，缺测未以 0 填补。", Font(10), Red,
                new XRect(Margin, top + height / 2, width, 18), XStringFormats.TopLeft);
            return;
        }
        if (max.Value - min.Value < 1) { min -= 0.5; max += 0.5; }
        var pad = (max.Value - min.Value) * 0.12;
        double lo0 = min.Value - pad, hi0 = max.Value + pad;

        string unit = isTemp ? "℃" : "%RH";
        var title = isTemp ? "温度曲线" : "湿度曲线";
        gfx.DrawString($"{title}（{unit}）", Font(10), Black,
            new XRect(Margin, top - 14, 200, 14), XStringFormats.TopLeft);

        // 坐标框
        gfx.DrawRectangle(XPens.LightGray, left, top, width, height);
        // 网格与刻度
        for (int i = 0; i <= 4; i++)
        {
            double yy = top + height * i / 4.0;
            gfx.DrawLine(new XPen(XColor.FromArgb(240, 240, 240), 0.6), left, yy, right, yy);
            double val = hi0 - (hi0 - lo0) * i / 4.0;
            gfx.DrawString(val.ToString("0.0", CultureInfo.InvariantCulture), Font(7.5), Gray,
                new XRect(Margin, yy - 6, 32, 12), XStringFormats.TopRight);
        }

        double X(int i) => left + (pts.Count <= 1 ? width / 2 : width * i / (pts.Count - 1.0));
        double Y(double v) => top + height - height * (v - lo0) / (hi0 - lo0);
        int tickCount = Math.Min(5, pts.Count);
        var ticks = new HashSet<int>();
        for (int tick = 0; tick < tickCount; tick++)
        {
            int index = tickCount == 1 ? 0 : (int)Math.Round(tick * (pts.Count - 1.0) / (tickCount - 1));
            if (!ticks.Add(index)) continue;
            string format = pts[^1].BucketStartLocal - pts[0].BucketStartLocal >= TimeSpan.FromDays(2) ? "MM-dd" : "HH:mm";
            gfx.DrawString(pts[index].BucketStartLocal.ToString(format), Font(7.5), Gray,
                new XRect(X(index) - 22, top + height + 5, 44, 12), XStringFormats.TopCenter);
        }

        // 极值带（半透明灰）
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            var alo = isTemp ? a.TemperatureMin : a.HumidityMin;
            var ahi = isTemp ? a.TemperatureMax : a.HumidityMax;
            var blo = isTemp ? b.TemperatureMin : b.HumidityMin;
            var bhi = isTemp ? b.TemperatureMax : b.HumidityMax;
            if (alo is null || ahi is null || blo is null || bhi is null) continue;
            var pen = new XPen(XColor.FromArgb(120, 150, 170, 200), 1.2);
            gfx.DrawLine(pen, X(i - 1), Y(ahi.Value), X(i), Y(bhi.Value));
            gfx.DrawLine(pen, X(i - 1), Y(alo.Value), X(i), Y(blo.Value));
        }

        // 均值折线：缺测断开
        var color = isTemp ? XColor.FromArgb(220, 70, 110) : XColor.FromArgb(60, 130, 190);
        var linePen = new XPen(color, 1.6);
        XPoint? prev = null;
        for (int i = 0; i < pts.Count; i++)
        {
            var v = isTemp ? pts[i].TemperatureAvg : pts[i].HumidityAvg;
            if (v is null) { prev = null; continue; }
            var cur = new XPoint(X(i), Y(v.Value));
            if (prev.HasValue) gfx.DrawLine(linePen, prev.Value, cur);
            gfx.DrawEllipse(new XSolidBrush(color), cur.X - 2.2, cur.Y - 2.2, 4.4, 4.4);
            if (pts.Count == 1)
            {
                double low = (isTemp ? pts[i].TemperatureMin : pts[i].HumidityMin) ?? v.Value;
                double high = (isTemp ? pts[i].TemperatureMax : pts[i].HumidityMax) ?? v.Value;
                gfx.DrawLine(linePen, cur.X, Y(low), cur.X, Y(high));
                gfx.DrawString($"{v.Value:0.0} {unit}", Font(8.5), Black,
                    new XRect(cur.X + 7, cur.Y - 16, 120, 15), XStringFormats.TopLeft);
            }
            prev = cur;
        }
    }

    private static string Fmt(double? v) =>
        v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "无数据";

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
