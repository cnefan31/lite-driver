using System.Buffers;
using System.Buffers.Binary;

namespace LiteDriver.Protocol;

/// <summary>帧解码器。每条物理连接一个独立实例，内部保存未完成的半帧状态，<b>不得跨连接复用</b>。</summary>
public interface IFrameDecoder
{
    /// <summary>
    /// 增量解析。返回 <see cref="FrameDecodeResult.Frame"/> 时 <paramref name="frame"/> 是
    /// <paramref name="buffer"/> 的一段视窗，仅在本次调用期间有效；调用方需自行复制。
    /// </summary>
    FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame);
}

/// <summary>帧解码结果。</summary>
public enum FrameDecodeResult
{
    /// <summary>数据不足，等待更多字节。</summary>
    NeedMore,

    /// <summary>成功解出一帧。</summary>
    Frame,

    /// <summary>字节流已不可信（非法长度、超上限）。必须关闭当前连接，不得继续猜边界。</summary>
    Fatal
}

/// <summary>帧编码器。可无状态、可跨连接共享，但本库仍按连接创建以保留扩展空间。</summary>
public interface IFrameEncoder
{
    /// <summary>把一帧载荷写成完整可发送帧。写入量不得超过 <see cref="DriverLimits.MaxFrameSize"/>。</summary>
    void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer);
}

/// <summary>固定长度分包：每帧恰好 <c>frameLength</c> 字节。</summary>
public sealed class FixedLengthFrameDecoder : IFrameDecoder
{
    private readonly int _frameLength;
    private readonly int _maxFrameSize;

    public FixedLengthFrameDecoder(int frameLength, int maxFrameSize)
    {
        if (frameLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLength), "帧长度必须为正");
        }

        if (frameLength > maxFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLength), "帧长度超过 MaxFrameSize");
        }

        _frameLength = frameLength;
        _maxFrameSize = maxFrameSize;
    }

    public FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        if (buffer.Length < _frameLength)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.NeedMore;
        }

        consumed = _frameLength;
        frame = buffer[.._frameLength];
        return FrameDecodeResult.Frame;
    }
}

/// <summary>长度前缀分包。前缀值表示帧体长度，总帧长 = 前缀 + 体（或含前缀，取决于 <c>lengthIncludesHeader</c>）。</summary>
public sealed class LengthPrefixFrameDecoder : IFrameDecoder
{
    private readonly int _prefixBytes;
    private readonly bool _bigEndian;
    private readonly bool _lengthIncludesHeader;
    private readonly int _maxFrameSize;

    public LengthPrefixFrameDecoder(int prefixBytes, bool bigEndian, bool lengthIncludesHeader, int maxFrameSize)
    {
        if (prefixBytes is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(prefixBytes), "前缀字节数只支持 1、2、4");
        }

        _prefixBytes = prefixBytes;
        _bigEndian = bigEndian;
        _lengthIncludesHeader = lengthIncludesHeader;
        _maxFrameSize = maxFrameSize;
    }

    public FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        if (buffer.Length < _prefixBytes)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.NeedMore;
        }

        var declared = ReadPrefix(buffer[.._prefixBytes]);
        var total = _lengthIncludesHeader ? declared : declared + _prefixBytes;

        if (declared < 0 || total < _prefixBytes || total > _maxFrameSize)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.Fatal;
        }

        if (buffer.Length < total)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.NeedMore;
        }

        consumed = total;
        frame = buffer[..total];
        return FrameDecodeResult.Frame;
    }

    private int ReadPrefix(ReadOnlySpan<byte> prefix) => _prefixBytes switch
    {
        1 => prefix[0],
        2 => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(prefix) : BinaryPrimitives.ReadUInt16LittleEndian(prefix),
        _ => (int)(_bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(prefix)
            : BinaryPrimitives.ReadUInt32LittleEndian(prefix))
    };
}

/// <summary>终止符分包，例如 <c>\r\n</c>。终止符出现在载荷中会被当作帧边界，请确认设备协议不要出现该序列。</summary>
public sealed class TerminatorFrameDecoder : IFrameDecoder
{
    private readonly byte[] _terminator;
    private readonly bool _includeTerminator;
    private readonly int _maxFrameSize;

