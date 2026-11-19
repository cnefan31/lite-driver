using System.Text.Json;
using LiteDriver.Orchestration;
using LiteDriver.Orchestration.Server;
using LiteDriver.Protocol;

namespace LiteDriver.Orchestration.Sample;

/// <summary>
/// 编排服务样例宿主：注册若干协议种类，启动可视化编排页面。
/// 页面上的增删改保存后热应用，<b>本进程无需重启</b>。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var port = 8099;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed))
            {
                port = parsed;
            }
        }

        var protocols = BuildProtocols();

        // 注册编解码器/变换器/处理器/出口，供编排页面上的对应节点选择。
        await using var engine = new OrchestrationEngine(
            protocols,
            codecs: BuildCodecs(),
            transforms: [new ComponentRegistration("scale", "数值换算", "乘以系数")],
            handlers: [new ComponentRegistration("log", "记录日志", "把消息写入事件流")],
            sinks: [new ComponentRegistration("queue", "内存队列", "送入内存队列")]);

        await using var server = new OrchestrationServer(
            engine,
            new OrchestrationServerOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Title = "LiteDriver 功能编排"
            });

        server.Start();

        Console.WriteLine($"编排控制台已启动： http://127.0.0.1:{server.Port}/");
        Console.WriteLine("在页面上拖拽组件、连线、保存并应用；后端无需重启。");
        Console.WriteLine("按 Ctrl+C 退出。");

        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult(true);
        };

        // 支持通过命令行灌入一份初始图，便于自动化验证。
        var graphFile = GetArg(args, "--graph");
        if (graphFile is not null && File.Exists(graphFile))
        {
            var json = await File.ReadAllTextAsync(graphFile).ConfigureAwait(false);
            var graph = JsonSerializer.Deserialize(json, OrchestrationJsonContext.Default.OrchestrationGraph);
            if (graph is not null)
            {
                var report = await engine.ApplyAsync(graph).ConfigureAwait(false);
                Console.WriteLine($"已从 {graphFile} 应用初始图：ok={report.Ok} rev={report.Revision} msg={report.Message}");
            }
        }

        await stop.Task.ConfigureAwait(false);
        await server.StopAsync().ConfigureAwait(false);
        Console.WriteLine("已停止。");
        return 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>注册给编排页面使用的编解码器。</summary>
    private static List<CodecRegistration> BuildCodecs() =>
    [
        new CodecRegistration(
            "text",
            "文本编解码",
            "长度前缀协议下取载荷按 UTF-8 转成文本消息",
            static ctx => new TextCodec(ctx))
    ];

    /// <summary>注册给编排页面使用的协议种类。</summary>
    private static List<ProtocolRegistration> BuildProtocols() =>
    [
        new ProtocolRegistration(
            "length-prefix",
            "长度前缀（2 字节大端）",
            "帧 = 2 字节大端长度 + 载荷。最常见的工业私有协议形态。",
            [],
            static ctx => new LengthPrefixKind(ctx),
            null),

        new ProtocolRegistration(
            "line-terminator",
            "终止符（\\n）",
            "帧 = 载荷 + \\n，带 HELLO/OK 握手与 PING/PONG 心跳。",
            [],
            static ctx => new LineKind(ctx),
            null),

        new ProtocolRegistration(
            "fixed-length",
            "固定长度（16 字节）",
            "每帧固定 16 字节，不足补空格。",
            [new ProtocolField("frameLength", "帧长度", "16", "16", "固定帧字节数（示例实现固定 16）")],
            static ctx => new FixedKind(ctx),
            null),

        new ProtocolRegistration(
            "stx-etx",
            "起始/结束标记",
            "帧 = 起始标记 + 数据 + 结束标记。标记用十六进制配置，至少配一个。",
            [
                new ProtocolField("start", "起始标记", "02", "02", "十六进制，如 02；留空表示不限制起始"),
                new ProtocolField("end", "结束标记", "03", "03", "十六进制，如 03ff；留空表示不限制结束")
            ],
            static ctx => new DelimitedKind(ctx),
            DelimitedKind.ValidateSettings)
    ];
}

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

