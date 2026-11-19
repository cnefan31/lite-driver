using LiteDriver.Orchestration;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>编排图编译：端口类型约束、必连端口、环路、配置生成与旧图迁移。</summary>
public sealed class OrchestrationCompilerTests
{
    private static readonly string[] Kinds = ["length-prefix", "stx-etx"];
    private static readonly string[] Codecs = ["text"];
    private static readonly NodeTypeCatalog Types = GraphCompiler.DefaultNodeTypes();

    private static GraphNode Node(string id, string type, params (string key, string value)[] settings)
    {
        var node = new GraphNode { Id = id, NodeType = type, Label = id };
        foreach (var (k, v) in settings)
        {
            node.Settings[k] = v;
        }

        return node;
    }

    private static GraphNode Protocol(string id, string kind = "length-prefix") =>
        Node(id, "protocol", ("kindId", kind));

    private static GraphNode Client(string id, string endpoints = "127.0.0.1:502") =>
        Node(id, "client", ("endpoints", endpoints));

    private static GraphNode Server(string id, int port = 9000) =>
        Node(id, "server", ("host", "127.0.0.1"), ("port", port.ToString()));

    private static GraphNode Codec(string id, string codecId = "text") =>
        Node(id, "codec", ("codecId", codecId));

    private static GraphNode Handler(string id) =>
        Node(id, "handler", ("handlerId", "log"));

    private static GraphNode Sink(string id) =>
        Node(id, "sink", ("sinkId", "queue"));

    private static OrchestrationGraph Graph(List<GraphNode> nodes, params (string from, string fromPort, string to, string toPort)[] edges) =>
        new()
        {
            Nodes = nodes,
            Edges = [.. edges.Select((e, i) => new GraphEdge
            {
                Id = $"e{i}", From = e.from, FromPort = e.fromPort, To = e.to, ToPort = e.toPort
            })]
        };

    private static CompileResult Compile(OrchestrationGraph g) =>
        GraphCompiler.Compile(g, Kinds, Types, new TestCatalogView());

    /// <summary>测试用注册表视图：声明若干 codec/transform/handler/sink。</summary>
    private sealed class TestCatalogView : INodeTypeCatalogView
    {
        public bool HasKind(string kindId) => Kinds.Contains(kindId);

        public bool HasCodec(string codecId) => Codecs.Contains(codecId);

        public bool HasTransform(string transformId) => transformId == "scale";

        public bool HasHandler(string handlerId) => handlerId == "log";

        public bool HasSink(string sinkId) => sinkId == "queue";
    }

    // ---------------- 端口类型约束（本次核心） ----------------

