using System.Text.Json;
using LiteDriver.Orchestration;
using LiteDriver.Protocol;

namespace LiteDriver.Orchestration.AotSmoke;

/// <summary>
/// 编排库的 NativeAOT smoke：在裁剪后的原生二进制里验证
/// 图编译、源生成 JSON 往返、真实热应用与服务端监听。
/// </summary>
public static class Program
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
                new GraphNode { Id = "p", Kind = NodeKind.Protocol, Label = "p", KindId = "length-prefix" },
                new GraphNode { Id = "c", Kind = NodeKind.Client, Label = "c", Endpoints = ["127.0.0.1:1"] },
                new GraphNode { Id = "s", Kind = NodeKind.Server, Label = "s", Host = "127.0.0.1", Port = 0 }
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", From = "p", To = "c" },
                new GraphEdge { Id = "e2", From = "p", To = "s" }
            ]
        };

        var result = GraphCompiler.Compile(graph, ["length-prefix"]);
        Expect(result.Ok, "编排图编译通过");
        Expect(result.Config is not null, "编译产出 DriverConfig");

        // 负例：未连协议必须报错并可定位到节点
        var bad = new OrchestrationGraph
        {
            Nodes = [new GraphNode { Id = "lonely", Kind = NodeKind.Server, Label = "lonely", Host = "127.0.0.1" }],
            Edges = []
        };
        var badResult = GraphCompiler.Compile(bad, ["length-prefix"]);
        Expect(!badResult.Ok, "未连协议的节点被拒绝");
        Expect(badResult.Issues.Any(i => i.NodeId == "lonely"), "问题可定位到具体节点");
    }

    private static Task CheckJsonRoundTripAsync()
    {
        var graph = new OrchestrationGraph
        {
            Nodes =
            [
                new GraphNode
                {
                    Id = "p",
                    Kind = NodeKind.Protocol,
                    Label = "协议",
                    KindId = "stx-etx",
                    Settings = new Dictionary<string, string> { ["start"] = "02", ["end"] = "03" }
                }
            ],
            Edges = []
        };

        var json = JsonSerializer.Serialize(graph, OrchestrationJsonContext.Default.OrchestrationGraph);
        var back = JsonSerializer.Deserialize(json, OrchestrationJsonContext.Default.OrchestrationGraph);

        Expect(back is not null, "源生成 JSON 往返成功");
        Expect(back!.Nodes.Count == 1 && back.Nodes[0].Kind == NodeKind.Protocol, "枚举按字符串往返正确");
        Expect(back.Nodes[0].Settings!["start"] == "02", "协议参数往返正确");
        return Task.CompletedTask;
    }

    private static async Task CheckApplyAndListenAsync()
    {
        var protocols = new List<ProtocolRegistration>
        {
            new("length-prefix", "长度前缀", "2 字节大端长度 + 载荷", [], static ctx => new LengthKind(ctx), null)
        };

        await using var engine = new OrchestrationEngine(protocols);

        var graph = new OrchestrationGraph
        {
            Nodes =
            [
                new GraphNode { Id = "p", Kind = NodeKind.Protocol, Label = "p", KindId = "length-prefix" },
                new GraphNode { Id = "s", Kind = NodeKind.Server, Label = "s", Host = "127.0.0.1", Port = 0, MaxSessions = 4 }
            ],
            Edges = [new GraphEdge { Id = "e", From = "p", To = "s" }]
        };

        var report = await engine.ApplyAsync(graph);
        Expect(report.Ok, "编排图热应用成功");

        var status = engine.Status();
        Expect(status.Running, "引擎进入运行状态");

        var server = status.Nodes.FirstOrDefault(n => n.Kind == "Server");
        Expect(server is not null && server.Port > 0, $"服务端已监听真实端口（{server?.Port}）");

        // 端口回写：应用后图内端口应变为实际端口，重复应用才不会漂移
        var withPort = engine.CurrentGraph;
        Expect(withPort.Nodes.Any(n => n.Port > 0), "实际端口已回写进图");
        var portAfterFirst = withPort.Nodes.First(n => n.Kind == NodeKind.Server).Port;

        // 第二次应用是「端口 0 → 实际端口」那一次，必然计为更新；此后才应稳定。
        var second = await engine.ApplyAsync(withPort);
        var portAfterSecond = engine.CurrentGraph.Nodes.First(n => n.Kind == NodeKind.Server).Port;
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
