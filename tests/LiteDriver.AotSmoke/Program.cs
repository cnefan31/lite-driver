using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using LiteDriver.Protocol;

namespace LiteDriver.AotSmoke;

/// <summary>
/// NativeAOT smoke test：在裁剪后的原生二进制里跑真实 TCP 增改删、交换、失败路径与 JSON 源生成，
/// 逐项断言并打印结果。任一项失败即以非零码退出。
/// </summary>
public static class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        Console.WriteLine("== LiteDriver NativeAOT smoke ==");
        Console.WriteLine($"IsDynamicCodeSupported = {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");

        await CheckJsonSourceGenerationAsync();
        await CheckLoopbackAsync();
        await CheckDynamicMutationsAsync();
        await CheckPortConflictRollbackAsync();
        await CheckNotReadyCommandAsync();

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>源生成 JSON 必须可解析；反射式重载应当被禁用。</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026", Justification = "负例断言：必须故意调用反射重载才能验证其被禁用。")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Aot", "IL3050", Justification = "负例断言：必须故意调用反射重载才能验证其被禁用。")]
    private static Task CheckJsonSourceGenerationAsync()
    {
        const string json = """
        { "devices": [ { "deviceId": "d", "kindId": "smoke", "endpoints": [ { "host": "127.0.0.1", "port": 502 } ] } ] }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize(json, LiteDriverJsonContext.Default.DriverConfig);
        Expect(config is not null && config.Devices.Length == 1 && config.Devices[0].DeviceId == "d",
            "源生成 JSON 反序列化配置");

        // 反射式序列化在关闭反射默认值后应失败——这是刻意的负例断言。
        // 此处必须调用反射重载才能验证它确实被禁用，故局部抑制裁剪/AOT 告警。
        var reflectionBlocked = false;
        try
        {
#pragma warning disable IL2026, IL3050 // 负例：故意调用反射重载
            _ = System.Text.Json.JsonSerializer.Serialize(new { a = 1 });
#pragma warning restore IL2026, IL3050
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            reflectionBlocked = true;
        }

        Expect(reflectionBlocked, "反射式 JSON 已被禁用");
        return Task.CompletedTask;
    }

    /// <summary>真实 socket 建连、交换、删除后端口释放。</summary>
    private static async Task CheckLoopbackAsync()
    {
        await using var fake = new SmokeDevice();
        fake.Start();

        await using var driver = NewDriver();
        var start = await driver.StartAsync(new DriverConfig
        {
            Devices =
            [
                new DeviceDescriptor
                {
                    DeviceId = "smoke",
                    KindId = "smoke",
                    Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = fake.Port }],
                    Retry = new RetryOptions { InitialMs = 50, MaxMs = 500, Jitter = 0 }
                }
            ]
        });

        Expect(start.Succeeded, "启动并应用初始配置");

        await driver.WaitUntilReadyAsync("smoke", TimeSpan.FromSeconds(10));
        Expect(driver.GetDevice("smoke").State == LinkState.Ready, "设备进入 Ready");

        using var result = await driver.GetDevice("smoke").ExchangeAsync("PING"u8.ToArray(), TimeSpan.FromSeconds(5));
        Expect(result.Disposition == CommandDisposition.Executed, "交换返回 Executed");
        Expect(result.Response.Length > 2 && Encoding.ASCII.GetString(result.Response.Span[2..]) == "PONG",
            "响应载荷为 PONG");

        var removed = await driver.RemoveDeviceAsync("smoke");
        Expect(removed.Status == MutationStatus.Applied, "删除设备成功");
    }

    /// <summary>运行期新增、更新、删除监听端点，并验证端口真实开闭。</summary>
    private static async Task CheckDynamicMutationsAsync()
    {
        await using var driver = NewDriver();
        await driver.StartAsync(new DriverConfig());

        var added = await driver.AddListenerAsync(new ListenerDescriptor
        {
            ListenerId = "gw", KindId = "smoke", Host = "127.0.0.1", Port = 0, MaxSessions = 4
        });
        Expect(added.Status == MutationStatus.Applied, "运行期新增监听端点");

        var port = driver.GetListenerPorts()["gw"];
        Expect(port > 0, "系统分配真实端口");

        var reachable = await CanConnectAsync(port);
        Expect(reachable, "新增后端口可连接");

        var removed = await driver.RemoveListenerAsync("gw");
        Expect(removed.Status == MutationStatus.Applied, "运行期删除监听端点");

        var closed = !await CanConnectAsync(port);
        Expect(closed, "删除后端口不可连接");
    }

    /// <summary>端口冲突必须回滚，不留半可用状态。</summary>
    private static async Task CheckPortConflictRollbackAsync()
    {
        await using var fake = new SmokeDevice();
        fake.Start();

        await using var driver = NewDriver();
        var result = await driver.StartAsync(new DriverConfig
        {
            Listeners =
            [
                new ListenerDescriptor
                {
                    ListenerId = "busy", KindId = "smoke", Host = "127.0.0.1", Port = fake.Port
                }
            ]
        });

        Expect(!result.Succeeded, "占用端口时启动失败");
        Expect(result.Issues.Any(i => i.Code == "stage_failed"), "失败原因为 stage_failed");
        Expect(driver.State == RunState.Created, "失败后回到 Created");
    }

    /// <summary>未 Ready 时命令返回 NotSent，绝不伪装成功。</summary>
    private static async Task CheckNotReadyCommandAsync()
    {
        await using var driver = NewDriver();
        var deadPort = GetFreePort();

        await driver.StartAsync(new DriverConfig
        {
            Devices =
            [
                new DeviceDescriptor
                {
                    DeviceId = "down",
                    KindId = "smoke",
                    Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = deadPort }],
                    Retry = new RetryOptions { InitialMs = 100, MaxMs = 200, Jitter = 0 }
                }
            ]
        });

        using var result = await driver.GetDevice("down").ExchangeAsync("PING"u8.ToArray());
        Expect(result.Disposition == CommandDisposition.NotSent, "未 Ready 时命令为 NotSent");
        Expect(result.ErrorCode == "not-ready", "错误码为 not-ready");
    }

    private static LiteDriver NewDriver() =>
        new LiteDriverBuilder()
            .RegisterKind("smoke", static ctx => new SmokeKind(ctx))
            .UseObserver(NullDriverObserver.Instance)
            .Build();

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
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

    private static void Expect(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}");
    }
}

/// <summary>smoke 用设备种类：2 字节大端长度前缀，PING→PONG。</summary>
internal sealed class SmokeKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public SmokeKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new SmokeProtocol(_maxFrameSize);
}

internal sealed class SmokeProtocol : ISessionProtocol
{
    private readonly LengthPrefixFrameEncoder _encoder = new(2, bigEndian: true, lengthIncludesHeader: false);

    public SmokeProtocol(int maxFrameSize) =>
        Decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, maxFrameSize);

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

/// <summary>smoke 用模拟设备。</summary>
internal sealed class SmokeDevice : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SmokeDevice() => _listener = new TcpListener(IPAddress.Loopback, 0);

    public int Port { get; private set; }

    public void Start()
    {
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[2];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header))
                    {
                        return;
                    }

                    var length = (header[0] << 8) | header[1];
                    var payload = new byte[length];
                    if (!await ReadExactAsync(stream, payload))
                    {
                        return;
                    }

                    var reply = Encoding.ASCII.GetBytes("PONG");
                    var frame = new byte[2 + reply.Length];
                    frame[0] = (byte)(reply.Length >> 8);
                    frame[1] = (byte)reply.Length;
                    reply.CopyTo(frame, 2);
                    await stream.WriteAsync(frame);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 客户端断开。
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
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
                // 停止超时。
            }
        }

        _cts.Dispose();
    }
}
