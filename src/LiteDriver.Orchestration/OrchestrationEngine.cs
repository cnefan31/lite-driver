using System.Collections.Concurrent;
using global::LiteDriver.Configuration;
using global::LiteDriver.Observability;
using global::LiteDriver.Protocol;

namespace LiteDriver.Orchestration;

/// <summary>宿主注册的编解码器（供编排页面的 codec 节点选择）。</summary>
public sealed record CodecRegistration(
    string CodecId,
    string DisplayName,
    string Description,
    Func<PayloadCodecContext, IPayloadCodec> Factory,
    PayloadCodecSettingsValidator? Validator = null);

/// <summary>宿主注册的变换器 / 处理器 / 出口，仅用于编排页面选择与校验存在性。</summary>
public sealed record ComponentRegistration(string Id, string DisplayName, string Description);

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
public sealed class OrchestrationEngine : IAsyncDisposable, INodeTypeCatalogView
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ProtocolRegistration> _protocols;
    private readonly IReadOnlyDictionary<string, ProtocolRegistration> _protocolMap;
    private readonly ConcurrentQueue<EventEntry> _events = new();
    private readonly OrchestrationObserver _observer;
    private readonly NodeTypeCatalog _nodeTypes;
    private readonly List<CodecRegistration> _codecs;
    private readonly List<ComponentRegistration> _transforms;
    private readonly List<ComponentRegistration> _handlers;
    private readonly List<ComponentRegistration> _sinks;
    private LiteDriver? _driver;
    private OrchestrationGraph _graph = new();
    private long _revision;
    private string? _lastError;
    private volatile bool _running;

    /// <summary>创建引擎。</summary>
    /// <param name="protocols">协议种类。</param>
    /// <param name="codecs">载荷编解码器（可选，供 codec 节点选择）。</param>
    /// <param name="transforms">变换器（可选，供 transform 节点选择）。</param>
    /// <param name="handlers">处理器（可选，供 handler 节点选择）。</param>
    /// <param name="sinks">出口（可选，供 sink 节点选择）。</param>
    /// <param name="nodeTypes">自定义节点类型目录；为 null 时使用六个内置类型。</param>
    public OrchestrationEngine(
        IEnumerable<ProtocolRegistration> protocols,
        IEnumerable<CodecRegistration>? codecs = null,
        IEnumerable<ComponentRegistration>? transforms = null,
        IEnumerable<ComponentRegistration>? handlers = null,
        IEnumerable<ComponentRegistration>? sinks = null,
        NodeTypeCatalog? nodeTypes = null)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        _protocols = [.. protocols];
        _protocolMap = _protocols.ToDictionary(p => p.KindId, StringComparer.Ordinal);
        _codecs = codecs is null ? [] : [.. codecs];
        _transforms = transforms is null ? [] : [.. transforms];
        _handlers = handlers is null ? [] : [.. handlers];
        _sinks = sinks is null ? [] : [.. sinks];
        _nodeTypes = nodeTypes ?? GraphCompiler.DefaultNodeTypes();
        _observer = new OrchestrationObserver(this);
    }

    /// <summary>节点类型目录（供前端渲染托盘）。</summary>
    public NodeTypeCatalog NodeTypes => _nodeTypes;

    /// <summary>已注册的编解码器。</summary>
    public IReadOnlyList<CodecRegistration> Codecs => _codecs;

    /// <summary>已注册的变换器。</summary>
    public IReadOnlyList<ComponentRegistration> Transforms => _transforms;

    /// <summary>已注册的处理器。</summary>
    public IReadOnlyList<ComponentRegistration> Handlers => _handlers;

    /// <summary>已注册的出口。</summary>
    public IReadOnlyList<ComponentRegistration> Sinks => _sinks;

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
            if (node.NodeType != "server")
            {
                continue;
            }

            var listenerId = string.IsNullOrWhiteSpace(node.Get("listenerId")) ? node.Id : node.Get("listenerId");
            if (ports.TryGetValue(listenerId, out var actual) && actual > 0)
            {
                node.Settings["port"] = actual.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>读取编译预览：不应用，只返回编译问题，供前端「校验」按钮使用。</summary>
    public CompileResult Validate(OrchestrationGraph graph) =>
        GraphCompiler.Compile(graph, [.. _protocolMap.Keys], _nodeTypes, this);

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

        foreach (var codec in _codecs)
        {
            var factory = codec.Factory;
            if (codec.Validator is null)
            {
                builder.RegisterCodec(codec.CodecId, ctx => factory(ctx));
            }
            else
            {
                builder.RegisterCodec(codec.CodecId, ctx => factory(ctx), codec.Validator);
            }
        }

        return builder.UseObserver(_observer).Build();
    }

    /// <summary>
    /// 从某个处理器节点的指定输出广播一条消息给下游。
    /// <para>
    /// 沿该输出端口的所有连线，把消息写进下游设备节点的发送通道（<c>tx</c>），
    /// 或继续传给下游处理器。这就是「处理结果回写设备」的闭环入口。
    /// </para>
    /// </summary>
    /// <param name="handlerNodeId">处理器节点 id。</param>
    /// <param name="outputPort">输出端口：ok / retry / fail。</param>
    /// <param name="message">处理结果消息。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask<IReadOnlyList<NodeSendResult>> EmitFromHandlerAsync(
        string handlerNodeId,
        string outputPort,
        PayloadMessage message,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerNodeId);

        var graph = CurrentGraph;
        var handler = graph.Nodes.FirstOrDefault(n => n.Id == handlerNodeId);
        if (handler is null || handler.NodeType != "handler")
        {
            return [NodeSendResult.Fail(SendStatus.InvalidNode, handlerNodeId, "不是处理器节点")];
        }

        var results = new List<NodeSendResult>();

        // 沿该输出端口的所有连线逐个下游派发；codec/transform 属于链路中间节点，需继续转发。
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string NodeId, string FromPort)>();

        foreach (var edge in graph.Edges.Where(e =>
                     e.From == handlerNodeId &&
                     string.Equals(e.FromPort, outputPort, StringComparison.Ordinal)))
        {
            pending.Enqueue((edge.To, edge.FromPort));
        }

        while (pending.Count > 0)
        {
            var (currentId, _) = pending.Dequeue();
            if (!visited.Add(currentId))
            {
                continue;
            }

            var target = graph.Nodes.FirstOrDefault(n => n.Id == currentId);
            if (target is null)
            {
                continue;
            }

            if (target.NodeType is "client" or "server")
            {
                results.Add(await SendAsync(target.Id, message, ct).ConfigureAwait(false));
                continue;
            }

            if (target.NodeType == "sink")
            {
                RecordEvent(new DriverEvent
                {
                    Kind = DriverEventKind.SessionClosed,
                    Direction = ConnectionDirection.Inbound,
                    DeviceId = target.Get("sinkId"),
                    Detail = $"sink:{outputPort}",
                    TimestampTicks = DateTime.UtcNow.Ticks
                });
                results.Add(NodeSendResult.Sent(target.Id, 0, 0));
                continue;
            }

            if (target.NodeType == "transform")
            {
                // 变换节点：把消息继续沿其 out 传递
                foreach (var next in graph.Edges.Where(e => e.From == target.Id &&
                                                            string.Equals(e.FromPort, "out", StringComparison.Ordinal)))
                {
                    pending.Enqueue((next.To, "out"));
                }

                continue;
            }

            if (target.NodeType == "codec")
            {
                // 编解码器：消息从其 enc 进入、frame 输出，再继续传给下游设备
                foreach (var next in graph.Edges.Where(e => e.From == target.Id &&
                                                            string.Equals(e.FromPort, "frame", StringComparison.Ordinal)))
                {
                    pending.Enqueue((next.To, "frame"));
                }

                results.Add(NodeSendResult.Sent(target.Id, 0, 0));
                continue;
            }
        }

        if (results.Count == 0)
        {
            results.Add(NodeSendResult.Fail(
                SendStatus.NotReady, handlerNodeId, $"输出 '{outputPort}' 未连接任何下游"));
        }

        return results;
    }

    /// <summary>
    /// 模拟一次处理并路由：给处理器喂一条消息，按 <c>mode</c> 产生结果并沿对应输出下发。
    /// 用于页面调试与验证「设备上报 → 处理 → 结果回写」闭环。
    /// </summary>
    public async ValueTask<IReadOnlyList<NodeSendResult>> ProcessAndRouteAsync(
        string handlerNodeId,
        PayloadMessage input,
        CancellationToken ct = default)
    {
        var graph = CurrentGraph;
        var handler = graph.Nodes.FirstOrDefault(n => n.Id == handlerNodeId);
        if (handler is null || handler.NodeType != "handler")
        {
            return [NodeSendResult.Fail(SendStatus.InvalidNode, handlerNodeId, "不是处理器节点")];
        }

        var mode = handler.Get("mode");
        var port = mode switch
        {
            "fail" => "fail",
            _ => "ok"
        };

        // echo 模式把输入原样回传；其余模式产生一条结果消息
        var output = input.HasValue && mode == "echo"
            ? input
            : PayloadMessage.From(new HandlerResult(handlerNodeId, mode, port, mode), "handler-result");

        return await EmitFromHandlerAsync(handlerNodeId, port, output, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 给处理器喂一段文本，按 <c>mode</c> 产生结果并沿对应输出路由。
    /// 文本需能被该处理器下游设备的 codec 理解（echo 模式直接回传文本）。
    /// </summary>
    public async ValueTask<IReadOnlyList<NodeSendResult>> ProcessTextAsync(
        string handlerNodeId,
        string text,
        CancellationToken ct = default)
    {
        var graph = CurrentGraph;
        var handler = graph.Nodes.FirstOrDefault(n => n.Id == handlerNodeId);
        if (handler is null || handler.NodeType != "handler")
        {
            return [NodeSendResult.Fail(SendStatus.InvalidNode, handlerNodeId, "不是处理器节点")];
        }

        var mode = handler.Get("mode");
        var port = mode == "fail" ? "fail" : "ok";

        // 处理结果用下游设备的文本 codec 构造，确保能被编码后写回 socket。
        // 若 mode=echo，把输入文本原样回传；否则回传「处理后的文本」。
        var outputText = mode == "echo" ? text : $"[{port}] {text}";
        var message = BuildOutboundMessage(graph, handlerNodeId, port, outputText);
        return await EmitFromHandlerAsync(handlerNodeId, port, message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 找到连到某个设备节点 tx 的上游编解码器节点的 codecId。
    /// 编解码器已改为独立节点，设备的编码方式由链路决定而非设备字段。
    /// </summary>
    private string? FindCodecForDevice(OrchestrationGraph graph, GraphNode device)
    {
        // 设备 tx ← 某节点 out：该节点应是 codec（RawFrame 输出）
        var edge = graph.Edges.FirstOrDefault(e =>
            string.Equals(e.To, device.Id, StringComparison.Ordinal) &&
            string.Equals(e.ToPort, "tx", StringComparison.Ordinal));
        if (edge is null)
        {
            return null;
        }

        var upstream = graph.Nodes.FirstOrDefault(n => n.Id == edge.From);
        if (upstream is null)
        {
            return null;
        }

        var codecId = upstream.Get("codecId").Trim();
        return codecId.Length == 0 ? null : codecId;
    }

    /// <summary>
    /// 为指定输出端口的下游设备构造一条可编码的文本消息。
    /// 处理器自身不绑定 codec，实际编码由下游设备的 codec 完成。
    /// </summary>
    private PayloadMessage BuildOutboundMessage(
        OrchestrationGraph graph,
        string handlerNodeId,
        string outputPort,
        string text)
    {
        var edge = graph.Edges.FirstOrDefault(e =>
            e.From == handlerNodeId && string.Equals(e.FromPort, outputPort, StringComparison.Ordinal));
        var downstream = edge is null ? null : graph.Nodes.FirstOrDefault(n => n.Id == edge.To);

        if (downstream is not null && downstream.NodeType is "client" or "server")
        {
            var codecId = FindCodecForDevice(graph, downstream) ?? "";
            var reg = _codecs.FirstOrDefault(c => string.Equals(c.CodecId, codecId, StringComparison.Ordinal));
            if (reg is not null)
            {
                try
                {
                    var codec = reg.Factory(PayloadCodecContext.CreateForStandalone(
                        codecId, downstream.Id, ConnectionDirection.Outbound));
                    if (codec is ITextPayloadCodec textCodec)
                    {
                        return textCodec.FromText(text);
                    }
                }
                catch (Exception)
                {
                    // 回落到通用结果消息，由下游编解码失败时如实报错。
                }
            }
        }

        return PayloadMessage.From(new HandlerResult(handlerNodeId, "text", outputPort, text), "handler-result");
    }

    /// <summary>
    /// 向某个设备节点（client / server）发送一条业务消息。
    /// <para>
    /// 流程：查节点 → 用节点自身 codec 把消息编码成帧载荷 → 写入 socket。
    /// client 写入当前连接；server 广播给该监听端点的全部入站会话。
    /// </para>
    /// <para>
    /// 返回 <see cref="SendStatus.Sent"/> 仅表示字节已交给内核，不代表设备已执行。
    /// </para>
    /// </summary>
    /// <param name="nodeId">目标设备节点 id。</param>
    /// <param name="message">业务消息；类型须与该节点 codec 约定一致。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask<NodeSendResult> SendAsync(
        string nodeId,
        PayloadMessage message,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var graph = CurrentGraph;
        var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null || node.NodeType is not ("client" or "server"))
        {
            return NodeSendResult.Fail(
                SendStatus.InvalidNode, nodeId, $"节点 '{nodeId}' 不存在或不是设备节点");
        }

        var driver = _driver;
        if (driver is null || !_running)
        {
            return NodeSendResult.Fail(SendStatus.NotReady, nodeId, "编排引擎尚未运行");
        }

        // 编解码器是独立节点：从设备 tx 的上游 codec 节点解析其 codecId
        var codecId = FindCodecForDevice(graph, node);
        if (codecId is null)
        {
            return NodeSendResult.Fail(
                SendStatus.NoCodec, nodeId, "该设备的发送通道未连接编解码器节点（tx 的上游缺少 codec）");
        }

        var registration = _codecs.FirstOrDefault(
            c => string.Equals(c.CodecId, codecId, StringComparison.Ordinal));
        if (registration is null)
        {
            return NodeSendResult.Fail(
                SendStatus.NoCodec, nodeId, $"编解码器 '{codecId}' 未注册");
        }

        byte[] payload;
        try
        {
            var codec = registration.Factory(PayloadCodecContext.CreateForStandalone(
                codecId,
                nodeId,
                node.NodeType == "client" ? ConnectionDirection.Outbound : ConnectionDirection.Inbound));

            // 处理结果消息（HandlerResult）携带文本；若 codec 只认自己的消息类型，
            // 用 ITextPayloadCodec 把文本包装成该 codec 期望的类型再编码。
            var toEncode = message;
            if (message.HasValue && message.As<object>() is HandlerResult hr && codec is ITextPayloadCodec tc)
            {
                toEncode = tc.FromText(hr.Text);
            }

            var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
            codec.Encode(toEncode, buffer);
            payload = buffer.WrittenSpan.ToArray();
        }
        catch (Exception ex)
        {
            return NodeSendResult.Fail(
                SendStatus.EncodeFailed, nodeId, ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            if (node.NodeType == "client")
            {
                var deviceId = string.IsNullOrWhiteSpace(node.Get("deviceId")) ? node.Id : node.Get("deviceId");
                if (!driver.TryGetDevice(deviceId, out var handle) || handle.State != LinkState.Ready)
                {
                    return NodeSendResult.Fail(SendStatus.NotReady, nodeId, "客户端链路未就绪");
                }

                await handle.SendFrameAsync(payload, ct).ConfigureAwait(false);
                return NodeSendResult.Sent(nodeId, payload.Length, 1);
            }

            // server：广播给该监听端点的全部入站会话
            var listenerId = string.IsNullOrWhiteSpace(node.Get("listenerId")) ? node.Id : node.Get("listenerId");
            var sessions = driver.GetInboundSessions(listenerId);
            if (sessions.Count == 0)
            {
                return NodeSendResult.Fail(SendStatus.NotReady, nodeId, "该监听端点当前没有入站会话");
            }

            var sent = 0;
            foreach (var session in sessions)
            {
                try
                {
                    await session.SendFrameAsync(payload, ct).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception)
                {
                    // 单个会话失败不影响其它会话；计数只统计成功的。
                }
            }

            return sent > 0
                ? NodeSendResult.Sent(nodeId, payload.Length, sent)
                : NodeSendResult.Fail(SendStatus.WriteFailed, nodeId, "全部入站会话写入失败");
        }
        catch (Exception ex)
        {
            return NodeSendResult.Fail(SendStatus.WriteFailed, nodeId, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 向设备节点发送一段文本。仅当该节点的 codec 实现 <see cref="ITextPayloadCodec"/> 时可用；
    /// 否则返回 <see cref="SendStatus.EncodeFailed"/> 并说明原因。
    /// </summary>
    public async ValueTask<NodeSendResult> SendTextAsync(string nodeId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var graph = CurrentGraph;
        var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null || node.NodeType is not ("client" or "server"))
        {
            return NodeSendResult.Fail(SendStatus.InvalidNode, nodeId, $"节点 '{nodeId}' 不存在或不是设备节点");
        }

        // 编解码器是独立节点：从设备 tx 的上游 codec 节点解析
        var codecId = FindCodecForDevice(graph, node);
        if (codecId is null)
        {
            return NodeSendResult.Fail(
                SendStatus.NoCodec, nodeId, "该设备的发送通道未连接编解码器节点");
        }

        var registration = _codecs.FirstOrDefault(
            c => string.Equals(c.CodecId, codecId, StringComparison.Ordinal));
        if (registration is null)
        {
            return NodeSendResult.Fail(SendStatus.NoCodec, nodeId, $"编解码器 '{codecId}' 未注册");
        }

        IPayloadCodec codec;
        try
        {
            codec = registration.Factory(PayloadCodecContext.CreateForStandalone(
                codecId,
                nodeId,
                node.NodeType == "client" ? ConnectionDirection.Outbound : ConnectionDirection.Inbound));
        }
        catch (Exception ex)
        {
            return NodeSendResult.Fail(SendStatus.EncodeFailed, nodeId, ex.GetType().Name + ": " + ex.Message);
        }

        if (codec is not ITextPayloadCodec textCodec)
        {
            return NodeSendResult.Fail(
                SendStatus.EncodeFailed,
                nodeId,
                $"编解码器 '{codecId}' 未实现 ITextPayloadCodec，无法从纯文本构造消息");
        }

        return await SendAsync(nodeId, textCodec.FromText(text), ct).ConfigureAwait(false);
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
                graph.Nodes.Count(n => n.NodeType == "client"),
                graph.Nodes.Count(n => n.NodeType == "server"),
                0, 0, [], [.. _events], _lastError);
        }

        var nodes = new List<NodeStatus>(graph.Nodes.Count);
        var ready = 0;
        var sessions = 0;
        var ports = driver.GetListenerPorts();

        foreach (var node in graph.Nodes)
        {
            if (node.NodeType is not ("client" or "server"))
            {
                // 协议/编解码器/变换/处理器/出口：静态节点，只回显类型与标识。
                nodes.Add(new NodeStatus(node.Id, node.NodeType, "Static", 0, 0, 0, node.Label));
                continue;
            }

            if (node.NodeType == "client")
            {
                var deviceId = string.IsNullOrWhiteSpace(node.Get("deviceId")) ? node.Id : node.Get("deviceId");
                if (driver.TryGetDevice(deviceId, out var handle))
                {
                    var state = handle.State.ToString();
                    if (handle.State == LinkState.Ready)
                    {
                        ready++;
                    }

                    nodes.Add(new NodeStatus(node.Id, "client", state, handle.Epoch, 0, 0, deviceId));
                }
                else
                {
                    nodes.Add(new NodeStatus(node.Id, "client", "Absent", 0, 0, 0, deviceId));
                }

                continue;
            }

            var listenerId = string.IsNullOrWhiteSpace(node.Get("listenerId")) ? node.Id : node.Get("listenerId");
            var count = driver.GetListenerSessionCount(listenerId);
            sessions += count;
            ports.TryGetValue(listenerId, out var port);
            nodes.Add(new NodeStatus(node.Id, "server", port > 0 ? "Listening" : "Absent", 0, count, port, listenerId));
        }

        return new OrchestrationStatus(
            true, _revision,
            graph.Nodes.Count(n => n.NodeType == "client"),
            graph.Nodes.Count(n => n.NodeType == "server"),
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

    // ---- INodeTypeCatalogView：供节点类型校验「某项是否已注册」 ----

    /// <inheritdoc />
    public bool HasKind(string kindId) => _protocolMap.ContainsKey(kindId);

    /// <inheritdoc />
    public bool HasCodec(string codecId) => _codecs.Any(c => string.Equals(c.CodecId, codecId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool HasTransform(string transformId) =>
        _transforms.Any(t => string.Equals(t.Id, transformId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool HasHandler(string handlerId) =>
        _handlers.Any(h => string.Equals(h.Id, handlerId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool HasSink(string sinkId) =>
        _sinks.Any(s => string.Equals(s.Id, sinkId, StringComparison.Ordinal));

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

/// <summary>圆环缓冲部分：让引擎同时充当节点类型校验所需的注册表视图。</summary>
internal sealed class OrchestrationObserver : IDriverObserver
{
    private readonly OrchestrationEngine _engine;

    public OrchestrationObserver(OrchestrationEngine engine) => _engine = engine;

    public void OnEvent(in DriverEvent e) => _engine.RecordEvent(e);
}
