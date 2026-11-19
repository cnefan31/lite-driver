namespace LiteDriver.Orchestration;

/// <summary>
/// 内置节点类型集合。每个节点都是独立的，各自带输入/输出以承接上游、向下游输出；
/// 协议与编解码器是<b>设备节点自身的属性</b>，不是外挂的连线绑定。
/// <code>
/// 客户端 [自身: 协议+编解码器] ──IMessage──► 处理器 [in:IMessage]
/// 服务端 [自身: 协议+编解码器] ──IMessage──► 出口   [in:Any]
///                              └─IMessage──► 变换   [IMessage→IMessage] ──► 处理器
/// </code>
/// 端口类型仍然约束数据流：设备节点只输出 <c>IMessage</c>，
/// 因此不能与只接受 <c>IFrame</c> 的节点相连——约束落在数据流语义上，而非绑定关系上。
/// </summary>
public static class BuiltInNodeTypes
{
    /// <summary>
    /// 客户端节点：主动拨号，是链路起点。
    /// <para>
    /// 协议与编解码器都是<b>节点自身属性</b>，不从外部节点拉线绑定：
    /// 每个客户端独立维护自己的协议，互不影响。节点只对外输出数据流。
    /// </para>
    /// </summary>
    public sealed class ClientType : INodeType
    {
        public string TypeId => "client";

        public string DisplayName => "客户端";

        public string Description => "主动拨号到设备。协议与编解码器是节点自身属性；对外输出业务消息流。";

        public bool IsSource => true;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("out", "业务消息", PortDirection.Output, PortTypes.Message)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("kindId", "协议", NodeFieldKind.Select, "", "本设备使用的帧格式", OptionsSource: "kinds"),
            new NodeField("codecId", "编解码器", NodeFieldKind.Select, "", "帧转业务消息的方式；留空表示输出原始帧", OptionsSource: "codecs"),
            new NodeField("endpoints", "端点", NodeFieldKind.Lines, "127.0.0.1:502", "每行一个 host:port"),
            new NodeField("retryInitialMs", "初始退避 ms", NodeFieldKind.Number, "500"),
            new NodeField("retryMaxMs", "退避上限 ms", NodeFieldKind.Number, "30000"),
            new NodeField("connectTimeoutMs", "拨号超时 ms", NodeFieldKind.Number, "3000"),
            new NodeField("heartbeatEnabled", "应用层心跳", NodeFieldKind.Bool, "false", "协议需实现心跳，否则库会显式报错")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var kindId = node.Get("kindId").Trim();
            if (kindId.Length == 0)
            {
                return "未选择协议";
            }

            if (!catalog.HasKind(kindId))
            {
                return $"协议 '{kindId}' 未注册";
            }

            var codecId = node.Get("codecId").Trim();
            if (codecId.Length > 0 && !catalog.HasCodec(codecId))
            {
                return $"编解码器 '{codecId}' 未注册";
            }

            var eps = node.GetLines("endpoints");
            if (eps.Length == 0)
            {
                return "至少需要一个端点";
            }

            foreach (var ep in eps)
            {
                if (!GraphCompiler.TryParseEndpoint(ep, out _, out _, out var err))
                {
                    return $"端点 '{ep}' 非法：{err}";
                }
            }

            if (node.GetInt("retryInitialMs", 500) <= 0 || node.GetInt("retryMaxMs", 30000) <= 0)
            {
                return "退避参数必须为正";
            }

