using System.Text.Json;
using LiteDriver.Orchestration;
using LiteDriver.Protocol;

namespace LiteDriver.Orchestration.AotSmoke;

/// <summary>
/// 编排库的 NativeAOT smoke：在裁剪后的原生二进制里验证
/// 图编译、源生成 JSON 往返、真实热应用与服务端监听。
/// </summary>
public static partial class Program
{
    private static int _failures;

    public static async Task<int> Main()
    {
        Console.WriteLine("== LiteDriver.Orchestration NativeAOT smoke ==");
        Console.WriteLine($"IsDynamicCodeSupported = {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");

        CheckCompile();
        await CheckJsonRoundTripAsync();
        await CheckApplyAndListenAsync();

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckCompile()
    {
        var graph = new OrchestrationGraph
        {
            Nodes =
            [
                Node("c", "client", ("kindId", "length-prefix"), ("endpoints", "127.0.0.1:1")),
                Node("s", "server", ("kindId", "length-prefix"), ("host", "127.0.0.1"), ("port", "0")),
                Node("k", "decoder", ("codecId", "text")),
                Node("h", "handler", ("handlerId", "log"))
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", From = "c", FromPort = "out", To = "k", ToPort = "in" },
                new GraphEdge { Id = "e2", From = "k", FromPort = "out", To = "h", ToPort = "in" }
            ]
        };

        var result = GraphCompiler.Compile(
            graph, ["length-prefix"], GraphCompiler.DefaultNodeTypes(), new SmokeCatalogView());
        Expect(result.Ok, "编排图编译通过");
        Expect(result.Config is not null, "编译产出 DriverConfig");

        // 负例：未连协议必须报错并可定位到节点
        // 负例 1：设备缺协议 -> 节点配置非法
        var mismatch = new OrchestrationGraph
        {
            Nodes =
            [
                Node("c", "client", ("kindId", ""), ("endpoints", "127.0.0.1:1")),
                Node("h", "handler", ("handlerId", "log"))
            ],
            Edges = [new GraphEdge { Id = "e1", From = "c", FromPort = "out", To = "h", ToPort = "in" }]
        };
        var badResult = GraphCompiler.Compile(
            mismatch, ["length-prefix"], GraphCompiler.DefaultNodeTypes(), new SmokeCatalogView());
        Expect(!badResult.Ok, "设备缺协议被拒绝");
        Expect(badResult.Issues.Any(i => i.Code == "node_invalid"), "拒绝原因是节点配置非法");

        // 负例 2：必连端口未连接
        var orphan = new OrchestrationGraph
        {
            Nodes = [Node("lonely", "handler", ("handlerId", "log"))],
            Edges = []
        };
        var orphanResult = GraphCompiler.Compile(
            orphan, ["length-prefix"], GraphCompiler.DefaultNodeTypes(), new SmokeCatalogView());
        Expect(!orphanResult.Ok, "缺必连端口的节点被拒绝");
        Expect(orphanResult.Issues.Any(i => i.NodeId == "lonely"), "问题可定位到具体节点");
    }

    private static Task CheckJsonRoundTripAsync()
    {
        var graph = new OrchestrationGraph
        {
            Nodes =
            [
                new GraphNode
                {
                    Id = "c",
                    NodeType = "client",
                    Label = "客户端",
                    Settings = new Dictionary<string, string> { ["kindId"] = "stx-etx" }
                }
            ],
            Edges = []
        };

        var json = JsonSerializer.Serialize(graph, OrchestrationJsonContext.Default.OrchestrationGraph);
        var back = JsonSerializer.Deserialize(json, OrchestrationJsonContext.Default.OrchestrationGraph);

        Expect(back is not null, "源生成 JSON 往返成功");
        Expect(back!.Nodes.Count == 1 && back.Nodes[0].NodeType == "client", "节点类型往返正确");
        Expect(back.Nodes[0].Settings["kindId"] == "stx-etx", "协议字段往返正确");
        return Task.CompletedTask;
    }

    private static async Task CheckApplyAndListenAsync()
    {
        var protocols = new List<ProtocolRegistration>
        {
            new("length-prefix", "长度前缀", "2 字节大端长度 + 载荷", [], static ctx => new LengthKind(ctx), null)
        };
        var codecs = new List<CodecRegistration>
        {
            new("text", "文本", "UTF-8", static ctx => new SmokeTextCodec(ctx))
        };

        await using var engine = new OrchestrationEngine(protocols, codecs);

        var graph = new OrchestrationGraph
        {
            Nodes =
            [
                Node("s", "server", ("kindId", "length-prefix"),
                     ("host", "127.0.0.1"), ("port", "0"), ("maxSessions", "4"))
            ],
            Edges = []
        };

        var report = await engine.ApplyAsync(graph);
        Expect(report.Ok, "编排图热应用成功");

        var status = engine.Status();
        Expect(status.Running, "引擎进入运行状态");

        var server = status.Nodes.FirstOrDefault(n => n.Kind == "server");
        Expect(server is not null && server.Port > 0, $"服务端已监听真实端口（{server?.Port}）");

        // 端口回写：应用后图内端口应变为实际端口，重复应用才不会漂移
        var withPort = engine.CurrentGraph;
        Expect(withPort.Nodes.Any(n => n.NodeType == "server" && n.GetInt("port") > 0), "实际端口已回写进图");
        var portAfterFirst = withPort.Nodes.First(n => n.NodeType == "server").GetInt("port");

        // 第二次应用是「端口 0 → 实际端口」那一次，必然计为更新；此后才应稳定。
        var second = await engine.ApplyAsync(withPort);
        var portAfterSecond = engine.CurrentGraph.Nodes.First(n => n.NodeType == "server").GetInt("port");
        Expect(second.Ok && portAfterFirst == portAfterSecond, "第二次应用后端口不再漂移");

        var third = await engine.ApplyAsync(engine.CurrentGraph);
        Expect(third.Ok && third.Unchanged > 0, "第三次原样应用判定为未变");
    }

    private static void Expect(bool condition, string what)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}");
    }
}

