using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ThermoHygro.App;

/// <summary>屏幕信息（用于四角定位与可见性纠正）。</summary>
public sealed class ScreenInfo
{
    public required string DeviceName { get; init; }
    public required bool IsPrimary { get; init; }
    public required Rect Bounds { get; init; }
    /// <summary>工作区（已排除任务栏），悬浮窗必须以此定位。</summary>
    public required Rect WorkArea { get; init; }

    public string Describe() => $"{(IsPrimary ? "主" : "副")} {DeviceName} 工作区 {WorkArea.Width:F0}×{WorkArea.Height:F0} @({WorkArea.X:F0},{WorkArea.Y:F0})";
}

/// <summary>
/// 屏慕枚举与悬浮窗定位。要求：
/// ①以工作区定位，避免任务栏遮挡；
/// ②处理 100/150/200% 缩放（WPF 的 Rect 已是 DIP，直接使用）；
/// ③支持负坐标副屏；
/// ④拔掉显示器或分辨率变化后自动回到可见区域。
/// </summary>
public static class ScreenLocator
{
    public static List<ScreenInfo> Enumerate()
    {
        var result = new List<ScreenInfo>();
        try
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                result.Add(new ScreenInfo
                {
                    DeviceName = s.DeviceName,
                    IsPrimary = s.Primary,
                    Bounds = ToRect(s.Bounds),
                    WorkArea = ToRect(s.WorkingArea)
                });
            }
        }
        catch { }
        if (result.Count == 0)
        {
            // 兜底：至少返回主屏，避免无显示器枚举导致悬浮窗消失
            var w = SystemParameters.PrimaryScreenWidth;
            var h = SystemParameters.PrimaryScreenHeight;
            result.Add(new ScreenInfo
            {
                DeviceName = "PRIMARY",
                IsPrimary = true,
                Bounds = new Rect(0, 0, w, h),
                WorkArea = new Rect(0, 0, w, h)
            });
        }
        return result;
    }

    private static Rect ToRect(System.Drawing.Rectangle r) =>
        new(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));

    public static ScreenInfo? FindByName(string? deviceName)
    {
        var all = Enumerate();
        if (string.IsNullOrWhiteSpace(deviceName))
            return all.FirstOrDefault(s => s.IsPrimary) ?? all.FirstOrDefault();
        return all.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(s => s.IsPrimary)
               ?? all.FirstOrDefault();
    }

    /// <summary>
    /// 计算悬浮窗左上角坐标（DIP）。角落由 corner 指定，边距可调，全部基于工作区。
    /// </summary>
    public static Point ComputePosition(string corner, ScreenInfo screen, Size size, double marginX, double marginY)
    {
        var wa = screen.WorkArea;
        double x, y;
        bool right = corner?.EndsWith("Right", StringComparison.OrdinalIgnoreCase) == true;
        bool bottom = corner?.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase) == true;
        bool center = string.Equals(corner, "Center", StringComparison.OrdinalIgnoreCase);

        if (center)
        {
            x = wa.X + (wa.Width - size.Width) / 2;
            y = wa.Y + (wa.Height - size.Height) / 2;
        }
        else
        {
            x = right ? wa.Right - size.Width - marginX : wa.X + marginX;
            y = bottom ? wa.Bottom - size.Height - marginY : wa.Y + marginY;
        }
        return new Point(x, y);
    }

    /// <summary>
    /// 可见性纠正：若窗口不在任何屏幕工作区内（拔屏/分辨率变化），移回主屏可见区。
    /// 返回 null 表示无需移动。
    /// </summary>
    public static Point? ClampToVisible(Point desired, Size size, IReadOnlyList<ScreenInfo> screens)
    {
        const double MinVisible = 40; // 至少露出这么多像素，保证可抓取
        foreach (var s in screens)
        {
            var wa = s.WorkArea;
            var inter = Rect.Intersect(new Rect(desired.X, desired.Y, size.Width, size.Height), wa);
            if (!inter.IsEmpty && inter.Width >= MinVisible && inter.Height >= MinVisible)
                return null;
        }
        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
        if (primary is null) return null;
        var wa2 = primary.WorkArea;
        double nx = Math.Min(Math.Max(desired.X, wa2.X), Math.Max(wa2.X, wa2.Right - size.Width));
        double ny = Math.Min(Math.Max(desired.Y, wa2.Y), Math.Max(wa2.Y, wa2.Bottom - size.Height));
        return new Point(nx, ny);
    }
}