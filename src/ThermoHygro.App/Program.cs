using System;
using System.Windows;
using System.Windows.Threading;

namespace ThermoHygro.App;

/// <summary>
/// 应用入口。使用显式 Main 以便在创建窗口前处理全局异常，
/// 保证异常不会静默消失，也不留下"看起来正常"的界面。
/// </summary>
public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        bool verification = args.Any(argument => argument is "--self-check" or "--serial-check" or "--ui-check");
        bool firstInstance = true;
        using var instance = verification ? null : new System.Threading.Mutex(true,
            "Local\\ThermoHygro-" + System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, out firstInstance);
        if (!verification && !firstInstance)
        {
            MessageBox.Show("温湿度监测记录已在运行，请从任务栏托盘图标打开。", "温湿度监测记录");
            return 0;
        }
        // WinExe 默认无控制台；命令行验证模式需要输出到调用方终端
        if (args.Length > 0)
        {
            try { AttachConsole(-1); } catch { }
        }
        var app = new App();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                AppLog.Fatal("未处理的域异常", ex);
            }
            catch { }
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (verification)
            {
                Console.Error.WriteLine("验证模式发生异常：" + e.Exception);
                Environment.Exit(1);
                return;
            }
            try
            {
                AppLog.Error("未处理的界面异常", e.Exception);
                MessageBox.Show(
                    "程序遇到未预期的错误，已记录到日志。\n\n" + e.Exception.Message,
                    "温湿度监测记录", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            }
            catch { }
        };
        app.Startup += (_, e) =>
        {
            if (e.Args.Contains("--screenshot-mode"))
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        };
        return app.Run();
    }
}
