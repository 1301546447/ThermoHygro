using System;
using System.IO;
using System.Text;

namespace ThermoHygro.App;

/// <summary>简单文件日志（轮转、容量上限），用于诊断与证据留存。绝不记录凭据。</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(ThermoHygro.Infrastructure.ConfigStore.DefaultDirectory, "Logs");

    public static string CurrentFile => Path.Combine(LogDir, "app.log");

    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m) => Write("ERROR", m);

    public static void Error(string m, Exception? ex) => Write("ERROR", Describe(m, ex));

    public static void Fatal(string m, Exception? ex) => Write("FATAL", Describe(m, ex));

    /// <summary>
    /// 异常日志必须带堆栈与内部异常：只记 "类型: 消息" 时，
    /// 生产环境的致命错误无法定位到具体代码行，日志就失去诊断价值。
    /// </summary>
    private static string Describe(string m, Exception? ex)
    {
        if (ex is null) return m;
        var sb = new StringBuilder(m);
        for (var e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(" :: ").Append(e.GetType().FullName).Append(": ").Append(e.Message);
            if (!ReferenceEquals(e, ex)) sb.Append("（内部异常）");
        }
        if (ex.StackTrace is { Length: > 0 } st) sb.Append(Environment.NewLine).Append(st);
        return sb.ToString();
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                var redacted = System.Text.RegularExpressions.Regex.Replace(message,
                    "(?i)(password|pwd|token|secret)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^;\\r\\n]*)", "$1=***");
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {redacted}{Environment.NewLine}";
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
                RotateIfNeeded();
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(CurrentFile);
            if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
            var old = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(CurrentFile, old, true);
            // 只保留最近 5 份
            var files = Directory.GetFiles(LogDir, "app-*.log");
            Array.Sort(files);
            for (int i = 0; i < files.Length - 5; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }

    public static string Tail(int maxLines = 300)
    {
        try
        {
            if (!File.Exists(CurrentFile)) return "（暂无日志）";
            var lines = File.ReadAllLines(CurrentFile);
            int start = Math.Max(0, lines.Length - maxLines);
            var sb = new StringBuilder();
            for (int i = start; i < lines.Length; i++) sb.AppendLine(lines[i]);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"（日志读取失败：{ex.Message}）";
        }
    }
}
