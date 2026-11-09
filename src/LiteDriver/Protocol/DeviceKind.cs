namespace LiteDriver.Protocol;

/// <summary>设备种类。实现者在本端编译期静态可达，通过 <c>LiteDriverBuilder.RegisterKind</c> 注册。</summary>
public interface IDeviceKind
{
    /// <summary>为一条<b>新的物理连接</b>创建协议栈。每次建连都会调用一次，实现必须返回全新实例。</summary>
    ISessionProtocol CreateProtocol(in ProtocolContext context);
}

/// <summary>创建协议栈时提供的上下文。</summary>
public readonly struct ProtocolContext
{
    internal ProtocolContext(
        string targetId,
        ConnectionDirection direction,
        DriverLimits limits,
        System.Text.Json.JsonElement? kindSettings)
    {
        TargetId = targetId;
        Direction = direction;
        Limits = limits;
        KindSettings = kindSettings;
    }

    /// <summary>出站时是 DeviceId，入站时是 ListenerId。</summary>
    public string TargetId { get; }

    public ConnectionDirection Direction { get; }

    /// <summary>已冻结的资源上限。</summary>
    public DriverLimits Limits { get; }

    /// <summary>该种类私有参数，已 <c>Clone()</c>，可安全长期持有。</summary>
    public System.Text.Json.JsonElement? KindSettings { get; }
}

/// <summary>
/// 一条物理连接的完整协议栈。所有成员都必须归属于该连接，不得跨连接共享有状态对象。
/// </summary>
public interface ISessionProtocol
{
    /// <summary>分包器，必须每条连接新建。</summary>
    IFrameDecoder Decoder { get; }

    /// <summary>组帧器。</summary>
    IFrameEncoder Encoder { get; }

    /// <summary>应用层握手；返回 <c>null</c> 表示无需握手。</summary>
    ISessionInitializer? Initializer { get; }

    /// <summary>应用层心跳；返回 <c>null</c> 表示该设备不做应用层探活。</summary>
    IHeartbeatPolicy? Heartbeat { get; }

    /// <summary>响应判定策略；默认实现为“有在途请求时，下一帧即响应”。</summary>
    IResponsePolicy Response { get; }
}

/// <summary>应用层握手。只有握手成功才允许进入 Ready。</summary>
public interface ISessionInitializer
{
    /// <summary>构造首个握手帧；返回 <c>null</c> 表示无需发送，直接视为握手成功。</summary>
    void WriteHandshake(System.Buffers.IBufferWriter<byte> writer);

    /// <summary>判定收到的帧是否完成握手。</summary>
    HandshakeVerdict Inspect(ReadOnlySpan<byte> frame);

    /// <summary>握手成功后调用一次，可记录设备身份（bootId / nonce / 型号）供审计与诊断。</summary>
    void OnHandshakeCompleted();
}

/// <summary>握手判定结论。</summary>
public enum HandshakeVerdict
{
    /// <summary>尚未完成，继续等待后续帧。</summary>
    Pending,

    /// <summary>握手成功，可以进入 Ready。</summary>
    Completed,

    /// <summary>握手失败（认证拒绝、版本不符、身份不匹配）。进入 Faulted，不再无限重拨。</summary>
    Rejected
}

/// <summary>应用层心跳策略。用于区分“设备内核还在回 ACK”与“设备应用真的还活着”。</summary>
public interface IHeartbeatPolicy
{
    /// <summary>构造一次探活帧。</summary>
    void WriteProbe(long sequence, System.Buffers.IBufferWriter<byte> writer);

    /// <summary>判定收到的帧是否是对某次探活的应答。</summary>
    bool IsProbeResponse(ReadOnlySpan<byte> frame);
}

/// <summary>
/// 响应判定。当帧格式不含 requestId 时（首版默认场景），实现必须保证
/// “一个请求发出后，只有一帧会被判定为它的响应”，其余一律走推送通道。
/// </summary>
public interface IResponsePolicy
{
    /// <summary>当前帧是否是对在途请求的响应。</summary>
    bool IsResponse(ReadOnlySpan<byte> frame);

    /// <summary>把响应帧分类为接受或拒绝。</summary>
    ResponseClassification Classify(ReadOnlySpan<byte> frame);
}

/// <summary>
/// 默认响应策略：有在途请求时把下一帧视为响应。<b>仅在协议严格一问一答时正确</b>；
/// 若设备会主动推送，请继承本类覆写 <see cref="IsResponse"/>，或实现自定义 <see cref="IResponsePolicy"/>。
/// </summary>
public class NextFrameResponsePolicy : IResponsePolicy
{
    /// <summary>按帧内容判定接受或拒绝。默认一律视为接受。</summary>
    public virtual ResponseClassification Classify(ReadOnlySpan<byte> frame) => ResponseClassification.Accepted;

    /// <summary>默认为是；设备会主动上报时请覆写。</summary>
    public virtual bool IsResponse(ReadOnlySpan<byte> frame) => true;
}
