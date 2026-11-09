using System.Buffers;
using System.Threading.Channels;
using LiteDriver.Observability;
using LiteDriver.Protocol;

namespace LiteDriver.Sessions;

/// <summary>会话结束原因。</summary>
internal enum SessionEndReason
{
    LocalStop,
    RemoteClosed,
    Fault,
    HeartbeatTimeout,
    FrameLimit,
    FrameCacheTimeout,
    HandshakeRejected,
    HandshakeTimeout,
    ListenerStopped
}

/// <summary>
/// 一条物理连接的全部运行时状态。每次建连新建一个实例，关闭后作废，<b>绝不跨重连复用</b>。
/// 所有异步回调都捕获本对象，投递前核对当前上下文相同。
/// </summary>
internal sealed class ConnectionContext
{
    private readonly Channel<OutboundFrame> _writeQueue;
    private readonly Channel<PendingRequest> _inFlight;
    private int _closed;
    private long _lastInboundTicks;
    private long _heartbeatMisses;
    private long _probeSequence;

    internal ConnectionContext(
        ConnectionId id,
        long epoch,
        System.Net.Sockets.Socket socket,
        ISessionProtocol protocol,
        DriverLimits limits,
        TimeProvider time,
        string? deviceId,
        string? listenerId,
        ConnectionDirection direction,
        string kindId)
    {
        ConnectionId = id;
        Epoch = epoch;
        Socket = socket;
        Protocol = protocol;
        Limits = limits;
        Time = time;
        DeviceId = deviceId;
        ListenerId = listenerId;
        Direction = direction;
        KindId = kindId;
        OpenedAtTicks = time.GetUtcNow().UtcTicks;
        _lastInboundTicks = OpenedAtTicks;

        _writeQueue = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(limits.MaxWriteQueue)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        // 帧格式不含 requestId 时，必须严格单在途，否则响应无法与请求关联。
        _inFlight = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ConnectionId ConnectionId { get; }

    public long Epoch { get; }

    public System.Net.Sockets.Socket Socket { get; }

    public ISessionProtocol Protocol { get; }

    public DriverLimits Limits { get; }

    public TimeProvider Time { get; }

    public string? DeviceId { get; }

    public string? ListenerId { get; }

    public ConnectionDirection Direction { get; }

    public string KindId { get; }

    public long OpenedAtTicks { get; }

    public string TargetId => DeviceId ?? ListenerId ?? "-";

    public ChannelReader<OutboundFrame> WriteReader => _writeQueue.Reader;

    public ChannelWriter<OutboundFrame> WriteWriter => _writeQueue.Writer;

    public ChannelReader<PendingRequest> InFlightReader => _inFlight.Reader;

    public ChannelWriter<PendingRequest> InFlightWriter => _inFlight.Writer;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public long LastInboundTicks => Interlocked.Read(ref _lastInboundTicks);

    public long HeartbeatMisses => Interlocked.Read(ref _heartbeatMisses);

    public void MarkInbound()
    {
        Interlocked.Exchange(ref _lastInboundTicks, Time.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _heartbeatMisses, 0);
    }

    public long NextProbeSequence() => Interlocked.Increment(ref _probeSequence);

    public void IncrementMisses() => Interlocked.Increment(ref _heartbeatMisses);

    public void ResetMisses() => Interlocked.Exchange(ref _heartbeatMisses, 0);

    /// <summary>尝试把上下文标记为关闭。仅第一次成功，用于保证拆链逻辑只执行一次。</summary>
    public bool TryMarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;

    /// <summary>安全关闭 socket，忽略重复关闭与已断开。</summary>
    public void CloseSocket()
    {
        try
        {
            if (Socket.Connected)
            {
                Socket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // 对端已断开或 socket 已释放，属预期路径。
        }

        try
        {
            Socket.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // 同上。
        }
    }
}

/// <summary>写队列中的一帧。所有权转移给写泵，由写泵归还缓冲。</summary>
internal sealed class OutboundFrame
{
    internal OutboundFrame(byte[] buffer, int length, bool pooled, TaskCompletionSource<bool>? sent)
    {
        Buffer = buffer;
        Length = length;
        Pooled = pooled;
        Sent = sent;
    }

    public byte[] Buffer { get; }

    public int Length { get; }

    public bool Pooled { get; }

    /// <summary>写入完成后完成的信号；<c>null</c> 表示调用方不关心。</summary>
    public TaskCompletionSource<bool>? Sent { get; }
}

/// <summary>一次在途请求。帧格式无 requestId 时最多只有一个处于在途状态。</summary>
internal sealed class PendingRequest
{
    internal PendingRequest(long epoch, ConnectionId connectionId, TimeSpan timeout)
    {
        Epoch = epoch;
        ConnectionId = connectionId;
        Timeout = timeout;
        Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public long Epoch { get; }

    public ConnectionId ConnectionId { get; }

    public TimeSpan Timeout { get; }

    public TaskCompletionSource<CommandResult> Completion { get; }
}

/// <summary>一帧入站数据。缓冲区从内存池租用，处置后归还。</summary>
internal sealed class InboundFrame : IDisposable
{
    private byte[]? _rented;
    private readonly int _length;
    private readonly bool _pooled;

    internal InboundFrame(byte[] buffer, int length, bool pooled)
    {
        _rented = buffer;
        _length = length;
        _pooled = pooled;
    }

    public ReadOnlyMemory<byte> Memory => _rented is null ? ReadOnlyMemory<byte>.Empty : _rented.AsMemory(0, _length);

    public void Dispose()
    {
        var rented = _rented;
        _rented = null;
        if (rented is not null && _pooled)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
