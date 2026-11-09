using System.Net.Sockets;
using LiteDriver.Configuration;
using LiteDriver.Observability;
using LiteDriver.Protocol;
using LiteDriver.Transport;

namespace LiteDriver;

/// <summary>
/// 驱动门面：唯一资源所有者。管理配置事务、设备与监听端点的生命周期、观测事件与优雅停机。
/// 一个实例可同时管理主动拨号链路与被动接入链路。
/// </summary>
public sealed class LiteDriver : IAsyncDisposable
{
    private readonly KindRegistry _kinds;
    private readonly DriverLimits _limits;
    private readonly ITransportFactory _transport;
    private readonly IEndpointResolver _resolver;
    private readonly IChaosPolicy _chaos;
    private readonly TimeProvider _time;
    private readonly IDriverObserver _observer;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, OutboundSupervisor> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListenerSupervisor> _listeners = new(StringComparer.Ordinal);
    private FrozenConfig _config = FrozenConfig.Empty;
    private long _revision;
    private RunState _state = RunState.Created;
    private bool _disposed;

    internal LiteDriver(
        KindRegistry kinds,
        DriverLimits limits,
        ITransportFactory transport,
        IEndpointResolver resolver,
        IChaosPolicy chaos,
        TimeProvider time,
        IDriverObserver observer)
    {
        _kinds = kinds;
        _limits = limits;
        _transport = transport;
        _resolver = resolver;
        _chaos = chaos;
        _time = time;
        _observer = observer;
    }

    /// <summary>当前生命周期状态。</summary>
    public RunState State => _state;

    /// <summary>已提交的配置版本号，从 0 起递增。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>已冻结的资源上限。</summary>
    public DriverLimits Limits => _limits;

    public TimeProvider TimeProvider => _time;

    /// <summary>
    /// 启动驱动并应用初始配置。<b>不等待设备连上</b>：设备未上电时本调用也会及时返回，
    /// 由内部监督器按退避继续重连。需要等链路可用时另调 <see cref="WaitUntilReadyAsync"/>。
    /// </summary>
    public async ValueTask<ApplyResult> StartAsync(DriverConfig config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state is RunState.Running)
        {
            throw new InvalidOperationException("驱动已在运行；请使用 ApplyAsync 变更配置。");
        }

        if (_state is RunState.Starting)
        {
            throw new InvalidOperationException("驱动正在启动。");
        }

        _state = RunState.Starting;
        Emit(DriverEventKind.DriverStarted, "start");

        var apply = await ApplyCoreAsync(config, cancellationToken).ConfigureAwait(false);
        if (!apply.Succeeded)
        {
            _state = RunState.Created;
            return apply;
        }

