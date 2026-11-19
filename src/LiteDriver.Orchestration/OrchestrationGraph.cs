using System.Text.Json.Serialization;

namespace LiteDriver.Orchestration;

/// <summary>
/// 编排图节点。所有节点共用一张表，用 <see cref="NodeType"/> 区分种类；
/// 只有该种类声明的字段会被读取，其余保持默认。
/// <para>
/// <b>兼容性</b>：<see cref="Kind"/> 是为兼容 0.2.0 及更早的旧图保留的字段，
/// 新图一律使用 <see cref="NodeType"/>；读取时会自动迁移（见 <see cref="GraphMigration"/>）。
/// </para>
/// </summary>
public sealed class GraphNode
{
    /// <summary>节点唯一标识，图内不可重复。</summary>
    public string Id { get; set; } = "";

    /// <summary>节点类型键，对应 <see cref="INodeType.TypeId"/>。</summary>
    public string NodeType { get; set; } = "";

    /// <summary>
    /// 【已弃用，仅用于迁移旧图】旧版的节点种类枚举名（Protocol/Client/Server）。
    /// 迁移完成后会被清空。
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>界面显示名。</summary>
    public string Label { get; set; } = "";

    /// <summary>画布 X 坐标（前端使用，后端原样保存）。</summary>
    public double X { get; set; }

    /// <summary>画布 Y 坐标。</summary>
    public double Y { get; set; }

    /// <summary>
    /// 节点参数。键由各 <see cref="INodeType.Fields"/> 声明；
    /// 值统一用字符串存储，便于前端直接编辑与 JSON 往返。
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>读取一个设置项，不存在时返回空串。</summary>
    public string Get(string key) => Settings.TryGetValue(key, out var v) ? v : "";

    /// <summary>读取整数设置项。</summary>
    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    /// <summary>读取布尔设置项。</summary>
    public bool GetBool(string key) =>
        bool.TryParse(Get(key), out var v) && v;

    /// <summary>读取多行设置项。</summary>
    public string[] GetLines(string key) =>
        Get(key).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>编排图连线：从 <see cref="From"/> 节点的 <see cref="FromPort"/> 指向 <see cref="To"/> 节点的 <see cref="ToPort"/>。</summary>
public sealed class GraphEdge
{
    public string Id { get; set; } = "";

    public string From { get; set; } = "";

    /// <summary>起点的输出端口 id。</summary>
    public string FromPort { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>终点的输入端口 id。</summary>
    public string ToPort { get; set; } = "";
}

/// <summary>
/// 编排图。它是唯一事实来源：后端据此编译出 <c>DriverConfig</c> 与链路拓扑。
/// </summary>
public sealed class OrchestrationGraph
{
    /// <summary>图版本号，每次成功应用自增。</summary>
    public long Revision { get; set; }

    public List<GraphNode> Nodes { get; set; } = [];

    public List<GraphEdge> Edges { get; set; } = [];

    /// <summary>深拷贝，避免引擎持有的快照被调用方修改。</summary>
    public OrchestrationGraph Clone() => new()
    {
        Revision = Revision,
        Nodes = [.. Nodes.Select(n => new GraphNode
        {
            Id = n.Id,
            NodeType = n.NodeType,
            Kind = n.Kind,
            Label = n.Label,
            X = n.X,
            Y = n.Y,
            Settings = new Dictionary<string, string>(n.Settings, StringComparer.Ordinal)
        })],
        Edges = [.. Edges.Select(e => new GraphEdge
        {
            Id = e.Id,
            From = e.From,
            FromPort = e.FromPort,
            To = e.To,
            ToPort = e.ToPort
        })]
    };
}

/// <summary>
/// 旧图迁移：0.2.0 及更早的图只有 <c>kind</c>（Protocol/Client/Server）且连线不带端口。
/// 迁移后统一成 <c>nodeType</c> + 端口，使旧图无需手工重建。
/// </summary>
public static class GraphMigration
{
    /// <summary>把旧格式节点/连线就地升级为新格式。已是新格式时不做改动。</summary>
    public static void Migrate(OrchestrationGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var node in graph.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.NodeType))
            {
                continue;
            }

            var legacy = node.Kind ?? "";
            node.NodeType = legacy switch
            {
                "Protocol" => "protocol",
                "Client" => "client",
                "Server" => "server",
                _ => node.NodeType
            };

            // 旧图的协议参数在顶层 Settings（start/end 等），新模型沿用 Settings，无需搬移。
            node.Kind = null;
        }

