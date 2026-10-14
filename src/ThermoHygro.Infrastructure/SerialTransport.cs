using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using ThermoHygro.Core;
using ThermoHygro.Protocols;

namespace ThermoHygro.Infrastructure;

/// <summary>串口传输结果。</summary>
public sealed class TransportResult
{
    public bool Success { get; init; }
    public byte[]? Frame { get; init; }
    public string? Error { get; init; }
    public string ErrorKind { get; init; } = "";
}

/// <summary>
/// 串口传输接口：独占、可取消、按真实长度收帧。
/// </summary>
public interface ISerialTransport : IDisposable
{
    bool IsOpen { get; }
    string PortName { get; }
    string Open(string portName, SerialSettings settings);
    void Close();
    /// <summary>发送请求并等待一帧回复；超时后清理缓冲区以隔离迟到回复。</summary>
    Task<TransportResult> RequestAsync(byte[] request, int expectedLength, byte expectedAddress, int timeoutMs, CancellationToken ct);
    /// <summary>丢弃输入缓冲区（用于隔离迟到回复）。</summary>
    void PurgeInput();
}

/// <summary>
/// 基于 SerialPort 的传输实现。要点：
/// ①独占打开，打开失败区分"端口不存在"与"被占用"；
/// ②每次请求前清空输入缓冲，超时后再次清理，严禁把上一指标的迟到回复当作本次结果；
/// ③单一收发循环由调用方串行化，本类不产生并发收发。
/// </summary>
public sealed class SerialTransport : ISerialTransport
{
    private SerialPort? _port;
    private readonly object _gate = new();
    private long _quarantineUntil;
    private byte _timedOutAddress;

    public bool IsOpen => _port?.IsOpen ?? false;
    public string PortName => _port?.PortName ?? "";

    public static string[] GetPortNames()
    {
        try { return SerialPort.GetPortNames(); }
        catch { return Array.Empty<string>(); }
    }

    public string Open(string portName, SerialSettings settings)
    {
        lock (_gate)
        {
            CloseNoLock();
            if (string.IsNullOrWhiteSpace(portName))
                return "未指定串口名称";

            try
            {
                var parity = settings.Parity switch
                {
                    "Even" => Parity.Even,
                    "Odd" => Parity.Odd,
                    "Mark" => Parity.Mark,
                    "Space" => Parity.Space,
                    _ => Parity.None
                };
                var stop = settings.StopBits switch
                {
                    "Two" => StopBits.Two,
                    "OnePointFive" => StopBits.OnePointFive,
                    _ => StopBits.One
                };

                var p = new SerialPort(portName, settings.BaudRate, parity, settings.DataBits, stop)
                {
                    ReadTimeout = settings.ReadTimeoutMs,
                    WriteTimeout = settings.ReadTimeoutMs,
                    Handshake = Handshake.None,
                    DtrEnable = false,
                    RtsEnable = false
                };
                p.Open();
                p.DiscardInBuffer();
                p.DiscardOutBuffer();
                _port = p;
                return "";
            }
            catch (UnauthorizedAccessException)
            {
                return $"串口 {portName} 已被其他程序占用（可能打开了串口调试工具）。请关闭占用程序后重试。";
            }
            catch (ArgumentException)
            {
                return $"串口名称 {portName} 无效。";
            }
            catch (IOException ex)
            {
                return $"串口 {portName} 不存在或无法打开：{ex.Message}";
            }
            catch (Exception ex)
            {
                return $"打开串口 {portName} 失败：{ex.GetType().Name}：{ex.Message}";
            }
        }
    }

    public void Close()
    {
        lock (_gate) CloseNoLock();
    }

    private void CloseNoLock()
    {
        var p = _port;
        _port = null;
        if (p is null) return;
        try { if (p.IsOpen) p.Close(); } catch { /* 释放失败不阻塞退出 */ }
        try { p.Dispose(); } catch { }
    }

    public void PurgeInput()
    {
        lock (_gate)
        {
            try
            {
                if (_port?.IsOpen == true)
                {
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                }
            }
            catch { }
        }
    }

    public async Task<TransportResult> RequestAsync(byte[] request, int expectedLength, byte expectedAddress,
        int timeoutMs, CancellationToken ct)
    {
        SerialPort? p;
        lock (_gate) p = _port;

        if (p is null || !p.IsOpen)
            return new TransportResult { Success = false, Error = "串口未打开", ErrorKind = "NotOpen" };

        try
        {
            // 功能码 03 的回复不回显寄存器。超时后直接请求同地址的下一指标，
            // 迟到的温度回复可能被当成湿度；先等待隔离窗口结束，再清理并发送。
            if (_timedOutAddress == expectedAddress)
            {
                double seconds = (Interlocked.Read(ref _quarantineUntil) - System.Diagnostics.Stopwatch.GetTimestamp())
                    / (double)System.Diagnostics.Stopwatch.Frequency;
                if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            // 关键：发请求前清空输入缓冲，隔离上一轮/上一指标的迟到回复
            lock (_gate)
            {
                try { p.DiscardInBuffer(); } catch { }
            }

            lock (_gate)
            {
                p.Write(request, 0, request.Length);
            }

            var reader = new FrameReader(256);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            byte[] scratch = new byte[64];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                {
                    _timedOutAddress = expectedAddress;
                    Interlocked.Exchange(ref _quarantineUntil, System.Diagnostics.Stopwatch.GetTimestamp()
                        + (long)(Math.Max(1000, timeoutMs) / 1000.0 * System.Diagnostics.Stopwatch.Frequency));
                    // 超时：清理迟到数据，防止污染下一次请求
                    lock (_gate)
                    {
                        try { p.DiscardInBuffer(); } catch { }
                    }
                    return new TransportResult
                    {
                        Success = false,
                        Error = $"超时：{timeoutMs} 毫秒内未收到完整合法回复",
                        ErrorKind = "Timeout"
                    };
                }

                int readTimeout = Math.Min(remaining, 200);
                int got = 0;
                try
                {
                    lock (_gate)
                    {
                        if (!p.IsOpen) return new TransportResult { Success = false, Error = "串口已被关闭", ErrorKind = "NotOpen" };
                        p.ReadTimeout = readTimeout;
                        got = p.Read(scratch, 0, scratch.Length);
                    }
                }
                catch (TimeoutException)
                {
                    got = 0;
                }
                catch (Exception ex)
                {
                    return new TransportResult
                    {
                        Success = false,
                        Error = $"串口读取失败：{FormatPortError(ex)}",
                        ErrorKind = "ReadError"
                    };
                }

                if (got > 0)
                {
                    reader.Append(scratch.AsSpan(0, got));
                    byte[]? frame = reader.TryTakeFrame(expectedLength, expectedAddress);
                    if (frame is not null)
                        return new TransportResult { Success = true, Frame = frame };
                }

                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return new TransportResult { Success = false, Error = "操作已取消", ErrorKind = "Canceled" };
        }
    }

    public static string FormatPortError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "串口被占用或权限不足",
        IOException => $"设备已断开或不存在：{ex.Message}",
        InvalidOperationException => $"串口状态无效（可能已被拔出）：{ex.Message}",
        _ => $"{ex.GetType().Name}：{ex.Message}"
    };

    public void Dispose() => Close();
}
