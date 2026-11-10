using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using LiteDriver.Protocol;
using LiteDriver.TestBench.Protocols;

namespace LiteDriver.TestBench;

/// <summary>
/// 测试台运行时：管理「客户端驱动」与「服务端驱动」两个实例，
/// 二者都注册全部内置协议，可在页面上随时切换并重连，从而验证不同自定义协议。
/// </summary>
public sealed class BenchRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<BenchEvent> _recentEventDtos = new();
    private LiteDriver? _client;
    private LiteDriver? _server;
    private string _clientProtocolId = "length-prefix";
    private string _serverProtocolId = "length-prefix";
    private int _serverPort;
    private readonly BenchObserver _observer;
    private CancellationTokenSource? _serverPumpCts;
    private Task? _serverPump;

    public BenchRuntime()
    {
        _observer = new BenchObserver(this);
    }

    public int ServerPort => _serverPort;

    public string ClientProtocolId => _clientProtocolId;

    public string ServerProtocolId => _serverProtocolId;

    /// <summary>最近事件快照，供页面显示实时流水。</summary>
    public IReadOnlyList<BenchEvent> RecentEvents => [.. _recentEventDtos];

    /// <summary>启动两个驱动：服务端监听 127.0.0.1 的指定端口，客户端连它。</summary>
    public async Task<BenchStatus> StartAsync(string clientProtocolId, string serverProtocolId, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            if (ProtocolCatalog.Find(clientProtocolId) is null)
            {
                throw new ArgumentException($"未知客户端协议 '{clientProtocolId}'");
            }

            if (ProtocolCatalog.Find(serverProtocolId) is null)
            {
                throw new ArgumentException($"未知服务端协议 '{serverProtocolId}'");
            }

            _clientProtocolId = clientProtocolId;
            _serverProtocolId = serverProtocolId;

            // ---- 服务端驱动：只监听，接受客户端连入 ----
            _server = BuildDriver(serverProtocolId);
            var serverStart = await _server.StartAsync(new DriverConfig
            {
                Listeners =
                [
                    new ListenerDescriptor
                    {
                        ListenerId = "bench-server",
                        KindId = serverProtocolId,
                        Host = "127.0.0.1",
                        Port = port,
                        MaxSessions = 8,
                        Heartbeat = new HeartbeatOptions { Enabled = false }
                    }
                ]
            }, ct).ConfigureAwait(false);

            if (!serverStart.Succeeded)
            {
                var msg = string.Join("；", serverStart.Issues.Select(i => i.Code + ": " + i.Message));
                await StopCoreAsync().ConfigureAwait(false);
                throw new InvalidOperationException("服务端驱动启动失败：" + msg);
            }

            _serverPort = _server.GetListenerPorts().TryGetValue("bench-server", out var bound) ? bound : port;

            // ---- 客户端驱动：主动连服务端 ----
            _client = BuildDriver(clientProtocolId);
            var clientStart = await _client.StartAsync(new DriverConfig
            {
                Devices =
                [
                    new DeviceDescriptor
                    {
                        DeviceId = "bench-client",
                        KindId = clientProtocolId,
                        Endpoints =
                        [
                            new EndpointDescriptor { Host = "127.0.0.1", Port = _serverPort }
                        ],
                        Retry = new RetryOptions { InitialMs = 200, MaxMs = 1500, Jitter = 0.1, ConnectTimeoutMs = 2000 },
                        Heartbeat = new HeartbeatOptions { Enabled = false },
                        Handshake = new HandshakeOptions { TimeoutMs = 3000 }
                    }
                ]
            }, ct).ConfigureAwait(false);

            if (!clientStart.Succeeded)
            {
                var msg = string.Join("；", clientStart.Issues.Select(i => i.Code + ": " + i.Message));
                await StopCoreAsync().ConfigureAwait(false);
                throw new InvalidOperationException("客户端驱动启动失败：" + msg);
            }

            StartServerEchoPump(serverProtocolId);

            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 服务端应答泵：入站会话本身只投递帧，由业务决定如何回应。
    /// 测试台在这里实现「按协议应答」，让客户端驱动的交换能拿到响应。
    /// </summary>
    private void StartServerEchoPump(string protocolId)
    {
        if (_server is null)
        {
            return;
        }

        var descriptor = ProtocolCatalog.Find(protocolId);
        if (descriptor is null)
        {
            return;
        }

        var server = _server;
        _serverPumpCts?.Dispose();
        _serverPumpCts = new CancellationTokenSource();
        var token = _serverPumpCts.Token;

        _serverPump = Task.Run(async () =>
        {
            var tracked = new HashSet<long>();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var session in server.GetInboundSessions("bench-server"))
                    {
                        var id = session.ConnectionId.Value;
                        if (!tracked.Add(id))
                        {
                            continue;
                        }

                        _ = Task.Run(() => ServeSessionAsync(session, descriptor, token), token);
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    RecordPumpError(ex.Message);
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <summary>在一条入站会话上循环读取并应答，直到会话结束。</summary>
    private async Task ServeSessionAsync(
        InboundSessionHandle session,
        ProtocolDescriptor descriptor,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            PushFrame? frame;
            try
            {
                frame = await session.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (frame is null)
            {
                return;
            }

            using (frame)
            {
                var payload = StripHeader(frame.Payload.Span, descriptor.Id);
                byte[] reply;
                if (payload.SequenceEqual("PING"u8) && descriptor.SupportsHeartbeat)
                {
                    reply = "PONG"u8.ToArray();
                }
                else if (payload.SequenceEqual("HELLO"u8) && descriptor.SupportsHandshake)
                {
                    reply = "OK"u8.ToArray();
                }
                else
                {
                    reply = descriptor.ServerResponder(payload);
                }

                if (reply.Length == 0)
                {
                    continue;
                }

                try
                {
                    await session.SendFrameAsync(reply, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    /// <summary>去掉协议头，取出业务载荷。</summary>
    private static byte[] StripHeader(ReadOnlySpan<byte> frame, string protocolId) => protocolId switch
    {
        "length-prefix" => frame.Length > 2 ? frame[2..].ToArray() : [],
        // 请求帧 = 6 字节 MBAP 头 + 单元ID 1 + 功能码 1 + 数据，业务载荷从第 8 字节开始。
        "modbus-like" => frame.Length > 8 ? frame[8..].ToArray() : [],
        // 标记协议：剥掉起始 0x02 与结束 0x03，取中间业务数据。
        "stx-etx" => frame.Length > 2 ? frame[1..^1].ToArray() : [],
        _ => frame.ToArray()
    };

    private LiteDriver BuildDriver(string protocolId)
    {
        // 两个驱动都注册全部内置协议：切换协议只需改配置，无需重建驱动实例。
        var builder = new LiteDriverBuilder();
        foreach (var p in ProtocolCatalog.List)
        {
            var factory = p.Factory;
            builder.RegisterKind(p.Id, ctx => factory(ctx));
        }

        return builder.UseObserver(_observer).Build();
    }

    public async Task<BenchStatus> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        _serverPumpCts?.Cancel();
        try
        {
            if (_serverPump is not null)
            {
                await _serverPump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException)
        {
            // 泵未按时退出不阻塞停机。
        }

        _serverPump = null;

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    /// <summary>当前状态快照。</summary>
    public BenchStatus Status()
    {
        var clientState = LinkState.Stopped;
        var clientEpoch = 0L;
        if (_client is not null && _client.TryGetDevice("bench-client", out var handle))
        {
            clientState = handle.State;
            clientEpoch = handle.Epoch;
        }

        return new BenchStatus(
            ServerRunning: _server is not null,
            ClientRunning: _client is not null,
            ServerPort: _serverPort,
            ClientProtocolId: _clientProtocolId,
            ServerProtocolId: _serverProtocolId,
            ClientState: clientState.ToString(),
            ClientEpoch: clientEpoch,
            InboundSessions: _server?.GetListenerSessionCount("bench-server") ?? 0,
            RecentEvents: [.. _recentEventDtos]);
    }

    /// <summary>
    /// 端到端连通测试：客户端发一帧，服务端按协议应答，逐项断言。
    /// </summary>
    public async Task<ConnectivityReport> RunConnectivityAsync(string message, CancellationToken ct)
    {
        if (_client is null || _server is null)
        {
            return ConnectivityReport.Fail("两个驱动尚未启动，请先点「启动」");
        }

        var steps = new List<CheckStep>();
        var protocol = ProtocolCatalog.Find(_clientProtocolId);

        // 1) 等客户端链路 Ready
        try
        {
            await _client.WaitUntilReadyAsync("bench-client", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            steps.Add(new CheckStep(true, "客户端链路 Ready", $"epoch={_client.GetDevice("bench-client").Epoch}"));
        }
        catch (Exception ex)
        {
            steps.Add(new CheckStep(false, "客户端链路 Ready", ex.Message));
            return new ConnectivityReport(false, steps, null, null);
        }

        // 2) 服务端应出现入站会话
        var sessionDeadline = DateTime.UtcNow.AddSeconds(5);
        var inbound = 0;
        while (DateTime.UtcNow < sessionDeadline)
        {
            inbound = _server.GetListenerSessionCount("bench-server");
            if (inbound > 0)
            {
                break;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        steps.Add(new CheckStep(inbound > 0, "服务端出现入站会话", $"sessions={inbound}"));
        if (inbound == 0)
        {
            return new ConnectivityReport(false, steps, null, null);
        }

        // 3) 客户端发出请求并拿到响应
        var payload = Encoding.UTF8.GetBytes(message);
        using var result = await _client.GetDevice("bench-client")
            .ExchangeAsync(payload, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        var executed = result.Disposition == CommandDisposition.Executed;
        steps.Add(new CheckStep(executed, "客户端收到应答", $"disposition={result.Disposition}"));
        if (!executed)
        {
            return new ConnectivityReport(false, steps, null, null);
        }

        // 4) 服务端确实收到并回应了内容
        var responseText = ExtractPayload(result.Response.Span, _clientProtocolId);
        var containsEcho = responseText.Contains("ECHO:", StringComparison.Ordinal);
        steps.Add(new CheckStep(containsEcho, "服务端应答内容符合协议约定", $"response='{responseText}'"));

        // 5) 双向独立确认：用裸 socket 再打一次服务端，验证服务端不依赖客户端驱动也工作
        var raw = await RawProbeAsync(_serverPort, payload, _serverProtocolId, ct).ConfigureAwait(false);
        steps.Add(new CheckStep(raw.Ok, "裸 socket 直连服务端可用", raw.Detail));

        return new ConnectivityReport(steps.All(s => s.Passed), steps, responseText, null);
    }

    /// <summary>
    /// 用裸 socket 直接向服务端发一帧，绕开客户端驱动，单独验证服务端模式。
    /// 这是「同时验证两种模式」的关键：即使客户端驱动异常，也能证明服务端本身可用。
    /// </summary>
    private static async Task<(bool Ok, string Detail)> RawProbeAsync(
        int port,
        byte[] payload,
        string protocolId,
        CancellationToken ct)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct).ConfigureAwait(false);

            var frame = BuildRawFrame(payload, protocolId);
            await socket.SendAsync(frame, System.Net.Sockets.SocketFlags.None, ct).ConfigureAwait(false);

            // 行协议与握手协议需要先应答服务端可能的握手帧。
            var buffer = new byte[512];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var read = await socket.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None, cts.Token)
                .ConfigureAwait(false);

            return read > 0
                ? (true, $"raw bytes={read}")
                : (false, "裸 socket 未收到任何字节");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>按协议构造一帧原始字节，用于裸 socket 探测。</summary>
    private static byte[] BuildRawFrame(byte[] payload, string protocolId)
    {
        switch (protocolId)
        {
            case "length-prefix":
            {
                var frame = new byte[2 + payload.Length];
                BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
                payload.CopyTo(frame, 2);
                return frame;
            }

            case "line-terminator":
            {
                var frame = new byte[payload.Length + 1];
                payload.CopyTo(frame, 0);
                frame[^1] = (byte)'\n';
                return frame;
            }

            case "fixed-length":
            {
                var frame = new byte[Protocols.FixedLengthKind.FrameLength];
                Array.Fill(frame, (byte)' ');
                Array.Copy(payload, frame, Math.Min(payload.Length, frame.Length));
                return frame;
            }

            case "stx-etx":
            {
                // 裸 socket 探测也要按标记协议构造：02 + 数据 + 03
                var frame = new byte[payload.Length + 2];
                frame[0] = 0x02;
                payload.CopyTo(frame, 1);
                frame[^1] = 0x03;
                return frame;
            }

            case "modbus-like":
            {
                var frame = new byte[6 + payload.Length];
                BinaryPrimitives.WriteUInt16BigEndian(frame, 1);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)payload.Length);
                payload.CopyTo(frame, 6);
                return frame;
            }

            default:
                return payload;
        }
    }

    /// <summary>按协议从响应帧里取出业务载荷（去掉协议头）。</summary>
    private static string ExtractPayload(ReadOnlySpan<byte> response, string protocolId) => protocolId switch
    {
        "length-prefix" => response.Length > 2 ? Encoding.UTF8.GetString(response[2..]) : "",
        "fixed-length" => Encoding.ASCII.GetString(response).TrimEnd(),
        // MBAP 帧 = 6 字节头 + 单元ID 1 + 功能码 1 + 数据，取数据需跳过 8 字节。
        "modbus-like" => response.Length > 8
            ? Encoding.UTF8.GetString(response[8..])
            : ($"<short:{response.Length}:{Convert.ToHexString(response)}>"),
        // 标记协议：去掉首尾标记。
        "stx-etx" => response.Length > 2 ? Encoding.UTF8.GetString(response[1..^1]) : "",
        _ => Encoding.UTF8.GetString(response)
    };

    /// <summary>把测试台自身的泵异常也记进事件流水，便于页面观察。</summary>
    internal void RecordPumpError(string detail)
    {
        _recentEventDtos.Enqueue(new BenchEvent(
            "PumpError", "Inbound", null, "bench-server", 0, 0, 0, detail, 0));
        while (_recentEventDtos.Count > 200 && _recentEventDtos.TryDequeue(out _))
        {
        }
    }

    internal void RecordEvent(in DriverEvent e)
    {
        _recentEventDtos.Enqueue(new BenchEvent(
            e.Kind.ToString(),
            e.Direction.ToString(),
            e.DeviceId,
            e.ListenerId,
            e.ConnectionId.Value,
            e.Generation,
            e.Epoch,
            e.Detail,
            e.Value));
        while (_recentEventDtos.Count > 200 && _recentEventDtos.TryDequeue(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>把驱动事件收进测试台环形缓冲。</summary>
internal sealed class BenchObserver : IDriverObserver
{
    private readonly BenchRuntime _runtime;

    public BenchObserver(BenchRuntime runtime) => _runtime = runtime;

    public void OnEvent(in DriverEvent e) => _runtime.RecordEvent(e);
}

/// <summary>测试台状态。</summary>
public sealed record BenchStatus(
    bool ServerRunning,
    bool ClientRunning,
    int ServerPort,
    string ClientProtocolId,
    string ServerProtocolId,
    string ClientState,
    long ClientEpoch,
    int InboundSessions,
    IReadOnlyList<BenchEvent> RecentEvents);

/// <summary>事件的可序列化投影（驱动内部 struct 不直接对外）。</summary>
public sealed record BenchEvent(
    string Kind,
    string Direction,
    string? DeviceId,
    string? ListenerId,
    long ConnectionId,
    long Generation,
    long Epoch,
    string? Detail,
    long Value);

/// <summary>单项断言。</summary>
public sealed record CheckStep(bool Passed, string Name, string Detail);

/// <summary>连通测试报告。</summary>
public sealed record ConnectivityReport(
    bool Passed,
    IReadOnlyList<CheckStep> Steps,
    string? ResponseText,
    string? Error)
{
    public static ConnectivityReport Fail(string error) =>
        new(false, [new CheckStep(false, "前置条件", error)], null, error);
}
