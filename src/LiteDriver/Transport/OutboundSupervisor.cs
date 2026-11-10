using System.Net;
using System.Net.Sockets;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using LiteDriver.Protocol;
using LiteDriver.Sessions;

namespace LiteDriver.Transport;

/// <summary>拨号器抽象：测试可替换为故障注入实现，生产用真实 socket。</summary>
public interface ITransportFactory
{
    /// <summary>建立一条 TCP 连接。<paramref name="cancellationToken"/> 必须能中断拨号。</summary>
    ValueTask<Socket> ConnectAsync(EndPoint endpoint, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>DNS 解析抽象，便于测试确定性地模拟 DNS 变化。</summary>
public interface IEndpointResolver
{
    /// <summary>解析主机名。返回空表示解析失败。</summary>
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>默认传输实现：直接使用 <see cref="Socket"/>。</summary>
public sealed class SocketTransportFactory : ITransportFactory
{
    public static SocketTransportFactory Instance { get; } = new();

    public async ValueTask<Socket> ConnectAsync(EndPoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>默认解析器：使用系统 DNS。</summary>
public sealed class SystemEndpointResolver : IEndpointResolver
{
    public static SystemEndpointResolver Instance { get; } = new();

    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

/// <summary>
/// 故障注入策略。仅在测试构建中注入；生产默认 <see cref="NullChaosPolicy"/>，
/// 不能由普通配置静默开启。
/// </summary>
public interface IChaosPolicy
{
    /// <summary>拨号前是否人为失败。</summary>
    bool ShouldFailConnect(string host, int port) => false;

    /// <summary>接受连接后是否立即断开。</summary>
    bool ShouldDropAfterAccept(string listenerId) => false;

    /// <summary>发包时是否故意拆成小块（制造半帧）。</summary>
    bool ShouldFragmentWrites => false;
}

/// <summary>生产默认：不注入任何故障。</summary>
public sealed class NullChaosPolicy : IChaosPolicy
{
    public static NullChaosPolicy Instance { get; } = new();
}

/// <summary>
/// 出站监督器：一条逻辑设备一条链路。唯一负责拨号、握手、心跳判死、退避重连与 Epoch 递增。
/// </summary>
internal sealed class OutboundSupervisor : IAsyncDisposable
{
    private readonly FrozenDevice _config;
    private readonly KindRegistry _kinds;
    private readonly DriverLimits _limits;
    private readonly ITransportFactory _transport;
    private readonly IEndpointResolver _resolver;
    private readonly IChaosPolicy _chaos;
    private readonly TimeProvider _time;
    private readonly Action<DriverEvent> _emit;
    private readonly SemaphoreSlim _exchangeGate;
    private readonly CancellationTokenSource _lifetime;
    private readonly List<Task> _background = [];
    private Task? _supervisor;
    private SessionRunner? _current;
    private long _epoch;
    private int _connectionCounter;
    private long _generation;
    private volatile bool _stopping;
    private long _endpointCursor;

    internal OutboundSupervisor(
        FrozenDevice config,
        long generation,
        KindRegistry kinds,
        DriverLimits limits,
        ITransportFactory transport,
        IEndpointResolver resolver,
        IChaosPolicy chaos,
        TimeProvider time,
        Action<DriverEvent> emit,
        CancellationToken lifetime)
    {
        _config = config;
        _generation = generation;
        _kinds = kinds;
        _limits = limits;
        _transport = transport;
        _resolver = resolver;
        _chaos = chaos;
        _time = time;
        _emit = emit;
        _exchangeGate = new SemaphoreSlim(1, 1);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    public string DeviceId => _config.DeviceId;

    public long Generation => Interlocked.Read(ref _generation);

    public LinkState State { get; private set; } = LinkState.Idle;

    public SessionRunner? Current => Volatile.Read(ref _current);

    internal void UpdateGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);

    public void Start()
    {
        _supervisor = Task.Run(SuperviseAsync);
    }

    private async Task SuperviseAsync()
    {
        if (_config.Heartbeat.Enabled)
        {
            // 该设备种类是否真的支持应用层心跳，在建连后才能判断；此处先占位。
        }

        var attempt = 0;
        while (!_stopping && !_lifetime.IsCancellationRequested)
        {
            try
            {
                if (await ConnectOnceAsync(attempt).ConfigureAwait(false))
                {
                    attempt = 0;
                    await WaitForSessionEndAsync().ConfigureAwait(false);
                    if (_stopping || _lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                }
                else
                {
                    attempt++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Emit(DriverEventKind.ProtocolFault, ex.GetType().Name + ":" + ex.Message, attempt);
                attempt++;
            }

            if (_stopping || _lifetime.IsCancellationRequested)
            {
                break;
            }

            SetState(LinkState.Retrying);
            var delay = _config.Retry.Backoff(attempt);
            if (_config.Retry.Jitter > 0)
            {
                var jitterFactor = 1 + ((Random.Shared.NextDouble() * 2 - 1) * _config.Retry.Jitter);
                delay = TimeSpan.FromMilliseconds(Math.Max(1, delay.TotalMilliseconds * jitterFactor));
            }

            Emit(DriverEventKind.RetryScheduled, "backoff", (long)delay.TotalMilliseconds);
            try
            {
                await Task.Delay(delay, _time, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(LinkState.Stopped);
    }

    /// <summary>尝试一次完整建连（解析 → 拨号 → 握手 → Ready）。返回是否连上。</summary>
    private async ValueTask<bool> ConnectOnceAsync(int attempt)
    {
        var endpoint = PickEndpoint(attempt);
        SetState(LinkState.Resolving);

        IPAddress[] addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(endpoint.Host, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit(DriverEventKind.Resolving, ex.GetType().Name, attempt);
            return false;
        }

        if (addresses.Length == 0)
        {
            Emit(DriverEventKind.Resolving, "dns-empty", attempt);
            return false;
        }

        var address = addresses[0];
        var remote = new IPEndPoint(address, endpoint.Port);
        SetState(LinkState.Connecting);
        Emit(DriverEventKind.Connecting, endpoint.EndpointId, 0);

        if (_chaos.ShouldFailConnect(endpoint.Host, endpoint.Port))
        {
            Emit(DriverEventKind.ProtocolFault, "chaos-connect-fail", attempt);
            return false;
        }

        Socket socket;
        try
        {
            socket = await _transport.ConnectAsync(remote, _config.Retry.ConnectTimeout, _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit(DriverEventKind.ProtocolFault, "connect:" + ex.GetType().Name, attempt);
            return false;
        }

        if (endpoint.Settle > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(endpoint.Settle, _time, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
        }

        Emit(DriverEventKind.Connected, endpoint.EndpointId, 0);
        SetState(LinkState.Handshaking);

        var epoch = Interlocked.Increment(ref _epoch);
        var connectionId = new ConnectionId(Interlocked.Increment(ref _connectionCounter));
        ISessionProtocol protocol;
        try
        {
            var context = new ProtocolContext(_config.DeviceId, ConnectionDirection.Outbound, _limits, _config.KindSettings);
            protocol = _kinds.CreateProtocol(_config.KindId, context);
        }
        catch (Exception ex)
        {
            Emit(DriverEventKind.ProtocolFault, "kind-factory:" + ex.GetType().Name, 0);
            socket.Dispose();
            return false;
        }

        var ctx = new ConnectionContext(
            connectionId,
            epoch,
            socket,
            protocol,
            _limits,
            _time,
            _config.DeviceId,
            null,
            ConnectionDirection.Outbound,
            _config.KindId);

        var runner = new SessionRunner(
            ctx,
            _emit,
            new FrozenHandshakeView(true, _config.Handshake.Timeout),
            new FrozenHeartbeatView(
                _config.Heartbeat.Enabled,
                _config.Heartbeat.Interval,
                _config.Heartbeat.Misses,
                _config.Heartbeat.Silent,
                _config.Heartbeat.ResponseTimeout),
            _lifetime.Token);

        Volatile.Write(ref _current, runner);
        var runTask = runner.StartAsync();

        // 等待握手完成（或无握手协议立即 Ready），失败即拆链。
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        handshakeCts.CancelAfter(_config.Handshake.Timeout);
        try
        {
            await runner.ReadyTask.WaitAsync(handshakeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            Emit(DriverEventKind.HandshakeFailed, "ready-wait", 0);
            await TeardownAsync(runner, runTask, SessionEndReason.HandshakeTimeout, "handshake-failed")
                .ConfigureAwait(false);
            return false;
        }

        SetState(LinkState.Ready);
        Emit(DriverEventKind.Ready, endpoint.EndpointId, epoch);
        _background.Add(runTask);
        return true;
    }

    private FrozenEndpoint PickEndpoint(int attempt)
    {
        // 单端点直接返回；多端点按轮转，尝试次数越多越往后选（冷却与容错）。
        var endpoints = _config.Endpoints;
        if (endpoints.Length == 1)
        {
            return endpoints[0];
        }

        var index = (int)(Interlocked.Increment(ref _endpointCursor) % endpoints.Length);
        return endpoints[index];
    }

    private async ValueTask WaitForSessionEndAsync()
    {
        var runner = Volatile.Read(ref _current);
        if (runner is null)
        {
            return;
        }

        var reason = await runner.Completion.ConfigureAwait(false);
        var runTask = _background.Count > 0 ? _background[^1] : null;
        await TeardownAsync(runner, runTask, reason, reason.ToString()).ConfigureAwait(false);
    }

    /// <summary>拆链：作废旧 epoch、排空 pending、关闭 socket、等待任务退出。</summary>
    private async ValueTask TeardownAsync(SessionRunner runner, Task? runTask, SessionEndReason reason, string detail)
    {
        var ctx = runner.Context;
        if (!ctx.TryMarkClosed())
        {
            // 已被其他路径关闭，等待既有任务结束即可。
        }

        runner.RequestStop(reason);
        runner.DrainPending(reason.ToString());
        ctx.CloseSocket();

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException)
            {
                Emit(DriverEventKind.CleanupTimeout, "session-teardown", 0);
            }
        }

        if (ReferenceEquals(Volatile.Read(ref _current), runner))
        {
            Volatile.Write(ref _current, null);
        }

        SetState(LinkState.Retrying);
        Emit(DriverEventKind.SessionClosed, detail, (long)reason);
        runner.ReleaseResources();
    }

    private void SetState(LinkState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        Emit(DriverEventKind.StateChanged, state.ToString(), (long)state);
    }

    private void Emit(DriverEventKind kind, string? detail, long value) => _emit(new DriverEvent
    {
        Kind = kind,
        Direction = ConnectionDirection.Outbound,
        DeviceId = _config.DeviceId,
        ConnectionId = Current?.Context.ConnectionId ?? default,
        Generation = Generation,
        Epoch = Current?.Context.Epoch ?? 0,
        Detail = detail,
        Value = value,
        TimestampTicks = _time.GetUtcNow().UtcTicks
    });

    /// <summary>执行一次请求-响应交换。严格单在途、绝不自动重放。</summary>
    public async ValueTask<CommandResult> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        await _exchangeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var runner = Volatile.Read(ref _current);
            if (runner is null || State != LinkState.Ready)
            {
                return CommandResult.None(CommandDisposition.NotSent, "not-ready", default, _epoch);
            }

            var effective = timeout ?? _config.Heartbeat.ResponseTimeout;
            return await runner.ExchangeAsync(request, effective, ct).ConfigureAwait(false);
        }
        finally
        {
            // 停机路径可能已释放门闩；在途 Exchange 的 finally 必须容忍，否则会掩盖真实错误。
            try
            {
                _exchangeGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 监管器已释放，无需归还许可。
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var runner = Volatile.Read(ref _current);
        if (runner is null || State != LinkState.Ready)
        {
            throw new InvalidOperationException($"设备 '{_config.DeviceId}' 当前不可写：{State}");
        }

        if (!runner.TryEnqueue(frame.Span, out var sent))
        {
            throw new InvalidOperationException($"设备 '{_config.DeviceId}' 写队列已满");
        }

        if (sent is not null)
        {
            await sent.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public SessionRunner? Runner => Volatile.Read(ref _current);

    public async ValueTask StopAsync()
    {
        _stopping = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        var runner = Volatile.Read(ref _current);
        if (runner is not null)
        {
            await TeardownAsync(runner, _background.Count > 0 ? _background[^1] : null, SessionEndReason.LocalStop, "device-stopped")
                .ConfigureAwait(false);
        }

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.WaitAsync(_limits.StopTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or AggregateException)
            {
                Emit(DriverEventKind.CleanupTimeout, "supervisor", 0);
            }
        }

        SetState(LinkState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        // 刻意不 Dispose _exchangeGate：在途 ExchangeAsync 的 finally 仍可能归还许可。
        // SemaphoreSlim 不持有非托管资源，交给 GC 回收即可，避免竞态抛 ObjectDisposedException。
        _lifetime.Dispose();
    }
}
