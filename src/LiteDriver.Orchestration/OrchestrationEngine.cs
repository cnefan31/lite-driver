using System.Collections.Concurrent;
using global::LiteDriver.Configuration;
using global::LiteDriver.Observability;
using global::LiteDriver.Protocol;

namespace LiteDriver.Orchestration;

/// <summary>宿主注册的协议种类。编排引擎据此构造驱动并校验 kindSettings。</summary>
public sealed record ProtocolRegistration(
    string KindId,
    string DisplayName,
    string Description,
    IReadOnlyList<ProtocolField> Fields,
    Func<ProtocolContext, IDeviceKind> Factory,
    KindSettingsValidator? Validator);

/// <summary>
/// 编排引擎：持有唯一 <see cref="LiteDriver"/> 实例，负责把编排图热应用上去。
/// <b>应用时不销毁驱动</b>，只调用 <c>ApplyAsync</c>，因此后端服务无需重启。
/// </summary>
public sealed class OrchestrationEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ProtocolRegistration> _protocols;
    private readonly IReadOnlyDictionary<string, ProtocolRegistration> _protocolMap;
    private readonly ConcurrentQueue<EventEntry> _events = new();
    private readonly OrchestrationObserver _observer;
    private LiteDriver? _driver;
    private OrchestrationGraph _graph = new();
    private long _revision;
    private string? _lastError;
    private volatile bool _running;

    /// <summary>创建引擎。</summary>
    public OrchestrationEngine(IEnumerable<ProtocolRegistration> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        _protocols = [.. protocols];
        _protocolMap = _protocols.ToDictionary(p => p.KindId, StringComparer.Ordinal);
        _observer = new OrchestrationObserver(this);
    }

    /// <summary>可用的协议种类。</summary>
    public IReadOnlyList<ProtocolRegistration> Protocols => _protocols;

    /// <summary>当前图（已保存的那一份）。</summary>
    public OrchestrationGraph CurrentGraph
    {
        get
        {
            lock (_events)
            {
                return _graph.Clone();
            }
        }
    }

    /// <summary>
    /// 应用编排图：编译 → 校验 → 热应用。成功后才更新已保存的图。
    /// 失败时驱动保持上一次成功的配置继续服务。
    /// </summary>
    public async ValueTask<ApplyReport> ApplyAsync(OrchestrationGraph graph, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var compiled = GraphCompiler.Compile(graph, [.. _protocolMap.Keys]);
            if (!compiled.Ok || compiled.Config is null)
            {
                _lastError = "编排图校验未通过";
                return new ApplyReport(false, _revision, _lastError, compiled.Issues, 0, 0, 0, 0);
            }

            _driver ??= BuildDriver();
            var wasRunning = _running;

            ApplyResult result;
            try
            {
                result = wasRunning
                    ? await _driver.ApplyAsync(compiled.Config, ct).ConfigureAwait(false)
                    : await _driver.StartAsync(compiled.Config, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return new ApplyReport(
                    false, _revision, "应用失败：" + ex.Message,
                    [new CompileIssue("apply_failed", ex.Message, null)], 0, 0, 0, 0);
            }

            if (!result.Succeeded)
            {
                _lastError = "驱动拒绝了该配置";
                var issues = result.Issues
                    .Select(i => new CompileIssue(i.Code, i.Message, i.Target))
                    .ToList();
                return new ApplyReport(false, _revision, _lastError, issues, 0, 0, 0, 0);
            }

            _running = true;
            _revision++;
            _lastError = null;
            graph.Revision = _revision;

            // 把系统分配的临时端口回写进图。否则 Port=0 的监听节点每次应用都会被判定为
            // 「端口变化」而重建 listener，实际端口随之漂移，客户端也会被连带重建。
            WriteBackBoundPorts(graph, _driver);

            var snapshot = graph.Clone();

            lock (_events)
            {
                _graph = snapshot;
            }

            return new ApplyReport(
                true, _revision, "已应用", [],
                result.Added, result.Updated, result.Removed, result.Unchanged);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把监听节点的实际端口回写进图，稳定 Port=0 场景下的端口。</summary>
    private static void WriteBackBoundPorts(OrchestrationGraph graph, LiteDriver driver)
    {
        var ports = driver.GetListenerPorts();
        if (ports.Count == 0)
        {
            return;
        }

        foreach (var node in graph.Nodes)
        {
            if (node.Kind != NodeKind.Server)
            {
                continue;
            }

            var listenerId = string.IsNullOrWhiteSpace(node.ListenerId) ? node.Id : node.ListenerId!;
            if (ports.TryGetValue(listenerId, out var actual) && actual > 0)
            {
                node.Port = actual;
            }
        }
    }

    /// <summary>读取编译预览：不应用，只返回编译问题，供前端「校验」按钮使用。</summary>
    public CompileResult Validate(OrchestrationGraph graph) =>
        GraphCompiler.Compile(graph, [.. _protocolMap.Keys]);

    private LiteDriver BuildDriver()
    {
        // 注册表在 Build 时冻结；所有协议必须在编译期静态可达。
        var builder = new LiteDriverBuilder();
        foreach (var protocol in _protocols)
        {
            var factory = protocol.Factory;
            if (protocol.Validator is null)
            {
                builder.RegisterKind(protocol.KindId, ctx => factory(ctx));
            }
            else
            {
                builder.RegisterKind(protocol.KindId, ctx => factory(ctx), protocol.Validator);
            }
        }

        return builder.UseObserver(_observer).Build();
    }

    /// <summary>当前运行时状态快照。</summary>
    public OrchestrationStatus Status()
    {
        var graph = CurrentGraph;
        var driver = _driver;

        if (driver is null || !_running)
        {
            return new OrchestrationStatus(
                false, _revision,
                graph.Nodes.Count(n => n.Kind == NodeKind.Client),
                graph.Nodes.Count(n => n.Kind == NodeKind.Server),
                0, 0, [], [.. _events], _lastError);
        }

        var nodes = new List<NodeStatus>(graph.Nodes.Count);
        var ready = 0;
        var sessions = 0;
        var ports = driver.GetListenerPorts();

        foreach (var node in graph.Nodes)
        {
            if (node.Kind == NodeKind.Protocol)
            {
                nodes.Add(new NodeStatus(node.Id, "Protocol", "Static", 0, 0, 0, node.KindId));
                continue;
            }

            if (node.Kind == NodeKind.Client)
            {
                var deviceId = string.IsNullOrWhiteSpace(node.DeviceId) ? node.Id : node.DeviceId!;
                if (driver.TryGetDevice(deviceId, out var handle))
                {
                    var state = handle.State.ToString();
                    if (handle.State == LinkState.Ready)
                    {
                        ready++;
                    }

                    nodes.Add(new NodeStatus(node.Id, "Client", state, handle.Epoch, 0, 0, deviceId));
                }
                else
                {
                    nodes.Add(new NodeStatus(node.Id, "Client", "Absent", 0, 0, 0, deviceId));
                }

                continue;
            }

            var listenerId = string.IsNullOrWhiteSpace(node.ListenerId) ? node.Id : node.ListenerId!;
            var count = driver.GetListenerSessionCount(listenerId);
            sessions += count;
            ports.TryGetValue(listenerId, out var port);
            nodes.Add(new NodeStatus(node.Id, "Server", port > 0 ? "Listening" : "Absent", 0, count, port, listenerId));
        }

        return new OrchestrationStatus(
            true, _revision,
            graph.Nodes.Count(n => n.Kind == NodeKind.Client),
            graph.Nodes.Count(n => n.Kind == NodeKind.Server),
            ready, sessions, nodes, [.. _events], _lastError);
    }

    internal void RecordEvent(in DriverEvent e)
    {
        var target = e.DeviceId ?? e.ListenerId;
        _events.Enqueue(new EventEntry(
            e.Kind.ToString(),
            e.Direction.ToString(),
            target,
            e.Epoch,
            e.Detail));

        while (_events.Count > 200 && _events.TryDequeue(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_driver is not null)
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
            _driver = null;
        }

        _running = false;
        _gate.Dispose();
    }
}

/// <summary>把驱动事件收进编排引擎的环形缓冲。</summary>
internal sealed class OrchestrationObserver : IDriverObserver
{
    private readonly OrchestrationEngine _engine;

    public OrchestrationObserver(OrchestrationEngine engine) => _engine = engine;

    public void OnEvent(in DriverEvent e) => _engine.RecordEvent(e);
}
