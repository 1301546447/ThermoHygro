using System;
using System.Collections.Generic;

namespace ThermoHygro.Protocols;

/// <summary>Modbus RTU CRC16（多项式 0xA001，初值 0xFFFF）。</summary>
public static class ModbusCrc
{
    public static ushort Compute(ReadOnlySpan<byte> data)
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

    /// <summary>Modbus RTU 线上顺序为低字节在前。</summary>
    public static void AppendTo(List<byte> target, ReadOnlySpan<byte> data)
    {
        ushort crc = Compute(data);
        target.Add((byte)(crc & 0xFF));
        target.Add((byte)((crc >> 8) & 0xFF));
    }

    public static bool VerifyTrailing(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3) return false;
        ushort expect = Compute(frame[..^2]);
        ushort actual = (ushort)(frame[^2] | (frame[^1] << 8));
        return expect == actual;
    }
}

/// <summary>帧解析失败原因（保留质量原因，不伪装成超限）。</summary>
public enum FrameRejectReason
{
    None = 0,
    TooShort,
    CrcMismatch,
    AddressMismatch,
    FunctionMismatch,
    ByteCountMismatch,
    DeviceException,
    RangeViolation
}

public sealed class FrameParseResult
{
    public bool Success { get; init; }
    public double? Value { get; init; }
    public FrameRejectReason Reason { get; init; }
    public string ReasonText { get; init; } = "";
    public byte ExceptionCode { get; init; }

    public static FrameParseResult Ok(double v) => new() { Success = true, Value = v };
    public static FrameParseResult Fail(FrameRejectReason r, string text, byte exc = 0) =>
        new() { Success = false, Reason = r, ReasonText = text, ExceptionCode = exc };
}

/// <summary>
/// 单个指标的读取请求描述。ECM-30 使用功能码 0x03，读 1 个保持寄存器。
/// </summary>
public sealed class MetricRequest
{
    public required string MetricId { get; init; }     // "temperature" / "humidity"
    public required ushort Register { get; init; }
    public required ushort RegisterCount { get; init; }
    public required double Scale { get; init; }        // 0.1
    public required bool Signed { get; init; }
    public required string Unit { get; init; }
    public double? RangeLow { get; init; }
    public double? RangeHigh { get; init; }
}

/// <summary>
/// ECM-30 协议描述与解析。协议事实来自既有实现与协议文献，未做实物验证时如实标注。
/// 回复 7 字节：[Addr][Func][Len=2][Hi][Lo][CRC低][CRC高]，CRC16 覆盖前 5 字节。
/// </summary>
public sealed class Ecm30Protocol
{
    public const string ProtocolId = "ecm30";
    public const string DisplayName = "ECM-30";

    public string Describe() => "ECM-30（Modbus RTU，功能码 0x03，9600 8N1）";

    /// <summary>实物验证状态：目前仅为依据既有实现重建，未做实物验证。</summary>
    public string VerificationStatus => "未经实物验证";

    public IReadOnlyList<MetricRequest> SupportedMetrics { get; } = new List<MetricRequest>
    {
        new() { MetricId = "temperature", Register = 0x0100, RegisterCount = 1, Scale = 0.1, Signed = true,  Unit = "℃", RangeLow = -40, RangeHigh = 125 },
        new() { MetricId = "humidity",    Register = 0x0105, RegisterCount = 1, Scale = 0.1, Signed = false, Unit = "%RH", RangeLow = 0, RangeHigh = 100 }
    };

    /// <summary>构建读取请求帧（不含自动 CRC 追加；此函数直接生成含 CRC 的完整请求）。</summary>
    public byte[] BuildReadRequest(byte slaveAddress, MetricRequest metric)
    {
        var body = new List<byte>(8) { slaveAddress, 0x03,
            (byte)(metric.Register >> 8), (byte)(metric.Register & 0xFF),
            (byte)(metric.RegisterCount >> 8), (byte)(metric.RegisterCount & 0xFF) };
        ModbusCrc.AppendTo(body, body.ToArray());
        return body.ToArray();
    }

    /// <summary>期望的回复长度：地址+功能+1字节长度+2字节数据+2字节CRC。</summary>
    public int ExpectedResponseLength => 7;