            return null;
        }
    }

    /// <summary>
    /// 服务端节点：监听端口，是链路起点。
    /// 协议与编解码器同样是节点自身属性，每个服务端独立维护。
    /// </summary>
    public sealed class ServerType : INodeType
    {
        public string TypeId => "server";

        public string DisplayName => "服务端";

        public string Description => "监听端口接受设备连接。协议与编解码器是节点自身属性；对外输出业务消息流。";

        public bool IsSource => true;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("out", "业务消息", PortDirection.Output, PortTypes.Message)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("kindId", "协议", NodeFieldKind.Select, "", "本监听端点使用的帧格式", OptionsSource: "kinds"),
            new NodeField("codecId", "编解码器", NodeFieldKind.Select, "", "帧转业务消息的方式；留空表示输出原始帧", OptionsSource: "codecs"),
            new NodeField("host", "监听地址", NodeFieldKind.Text, "0.0.0.0"),
            new NodeField("port", "端口", NodeFieldKind.Number, "0", "0 表示由系统分配"),
            new NodeField("maxSessions", "最大会话", NodeFieldKind.Number, "64"),
            new NodeField("heartbeatEnabled", "应用层心跳", NodeFieldKind.Bool, "false")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var kindId = node.Get("kindId").Trim();
            if (kindId.Length == 0)
            {
                return "未选择协议";
            }

            if (!catalog.HasKind(kindId))
            {
                return $"协议 '{kindId}' 未注册";
            }

            var codecId = node.Get("codecId").Trim();
            if (codecId.Length > 0 && !catalog.HasCodec(codecId))
            {
                return $"编解码器 '{codecId}' 未注册";
            }

            var port = node.GetInt("port", 0);
            if (port is < 0 or > 65535)
            {
                return $"端口 {port} 越界（0..65535）";
            }

            if (node.GetInt("maxSessions", 64) is < 1 or > 1024)
            {
                return "最大会话数必须在 1..1024";
            }

            if (node.Get("host").Trim().Length == 0)
            {
                return "监听地址不能为空";
            }

            return null;
        }
    }

    /// <summary>变换节点：在消息层面做字段级处理。</summary>
    public sealed class TransformType : INodeType
    {
        public string TypeId => "transform";

        public string DisplayName => "变换";

        public string Description => "对业务消息做转换（单位换算、取位、重命名等）。输入输出都是 IMessage，可串联多个。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "消息", PortDirection.Input, PortTypes.Message, required: true),
            new PortSpec("out", "消息", PortDirection.Output, PortTypes.Message)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("transformId", "变换器", NodeFieldKind.Select, "", "宿主注册的变换器", OptionsSource: "transforms"),
            new NodeField("expression", "参数", NodeFieldKind.Text, "", "由变换器自行解释")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var id = node.Get("transformId").Trim();
            if (id.Length == 0)
            {
                return "未选择变换器";
            }

            return catalog.HasTransform(id) ? null : $"变换器 '{id}' 未注册";
        }
    }

    /// <summary>处理器节点：业务逻辑终点。</summary>
    public sealed class HandlerType : INodeType
    {
        public string TypeId => "handler";

        public string DisplayName => "处理器";

        public string Description => "接收业务消息并执行业务逻辑。输入是 IMessage，因此不能直连客户端（中间必须经编解码器）。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "业务消息", PortDirection.Input, PortTypes.Message, required: true)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("handlerId", "处理器", NodeFieldKind.Select, "", "宿主注册的处理器", OptionsSource: "handlers"),
            new NodeField("mode", "处理模式", NodeFieldKind.Select, "log", "",
                [new NodeFieldOption("log", "记录日志"), new NodeFieldOption("echo", "回显应答")])
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var id = node.Get("handlerId").Trim();
            if (id.Length == 0)
            {
                return "未选择处理器";
            }

            return catalog.HasHandler(id) ? null : $"处理器 '{id}' 未注册";
        }
    }

    /// <summary>出口节点：把消息送往外部（日志、队列、另一条链路）。</summary>
    public sealed class SinkType : INodeType
    {
        public string TypeId => "sink";

        public string DisplayName => "出口";

        public string Description => "把处理完的消息送到外部：内存队列、日志或转发到另一条链路。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "业务消息", PortDirection.Input, PortTypes.Any, required: true)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("sinkId", "出口", NodeFieldKind.Select, "", "宿主注册的出口", OptionsSource: "sinks"),
            new NodeField("target", "目标", NodeFieldKind.Text, "", "如队列名或日志路径")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var id = node.Get("sinkId").Trim();
            if (id.Length == 0)
            {
                return "未选择出口";
            }

            return catalog.HasSink(id) ? null : $"出口 '{id}' 未注册";
        }
    }
}

/// <summary>
/// 校验时节点类型需要的注册表视图。只暴露「有没有」这一信息，
/// 避免把整个引擎暴露给节点类型实现。
/// </summary>
public interface INodeTypeCatalogView
{
    /// <summary>协议 kindId 是否已注册。</summary>
    bool HasKind(string kindId);

    /// <summary>编解码器 codecId 是否已注册。</summary>
    bool HasCodec(string codecId);

    /// <summary>变换器 transformId 是否已注册。</summary>
    bool HasTransform(string transformId);

    /// <summary>处理器 handlerId 是否已注册。</summary>
    bool HasHandler(string handlerId);

    /// <summary>出口 sinkId 是否已注册。</summary>
    bool HasSink(string sinkId);
}
