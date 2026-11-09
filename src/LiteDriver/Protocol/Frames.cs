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
