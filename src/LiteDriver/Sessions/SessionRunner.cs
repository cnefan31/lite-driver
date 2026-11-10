using System.Buffers;
using System.Net.Sockets;
using System.Threading.Channels;
using LiteDriver.Observability;
using LiteDriver.Protocol;

namespace LiteDriver.Sessions;

/// <summary>
/// 一条已建立物理连接的运行器：单读泵 + 单写泵 + 可选心跳 + 握手。
/// 实例与 <see cref="ConnectionContext"/> 一一对应，连接关闭即整体作废。
/// </summary>
internal sealed class SessionRunner
{
    private readonly ConnectionContext _ctx;
    private readonly Action<DriverEvent> _emit;
    private readonly Channel<InboundFrame> _pushQueue;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource<SessionEndReason> _ended =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FrozenHandshakeView _handshake;
    private readonly FrozenHeartbeatView _heartbeat;
    private readonly SemaphoreSlim _singleFlight;
    private byte[] _accumulator;
    private int _accumulated;
    private long _handshakeDeadlineTicks;
    private bool _handshakeDone;
    private int _endedFlag;

    internal SessionRunner(
        ConnectionContext ctx,
        Action<DriverEvent> emit,
        FrozenHandshakeView handshake,
        FrozenHeartbeatView heartbeat,
        CancellationToken lifetimeToken)
    {
        _ctx = ctx;
        _emit = emit;
        _handshake = handshake;
        _heartbeat = heartbeat;
        _accumulator = ArrayPool<byte>.Shared.Rent(ctx.Limits.MaxFrameSize);
        _accumulated = 0;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _singleFlight = new SemaphoreSlim(1, 1);
        _pushQueue = Channel.CreateBounded<InboundFrame>(new BoundedChannelOptions(ctx.Limits.MaxPushQueue)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        _handshakeDone = ctx.Protocol.Initializer is null;
        _handshakeDeadlineTicks = ctx.Time.GetUtcNow().UtcTicks + _handshake.Timeout.Ticks;
    }

    public ConnectionContext Context => _ctx;

    public ChannelReader<InboundFrame> PushReader => _pushQueue.Reader;

    public Task<SessionEndReason> Completion => _ended.Task;

    /// <summary>握手完成时成功；会话提前结束时以异常方式结束。</summary>
    public Task ReadyTask => _ready.Task;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>发送握手帧（若协议要求）并启动读泵、写泵与心跳。</summary>
    public async Task StartAsync()
    {
        if (_ctx.Protocol.Initializer is not null)
        {
            var writer = new ArrayBufferWriter<byte>(64);
            _ctx.Protocol.Initializer.WriteHandshake(writer);
            if (writer.WrittenCount > 0 && !TryEnqueue(writer.WrittenSpan, out _))
            {
                Emit(DriverEventKind.HandshakeFailed, "handshake-enqueue-failed", 0);
                RequestStop(SessionEndReason.HandshakeRejected);
            }
        }
        else
        {
            // 无握手要求的协议在建连后即可用。
            _ready.TrySetResult(true);
        }

        await Task.WhenAll(ReadLoopAsync(), WriteLoopAsync(), HeartbeatLoopAsync()).ConfigureAwait(false);
    }

    /// <summary>请求结束当前会话（幂等）。</summary>
    public void RequestStop(SessionEndReason reason)
    {
        if (Interlocked.Exchange(ref _endedFlag, 1) == 0)
        {
            _ended.TrySetResult(reason);
        }

        _ready.TrySetException(new InvalidOperationException($"会话在握手完成前结束：{reason}"));

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }
    }

