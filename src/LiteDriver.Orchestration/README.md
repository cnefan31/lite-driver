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

**每个节点都是独立的**：各自带输入/输出，用来承接上一个节点的输出、向下一个节点输出。
**协议与编解码器是设备节点自身的属性**，不从外部节点拉线绑定——每个客户端/服务端独立维护自己的协议，互不影响。

| 节点类型 | 输入 | 自身属性 | 输出 |
| --- | --- | --- | --- |
| 客户端 `client` | 发送 `tx`（可选） | **协议 kindId**、**编解码器 codecId**、端点、退避、心跳 | 上报 `out` |
| 服务端 `server` | 发送 `tx`（可选） | **协议 kindId**、**编解码器 codecId**、监听地址/端口、最大会话 | 上报 `out` |
| 变换 `transform` | 消息 `IMessage` | transformId、参数 | 消息 `IMessage` |
| 处理器 `handler` | 消息 `IMessage` | handlerId、模式 | **`ok` / `retry` / `fail` 三个命名输出** |
| 出口 `sink` | 消息 `Any` | sinkId、目标 | — |

### 连线只表达数据流

```
客户端 [自带协议+编解码器] ──IMessage──► 处理器
                          └─IMessage──► 变换 ──IMessage──► 处理器
服务端 [自带协议+编解码器] ──IMessage──► 出口
```

连线不代表"绑定"，只代表"数据从这里流到那里"。因此：

- 两个客户端可以各自用不同协议，互不干扰
- 不需要协议节点，也不存在"协议绑定"关系
- 端口类型仍然约束数据流（`IMessage` 不能接只收 `IFrame` 的端口），但当前内置节点里设备已统一输出 `IMessage`

### 设备节点的收发

设备是链路起点，但 TCP 是双向的，所以设备有两个端口：

- **`out`（上报）**：设备读到的数据经自身 codec 解码后，作为业务消息向下游输出
- **`tx`（发送，可选）**：上游写入的消息经**同一 codec** 编码、由协议组帧，写入 socket

```
                       ┌── out ──► 处理器 / 出口
客户端 [自带协议+codec] ─┤
                       └── tx  ◄── 上游节点的消息（写入设备）
```

`tx` 是可选的：只做数据采集时不接它。接入后即可在编排里表达"下发命令"。

### 处理器的结果路由

处理器不是死胡同：它有三个命名输出，按处理结果分流。

```
                      ┌── ok    ──► 客户端 tx（结果回写设备）
设备上报 ──► 处理器 ──┼── retry ──► 重试队列 / 另一设备
                      └── fail  ──► 告警出口
```

`mode` 字段决定结果走哪个输出：`log` 与 `echo` 走 `ok`，`fail` 走 `fail`。

**完整闭环**（已实测）：`客户端 out → 处理器 in → 处理器 ok → 客户端 tx → 真实写入 socket`。

注意：写入 `tx` 的边**不计入环路检测**——它是独立单向通道，与 `out` 上的上报不构成运行时自激。

### 页面上的调试直发

设备节点属性面板里有「调试直发」：输入一段文本，直接用该节点自身的 codec 编码后写入设备，**不经过编排链路**。用于调试阶段快速验证协议与链路是否通。

对应 API：

```http
POST /api/send
{ "nodeId": "cli", "text": "HELLO" }
```

返回 `status`：`Sent` / `NotReady` / `NoCodec` / `EncodeFailed` / `InvalidNode` / `WriteFailed`。

> `Sent` 只表示字节已交给内核，**不代表设备已执行**。

处理器的模拟路由：

```http
POST /api/process
{ "nodeId": "h", "text": "输入消息" }
```

处理器自身的 `EncodedFailed` 会被如实返回（例如下游 codec 无法编码该消息），不会静默吞掉。

### 文本直发的前提

引擎无法凭空构造任意 codec 的消息类型（如 `TextMessage`），因此需要文本互操作的 codec 实现 `ITextPayloadCodec`：

```csharp
public sealed class TextCodec : IPayloadCodec, ITextPayloadCodec
{
    public PayloadMessage FromText(string text) => PayloadMessage.From(new TextMessage(text), "text");
    // ...
}
```

未实现该接口时，`/api/send` 会返回 `EncodeFailed` 并说明原因，不会静默失败。

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
| `edge_type_mismatch` | 端口类型不匹配（如把 IFrame 接到 IMessage） |
| `edge_port_missing` | 连线引用了不存在的端口 |
| `port_required_unconnected` | 必连输入端口没有连线 |
| `graph_cycle` | 链路存在环路 |
| `node_type_unknown` | 节点类型未注册 |
| `node_invalid` | 节点自身配置非法（设备缺协议、codecId 未注册、端点格式错等） |
| `listener_port_conflict` | 两个监听节点抢占同一地址 |

## 旧图兼容

旧图（含 `kind` 或独立 protocol/codec 节点）读取时会自动迁移：

- `kind`（Protocol/Client/Server）→ `nodeType`
- 连线无端口 → 补默认端口
- **独立 protocol 节点的 `kindId` 并入下游设备节点，然后移除该节点**
- 独立 codec 节点同理并入下游设备

迁移后设备成为链路起点，协议与编解码器都在设备自身属性上。

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