/// <summary>行协议：\n 分隔，带握手与心跳。</summary>
public sealed class LineKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public LineKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) =>
        new LineProtocol(_maxFrameSize, context.Direction);
}

/// <summary>行协议栈。</summary>
public sealed class LineProtocol : ISessionProtocol
{
    private readonly TerminatorFrameEncoder _encoder = new("\n"u8);
    private readonly TerminatorFrameDecoder _decoder;
    private readonly LineHandshake? _handshake;

    public LineProtocol(int maxFrameSize, ConnectionDirection direction)
    {
        _decoder = new TerminatorFrameDecoder("\n"u8, includeTerminator: false, maxFrameSize);
        _handshake = direction == ConnectionDirection.Outbound ? new LineHandshake() : null;
    }

    public IFrameDecoder Decoder => _decoder;

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer => _handshake;

    public IHeartbeatPolicy? Heartbeat { get; } = new LineHeartbeat();

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

/// <summary>行协议握手。</summary>
public sealed class LineHandshake : ISessionInitializer
{
    public void WriteHandshake(System.Buffers.IBufferWriter<byte> writer)
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

/// <summary>行协议心跳。</summary>
public sealed class LineHeartbeat : IHeartbeatPolicy
{
    public void WriteProbe(long sequence, System.Buffers.IBufferWriter<byte> writer)
    {
        "PING"u8.CopyTo(writer.GetSpan(4));
        writer.Advance(4);
    }

    public bool IsProbeResponse(ReadOnlySpan<byte> frame) => frame.SequenceEqual("PONG"u8);
}

/// <summary>固定 16 字节协议。</summary>
public sealed class FixedKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public FixedKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new FixedProtocol(_maxFrameSize);
}

/// <summary>固定长度协议栈。</summary>
public sealed class FixedProtocol : ISessionProtocol
{
    private const int FrameLength = 16;

    public FixedProtocol(int maxFrameSize) =>
        Decoder = new FixedLengthFrameDecoder(FrameLength, Math.Max(maxFrameSize, FrameLength));

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder { get; } = new FixedPaddedEncoder();

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

/// <summary>补齐到固定长度的编码器。</summary>
public sealed class FixedPaddedEncoder : IFrameEncoder
{
    public void Encode(ReadOnlySpan<byte> payload, System.Buffers.IBufferWriter<byte> writer)
    {
        const int length = 16;
        var span = writer.GetSpan(length);
        var frame = span[..length];
        frame.Fill((byte)' ');
        payload[..Math.Min(payload.Length, length)].CopyTo(frame);
        writer.Advance(length);
    }
}

/// <summary>示例编解码器：长度前缀帧的载荷按 UTF-8 解码，并支持从纯文本构造消息。</summary>
public sealed class TextCodec : IPayloadCodec, ITextPayloadCodec
{
    public TextCodec(PayloadCodecContext ctx) => _ = ctx;

    /// <inheritdoc />
    public string CodecId => "text";

    /// <inheritdoc />
    public PayloadMessage Decode(ReadOnlySpan<byte> frame)
    {
        // 长度前缀协议：前 2 字节是帧体长度。
        var payload = frame.Length > 2 ? frame[2..] : default;
        return PayloadMessage.From(new TextMessage(System.Text.Encoding.UTF8.GetString(payload)), "text");
    }

    /// <inheritdoc />
    public PayloadMessage FromText(string text) => PayloadMessage.From(new TextMessage(text), "text");

    /// <inheritdoc />
    public void Encode(PayloadMessage message, System.Buffers.IBufferWriter<byte> writer)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message.As<TextMessage>().Text);
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }
}

/// <summary>示例业务消息。</summary>
public sealed record TextMessage(string Text);
