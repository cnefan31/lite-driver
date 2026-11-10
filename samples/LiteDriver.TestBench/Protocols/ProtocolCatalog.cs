using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using LiteDriver.Protocol;

namespace LiteDriver.TestBench.Protocols;

/// <summary>
/// 测试台协议目录中的一项。每个协议同时提供客户端侧与服务端侧的 kindId，
/// 两者帧格式约定相同，便于在测试台里让「客户端驱动」与「服务端驱动」互连。
/// </summary>
public sealed record ProtocolDescriptor(
    string Id,
    string DisplayName,
    string Description,
    string Encoding,
    bool SupportsHeartbeat,
    bool SupportsHandshake,
    Func<ProtocolContext, IDeviceKind> Factory,
    Func<ReadOnlySpan<byte>, byte[]> ServerResponder);

/// <summary>
/// 内置协议目录。新增一个自定义协议只需在 <see cref="List"/> 里加一项，
/// 测试台页面会自动出现该协议，无需改前端。
/// </summary>
public static class ProtocolCatalog
{
    public static IReadOnlyList<ProtocolDescriptor> List { get; } =
    [
        new ProtocolDescriptor(
            "length-prefix",
            "长度前缀（2 字节大端）",
            "帧 = 2 字节大端长度 + 载荷。最常见的工业私有协议形态。",
            "UTF-8 文本",
            SupportsHeartbeat: false,
            SupportsHandshake: false,
            static ctx => new LengthPrefixKind(ctx),
            static payload => Encoding.UTF8.GetBytes("ECHO:" + Encoding.UTF8.GetString(payload))),

        new ProtocolDescriptor(
            "line-terminator",
            "终止符（\\n）",
            "帧 = 载荷 + \\n。文本行协议，便于用 telnet 手工调试。",
            "UTF-8 文本",
            SupportsHeartbeat: true,
            SupportsHandshake: true,
            static ctx => new LineTerminatorKind(ctx),
            static payload => Encoding.UTF8.GetBytes("ECHO:" + Encoding.UTF8.GetString(payload))),

        new ProtocolDescriptor(
            "fixed-length",
            "固定长度（16 字节）",
            "每帧固定 16 字节，不足补空格。适用于定长寄存器块传输。",
            "ASCII，定长补空格",
            SupportsHeartbeat: false,
            SupportsHandshake: false,
            static ctx => new FixedLengthKind(ctx),
            static payload =>
            {
                var text = Encoding.ASCII.GetString(payload).TrimEnd();
                var reply = Encoding.ASCII.GetBytes("ECHO:" + text);
                var frame = new byte[FixedLengthKind.FrameLength];
                Array.Fill(frame, (byte)' ');
                Array.Copy(reply, frame, Math.Min(reply.Length, frame.Length));
                return frame;
            }),

        new ProtocolDescriptor(
            "stx-etx",
            "起始/结束标记（默认 02..03）",
            "帧 = 起始标记 + 数据 + 结束标记，两者均可配十六进制且至少配一个。测试台固定用 02 与 03。",
            "UTF-8 文本，包 02/03",
            SupportsHeartbeat: false,
            SupportsHandshake: false,
            static ctx => DelimitedTestKind.Factory(ctx),
            static payload => Encoding.UTF8.GetBytes("ECHO:" + Encoding.UTF8.GetString(payload))),

        new ProtocolDescriptor(
            "modbus-like",
            "Modbus 风格（MBAP 类似）",
            "帧 = 事务ID(2) + 协议ID(2) + 长度(2) + 单元ID(1) + 功能码(1) + 数据。用于验证带事务标识的协议。",
            "二进制",
            SupportsHeartbeat: false,
            SupportsHandshake: false,
            static ctx => new ModbusLikeKind(ctx),
            static payload =>
            {
                // 入站时协议层已剥掉 6 字节 MBAP 头与 1 字节功能码，payload 即业务数据。
                var body = Encoding.ASCII.GetBytes("ECHO:" + Encoding.ASCII.GetString(payload));
                return body;
            })
    ];

