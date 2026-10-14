using System;
using System.Windows;
using System.Windows.Forms;

namespace ThermoHygro.App;

/// <summary>
/// 托盘图标与菜单。提供：打开窗口 / 显示隐藏悬浮 / 开始暂停 / 退出。
/// 托盘始终可以关闭鼠标穿透并恢复悬浮窗位置，避免"无法操作"的死局。
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _toggleOverlayItem;
    private readonly ToolStripMenuItem _toggleAcquireItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _resetPosItem;
    private readonly ToolStripMenuItem _exitItem;

    public event Action? OpenRequested;
    public event Action? ToggleOverlayRequested;
    public event Action? ToggleAcquireRequested;
    public event Action<bool>? ClickThroughChanged;
    public event Action? ResetPositionRequested;
    public event Action? ExitRequested;

    public TrayManager()
    {
        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Visible = true,
            Text = "温湿度监测记录"
        };

        var menu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem("打开主窗口");
        _openItem.Click += (_, _) => OpenRequested?.Invoke();

        _toggleOverlayItem = new ToolStripMenuItem("隐藏悬浮显示") { CheckOnClick = false };
        _toggleOverlayItem.Click += (_, _) => ToggleOverlayRequested?.Invoke();

        _toggleAcquireItem = new ToolStripMenuItem("暂停采集") { CheckOnClick = false };
        _toggleAcquireItem.Click += (_, _) => ToggleAcquireRequested?.Invoke();

        _clickThroughItem = new ToolStripMenuItem("悬浮窗鼠标穿透") { CheckOnClick = true, Checked = true };
        _clickThroughItem.Click += (_, _) => ClickThroughChanged?.Invoke(_clickThroughItem.Checked);

        _resetPosItem = new ToolStripMenuItem("恢复悬浮窗位置");
        _resetPosItem.Click += (_, _) => ResetPositionRequested?.Invoke();

        _exitItem = new ToolStripMenuItem("退出（停止采集）");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleOverlayItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_resetPosItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleAcquireItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void SetOverlayVisible(bool visible) =>
        _toggleOverlayItem.Text = visible ? "隐藏悬浮显示" : "显示悬浮显示";

    public void SetAcquiring(bool acquiring) =>
        _toggleAcquireItem.Text = acquiring ? "暂停采集" : "开始采集";

    public void SetClickThroughChecked(bool value)
    {
        _clickThroughItem.Checked = value;
    }

    public void SetStatus(string text)
    {
        try
        {
            // NotifyIcon.Text 上限 63 字符
            _icon.Text = text.Length > 60 ? text[..60] : text;
        }
        catch { }
    }

    public void ShowBalloon(string title, string body)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = body;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(6000);
        }
        catch { }
    }

    /// <summary>生成一个简单图标（避免依赖外部 .ico 资源）。</summary>
    private static System.Drawing.Icon CreateIcon()
    {
        try
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 30, 90, 160));
                g.FillEllipse(bg, 1, 1, 30, 30);
                using var fg = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                g.FillRectangle(fg, 15, 6, 4, 20);
                using var bulb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 230, 60, 60));
                g.FillEllipse(bulb, 11, 3, 12, 12);
            }
            var h = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(h);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }
    }
}