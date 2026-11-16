
namespace LiteDriver.Orchestration;

/// <summary>编排图里的节点类型。</summary>
public enum NodeKind
{
    /// <summary>协议节点：定义帧格式，被客户端/服务端引用。</summary>
    Protocol,

    /// <summary>客户端节点：主动拨号到某个端点。</summary>
    Client,

    /// <summary>服务端节点：监听端口并接受连接。</summary>
    Server
}

/// <summary>
/// 编排图节点。三类节点共用一张表，用 <see cref="Kind"/> 区分；
/// 只有与 <see cref="Kind"/> 对应的字段会被读取，其余保持默认。
/// </summary>
public sealed class GraphNode
{
    /// <summary>节点唯一标识，图内不可重复。</summary>
    public string Id { get; set; } = "";

    public NodeKind Kind { get; set; }

    /// <summary>画布上的显示名，可随意修改，不参与业务语义。</summary>
    public string Label { get; set; } = "";

    /// <summary>画布坐标，仅前端使用，后端原样保存。</summary>
    public double X { get; set; }

    public double Y { get; set; }

    // ---- Protocol 节点 ----

    /// <summary>协议种类键，必须是宿主注册的 kind（如 <c>length-prefix</c>）。</summary>
    public string? KindId { get; set; }

    /// <summary>协议私有参数。起始/结束标记等放在这里，如 <c>{"start":"02","end":"03"}</c>。</summary>
    public Dictionary<string, string>? Settings { get; set; }

    // ---- Client 节点 ----

    /// <summary>设备标识（仅 Client 有效）。为空时由 <see cref="GraphNode.Id"/> 派生。</summary>
    public string? DeviceId { get; set; }

    /// <summary>端点列表，形如 <c>127.0.0.1:502</c>（仅 Client 有效）。</summary>
    public List<string>? Endpoints { get; set; }

    /// <summary>首次重连退避毫秒（仅 Client 有效）。</summary>
    public int RetryInitialMs { get; set; } = 500;

    /// <summary>重连退避上限毫秒（仅 Client 有效）。</summary>
    public int RetryMaxMs { get; set; } = 30_000;

    /// <summary>拨号超时毫秒（仅 Client 有效）。</summary>
    public int ConnectTimeoutMs { get; set; } = 3_000;

    // ---- Server 节点 ----

    /// <summary>监听标识（仅 Server 有效）。为空时由 <see cref="GraphNode.Id"/> 派生。</summary>
    public string? ListenerId { get; set; }

    /// <summary>监听地址（仅 Server 有效）。默认 0.0.0.0。</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>监听端口（仅 Server 有效）。0 表示由系统分配。</summary>
    public int Port { get; set; }

    /// <summary>该端点最大并发会话数（仅 Server 有效）。</summary>
    public int MaxSessions { get; set; } = 64;

    // ---- 公共 ----

    /// <summary>是否启用应用层心跳。心跳策略由协议实现决定。</summary>
    public bool HeartbeatEnabled { get; set; }

    /// <summary>心跳间隔毫秒。</summary>
    public int HeartbeatIntervalMs { get; set; } = 5_000;

    /// <summary>连续未回应判定失效次数。</summary>
    public int HeartbeatMisses { get; set; } = 3;
}

/// <summary>编排图连线：从 <see cref="From"/> 节点指向 <see cref="To"/> 节点。</summary>
public sealed class GraphEdge
{
    public string Id { get; set; } = "";

    public string From { get; set; } = "";

    public string To { get; set; } = "";
}

/// <summary>
/// 编排图。它是唯一事实来源：后端据此编译出 <c>DriverConfig</c> 并热应用，
/// 图本身可被原样保存与恢复，重启宿主后重放即可。
/// </summary>
public sealed class OrchestrationGraph
{
    /// <summary>图版本号，每次保存自增，便于前端判断是否需要刷新。</summary>
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
            Kind = n.Kind,
            Label = n.Label,
            X = n.X,
            Y = n.Y,
            KindId = n.KindId,
            Settings = n.Settings is null ? null : new Dictionary<string, string>(n.Settings),
            DeviceId = n.DeviceId,
            Endpoints = n.Endpoints is null ? null : [.. n.Endpoints],
            RetryInitialMs = n.RetryInitialMs,
            RetryMaxMs = n.RetryMaxMs,
            ConnectTimeoutMs = n.ConnectTimeoutMs,
            ListenerId = n.ListenerId,
            Host = n.Host,
            Port = n.Port,
            MaxSessions = n.MaxSessions,
            HeartbeatEnabled = n.HeartbeatEnabled,
            HeartbeatIntervalMs = n.HeartbeatIntervalMs,
            HeartbeatMisses = n.HeartbeatMisses
        })],
        Edges = [.. Edges.Select(e => new GraphEdge { Id = e.Id, From = e.From, To = e.To })]
    };
}

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

/// <summary>编译结果。</summary>
public sealed record CompileResult(
    bool Ok,
    global::LiteDriver.Configuration.DriverConfig? Config,
    IReadOnlyList<CompileIssue> Issues);

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