    public TerminatorFrameDecoder(ReadOnlySpan<byte> terminator, bool includeTerminator, int maxFrameSize)
    {
        if (terminator.Length is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(terminator), "终止符长度必须在 1..8");
        }

        _terminator = terminator.ToArray();
        _includeTerminator = includeTerminator;
        _maxFrameSize = maxFrameSize;
    }

    public FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        if (buffer.Length > _maxFrameSize)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.Fatal;
        }

        var index = buffer.IndexOf(_terminator);
        if (index < 0)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.NeedMore;
        }

        var total = index + _terminator.Length;
        consumed = total;
        frame = _includeTerminator ? buffer[..total] : buffer[..index];
        return FrameDecodeResult.Frame;
    }
}

/// <summary>长度前缀编码器（2 字节大端，前缀为帧体长度）。</summary>
public sealed class LengthPrefixFrameEncoder : IFrameEncoder
{
    private readonly int _prefixBytes;
    private readonly bool _bigEndian;
    private readonly bool _lengthIncludesHeader;

    public LengthPrefixFrameEncoder(int prefixBytes, bool bigEndian, bool lengthIncludesHeader)
    {
        if (prefixBytes is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(prefixBytes), "前缀字节数只支持 1、2、4");
        }

        _prefixBytes = prefixBytes;
        _bigEndian = bigEndian;
        _lengthIncludesHeader = lengthIncludesHeader;
    }

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        var declared = _lengthIncludesHeader ? payload.Length + _prefixBytes : payload.Length;
        var header = writer.GetSpan(_prefixBytes);
        switch (_prefixBytes)
        {
            case 1:
                header[0] = (byte)declared;
                break;
            case 2:
                if (_bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)declared);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)declared);
                }

                break;
            default:
                if (_bigEndian)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)declared);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)declared);
                }

                break;
        }

        writer.Advance(_prefixBytes);
        payload.CopyTo(writer.GetSpan(payload.Length));
        writer.Advance(payload.Length);
    }
}

/// <summary>终止符编码器。</summary>
public sealed class TerminatorFrameEncoder : IFrameEncoder
{
    private readonly byte[] _terminator;

    public TerminatorFrameEncoder(ReadOnlySpan<byte> terminator)
    {
        if (terminator.Length is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(terminator), "终止符长度必须在 1..8");
        }

        _terminator = terminator.ToArray();
    }

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        payload.CopyTo(writer.GetSpan(payload.Length));
        writer.Advance(payload.Length);
        _terminator.CopyTo(writer.GetSpan(_terminator.Length));
        writer.Advance(_terminator.Length);
    }
}

/// <summary>原样透传编码器：不做任何封装，直接把载荷写出。</summary>
public sealed class PassthroughFrameEncoder : IFrameEncoder
{
    public static PassthroughFrameEncoder Instance { get; } = new();

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        payload.CopyTo(writer.GetSpan(payload.Length));
        writer.Advance(payload.Length);
    }
}

/// <summary>原样透传解码器：每次读取到的字节都当作一帧。仅适用于设备侧本就按消息分界的场景。</summary>
public sealed class PassthroughFrameDecoder : IFrameDecoder
{
    private readonly int _maxFrameSize;

    public PassthroughFrameDecoder(int maxFrameSize) => _maxFrameSize = maxFrameSize;

    public FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        if (buffer.Length > _maxFrameSize)
        {
            consumed = 0;
            frame = default;
            return FrameDecodeResult.Fatal;
        }

        consumed = buffer.Length;
        frame = buffer;
        return FrameDecodeResult.Frame;
    }
}

/// <summary>
/// 十六进制标记工具：把 <c>"02"</c>、<c>"03ff"</c>、<c>"02 03 FF"</c>、<c>"0x02,0x03"</c>
/// 这类写法解析为字节序列。空字符串或 null 表示"未配置该标记"。
/// </summary>
public static class HexMarker
{
    /// <summary>可配置标记的最大字节数。</summary>
    public const int MaxLength = 16;

