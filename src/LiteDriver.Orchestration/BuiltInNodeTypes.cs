namespace LiteDriver.Orchestration;

/// <summary>
/// 内置节点类型集合。端口类型是<b>强约束</b>，不是装饰：
/// <code>
/// 客户端 [协议] ──RawFrame──► 解码器 ──IMessage──► 处理器 ──IMessage──► 编码器 ──RawFrame──► 设备 tx
/// 服务端 [协议] ──RawFrame──► 解码器 ──IMessage──► 出口
/// </code>
/// 设备节点只产出 <c>RawFrame</c>，处理器只接受 <c>IMessage</c>，
/// 因此「客户端直连处理器」会被类型校验拒绝——必须经过编解码器。
/// </summary>
public static class BuiltInNodeTypes
{
    /// <summary>
    /// 编解码器节点：把原始帧转成业务消息，是 RawFrame → Message 的唯一通道。
    /// <para>
    /// 因为客户端/服务端输出 <c>RawFrame</c>、处理器只接受 <c>IMessage</c>，
    /// 两者之间<b>必须</b>经过本节点，否则连线会被类型校验拒绝。
    /// </para>
    /// </summary>
    /// <summary>
    /// 解码器节点：原始帧 → 业务消息（上行链路）。
    /// <para>
    /// 与编码器<b>分开</b>：上行只负责把设备上报的帧解析成消息，
    /// 下行只负责把消息序列化成帧。两者各自配置 codecId，
    /// 因此上下行可以用不同编解码策略而互不耦合。
    /// </para>
    /// </summary>
    public sealed class DecoderType : INodeType
    {
        public string TypeId => "decoder";

        public string DisplayName => "解码器";

        public string Description => "上行：原始帧 → 业务消息。只管解码，不参与下发。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "原始帧", PortDirection.Input, PortTypes.RawFrame, required: true),
            new PortSpec("out", "业务消息", PortDirection.Output, PortTypes.Message)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("codecId", "解码器", NodeFieldKind.Select, "", "宿主注册的编解码器（解码侧）",
                OptionsSource: "codecs")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var codecId = node.Get("codecId").Trim();
            if (codecId.Length == 0)
            {
                return "未选择解码器";
            }

