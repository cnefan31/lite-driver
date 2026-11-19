# LiteDriver.Orchestration

LiteDriver 的可视化**功能编排**库。用浏览器拖拽组件、连线、保存并应用，在线增删改客户端 / 服务端 / 协议节点，**后端无需重启**。

基于 [NanoServer](https://www.nuget.org/packages/NanoServer) 承载页面与 REST API，依赖 `LiteDriver` 与 `LiteDriver.Hosting`。

## 它解决什么

原先改一个端点或加一个监听，要改配置文件再重启进程。现在可以在页面上直接编排，点「保存并应用」即热生效——连接层的增删改由 `LiteDriver.ApplyAsync` 的事务语义保证，失败自动保留上一次成功的配置。

## 快速开始

```csharp
using LiteDriver.Orchestration;
using LiteDriver.Orchestration.Server;
using LiteDriver.Protocol;

// 1) 注册可用的协议种类（工厂必须静态可分析）
var protocols = new List<ProtocolRegistration>
{
    new("length-prefix", "长度前缀", "2 字节大端长度 + 载荷", [],
        static ctx => new MyLengthKind(ctx), null),

    new("stx-etx", "起始/结束标记", "02..03 定界", 
        [ new ProtocolField("start", "起始标记", "02", "02", "十六进制"),
          new ProtocolField("end", "结束标记", "03", "03", "十六进制") ],
        static ctx => new DelimitedKind(ctx),
        DelimitedKind.ValidateSettings),
};

// 2) 启动编排引擎与服务
await using var engine = new OrchestrationEngine(protocols);
await using var server = new OrchestrationServer(engine,
    new OrchestrationServerOptions { Host = "127.0.0.1", Port = 8099 });

server.Start();
Console.WriteLine($"打开 http://127.0.0.1:{server.Port}/");
```

## 页面上能做什么

| 操作 | 说明 |
| --- | --- |
| 拖入组件 | 从左侧拖入「协议 / 客户端 / 服务端」到画布 |
| 连线 | 从协议节点的右侧圆点拖到客户端/服务端节点，建立 `协议 → 实体` 绑定 |
| 编辑属性 | 选中节点后在右栏改端口、端点、重试退避、心跳、协议参数 |
| 校验 | 先校验再应用；失败项会在对应节点上标红 |
| 保存并应用 | 热应用，返回新增/更新/删除/未变 计数 |
| 删除节点 / 连线 | 选中后删除，再次应用即生效 |

## 节点与端口（连接约束）

编排图由**节点**与**端口连线**组成。连接是否合法，完全由**端口数据类型**决定，不在编译器里硬编码节点种类判断。

| 节点类型 | 输入端口 | 输出端口 |
| --- | --- | --- |
| 协议 `protocol` | 配置 `Any` | 帧格式 `IFrame` |
| 客户端 `client` | 协议 `IFrame`（必连） | 原始帧 `IFrame` |
| 服务端 `server` | 协议 `IFrame`（必连） | 入站帧 `IFrame` |
| 编解码器 `codec` | 原始帧 `IFrame`（必连） | 业务消息 `IMessage` |
| 变换 `transform` | 消息 `IMessage`（必连） | 消息 `IMessage` |
| 处理器 `handler` | 消息 `IMessage`（必连） | — |
| 出口 `sink` | 消息 `Any`（必连） | — |

### 为什么客户端不能直连处理器

客户端的输出是 `IFrame`，处理器的输入是 `IMessage`，类型不匹配，连线会被拒：

```
客户端 [IFrame] ──✗──► 处理器 [IMessage]
客户端 [IFrame] ──✓──► 编解码器 [IFrame→IMessage] ──✓──► 处理器 [IMessage]
```

这正是「用接口约束连接性」的实现方式：**端口类型是契约，连线时校验**。

### 合法链路示例

```
协议 ──► 客户端 ──► 编解码器 ──► 变换 ──► 处理器
协议 ──► 服务端 ──► 编解码器 ──► 出口
```

## 扩展自己的节点类型

实现 `INodeType` 并注册即可，编译器与前端自动适配：

```csharp
public sealed class MyFilterType : INodeType
{
    public string TypeId => "filter";
    public string DisplayName => "过滤器";
    public string Description => "按条件丢弃消息";
    public bool IsSource => false;

    public IReadOnlyList<PortSpec> Ports { get; } =
    [
        new PortSpec("in", "消息", PortDirection.Input, PortTypes.Message, required: true),
        new PortSpec("out", "消息", PortDirection.Output, PortTypes.Message)
    ];

    public IReadOnlyList<NodeField> Fields { get; } =
    [
        new NodeField("expression", "条件", NodeFieldKind.Text, "", "丢弃表达式")
    ];

    public string? Validate(GraphNode node, INodeTypeCatalogView catalog) => null;
}

var nodeTypes = GraphCompiler.DefaultNodeTypes().Register(new MyFilterType());
var engine = new OrchestrationEngine(protocols, nodeTypes: nodeTypes);
```

## 校验规则

编译期一次性报出全部问题，每条都能定位到节点或连线：

| 代码 | 含义 |
| --- | --- |
| `edge_type_mismatch` | 端口类型不匹配（如客户端直连处理器） |
| `edge_port_missing` | 连线引用了不存在的端口 |
| `port_required_unconnected` | 必连输入端口没有连线 |
| `graph_cycle` | 链路存在环路 |
| `node_type_unknown` | 节点类型未注册 |
| `node_invalid` | 节点自身配置非法（缺 kindId、端点格式错等） |
| `listener_port_conflict` | 两个监听节点抢占同一地址 |

## 旧图兼容

0.2.0 及更早的图只有 `kind`（Protocol/Client/Server）且连线不带端口。
读取时 `GraphMigration` 会自动补 `nodeType` 与端口，无需手工重建。

> 注意：旧图的「协议 → 客户端」语义是「客户端使用该协议」，迁移后仍成立；
> 但旧图没有编解码器节点，因此升级后需要补一个编解码器节点才能接处理器。

## REST API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | 健康检查 |
| GET | `/api/protocols` | 可用协议与参数字段 |
| GET | `/api/nodetypes` | 节点类型、端口声明与字段（驱动托盘与属性面板） |
| GET | `/api/registry` | 注册表候选（kinds/codecs/transforms/handlers/sinks） |
| POST | `/api/connectable` | 判断两个端口能否相连 |
| GET | `/api/graph` | 当前已保存的编排图 |
| GET | `/api/state` | 运行时状态：各节点状态、就绪数、会话数、事件流 |
| POST | `/api/validate` | 仅校验，不应用；返回可定位到节点的 `issues` |
| POST | `/api/apply` | 编译并热应用 |

## 编排模型

图是唯一事实来源：`Protocol` / `Client` / `Server` 三类节点 + 边。编译器把它翻译成 `DriverConfig`：

- 一个 `Client` 节点 → 一个 `DeviceDescriptor`（端点、退避、心跳）
- 一个 `Server` 节点 → 一个 `ListenerDescriptor`（监听地址、端口、会话上限）
- 边 `Protocol → Client/Server` 决定该节点用哪套帧格式；**一个节点只能绑定一个协议**
- 协议节点的 `Settings` 键值原样进入 `kindSettings`

编译是纯函数，不碰网络，因此校验失败能一次性全部报出并定位到节点。

## 端口稳定性

`Port = 0` 时由系统分配端口。应用成功后库会把**实际端口回写进图**，否则每次应用都会被判为「端口变化」而重建监听，端口持续漂移。

注意：第一次应用（`0 → 实际端口`）必然计为一次「更新」，从第二次起才稳定为「未变」。

## 关于 AOT

本包只面向 `net10.0`（NanoServer 仅提供 net10.0 资产）。JSON 全部走源生成，`IsAotCompatible` 已开启，`tests/LiteDriver.Orchestration.AotSmoke` 在原生二进制里验证编译、往返与热应用。