    /// <summary>尝试解析十六进制标记。空输入返回空数组（表示未配置）。</summary>
    public static bool TryParse(string? text, out byte[] marker, out string? error)
    {
        marker = [];
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var digits = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is ' ' or ',' or '-' or ':' or '\t' || ch == '\r' || ch == '\n')
            {
                continue;
            }

            if (ch == 'x' || ch == 'X')
            {
                // 支持 0x02 写法：丢弃 'x'，同时丢掉它前面刚收集的那个 '0'，
                // 否则 "0x02" 会变成 "002" 这类错误结果。
                if (digits.Length > 0 && digits[^1] == '0')
                {
                    digits.Length--;
                }

                continue;
            }

            if (!Uri.IsHexDigit(ch))
            {
                error = $"非法十六进制字符 '{ch}'（只允许 0-9 a-f A-F 与分隔符）";
                return false;
            }

            digits.Append(ch);
        }

        if (digits.Length == 0)
        {
            return true;
        }

        if (digits.Length % 2 != 0)
        {
            error = $"十六进制位数必须为偶数，实际 {digits.Length} 位";
            return false;
        }

        var count = digits.Length / 2;
        if (count > MaxLength)
        {
            error = $"标记长度 {count} 字节超过上限 {MaxLength}";
            return false;
        }

        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
        }

        marker = bytes;
        return true;
    }

    /// <summary>把字节序列格式化为大写十六进制，便于日志与回显。</summary>
    public static string Format(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 0 ? "(空)" : Convert.ToHexString(bytes);
}

/// <summary>
/// 起始/结束标记分帧器。
/// <para>
/// 起始标记与结束标记各自独立可配，<b>至少配置一个</b>。支持三种形态：
/// 仅起始标记、仅结束标记、两者都有。配置后交付的帧<b>包含</b>标记本身。
/// </para>
/// <para>
/// 仅配置起始标记时，一帧的范围是「本起始标记到下一个起始标记之前」；
/// 最后一帧需要等到下一个起始标记到达才交付，因此该模式天然有一帧延迟。
/// </para>
/// <para>
/// 起始标记之前的无关字节会被静默丢弃并重新同步（通过 <c>NeedMore</c> + <c>consumed</c> 上报丢弃量）。
/// </para>
/// <para>
/// <b>仅配置结束标记时的固有约束</b>：没有起始标记就无法判断哪里是帧的开头，
/// 因此「上一个结束标记之后到本结束标记之前」的全部字节都会被当作一帧内容，
/// 断线重连后残留的半截数据会并入下一帧。若设备协议无法保证这一点，
/// 请同时配置起始标记。
/// </para>
/// </summary>
public sealed class DelimitedFrameDecoder : IFrameDecoder
{
    private readonly byte[] _start;
    private readonly byte[] _end;
    private readonly int _maxFrameSize;

    /// <summary>构造函数。<paramref name="start"/> 与 <paramref name="end"/> 不能同时为空。</summary>
    public DelimitedFrameDecoder(ReadOnlySpan<byte> start, ReadOnlySpan<byte> end, int maxFrameSize)
    {
        if (start.IsEmpty && end.IsEmpty)
        {
            throw new ArgumentException("起始标记与结束标记至少要配置一个");
        }

        if (start.Length > HexMarker.MaxLength || end.Length > HexMarker.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"标记长度不得超过 {HexMarker.MaxLength} 字节");
        }

