namespace LiteDriver.Orchestration;

/// <summary>
/// 内置节点类型集合。端口类型决定可连接性：
/// <code>
/// 客户端  [in:IFrame]  ──►  编解码器 [in:IFrame out:IMessage]  ──►  处理器 [in:IMessage]
///                        └─► 协议提供帧格式，输出 IFrame
/// </code>
/// 因此「客户端直连处理器」会因 <c>IFrame ≠ IMessage</c> 被拒——这就是用接口约束连接性的落点。
/// </summary>
public static class BuiltInNodeTypes
{
    /// <summary>协议节点：提供帧格式（分帧 + 组帧）。</summary>
    public sealed class ProtocolType : INodeType
    {
        public string TypeId => "protocol";

        public string DisplayName => "协议";

        public string Description => "定义帧格式（分帧与组帧）。输出原始帧，连接到客户端/服务端的协议端口。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "配置", PortDirection.Input, PortTypes.Any),
            new PortSpec("out", "帧格式", PortDirection.Output, PortTypes.Frame)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("kindId", "协议种类", NodeFieldKind.Select, "", "宿主注册的协议种类", OptionsSource: "kinds")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var kindId = node.Get("kindId").Trim();
            if (kindId.Length == 0)
            {
                return "未选择协议种类";
            }

            return catalog.HasKind(kindId) ? null : $"协议种类 '{kindId}' 未注册";
        }
    }

    /// <summary>客户端节点：主动拨号。输入需要帧格式，输出原始帧供后续解码。</summary>
    public sealed class ClientType : INodeType
    {
        public string TypeId => "client";

        public string DisplayName => "客户端";

        public string Description => "主动拨号到设备。需要协议提供帧格式；输出的原始帧必须经编解码器转成消息才能接处理器。";

        public bool IsSource => true;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("codec", "协议", PortDirection.Input, PortTypes.Frame, required: true),
            new PortSpec("in", "原始帧", PortDirection.Output, PortTypes.Frame)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("endpoints", "端点", NodeFieldKind.Lines, "127.0.0.1:502", "每行一个 host:port"),
            new NodeField("retryInitialMs", "初始退避 ms", NodeFieldKind.Number, "500"),
            new NodeField("retryMaxMs", "退避上限 ms", NodeFieldKind.Number, "30000"),
            new NodeField("connectTimeoutMs", "拨号超时 ms", NodeFieldKind.Number, "3000"),
            new NodeField("heartbeatEnabled", "应用层心跳", NodeFieldKind.Bool, "false", "协议需实现心跳，否则库会显式报错")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
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

    /// <summary>服务端节点：监听端口。输入需要帧格式，输出每个入站会话的原始帧。</summary>
    public sealed class ServerType : INodeType
    {
        public string TypeId => "server";

        public string DisplayName => "服务端";

        public string Description => "监听端口接受设备连接。需要协议提供帧格式；输出的原始帧必须经编解码器转成消息。";

        public bool IsSource => true;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("codec", "协议", PortDirection.Input, PortTypes.Frame, required: true),
            new PortSpec("conn", "入站帧", PortDirection.Output, PortTypes.Frame)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("host", "监听地址", NodeFieldKind.Text, "0.0.0.0"),
            new NodeField("port", "端口", NodeFieldKind.Number, "0", "0 表示由系统分配"),
            new NodeField("maxSessions", "最大会话", NodeFieldKind.Number, "64"),
            new NodeField("heartbeatEnabled", "应用层心跳", NodeFieldKind.Bool, "false")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
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

    /// <summary>编解码器节点：把原始帧转成业务消息。</summary>
    public sealed class CodecType : INodeType
    {
        public string TypeId => "codec";

        public string DisplayName => "编解码器";

        public string Description => "把原始帧解码成业务消息（对应 IPayloadCodec）。只有它能同时接受 IFrame 并产出 IMessage。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "原始帧", PortDirection.Input, PortTypes.Frame, required: true),
            new PortSpec("out", "业务消息", PortDirection.Output, PortTypes.Message)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("codecId", "编解码器", NodeFieldKind.Select, "", "宿主注册的编解码器", OptionsSource: "codecs")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var codecId = node.Get("codecId").Trim();
            if (codecId.Length == 0)
            {
                return "未选择编解码器";
            }

            return catalog.HasCodec(codecId) ? null : $"编解码器 '{codecId}' 未注册";
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
