using System;
using System.Windows;
using System.Windows.Interop;

namespace ThermoHygro.App;

/// <summary>
/// 悬浮窗的 Windows 互操作：鼠标穿透（WS_EX_TRANSPARENT）、
/// 不抢焦点（WS_EX_NOACTIVATE）、工具窗口（不占 Alt+Tab）。
/// </summary>
internal static class OverlayInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void ApplyClickThrough(Window w, bool clickThrough)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int style = GetWindowLong(h, GWL_EXSTYLE);
        int updated = clickThrough ? (style | WS_EX_TRANSPARENT) : (style & ~WS_EX_TRANSPARENT);
        updated |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
        SetWindowLong(h, GWL_EXSTYLE, updated);
    }

    /// <summary>置顶：处理独占全屏等无法覆盖的情形不做注入，仅设置 Topmost。</summary>
    public static void ApplyTopmost(Window w, bool topmost)
    {
        try { w.Topmost = topmost; } catch { }
    }
}