            return catalog.HasCodec(codecId) ? null : $"编解码器 '{codecId}' 未注册";
        }
    }

    /// <summary>
    /// 编码器节点：业务消息 → 原始帧（下行链路）。
    /// <para>
    /// 与解码器分开，便于下行单独配置策略（例如不同的序列化版本），
    /// 不会因为调整上行而影响下行。
    /// </para>
    /// </summary>
    public sealed class EncoderType : INodeType
    {
        public string TypeId => "encoder";

        public string DisplayName => "编码器";

        public string Description => "下行：业务消息 → 原始帧。只管编码，不参与上报解析。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "业务消息", PortDirection.Input, PortTypes.Message, required: true),
            new PortSpec("out", "原始帧", PortDirection.Output, PortTypes.RawFrame)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("codecId", "编码器", NodeFieldKind.Select, "", "宿主注册的编解码器（编码侧）",
                OptionsSource: "codecs")
        ];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            var codecId = node.Get("codecId").Trim();
            if (codecId.Length == 0)
            {
                return "未选择编码器";
            }

            return catalog.HasCodec(codecId) ? null : $"编解码器 '{codecId}' 未注册";
        }
    }

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

        public string Description => "主动拨号到设备。协议是自身属性；tx 接原始帧写入设备，out 输出原始帧（须经编解码器转成业务消息）。";

        public bool IsSource => true;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            // 发送通道：接上游送来的原始帧，写入本设备 socket
            new PortSpec("tx", "发送帧", PortDirection.Input, PortTypes.RawFrame),
            // 上报：设备读到的裸帧，必须经编解码器才能变成业务消息
            new PortSpec("out", "原始帧", PortDirection.Output, PortTypes.RawFrame)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("kindId", "协议", NodeFieldKind.Select, "", "本设备使用的帧格式", OptionsSource: "kinds"),
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

        public string Description => "监听端口接受设备连接。in 端口写入的消息广播给入站会话，out 端口输出设备上报。";

        public bool IsSource => true;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            // 发送通道：接上游送来的原始帧，广播给本监听端点的全部入站会话
            new PortSpec("tx", "发送帧", PortDirection.Input, PortTypes.RawFrame),
            new PortSpec("out", "原始帧", PortDirection.Output, PortTypes.RawFrame)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("kindId", "协议", NodeFieldKind.Select, "", "本监听端点使用的帧格式", OptionsSource: "kinds"),
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

    /// <summary>
    /// 处理器节点：执行业务逻辑，并按处理结果路由到不同输出。
    /// <para>
    /// 三个命名输出对应三种结果：<c>ok</c>（成功）、<c>retry</c>（可重试失败）、
    /// <c>fail</c>（确定失败）。下游可分别接到不同设备节点或出口，
    /// 形成「设备上报 → 处理 → 结果回写/ 分流」的闭环。
    /// </para>
    /// </summary>
    public sealed class HandlerType : INodeType
    {
        public string TypeId => "handler";

        public string DisplayName => "处理器";

        public string Description => "执行业务逻辑，按结果路由：成功/可重试/失败三个输出可分别接不同下游。";

        public bool IsSource => false;

        public IReadOnlyList<PortSpec> Ports { get; } =
        [
            new PortSpec("in", "业务消息", PortDirection.Input, PortTypes.Message, required: true),
            new PortSpec("ok", "成功", PortDirection.Output, PortTypes.Message),
            new PortSpec("retry", "可重试", PortDirection.Output, PortTypes.Message),
            new PortSpec("fail", "失败", PortDirection.Output, PortTypes.Message)
        ];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField("handlerId", "处理器", NodeFieldKind.Select, "", "宿主注册的处理器", OptionsSource: "handlers"),
            new NodeField("mode", "处理模式", NodeFieldKind.Select, "log", "决定结果走哪个输出",
                [new NodeFieldOption("log", "只记录日志（走 ok）"),
                 new NodeFieldOption("echo", "回显：把输入回传（走 ok）"),
                 new NodeFieldOption("fail", "固定失败（走 fail）")])
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

    /// <summary>
    /// 占位节点：<b>端口代理</b>。绑定任意节点的任意输入/输出端口，
    /// 在画布上作为该端口的一个可移动副本存在，用来把走线整理整齐。
    /// <para>
    /// 连线连到占位节点，编译时会被解析为真实的目标节点与端口，
    /// 因此它不改变数据流语义，只改变线路的接入位置。
    /// </para>
    /// </summary>
    public sealed class PlaceholderType : INodeType
    {
        /// <summary>占位节点对外暴露的唯一端口 id。</summary>
        public const string ProxyPortId = "p";

        /// <summary>绑定目标节点 id 的设置键。</summary>
        public const string TargetNodeKey = "targetNode";

        /// <summary>绑定目标端口 id 的设置键。</summary>
        public const string TargetPortKey = "targetPort";

        public string TypeId => "placeholder";

        public string DisplayName => "占位";

        public string Description => "绑定某个节点的输入/输出端口，作为该端口的可移动副本，用于整理走线。";

        public bool IsSource => false;

        /// <summary>不生成设备/监听；连线由编译器解析到真实端口。</summary>
        public bool IsLayoutOnly => true;

        /// <summary>静态端口为空，实际端口随绑定目标变化。</summary>
        public IReadOnlyList<PortSpec> Ports { get; } = [];

        public IReadOnlyList<NodeField> Fields { get; } =
        [
            new NodeField(TargetNodeKey, "绑定节点", NodeFieldKind.Select, "",
                "选择要镜像哪个节点的端口", OptionsSource: "nodes"),
            new NodeField(TargetPortKey, "绑定端口", NodeFieldKind.Select, "",
                "选择该节点上的哪个端口", OptionsSource: "ports")
        ];

        /// <summary>端口依赖整张图解析，由编译器负责；此处返回空。</summary>
        public IReadOnlyList<PortSpec> PortsOf(GraphNode node, INodeTypeCatalogView catalog) => [];

        public string? Validate(GraphNode node, INodeTypeCatalogView catalog)
        {
            // 目标是否存在需要整张图，由编译器校验；这里只检查必填
            if (node.Get(TargetNodeKey).Trim().Length == 0)
            {
                return "未绑定节点";
            }

            if (node.Get(TargetPortKey).Trim().Length == 0)
            {
                return "未绑定端口";
            }

            return null;
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
