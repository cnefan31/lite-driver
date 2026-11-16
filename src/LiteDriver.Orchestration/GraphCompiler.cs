using System.Globalization;
using System.Text.Json;
using global::LiteDriver.Configuration;

namespace LiteDriver.Orchestration;

/// <summary>
/// 把编排图编译成 <see cref="DriverConfig"/>。
/// 编译是纯函数、不碰网络，因此可以在应用前把所有问题一次性报出并定位到节点。
/// </summary>
public static class GraphCompiler
{
    /// <summary>编译编排图。</summary>
    /// <param name="graph">要编译的图。</param>
    /// <param name="knownKinds">宿主注册的协议种类键。</param>
    public static CompileResult Compile(OrchestrationGraph graph, IReadOnlyCollection<string> knownKinds)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(knownKinds);

        var issues = new List<CompileIssue>();
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        // ---- 1) 节点标识唯一性 ----
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new CompileIssue("node_id_empty", "存在缺少 Id 的节点", null));
                continue;
            }

            if (!nodes.TryAdd(node.Id, node))
            {
                issues.Add(new CompileIssue("node_id_duplicate", $"节点 Id '{node.Id}' 重复", node.Id));
            }
        }

        if (issues.Count > 0)
        {
            return new CompileResult(false, null, issues);
        }

        // ---- 2) 连线只允许 Protocol → Client/Server ----
        // 记录每个 Client/Server 引用的协议节点。
        var reference = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.From, out var from))
            {
                issues.Add(new CompileIssue("edge_from_missing", $"连线起点 '{edge.From}' 不存在", edge.From));
                continue;
            }

            if (!nodes.TryGetValue(edge.To, out var to))
            {
                issues.Add(new CompileIssue("edge_to_missing", $"连线终点 '{edge.To}' 不存在", edge.To));
                continue;
            }

            if (from.Kind != NodeKind.Protocol)
            {
                issues.Add(new CompileIssue(
                    "edge_from_not_protocol",
                    "连线必须从协议节点出发（协议 → 客户端 / 协议 → 服务端）",
                    from.Id));
                continue;
            }

            if (to.Kind == NodeKind.Protocol)
            {
                issues.Add(new CompileIssue("edge_to_protocol", "协议节点不能作为连线终点", to.Id));
                continue;
            }

            if (reference.TryGetValue(to.Id, out var existing))
            {
                if (existing != from.Id)
                {
                    issues.Add(new CompileIssue(
                        "node_multiple_protocols",
                        $"节点 '{to.Label}' 已绑定协议 '{existing}'，不能同时绑定多个协议",
                        to.Id));
                }

                continue;
            }

            reference[to.Id] = from.Id;
        }

        // ---- 3) 逐个节点校验 ----
        var devices = new List<DeviceDescriptor>();
        var listeners = new List<ListenerDescriptor>();
        var usedDeviceIds = new HashSet<string>(StringComparer.Ordinal);
        var usedListenerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            switch (node.Kind)
            {
                case NodeKind.Protocol:
                    CompileProtocol(node, knownKinds, issues);
                    break;

                case NodeKind.Client:
                    CompileClient(node, nodes, reference, knownKinds, devices, usedDeviceIds, issues);
                    break;

                case NodeKind.Server:
                    CompileServer(node, nodes, reference, knownKinds, listeners, usedListenerIds, issues);
                    break;

                default:
                    issues.Add(new CompileIssue("node_kind_unknown", $"未知节点类型 {node.Kind}", node.Id));
                    break;
            }
        }

        // ---- 4) 监听端口冲突（同一 host:port 非 0）----
        var ports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var listener in listeners)
        {
            if (listener.Port == 0)
            {
                continue;
            }

            var key = listener.Host + ":" + listener.Port.ToString(CultureInfo.InvariantCulture);
            if (ports.TryGetValue(key, out var owner))
            {
                issues.Add(new CompileIssue(
                    "listener_port_conflict", $"监听地址 {key} 与节点 '{owner}' 冲突", listener.ListenerId));
            }
            else
            {
                ports[key] = listener.ListenerId;
            }
        }

        if (issues.Count > 0)
        {
            return new CompileResult(false, null, issues);
        }

        var config = new DriverConfig
        {
            Devices = [.. devices],
            Listeners = [.. listeners]
        };

        return new CompileResult(true, config, issues);
    }

    private static void CompileProtocol(GraphNode node, IReadOnlyCollection<string> knownKinds, List<CompileIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(node.KindId))
        {
            issues.Add(new CompileIssue("protocol_kind_empty", $"协议节点 '{node.Label}' 未选择协议种类", node.Id));
            return;
        }

        if (!knownKinds.Contains(node.KindId))
        {
            issues.Add(new CompileIssue(
                "protocol_kind_unknown",
                $"协议节点 '{node.Label}' 使用了未注册的种类 '{node.KindId}'",
                node.Id));
        }
    }

    private static void CompileClient(
        GraphNode node,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, string> reference,
        IReadOnlyCollection<string> knownKinds,
        List<DeviceDescriptor> devices,
        HashSet<string> usedDeviceIds,
        List<CompileIssue> issues)
    {
        if (!reference.TryGetValue(node.Id, out var protocolNodeId))
        {
            issues.Add(new CompileIssue(
                "client_no_protocol",
                $"客户端节点 '{node.Label}' 未连接协议节点",
                node.Id));
            return;
        }

        var protocol = nodes[protocolNodeId];
        if (string.IsNullOrWhiteSpace(protocol.KindId) || !knownKinds.Contains(protocol.KindId!))
        {
            // 协议节点自身的问题已单独报告，这里不重复。
            return;
        }

        var deviceId = string.IsNullOrWhiteSpace(node.DeviceId) ? node.Id : node.DeviceId!.Trim();
        if (!usedDeviceIds.Add(deviceId))
        {
            issues.Add(new CompileIssue("device_id_duplicate", $"设备标识 '{deviceId}' 重复", node.Id));
            return;
        }

        var endpoints = new List<EndpointDescriptor>();
        if (node.Endpoints is null || node.Endpoints.Count == 0)
        {
            issues.Add(new CompileIssue(
                "client_no_endpoint",
                $"客户端节点 '{node.Label}' 至少需要一个端点",
                node.Id));
            return;
        }

        for (var i = 0; i < node.Endpoints.Count; i++)
        {
            if (!TryParseEndpoint(node.Endpoints[i], out var host, out var port, out var error))
            {
                issues.Add(new CompileIssue(
                    "client_endpoint_invalid",
                    $"客户端节点 '{node.Label}' 第 {i + 1} 个端点非法：{error}",
                    node.Id));
                continue;
            }

            endpoints.Add(new EndpointDescriptor
            {
                Host = host,
                Port = port,
                EndpointId = $"ep{i}"
            });
        }

        if (endpoints.Count == 0)
        {
            return;
        }

        devices.Add(new DeviceDescriptor
        {
            DeviceId = deviceId,
            KindId = protocol.KindId!,
            Endpoints = [.. endpoints],
            Retry = new RetryOptions
            {
                InitialMs = Math.Clamp(node.RetryInitialMs, 1, 600_000),
                MaxMs = Math.Clamp(node.RetryMaxMs, 1, 3_600_000),
                ConnectTimeoutMs = Math.Clamp(node.ConnectTimeoutMs, 100, 600_000),
                Jitter = 0.2
            },
            Heartbeat = new HeartbeatOptions
            {
                Enabled = node.HeartbeatEnabled,
                IntervalMs = Math.Clamp(node.HeartbeatIntervalMs, 100, 3_600_000),
                Misses = Math.Clamp(node.HeartbeatMisses, 1, 100)
            },
            KindSettings = BuildSettings(protocol)
        });
    }

    private static void CompileServer(
        GraphNode node,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, string> reference,
        IReadOnlyCollection<string> knownKinds,
        List<ListenerDescriptor> listeners,
        HashSet<string> usedListenerIds,
        List<CompileIssue> issues)
    {
        if (!reference.TryGetValue(node.Id, out var protocolNodeId))
        {
            issues.Add(new CompileIssue(
                "server_no_protocol",
                $"服务端节点 '{node.Label}' 未连接协议节点",
                node.Id));
            return;
        }

        var protocol = nodes[protocolNodeId];
        if (string.IsNullOrWhiteSpace(protocol.KindId) || !knownKinds.Contains(protocol.KindId!))
        {
            return;
        }

        var listenerId = string.IsNullOrWhiteSpace(node.ListenerId) ? node.Id : node.ListenerId!.Trim();
        if (!usedListenerIds.Add(listenerId))
        {
            issues.Add(new CompileIssue("listener_id_duplicate", $"监听标识 '{listenerId}' 重复", node.Id));
            return;
        }

        if (node.Port is < 0 or > 65535)
        {
            issues.Add(new CompileIssue(
                "server_port_range",
                $"服务端节点 '{node.Label}' 端口 {node.Port} 越界（0..65535）",
                node.Id));
            return;
        }

        listeners.Add(new ListenerDescriptor
        {
            ListenerId = listenerId,
            KindId = protocol.KindId!,
            Host = string.IsNullOrWhiteSpace(node.Host) ? "0.0.0.0" : node.Host.Trim(),
            Port = node.Port,
            MaxSessions = Math.Clamp(node.MaxSessions, 1, 1024),
            Heartbeat = new HeartbeatOptions
            {
                Enabled = node.HeartbeatEnabled,
                IntervalMs = Math.Clamp(node.HeartbeatIntervalMs, 100, 3_600_000),
                Misses = Math.Clamp(node.HeartbeatMisses, 1, 100)
            },
            KindSettings = BuildSettings(protocol)
        });
    }

    /// <summary>把协议节点的键值参数转成 <c>JsonElement</c>（引擎只接受 JSON）。</summary>
    private static JsonElement? BuildSettings(GraphNode protocol)
    {
        if (protocol.Settings is null || protocol.Settings.Count == 0)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(protocol.Settings, OrchestrationJsonContext.Default.DictionaryStringString);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>解析 <c>host:port</c>。支持 IPv6 字面量 <c>[::1]:502</c>。</summary>
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
}