        _state = RunState.Running;
        return apply;
    }

    /// <summary>
    /// 以事务方式应用完整配置：校验 → 冻结 → 预绑定 → 原子提交 → 停用旧代。
    /// 任一步失败则保持旧快照继续服务，并回滚本轮新建资源。
    /// </summary>
    public async ValueTask<ApplyResult> ApplyAsync(DriverConfig config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state is not RunState.Running)
        {
            return new ApplyResult(
                Revision,
                0,
                0,
                0,
                0,
                [new ApplyIssue("not_running", $"驱动未在运行（当前 {_state}），无法应用配置")]);
        }

        return await ApplyCoreAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ApplyResult> ApplyCoreAsync(DriverConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _configGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = FrozenConfig.Create(
                config,
                Revision + 1,
                _kinds.Keys,
                _limits,
                _kinds.ValidateSettings);

            if (!next.IsValid)
            {
                Emit(DriverEventKind.ConfigRolledBack, "validation-failed");
                return new ApplyResult(Revision, 0, 0, 0, 0, next.Issues);
            }

            // ---- Stage：新建 listener 先绑定端口，失败则回滚本轮新建对象 ----
            var stagedListeners = new List<ListenerSupervisor>();
            var stagedDevices = new List<OutboundSupervisor>();
            var reusedListeners = new List<ListenerSupervisor>();
            var reusedDevices = new List<OutboundSupervisor>();
            var nextGeneration = Revision + 1;

            try
            {
                foreach (var listener in next.Listeners)
                {
                    if (_listeners.TryGetValue(listener.ListenerId, out var existing) &&
                        existing.Generation == nextGeneration - 1 &&
                        SameListener(existing, listener))
                    {
                        reusedListeners.Add(existing);
                        continue;
                    }

                    var supervisor = new ListenerSupervisor(
                        listener,
                        nextGeneration,
                        _kinds,
                        _limits,
                        _chaos,
                        _time,
                        EmitEvent,
                        _lifetime.Token);

                    // 端口占用必须在此暴露；抛异常即进入回滚。
                    supervisor.Bind();
                    stagedListeners.Add(supervisor);
                }

                foreach (var device in next.Devices)
                {
                    if (CanReuseDevice(device, nextGeneration))
                    {
                        reusedDevices.Add(_devices[device.DeviceId]);
                        continue;
                    }

                    var supervisor = new OutboundSupervisor(
                        device,
                        nextGeneration,
                        _kinds,
                        _limits,
                        _transport,
                        _resolver,
                        _chaos,
                        _time,
                        EmitEvent,
                        _lifetime.Token);

                    stagedDevices.Add(supervisor);
                }
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
            {
                foreach (var staged in stagedListeners)
                {
                    await staged.DisposeAsync().ConfigureAwait(false);
                }

                foreach (var staged in stagedDevices)
                {
                    await staged.DisposeAsync().ConfigureAwait(false);
                }

                Emit(DriverEventKind.ConfigRolledBack, "stage-failed:" + ex.GetType().Name);
                return new ApplyResult(
                    Revision, 0, 0, 0, 0, [new ApplyIssue("stage_failed", ex.Message)]);
            }

            // ---- Commit：原子替换快照 ----
            var retiredDevices = new List<OutboundSupervisor>();
            var retiredListeners = new List<ListenerSupervisor>();
            var added = 0;
            var updated = 0;
            var unchanged = 0;

            foreach (var device in next.Devices)
            {
                if (_devices.TryGetValue(device.DeviceId, out var previous))
                {
                    if (reusedDevices.Contains(previous))
                    {
                        unchanged++;
                    }
                    else
                    {
                        retiredDevices.Add(previous);
                        updated++;
                    }
                }
                else
                {
                    added++;
                }
            }

            foreach (var listener in next.Listeners)
            {
                if (_listeners.TryGetValue(listener.ListenerId, out var previous))
                {
                    if (reusedListeners.Contains(previous))
                    {
                        unchanged++;
                    }
                    else
                    {
                        retiredListeners.Add(previous);
                        updated++;
                    }
                }
                else
                {
                    added++;
                }
            }

            var removed = 0;
            foreach (var (id, supervisor) in _devices)
            {
                if (!next.DeviceMap.ContainsKey(id))
                {
                    retiredDevices.Add(supervisor);
                    removed++;
                }
            }

            foreach (var (id, supervisor) in _listeners)
            {
                if (!next.ListenerMap.ContainsKey(id))
                {
                    retiredListeners.Add(supervisor);
                    removed++;
                }
            }

            // 先启动新资源，避免更新期出现服务真空；随后停用旧代。
            foreach (var staged in stagedListeners)
            {
                staged.Start();
            }

            foreach (var staged in stagedDevices)
            {
                staged.Start();
            }

            _devices.Clear();
            foreach (var device in next.Devices)
            {
                if (reusedDevices.FirstOrDefault(d => d.DeviceId == device.DeviceId) is { } reuse)
                {
                    _devices[device.DeviceId] = reuse;
                }
            }

            foreach (var staged in stagedDevices)
            {
                _devices[staged.DeviceId] = staged;
            }

            _listeners.Clear();
            foreach (var listener in next.Listeners)
            {
                if (reusedListeners.FirstOrDefault(l => l.ListenerId == listener.ListenerId) is { } reuse)
                {
                    _listeners[listener.ListenerId] = reuse;
                }
            }

            foreach (var staged in stagedListeners)
            {
                _listeners[staged.ListenerId] = staged;
            }

            _config = next;
            Interlocked.Exchange(ref _revision, next.Revision);

            // ---- Retire：停用旧代并等待其退出 ----
            foreach (var retired in retiredListeners)
            {
                await retired.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var retired in retiredDevices)
            {
                await retired.DisposeAsync().ConfigureAwait(false);
            }

            Emit(DriverEventKind.ConfigApplied, $"rev={next.Revision}");

            return new ApplyResult(next.Revision, added, updated, removed, unchanged, []);
        }
        finally
        {
            _configGate.Release();
        }
    }

    private bool SameListener(ListenerSupervisor existing, FrozenListener next)
    {
        var current = _config.ListenerMap.TryGetValue(existing.ListenerId, out var c) ? c : null;
        return current is not null &&
               current.KindId == next.KindId &&
               current.Host == next.Host &&
               current.Port == next.Port &&
               current.Backlog == next.Backlog &&
               current.MaxSessions == next.MaxSessions &&
               (current.KindSettings?.GetRawText() ?? "") == (next.KindSettings?.GetRawText() ?? "");
    }

    private bool CanReuseDevice(FrozenDevice next, long nextGeneration)
    {
        if (!_devices.TryGetValue(next.DeviceId, out var existing))
        {
            return false;
        }

        if (!_config.DeviceMap.TryGetValue(next.DeviceId, out var current))
        {
            return false;
        }

        // 端点与种类不变时视为可复用传输，仅更新代与软参数（退避、心跳不重建 socket）。
        return current.SameTransport(next) && existing.Generation <= nextGeneration;
    }

    /// <summary>新增一个设备。等价于把当前配置加一项后调用 <see cref="ApplyAsync"/>。</summary>
    public async ValueTask<MutationResult> AddDeviceAsync(DeviceDescriptor descriptor, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_state is not RunState.Running)
        {
            return new MutationResult(MutationStatus.NotRunning, descriptor.DeviceId, Revision);
        }

        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_config.DeviceMap.ContainsKey(descriptor.DeviceId ?? ""))
            {
                return new MutationResult(MutationStatus.AlreadyExists, descriptor.DeviceId, Revision);
            }
        }
        finally
        {
            _configGate.Release();
        }

        var snapshot = BuildConfig(devices => Array.Find(devices, d => d.DeviceId == descriptor.DeviceId) is null
            ? [.. devices, descriptor]
            : devices);
        var result = await ApplyAsync(snapshot, ct).ConfigureAwait(false);
        return new MutationResult(
            result.Succeeded ? MutationStatus.Applied : MutationStatus.Invalid,
            descriptor.DeviceId,
            result.Revision,
            result.Succeeded ? null : result.Issues[0].Message);
    }

    /// <summary>更新一个设备。不存在时返回 <see cref="MutationStatus.NotFound"/>。</summary>
    public async ValueTask<MutationResult> UpdateDeviceAsync(
        string deviceId,
        DeviceDescriptor descriptor,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_state is not RunState.Running)
        {
            return new MutationResult(MutationStatus.NotRunning, deviceId, Revision);
        }

        var exists = _config.DeviceMap.ContainsKey(deviceId);
        if (!exists)
        {
            return new MutationResult(MutationStatus.NotFound, deviceId, Revision);
        }

        var snapshot = BuildConfig(devices =>
        {
            var copy = new DeviceDescriptor[devices.Length];
            for (var i = 0; i < devices.Length; i++)
            {
                copy[i] = devices[i].DeviceId == deviceId ? descriptor : devices[i];
            }

            return copy;
        });

        var result = await ApplyAsync(snapshot, ct).ConfigureAwait(false);
        return new MutationResult(
            result.Succeeded ? MutationStatus.Applied : MutationStatus.Invalid,
            deviceId,
            result.Revision,
            result.Succeeded ? null : result.Issues[0].Message);
    }

    /// <summary>删除一个设备。返回前已完成取消、关闭与资源释放。</summary>
    public async ValueTask<MutationResult> RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (_state is not RunState.Running)
        {
            return new MutationResult(MutationStatus.NotRunning, deviceId, Revision);
        }

        if (!_config.DeviceMap.ContainsKey(deviceId))
        {
            return new MutationResult(MutationStatus.NotFound, deviceId, Revision);
        }

        var snapshot = BuildConfig(devices => Array.FindAll(devices, d => d.DeviceId != deviceId));
        var result = await ApplyAsync(snapshot, ct).ConfigureAwait(false);
        return new MutationResult(
            result.Succeeded ? MutationStatus.Applied : MutationStatus.Invalid,
            deviceId,
            result.Revision,
            result.Succeeded ? null : result.Issues[0].Message);
    }

    /// <summary>新增一个监听端点。</summary>
    public async ValueTask<MutationResult> AddListenerAsync(ListenerDescriptor descriptor, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_state is not RunState.Running)
        {
            return new MutationResult(MutationStatus.NotRunning, descriptor.ListenerId, Revision);
        }

        if (_config.ListenerMap.ContainsKey(descriptor.ListenerId ?? ""))
        {
            return new MutationResult(MutationStatus.AlreadyExists, descriptor.ListenerId, Revision);
        }

        var snapshot = BuildConfig(devices => devices, listeners => [.. listeners, descriptor]);
        var result = await ApplyAsync(snapshot, ct).ConfigureAwait(false);
        return new MutationResult(
            result.Succeeded ? MutationStatus.Applied : MutationStatus.Invalid,
            descriptor.ListenerId,
            result.Revision,
            result.Succeeded ? null : result.Issues[0].Message);
    }

    /// <summary>删除一个监听端点，返回前已停止接受新连接。</summary>
    public async ValueTask<MutationResult> RemoveListenerAsync(string listenerId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerId);
        if (_state is not RunState.Running)
        {
            return new MutationResult(MutationStatus.NotRunning, listenerId, Revision);
        }

        if (!_config.ListenerMap.ContainsKey(listenerId))
        {
            return new MutationResult(MutationStatus.NotFound, listenerId, Revision);
        }

        var snapshot = BuildConfig(devices => devices, listeners => Array.FindAll(listeners, l => l.ListenerId != listenerId));
        var result = await ApplyAsync(snapshot, ct).ConfigureAwait(false);
        return new MutationResult(
            result.Succeeded ? MutationStatus.Applied : MutationStatus.Invalid,
            listenerId,
            result.Revision,
            result.Succeeded ? null : result.Issues[0].Message);
    }

    /// <summary>按当前配置构造新的 POCO 快照，供增删改包装使用。</summary>
    private DriverConfig BuildConfig(
        Func<DeviceDescriptor[], DeviceDescriptor[]>? mutateDevices = null,
        Func<ListenerDescriptor[], ListenerDescriptor[]>? mutateListeners = null)
    {
        var devices = new DeviceDescriptor[_config.Devices.Length];
        for (var i = 0; i < _config.Devices.Length; i++)
        {
            devices[i] = Rehydrate(_config.Devices[i]);
        }

        var listeners = new ListenerDescriptor[_config.Listeners.Length];
        for (var i = 0; i < _config.Listeners.Length; i++)
        {
            var l = _config.Listeners[i];
            listeners[i] = new ListenerDescriptor
            {
                ListenerId = l.ListenerId,
                KindId = l.KindId,
                Host = l.Host,
                Port = l.Port,
                Backlog = l.Backlog,
                MaxSessions = l.MaxSessions,
                KindSettings = l.KindSettings
            };
        }

        return new DriverConfig
        {
            Devices = mutateDevices?.Invoke(devices) ?? devices,
            Listeners = [.. (mutateListeners?.Invoke(listeners) ?? listeners)]
        };
    }

    private static DeviceDescriptor Rehydrate(FrozenDevice d) => new()
    {
        DeviceId = d.DeviceId,
        KindId = d.KindId,
        Endpoints = [.. Array.ConvertAll(d.Endpoints, static e => new EndpointDescriptor
        {
            EndpointId = e.EndpointId,
            Host = e.Host,
            Port = e.Port,
            SettleMs = (int)e.Settle.TotalMilliseconds
        })],
        Retry = new RetryOptions
        {
            InitialMs = (int)d.Retry.Initial.TotalMilliseconds,
            MaxMs = (int)d.Retry.Max.TotalMilliseconds,
            Jitter = d.Retry.Jitter,
            ConnectTimeoutMs = (int)d.Retry.ConnectTimeout.TotalMilliseconds,
            EndpointCooldownMs = (int)d.Retry.EndpointCooldown.TotalMilliseconds
        },
        Heartbeat = new HeartbeatOptions
        {
            Enabled = d.Heartbeat.Enabled,
            IntervalMs = (int)d.Heartbeat.Interval.TotalMilliseconds,
            Misses = d.Heartbeat.Misses,
            SilentMs = (int)d.Heartbeat.Silent.TotalMilliseconds,
            ResponseTimeoutMs = (int)d.Heartbeat.ResponseTimeout.TotalMilliseconds
        },
        Handshake = new HandshakeOptions
        {
            TimeoutMs = (int)d.Handshake.Timeout.TotalMilliseconds
        },
        KindSettings = d.KindSettings
    };

    /// <summary>取一个设备的门面。</summary>
    public DeviceHandle GetDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!_devices.TryGetValue(deviceId, out var supervisor))
        {
            throw new KeyNotFoundException($"设备 '{deviceId}' 不存在");
        }

        return new DeviceHandle(supervisor);
    }

    public bool TryGetDevice(string deviceId, out DeviceHandle handle)
    {
        if (_devices.TryGetValue(deviceId, out var supervisor))
        {
            handle = new DeviceHandle(supervisor);
            return true;
        }

        handle = default;
        return false;
    }

    /// <summary>等待某设备进入 Ready。</summary>
    public async ValueTask WaitUntilReadyAsync(string deviceId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_devices.TryGetValue(deviceId, out var supervisor))
        {
            throw new KeyNotFoundException($"设备 '{deviceId}' 不存在");
        }

        var deadline = _time.GetUtcNow() + timeout;
        while (_time.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (supervisor.State == LinkState.Ready)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), _time, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"设备 '{deviceId}' 在 {timeout} 内未进入 Ready（当前 {supervisor.State}）");
    }

    /// <summary>所有监听端点实际绑定的端口，便于测试读取系统分配端口。</summary>
    public IReadOnlyDictionary<string, int> GetListenerPorts()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, supervisor) in _listeners)
        {
            result[id] = supervisor.BoundPort;
        }

        return result;
    }

    /// <summary>取某监听端点当前的入站会话句柄，用于读取设备上报并回写。</summary>
    public IReadOnlyList<InboundSessionHandle> GetInboundSessions(string listenerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerId);
        return _listeners.TryGetValue(listenerId, out var supervisor)
            ? supervisor.SnapshotHandles()
            : [];
    }

    /// <summary>取某监听端点当前的入站会话数。</summary>
    public int GetListenerSessionCount(string listenerId) =>
        _listeners.TryGetValue(listenerId, out var supervisor) ? supervisor.ActiveSessions : 0;

    internal ListenerSupervisor? GetListenerSupervisor(string listenerId) =>
        _listeners.TryGetValue(listenerId, out var supervisor) ? supervisor : null;

    /// <summary>幂等停机：先停止接受与拨号，再取消、关闭、等待全部子任务退出。</summary>
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_state is RunState.Stopping or RunState.Stopped or RunState.Disposed)
        {
            return;
        }

        _state = RunState.Stopping;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        foreach (var listener in _listeners.Values.ToArray())
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var device in _devices.Values.ToArray())
        {
            await device.DisposeAsync().ConfigureAwait(false);
        }

        _listeners.Clear();
        _devices.Clear();
        _config = FrozenConfig.Empty;
        _state = RunState.Stopped;
        Emit(DriverEventKind.DriverStopped, "stop");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _state = RunState.Disposed;
        _configGate.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>转发子组件事件，保留其全部字段（设备、连接、epoch 等）。</summary>
    private void EmitEvent(DriverEvent e)
    {
        try
        {
            _observer.OnEvent(e);
        }
        catch
        {
            // 观察者异常不得影响驱动运行。
        }
    }

    /// <summary>驱动级事件：不带设备/连接标识。</summary>
    private void Emit(DriverEventKind kind, string? detail)
    {
        EmitEvent(new DriverEvent
        {
            Kind = kind,
            Detail = detail,
            TimestampTicks = _time.GetUtcNow().UtcTicks,
            Generation = Revision
        });
    }
}