    private async Task ReadLoopAsync()
    {
        var ct = _cts.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(_ctx.Limits.ReadBufferSize);
        try
        {
            while (!ct.IsCancellationRequested && !_ctx.IsClosed)
            {
                int read;
                try
                {
                    read = await _ctx.Socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), SocketFlags.None, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RequestStop(SessionEndReason.LocalStop);
                    return;
                }
                catch (SocketException ex)
                {
                    RequestStop(SessionEndReason.RemoteClosed);
                    Emit(DriverEventKind.SessionClosed, ex.SocketErrorCode.ToString(), (long)ex.SocketErrorCode);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    RequestStop(SessionEndReason.LocalStop);
                    return;
                }

                if (read == 0)
                {
                    // 对端优雅关闭：Receive 返回 0 是最确定的拆链信号。
                    Emit(DriverEventKind.SessionClosed, "peer-closed", 0);
                    RequestStop(SessionEndReason.RemoteClosed);
                    return;
                }

                _ctx.MarkInbound();
                if (!AppendAndDrain(buffer.AsSpan(0, read), out var stopReason, out var stopDetail))
                {
                    RequestStop(stopReason);
                    if (stopDetail is not null)
                    {
                        Emit(DriverEventKind.SessionClosed, stopDetail, 0);
                    }

                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>把新字节拼进半帧缓存并逐帧分发。返回 <c>false</c> 表示必须拆链。</summary>
    private bool AppendAndDrain(ReadOnlySpan<byte> incoming, out SessionEndReason reason, out string? detail)
    {
        reason = SessionEndReason.Fault;
        detail = null;

        if (_accumulated + incoming.Length > _ctx.Limits.MaxFrameSize)
        {
            reason = SessionEndReason.FrameLimit;
            detail = "frame-limit";
            Emit(DriverEventKind.FrameLimitExceeded, "accumulate-overflow", _accumulated + incoming.Length);
            return false;
        }

        incoming.CopyTo(_accumulator.AsSpan(_accumulated));
        _accumulated += incoming.Length;

        while (true)
        {
            var span = _accumulator.AsSpan(0, _accumulated);
            var result = _ctx.Protocol.Decoder.Decode(span, out var consumed, out var frame);
            if (result == FrameDecodeResult.NeedMore)
            {
                if (!_handshakeDone && _ctx.Time.GetUtcNow().UtcTicks > _handshakeDeadlineTicks)
                {
                    reason = SessionEndReason.HandshakeTimeout;
                    detail = "handshake-timeout";
                    Emit(DriverEventKind.HandshakeFailed, detail, 0);
                    return false;
                }

                return true;
            }

            if (result == FrameDecodeResult.Fatal)
            {
                reason = SessionEndReason.FrameLimit;
                detail = "fatal-frame";
                Emit(DriverEventKind.FrameLimitExceeded, detail, _accumulated);
                return false;
            }

            if (!Dispatch(frame, out detail))
            {
                reason = SessionEndReason.HandshakeRejected;
                return false;
            }

            // 复制到池化缓冲后前移半帧缓存；视窗本身在下次解析后失效。
            _accumulated -= consumed;
            if (_accumulated > 0)
            {
                _accumulator.AsSpan(consumed, _accumulated).CopyTo(_accumulator);
            }
        }
    }

    private bool Dispatch(ReadOnlySpan<byte> frame, out string? detail)
    {
        detail = null;

        if (!_handshakeDone)
        {
            var verdict = _ctx.Protocol.Initializer!.Inspect(frame);
            switch (verdict)
            {
                case HandshakeVerdict.Pending:
                    return true;
                case HandshakeVerdict.Rejected:
                    detail = "handshake-rejected";
                    Emit(DriverEventKind.HandshakeFailed, detail, 0);
                    return false;
                case HandshakeVerdict.Completed:
                    _handshakeDone = true;
                    _ctx.Protocol.Initializer.OnHandshakeCompleted();
                    _ready.TrySetResult(true);
                    Emit(DriverEventKind.Ready, "handshake-completed", 0);
                    return true;
                default:
                    detail = "handshake-unknown";
                    return false;
            }
        }

        if (_ctx.Protocol.Heartbeat is not null && _ctx.Protocol.Heartbeat.IsProbeResponse(frame))
        {
            _ctx.ResetMisses();
            return true;
        }

        // 响应判定只在出站方向有意义：入站会话没有「我们发出的请求」，
        // 一切帧都应作为上报数据交付给应用，不能走响应策略，否则会误报 UnknownFrame。
        if (_ctx.Direction == ConnectionDirection.Outbound && _ctx.Protocol.Response.IsResponse(frame))
        {
            if (_ctx.InFlightReader.TryRead(out var pending))
            {
                var classification = _ctx.Protocol.Response.Classify(frame);
                var copy = ArrayPool<byte>.Shared.Rent(frame.Length);
                frame.CopyTo(copy);
                var disposition = classification == ResponseClassification.Accepted
                    ? CommandDisposition.Executed
                    : CommandDisposition.Rejected;
                pending.Completion.TrySetResult(new CommandResult(
                    disposition, copy, frame.Length, null, _ctx.ConnectionId, _ctx.Epoch));
                return true;
            }

            Emit(DriverEventKind.UnknownFrame, "no-pending", frame.Length);
        }

        var rented = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(rented);
        var item = new InboundFrame(rented, frame.Length, pooled: true);
        if (!_pushQueue.Writer.TryWrite(item))
        {
            item.Dispose();
            Emit(DriverEventKind.FrameDropped, "push-queue-full", frame.Length);
        }

        return true;
    }

    private async Task WriteLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var frame in _ctx.WriteReader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var ok = false;
                try
                {
                    var sent = 0;
                    while (sent < frame.Length)
                    {
                        // 一次 Send 可能只写出一部分，必须循环直到整帧写完。
                        sent += await _ctx.Socket
                            .SendAsync(frame.Buffer.AsMemory(sent, frame.Length - sent), SocketFlags.None, ct)
                            .ConfigureAwait(false);
                    }

                    ok = true;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    Emit(DriverEventKind.SessionClosed, "send-failed", 0);
                    RequestStop(ex is OperationCanceledException ? SessionEndReason.LocalStop : SessionEndReason.RemoteClosed);
                }
                finally
                {
                    if (frame.Pooled)
                    {
                        ArrayPool<byte>.Shared.Return(frame.Buffer);
                    }

                    frame.Sent?.TrySetResult(ok);
                }

                if (!ok)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        if (_heartbeat.Enabled && _ctx.Protocol.Heartbeat is null)
        {
            // 配置要求应用层心跳，但该设备种类未提供心跳实现：这属能力不匹配，
            // 必须显式告警，不能静默当作已启用。
            Emit(DriverEventKind.ProtocolFault, "heartbeat-unsupported-by-kind", 0);
        }

        if (_ctx.Protocol.Heartbeat is null || !_heartbeat.Enabled)
        {
            // 心跳关闭时也要能响应 Stop：挂到取消令牌上即可。
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _ctx.Time, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }

            return;
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_heartbeat.Interval, _ctx.Time, _cts.Token).ConfigureAwait(false);
                if (_ctx.IsClosed)
                {
                    return;
                }

                var now = _ctx.Time.GetUtcNow().UtcTicks;
                if (_heartbeat.Silent > TimeSpan.Zero && now - _ctx.LastInboundTicks > _heartbeat.Silent.Ticks)
                {
                    Emit(DriverEventKind.HeartbeatTimeout, "silent", _ctx.HeartbeatMisses);
                    RequestStop(SessionEndReason.HeartbeatTimeout);
                    return;
                }

                var sequence = _ctx.NextProbeSequence();
                var buffer = new ArrayBufferWriter<byte>(64);
                _ctx.Protocol.Heartbeat.WriteProbe(sequence, buffer);
                if (!TryEnqueue(buffer.WrittenSpan, out _))
                {
                    Emit(DriverEventKind.SendBackpressure, "heartbeat", 0);
                    continue;
                }

                var misses = _ctx.HeartbeatMisses + 1;
                if (misses >= _heartbeat.Misses)
                {
                    Emit(DriverEventKind.HeartbeatTimeout, "misses", misses);
                    RequestStop(SessionEndReason.HeartbeatTimeout);
                    return;
                }

                _ctx.IncrementMisses();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    /// <summary>把载荷组帧并入写队列。返回 <c>false</c> 表示队列已满或会话已关闭。</summary>
    internal bool TryEnqueue(ReadOnlySpan<byte> payload, out TaskCompletionSource<bool>? sent)
    {
        sent = null;
        if (_ctx.IsClosed)
        {
            return false;
        }

        var writer = new ArrayBufferWriter<byte>(payload.Length + 8);
        _ctx.Protocol.Encoder.Encode(payload, writer);
        var length = writer.WrittenCount;
        if (length > _ctx.Limits.MaxFrameSize)
        {
            Emit(DriverEventKind.FrameLimitExceeded, "encode-overflow", length);
            return false;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        writer.WrittenSpan.CopyTo(rented);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sent = tcs;
        if (_ctx.WriteWriter.TryWrite(new OutboundFrame(rented, length, pooled: true, tcs)))
        {
            return true;
        }

        ArrayPool<byte>.Shared.Return(rented);
        sent = null;
        Emit(DriverEventKind.SendBackpressure, "queue-full", 0);
        return false;
    }

    /// <summary>
    /// 发送一个请求并等待响应。严格单在途：信号量保证同一连接上同时只有一个交换。
    /// </summary>
    internal async ValueTask<CommandResult> ExchangeAsync(
        ReadOnlyMemory<byte> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (_ctx.IsClosed)
        {
            return CommandResult.None(CommandDisposition.NotSent, "closed", _ctx.ConnectionId, _ctx.Epoch);
        }

        var acquired = false;
        try
        {
            await _singleFlight.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
        }
        catch (OperationCanceledException)
        {
            return CommandResult.None(CommandDisposition.NotSent, "canceled-before-enqueue", _ctx.ConnectionId, _ctx.Epoch);
        }

        try
        {
            var pending = new PendingRequest(_ctx.Epoch, _ctx.ConnectionId, timeout);
            if (!_ctx.InFlightWriter.TryWrite(pending))
            {
                return CommandResult.None(
                    CommandDisposition.BackpressureRejected, "in-flight-busy", _ctx.ConnectionId, _ctx.Epoch);
            }

            // 必须先登记 pending 再发送，否则快速响应可能先到而无人接收。
            if (!TryEnqueue(payload.Span, out var sent))
            {
                _ctx.InFlightReader.TryRead(out _);
                return CommandResult.None(
                    CommandDisposition.BackpressureRejected, "write-queue-full", _ctx.ConnectionId, _ctx.Epoch);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeoutCts.CancelAfter(timeout);
            try
            {
                var completed = await pending.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                return completed;
            }
            catch (OperationCanceledException)
            {
                _ctx.InFlightReader.TryRead(out _);
                var reason = ct.IsCancellationRequested ? "caller-canceled" : "timeout-or-disconnected";
                // 只有“确认没写出去”才允许标记 NotSent；其余一律 Unknown，禁止调用方据此自动重放。
                var neverWritten = sent is { Task.IsCompletedSuccessfully: true } && !sent.Task.Result;
                var disposition = neverWritten
                    ? CommandDisposition.NotSent
                    : CommandDisposition.SentOutcomeUnknown;
                return CommandResult.None(disposition, reason, _ctx.ConnectionId, _ctx.Epoch);
            }
        }
        finally
        {
            if (acquired)
            {
                // 拆链路径可能已先一步释放信号量；在途 Exchange 的 finally 必须能被容忍，
                // 否则会抛 ObjectDisposedException 覆盖掉真正的命令结果语义。
                try
                {
                    _singleFlight.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 会话已释放，无需归还许可。
                }
            }
        }
    }

    /// <summary>排空在途请求与推送队列，给所有等待者一个确定终态。</summary>
    internal void DrainPending(string errorCode)
    {
        while (_ctx.InFlightReader.TryRead(out var pending))
        {
            pending.Completion.TrySetResult(CommandResult.None(
                CommandDisposition.SentOutcomeUnknown, errorCode, _ctx.ConnectionId, _ctx.Epoch));
            Emit(DriverEventKind.PendingAbandoned, errorCode, 0);
        }

        _pushQueue.Writer.TryComplete();
        while (_pushQueue.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }
    }

    internal void ReleaseResources()
    {
        var buffer = _accumulator;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _accumulator = [];
        }

        _accumulated = 0;
        _pushQueue.Writer.TryComplete();
        while (_pushQueue.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }

        // 刻意不 Dispose _singleFlight：ExchangeAsync 的 finally 可能仍在归还许可。
        // SemaphoreSlim 不持有非托管资源，由 GC 回收即可，避免竞态导致的 ObjectDisposedException。
        _cts.Dispose();
    }

    internal void Emit(DriverEventKind kind, string? detail, long value) => _emit(new DriverEvent
    {
        Kind = kind,
        Direction = _ctx.Direction,
        DeviceId = _ctx.DeviceId,
        ListenerId = _ctx.ListenerId,
        ConnectionId = _ctx.ConnectionId,
        Epoch = _ctx.Epoch,
        Endpoint = null,
        Detail = detail,
        Value = value,
        TimestampTicks = _ctx.Time.GetUtcNow().UtcTicks
    });
}

/// <summary>握手视图：把冻结配置投影为会话运行器需要的最小集合。</summary>
internal readonly record struct FrozenHandshakeView(bool Enabled, TimeSpan Timeout);

/// <summary>心跳视图。</summary>
internal readonly record struct FrozenHeartbeatView(
    bool Enabled,
    TimeSpan Interval,
    int Misses,
    TimeSpan Silent,
    TimeSpan ResponseTimeout);
