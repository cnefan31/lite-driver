using System.Buffers;
using System.Text;
using LiteDriver.Configuration;
using LiteDriver.Protocol;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>载荷编解码器的注册、配置校验与端到端使用。</summary>
public sealed class PayloadCodecTests
{
    /// <summary>示例 codec：把帧载荷按 UTF-8 解码成 <see cref="TextMessage"/>，可配前缀剥离。</summary>
    private sealed class TextCodec : IPayloadCodec
    {
        private readonly int _stripPrefix;

        public TextCodec(PayloadCodecContext ctx) =>
            _stripPrefix = ctx.CodecSettings is { } s && s.TryGetProperty("stripPrefix", out var v) && v.TryGetInt32(out var n)
                ? n
                : 0;

        public string CodecId => "text";

        public PayloadMessage Decode(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < _stripPrefix)
            {
                throw new PayloadDecodeException($"帧长 {frame.Length} 小于需剥离的前缀 {_stripPrefix}");
            }

            var text = Encoding.UTF8.GetString(frame[_stripPrefix..]);
            if (text == "IGNORE")
            {
                return PayloadMessage.Empty("text-ignored");
            }

            return PayloadMessage.From(new TextMessage(text), "text");
        }

        public void Encode(PayloadMessage message, IBufferWriter<byte> writer)
        {
            var text = message.As<TextMessage>().Text;
            var bytes = Encoding.UTF8.GetBytes(text);
            bytes.CopyTo(writer.GetSpan(bytes.Length));
            writer.Advance(bytes.Length);
        }
    }

    /// <summary>业务消息。</summary>
    public sealed record TextMessage(string Text);

    /// <summary>会抛非 decode 异常的 codec，用于验证异常被兜住并拆链。</summary>
    private sealed class CrashingCodec : IPayloadCodec
    {
        public CrashingCodec(PayloadCodecContext ctx) => _ = ctx;

        public string CodecId => "crash";

        public PayloadMessage Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("boom");

        public void Encode(PayloadMessage message, IBufferWriter<byte> writer) => _ = (message, writer);
    }

    private static LiteDriverBuilder NewBuilder() =>
        new LiteDriverBuilder()
            .RegisterKind("raw", static ctx => new RawKind(ctx))
            .RegisterCodec("text", static ctx => new TextCodec(ctx))
            .RegisterCodec("crash", static ctx => new CrashingCodec(ctx));

    private static DeviceDescriptor DeviceFor(int port, string? codecId, string? codecSettings = null) => new()
    {
        DeviceId = "dev",
        KindId = "raw",
        Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = port }],
        Retry = new RetryOptions { InitialMs = 100, MaxMs = 1000, Jitter = 0, ConnectTimeoutMs = 2000 },
        CodecId = codecId,
        CodecSettings = codecSettings is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize(codecSettings, LiteDriverJsonContext.Default.JsonElement)
    };

    [Fact]
    public void ShouldRejectUnknownCodecId()
    {
        var result = FrozenConfig.Create(
            new DriverConfig { Devices = [DeviceFor(502, "not-registered")] },
            1,
            ["raw"],
            new DriverLimits(),
            static (_, _) => null,
            ["text"],
            null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "codec_unknown");
    }

    [Fact]
    public void ShouldAcceptKnownCodecId()
    {
        var result = FrozenConfig.Create(
            new DriverConfig { Devices = [DeviceFor(502, "text")] },
            1,
            ["raw"],
            new DriverLimits(),
            static (_, _) => null,
            ["text"],
            null);

        Assert.True(result.IsValid, string.Join("；", result.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void ShouldPropagateCodecSettingsValidation()
    {
        var result = FrozenConfig.Create(
            new DriverConfig
            {
                Devices = [DeviceFor(502, "text", """{"stripPrefix":"bad"}""")]
            },
            1,
            ["raw"],
            new DriverLimits(),
            static (_, _) => null,
            ["text"],
            static (codecId, _) => codecId == "text" ? "自定义校验失败" : null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "codec_settings_invalid");
    }

    [Fact]
    public void ShouldRejectUnknownCodecOnListener()
    {
        var result = FrozenConfig.Create(
            new DriverConfig
            {
                Listeners =
                [
                    new ListenerDescriptor
                    {
                        ListenerId = "l", KindId = "raw", Host = "127.0.0.1", Port = 0, CodecId = "nope"
                    }
                ]
            },
            1,
            ["raw"],
            new DriverLimits(),
            static (_, _) => null,
            ["text"],
            null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "codec_unknown");
    }

    [Fact]
    public void ShouldNotAllowRegisterAfterFreeze()
    {
        var builder = NewBuilder();
        _ = builder.Build();
        Assert.Throws<InvalidOperationException>(() =>
            builder.RegisterCodec("late", static ctx => new TextCodec(ctx)));
    }

    [Fact]
    public void ShouldRejectDuplicateCodecId()
    {
        var builder = NewBuilder();
        Assert.Throws<ArgumentException>(() =>
            builder.RegisterCodec("text", static ctx => new TextCodec(ctx)));
    }

    [Fact]
    public async Task ShouldDecodeResponseIntoMessage()
    {
        // 服务端回 "hello"，codec 无前缀剥离
        await using var fake = new EchoServer();
        fake.Start();

        await using var driver = NewBuilder().Build();
        var start = await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port, "text")] });
        Assert.True(start.Succeeded, string.Join("；", start.Issues.Select(i => i.Message)));

        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));
        using var result = await driver.GetDevice("dev").ExchangeAsync("ping"u8.ToArray(), TimeSpan.FromSeconds(5));

        Assert.Equal(CommandDisposition.Executed, result.Disposition);
        Assert.True(result.Message.HasValue, "应解码出业务消息");
        Assert.Equal("hello", result.Message.As<TextMessage>().Text);
    }

    [Fact]
    public async Task ShouldExposeCodecMessageOnInboundSession()
    {
        await using var driver = NewBuilder().Build();

        await driver.StartAsync(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor
                {
                    ListenerId = "gw", KindId = "raw", Host = "127.0.0.1", Port = 0, MaxSessions = 2,
                    CodecId = "text"
                }
            ]
        });

        var port = driver.GetListenerPorts()["gw"];
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);

        // 必须先等 accept 完成、会话注册后再写数据。
        // 若连上就立刻写，数据可能在会话 Context 建立前到达，
        // 在慢速 CI 上会表现为帧丢失（本地快机器不会复现）。
        InboundSessionHandle session = default;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !session.IsValid)
        {
            var sessions = driver.GetInboundSessions("gw");
            if (sessions.Count > 0)
            {
                session = sessions[0];
                break;
            }

            await Task.Delay(50);
        }

        Assert.True(session.IsValid);

        var stream = client.GetStream();
        await stream.WriteAsync("inbound-text"u8.ToArray());
        await stream.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var frame = await session.ReceiveAsync(cts.Token);

        Assert.NotNull(frame);
        Assert.True(frame!.Message.HasValue, "入站帧应带解码后的消息");
        Assert.Equal("inbound-text", frame.Message.As<TextMessage>().Text);
        // 原始载荷仍然可用（含协议头语义，这里是 raw 所以就是原文）
        Assert.Equal("inbound-text", Encoding.UTF8.GetString(frame.Payload.Span));
    }

    [Fact]
    public async Task ShouldKeepRawBytesWhenNoCodecConfigured()
    {
        await using var fake = new EchoServer();
        fake.Start();

        await using var driver = NewBuilder().Build();
        await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port, null)] });
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        using var result = await driver.GetDevice("dev").ExchangeAsync("ping"u8.ToArray(), TimeSpan.FromSeconds(5));

        Assert.Equal(CommandDisposition.Executed, result.Disposition);
        Assert.False(result.Message.HasValue, "未配置 codec 时不应有消息");
        Assert.Equal("hello", Encoding.UTF8.GetString(result.Response.Span));
    }

    /// <summary>raw 协议：原样透传，便于聚焦 codec 行为。</summary>
    private sealed class RawKind : IDeviceKind
    {
        private readonly int _max;

        public RawKind(in ProtocolContext ctx) => _max = ctx.Limits.MaxFrameSize;

        public ISessionProtocol CreateProtocol(in ProtocolContext ctx) => new RawProtocol(_max);
    }

    private sealed class RawProtocol : ISessionProtocol
    {
        public RawProtocol(int max) => Decoder = new PassthroughFrameDecoder(max);

        public IFrameDecoder Decoder { get; }

        public IFrameEncoder Encoder { get; } = PassthroughFrameEncoder.Instance;

        public ISessionInitializer? Initializer => null;

        public IHeartbeatPolicy? Heartbeat => null;

        public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
    }

    /// <summary>极简服务端：收到什么就回 "hello"。</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public EchoServer() => _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);

        public int Port { get; private set; }

        public void Start()
        {
            _listener.Start(4);
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient c;
                try
                {
                    c = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[256];
                        try
                        {
                            var n = await s.ReadAsync(buf, _cts.Token);
                            if (n > 0)
                            {
                                await s.WriteAsync("hello"u8.ToArray(), _cts.Token);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException)
                        {
                            // 连接结束。
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_loop is not null)
            {
                try
                {
                    await _loop.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    // 预期。
                }
            }

            _cts.Dispose();
        }
    }
}
