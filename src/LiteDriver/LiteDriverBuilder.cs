using LiteDriver.Observability;
using LiteDriver.Protocol;
using LiteDriver.Transport;

namespace LiteDriver;

/// <summary>
/// 驱动构建器。在 <see cref="Build"/> 时冻结设备种类注册表；之后不可再注册，
/// 保证 NativeAOT 下所有种类在编译产物中静态可达。
/// </summary>
public sealed class LiteDriverBuilder
{
    private readonly KindRegistry _kinds = new();
    private DriverLimits _limits = new();
    private ITransportFactory _transport = SocketTransportFactory.Instance;
    private IEndpointResolver _resolver = SystemEndpointResolver.Instance;
    private IChaosPolicy _chaos = NullChaosPolicy.Instance;
    private TimeProvider _time = TimeProvider.System;
    private IDriverObserver _observer = NullDriverObserver.Instance;

    /// <summary>
    /// 注册设备种类。<paramref name="kindId"/> 是配置里的稳定字符串键，<b>不是 CLR 类型名</b>；
    /// 工厂委托必须是静态可分析的（推荐 <c>static ctx =&gt; new XxxKind(ctx)</c>）。
    /// </summary>
    public LiteDriverBuilder RegisterKind(string kindId, Func<ProtocolContext, IDeviceKind> factory)
    {
        _kinds.Register(kindId, factory, null);
        return this;
    }

    /// <summary>注册设备种类，并附带其私有参数校验器（把错误提前到配置校验阶段）。</summary>
    public LiteDriverBuilder RegisterKind(
        string kindId,
        Func<ProtocolContext, IDeviceKind> factory,
        KindSettingsValidator settingsValidator)
    {
        ArgumentNullException.ThrowIfNull(settingsValidator);
        _kinds.Register(kindId, factory, settingsValidator);
        return this;
    }

    public LiteDriverBuilder UseLimits(DriverLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        return this;
    }

    public LiteDriverBuilder UseTransportFactory(ITransportFactory transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        return this;
    }

    public LiteDriverBuilder UseEndpointResolver(IEndpointResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        return this;
    }

    /// <summary>注入故障策略。<b>仅供测试</b>；生产不要接入。</summary>
    public LiteDriverBuilder UseChaosPolicy(IChaosPolicy chaos)
    {
        ArgumentNullException.ThrowIfNull(chaos);
        _chaos = chaos;
        return this;
    }

    /// <summary>注入时间源，测试用 <c>FakeTimeProvider</c> 可快进退避与心跳。</summary>
    public LiteDriverBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        return this;
    }

    public LiteDriverBuilder UseObserver(IDriverObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observer = observer;
        return this;
    }

    /// <summary>构建驱动并冻结注册表。</summary>
    public LiteDriver Build() => BuildWithObserver(_observer);

    /// <summary>
    /// 用外部提供的观察者构建驱动，其余配置沿用本 builder。
    /// 供 DI 集成层使用：先收集种类注册，再从容器取观察者。
    /// </summary>
    public LiteDriver BuildWithObserver(IDriverObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _kinds.Freeze();
        var limits = _limits.ValidateAndFreeze();
        return new LiteDriver(_kinds, limits, _transport, _resolver, _chaos, _time, observer);
    }
}
