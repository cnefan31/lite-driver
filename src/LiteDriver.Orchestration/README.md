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

## REST API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | 健康检查 |
| GET | `/api/protocols` | 可用协议与参数字段（驱动页面表单） |
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
