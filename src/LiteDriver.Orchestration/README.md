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

**端口数据类型是强契约**：前一个节点的输出类型必须与后一个节点的输入类型一致，否则连线被拒。连线同时有**方向**（输出 → 输入，界面上带箭头）。

| 节点类型 | 输入 | 输出 |
| --- | --- | --- |
| 客户端 `client` | `tx` 发送帧（RawFrame） | `out` 原始帧（RawFrame） |
| 服务端 `server` | `tx` 发送帧（RawFrame） | `out` 原始帧（RawFrame） |
| 解码器 `decoder` | `in` 原始帧 | `out` 业务消息 |
| 编码器 `encoder` | `in` 业务消息 | `out` 原始帧 |
| 变换 `transform` | `in` 业务消息 | `out` 业务消息 |
| 处理器 `handler` | `in` 业务消息 | `ok` / `retry` / `fail` 业务消息 |
| 出口 `sink` | `in` 任意 | — |

### 类型约束拦住什么

设备产出 `RawFrame`，处理器只收 `IMessage`，因此：

```
客户端 [RawFrame] ──✗──► 处理器 [IMessage]      类型不匹配，被拒
客户端 [RawFrame] ──✓──► 解码器 ──✓──► 处理器
```

同理，处理结果是 `IMessage`，设备 `tx` 要 `RawFrame`，**必须经编解码器的编码方向**才能写回：

```
处理器 ok ──✗──► 客户端 tx
处理器 ok ──✓──► 编码器 ──✓──► 客户端 tx
```

### 解码器与编码器分开

编解码已拆成两个独立节点，**上下行各自独立配置、互不耦合**：

| 节点 | 方向 | 输入 → 输出 | 配置 |
| --- | --- | --- | --- |
| `decoder` 解码器 | 上行 | `RawFrame` → `IMessage` | 自己的 `codecId` |
| `encoder` 编码器 | 下行 | `IMessage` → `RawFrame` | 自己的 `codecId` |

好处：

- 上行换解析方式不影响下行序列化
- 两端可以使用不同编解码策略（如不同协议版本）
- 只做采集时不必接编码器，只做下发时不必接解码器

```
上行：设备 out ─RawFrame─► 解码器 ─IMessage─► 处理器
下行：处理器 ok ─IMessage─► 编码器 ─RawFrame─► 设备 tx
```

两条链路在图上完全分开，不共享节点。

### 端口标识与翻转

每个端口显示三样信息，便于识别：

| 元素 | 含义 |
| --- | --- |
| 圆点 | 连接手柄，可拖出/接入 |
| 方向符号 `▶` / `◀` | 数据流向 |
| 类型徽标 | `帧`（RawFrame）/ `消息`（IMessage）/ `任意`（Any） |

节点可**左右翻转**：选中后在属性面板点「左右翻转」，输入端口移到右侧、输出端口移到左侧。用于把链路画成从右向左，避免连线大跨度回折。

翻转只影响布局，不改变语义——箭头仍指向数据流向，端口类型约束照旧。

### 无限画布与视图

画布是无限大的虚拟平面，支持自由缩放与拖动：

| 操作 | 效果 |
| --- | --- |
| 滚轮 | 以光标为锚点缩放（25% ~ 250%） |
| 空白处拖动 | 平移画布 |
| 底部 `−` / `+` | 以画布中心缩放 |
| 底部「适应」 | 自动缩放并居中到全部节点 |
| 底部「100%」 | 重置视图 |

缩放只影响显示，节点坐标始终是 world 坐标，连线端点会随缩放正确跟随。

### 端口标识

每个端口都带明确的 `输入` / `输出` 徽标（蓝色 / 绿色），配合连接圆点与类型徽标，一眼可辨方向与数据类型。

### 布局占位节点

`placeholder` 是没有端口、不参与编译的纯布局节点：

- 用来撑开走线空间、绕开拥挤区域
- 让某个端口的走线从指定位置经过
- 编译时被忽略，不会产生校验问题，也不需要连线

### 节点配色

属性面板可为任意节点设置颜色（6 个预设 + 自定义取色器），仅用于视觉归类，不影响语义与校验。颜色随图保存。

### 连线样式

顶栏可切换四种走线风格，均为业界编辑器（React Flow）同款：

| 样式 | 形态 |
| --- | --- |
| 曲线 | 单段三次贝塞尔（默认） |
| 圆角折线 | 正交 + 圆角转角 |
| 直角折线 | 正交直角 |
| 直线 | 近乎直线 |

### 连线方向

- 只能从**输出端口**拖向**输入端口**
- 从输入端口起拖会明确提示「这是输入端口，不能从它起拖」
- 连线带箭头，标明数据流向
- 连线可点击或右键删除

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
| `node_invalid` | 节点自身配置非法（设备缺协议、编解码器未选或未注册、端点格式错等） |
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
