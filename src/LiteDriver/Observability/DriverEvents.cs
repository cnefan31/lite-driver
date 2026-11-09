namespace LiteDriver.Observability;

/// <summary>驱动事件类别。</summary>
public enum DriverEventKind
{
    DriverStarted,
    DriverStopped,
    ConfigApplied,
    ConfigRolledBack,
    DeviceAdded,
    DeviceUpdated,
    DeviceRemoved,
    ListenerAdded,
    ListenerUpdated,
    ListenerRemoved,
    StateChanged,
    Resolving,
    Connecting,
    Connected,
    HandshakeFailed,
    Ready,
    Degraded,
    HeartbeatTimeout,
    FrameLimitExceeded,
    FrameCacheTimeout,
    ProtocolFault,
    FrameDropped,
    StaleEpochDropped,
    UnknownFrame,
    PendingAbandoned,
    SendBackpressure,
    SessionAccepted,
    SessionRejected,
    SessionClosed,
    RetryScheduled,
    Faulted,
    CleanupTimeout,
    ObserverFault
}

/// <summary>
/// 一条结构化事件。字段固定、无装箱、无反射，便于 NativeAOT 与高频路径。
/// </summary>
public readonly struct DriverEvent
{
    public DriverEventKind Kind { get; init; }

    public ConnectionDirection Direction { get; init; }

    public string? DeviceId { get; init; }

    public string? ListenerId { get; init; }

    public ConnectionId ConnectionId { get; init; }

    public long Generation { get; init; }

    public long Epoch { get; init; }

    public string? Endpoint { get; init; }

    /// <summary>错误码或补充说明；正常事件为 <c>null</c>。</summary>
    public string? Detail { get; init; }

    /// <summary>相关字节数、次数等数值，语义随 <see cref="Kind"/> 而定。</summary>
    public long Value { get; init; }

    public long TimestampTicks { get; init; }

    public override string ToString()
    {
        var target = DeviceId ?? ListenerId ?? "-";
        return Detail is null
            ? $"{Kind} {Direction} {target} conn={ConnectionId} gen={Generation} epoch={Epoch} endpoint={Endpoint} v={Value}"
            : $"{Kind} {Direction} {target} conn={ConnectionId} gen={Generation} epoch={Epoch} endpoint={Endpoint} v={Value} detail={Detail}";
    }
}

/// <summary>
/// 驱动唯一观测出口。实现方抛出异常不会影响驱动运行，但会被记录为 <see cref="DriverEventKind.ObserverFault"/>。
/// </summary>
public interface IDriverObserver
{
    void OnEvent(in DriverEvent e);
}

/// <summary>默认观察者：什么也不做，零分配。</summary>
public sealed class NullDriverObserver : IDriverObserver
{
    public static NullDriverObserver Instance { get; } = new();

    public void OnEvent(in DriverEvent e)
    {
    }
}

/// <summary>把事件写入 <see cref="Action{T}"/> 的观察者，便于示例与测试。</summary>
public sealed class DelegateDriverObserver : IDriverObserver
{
    private readonly Action<DriverEvent> _sink;

    public DelegateDriverObserver(Action<DriverEvent> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public void OnEvent(in DriverEvent e) => _sink(e);
}

/// <summary>事件计数器与最近事件快照，供示例与诊断端点读取。</summary>
public sealed class DriverMetrics : IDriverObserver
{
    private readonly long[] _counters = new long[(int)DriverEventKind.ObserverFault + 1];
    private long _totalEvents;
    private long _lastTimestamp;

    public void OnEvent(in DriverEvent e)
    {
        var index = (int)e.Kind;
        if ((uint)index < (uint)_counters.Length)
        {
            Interlocked.Increment(ref _counters[index]);
        }

        Interlocked.Increment(ref _totalEvents);
        Interlocked.Exchange(ref _lastTimestamp, e.TimestampTicks);
    }

    public long TotalEvents => Interlocked.Read(ref _totalEvents);

    public long LastTimestampTicks => Interlocked.Read(ref _lastTimestamp);

    public long CountOf(DriverEventKind kind)
    {
        var index = (int)kind;
        return (uint)index < (uint)_counters.Length ? Interlocked.Read(ref _counters[index]) : 0;
    }

    public Dictionary<string, long> Snapshot()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < _counters.Length; i++)
        {
            var value = Interlocked.Read(ref _counters[i]);
            if (value != 0)
            {
                result[((DriverEventKind)i).ToString()] = value;
            }
        }

        result["Total"] = TotalEvents;
        return result;
    }
}
