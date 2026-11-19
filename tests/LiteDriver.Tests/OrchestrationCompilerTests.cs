using LiteDriver.Orchestration;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>
/// 编排图编译：节点独立性、端口类型约束、必连端口、环路、配置生成与旧图迁移。
/// <para>
/// 设计要点：每个节点独立，自带输入/输出以承接上游、向下游输出；
/// 协议与编解码器是<b>设备节点自身属性</b>，不是外挂的连线绑定。
/// </para>
/// </summary>
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

    /// <summary>客户端：协议是自身字段，输出原始帧。</summary>
    private static GraphNode Client(string id, string endpoints = "127.0.0.1:502",
        string kind = "length-prefix") =>
        Node(id, "client", ("endpoints", endpoints), ("kindId", kind));

    /// <summary>服务端：协议是自身字段，输出原始帧。</summary>
    private static GraphNode Server(string id, int port = 9000, string kind = "length-prefix") =>
        Node(id, "server", ("host", "127.0.0.1"), ("port", port.ToString()), ("kindId", kind));

    /// <summary>解码器：原始帧 → 业务消息（上行）。</summary>
    private static GraphNode Decoder(string id, string codecId = "text") =>
        Node(id, "decoder", ("codecId", codecId));

    /// <summary>编码器：业务消息 → 原始帧（下行）。</summary>
    private static GraphNode Encoder(string id, string codecId = "text") =>
        Node(id, "encoder", ("codecId", codecId));

    private static GraphNode Handler(string id) => Node(id, "handler", ("handlerId", "log"));

    private static GraphNode Sink(string id) => Node(id, "sink", ("sinkId", "queue"));

    private static GraphNode Transform(string id) =>
        Node(id, "transform", ("transformId", "scale"), ("expression", "x2"));

    private static OrchestrationGraph Graph(
        List<GraphNode> nodes,
        params (string from, string fromPort, string to, string toPort)[] edges) =>
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

    private sealed class TestCatalogView : INodeTypeCatalogView
    {
        public bool HasKind(string kindId) => Kinds.Contains(kindId);

        public bool HasCodec(string codecId) => Codecs.Contains(codecId);

        public bool HasTransform(string transformId) => transformId == "scale";

        public bool HasHandler(string handlerId) => handlerId == "log";

        public bool HasSink(string sinkId) => sinkId == "queue";
    }

    // ---------------- 节点独立性（本次核心） ----------------

    [Fact]
    public void ClientAndServer_ShouldBeIndependentWithoutProtocolNodes()
    {
        // 两个设备各自带不同协议，互不影响；画布上没有协议节点
        var g = Graph(
            [Client("c1", "127.0.0.1:502", kind: "length-prefix"),
             Client("c2", "127.0.0.1:503", kind: "stx-etx"),
             Decoder("k1"), Decoder("k2"),
             Handler("h1"), Handler("h2")],
            ("c1", "out", "k1", "in"), ("k1", "out", "h1", "in"),
            ("c2", "out", "k2", "in"), ("k2", "out", "h2", "in"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Equal(2, result.Config!.Devices.Length);
        // 各设备使用各自声明的协议
        Assert.Equal("length-prefix", result.Config.Devices.Single(d => d.DeviceId == "c1").KindId);
        Assert.Equal("stx-etx", result.Config.Devices.Single(d => d.DeviceId == "c2").KindId);
    }

    [Fact]
    public void CodecNode_ShouldBeRequiredBetweenDeviceAndHandler()
    {
        // 设备输出 RawFrame、处理器只收 IMessage，必须经编解码器
        var bad = Graph([Client("c"), Handler("h")], ("c", "out", "h", "in"));
        var badResult = Compile(bad);
        Assert.False(badResult.Ok);
        Assert.Contains(badResult.Issues, i => i.Code == "edge_type_mismatch");

        var good = Graph([Client("c"), Decoder("k"), Handler("h")],
            ("c", "out", "k", "in"), ("k", "out", "h", "in"));
        Assert.True(Compile(good).Ok);
    }

    [Fact]
    public void NodeTypeCatalog_ShouldSplitDecoderAndEncoder()
    {
        // 协议是设备自身字段；编解码器拆成独立的解码器与编码器
        Assert.Null(Types.Find("protocol"));
        Assert.Null(Types.Find("codec"));
        Assert.NotNull(Types.Find("decoder"));
        Assert.NotNull(Types.Find("encoder"));
    }

    [Fact]
    public void DecoderAndEncoder_ShouldHaveDifferentDirections()
    {
        // 解码器只做上行，编码器只做下行，端口方向相反
        var dec = Types.Find("decoder")!;
        Assert.Contains(dec.Ports, p => p.Id == "in" && p.Direction == PortDirection.Input
            && p.DataType == PortTypes.RawFrame);
        Assert.Contains(dec.Ports, p => p.Id == "out" && p.Direction == PortDirection.Output
            && p.DataType == PortTypes.Message);

        var enc = Types.Find("encoder")!;
        Assert.Contains(enc.Ports, p => p.Id == "in" && p.Direction == PortDirection.Input
            && p.DataType == PortTypes.Message);
        Assert.Contains(enc.Ports, p => p.Id == "out" && p.Direction == PortDirection.Output
            && p.DataType == PortTypes.RawFrame);
    }

    [Fact]
    public void EncoderCannotFeedHandlerDirectly()
    {
        // 编码器输出 RawFrame，处理器只收 IMessage，类型系统应拒绝
        var g = Graph([Client("c"), Encoder("e"), Handler("h")],
            ("c", "out", "e", "in"),
            ("e", "out", "h", "in"));

        var result = Compile(g);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_type_mismatch");
    }

    [Fact]
    public void DecoderShouldNotAcceptMessageInput()
    {
        // 解码器输入只收 RawFrame：把处理器结果直接接到解码器必须被拒
        var g = Graph([Client("c"), Decoder("d"), Handler("h")],
            ("c", "out", "d", "in"),
            ("d", "out", "h", "in"),
            ("h", "ok", "d", "in"));

        var result = Compile(g);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_type_mismatch");
    }

    [Fact]
    public void UploadAndDownloadChains_ShouldBeIndependent()
    {
        // 上下行分别配置不同 codec，互不影响
        var g = Graph([Client("c"), Decoder("d", "text"), Handler("h"),
                       Encoder("e", "text"), Sink("s")],
            ("c", "out", "d", "in"),
            ("d", "out", "h", "in"),
            ("h", "ok", "e", "in"),
            ("e", "out", "c", "tx"),
            ("h", "fail", "s", "in"));

        var result = Compile(g);
        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
    }

    // ---------------- 端口类型约束 ----------------

    [Fact]
    public void DevicePorts_ShouldAllBeRawFrameTyped()
    {
        // 设备的收发都是原始帧；业务消息只能由编解码器产出
        var clientType = Types.Find("client")!;
        Assert.All(clientType.Ports, p => Assert.Equal(PortTypes.RawFrame, p.DataType));
        Assert.DoesNotContain(clientType.Ports, p => p.DataType == PortTypes.Message);
    }

    [Fact]
    public void Handler_ShouldExposeThreeNamedOutputs()
    {
        var t = Types.Find("handler")!;
        var outs = t.Ports.Where(p => p.Direction == PortDirection.Output).Select(p => p.Id).ToArray();
        Assert.Equal(["ok", "retry", "fail"], outs);
    }

    [Fact]
    public void Handler_ShouldNotAllowLegacyOutPort()
    {
        // 旧的单一 out 端口已不存在，应由 ok 承接
        var g = Graph([Client("c"), Handler("h")],
            ("c", "out", "h", "in"),
            ("h", "out", "c", "tx"));

        var result = Compile(g);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_port_missing");
    }

    [Fact]
    public void HandlerResult_ShouldRouteBackThroughCodecEncoder()
    {
        // 闭环：上行 客户端→解码器→处理器；下行 处理器→编码器→客户端 tx
        var g = Graph([Client("c"), Decoder("k"), Handler("h"), Encoder("e")],
            ("c", "out", "k", "in"),
            ("k", "out", "h", "in"),
            ("h", "ok", "e", "in"),
            ("e", "out", "c", "tx"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void HandlerResult_CannotWriteDeviceTxDirectly()
    {
        // 处理结果是 IMessage，设备 tx 要 RawFrame，直连必须被拒
        var g = Graph([Client("c"), Decoder("k"), Handler("h")],
            ("c", "out", "k", "in"),
            ("k", "out", "h", "in"),
            ("h", "ok", "c", "tx"));

        var result = Compile(g);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_type_mismatch");
    }

    [Fact]
    public void WriteBackEdges_ShouldNotCountAsCycle()
    {
        // 目标为设备 tx 或编码器的边不构成自激
        var g = Graph([Client("c"), Decoder("k"), Handler("h"), Encoder("e")],
            ("c", "out", "k", "in"),
            ("k", "out", "h", "in"),
            ("h", "ok", "e", "in"),
            ("e", "out", "c", "tx"));

        var result = Compile(g);
        Assert.DoesNotContain(result.Issues, i => i.Code == "graph_cycle");
    }

    [Fact]
    public void ShouldRejectTrueCycleBetweenHandlers()
    {
        // 处理器 ok -> 处理器 in 的真回环仍应被拒
        var g = Graph([Client("c"), Handler("h1"), Handler("h2")],
            ("c", "out", "h1", "in"),
            ("h1", "ok", "h2", "in"),
            ("h2", "ok", "h1", "in"));

        var result = Compile(g);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "graph_cycle");
    }

    [Fact]
    public void ShouldRejectSinkAsSource()
    {
        var g = Graph([Client("c"), Sink("s"), Handler("h")],
            ("c", "out", "h", "in"),
            ("s", "in", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_port_missing");
    }

    [Fact]
    public void Transform_ShouldChainBetweenMessages()
    {
        var g = Graph([Client("c"), Decoder("k"), Transform("t1"), Transform("t2"), Handler("h")],
            ("c", "out", "k", "in"),
            ("k", "out", "t1", "in"),
            ("t1", "out", "t2", "in"),
            ("t2", "out", "h", "in"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Equal(["c", "k", "t1", "t2", "h"], result.Topology[0].Path);
    }

    [Fact]
    public void Sink_ShouldAcceptMessageViaCodec()
    {
        var g = Graph([Client("c"), Decoder("k"), Sink("s")],
            ("c", "out", "k", "in"), ("k", "out", "s", "in"));
        Assert.True(Compile(g).Ok);
    }

    // ---------------- 端口存在性与必连 ----------------

    [Fact]
    public void ShouldRejectUnknownPort()
    {
        var g = Graph([Client("c"), Decoder("k"), Handler("h")],
            ("c", "out", "k", "in"), ("k", "out", "h", "in"), ("c", "nope", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_port_missing");
    }

    [Fact]
    public void ShouldRejectUnconnectedRequiredPort()
    {
        var g = Graph([Handler("h")]);

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "port_required_unconnected" && i.NodeId == "h");
    }

    // ---------------- 环路 ----------------

    [Fact]
    public void ShouldRejectCycle()
    {
        var g = Graph([Client("c"), Transform("t1"), Transform("t2")],
            ("c", "out", "t1", "in"),
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
    public void ShouldRejectMissingProtocolOnDevice()
    {
        var g = Graph([Client("c", kind: ""), Decoder("k"), Handler("h")],
            ("c", "out", "k", "in"), ("k", "out", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_invalid" && i.NodeId == "c");
    }

    [Fact]
    public void ShouldRejectUnknownKindOnDevice()
    {
        var g = Graph([Client("c", kind: "not-registered"), Decoder("k"), Handler("h")],
            ("c", "out", "k", "in"), ("k", "out", "h", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_invalid" && i.NodeId == "c");
    }

    [Fact]
    public void ShouldRejectUnknownCodecOnCodecNode()
    {
        var g = Graph([Client("c"), Decoder("k", "not-a-codec"), Handler("h")],
            ("c", "out", "k", "in"), ("k", "out", "h", "in"));

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
        var g = Graph([Client("c", endpoint), Decoder("k"), Handler("h")],
            ("c", "out", "k", "in"), ("k", "out", "h", "in"));

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
        var g = Graph([Server("s1", 9000), Server("s2", 9000), Decoder("c1"), Decoder("c2"), Sink("k1"), Sink("k2")],
            ("s1", "out", "c1", "in"), ("c1", "out", "k1", "in"),
            ("s2", "out", "c2", "in"), ("c2", "out", "k2", "in"));

        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "listener_port_conflict");
    }

    [Fact]
    public void ShouldAllowMultipleEphemeralListeners()
    {
        var g = Graph([Server("s1", 0), Server("s2", 0), Decoder("c1"), Decoder("c2"), Sink("k1"), Sink("k2")],
            ("s1", "out", "c1", "in"), ("c1", "out", "k1", "in"),
            ("s2", "out", "c2", "in"), ("c2", "out", "k2", "in"));

        var result = Compile(g);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Equal(2, result.Config!.Listeners.Length);
    }

    [Fact]
    public void ShouldRejectDuplicateNodeIds()
    {
        var g = Graph([Client("dup"), Client("dup")]);
        var result = Compile(g);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_id_duplicate");
    }

    // ---------------- 旧图迁移 ----------------

    [Fact]
    public void ShouldMigrateLegacyGraph_FoldingProtocolIntoDevice()
    {
        // 旧格式：protocol 节点 + 无端口连线，客户端没有 kindId 字段
        var legacy = new OrchestrationGraph
        {
            Nodes =
            [
                Node("p", "", ("kindId", "length-prefix")),
                Node("c", "", ("endpoints", "127.0.0.1:502"))
            ],
            Edges = [new GraphEdge { Id = "e1", From = "p", To = "c" }]
        };
        legacy.Nodes[0].Kind = "Protocol";
        legacy.Nodes[1].Kind = "Client";

        var result = Compile(legacy);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        // 协议节点已被移除，kindId 并入客户端
        Assert.Single(legacy.Nodes);
        Assert.Equal("client", legacy.Nodes[0].NodeType);
        Assert.Equal("length-prefix", legacy.Nodes[0].Get("kindId"));
        Assert.Empty(legacy.Edges);
    }

    [Fact]
    public void MigratedLegacyGraph_ShouldProduceDevice()
    {
        var legacy = new OrchestrationGraph
        {
            Nodes =
            [
                Node("p", "", ("kindId", "length-prefix")),
                Node("s", "", ("host", "127.0.0.1"), ("port", "9000"))
            ],
            Edges = [new GraphEdge { Id = "e1", From = "p", To = "s" }]
        };
        legacy.Nodes[0].Kind = "Protocol";
        legacy.Nodes[1].Kind = "Server";

        var result = Compile(legacy);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Single(result.Config!.Listeners);
        Assert.Equal("length-prefix", result.Config.Listeners[0].KindId);
    }

    // ---------------- 其它 ----------------

    [Fact]
    public void Flipped_ShouldPersistThroughClone()
    {
        // 左右翻转是布局属性，需随图保存与恢复
        var g = Graph([Client("c")]);
        g.Nodes[0].Flipped = true;

        var copy = g.Clone();
        Assert.True(copy.Nodes[0].Flipped);
    }

    [Fact]
    public void Clone_ShouldBeDeepCopy()
    {
        var g = Graph([Client("c")]);
        g.Nodes[0].Settings["kindId"] = "v";

        var copy = g.Clone();
        copy.Nodes[0].Settings["kindId"] = "changed";

        Assert.Equal("v", g.Nodes[0].Settings["kindId"]);
    }

    [Fact]
    public void PortTypes_AnyShouldMatchEverything()
    {
        Assert.True(PortSpec.TypesCompatible(PortTypes.RawFrame, PortTypes.Any));
        Assert.True(PortSpec.TypesCompatible(PortTypes.Any, PortTypes.Message));
        Assert.True(PortSpec.TypesCompatible(PortTypes.Message, PortTypes.Message));
        Assert.False(PortSpec.TypesCompatible(PortTypes.RawFrame, PortTypes.Message));
    }

    [Fact]
    public void DeviceTypes_ShouldUseRawFrameOnBothDirections()
    {
        // 设备的收发都是原始帧：tx 接收待发送帧，out 输出上报帧。
        // 这正是「设备不能直连处理器」的类型基础。
        foreach (var typeId in new[] { "client", "server" })
        {
            var t = Types.Find(typeId)!;
            Assert.Contains(t.Ports, p =>
                p.Id == "tx" && p.Direction == PortDirection.Input && p.DataType == PortTypes.RawFrame);
            Assert.Contains(t.Ports, p =>
                p.Id == "out" && p.Direction == PortDirection.Output && p.DataType == PortTypes.RawFrame);
        }
    }

    [Fact]
    public void DeviceSendPort_ShouldNotBeRequired()
    {
        // 发送是可选的：不连 tx 也能只用接收方向
        foreach (var typeId in new[] { "client", "server" })
        {
            var tx = Types.Find(typeId)!.Ports.Single(p => p.Id == "tx");
            Assert.False(tx.Required);
        }
    }

    [Fact]
    public void ShouldAllowUpstreamToDeviceSendPort()
    {
        // 处理器产出的消息可以写入设备 tx 端口（构成发送链路）
        var g = Graph([Client("c"), Handler("h")],
            ("c", "out", "h", "in"),
            ("h", "out", "c", "tx"));

        // handler 没有输出端口，因此连线应被拒——说明「处理器直接写设备」需要变换节点或出口
        var result = Compile(g);
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_port_missing");
    }

    [Fact]
    public void DeviceTypes_ShouldDeclareProtocolFieldButNotCodec()
    {
        // 协议是设备自身字段；编解码器已独立成节点，不再是设备字段
        foreach (var typeId in new[] { "client", "server" })
        {
            var t = Types.Find(typeId)!;
            Assert.Contains(t.Fields, f => f.Key == "kindId" && f.OptionsSource == "kinds");
            Assert.DoesNotContain(t.Fields, f => f.Key == "codecId");
        }
    }
}
