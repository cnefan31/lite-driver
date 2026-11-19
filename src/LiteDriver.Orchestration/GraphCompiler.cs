using System.Globalization;
using System.Text.Json;
using global::LiteDriver.Configuration;

namespace LiteDriver.Orchestration;

/// <summary>
/// 把编排图编译成 <see cref="DriverConfig"/> 与链路拓扑。
/// <para>
/// 编译是纯函数、不碰网络，因此能在应用前一次性报出全部问题并定位到节点/连线。
/// 连接合法性完全由<b>端口类型</b>决定，不在编译器里硬编码节点种类判断。
/// </para>
/// </summary>
public static class GraphCompiler
{
    /// <summary>编译编排图。</summary>
    public static CompileResult Compile(
        OrchestrationGraph graph,
        IReadOnlyCollection<string> knownKinds,
        NodeTypeCatalog? nodeTypes = null,
        INodeTypeCatalogView? catalogView = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(knownKinds);

        // 先迁移旧图，使后续逻辑只需处理新格式。
        GraphMigration.Migrate(graph);

        var types = nodeTypes ?? DefaultNodeTypes();
        var view = catalogView ?? new KindOnlyView(knownKinds);

        var issues = new List<CompileIssue>();
        var nodeMap = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        // ---- 1) 节点标识与类型 ----
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new CompileIssue("node_id_empty", "存在缺少 Id 的节点", null));
                continue;
            }

            if (!nodeMap.TryAdd(node.Id, node))
            {
                issues.Add(new CompileIssue("node_id_duplicate", $"节点 Id '{node.Id}' 重复", node.Id));
            }
        }

        if (issues.Count > 0)
        {
            return new CompileResult(false, null, issues, [], []);
        }

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeType))
            {
                issues.Add(new CompileIssue("node_type_empty", $"节点 '{node.Label}' 缺少类型", node.Id));
                continue;
            }

            var type = types.Find(node.NodeType);
            if (type is null)
            {
                issues.Add(new CompileIssue(
                    "node_type_unknown", $"节点 '{node.Label}' 的类型 '{node.NodeType}' 未注册", node.Id));
                continue;
            }

            var error = type.Validate(node, view);
            if (error is not null)
            {
                issues.Add(new CompileIssue("node_invalid", $"节点 '{node.Label}'：{error}", node.Id));
            }
        }

        // ---- 2) 连线：端口存在性 + 方向 + 类型兼容 ----
        foreach (var edge in graph.Edges)
        {
            if (!nodeMap.TryGetValue(edge.From, out var from))
            {
                issues.Add(new CompileIssue("edge_from_missing", $"连线起点 '{edge.From}' 不存在", edge.From));
                continue;
            }

            if (!nodeMap.TryGetValue(edge.To, out var to))
            {
                issues.Add(new CompileIssue("edge_to_missing", $"连线终点 '{edge.To}' 不存在", edge.To));
                continue;
            }

            var fromType = types.Find(from.NodeType);
            var toType = types.Find(to.NodeType);
            if (fromType is null || toType is null)
            {
                continue;
            }

            var outPort = fromType.Ports.FirstOrDefault(
                p => p.Direction == PortDirection.Output &&
                     string.Equals(p.Id, edge.FromPort, StringComparison.Ordinal));
            var inPort = toType.Ports.FirstOrDefault(
                p => p.Direction == PortDirection.Input &&
                     string.Equals(p.Id, edge.ToPort, StringComparison.Ordinal));

            if (outPort is null)
            {
                // 方向错误最常见：把连线接反了。给出可操作的提示。
                var reversed = fromType.Ports.Any(
                    p => p.Direction == PortDirection.Input && p.Id == edge.FromPort);
                issues.Add(new CompileIssue(
                    "edge_port_missing",
                    reversed
                        ? $"连线方向错误：'{from.Label}.{edge.FromPort}' 是输入端口，连线必须从输出端口出发"
                        : $"节点 '{from.Label}' 没有输出端口 '{edge.FromPort}'（连线起点必须是输出端口）",
                    from.Id));
                continue;
            }

            if (inPort is null)
            {
                var reversed = toType.Ports.Any(
                    p => p.Direction == PortDirection.Output && p.Id == edge.ToPort);
                issues.Add(new CompileIssue(
                    "edge_port_missing",
                    reversed
                        ? $"连线方向错误：'{to.Label}.{edge.ToPort}' 是输出端口，连线必须指向输入端口"
                        : $"节点 '{to.Label}' 没有输入端口 '{edge.ToPort}'（连线终点必须是输入端口）",
                    to.Id));
                continue;
            }

            if (!PortSpec.TypesCompatible(outPort.DataType, inPort.DataType))
            {
                issues.Add(new CompileIssue(
                    "edge_type_mismatch",
                    $"类型不匹配：{from.Label} 输出 {outPort.DataType}，但 {to.Label} 输入要求 {inPort.DataType}；" +
                    $"类型不匹配，中间需要一个转换节点",
                    to.Id));
            }
        }

        // ---- 3) 必连端口 + 链路完整性 ----
        CheckRequiredPorts(graph, types, issues);
        CheckNoCycles(graph, nodeMap, issues);

        if (issues.Count > 0)
        {
            return new CompileResult(false, null, issues, [], []);
        }

        // ---- 4) 生成 DriverConfig 与拓扑 ----
        return BuildConfig(graph, types, knownKinds, issues);
    }

    /// <summary>默认节点类型目录（六个内置类型）。</summary>
    public static NodeTypeCatalog DefaultNodeTypes() => new NodeTypeCatalog()
        .Register(new BuiltInNodeTypes.ClientType())
        .Register(new BuiltInNodeTypes.CodecType())
        .Register(new BuiltInNodeTypes.ServerType())
        .Register(new BuiltInNodeTypes.TransformType())
        .Register(new BuiltInNodeTypes.HandlerType())
        .Register(new BuiltInNodeTypes.SinkType());

    /// <summary>检查每条链路的必连端口是否都有连线。</summary>
    private static void CheckRequiredPorts(
        OrchestrationGraph graph,
        NodeTypeCatalog types,
        List<CompileIssue> issues)
    {
        foreach (var node in graph.Nodes)
        {
            var type = types.Find(node.NodeType);
            if (type is null)
            {
                continue;
            }

            foreach (var port in type.Ports)
            {
                if (!port.Required || port.Direction != PortDirection.Input)
                {
                    continue;
                }

                var connected = graph.Edges.Any(e =>
                    string.Equals(e.To, node.Id, StringComparison.Ordinal) &&
                    string.Equals(e.ToPort, port.Id, StringComparison.Ordinal));

                if (!connected)
                {
                    issues.Add(new CompileIssue(
                        "port_required_unconnected",
                        $"{type.DisplayName} '{node.Label}' 的必连输入 '{port.Label}' 未连接",
                        node.Id));
                }
            }
        }
    }

    /// <summary>检查环路：链路必须是有向无环图，否则运行时转发会自激。</summary>
    private static void CheckNoCycles(
        OrchestrationGraph graph,
        IReadOnlyDictionary<string, GraphNode> nodeMap,
        List<CompileIssue> issues)
    {
        var adjacency = graph.Nodes.ToDictionary(
            n => n.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var e in graph.Edges)
        {
            // 回写边不算「数据回流」：设备把处理结果写回 socket 是正当闭环。
            // · tx   —— 设备写入通道，与 out 上的上报是两条独立单向通道
            // · enc  —— 编解码器的编码入口，与 out 上的解码是另一条独立通道
            // 这两条边都不构成运行时自激，应从环路判定中排除。
            if (string.Equals(e.ToPort, "tx", StringComparison.Ordinal) ||
                string.Equals(e.ToPort, "enc", StringComparison.Ordinal))
            {
                continue;
            }

            if (adjacency.TryGetValue(e.From, out var list))
            {
                list.Add(e.To);
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in adjacency.Keys)
        {
            if (HasCycle(id, adjacency, visiting, done, out var culprit))
            {
                var label = nodeMap.TryGetValue(culprit, out var n) ? n.Label : culprit;
                issues.Add(new CompileIssue("graph_cycle", $"链路存在环路，经过节点 '{label}'", culprit));
                return;
            }
        }
    }

    private static bool HasCycle(
        string id,
        Dictionary<string, List<string>> adjacency,
        HashSet<string> visiting,
        HashSet<string> done,
        out string culprit)
    {
        culprit = id;
        if (done.Contains(id))
        {
            return false;
        }

        if (!visiting.Add(id))
        {
            return true;
        }

        foreach (var next in adjacency[id])
        {
            if (HasCycle(next, adjacency, visiting, done, out culprit))
            {
                return true;
            }
        }

        visiting.Remove(id);
        done.Add(id);
        return false;
    }

    private static CompileResult BuildConfig(
        OrchestrationGraph graph,
        NodeTypeCatalog types,
        IReadOnlyCollection<string> knownKinds,
        List<CompileIssue> issues)
    {
        var devices = new List<DeviceDescriptor>();
        var listeners = new List<ListenerDescriptor>();
        var topology = new List<FlowLink>();
        var usedDeviceIds = new HashSet<string>(StringComparer.Ordinal);
        var usedListenerIds = new HashSet<string>(StringComparer.Ordinal);

        // 编解码器/处理器/变换/出口节点在链路上传递，仅用于生成拓扑描述。
        foreach (var node in graph.Nodes)
        {
            switch (node.NodeType)
            {
                case "client":
                {
                    // 协议与编解码器是节点自身属性
                    var kindId = node.Get("kindId").Trim();
                    if (kindId.Length == 0 || !knownKinds.Contains(kindId))
                    {
                        break; // 已在节点校验中报告
                    }

                    var codecId = node.Get("codecId").Trim();

                    var deviceId = string.IsNullOrWhiteSpace(node.Get("deviceId"))
                        ? node.Id
                        : node.Get("deviceId").Trim();
                    if (!usedDeviceIds.Add(deviceId))
                    {
                        issues.Add(new CompileIssue("device_id_duplicate", $"设备标识 '{deviceId}' 重复", node.Id));
                        break;
                    }

                    var endpoints = new List<EndpointDescriptor>();
                    var eps = node.GetLines("endpoints");
                    for (var i = 0; i < eps.Length; i++)
                    {
                        if (!TryParseEndpoint(eps[i], out var host, out var port, out _))
                        {
                            continue;
                        }

                        endpoints.Add(new EndpointDescriptor { Host = host, Port = port, EndpointId = $"ep{i}" });
                    }

                    // 客户端自带的下游 codecId（若 codec 节点挂在它下游，由引擎关联）
                    devices.Add(new DeviceDescriptor
                    {
                        DeviceId = deviceId,
                        KindId = kindId,
                        Endpoints = [.. endpoints],
                        Retry = new RetryOptions
                        {
                            InitialMs = Math.Clamp(node.GetInt("retryInitialMs", 500), 1, 600_000),
                            MaxMs = Math.Clamp(node.GetInt("retryMaxMs", 30_000), 1, 3_600_000),
                            ConnectTimeoutMs = Math.Clamp(node.GetInt("connectTimeoutMs", 3_000), 100, 600_000),
                            Jitter = 0.2
                        },
                        Heartbeat = new HeartbeatOptions { Enabled = node.GetBool("heartbeatEnabled") },
                        CodecId = codecId.Length == 0 ? null : codecId
                    });
                    break;
                }

                case "server":
                {
                    var kindId = node.Get("kindId").Trim();
                    if (kindId.Length == 0 || !knownKinds.Contains(kindId))
                    {
                        break;
                    }

                    var codecId = node.Get("codecId").Trim();

                    var listenerId = string.IsNullOrWhiteSpace(node.Get("listenerId"))
                        ? node.Id
                        : node.Get("listenerId").Trim();
                    if (!usedListenerIds.Add(listenerId))
                    {
                        issues.Add(new CompileIssue("listener_id_duplicate", $"监听标识 '{listenerId}' 重复", node.Id));
                        break;
                    }

                    var host = node.Get("host").Trim();
                    listeners.Add(new ListenerDescriptor
                    {
                        ListenerId = listenerId,
                        KindId = kindId,
                        Host = host.Length == 0 ? "0.0.0.0" : host,
                        Port = Math.Clamp(node.GetInt("port", 0), 0, 65535),
                        MaxSessions = Math.Clamp(node.GetInt("maxSessions", 64), 1, 1024),
                        Heartbeat = new HeartbeatOptions { Enabled = node.GetBool("heartbeatEnabled") },
                        CodecId = codecId.Length == 0 ? null : codecId
                    });
                    break;
                }
            }
        }

        // 端口冲突（非 0 端口）
        var ports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in listeners)
        {
            if (l.Port == 0)
            {
                continue;
            }

            var key = l.Host + ":" + l.Port.ToString(CultureInfo.InvariantCulture);
            if (ports.TryGetValue(key, out var owner))
            {
                issues.Add(new CompileIssue("listener_port_conflict", $"监听地址 {key} 与 '{owner}' 冲突", l.ListenerId));
            }
            else
            {
                ports[key] = l.ListenerId;
            }
        }

        // 链路拓扑：从每个客户端/服务端出发，沿连线走一遍，记录节点序列。
        foreach (var node in graph.Nodes)
        {
            if (node.NodeType is not ("client" or "server"))
            {
                continue;
            }

            var outPort = "out";
            var path = new List<string> { node.Id };
            var current = node.Id;
            var guard = 0;

            while (guard++ < 64)
            {
                var edge = graph.Edges.FirstOrDefault(e =>
                    string.Equals(e.From, current, StringComparison.Ordinal) &&
                    (current != node.Id || string.Equals(e.FromPort, outPort, StringComparison.Ordinal)));

                if (edge is null)
                {
                    break;
                }

                var nextNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.To);
                if (nextNode is null)
                {
                    break;
                }

                path.Add(nextNode.Id);
                current = nextNode.Id;

                if (nextNode.NodeType is "handler" or "sink")
                {
                    break;
                }
            }

            topology.Add(new FlowLink(node.Id, [.. path]));
        }

        if (issues.Count > 0)
        {
            return new CompileResult(false, null, issues, [], []);
        }

        var config = new DriverConfig { Devices = [.. devices], Listeners = [.. listeners] };
        return new CompileResult(true, config, issues, topology, graph.Edges.Select(e => e.Id).ToArray());
    }

    /// <summary>解析 <c>host:port</c>，支持 <c>[::1]:502</c>。</summary>
    public static bool TryParseEndpoint(string? text, out string host, out int port, out string? error)
    {
        host = "";
        port = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "端点为空";
            return false;
        }

        var value = text.Trim();
        string hostPart;
        string portPart;

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                error = "IPv6 地址缺少 ']'";
                return false;
            }

            hostPart = value[1..close];
            var rest = value[(close + 1)..];
            if (!rest.StartsWith(':'))
            {
                error = "缺少端口（应形如 [::1]:502）";
                return false;
            }

            portPart = rest[1..];
        }
        else
        {
            var index = value.LastIndexOf(':');
            if (index <= 0 || index == value.Length - 1)
            {
                error = "应形如 host:port";
                return false;
            }

            hostPart = value[..index];
            portPart = value[(index + 1)..];
        }

        if (hostPart.Length == 0)
        {
            error = "主机为空";
            return false;
        }

        if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"端口 '{portPart}' 不是数字";
            return false;
        }

        if (parsed is < 1 or > 65535)
        {
            error = $"端口 {parsed} 越界（1..65535）";
            return false;
        }

        host = hostPart;
        port = parsed;
        return true;
    }

    /// <summary>只有 kind 注册信息的目录视图（用于不需要其它注册表的场景）。</summary>
    private sealed class KindOnlyView : INodeTypeCatalogView
    {
        private readonly IReadOnlyCollection<string> _kinds;

        public KindOnlyView(IReadOnlyCollection<string> kinds) => _kinds = kinds;

        public bool HasKind(string kindId) => _kinds.Contains(kindId);

        public bool HasCodec(string codecId) => true;

        public bool HasTransform(string transformId) => true;

        public bool HasHandler(string handlerId) => true;

        public bool HasSink(string sinkId) => true;
    }
}

/// <summary>一条从链路端点出发的消息流路径（节点 id 序列），供诊断与前端展示。</summary>
public sealed record FlowLink(string SourceNodeId, IReadOnlyList<string> Path);
