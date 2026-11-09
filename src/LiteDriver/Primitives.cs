using System.Buffers;

namespace LiteDriver;

/// <summary>驱动程序自身（对象）的生命周期状态。</summary>
public enum RunState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Disposed
}

/// <summary>单个逻辑设备的链路状态。每次物理连接递增 <c>Epoch</c>。</summary>
public enum LinkState
{
    Idle,
    Resolving,
    Connecting,
    Handshaking,
    Ready,
    Degraded,
    Retrying,
    Faulted,
    Stopped
}

/// <summary>连接的发起方向。</summary>
public enum ConnectionDirection
{
    /// <summary>本端主动拨号到设备侧的 TCP Server。</summary>
    Outbound,

    /// <summary>设备侧主动连入，本端监听并接受。</summary>
    Inbound
}

/// <summary>命令的最终处置。区分“没发出去”“发出去了但结果未知”“设备拒绝”“设备已执行”。</summary>
public enum CommandDisposition
{
    /// <summary>可确认整帧零字节未写出，例如未 Ready、队列拒绝、入队前取消。</summary>
    NotSent,

    /// <summary>整帧已交给内核但无匹配响应，或超时、拆链、部分写出后异常。<b>调用方不得据此自动重放写命令。</b></summary>
    SentOutcomeUnknown,

    /// <summary>收到匹配的应用层否定应答。</summary>
    Rejected,

    /// <summary>收到匹配的应用层接受应答。是否真正生效仍需业务按协议读回确认。</summary>
    Executed,

    /// <summary>调用方取消且可确认调用方在写出前放弃。</summary>
    Canceled,

    /// <summary>写队列或在途表已满，命令未进入发送路径。</summary>
    BackpressureRejected
}

/// <summary>心跳探测结果。</summary>
public enum SessionProbeResult
{
    Alive,
    Dead
}

/// <summary>应用层对响应帧的分类。</summary>
public enum ResponseClassification
{
    /// <summary>接受，映射为 <see cref="CommandDisposition.Executed"/>。</summary>
    Accepted,

    /// <summary>设备明确拒绝，映射为 <see cref="CommandDisposition.Rejected"/>。</summary>
    Rejected
}

/// <summary>物理连接的标识，每次建连递增，不跨重连复用。</summary>
public readonly record struct ConnectionId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>一次配置事务的结果。</summary>
public sealed record ApplyResult(
    long Revision,
    int Added,
    int Updated,
    int Removed,
    int Unchanged,
    IReadOnlyList<ApplyIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;
}

/// <summary>配置事务中的单条问题，<paramref name="Code"/> 供程序判断，<paramref name="Message"/> 供人阅读。</summary>
public sealed record ApplyIssue(string Code, string Message, string? Target = null);

/// <summary>单个增删改操作的结果码。</summary>
public enum MutationStatus
{
    Applied,
    AlreadyExists,
    NotFound,
    Conflict,
    Invalid,
    NotRunning
}

/// <summary>单个增删改操作的结果。</summary>
public sealed record MutationResult(MutationStatus Status, string? Target, long Revision, string? Message = null)
{
    public bool Succeeded => Status == MutationStatus.Applied;
}

/// <summary>单向发送的结果。</summary>
public sealed record SendResult(bool Written, long BytesWritten, string? ErrorCode)
{
    public static SendResult Ok(long bytes) => new(true, bytes, null);
    public static SendResult Fail(string code) => new(false, 0, code);
}

/// <summary>
/// 一次请求-响应交换的结果。响应内存从内存池租用，<see cref="Dispose"/> 时归还；
/// 未调用 <see cref="Dispose"/> 会造成该缓冲区无法归还池（不会崩溃，但会多一次分配）。
/// </summary>
public sealed class CommandResult : IDisposable
{
    private byte[]? _rented;