    public static ProtocolDescriptor? Find(string id) =>
        List.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

// ---------------------------------------------------------------------------
// 1) 长度前缀
// ---------------------------------------------------------------------------

/// <summary>长度前缀协议：2 字节大端长度 + 载荷。</summary>
public sealed class LengthPrefixKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public LengthPrefixKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new LengthPrefixProtocol(_maxFrameSize);
}

/// <summary>长度前缀协议栈。</summary>
public sealed class LengthPrefixProtocol : ISessionProtocol
{
    private readonly LengthPrefixFrameEncoder _encoder = new(2, bigEndian: true, lengthIncludesHeader: false);

    public LengthPrefixProtocol(int maxFrameSize) =>
        Decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, maxFrameSize);

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

// ---------------------------------------------------------------------------
// 2) 终止符 + 握手 + 心跳
// ---------------------------------------------------------------------------

/// <summary>行协议：\n 分隔，带 HELLO/OK 握手与 PING/PONG 心跳。</summary>
public sealed class LineTerminatorKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public LineTerminatorKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) =>
        new LineTerminatorProtocol(_maxFrameSize, context.Direction);
}

/// <summary>行协议栈。服务端方向不主动握手，客户端方向先发 HELLO。</summary>
public sealed class LineTerminatorProtocol : ISessionProtocol
{
    private readonly TerminatorFrameEncoder _encoder = new("\n"u8);
    private readonly TerminatorFrameDecoder _decoder;
    private readonly LineHandshake? _handshake;
    private readonly LineHeartbeat _heartbeat = new();

    public LineTerminatorProtocol(int maxFrameSize, ConnectionDirection direction)
    {
        _decoder = new TerminatorFrameDecoder("\n"u8, includeTerminator: false, maxFrameSize);
        // 只让主动拨号侧发握手，服务端被动等待，避免两侧同时发 HELLO。
        _handshake = direction == ConnectionDirection.Outbound ? new LineHandshake() : null;
    }

    public IFrameDecoder Decoder => _decoder;

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer => _handshake;

    public IHeartbeatPolicy? Heartbeat => _heartbeat;

    public IResponsePolicy Response { get; } = new LineEchoResponsePolicy();
}

/// <summary>握手：发 HELLO，等 OK。</summary>
public sealed class LineHandshake : ISessionInitializer
{
    public void WriteHandshake(IBufferWriter<byte> writer)
    {
        "HELLO"u8.CopyTo(writer.GetSpan(5));
        writer.Advance(5);
    }

    public HandshakeVerdict Inspect(ReadOnlySpan<byte> frame) =>
        frame.SequenceEqual("OK"u8) ? HandshakeVerdict.Completed
        : frame.StartsWith("FAIL"u8) ? HandshakeVerdict.Rejected
        : HandshakeVerdict.Pending;

    public void OnHandshakeCompleted()
    {
    }
}

/// <summary>心跳：发 PING，收 PONG。</summary>
public sealed class LineHeartbeat : IHeartbeatPolicy
{
    public void WriteProbe(long sequence, IBufferWriter<byte> writer)
    {
        "PING"u8.CopyTo(writer.GetSpan(4));
        writer.Advance(4);
    }

    public bool IsProbeResponse(ReadOnlySpan<byte> frame) => frame.SequenceEqual("PONG"u8);
}

/// <summary>行协议响应判定：服务端的 ECHO 前缀视为响应。</summary>
public sealed class LineEchoResponsePolicy : NextFrameResponsePolicy
{
    public override bool IsResponse(ReadOnlySpan<byte> frame) => true;

    public override ResponseClassification Classify(ReadOnlySpan<byte> frame) =>
        frame.StartsWith("ERR"u8) ? ResponseClassification.Rejected : ResponseClassification.Accepted;
}

// ---------------------------------------------------------------------------
// 3) 固定长度
// ---------------------------------------------------------------------------

/// <summary>固定 16 字节协议。</summary>
public sealed class FixedLengthKind : IDeviceKind
{
    /// <summary>帧长。</summary>
    public const int FrameLength = 16;

    private readonly int _maxFrameSize;

    public FixedLengthKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new FixedLengthProtocol(_maxFrameSize);
}

