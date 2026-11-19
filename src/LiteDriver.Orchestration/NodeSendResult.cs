namespace LiteDriver.Orchestration;

/// <summary>一次向设备节点发送的结果。</summary>
public enum SendStatus
{
    /// <summary>已成功写入 socket。</summary>
    Sent,

    /// <summary>设备未就绪（未连接 / 监听无会话），未发送。</summary>
    NotReady,

    /// <summary>节点未配置编解码器，无法把消息序列化成字节。</summary>
    NoCodec,

    /// <summary>编解码失败（消息类型不符或编码异常）。</summary>
    EncodeFailed,

    /// <summary>节点不存在或不是可发送的设备节点。</summary>
    InvalidNode,

    /// <summary>写入失败（连接已断、队列满等）。</summary>
    WriteFailed
}

/// <summary>
/// 发送结果。
/// <para>
/// <see cref="Status"/> 为 <see cref="SendStatus.Sent"/> 仅表示「字节已交给内核」，
/// <b>不代表设备已执行</b>——这与库内 <c>CommandDisposition</c> 的语义保持一致。
/// </para>
/// </summary>
public sealed record NodeSendResult(
    SendStatus Status,
    string NodeId,
    int BytesWritten,
    int SessionCount,
    string? Error)
{
    /// <summary>是否写入成功。</summary>
    public bool Ok => Status == SendStatus.Sent;

    /// <summary>构造成功结果。</summary>
    public static NodeSendResult Sent(string nodeId, int bytes, int sessions) =>
        new(SendStatus.Sent, nodeId, bytes, sessions, null);

    /// <summary>构造失败结果。</summary>
    public static NodeSendResult Fail(SendStatus status, string nodeId, string error) =>
        new(status, nodeId, 0, 0, error);
}
