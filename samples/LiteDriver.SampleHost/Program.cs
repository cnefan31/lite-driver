using System.Text;
using LiteDriver.Configuration;
using LiteDriver.Hosting;
using LiteDriver.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteDriver.SampleHost;

/// <summary>
/// 通用主机示例：用 <see cref="Host.CreateApplicationBuilder"/> 承载 LiteDriver，
/// 演示注册设备种类、应用初始配置、运行期增删改、单在途交换与优雅停机。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // ---- 通用主机集成：注册驱动 + 显式注册设备种类 ----
        builder.Services.AddLiteDriver(
            kinds =>
            {
                // 静态工厂委托：NativeAOT 下全部分支在编译期可达。
                kinds.RegisterKind(
                    "line-lp",
                    static ctx => new LineDeviceKind(ctx),
                    LineKindSettings.Validate);
                kinds.RegisterKind("line-hb", static ctx => new LineHeartbeatKind(ctx));
            },
            host =>
            {
                host.InitialConfig = new DriverConfig
                {
                    Devices = [],
                    Listeners = []
                };
            });

        builder.Services.AddLiteDriverObserver<LoggingObserver>();

        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SampleHost");
        var driver = host.Services.GetRequiredService<LiteDriver>();

        // 一个本地模拟设备，供出站链路验证。
        await using var fake = new FakeDeviceServer();
        fake.Start();
        logger.LogInformation("模拟设备已启动，端口 {Port}", fake.Port);

        await host.StartAsync();

        var added = await driver.AddDeviceAsync(new DeviceDescriptor
        {
            DeviceId = "press-01",
            KindId = "line-lp",
            Endpoints = [new EndpointDescriptor { Host = "127.0.0.1", Port = fake.Port }],
            Retry = new RetryOptions { InitialMs = 200, MaxMs = 2000 },
            Heartbeat = new HeartbeatOptions { Enabled = false }
        });
        logger.LogInformation("新增设备：{Status} rev={Revision}", added.Status, added.Revision);

        // 另起一个监听端点，演示入站链路。
        var listener = await driver.AddListenerAsync(new ListenerDescriptor
        {
            ListenerId = "gateway-01",
            KindId = "line-lp",
            Host = "127.0.0.1",
            Port = 0,
            MaxSessions = 8
        });
        logger.LogInformation("新增监听端点：{Status}", listener.Status);
        if (driver.GetListenerPorts().TryGetValue("gateway-01", out var boundPort))
        {
            logger.LogInformation("监听端点实际端口 {Port}", boundPort);
        }

        // ---- 等链路就绪并做一次单在途交换 ----
        try
        {
            await driver.WaitUntilReadyAsync("press-01", TimeSpan.FromSeconds(10));
            var handle = driver.GetDevice("press-01");
            using var result = await handle.ExchangeAsync("PING"u8.ToArray(), TimeSpan.FromSeconds(5));
            // 本例的帧含 2 字节长度前缀，打印时跳过前缀只显示载荷。
            var payload = result.Response.Length > 2 ? result.Response[2..] : default;
            var text = result.Disposition == CommandDisposition.Executed
                ? Encoding.ASCII.GetString(payload.Span)
                : "(无响应载荷)";
            logger.LogInformation(
                "交换结果 {Disposition} 载荷='{Text}' conn={Conn} epoch={Epoch}",
                result.Disposition, text, result.ConnectionId, result.Epoch);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning("等待链路就绪超时：{Message}", ex.Message);
        }

        // ---- 演示设备侧断链后的自动重连 ----
        logger.LogInformation("触发一次设备侧断链，观察自动重连…");
        var beforeConn = fake.TotalConnections;
        try
        {
            await driver.GetDevice("press-01").SendFrameAsync("#drop"u8.ToArray(), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // 设备可能已在此之前断开，忽略。
        }

        var reconnected = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (fake.TotalConnections > beforeConn && driver.GetDevice("press-01").State == LinkState.Ready)
            {
                reconnected = true;
                break;
            }

            await Task.Delay(200);
        }

        logger.LogInformation(
            "重连{Result}：连接次数 {Count}，当前状态 {State}",
            reconnected ? "成功" : "未在预期时间内完成", fake.TotalConnections, driver.GetDevice("press-01").State);

        logger.LogInformation("驱动 rev={Revision}，收尾演示增删改…", driver.Revision);
        var removed = await driver.RemoveDeviceAsync("press-01");
        logger.LogInformation("删除设备：{Status}", removed.Status);

        logger.LogInformation("开始停机…");
        await host.StopAsync();
        logger.LogInformation("已停机，模拟设备累计命令 {Count} 条", fake.TotalCommands);
        return 0;
    }
}

/// <summary>把驱动事件桥接到 <see cref="ILogger"/>，演示观测接入方式。</summary>
public sealed class LoggingObserver : IDriverObserver
{
    private readonly ILogger<LoggingObserver> _logger;

    public LoggingObserver(ILogger<LoggingObserver> logger) => _logger = logger;

    public void OnEvent(in DriverEvent e)
    {
        // 只记录有价值的状态迁移，避免高频事件刷屏。
        var level = e.Kind switch
        {
            DriverEventKind.Ready => LogLevel.Information,
            DriverEventKind.Faulted or DriverEventKind.HandshakeFailed or DriverEventKind.HeartbeatTimeout => LogLevel.Warning,
            DriverEventKind.ConfigApplied or DriverEventKind.DriverStarted or DriverEventKind.DriverStopped => LogLevel.Information,
            DriverEventKind.StateChanged => LogLevel.Debug,
            _ => LogLevel.Trace
        };

        _logger.Log(level, "[{Kind}] {Target} conn={Conn} gen={Gen} epoch={Epoch} {Detail}",
            e.Kind, e.DeviceId ?? e.ListenerId ?? "-", e.ConnectionId, e.Generation, e.Epoch, e.Detail);
    }
}
