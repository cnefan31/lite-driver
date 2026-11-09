using System.Linq;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LiteDriver.Hosting;

/// <summary>通用主机选项。</summary>
public sealed class LiteDriverHostOptions
{
    /// <summary>启动时使用的初始配置。为 <c>null</c> 时驱动以空配置运行。</summary>
    public DriverConfig? InitialConfig { get; set; }

    /// <summary>启动时等待初始配置完成的最长时间。</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>停机时等待在途任务退出的最长时间。</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 把 <see cref="LiteDriver"/> 作为 <see cref="IHostedService"/> 托管。
/// 只转发启动与停止，不重复实现驱动状态机，也不扫描程序集或反射创建类型。
/// </summary>
public sealed class LiteDriverHostedService : IHostedService
{
    private readonly LiteDriver _driver;
    private readonly LiteDriverHostOptions _options;

    public LiteDriverHostedService(LiteDriver driver, LiteDriverHostOptions options)
    {
        _driver = driver;
        _options = options;
    }

    public LiteDriver Driver => _driver;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.StartupTimeout);

        var config = _options.InitialConfig ?? new DriverConfig();
        var result = await _driver.StartAsync(config, cts.Token).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // 启动期配置非法属致命错误：让主机启动失败，而不是带病运行。
            var detail = string.Join("；", result.Issues.Select(static i => i.Code + ": " + i.Message));
            throw new InvalidOperationException("LiteDriver 启动失败：" + detail);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ShutdownTimeout);
        await _driver.StopAsync(cts.Token).ConfigureAwait(false);
    }
}

/// <summary>DI 注册扩展。</summary>
public static class LiteDriverServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="LiteDriver"/> 与其 <see cref="LiteDriverHostedService"/>。
    /// 设备种类在 <paramref name="configureKinds"/> 里用显式工厂注册，不做程序集扫描。
    /// </summary>
    public static IServiceCollection AddLiteDriver(
        this IServiceCollection services,
        Action<LiteDriverBuilder> configureKinds,
        Action<LiteDriverHostOptions>? configureHost = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureKinds);

        var builder = new LiteDriverBuilder();
        configureKinds(builder);

        services.AddSingleton(builder);
        services.AddSingleton(sp =>
        {
            // 观察者是可选依赖：容器内有 IDriverObserver 就用，否则零开销 Null 实现。
            var observer = sp.GetService<IDriverObserver>() ?? NullDriverObserver.Instance;
            var configured = sp.GetRequiredService<LiteDriverBuilder>();
            return LiteDriverFactory.BuildWithObserver(configured, observer);
        });

        services.AddSingleton(sp =>
        {
            var options = new LiteDriverHostOptions();
            configureHost?.Invoke(options);
            return options;
        });

        services.AddHostedService(sp => new LiteDriverHostedService(
            sp.GetRequiredService<LiteDriver>(),
            sp.GetRequiredService<LiteDriverHostOptions>()));

        return services;
    }

    /// <summary>注册一个自定义观察者，供 <c>AddLiteDriver</c> 自动取用。</summary>
    public static IServiceCollection AddLiteDriverObserver<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TObserver>(
        this IServiceCollection services)
        where TObserver : class, IDriverObserver
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDriverObserver, TObserver>();
        return services;
    }
}
