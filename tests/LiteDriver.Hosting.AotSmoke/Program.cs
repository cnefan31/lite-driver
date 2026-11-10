// 本工程命名空间以 LiteDriver. 开头，会遮蔽 LiteDriver 命名空间本身，
// 故用别名精确指向驱动类型（同类歧义已在 README 中记录）。
using Driver = LiteDriver.LiteDriver;
using LiteDriver.Configuration;
using LiteDriver.Hosting;
using LiteDriver.Observability;
using LiteDriver.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LiteDriver.Hosting.AotSmoke;

/// <summary>
/// 通用主机集成的 NativeAOT smoke：在裁剪后的原生二进制里真实构建 Host、
/// 启动 LiteDriver、校验 DI 复用语义与同步释放，逐项断言。
/// </summary>
public static class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        Console.WriteLine("== LiteDriver.Hosting NativeAOT smoke ==");
        Console.WriteLine($"IsDynamicCodeSupported = {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");

        await CheckSingleRegistrationAsync();
        await CheckDuplicateRegistrationAsync();

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Host 能启动、解析出驱动、并且只触发一次 DriverStarted。</summary>
    private static async Task CheckSingleRegistrationAsync()
    {
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Services.AddLiteDriver(
            kinds => kinds.RegisterKind("smoke", static ctx => new SmokeKind(ctx)),
            host => host.InitialConfig = new DriverConfig());
        builder.Services.AddSingleton<CountingObserver>();
        builder.Services.AddSingleton<IDriverObserver>(sp => sp.GetRequiredService<CountingObserver>());

        using var host = builder.Build();
        var observer = host.Services.GetRequiredService<CountingObserver>();

        await host.StartAsync();
        var driver = host.Services.GetRequiredService<Driver>();
        Expect(driver.State == RunState.Running, "Host 启动后驱动为 Running");

        await host.StopAsync();
        Expect(driver.State == RunState.Stopped, "Host 停止后驱动为 Stopped");
        Expect(observer.Started == 1, $"DriverStarted 事件为 1 次（实际 {observer.Started}）");
        Expect(observer.Stopped == 1, $"DriverStopped 事件为 1 次（实际 {observer.Stopped}）");
    }

    /// <summary>重复 AddLiteDriver 不应产生多个驱动实例或多个 HostedService。</summary>
    private static async Task CheckDuplicateRegistrationAsync()
    {
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Services.AddLiteDriver(
            kinds => kinds.RegisterKind("smoke", static ctx => new SmokeKind(ctx)),
            host => host.InitialConfig = new DriverConfig());
        builder.Services.AddLiteDriver(
            kinds => kinds.RegisterKind("smoke", static ctx => new SmokeKind(ctx)),
            host => host.InitialConfig = new DriverConfig());
        builder.Services.AddSingleton<CountingObserver>();
        builder.Services.AddSingleton<IDriverObserver>(sp => sp.GetRequiredService<CountingObserver>());

        using var host = builder.Build();

        var a = host.Services.GetRequiredService<Driver>();
        var b = host.Services.GetRequiredService<Driver>();
        Expect(ReferenceEquals(a, b), "重复注册后仍是同一驱动实例");

        var hostedCount = host.Services.GetServices<IHostedService>().Count();
        Expect(hostedCount == 1, $"重复注册后只有一个 IHostedService（实际 {hostedCount}）");

        var observer = host.Services.GetRequiredService<CountingObserver>();
        await host.StartAsync();
        await host.StopAsync();
        Expect(observer.Started == 1, $"重复注册后只启动一次（实际 {observer.Started}）");
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

/// <summary>计数驱动事件，用于断言启动次数。</summary>
internal sealed class CountingObserver : IDriverObserver
{
    public int Started;
    public int Stopped;

    public void OnEvent(in DriverEvent e)
    {
        if (e.Kind == DriverEventKind.DriverStarted)
        {
            Interlocked.Increment(ref Started);
        }

        if (e.Kind == DriverEventKind.DriverStopped)
        {
            Interlocked.Increment(ref Stopped);
        }
    }
}

/// <summary>smoke 用最简设备种类。</summary>
internal sealed class SmokeKind : IDeviceKind
{
    public SmokeKind(in ProtocolContext context) => _ = context;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new SmokeProtocol(context.Limits.MaxFrameSize);
}

/// <summary>smoke 用透传协议栈。</summary>
internal sealed class SmokeProtocol : ISessionProtocol
{
    public SmokeProtocol(int maxFrameSize) => Decoder = new PassthroughFrameDecoder(maxFrameSize);

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder { get; } = PassthroughFrameEncoder.Instance;

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}
