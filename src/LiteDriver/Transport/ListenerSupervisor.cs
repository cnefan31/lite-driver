using System.Net;
using System.Net.Sockets;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using LiteDriver.Protocol;
using LiteDriver.Sessions;

namespace LiteDriver.Transport;

/// <summary>
/// 入站监督器：绑定端口、接受连接、为每个 accepted socket 建立独立 SessionContext。
/// 监听失败让配置事务失败；accept 瞬时异常按退避重启并继续监听。
/// </summary>
internal sealed class ListenerSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly FrozenListener _config;
    private readonly KindRegistry _kinds;
    private readonly DriverLimits _limits;
    private readonly IChaosPolicy _chaos;
    private readonly TimeProvider _time;
    private readonly Action<DriverEvent> _emit;
    private readonly CancellationTokenSource _lifetime;
    private readonly List<SessionRunner> _sessions = [];
    private readonly object _sessionLock = new();
    private Socket? _listener;
    private Task? _acceptLoop;
    private long _generation;
    private long _connectionCounter;
    private long _epoch;
    private volatile bool _stopping;
    private int _activeSessions;

    internal ListenerSupervisor(
        FrozenListener config,
        long generation,
        KindRegistry kinds,
        DriverLimits limits,
        IChaosPolicy chaos,
        TimeProvider time,
        Action<DriverEvent> emit,
        CancellationToken lifetime)
    {
        _config = config;
        _generation = generation;
        _kinds = kinds;
        _limits = limits;
        _chaos = chaos;
        _time = time;
        _emit = emit;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    public string ListenerId => _config.ListenerId;

    public long Generation => Interlocked.Read(ref _generation);

    public int ActiveSessions => Volatile.Read(ref _activeSessions);

    /// <summary>本地实际监听的端口（配置为 0 时由系统分配）。</summary>
    public int BoundPort { get; private set; }

    internal void UpdateGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);

    /// <summary>绑定端口。失败抛出，由配置事务转为 Stage 失败并回滚。</summary>
    public void Bind()
    {
        var address = IPAddress.TryParse(_config.Host, out var parsed)
            ? parsed
            : _config.Host == "localhost" ? IPAddress.Loopback : IPAddress.Any;

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(address, _config.Port));
            socket.Listen(_config.Backlog);
            BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            _listener = socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Start()
    {
        if (_listener is null)
        {
            throw new InvalidOperationException($"监听端点 '{_config.ListenerId}' 尚未 Bind");
        }

        _acceptLoop = Task.Run(AcceptLoopAsync);
        _emit(new DriverEvent
        {
            Kind = DriverEventKind.ListenerAdded,
            Direction = ConnectionDirection.Inbound,
            ListenerId = _config.ListenerId,
            Generation = Generation,
            Detail = "listening",
            Value = BoundPort,
            TimestampTicks = _time.GetUtcNow().UtcTicks
        });
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping && !_lifetime.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _listener!.AcceptAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // accept 瞬时异常（例如 fd 短暂耗尽）不应终止监听循环。
                _emit(new DriverEvent
                {
                    Kind = DriverEventKind.ProtocolFault,
                    Direction = ConnectionDirection.Inbound,
                    ListenerId = _config.ListenerId,
                    Detail = "accept:" + ex.SocketErrorCode,
                    TimestampTicks = _time.GetUtcNow().UtcTicks
                });

                try
                {
                    await Task.Delay(AcceptRetryDelay, _time, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleAcceptedAsync(accepted));
        }
    }

    private async Task HandleAcceptedAsync(Socket accepted)
    {
        var sessions = Interlocked.Increment(ref _activeSessions);
        if (sessions > _config.MaxSessions)
        {
            Interlocked.Decrement(ref _activeSessions);
            RejectSession(accepted, "max-sessions");
            return;
        }

        if (_chaos.ShouldDropAfterAccept(_config.ListenerId))
        {
            Interlocked.Decrement(ref _activeSessions);
            RejectSession(accepted, "chaos-accept-drop");
            return;
        }

        accepted.NoDelay = true;
        var epoch = Interlocked.Increment(ref _epoch);
        var connectionId = new ConnectionId(Interlocked.Increment(ref _connectionCounter));

        ISessionProtocol protocol;
        try
        {
            var context = new ProtocolContext(_config.ListenerId, ConnectionDirection.Inbound, _limits, _config.KindSettings);
            protocol = _kinds.CreateProtocol(_config.KindId, context);
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _activeSessions);
            RejectSession(accepted, "kind-factory:" + ex.GetType().Name);
            return;
        }

        var ctx = new ConnectionContext(
            connectionId,
            epoch,
            accepted,
            protocol,
            _limits,
            _time,
            null,
            _config.ListenerId,
            ConnectionDirection.Inbound,
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

        lock (_sessionLock)
        {
            _sessions.Add(runner);
        }

        _emit(new DriverEvent
        {
            Kind = DriverEventKind.SessionAccepted,
            Direction = ConnectionDirection.Inbound,
            ListenerId = _config.ListenerId,
            ConnectionId = connectionId,
            Epoch = epoch,
            Generation = Generation,
            TimestampTicks = _time.GetUtcNow().UtcTicks
        });

        try
        {
            await runner.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _emit(new DriverEvent
            {
                Kind = DriverEventKind.ProtocolFault,
                Direction = ConnectionDirection.Inbound,
                ListenerId = _config.ListenerId,
                ConnectionId = connectionId,
                Epoch = epoch,
                Detail = "session:" + ex.GetType().Name,
                TimestampTicks = _time.GetUtcNow().UtcTicks
            });
        }
        finally
        {
            runner.RequestStop(SessionEndReason.ListenerStopped);
            runner.DrainPending("session-ended");
            ctx.CloseSocket();
            runner.ReleaseResources();
            lock (_sessionLock)
            {
                _sessions.Remove(runner);
            }

            Interlocked.Decrement(ref _activeSessions);
            _emit(new DriverEvent
            {
                Kind = DriverEventKind.SessionClosed,
                Direction = ConnectionDirection.Inbound,
                ListenerId = _config.ListenerId,
                ConnectionId = connectionId,
                Epoch = epoch,
                TimestampTicks = _time.GetUtcNow().UtcTicks
            });
        }
    }

    private void RejectSession(Socket socket, string reason)
    {
        _emit(new DriverEvent
        {
            Kind = DriverEventKind.SessionRejected,
            Direction = ConnectionDirection.Inbound,
            ListenerId = _config.ListenerId,
            Detail = reason,
            TimestampTicks = _time.GetUtcNow().UtcTicks
        });

        try
        {
            socket.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // 预期路径。
        }
    }

    /// <summary>取当前所有入站会话句柄，供应用读取设备上报。</summary>
    public InboundSessionHandle[] SnapshotHandles()
    {
        SessionRunner[] runners;
        lock (_sessionLock)
        {
            runners = [.. _sessions];
        }

        var handles = new InboundSessionHandle[runners.Length];
        for (var i = 0; i < runners.Length; i++)
        {
            handles[i] = new InboundSessionHandle(runners[i]);
        }

        return handles;
    }

    /// <summary>取当前所有入站会话的快照，供测试与诊断读取。</summary>
    public SessionRunner[] SnapshotSessions()
    {
        lock (_sessionLock)
        {
            return [.. _sessions];
        }
    }

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

        try
        {
            _listener?.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // 预期路径。
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(_limits.StopTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or AggregateException)
            {
                _emit(new DriverEvent
                {
                    Kind = DriverEventKind.CleanupTimeout,
                    Direction = ConnectionDirection.Inbound,
                    ListenerId = _config.ListenerId,
                    Detail = "accept-loop",
                    TimestampTicks = _time.GetUtcNow().UtcTicks
                });
            }
        }

        SessionRunner[] pending;
        lock (_sessionLock)
        {
            pending = [.. _sessions];
        }

        foreach (var session in pending)
        {
            session.RequestStop(SessionEndReason.ListenerStopped);
            session.Context.CloseSocket();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
