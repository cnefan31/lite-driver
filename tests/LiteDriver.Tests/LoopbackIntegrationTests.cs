using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using LiteDriver.SampleHost;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>
/// 环回集成测试：真实 socket、真实线程、真实重连。覆盖设计稿里最关键的稳健性不变量。
/// </summary>
public sealed class LoopbackIntegrationTests
{
    private static LiteDriver NewDriver(IDriverObserver? observer = null) =>
        new LiteDriverBuilder()
            // 必须使用真实 context：帧长上限来自 DriverLimits，传 default 会让解码器拒绝一切帧。
            .RegisterKind("line-lp", static ctx => new LineDeviceKind(ctx))
            .RegisterKind("line-hb", static ctx => new LineHeartbeatKind(ctx))
            // 只有心跳、无握手的种类，用于单独验证心跳判死路径。
            .RegisterKind("probe-only", static _ => new ProbeOnlyKind())
            .UseObserver(observer ?? NullDriverObserver.Instance)
            .Build();

    private static DeviceDescriptor DeviceFor(int port, bool heartbeat = false) => new()
    {
        DeviceId = "dev",
        KindId = "line-lp",
        Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = port }],
        Retry = new RetryOptions { InitialMs = 100, MaxMs = 1000, Jitter = 0, ConnectTimeoutMs = 2000 },
        Heartbeat = new HeartbeatOptions { Enabled = heartbeat, IntervalMs = 200, Misses = 3, SilentMs = 2000 }
    };

    [Fact]
    public async Task Should_ConnectExchangeAndReportExecuted()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        await using var driver = NewDriver();

        var start = await driver.StartAsync(new DriverConfig
        {
            Devices = [DeviceFor(fake.Port)]
        });

        Assert.True(start.Succeeded);
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        var handle = driver.GetDevice("dev");
        using var result = await handle.ExchangeAsync("PING"u8.ToArray(), TimeSpan.FromSeconds(5));

        Assert.Equal(CommandDisposition.Executed, result.Disposition);
        // 帧含 2 字节长度前缀，载荷从第 3 字节起。
        Assert.Equal("PONG", Encoding.ASCII.GetString(result.Response.Span[2..]));
        Assert.True(result.Epoch > 0);
    }

    [Fact]
    public async Task Should_ReconnectAfterRemoteDropAndUseNewEpoch()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        await using var driver = NewDriver();

        await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port)] });
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        var firstEpoch = driver.GetDevice("dev").Epoch;
        var beforeConnections = fake.TotalConnections;

        // 让设备端主动断开。
        await driver.GetDevice("dev").SendFrameAsync("#drop"u8.ToArray());

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (driver.GetDevice("dev").State == LinkState.Ready && fake.TotalConnections > beforeConnections)
            {
                break;
            }

            await Task.Delay(100);
        }

        var handle = driver.GetDevice("dev");
        Assert.Equal(LinkState.Ready, handle.State);
        Assert.True(fake.TotalConnections > beforeConnections, "应建立新连接");
        Assert.True(handle.Epoch > firstEpoch, $"新 epoch 应增大，实际 {handle.Epoch} vs {firstEpoch}");

        // 新连接上仍能正常交换。
        using var result = await handle.ExchangeAsync("PING"u8.ToArray(), TimeSpan.FromSeconds(5));
        Assert.Equal(CommandDisposition.Executed, result.Disposition);
    }

    [Fact]
    public async Task Should_ReportNotSentWhenDeviceNeverComesUp()
    {
        // 取一个几乎不可能有服务在听的端口。
        var deadPort = GetFreePort();
        await using var driver = NewDriver();

        var start = await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(deadPort)] });

        // 关键不变量：StartAsync 不被无限重连阻塞，及时返回成功（配置合法）但链路未就绪。
        Assert.True(start.Succeeded);
        Assert.NotEqual(LinkState.Ready, driver.GetDevice("dev").State);

        using var result = await driver.GetDevice("dev").ExchangeAsync("PING"u8.ToArray());
        Assert.Equal(CommandDisposition.NotSent, result.Disposition);
        Assert.Equal("not-ready", result.ErrorCode);
    }

    [Fact]
    public async Task Should_NotAutoReplayWriteWhenLinkDropsMidExchange()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        await using var driver = NewDriver();

        await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port)] });
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        var commandsBefore = fake.TotalCommands;

        // #drop 会让设备端直接关闭连接且不回包，交换必然拿不到响应。
        using var result = await driver.GetDevice("dev").ExchangeAsync("#drop"u8.ToArray(), TimeSpan.FromSeconds(3));

        Assert.Equal(CommandDisposition.SentOutcomeUnknown, result.Disposition);

        // 等待重连完成，确认库没有自动重发那条写命令。
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(15));
        await Task.Delay(500);

        // 设备只收到过 #drop 这一条：重连后没有把它再发一遍。
        Assert.Equal(commandsBefore + 1, fake.TotalCommands);
    }

    [Fact]
    public async Task Apply_ShouldRollbackWhenListenerPortIsBusy()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        await using var driver = NewDriver();

        // 先用 fake 占用的端口做监听，必然绑定失败。
        var start = await driver.StartAsync(new DriverConfig
        {
            Devices = [DeviceFor(fake.Port)],
            Listeners =
            [
                new ListenerDescriptor
                {
                    ListenerId = "gw", KindId = "line-lp", Host = "127.0.0.1", Port = fake.Port
                }
            ]
        });

        Assert.False(start.Succeeded);
        Assert.Contains(start.Issues, i => i.Code == "stage_failed");
        // 失败后驱动回到 Created，不留下半可用状态。
        Assert.Equal(RunState.Created, driver.State);
    }

    [Fact]
    public async Task Apply_ShouldKeepPreviousSnapshotWhenValidationFails()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        await using var driver = NewDriver();

        await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port)] });
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));
        var goodRevision = driver.Revision;

        var bad = await driver.ApplyAsync(new DriverConfig
        {
            Devices = [new DeviceDescriptor { DeviceId = "dev", KindId = "unknown-kind" }]
        });

        Assert.False(bad.Succeeded);
        Assert.Equal(goodRevision, driver.Revision);
        // 旧链路继续服务。
        Assert.Equal(LinkState.Ready, driver.GetDevice("dev").State);
        using var result = await driver.GetDevice("dev").ExchangeAsync("PING"u8.ToArray(), TimeSpan.FromSeconds(5));
        Assert.Equal(CommandDisposition.Executed, result.Disposition);
    }

    [Fact]
    public async Task RemoveDevice_ShouldStopLinkAndAllowReAdd()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        await using var driver = NewDriver();

        await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port)] });
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        var removed = await driver.RemoveDeviceAsync("dev");
        Assert.Equal(MutationStatus.Applied, removed.Status);
        Assert.False(driver.TryGetDevice("dev", out _));

        // 同 ID 可再次添加。
        var added = await driver.AddDeviceAsync(DeviceFor(fake.Port));
        Assert.Equal(MutationStatus.Applied, added.Status);
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        // 重复添加返回 AlreadyExists，不静默覆盖。
        var duplicate = await driver.AddDeviceAsync(DeviceFor(fake.Port));
        Assert.Equal(MutationStatus.AlreadyExists, duplicate.Status);

        var missing = await driver.RemoveDeviceAsync("not-there");
        Assert.Equal(MutationStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task InboundListener_ShouldAcceptSessionAndDeliverFrames()
    {
        await using var driver = NewDriver();

        await driver.StartAsync(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor
                {
                    ListenerId = "gw", KindId = "line-lp", Host = "127.0.0.1", Port = 0, MaxSessions = 4
                }
            ]
        });

        var ports = driver.GetListenerPorts();
        Assert.True(ports.TryGetValue("gw", out var port));
        Assert.True(port > 0, "系统应分配一个真实端口");

        // 设备侧连入并发送一帧。
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0, 4, (byte)'P', (byte)'I', (byte)'N', (byte)'G' });
        await stream.FlushAsync();

        // 应用侧应能通过句柄拿到这一帧（入站会话只投递，不自动应答）。
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

        Assert.True(session.IsValid, "应出现一个入站会话");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var frame = await ReceiveNextAsync(session, cts.Token);
        Assert.NotNull(frame);
        // 载荷含 2 字节长度前缀，正文为 PING。
        Assert.Equal("PING", Encoding.ASCII.GetString(frame!.Payload.Span[2..]));

        // 应用可以回写，设备侧应收到。
        await session.SendFrameAsync("PONG"u8.ToArray(), cts.Token);

        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cts.Token);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cts.Token);
        Assert.Equal("PONG", Encoding.ASCII.GetString(payload));
    }

    private static async Task<PushFrame?> ReceiveNextAsync(InboundSessionHandle session, CancellationToken ct)
    {
        // 句柄每次只读一帧；通道在会话结束时关闭。
        return await session.ReceiveAsync(ct);
    }

    [Fact]
    public async Task RemoveListener_ShouldCloseBoundPort()
    {
        await using var driver = NewDriver();

        await driver.StartAsync(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor { ListenerId = "gw", KindId = "line-lp", Host = "127.0.0.1", Port = 0 }
            ]
        });

        var port = driver.GetListenerPorts()["gw"];
        var removed = await driver.RemoveListenerAsync("gw");
        Assert.Equal(MutationStatus.Applied, removed.Status);

        // 端口应不可再连接。
        var refused = false;
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            refused = true;
        }

        Assert.True(refused, "删除监听端点后端口应不可连接");
    }

    [Fact]
    public async Task Stop_ShouldBeIdempotentAndLeaveNoReconnect()
    {
        await using var fake = new FakeDeviceServer();
        fake.Start();
        var driver = NewDriver();

        await driver.StartAsync(new DriverConfig { Devices = [DeviceFor(fake.Port)] });
        await driver.WaitUntilReadyAsync("dev", TimeSpan.FromSeconds(10));

        await driver.StopAsync();
        var connectionsAfterStop = fake.TotalConnections;

        // 再停一次不应抛异常。
        await driver.StopAsync();
        await Task.Delay(700);

        Assert.Equal(RunState.Stopped, driver.State);
        Assert.Equal(connectionsAfterStop, fake.TotalConnections);
        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Heartbeat_ShouldDetectSilentPeer()
    {
        // 一个只接受连接、永不回包的"死设备"。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    lock (accepted)
                    {
                        accepted.Add(client);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // 测试结束；Linux 上 Stop 后挂起的 Accept 会抛 SocketException。
            }
        });

        try
        {
            await using var driver = NewDriver();
            var config = new DriverConfig
            {
                Devices =
                [
                    new DeviceDescriptor
                    {
                        DeviceId = "silent",
                        KindId = "probe-only",
                        Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = port }],
                        Retry = new RetryOptions { InitialMs = 100, MaxMs = 500, Jitter = 0 },
                        Heartbeat = new HeartbeatOptions
                        {
                            Enabled = true,
                            IntervalMs = 200,
                            Misses = 4,
                            SilentMs = 0,
                            ResponseTimeoutMs = 200
                        }
                    }
                ]
            };

            await driver.StartAsync(config);
            await driver.WaitUntilReadyAsync("silent", TimeSpan.FromSeconds(10));

            // 心跳连续未回应后应被判死并进入重连，不再是 Ready。
            var detected = false;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (driver.GetDevice("silent").State is LinkState.Retrying or LinkState.Handshaking
                    or LinkState.Connecting or LinkState.Resolving)
                {
                    detected = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(detected, "心跳超时后应判定链路失效并进入重连");
        }
        finally
        {
            lock (accepted)
            {
                foreach (var c in accepted)
                {
                    c.Dispose();
                }
            }

            listener.Stop();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>测试用种类：仅提供应用层心跳（PING/PONG），无握手、长度前缀分包，便于单独验证判死路径。</summary>
internal sealed class ProbeOnlyKind : Protocol.IDeviceKind
{
    public Protocol.ISessionProtocol CreateProtocol(in Protocol.ProtocolContext context) => new ProbeOnlyProtocol(context.Limits.MaxFrameSize);
}

/// <summary>仅心跳的协议栈。</summary>
internal sealed class ProbeOnlyProtocol : Protocol.ISessionProtocol
{
    private readonly Protocol.LengthPrefixFrameEncoder _encoder = new(2, bigEndian: true, lengthIncludesHeader: false);

    public ProbeOnlyProtocol(int maxFrameSize) =>
        Decoder = new Protocol.LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, maxFrameSize);

    public Protocol.IFrameDecoder Decoder { get; }

    public Protocol.IFrameEncoder Encoder => _encoder;

    public Protocol.ISessionInitializer? Initializer => null;

    public Protocol.IHeartbeatPolicy? Heartbeat { get; } = new ProbeOnlyHeartbeat();

    public Protocol.IResponsePolicy Response { get; } = new Protocol.NextFrameResponsePolicy();
}

/// <summary>发 PING，收 PONG。</summary>
internal sealed class ProbeOnlyHeartbeat : Protocol.IHeartbeatPolicy
{
    public void WriteProbe(long sequence, System.Buffers.IBufferWriter<byte> writer)
    {
        "PING"u8.CopyTo(writer.GetSpan(4));
        writer.Advance(4);
    }

    public bool IsProbeResponse(ReadOnlySpan<byte> frame) =>
        frame.Length >= 2 && frame[2..].SequenceEqual("PONG"u8);
}