/// <summary>smoke 用长度前缀协议。</summary>
internal sealed class LengthKind : IDeviceKind
{
    private readonly int _maxFrameSize;

    public LengthKind(in ProtocolContext context) => _maxFrameSize = context.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext context) => new LengthProtocol(_maxFrameSize);
}

/// <summary>smoke 用协议栈。</summary>
internal sealed class LengthProtocol : ISessionProtocol
{
    private readonly LengthPrefixFrameEncoder _encoder = new(2, bigEndian: true, lengthIncludesHeader: false);

    public LengthProtocol(int maxFrameSize) =>
        Decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, maxFrameSize);

    public IFrameDecoder Decoder { get; }

    public IFrameEncoder Encoder => _encoder;

    public ISessionInitializer? Initializer => null;

    public IHeartbeatPolicy? Heartbeat => null;

    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}

/// <summary>smoke 用节点构造辅助。</summary>
static partial class Program
{
    private static GraphNode Node(string id, string type, params (string key, string value)[] settings)
    {
        var node = new GraphNode { Id = id, NodeType = type, Label = id };
        foreach (var (k, v) in settings)
        {
            node.Settings[k] = v;
        }

        return node;
    }

    /// <summary>smoke 用注册表视图。</summary>
    private sealed class SmokeCatalogView : INodeTypeCatalogView
    {
        public bool HasKind(string kindId) => kindId == "length-prefix";

        public bool HasCodec(string codecId) => codecId == "text";

        public bool HasTransform(string transformId) => false;

        public bool HasHandler(string handlerId) => handlerId == "log";

        public bool HasSink(string sinkId) => sinkId == "queue";
    }
}

/// <summary>smoke 用编解码器。</summary>
internal sealed class SmokeTextCodec : IPayloadCodec
{
    public SmokeTextCodec(PayloadCodecContext ctx) => _ = ctx;

    public string CodecId => "text";

    public PayloadMessage Decode(ReadOnlySpan<byte> frame)
    {
        var payload = frame.Length > 2 ? frame[2..] : default;
        return PayloadMessage.From(new SmokeTextMessage(System.Text.Encoding.UTF8.GetString(payload)));
    }

    public void Encode(PayloadMessage message, System.Buffers.IBufferWriter<byte> writer)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message.As<SmokeTextMessage>().Text);
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }
}

/// <summary>smoke 业务消息。</summary>
internal sealed record SmokeTextMessage(string Text);