/// <summary>固定长度协议栈，发送时自动补齐到 16 字节。</summary>
public sealed class FixedLengthProtocol : ISessionProtocol
{
    public FixedLengthProtocol(int maxFrameSize)
    {
        // 固定长度协议要求 maxFrameSize 至少能容纳一帧。
        var limit = Math.Max(maxFrameSize, FixedLengthKind.FrameLength);
        Decoder = new FixedLengthFrameDecoder(FixedLengthKind.FrameLength, limit);
    }

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder { get; } = new FixedLengthPaddedEncoder();

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

/// <summary>把载荷补齐到固定长度再写出。</summary>
public sealed class FixedLengthPaddedEncoder : IFrameEncoder
{
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(FixedLengthKind.FrameLength);
        var frame = span[..FixedLengthKind.FrameLength];
        frame.Fill((byte)' ');
        payload[..Math.Min(payload.Length, FixedLengthKind.FrameLength)].CopyTo(frame);
        writer.Advance(FixedLengthKind.FrameLength);
    }
}

// ---------------------------------------------------------------------------
// 4) Modbus 风格（带事务 ID）
// ---------------------------------------------------------------------------

/// <summary>MBAP 风格协议：事务ID + 协议ID + 长度 + 单元ID + 功能码 + 数据。</summary>
public sealed class ModbusLikeKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public ModbusLikeKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new ModbusLikeProtocol(_maxFrameSize);
}

/// <summary>MBAP 风格协议栈。</summary>
public sealed class ModbusLikeProtocol : ISessionProtocol
{
    public ModbusLikeProtocol(int maxFrameSize) =>
        Decoder = new MbapFrameDecoder(maxFrameSize);

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder { get; } = new MbapFrameEncoder();

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

/// <summary>MBAP 解码：按第 5 字节起的长度字段切帧。</summary>
public sealed class MbapFrameDecoder : IFrameDecoder
{
    private readonly int _maxFrameSize;

    public MbapFrameDecoder(int maxFrameSize) => _maxFrameSize = maxFrameSize;

    public FrameDecodeResult Decode(ReadOnlySpan<byte> buffer, out int consumed, out ReadOnlySpan<byte> frame)
    {
        consumed = 0;
        frame = default;

        if (buffer.Length < 6)
        {
            return FrameDecodeResult.NeedMore;
        }

        var declared = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..6]);
        var total = 6 + declared;

        if (declared == 0 || total > _maxFrameSize)
        {
            return FrameDecodeResult.Fatal;
        }

        if (buffer.Length < total)
        {
            return FrameDecodeResult.NeedMore;
        }

        consumed = total;
        frame = buffer[..total];
        return FrameDecodeResult.Frame;
    }
}

/// <summary>MBAP 编码：为每帧生成递增事务 ID。</summary>
public sealed class MbapFrameEncoder : IFrameEncoder
{
    private int _transaction;

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> writer)
    {
        // 完整帧 = MBAP 头（事务ID 2 + 协议ID 2 + 长度 2）+ 单元ID 1 + 功能码 1 + 数据。
        // 长度字段表示其后的字节数：单元ID + 功能码 + 数据。
        const byte unitId = 0x01;
        const byte functionCode = 0x03;
        var length = 1 + 1 + payload.Length;
        var span = writer.GetSpan(6 + length);
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)Interlocked.Increment(ref _transaction));
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)length);
        span[6] = unitId;
        span[7] = functionCode;
        payload.CopyTo(span[8..]);
        writer.Advance(6 + length);
    }
}

// ---------------------------------------------------------------------------
// 5) 起始/结束标记（使用核心库 DelimitedKind）
// ---------------------------------------------------------------------------

/// <summary>
/// 测试台用的标记协议：固定 start=02 end=03，让页面可直接验证该分帧器。
/// 核心库的 <see cref="DelimitedKind"/> 既支持注册时固定标记，
/// 也支持从 kindSettings 的 start/end 读取。
/// </summary>
public static class DelimitedTestKind
{
    /// <summary>注册进驱动的工厂（标记固定在注册时）。</summary>
    public static Func<ProtocolContext, IDeviceKind> Factory { get; } =
        DelimitedKind.CreateFactory("02", "03");
}