    internal CommandResult(
        CommandDisposition disposition,
        byte[]? rented,
        int length,
        string? errorCode,
        ConnectionId connectionId,
        long epoch)
    {
        Disposition = disposition;
        _rented = rented;
        Length = length;
        ErrorCode = errorCode;
        ConnectionId = connectionId;
        Epoch = epoch;
    }

    public CommandDisposition Disposition { get; }

    public string? ErrorCode { get; }

    public ConnectionId ConnectionId { get; }

    public long Epoch { get; }

    /// <summary>响应载荷长度；无响应时为 0。</summary>
    public int Length { get; }

    /// <summary>响应载荷。仅在 <see cref="Dispose"/> 之前有效。</summary>
    public ReadOnlyMemory<byte> Response =>
        _rented is null ? ReadOnlyMemory<byte>.Empty : _rented.AsMemory(0, Length);

    internal static CommandResult None(CommandDisposition disposition, string? errorCode, ConnectionId id, long epoch) =>
        new(disposition, null, 0, errorCode, id, epoch);

    public void Dispose()
    {
        var rented = _rented;
        _rented = null;
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>资源上限。所有队列、帧长、会话数都有界，避免无上限内存增长。</summary>
public sealed class DriverLimits
{
    /// <summary>单帧最大字节数，超出即关闭当前链路。默认 1 MiB。</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

    /// <summary>单次 socket 读取的缓冲区大小，从 <see cref="ArrayPool{T}"/> 租用。默认 8 KiB。</summary>
    public int ReadBufferSize { get; init; } = 8 * 1024;

    /// <summary>单连接待发送队列容量，超出返回 <see cref="CommandDisposition.BackpressureRejected"/>。默认 128。</summary>
    public int MaxWriteQueue { get; init; } = 128;

    /// <summary>单个监听端点的最大并发会话数。默认 64。</summary>
    public int MaxSessionsPerListener { get; init; } = 64;

    /// <summary>全部监听端点的最大并发会话总数。默认 512。</summary>
    public int MaxTotalSessions { get; init; } = 512;

    /// <summary>优雅停机的最长等待时间，超时后强制中断。默认 10 秒。</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>半帧缓存的最长保持时间，超时丢弃已缓存字节并关闭链路。默认 1 秒。</summary>
    public TimeSpan FrameCacheTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>推送帧通道容量（每连接）。默认 256。</summary>
    public int MaxPushQueue { get; init; } = 256;

    internal DriverLimits ValidateAndFreeze()
    {
        var issues = new List<string>();
        if (MaxFrameSize < 64) issues.Add("MaxFrameSize 必须 ≥ 64");
        if (ReadBufferSize < 512) issues.Add("ReadBufferSize 必须 ≥ 512");
        if (MaxWriteQueue < 1) issues.Add("MaxWriteQueue 必须 ≥ 1");
        if (MaxSessionsPerListener < 1) issues.Add("MaxSessionsPerListener 必须 ≥ 1");
        if (MaxTotalSessions < MaxSessionsPerListener) issues.Add("MaxTotalSessions 必须 ≥ MaxSessionsPerListener");
        if (StopTimeout <= TimeSpan.Zero) issues.Add("StopTimeout 必须为正");
        if (FrameCacheTimeout <= TimeSpan.Zero) issues.Add("FrameCacheTimeout 必须为正");
        if (MaxPushQueue < 1) issues.Add("MaxPushQueue 必须 ≥ 1");
        if (issues.Count > 0)
        {
            throw new ArgumentException("DriverLimits 非法：" + string.Join("；", issues));
        }

        return new DriverLimits
        {
            MaxFrameSize = MaxFrameSize,
            ReadBufferSize = ReadBufferSize,
            MaxWriteQueue = MaxWriteQueue,
            MaxSessionsPerListener = MaxSessionsPerListener,
            MaxTotalSessions = MaxTotalSessions,
            StopTimeout = StopTimeout,
            FrameCacheTimeout = FrameCacheTimeout,
            MaxPushQueue = MaxPushQueue
        };
    }
}
