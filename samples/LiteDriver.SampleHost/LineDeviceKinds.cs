using System.Buffers;
using System.Text;
using LiteDriver.Protocol;

namespace LiteDriver.SampleHost;

/// <summary>
/// 示例设备种类：长度前缀帧（2 字节大端，前缀为帧体长度），
/// 以 ASCII 文本帧模拟“读寄存器”类请求与应答。
/// <para>
/// 该协议<b>不含 requestId</b>，因此驱动强制单在途串行；示例设备按“一问一答”回复。
/// </para>
/// </summary>
public sealed class LineDeviceKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public LineDeviceKind(ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new LineProtocol(_maxFrameSize);
}

/// <summary>一条连接的协议栈：全部成员按连接新建，不跨连接复用。</summary>
public sealed class LineProtocol : ISessionProtocol
{
    private readonly LengthPrefixFrameEncoder _encoder = new(2, bigEndian: true, lengthIncludesHeader: false);
    private readonly LineResponsePolicy _response = new();

    public LineProtocol(int maxFrameSize)
    {
        Decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, maxFrameSize);
        Heartbeat = null;
        Initializer = null;
    }

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer { get; }

    public IHeartbeatPolicy? Heartbeat { get; }

    public IResponsePolicy Response => _response;
}

/// <summary>把以 <c>ERR</c> 开头的应答判为拒绝，其余视为接受。</summary>
public sealed class LineResponsePolicy : NextFrameResponsePolicy
{
    public override ResponseClassification Classify(ReadOnlySpan<byte> frame)
    {
        // 帧含 2 字节长度前缀，载荷从第 3 字节开始。
        var payload = frame.Length > 2 ? frame[2..] : default;
        return payload.StartsWith("ERR"u8) ? ResponseClassification.Rejected : ResponseClassification.Accepted;
    }
}

/// <summary>
/// 示例设备种类：带应用层心跳与握手的分隔符帧。
/// 握手帧 <c>HELLO\n</c>，设备回 <c>OK\n</c>；心跳帧 <c>PING\n</c>，设备回 <c>PONG\n</c>。
/// </summary>
public sealed class LineHeartbeatKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public LineHeartbeatKind(ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new LineHeartbeatProtocol(_maxFrameSize);
}

/// <summary>带握手与心跳的协议栈。</summary>
public sealed class LineHeartbeatProtocol : ISessionProtocol
{
    private readonly TerminatorFrameEncoder _encoder = new("\n"u8);
    private readonly TerminatorFrameDecoder _decoder;
    private readonly LineHandshake _handshake = new();
    private readonly LineHeartbeat _heartbeat = new();

    public LineHeartbeatProtocol(int maxFrameSize)
    {
        _decoder = new TerminatorFrameDecoder("\n"u8, includeTerminator: false, maxFrameSize);
    }

    public IFrameDecoder Decoder => _decoder;

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer => _handshake;

    public IHeartbeatPolicy? Heartbeat => _heartbeat;

    public IResponsePolicy Response { get; } = new LineResponsePolicy();
}

/// <summary>握手：发 <c>HELLO</c>，等 <c>OK</c>；<c>FAIL</c> 视为拒绝。</summary>
public sealed class LineHandshake : ISessionInitializer
{
    public void WriteHandshake(IBufferWriter<byte> writer)
    {
        "HELLO"u8.CopyTo(writer.GetSpan(5));
        writer.Advance(5);
    }

    public HandshakeVerdict Inspect(ReadOnlySpan<byte> frame)
    {
        if (frame.SequenceEqual("OK"u8))
        {
            return HandshakeVerdict.Completed;
        }

        return frame.StartsWith("FAIL"u8) ? HandshakeVerdict.Rejected : HandshakeVerdict.Pending;
    }

    public void OnHandshakeCompleted()
    {
        // 示例不保存设备身份；真实实现可在此记录 bootId / 型号。
    }
}

/// <summary>心跳：发 <c>PING</c>，等 <c>PONG</c>。</summary>
public sealed class LineHeartbeat : IHeartbeatPolicy
{
    public void WriteProbe(long sequence, IBufferWriter<byte> writer)
    {
        "PING"u8.CopyTo(writer.GetSpan(4));
        writer.Advance(4);
    }

    public bool IsProbeResponse(ReadOnlySpan<byte> frame) => frame.SequenceEqual("PONG"u8);
}

/// <summary>示例设备种类的 kindSettings 校验：只允许固定键。</summary>
public static class LineKindSettings
{
    /// <summary>校验长度前缀帧种类的参数。目前只允许一个可选的 <c>maxFrameSize</c>。</summary>
    public static string? Validate(ReadOnlySpan<byte> settings)
    {
        if (settings.IsEmpty)
        {
            return null;
        }

        var encoding = Encoding.UTF8.GetString(settings);
        if (!encoding.Contains('{'))
        {
            return "kindSettings 必须是 JSON 对象";
        }

        return null;
    }
}
