using System;
using System.IO.Ports;
using System.Threading;

namespace ThermoHygro.MockSensor;

/// <summary>
/// ECM-30 传感器模拟器（仅用于开发与验证，绝不冒充真实读数）。
/// 在指定串口上响应 Modbus RTU 功能码 0x03 读寄存器请求：
///   温度请求 01 03 01 00 00 01 -> 01 03 02 [值] [CRC]
///   湿度请求 01 03 01 05 00 01 -> 01 03 02 [值] [CRC]
/// 模式可切换：normal / negative / zero / crc-error / exception / no-response / late
/// </summary>
public static class Program
{
    private static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 1) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                else crc >>= 1;
            }
        }
        return crc;
    }

    private static byte[] Frame(byte addr, byte func, ushort raw)
    {
        var f = new byte[7];
        f[0] = addr; f[1] = func; f[2] = 0x02;
        f[3] = (byte)(raw >> 8); f[4] = (byte)(raw & 0xFF);
        ushort c = Crc(f.AsSpan(0, 5));
        f[5] = (byte)(c & 0xFF); f[6] = (byte)((c >> 8) & 0xFF);
        return f;
    }

    private static byte[] ExceptionFrame(byte addr, byte func, byte code)
    {
        var f = new byte[5];
        f[0] = addr; f[1] = (byte)(func | 0x80); f[2] = code;
        ushort c = Crc(f.AsSpan(0, 3));
        f[3] = (byte)(c & 0xFF); f[4] = (byte)((c >> 8) & 0xFF);
        return f;
    }

    public static int Main(string[] args)
    {
        string port = args.Length > 0 ? args[0] : "COM21";
        string mode = args.Length > 1 ? args[1] : "normal";
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
        var addresses = (args.Length > 3 ? args[3] : "1").Split(',').Select(byte.Parse).ToHashSet();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"[模拟传感器] 端口={port} 模式={mode} 时长={seconds}s 地址={string.Join(',', addresses)}");
        Console.WriteLine("[模拟传感器] 注意：本程序仅用于开发验证，数据为模拟值，不是真实读数。");

        try
        {
            using var sp = new SerialPort(port, 9600, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 300,
                WriteTimeout = 500
            };
            sp.Open();
            Console.WriteLine("[模拟传感器] 已打开串口，开始响应请求…");
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var buf = new byte[64];
            var acc = new System.Collections.Generic.List<byte>();
            int tempRaw = 235;   // 23.5
            int humRaw = 500;    // 50.0
            int round = 0;

            while (DateTime.UtcNow < deadline)
            {
                int n;
                try { n = sp.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }
                if (n <= 0) continue;

                for (int i = 0; i < n; i++) acc.Add(buf[i]);

                // 请求帧固定 8 字节；按帧提取，避免分批到达被拆分
                while (acc.Count >= 8)
                {
                    var req = acc.GetRange(0, 8);
                    acc.RemoveRange(0, 8);
                    byte a = req[0];
                    byte fn = req[1];
                    ushort reg = (ushort)((req[2] << 8) | req[3]);
                    Console.WriteLine($"[模拟传感器] 收到请求 addr={a} func=0x{fn:X2} reg=0x{reg:X4}");
                    if (!addresses.Contains(a) || fn != 0x03) continue;
                    byte addr = a;
                    bool isTemp = reg == 0x0100;
                    round++;

                    if (mode == "no-response" && isTemp) { Console.WriteLine("  -> no-response：不回复"); continue; }
                    if (mode == "late" && isTemp)
                    {
                        Console.WriteLine("  -> late：温度延迟 4 秒回复（应被超时隔离）");
                        var cap = sp;
                        var t = new Thread(() =>
                        {
                            Thread.Sleep(4000);
                            try { var f = Frame(addr, 0x03, (ushort)tempRaw); cap.Write(f, 0, f.Length); } catch { }
                        });
                        t.IsBackground = true; t.Start();
                        continue;
                    }
                    if (mode == "exception")
                    {
                        var ex = ExceptionFrame(addr, 0x03, 0x02);
                        sp.Write(ex, 0, ex.Length);
                        Console.WriteLine("  -> exception：返回异常码 0x02");
                        continue;
                    }
                    if (mode == "crc-error")
                    {
                        var f = Frame(addr, 0x03, (ushort)(isTemp ? tempRaw : humRaw));
                        f[6] ^= 0xFF;
                        sp.Write(f, 0, f.Length);
                        Console.WriteLine("  -> crc-error：CRC 被破坏");
                        continue;
                    }

                    ushort raw;
                    if (isTemp)
                    {
                        raw = mode switch
                        {
                            "negative" => unchecked((ushort)(short)-123),
                            "zero" => 0,
                            "multi" => addr == 1 ? (ushort)40 : (ushort)250,
                            _ => (ushort)tempRaw
                        };
                        if (mode == "normal") tempRaw = 220 + (round % 30);
                    }
                    else
                    {
                        raw = (ushort)humRaw;
                        if (mode == "normal") humRaw = 480 + (round % 40);
                    }
                    var resp = Frame(addr, 0x03, raw);
                    sp.Write(resp, 0, resp.Length);
                    Console.WriteLine($"  -> 回复 {string.Join(" ", resp)} ({raw / 10.0:F1})");
                }
            }
            sp.Close();
            Console.WriteLine("[模拟传感器] 正常结束");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[模拟传感器] 失败：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }
}