        // 旧连线没有端口：按两端节点类型补默认端口，使「协议 → 客户端/服务端」继续有效。
        foreach (var edge in graph.Edges)
        {
            if (!string.IsNullOrWhiteSpace(edge.FromPort) && !string.IsNullOrWhiteSpace(edge.ToPort))
            {
                continue;
            }

            var from = graph.Nodes.FirstOrDefault(n => n.Id == edge.From);
            var to = graph.Nodes.FirstOrDefault(n => n.Id == edge.To);
            if (from is null || to is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(edge.FromPort))
            {
                edge.FromPort = DefaultOutputPort(from.NodeType);
            }

            if (string.IsNullOrWhiteSpace(edge.ToPort))
            {
                edge.ToPort = DefaultInputPort(to.NodeType);
            }
        }
    }

    /// <summary>各类型的默认输出端口。</summary>
    public static string DefaultOutputPort(string nodeType) => nodeType switch
    {
        "protocol" => "out",
        "codec" => "out",
        "transform" => "out",
        "client" => "in",
        "server" => "conn",
        _ => "out"
    };

    /// <summary>各类型的默认输入端口。</summary>
    public static string DefaultInputPort(string nodeType) => nodeType switch
    {
        "client" => "codec",
        "server" => "conn",
        "codec" => "in",
        "transform" => "in",
        "handler" => "in",
        "sink" => "in",
        // 旧模型里连线是「协议 → 客户端」，语义是「客户端使用该协议」，
        // 迁移时把协议输出接到客户端/服务端的协议输入端口。
        "protocol" => "in",
        _ => "in"
    };
}

/// <summary>编译结果。</summary>
public sealed record CompileResult(
    bool Ok,
    global::LiteDriver.Configuration.DriverConfig? Config,
    IReadOnlyList<CompileIssue> Issues,
    IReadOnlyList<FlowLink> Topology,
    IReadOnlyList<string> EdgeIds);

/// <summary>编译期问题。带 <see cref="NodeId"/> 时前端可就地高亮该节点。</summary>
public sealed record CompileIssue(string Code, string Message, string? NodeId);

/// <summary>应用编排图的结果。</summary>
public sealed record ApplyReport(
    bool Ok,
    long Revision,
    string Message,
    IReadOnlyList<CompileIssue> Issues,
    int Added,
    int Updated,
    int Removed,
    int Unchanged);

/// <summary>运行时快照，供前端实时刷新。</summary>
public sealed record OrchestrationStatus(
    bool Running,
    long Revision,
    int ClientCount,
    int ServerCount,
    int ReadyClients,
    int InboundSessions,
    IReadOnlyList<NodeStatus> Nodes,
    IReadOnlyList<EventEntry> Events,
    string? LastError);

/// <summary>单个节点的运行时状态。</summary>
public sealed record NodeStatus(
    string Id,
    string Kind,
    string State,
    long Epoch,
    int Sessions,
    int Port,
    string? Detail);

/// <summary>事件流条目（驱动内部 struct 不直接对外序列化）。</summary>
public sealed record EventEntry(
    string Kind,
    string Direction,
    string? Target,
    long Epoch,
    string? Detail);

/// <summary>协议元信息，供前端渲染协议节点的参数表单。</summary>
public sealed record ProtocolOption(
    string KindId,
    string DisplayName,
    string Description,
    IReadOnlyList<ProtocolField> Fields);

/// <summary>协议节点的可编辑参数。</summary>
public sealed record ProtocolField(
    string Key,
    string Label,
    string Placeholder,
    string? DefaultValue,
    string Help);

/// <summary>节点类型元信息（供前端渲染托盘与属性面板）。</summary>
public sealed record NodeTypeInfo(
    string TypeId,
    string DisplayName,
    string Description,
    bool IsSource,
    IReadOnlyList<PortInfo> Ports,
    IReadOnlyList<FieldInfo> Fields);

/// <summary>端口元信息。</summary>
public sealed record PortInfo(
    string Id,
    string Label,
    string Direction,
    string DataType,
    bool Required);

/// <summary>字段元信息。</summary>
public sealed record FieldInfo(
    string Key,
    string Label,
    string Kind,
    string? DefaultValue,
    string Help,
    IReadOnlyList<OptionInfo> Options,
    string? OptionsSource);

/// <summary>注册表候选清单，供前端渲染下拉（键 = 注册表名）。</summary>
public sealed record RegistryOptions(IReadOnlyList<OptionInfo> Kinds, IReadOnlyList<OptionInfo> Codecs,
    IReadOnlyList<OptionInfo> Transforms, IReadOnlyList<OptionInfo> Handlers, IReadOnlyList<OptionInfo> Sinks);

/// <summary>下拉候选。</summary>
public sealed record OptionInfo(string Value, string Label);

/// <summary>可连接性查询请求：判断 from 的某端口能否连到 to 的某端口。</summary>
public sealed record ConnectionProbe(
    string FromNodeType,
    string FromPort,
    string ToNodeType,
    string ToPort);

/// <summary>可连接性探测结果。</summary>
public sealed record ConnectableResult(bool Ok, string? Reason);