        _start = start.ToArray();
        _end = end.ToArray();
        _maxFrameSize = maxFrameSize;
    }

    /// <summary>起始标记（可能为空）。</summary>
    public ReadOnlySpan<byte> Start => _start;

    /// <summary>结束标记（可能为空）。</summary>
    public ReadOnlySpan<byte> End => _end;

    public FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        consumed = 0;
        frame = default;

        if (_start.Length > 0)
        {
            return DecodeWithStart(buffer, out consumed, out frame);
        }

        return DecodeEndOnly(buffer, out consumed, out frame);
    }

    /// <summary>有起始标记：可再叠加结束标记，或依靠下一个起始标记切分。</summary>
    private FrameDecodeResult DecodeWithStart(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        consumed = 0;
        frame = default;

        // 1) 定位起始标记；其之前的字节属于前导垃圾，丢弃并重新同步。
        var startIndex = buffer.IndexOf(_start);
        if (startIndex < 0)
        {
            // 整个缓冲都不是有效的起始前缀：全部丢弃。
            // 但保留末尾可能与起始标记前缀重叠的若干字节，避免把跨包的 start 切断。
            var keep = TrailingPrefixOverlap(buffer, _start);
            consumed = buffer.Length - keep;
            return FrameDecodeResult.NeedMore;
        }

        if (startIndex > 0)
        {
            // 丢弃起始标记之前的垃圾字节，下一轮即可对齐到 start。
            consumed = startIndex;
            return FrameDecodeResult.NeedMore;
        }

        // 此刻 buffer 以起始标记开头。
        var bodyStart = _start.Length;

        if (_end.Length > 0)
        {
            // 2a) 有结束标记：从 bodyStart 起找结束标记。
            var searchFrom = bodyStart;
            while (true)
            {
                var endIndex = buffer[searchFrom..].IndexOf(_end);
                if (endIndex < 0)
                {
                    if (buffer.Length > _maxFrameSize)
                    {
                        return FrameDecodeResult.Fatal;
                    }

                    return FrameDecodeResult.NeedMore;
                }

                var endPos = searchFrom + endIndex;
                var total = endPos + _end.Length;
                if (total > _maxFrameSize)
                {
                    return FrameDecodeResult.Fatal;
                }

                consumed = total;
                frame = buffer[..total];
                return FrameDecodeResult.Frame;
            }
        }

        // 2b) 仅有起始标记：本帧到下一个起始标记之前。
        var nextStart = buffer[_start.Length..].IndexOf(_start);
        if (nextStart < 0)
        {
            if (buffer.Length > _maxFrameSize)
            {
                return FrameDecodeResult.Fatal;
            }

            // 需要更多数据才能确认本帧边界。
            return FrameDecodeResult.NeedMore;
        }

        var boundary = _start.Length + nextStart;
        if (boundary > _maxFrameSize)
        {
            return FrameDecodeResult.Fatal;
        }

        consumed = boundary;
        frame = buffer[..boundary];
        return FrameDecodeResult.Frame;
    }

    /// <summary>仅有结束标记：从缓冲区开头找结束标记，其之前都算一帧。</summary>
    private FrameDecodeResult DecodeEndOnly(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        consumed = 0;
        frame = default;

        var endIndex = buffer.IndexOf(_end);
        if (endIndex < 0)
        {
            if (buffer.Length > _maxFrameSize)
            {
                return FrameDecodeResult.Fatal;
            }

            return FrameDecodeResult.NeedMore;
        }

        var total = endIndex + _end.Length;
        if (total > _maxFrameSize)
        {
            return FrameDecodeResult.Fatal;
        }

        consumed = total;
        frame = buffer[..total];
        return FrameDecodeResult.Frame;
    }

    /// <summary>缓冲区末尾有多少字节是起始标记的前缀，需要保留等待拼接。</summary>
    private static int TrailingPrefixOverlap(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> start)
    {
        var max = Math.Min(start.Length - 1, buffer.Length);
        for (var len = max; len > 0; len--)
        {
            if (buffer[^len..].SequenceEqual(start[..len]))
            {
                return len;
            }
        }

        return 0;
    }
}

/// <summary>起始/结束标记编码器：按配置包上标记。</summary>
public sealed class DelimitedFrameEncoder : IFrameEncoder
{
    private readonly byte[] _start;
    private readonly byte[] _end;

    public DelimitedFrameEncoder(ReadOnlySpan<byte> start, ReadOnlySpan<byte> end)
    {
        if (start.IsEmpty && end.IsEmpty)
        {
            throw new ArgumentException("起始标记与结束标记至少要配置一个");
        }

        _start = start.ToArray();
        _end = end.ToArray();
    }

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        if (_start.Length > 0)
        {
            _start.CopyTo(writer.GetSpan(_start.Length));
            writer.Advance(_start.Length);
        }

        payload.CopyTo(writer.GetSpan(payload.Length));
        writer.Advance(payload.Length);

        if (_end.Length > 0)
        {
            _end.CopyTo(writer.GetSpan(_end.Length));
            writer.Advance(_end.Length);
        }
    }
}
