# LiteDriver

面向工业 SOCKET 设备的轻量 TCP 连接管理库。核心零 NuGet 依赖，支持 .NET 通用主机与 NativeAOT。

[![NuGet](https://img.shields.io/nuget/v/LiteDriver.svg)](https://www.nuget.org/packages/LiteDriver)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)

## 它解决什么问题

工业现场的 TCP 长连接，难点通常不是「怎样 `ConnectAsync`」，而是这些：

| 现场问题 | LiteDriver 的处理 |
| --- | --- |
| 设备断电、拔网线、进程僵死，`Socket.Connected` 仍为 true | 不用 `Connected` 判活；`Ready` 必须晚于 TCP 建连、应用握手、身份确认 |
| 断线后旧回调、旧半包、旧响应串进新连接 | 四层标识 `Generation / Epoch / ConnectionId / RequestId` 逐层隔离 |
| 设备重启后同一请求收到上一次的响应 | 每个物理连接独立 decoder、缓冲区与在途表，跨 epoch 一律丢弃 |
| 写命令超时后不知道设备到底执行了没 | 只返回 `SentOutcomeUnknown`，**绝不自动重放** |
| 设备帧没有 requestId | 强制单在途串行，不做不可靠的并发关联 |
| 配置热更新改错一个字段导致整片设备掉线 | 单写者事务：校验 → 冻结 → 预绑定 → 原子提交 → 停旧代，失败保持旧快照 |
| 裁剪后 AOT 发布成功但运行时缺类型 | 启动期冻结的显式工厂注册表，禁止反射与运行时代码生成 |

## 安装

```bash
dotnet add package LiteDriver
dotnet add package LiteDriver.Hosting   # 需要通用主机集成时
```

支持 `net8.0` 与 `net10.0`。

## 最小示例

```csharp
using LiteDriver;
using LiteDriver.Configuration;
using LiteDriver.Protocol;

// 1. 注册设备种类：kindId 是配置里的稳定字符串键，不是 CLR 类型名
var driver = new LiteDriverBuilder()
    .RegisterKind("my-protocol", static ctx => new MyDeviceKind(ctx))
    .Build();

// 2. 启动并应用初始配置（不等待设备连上）
await driver.StartAsync(new DriverConfig
{
    Devices =
    [
        new DeviceDescriptor
        {
            DeviceId = "press-01",
            KindId = "my-protocol",
            Endpoints = [ new EndpointDescriptor { Host = "192.168.1.10", Port = 502 } ]
        }
    ]
});

// 3. 等链路可用后发一条命令
await driver.WaitUntilReadyAsync("press-01", TimeSpan.FromSeconds(10));
using var result = await driver.GetDevice("press-01")
    .ExchangeAsync(requestBytes, TimeSpan.FromSeconds(5));

switch (result.Disposition)
{
    case CommandDisposition.Executed:
        // 设备已接受
        break;
    case CommandDisposition.SentOutcomeUnknown:
        // 发出去了但结果未知：不要自动重放，先读回状态对账
        break;
}
```

## 通用主机集成

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLiteDriver(
    kinds => kinds.RegisterKind("my-protocol", static ctx => new MyDeviceKind(ctx)),
    host  => host.InitialConfig = MyConfig.Load());

using var host = builder.Build();
await host.RunAsync();
```

`LiteDriverHostedService` 只转发启动与停止，不复制状态机、不扫描程序集。启动期配置非法会让主机启动失败，而不是带病运行。

## 实现一个设备种类

只需实现两个接口。`CreateProtocol` 在**每次物理连接**时被调用，必须返回全新实例。

```csharp
public sealed class MyDeviceKind : IDeviceKind
{
    private readonly int _maxFrameSize;
    public MyDeviceKind(in ProtocolContext ctx) => _maxFrameSize = ctx.Limits.MaxFrameSize;

    public ISessionProtocol CreateProtocol(in ProtocolContext ctx) => new MyProtocol(_maxFrameSize);
}

public sealed class MyProtocol : ISessionProtocol
{
    public MyProtocol(int maxFrameSize) =>
        Decoder = new LengthPrefixFrameDecoder(2, bigEndian: true, lengthIncludesHeader: false, maxFrameSize);

    public IFrameDecoder Decoder { get; }                                   // 每条连接新建
    public IFrameEncoder Encoder { get; } = new LengthPrefixFrameEncoder(2, true, false);
    public ISessionInitializer? Initializer => null;                        // 需要握手时提供
    public IHeartbeatPolicy? Heartbeat => null;                             // 需要应用心跳时提供
    public IResponsePolicy Response { get; } = new NextFrameResponsePolicy();
}
```

内置三种分包器：`FixedLengthFrameDecoder`、`LengthPrefixFrameDecoder`、`TerminatorFrameDecoder`，另有 `PassthroughFrameDecoder`。

## 命令结果语义

| Disposition | 含义 | 可否自动重试 |
| --- | --- | --- |
| `NotSent` | 确认整帧未写出（未 Ready、队列满、入队前取消） | 可以 |
| `SentOutcomeUnknown` | 已交给内核但无响应，或超时/断链/部分写出 | **不可以** |
| `Rejected` | 收到匹配的应用层否定应答 | 不可以 |
| `Executed` | 收到匹配的应用层接受应答 | 按业务需要读回确认 |
| `Canceled` | 调用方取消且确认未写出 | 可以 |
| `BackpressureRejected` | 写队列或在途表已满 | 可以（退避后） |

## 关键不变量

- 一个 socket 任意时刻只有一个写循环，完整帧写完才释放写锁
- 一个 socket 任意时刻只有一个读泵，decoder 不跨连接复用
- 所有回调携带 epoch，旧 epoch 一律丢弃
- 写队列、在途表、读缓冲、帧长、会话数全部有上限
- 主动 Stop 产生的关闭不触发重连
- 心跳开启但协议未提供心跳实现时，显式报 `heartbeat-unsupported-by-kind`，不静默当作已启用

## 文档

- [架构设计与审阅稿](./docs/lite-driver-architecture-review.html)（单文件 HTML，断网可开）
- [API 说明](./docs/API.md)
- [配置参数与调优](./docs/CONFIGURATION.md)
- [发布流程](./docs/PUBLISHING.md)

## 示例

- [samples/LiteDriver.SampleHost](./samples/LiteDriver.SampleHost) —— 通用主机 + 本地模拟设备，演示建连、交换、断链重连、运行期增删改与优雅停机

## 目标与非目标

**做**：TCP 明文长连接管理、断线重连、运行时增改删、可插拔帧编解码、显式命令结果、通用主机集成、NativeAOT。

**不做**：具体 PLC 协议（Modbus/S7/MC/FINS/SECS）、TLS/串口/UDP/WebSocket/RPC、运行时装插件、磁盘持久化命令队列、自动重放写命令。

## 许可

MIT
