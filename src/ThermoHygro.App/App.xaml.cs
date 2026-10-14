using System;
using System.Windows;
using ThermoHygro.Core;
using ThermoHygro.Infrastructure;

namespace ThermoHygro.App;

public partial class App : Application
{
    private MainWindow? _main;
    private TrayManager? _tray;
    private bool _exiting;

    public AppConfiguration Configuration { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var uiIndex = Array.FindIndex(e.Args, argument => argument == "--ui-check");
        if (uiIndex >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string directory = e.Args.Length > uiIndex + 1 ? Path.GetFullPath(e.Args[uiIndex + 1]) : Path.Combine(Path.GetTempPath(), "ThermoHygroUi-" + Guid.NewGuid().ToString("N"));
            Environment.Exit(DesktopSmokeCheck.Run(directory));
            return;
        }
        AppLog.Info("=== 启动温湿度监测记录 0.5.0 ===");

        Configuration = ConfigStore.Load();

        var headless = Array.Exists(e.Args, a => a.Equals("--self-check", StringComparison.OrdinalIgnoreCase));
        if (headless)
        {
            int code = HeadlessSelfCheck.Run();
            AppLog.Info($"自检退出码 {code}");
            Environment.Exit(code);
            return;
        }

        var serialIdx = Array.FindIndex(e.Args, a => a.Equals("--serial-check", StringComparison.OrdinalIgnoreCase));
        if (serialIdx >= 0 && e.Args.Length >= serialIdx + 3)
        {
            var portA = e.Args[serialIdx + 1];
            var portB = e.Args[serialIdx + 2];
            var mode = e.Args.Length >= serialIdx + 4 ? e.Args[serialIdx + 3] : "normal";
            int code2 = SerialCheck.Run(portA, portB, mode);
            AppLog.Info($"串口回归退出码 {code2}（{portA} ↔ {portB}，模式 {mode}）");
            Environment.Exit(code2);
            return;
        }

        _main = new MainWindow(Configuration);
        _main.ExitRequested += DoExit;

        _tray = new TrayManager();
        _main.AttachTray(_tray);
        _tray.OpenRequested += () => ShowMain();
        _tray.ToggleOverlayRequested += () => _main.SetOverlayVisible(!_main.IsOverlayVisible);
        _tray.ToggleAcquireRequested += () =>
        {
            if (_main.IsAcquiring) _main.PauseAcquisition();
            else _main.StartAcquisition();
            _tray.SetAcquiring(_main.IsAcquiring);
        };
        _tray.ExitRequested += DoExit;

        // 注：X 关闭已改为“隐藏”（关闭窗口后 WPF 无法再 Show），
        // 因此首次隐藏提示放在 MainWindow 的 Closing 拦截处（见该处代码）。

        // 悬浮窗：默认启用
        if (Configuration.Overlay.Enabled) _main.EnsureOverlay();
        _tray.SetOverlayVisible(Configuration.Overlay.Enabled);
        _tray.SetAcquiring(_main.IsAcquiring);
        _tray.SetClickThroughChecked(Configuration.Overlay.ClickThrough);

        // 首次使用向导
        _main.Show();
        if (!Configuration.WizardCompleted && Configuration.ConfigurationReadError.Length == 0)
        {
            var wiz = new SetupWizard(_main, Configuration);
            wiz.ShowDialog();
        }

        _main.Show();
        _main.ShowWelcomeHint();
        _main.RefreshTrend();
        if (Configuration.StartWithWindows)
        {
            var error = global::ThermoHygro.App.MainWindow.ApplyStartWithWindows(true);
            if (error.Length > 0) AppLog.Warn(error);
        }

        if (Configuration.AutoStartAcquisition)
        {
            AppLog.Info("按配置自动开始采集");
            _main.StartAcquisition();
        }
        else AppLog.Info("自动开始采集未启用（默认关闭）");
    }

    private void ShowMain()
    {
        if (_main is null) return;
        try
        {
            if (!_main.IsVisible) _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Focus();
        }
        catch (Exception ex)
        {
            // 不静默：窗口无法显示时告知用户，否则报盘图标点了没反应且无任何提示
            AppLog.Error("打开主窗口失败", ex);
            try { _tray?.ShowBalloon("无法打开窗口", "主窗口无法显示：" + ex.Message); } catch { }
        }
    }

    private void DoExit()
    {
        if (_exiting) return;
        _exiting = true;
        AppLog.Info("用户请求退出");
        _main?.ShutdownForExit();
        try { _tray?.Dispose(); } catch { }
        AppLog.Info("=== 已退出 ===");
        Shutdown();
    }
}
