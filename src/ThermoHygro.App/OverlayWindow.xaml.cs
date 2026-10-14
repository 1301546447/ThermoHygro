using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;

namespace ThermoHygro.App;

/// <summary>
/// 透明置顶悬浮读数窗。
/// 关键行为：
/// ①默认透明背景 + 不透明数字（背景与文字透明度分开）；
/// ②置顶、不抢焦点、可鼠标穿透；
/// ③四角/显示器/边距，以工作区定位避开任务栏；
/// ④过期显示 --（由独立 DispatcherTimer 判定，采集线程卡死也能变 --）；
/// ⑤本地保存失败显示"未保存"。
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _freshnessTimer;
    private OverlaySettings _settings = new();
    private AcquisitionSnapshot _last = new();
    private bool _clickThroughApplied;
    private string _dragCorner = "BottomRight";

    public OverlayWindow()
    {
        InitializeComponent();

        // 独立计时：即使后台采集卡死，也会按新鲜度窗口把读数标为过期
        _freshnessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _freshnessTimer.Tick += (_, _) => RefreshFromLast();
        _freshnessTimer.Start();

        Loaded += (_, _) => { ApplyClickThrough(); RepositionSafe(); };
        SystemEvents_DisplayChangedHook();
    }

    /// <summary>
    /// 拖动结束后的位置变更事件（参数：Corner / MarginX / MarginY）。
    /// 主程序据此把新位置写回设置，否则用户拖动的悬浮窗位置在重启后会丢失。
    /// </summary>
    public event Action<string, double, double>? DragPositionChanged;

    /// <summary>新鲜度窗口（由主程序按采集间隔与超时值设置）。</summary>
    public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>允许拖动（关闭穿透时）以调整位置。</summary>
    public void SetDraggable(bool draggable)
    {
        _draggable = draggable;
    }
    private bool _draggable;

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_draggable && !_settings.ClickThrough)
        {
            try
            {
                DragMove();
                // 拖动结束：把新位置换算为“最近的角 + 边距”并上报，保证重启后位置不丢
                ReportPositionAfterDrag();
            }
            catch { }
        }
        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>
    /// 把当前实际位置换算为设置可保存的形式。
    /// 保持用户当前的角不变（不因拖动到屏幕中部就强行改角），只更新边距；
    /// 若拖到另一侧且超出屏幕中线，则自动切换到更贴近的那个角，避免下次重启时位置跳回。
    /// </summary>
    private void ReportPositionAfterDrag()
    {
        try
        {
            var screen = ScreenLocator.FindByName(_settings.DisplayDeviceName) ?? ScreenLocator.Enumerate().FirstOrDefault(s => s.IsPrimary);
            if (screen is null) return;
            var wa = screen.WorkArea;
            double centerX = wa.Left + wa.Width / 2;
            double centerY = wa.Top + wa.Height / 2;
            double cx = Left + ActualWidth / 2;
            double cy = Top + ActualHeight / 2;

            bool right = cx >= centerX;
            bool bottom = cy >= centerY;
            var corner = (bottom ? "Bottom" : "Top") + (right ? "Right" : "Left");
            double marginX = right ? Math.Max(0, wa.Right - (Left + ActualWidth)) : Math.Max(0, Left - wa.Left);
            double marginY = bottom ? Math.Max(0, wa.Bottom - (Top + ActualHeight)) : Math.Max(0, Top - wa.Top);

            _settings.Corner = corner;
            _settings.MarginX = Math.Round(marginX, 0);
            _settings.MarginY = Math.Round(marginY, 0);
            DragPositionChanged?.Invoke(corner, _settings.MarginX, _settings.MarginY);
        }
        catch { }
    }

    private SystemEventsWatcher? _watcher;

    private void SystemEvents_DisplayChangedHook()
    {
        try
        {
            _watcher = new SystemEventsWatcher(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                RepositionSafe();
                RefreshFromLast();
            })));
        }
        catch { }
    }

    /// <summary>应用显示设置并立即刷新（供预览即时生效）。</summary>
    public void ApplySettings(OverlaySettings s, bool persist = false)
    {
        _settings = s.Clone();
        _dragCorner = _settings.Corner;

        double size = Math.Clamp(_settings.FontSize, 16, 96);
        TempText.FontSize = size;
        HumText.FontSize = size;
        SepText.FontSize = Math.Max(12, size * 0.56);

        TempText.Foreground = ParseBrush(_settings.TemperatureColor, "#E53935");
        HumText.Foreground = ParseBrush(_settings.HumidityColor, "#1E88E5");
        StatusText.Foreground = ParseBrush(_settings.TextColor, "#FFFFFF", 0.75);
        DeviceText.Foreground = ParseBrush(_settings.TextColor, "#FFFFFF", 0.55);

        // 背景透明度与文字透明度分开
        byte bgAlpha = (byte)Math.Round(Math.Clamp(_settings.BackgroundOpacity, 0, 1) * 255);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 16, 20, 24));
        RootBorder.Opacity = 1.0;

        var textOpacity = Math.Clamp(_settings.TextOpacity, 0.15, 1.0);
        TempText.Opacity = textOpacity;
        HumText.Opacity = textOpacity;
        SepText.Opacity = textOpacity;

        // 文字阴影保证浅色背景下可读
        if (_settings.ShadowEnabled)
        {
            var eff = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9 };
            TempText.Effect = eff;
            HumText.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9 };
        }
        else
        {
            TempText.Effect = null;
            HumText.Effect = null;
        }

        TempText.Visibility = _settings.ShowTemperature ? Visibility.Visible : Visibility.Collapsed;
        HumText.Visibility = _settings.ShowHumidity ? Visibility.Visible : Visibility.Collapsed;
        SepText.Visibility = (_settings.ShowTemperature && _settings.ShowHumidity) ? Visibility.Visible : Visibility.Collapsed;
        DeviceText.Visibility = _settings.ShowDeviceLabel ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = _settings.ShowStatusText ? Visibility.Visible : Visibility.Collapsed;

        Topmost = true;
        ApplyClickThrough();
        RepositionSafe();
        RefreshFromLast();

        if (persist) ConfigStore.SaveOverlay(_settings);
    }

    private static SolidColorBrush ParseBrush(string value, string fallback, double shapeOpacity = 1.0)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(value);
            return new SolidColorBrush(c) { Opacity = shapeOpacity };
        }
        catch
        {
            return new SolidColorBrush(((Color)ColorConverter.ConvertFromString(fallback))) { Opacity = shapeOpacity };
        }
    }

    public void ApplyClickThrough()
    {
        if (!IsLoaded) { _clickThroughApplied = false; _appliedClickThrough = false; return; }
        // 同一状态不重复调用 Win32（穿透设置频繁触发的场景下避免无意义开销）
        if (_clickThroughApplied && _appliedClickThrough == _settings.ClickThrough) return;
        try
        {
            OverlayInterop.ApplyClickThrough(this, _settings.ClickThrough);
            _clickThroughApplied = true;
            _appliedClickThrough = _settings.ClickThrough;
        }
        catch { }
    }

    /// <summary>已实际应用过的穿透状态（与 _clickThroughApplied 配合避免重复调用）。</summary>
    private bool _appliedClickThrough;

    /// <summary>更新读数显示（由界面线程调用）。</summary>
    public void Update(AcquisitionSnapshot snap)
    {
        _last = snap;
        RefreshFromLast();
    }

    /// <summary>
    /// 用独立计时重新评估新鲜度并渲染。这里不依赖采集线程是否存活，
    /// 因此后台卡死也能在新鲜度窗口后显示 --。
    /// </summary>
    private void RefreshFromLast()
    {
        var snap = _last;
        var now = DateTimeOffset.UtcNow;
        var window = FreshnessWindow;

        string temp = RenderMetric(snap.Reading.Temperature, "℃", window, now, out bool tempOk);
        string hum = RenderMetric(snap.Reading.Humidity, "%RH", window, now, out bool humOk);

        if (!_settings.ShowTemperature) TempText.Visibility = Visibility.Collapsed;
        if (!_settings.ShowHumidity) HumText.Visibility = Visibility.Collapsed;

        TempText.Text = temp;
        HumText.Text = hum;

        // 状态行：通信 / 本地保存 / 远端同步，三者独立，失败与过期不显示为正常
        var parts = new System.Collections.Generic.List<string>();
        parts.Add(DescribeCollector(snap.Collector));
        if (snap.Storage == StorageState.Fault) parts.Add("未保存");
        else if (snap.Storage == StorageState.Ok) parts.Add("已保存");
        if (snap.Remote == RemoteSyncState.Error) parts.Add("远端同步失败");
        else if (snap.Remote == RemoteSyncState.Pending) parts.Add("待同步");

        bool anyStale = IsStale(snap.Reading.Temperature, window, now) || IsStale(snap.Reading.Humidity, window, now);
        if (anyStale) parts.Insert(0, "读数过期");

        StatusText.Text = string.Join(" · ", parts);
        DeviceText.Text = string.IsNullOrWhiteSpace(snap.DeviceLabel) ? "" : snap.DeviceLabel;

        // 视觉提示：过期或失败时降低不透明度并加提示，不伪装成正常
        bool degraded = anyStale
            || snap.Collector is CollectorState.Disconnected or CollectorState.Reconnecting or CollectorState.PartiallyMissing
            || snap.Storage == StorageState.Fault;
        RootBorder.BorderBrush = degraded ? new SolidColorBrush(Color.FromArgb(200, 255, 152, 0)) : null;
        RootBorder.BorderThickness = degraded ? new Thickness(1.5) : new Thickness(0);
    }

    private static bool IsStale(MetricReading r, TimeSpan window, DateTimeOffset now)
    {
        if (r.Quality == MetricQuality.Stale) return true;
        if (r.Quality is not (MetricQuality.Good or MetricQuality.OutOfRange)) return false;
        return r.TimestampUtc.HasValue && now - r.TimestampUtc.Value > window;
    }

    private static string RenderMetric(MetricReading r, string unit, TimeSpan window, DateTimeOffset now, out bool ok)
    {
        switch (r.Quality)
        {
            case MetricQuality.NotSupported:
                ok = false;
                return $"-- {unit}";
            case MetricQuality.Good:
            case MetricQuality.OutOfRange:
                if (IsStale(r, window, now))
                {
                    ok = false;
                    return $"-- {unit}";
                }
                ok = true;
                return $"{r.Value:F1} {unit}";
            case MetricQuality.Stale:
                ok = false;
                return $"-- {unit}";
            case MetricQuality.Failed:
                ok = false;
                return $"-- {unit}";
            default:
                ok = false;
                return $"-- {unit}";
        }
    }

    private static string DescribeCollector(CollectorState s) => s switch
    {
        CollectorState.NotConfigured => "未配置",
        CollectorState.Disconnected => "未连接",
        CollectorState.Connecting => "连接中",
        CollectorState.Acquiring => "采集中",
        CollectorState.PartiallyMissing => "部分缺测",
        CollectorState.Reconnecting => "重连中",
        CollectorState.Paused => "已暂停",
        CollectorState.StorageFault => "存储故障",
        _ => "未知"
    };

    /// <summary>按设置重算位置；带可见性纠正，避免拔屏后消失。</summary>
    public void RepositionSafe()
    {
        try
        {
            var screens = ScreenLocator.Enumerate();
            // 指定的屏幕不存在时（拔屏/换设备/未指定）回退到主屏，
            // 否则悬浮窗会留在已消失的屏幕坐标上，用户看不到也无法拖动回来。
            var screen = ScreenLocator.FindByName(_settings.DisplayDeviceName)
                         ?? screens.FirstOrDefault(s => s.IsPrimary)
                         ?? screens.FirstOrDefault();
            if (screen is null) return;

            // 让 WPF 先完成布局测量，得到真实尺寸
            UpdateLayout();
            var size = new Size(Math.Max(ActualWidth, 60), Math.Max(ActualHeight, 30));
            var pos = ScreenLocator.ComputePosition(_settings.Corner, screen, size, _settings.MarginX, _settings.MarginY);

            var clamped = ScreenLocator.ClampToVisible(pos, size, screens);
            if (clamped.HasValue) pos = clamped.Value;

            Left = pos.X;
            Top = pos.Y;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"悬浮窗定位失败：{ex.Message}");
        }
    }

    public void Shutdown()
    {
        try { _freshnessTimer.Stop(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        try { Close(); } catch { }
    }
}

/// <summary>显示设置变化监听（显示器拔插、分辨率变化）。</summary>
internal sealed class SystemEventsWatcher : IDisposable
{
    private readonly EventHandler _handler;
    private bool _subscribed;

    public SystemEventsWatcher(Action onChanged)
    {
        _handler = (_, _) => { try { onChanged(); } catch { } };
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _handler;
            _subscribed = true;
        }
        catch { }
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _handler; } catch { }
        _subscribed = false;
    }
}