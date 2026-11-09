using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDriver.Configuration;

/// <summary>完整驱动配置。可变 POCO 仅用于反序列化；进入内核前会冻结为不可变快照。</summary>
public sealed class DriverConfig
{
    public DeviceDescriptor[] Devices { get; init; } = [];

    public ListenerDescriptor[] Listeners { get; init; } = [];
}

/// <summary>一个逻辑设备的配置。<c>KindId</c> 是启动期注册的键，不是 CLR 类型名。</summary>
public sealed class DeviceDescriptor
{
    /// <summary>逻辑设备标识，在驱动内唯一。</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>设备种类键，必须已在 <c>LiteDriverBuilder.RegisterKind</c> 注册。</summary>
    public string KindId { get; init; } = "";

    /// <summary>按顺序尝试的端点列表。</summary>
    public EndpointDescriptor[] Endpoints { get; init; } = [];

    public RetryOptions Retry { get; init; } = new();

    public HeartbeatOptions Heartbeat { get; init; } = new();

    public HandshakeOptions Handshake { get; init; } = new();

    /// <summary>设备种类私有参数。由该种类按固定键校验，不是任意对象图。</summary>
    public JsonElement? KindSettings { get; init; }
}

/// <summary>拨号端点。</summary>
public sealed class EndpointDescriptor
{
    public string Host { get; init; } = "";

    public int Port { get; init; }

    /// <summary>端点标识，仅用于日志与冷却区分；为空时由序号生成。</summary>
    public string? EndpointId { get; init; }

    /// <summary>连接成功后需要的额外静默时间（部分设备限制连接速率）。</summary>
    public int SettleMs { get; init; }
}

/// <summary>重连退避参数。</summary>
public sealed class RetryOptions
{
    /// <summary>首次退避时长。</summary>
    public int InitialMs { get; init; } = 500;

    /// <summary>退避上限。</summary>
    public int MaxMs { get; init; } = 30_000;

    /// <summary>抖动比例，0 到 1。</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>单次拨号超时。</summary>
    public int ConnectTimeoutMs { get; init; } = 3_000;

    /// <summary>单端点失败后的冷却时长，冷却期内优先使用其他端点。</summary>
    public int EndpointCooldownMs { get; init; } = 0;
}

/// <summary>应用层心跳参数。协议无心跳时应当关闭。</summary>
public sealed class HeartbeatOptions
{
    public bool Enabled { get; init; }

    /// <summary>探测间隔。</summary>
    public int IntervalMs { get; init; } = 5_000;

    /// <summary>连续多少次未回应判定链路失效。</summary>
    public int Misses { get; init; } = 3;

    /// <summary>整体静默上限（入站与探测帧均无）；0 表示不限制。</summary>
    public int SilentMs { get; init; } = 20_000;

    /// <summary>单次探测等待回应的超时。</summary>
    public int ResponseTimeoutMs { get; init; } = 2_000;
}

/// <summary>应用握手参数。</summary>
public sealed class HandshakeOptions
{
    /// <summary>握手整体超时。</summary>
    public int TimeoutMs { get; init; } = 3_000;
}

/// <summary>监听端点配置。</summary>
public sealed class ListenerDescriptor
{
    public string ListenerId { get; init; } = "";

    /// <summary>入站会话使用的设备种类键。</summary>
    public string KindId { get; init; } = "";

    public string Host { get; init; } = "0.0.0.0";

    public int Port { get; init; }

    /// <summary>listen backlog。</summary>
    public int Backlog { get; init; } = 32;

    /// <summary>该端点的最大并发会话数。</summary>
    public int MaxSessions { get; init; } = 64;

    public HandshakeOptions Handshake { get; init; } = new();

    public HeartbeatOptions Heartbeat { get; init; } = new();

    public JsonElement? KindSettings { get; init; }
}

/// <summary>源码生成上下文：核心唯一允许的 JSON 入口。反射式重载在 AOT 下被禁用。</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DriverConfig))]
[JsonSerializable(typeof(DeviceDescriptor))]
[JsonSerializable(typeof(ListenerDescriptor))]
[JsonSerializable(typeof(EndpointDescriptor))]
[JsonSerializable(typeof(RetryOptions))]
[JsonSerializable(typeof(HeartbeatOptions))]
[JsonSerializable(typeof(HandshakeOptions))]
[JsonSerializable(typeof(DriverLimits))]
[JsonSerializable(typeof(JsonElement))]
public partial class LiteDriverJsonContext : JsonSerializerContext
{
}
