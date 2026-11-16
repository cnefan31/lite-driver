using LiteDriver.Orchestration;
using Xunit;

namespace LiteDriver.Tests;

/// <summary>编排图编译器的校验与映射行为。</summary>
public sealed class OrchestrationCompilerTests
{
    private static readonly string[] Kinds = ["length-prefix", "line-terminator", "stx-etx"];

    private static GraphNode Proto(string id, string kind = "length-prefix") =>
        new() { Id = id, Kind = NodeKind.Protocol, Label = id, KindId = kind };

    private static GraphNode Client(string id, params string[] endpoints) => new()
    {
        Id = id,
        Kind = NodeKind.Client,
        Label = id,
        Endpoints = [.. endpoints]
    };

    private static GraphNode Server(string id, int port = 0) => new()
    {
        Id = id,
        Kind = NodeKind.Server,
        Label = id,
        Host = "127.0.0.1",
        Port = port
    };

    private static OrchestrationGraph Graph(List<GraphNode> nodes, params (string from, string to)[] edges) => new()
    {
        Nodes = nodes,
        Edges = [.. edges.Select((e, i) => new GraphEdge { Id = $"e{i}", From = e.from, To = e.to })]
    };

    [Fact]
    public void ShouldCompileValidGraph()
    {
        var g = Graph(
            [Proto("p"), Client("c", "127.0.0.1:502"), Server("s", 9000)],
            ("p", "c"), ("p", "s"));

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.NotNull(result.Config);
        Assert.Single(result.Config!.Devices);
        Assert.Single(result.Config.Listeners);
        Assert.Equal("length-prefix", result.Config.Devices[0].KindId);
        Assert.Equal(9000, result.Config.Listeners[0].Port);
    }

    [Fact]
    public void ShouldRejectDuplicateNodeIds()
    {
        var g = Graph([Proto("dup"), Client("dup", "127.0.0.1:1")]);
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_id_duplicate");
    }

    [Fact]
    public void ShouldRejectClientWithoutProtocol()
    {
        var g = Graph([Proto("p"), Client("c", "127.0.0.1:1")]);
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        var issue = Assert.Single(result.Issues, i => i.Code == "client_no_protocol");
        Assert.Equal("c", issue.NodeId);
    }

    [Fact]
    public void ShouldRejectServerWithoutProtocol()
    {
        var g = Graph([Proto("p"), Server("s")]);
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "server_no_protocol" && i.NodeId == "s");
    }

    [Fact]
    public void ShouldRejectUnknownProtocolKind()
    {
        var g = Graph([Proto("p", "not-registered"), Client("c", "127.0.0.1:1")], ("p", "c"));
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "protocol_kind_unknown");
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
        var g = Graph([Proto("p"), Client("c", endpoint)], ("p", "c"));
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "client_endpoint_invalid");
    }

    [Fact]
    public void ShouldAcceptIpv6Endpoint()
    {
        Assert.True(GraphCompiler.TryParseEndpoint("[::1]:502", out var host, out var port, out _));
        Assert.Equal("::1", host);
        Assert.Equal(502, port);
    }

    [Fact]
    public void ShouldRejectClientWithoutEndpoint()
    {
        var g = Graph([Proto("p"), Client("c")], ("p", "c"));
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "client_no_endpoint");
    }

    [Fact]
    public void ShouldRejectEdgeFromNonProtocol()
    {
        var g = Graph([Proto("p"), Client("c", "127.0.0.1:1"), Server("s")], ("c", "s"));
        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "edge_from_not_protocol");
    }

    [Fact]
    public void ShouldRejectNodeBoundToTwoProtocols()
    {
        var g = Graph(
            [Proto("p1"), Proto("p2", "stx-etx"), Client("c", "127.0.0.1:1")],
            ("p1", "c"), ("p2", "c"));

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "node_multiple_protocols");
    }

    [Fact]
    public void ShouldRejectConflictingListenerPorts()
    {
        var g = Graph(
            [Proto("p"), Server("s1", 9000), Server("s2", 9000)],
            ("p", "s1"), ("p", "s2"));

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "listener_port_conflict");
    }

    [Fact]
    public void ShouldAllowMultipleEphemeralListeners()
    {
        var g = Graph(
            [Proto("p"), Server("s1", 0), Server("s2", 0)],
            ("p", "s1"), ("p", "s2"));

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.True(result.Ok, string.Join("；", result.Issues.Select(i => i.Message)));
        Assert.Equal(2, result.Config!.Listeners.Length);
    }

    [Fact]
    public void ShouldCarryProtocolSettingsIntoKindSettings()
    {
        var proto = Proto("p", "stx-etx");
        proto.Settings = new Dictionary<string, string> { ["start"] = "02", ["end"] = "03" };
        var g = Graph([proto, Client("c", "127.0.0.1:1")], ("p", "c"));

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.True(result.Ok);
        var settings = result.Config!.Devices[0].KindSettings;
        Assert.NotNull(settings);
        Assert.Equal("02", settings!.Value.GetProperty("start").GetString());
    }

    [Fact]
    public void ShouldUseNodeIdWhenDeviceIdBlank()
    {
        var g = Graph([Proto("p"), Client("my-client", "127.0.0.1:1")], ("p", "c"));
        g.Nodes[1].Id = "my-client";
        g.Edges[0].To = "my-client";

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.True(result.Ok);
        Assert.Equal("my-client", result.Config!.Devices[0].DeviceId);
    }

    [Fact]
    public void ShouldRejectDuplicateDeviceIds()
    {
        var c1 = Client("c1", "127.0.0.1:1");
        c1.DeviceId = "same";
        var c2 = Client("c2", "127.0.0.1:2");
        c2.DeviceId = "same";
        var g = Graph([Proto("p"), c1, c2], ("p", "c1"), ("p", "c2"));

        var result = GraphCompiler.Compile(g, Kinds);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "device_id_duplicate");
    }

    [Fact]
    public void Clone_ShouldBeDeepCopy()
    {
        var g = Graph([Proto("p"), Client("c", "127.0.0.1:1")], ("p", "c"));
        g.Nodes[0].Settings = new Dictionary<string, string> { ["k"] = "v" };

        var copy = g.Clone();
        copy.Nodes[0].Settings!["k"] = "changed";
        copy.Nodes[1].Endpoints!.Add("127.0.0.1:9999");

        Assert.Equal("v", g.Nodes[0].Settings!["k"]);
        Assert.Single(g.Nodes[1].Endpoints!);
    }
}