    /// <summary>
    /// 严格解析回复帧。必须校验长度、地址、功能码、字节数、CRC 全部通过才返回值，
    /// 否则拒绝并保留明确原因（截断/损坏/异常响应都不容伪装成有效读数）。
    /// </summary>
    public FrameParseResult ParseResponse(byte[] frame, byte expectedSlaveAddress, MetricRequest metric)
    {
        if (frame is null || frame.Length < 5)
            return FrameParseResult.Fail(FrameRejectReason.TooShort, $"回复长度不足（收到 {frame?.Length ?? 0} 字节，至少 5 字节）");

        // 异常响应：[Addr][Func|0x80][ExceptionCode][CRC低][CRC高]
        if ((frame[1] & 0x80) != 0)
        {
            if (frame.Length < 5)
                return FrameParseResult.Fail(FrameRejectReason.TooShort, "异常响应长度不足");
            if (!ModbusCrc.VerifyTrailing(frame.AsSpan(0, 5)))
                return FrameParseResult.Fail(FrameRejectReason.CrcMismatch, "异常响应 CRC 校验失败（报文损坏）");
            if (frame[0] != expectedSlaveAddress)
                return FrameParseResult.Fail(FrameRejectReason.AddressMismatch,
                    $"异常响应地址不匹配（期望 {expectedSlaveAddress}，收到 {frame[0]}）");
            byte code = frame[2];
            return FrameParseResult.Fail(FrameRejectReason.DeviceException,
                $"设备返回异常码 0x{code:X2}：{DescribeException(code)}", code);
        }

        if (frame.Length != ExpectedResponseLength)
            return FrameParseResult.Fail(FrameRejectReason.TooShort,
                $"回复长度异常（收到 {frame.Length} 字节，期望 {ExpectedResponseLength} 字节）");

        if (!ModbusCrc.VerifyTrailing(frame))
            return FrameParseResult.Fail(FrameRejectReason.CrcMismatch, "CRC 校验失败（报文损坏，拒绝该数值）");

        if (frame[0] != expectedSlaveAddress)
            return FrameParseResult.Fail(FrameRejectReason.AddressMismatch,
                $"地址不匹配（期望 {expectedSlaveAddress}，收到 {frame[0]}）");

        if (frame[1] != 0x03)
            return FrameParseResult.Fail(FrameRejectReason.FunctionMismatch,
                $"功能码不匹配（期望 0x03，收到 0x{frame[1]:X2}）");

        if (frame[2] != 0x02)
            return FrameParseResult.Fail(FrameRejectReason.ByteCountMismatch,
                $"字节数不匹配（期望 2，收到 {frame[2]}）");

        ushort raw = (ushort)((frame[3] << 8) | frame[4]);
        double value = metric.Signed ? (short)raw * metric.Scale : raw * metric.Scale;

        if (metric.RangeLow.HasValue && value < metric.RangeLow.Value)
            return FrameParseResult.Fail(FrameRejectReason.RangeViolation,
                $"数值 {value:F1}{metric.Unit} 低于量程下限 {metric.RangeLow.Value}{metric.Unit}，按异常值拒绝");
        if (metric.RangeHigh.HasValue && value > metric.RangeHigh.Value)
            return FrameParseResult.Fail(FrameRejectReason.RangeViolation,
                $"数值 {value:F1}{metric.Unit} 高于量程上限 {metric.RangeHigh.Value}{metric.Unit}，按异常值拒绝");

        return FrameParseResult.Ok(value);
    }

    public static string DescribeException(byte code) => code switch
    {
        0x01 => "非法功能码",
        0x02 => "非法数据地址",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认（处理中）",
        0x06 => "从站设备忙",
        0x08 => "存储奇偶校验错",
        0x0A => "网关路径不可用",
        0x0B => "网关目标设备无响应",
        _ => "未知异常码"
    };
}

/// <summary>
/// 帧读取器：从串口字节流中抽取完整合法帧，处理分片到达、粘连帧与前置噪声，
/// 并限制缓存上限避免无界增长。
/// </summary>
public sealed class FrameReader
{
    private readonly List<byte> _buffer = new();
    private readonly int _maxBuffer;

    public FrameReader(int maxBuffer = 256) => _maxBuffer = maxBuffer;

    public int BufferedCount => _buffer.Count;

    /// <summary>追加到达的字节。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) _buffer.Add(b);
        if (_buffer.Count > _maxBuffer)
        {
            // 丢弃最旧数据，保留最近窗口，避免无界缓存
            int drop = _buffer.Count - _maxBuffer;
            _buffer.RemoveRange(0, drop);
            DiscardedBytes += drop;
        }
    }

    public int DiscardedBytes { get; private set; }

    /// <summary>
    /// 尝试从缓冲区头部取出一个完整帧。返回 null 表示数据尚未到齐。
    /// expectLength 由协议给出（7）；如首字节不是期望地址且看起来像噪声，会做一次前导丢弃。
    /// </summary>
    public byte[]? TryTakeFrame(int expectLength, byte expectAddress)
    {
        while (true)
        {
            if (_buffer.Count < expectLength) return null;

            // 前导噪声处理：首字节既不是期望地址也不是其异常位形式，丢弃 1 字节继续找帧头
            byte head = _buffer[0];
            if (head != expectAddress && head != (byte)(expectAddress | 0x80))
            {
                _buffer.RemoveAt(0);
                DiscardedBytes++;
                continue;
            }

            // 若首字节是异常位形式，异常帧长 5
            int frameLen = head == (byte)(expectAddress | 0x80) ? 5 : expectLength;
            if (_buffer.Count < frameLen) return null;

            byte[] frame = _buffer.GetRange(0, frameLen).ToArray();

            if (ModbusCrc.VerifyTrailing(frame))
            {
                _buffer.RemoveRange(0, frameLen);
                return frame;
            }

            // CRC 不通过：可能是噪声导致错位，丢弃 1 字节后重试对齐
            _buffer.RemoveAt(0);
            DiscardedBytes++;
        }
    }

    public void Clear() => _buffer.Clear();
}