    [Fact]
    public void Client_ShouldNotConnectDirectlyToHandler()
    {
        // 用户明确要求的场景：客户端与报文处理逻辑之间必须有编解码器
        var g = Graph(
            [Protocol("p"), Client("c"), Handler("h")],
            ("p", "out", "c", "codec"),
            ("c", "in", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        var issue = Assert.Single(result.Issues, i => i.Code == "edge_type_mismatch");
        Assert.Contains("类型不匹配", issue.Message);
    }

    [Fact]
    public void Client_ShouldConnectToCodec()
    {
        var g = Graph(
            [Protocol("p"), Client("c"), Codec("k"), Handler("h")],
            ("p", "out", "c", "codec"),
            ("c", "in", "k", "in"),
            ("k", "out", "h", "in"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.NotNull(result.Config);
        Assert.Single(result.Config!.Devices);
        Assert.Single(result.Topology);
        Assert.Equal(["c", "k", "h"], result.Topology[0].Path);
    }

    [Fact]
    public void Codec_ShouldNotAcceptMessageInput()
    {
        // codec 输入是 IFrame，不能接另一个 codec 的 IMessage 输出
        var g = Graph(
            [Protocol("p"), Client("c"), Codec("k1"), Codec("k2"), Handler("h")],
            ("p", "out", "c", "codec"),
            ("c", "in", "k1", "in"),
            ("k1", "out", "k2", "in"),
            ("k2", "out", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_type_mismatch");
    }

    [Fact]
    public void Transform_ShouldChainBetweenMessages()
    {
        var g = Graph(
            [Protocol("p"), Client("c"), Codec("k"),
             Node("t", "transform", ("transformId", "scale"), ("expression", "x2")),
             Handler("h")],
            ("p", "out", "c", "codec"),
            ("c", "in", "k", "in"),
            ("k", "out", "t", "in"),
            ("t", "out", "h", "in"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Equal(["c", "k", "t", "h"], result.Topology[0].Path);
    }

    [Fact]
    public void Sink_ShouldAcceptMessage()
    {
        var g = Graph(
            [Protocol("p"), Client("c"), Codec("k"), Sink("s")],
            ("p", "out", "c", "codec"),
            ("c", "in", "k", "in"),
            ("k", "out", "s", "in"));

        Assert.True(Compile(g).Ok);
    }

    [Fact]
    public void Protocol_ShouldNotConnectToHandler()
    {
        var g = Graph(
            [Protocol("p"), Handler("h")],
            ("p", "out", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_type_mismatch");
    }

    // ---------------- 端口存在性与必连 ----------------

    [Fact]
    public void ShouldRejectUnknownPort()
    {
        var g = Graph(
            [Protocol("p"), Client("c")],
            ("p", "nope", "c", "codec"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_port_missing");
    }

    [Fact]
    public void ShouldRejectUnconnectedRequiredPort()
    {
        // 客户端必须接协议（codec 输入必连）
        var g = Graph([Client("c")]);

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "port_required_unconnected" && i.NodeId == "c");
    }

    [Fact]
    public void ShouldRejectHandlerWithoutInput()
    {
        var g = Graph([Protocol("p"), Handler("h")], ("p", "out", "h", "in"));

        var result = Compile(g);

        // 类型不匹配 + 必连缺失都应报出
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_type_mismatch");
    }

    // ---------------- 环路 ---------------

    [Fact]
    public void ShouldRejectCycle()
    {
        var g = Graph(
            [Protocol("p"), Client("c"), Codec("k"),
             Node("t1", "transform", ("transformId", "scale")),
             Node("t2", "transform", ("transformId", "scale"))],
            ("p", "out", "c", "codec"),
            ("c", "in", "k", "in"),
            ("k", "out", "t1", "in"),
            ("t1", "out", "t2", "in"),
            ("t2", "out", "t1", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "graph_cycle");
    }

    // ---------------- 节点自身校验 ----------------

    [Fact]
    public void ShouldRejectUnknownNodeType()
    {
        var g = Graph([Node("x", "not-a-type")]);
        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_type_unknown");
    }

    [Fact]
    public void ShouldRejectUnknownKindId()
    {
        var g = Graph([Protocol("p", "not-registered"), Client("c"), Codec("k")],
            ("p", "out", "c", "codec"), ("c", "in", "k", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_invalid" && i.NodeId == "p");
    }

    [Fact]
    public void ShouldRejectUnknownCodecId()
    {
        var g = Graph(
            [Protocol("p"), Client("c"), Codec("k", "not-a-codec"), Handler("h")],
            ("p", "out", "c", "codec"), ("c", "in", "k", "in"), ("k", "out", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_invalid" && i.NodeId == "k");
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:70000")]
    [InlineData(":502")]
    [InlineData("host:abc")]
    public void ShouldRejectInvalidEndpoint(string endpoint)
    {
        var g = Graph([Protocol("p"), Client("c", endpoint), Codec("k")],
            ("p", "out", "c", "codec"), ("c", "in", "k", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_invalid" && i.NodeId == "c");
    }

    [Fact]
    public void ShouldAcceptIpv6Endpoint()
    {
        Assert.True(GraphCompiler.TryParseEndpoint("[::1]:502", out var host, out var port, out _));
        Assert.Equal("::1", host);
        Assert.Equal(502, port);
    }

    [Fact]
    public void ShouldRejectConflictingListenerPorts()
    {
        var g = Graph(
            [Protocol("p"), Server("s1", 9000), Server("s2", 9000)],
            ("p", "out", "s1", "codec"), ("p", "out", "s2", "codec"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "listener_port_conflict");
    }

    [Fact]
    public void ShouldAllowMultipleEphemeralListeners()
    {
        var g = Graph(
            [Protocol("p"), Server("s1", 0), Server("s2", 0)],
            ("p", "out", "s1", "codec"), ("p", "out", "s2", "codec"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Equal(2, result.Config!.Listeners.Length);
    }

    [Fact]
    public void ShouldRejectDuplicateNodeIds()
    {
        var g = Graph([Protocol("dup"), Node("dup", "handler", ("handlerId", "log"))]);
        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_id_duplicate");
    }

    // ---------------- 旧图迁移 ----------------

    [Fact]
    public void ShouldMigrateLegacyGraphWithoutPorts()
    {
        // 旧格式：只有 kind，连线不带端口
        var legacy = new OrchestrationGraph
        {
            Nodes =
            [
                new GraphNode { Id = "p", Kind = "Protocol", Label = "协议", Y = 0 },
                new GraphNode { Id = "c", Kind = "Client", Label = "客户端" },
                new GraphNode { Id = "s", Kind = "Server", Label = "服务端" }
            ],
            Edges =
            [
                new GraphEdge { Id = "e1", From = "p", To = "c" },
                new GraphEdge { Id = "e2", From = "p", To = "s" }
            ]
        };
        legacy.Nodes[0].Settings["kindId"] = "length-prefix";

        GraphMigration.Migrate(legacy);

        // 迁移后：nodeType 就位、端口补齐、旧 kind 清空
        Assert.Equal("protocol", legacy.Nodes[0].NodeType);
        Assert.Equal("client", legacy.Nodes[1].NodeType);
        Assert.Equal("server", legacy.Nodes[2].NodeType);
        Assert.Null(legacy.Nodes[0].Kind);
        Assert.Equal("out", legacy.Edges[0].FromPort);
        Assert.Equal("codec", legacy.Edges[0].ToPort);
    }

    [Fact]
    public void MigratedLegacyGraph_ShouldStayValid()
    {
        var legacy = new OrchestrationGraph
        {
            Nodes =
            [
                new GraphNode { Id = "p", Kind = "Protocol", Label = "协议" },
                new GraphNode { Id = "c", Kind = "Client", Label = "客户端" }
            ],
            Edges = [new GraphEdge { Id = "e1", From = "p", To = "c" }]
        };
        legacy.Nodes[0].Settings["kindId"] = "length-prefix";
        legacy.Nodes[1].Settings["endpoints"] = "127.0.0.1:502";

        var result = Compile(legacy);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Single(result.Config!.Devices);
    }

    // ---------------- 其它 ----------------

    [Fact]
    public void Clone_ShouldBeDeepCopy()
    {
        var g = Graph([Protocol("p")]);
        g.Nodes[0].Settings["kindId"] = "v";

        var copy = g.Clone();
        copy.Nodes[0].Settings["kindId"] = "changed";

        Assert.Equal("v", g.Nodes[0].Settings["kindId"]);
    }

    [Fact]
    public void PortTypes_AnyShouldMatchEverything()
    {
        Assert.True(PortSpec.TypesCompatible(PortTypes.Frame, PortTypes.Any));
        Assert.True(PortSpec.TypesCompatible(PortTypes.Any, PortTypes.Message));
        Assert.True(PortSpec.TypesCompatible(PortTypes.Frame, PortTypes.Frame));
        Assert.False(PortSpec.TypesCompatible(PortTypes.Frame, PortTypes.Message));
    }

    [Fact]
    public void BuiltInTypes_ShouldDeclareExpectedPorts()
    {
        var client = Types.Find("client")!;
        Assert.Contains(client.Ports, p => p.Id == "codec" && p.Direction == PortDirection.Input && p.DataType == PortTypes.Frame);
        Assert.Contains(client.Ports, p => p.Id == "in" && p.Direction == PortDirection.Output && p.DataType == PortTypes.Frame);

        var handler = Types.Find("handler")!;
        Assert.Contains(handler.Ports, p => p.Id == "in" && p.DataType == PortTypes.Message);
    }
}
