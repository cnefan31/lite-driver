# API 说明

按使用顺序组织。所有类型在 `LiteDriver` 命名空间，配置类型在 `LiteDriver.Configuration`，协议接口在 `LiteDriver.Protocol`，观测在 `LiteDriver.Observability`。

---

## 一、构建

### LiteDriverBuilder

| 方法 | 说明 |
| --- | --- |
| `RegisterKind(string kindId, Func<ProtocolContext, IDeviceKind> factory)` | 注册设备种类。`kindId` 是配置键，不是 CLR 类型名。工厂须静态可分析 |
| `RegisterKind(..., KindSettingsValidator validator)` | 同上，附带该种类私有参数的校验器，把错误提前到配置校验阶段 |
| `UseLimits(DriverLimits)` | 设置资源上限 |
| `UseTransportFactory(ITransportFactory)` | 替换拨号实现（测试注入用） |
| `UseEndpointResolver(IEndpointResolver)` | 替换 DNS 解析 |
| `UseChaosPolicy(IChaosPolicy)` | **仅测试**：注入连接失败、accept 即断等故障 |
| `UseTimeProvider(TimeProvider)` | 注入时间源，测试可用 `FakeTimeProvider` 快进 |
| `UseObserver(IDriverObserver)` | 注入观测者 |
| `Build()` | 冻结注册表并返回驱动。之后不可再注册 |

注册表冻结是 AOT 的前提：所有 kind 实现必须在编译产物中静态可达，`Build()` 之后不再接受新键。

---

## 二、生命周期

### LiteDriver

> 命名空间 `LiteDriver` 与类 `LiteDriver` 同名。顶层语句直接写 `GetRequiredService<LiteDriver>()` 会报 `CS0118`，请用 `using Driver = LiteDriver.LiteDriver;` 别名或 `global::LiteDriver.LiteDriver` 限定。

| 成员 | 说明 |
| --- | --- |
| `State` | `Created / Starting / Running / Stopping / Stopped / Disposed` |
| `Revision` | 已提交配置版本号，从 0 起递增 |
| `StartAsync(DriverConfig, ct)` | 启动并应用初始配置。**不等待设备连上**，设备未上电也及时返回 |
| `ApplyAsync(DriverConfig, ct)` | 以事务方式替换完整配置 |
| `StopAsync(ct)` | 幂等停机，等待子任务退出 |
| `DisposeAsync()` | 停机并释放 |

`StartAsync` 与 `ApplyAsync` 返回 `ApplyResult`：

```csharp
public sealed record ApplyResult(
    long Revision, int Added, int Updated, int Removed, int Unchanged,
    IReadOnlyList<ApplyIssue> Issues);
```

`Issues` 为空即成功。失败时配置保持上一次成功的快照。

**为什么 `StartAsync` 不等待连接**：工业设备常常在系统启动时还没上电。若让启动阻塞到连上，整个宿主会被一个离线设备拖死。需要等链路时显式调用 `WaitUntilReadyAsync`。

---

## 三、设备操作

| 成员 | 说明 |
| --- | --- |
| `AddDeviceAsync(DeviceDescriptor, ct)` | 新增设备，已存在返回 `AlreadyExists` |
| `UpdateDeviceAsync(string, DeviceDescriptor, ct)` | 更新设备，不存在返回 `NotFound` |
| `RemoveDeviceAsync(string, ct)` | 删除设备，返回前完成取消、关闭与资源释放 |
| `AddListenerAsync(ListenerDescriptor, ct)` | 新增监听端点 |
| `RemoveListenerAsync(string, ct)` | 删除监听端点，返回前已停止接受新连接 |
| `GetDevice(string)` | 取设备句柄，不存在抛 `KeyNotFoundException` |
| `TryGetDevice(string, out DeviceHandle)` | 试取设备句柄 |
| `WaitUntilReadyAsync(string, TimeSpan, ct)` | 等待设备进入 `Ready`，超时抛 `TimeoutException` |
| `GetListenerPorts()` | 各监听端点实际绑定端口（`Port = 0` 时由系统分配） |
| `GetInboundSessions(string)` | 取某监听端点的入站会话句柄 |
| `GetListenerSessionCount(string)` | 当前入站会话数 |

所有增删改都是 `ApplyAsync` 的便捷包装，走同一套校验与事务。

---

## 四、DeviceHandle

| 成员 | 说明 |
| --- | --- |
| `IsValid` | 句柄是否仍指向存活设备 |
| `DeviceId` / `State` / `Generation` | 标识与状态 |
| `CurrentConnection` / `Epoch` | 当前物理连接标识；每次重连变化 |
| `ExchangeAsync(ReadOnlyMemory<byte>, TimeSpan?, ct)` | 发送请求并等待响应，**严格单在途** |
| `SendFrameAsync(ReadOnlyMemory<byte>, ct)` | 单向发送，等待写入 socket 完成 |

`ExchangeAsync` 返回 `CommandResult`，**必须 `using`**，响应缓冲从内存池租用：

```csharp
using var result = await handle.ExchangeAsync(payload, TimeSpan.FromSeconds(5));
if (result.Disposition == CommandDisposition.Executed)
{
    var bytes = result.Response;   // 仅在 using 作用域内有效
}
```

---

## 五、协议扩展

### IDeviceKind

```csharp
public interface IDeviceKind
{
    ISessionProtocol CreateProtocol(in ProtocolContext context);
}
```

`ProtocolContext` 提供 `TargetId`、`Direction`、`Limits`、`KindSettings`。**每次物理连接调用一次，必须返回全新实例。**

### ISessionProtocol

| 成员 | 说明 |
| --- | --- |
| `Decoder` | 分包器，必须每连接新建 |
| `Encoder` | 组帧器 |
| `Initializer` | 应用握手，`null` 表示无握手 |
| `Heartbeat` | 应用心跳，`null` 表示不做应用层探活 |
| `Response` | 响应判定策略 |

### ISessionInitializer

| 成员 | 说明 |
| --- | --- |
| `WriteHandshake(IBufferWriter<byte>)` | 写握手帧；零字节表示无需发送 |
| `Inspect(ReadOnlySpan<byte>)` | 返回 `Pending / Completed / Rejected` |
| `OnHandshakeCompleted()` | 握手成功后调用一次，可记录设备身份 |

### IHeartbeatPolicy

| 成员 | 说明 |
| --- | --- |
| `WriteProbe(long sequence, IBufferWriter<byte>)` | 构造探活帧 |
| `IsProbeResponse(ReadOnlySpan<byte>)` | 判定是否应答 |

### IResponsePolicy

| 成员 | 说明 |
| --- | --- |
| `IsResponse(ReadOnlySpan<byte>)` | 当前帧是否为在途请求的响应 |
| `Classify(ReadOnlySpan<byte>)` | 分类为 `Accepted` 或 `Rejected` |

默认实现 `NextFrameResponsePolicy`：有在途请求时把下一帧视为响应。**设备会主动推送时必须覆写 `IsResponse`。**

---

## 六、内置分包器

| 类型 | 说明 |
| --- | --- |
| `FixedLengthFrameDecoder` | 固定长度分包 |
| `LengthPrefixFrameDecoder` | 长度前缀分包，支持 1/2/4 字节、大小端、前缀是否含头 |
| `TerminatorFrameDecoder` | 终止符分包（如 `\r\n`） |
| `PassthroughFrameDecoder` | 每次读取即一帧，仅用于设备侧本就分界的场景 |

对应编码器：`LengthPrefixFrameEncoder`、`TerminatorFrameEncoder`、`PassthroughFrameEncoder`。

解码器返回 `FrameDecodeResult`：`NeedMore` / `Frame` / `Fatal`。`Fatal` 表示字节流已不可信，库会关闭当前连接，不尝试继续猜边界。

**载荷视窗语义**：`Decode` 返回的 `frame` 是输入缓冲的一段视窗，仅在本次调用期间有效。需要跨调用保存时必须复制。

---

## 七、观测

### IDriverObserver

```csharp
void OnEvent(in DriverEvent e);
```

`DriverEvent` 字段：`Kind`、`Direction`、`DeviceId`、`ListenerId`、`ConnectionId`、`Generation`、`Epoch`、`Endpoint`、`Detail`、`Value`、`TimestampTicks`。

观察者抛异常不影响驱动运行。

现成实现：`NullDriverObserver`（零开销）、`DelegateDriverObserver`、`DriverMetrics`（计数器 + `Snapshot()`）。

### 关键事件

| Kind | 何时 |
| --- | --- |
| `Ready` | 握手完成、链路可用 |
| `StateChanged` | 链路状态迁移 |
| `RetryScheduled` | 安排一次重连退避 |
| `HeartbeatTimeout` | 应用心跳判死 |
| `PendingAbandoned` | 在途请求因拆链被放弃（返回 `SentOutcomeUnknown`） |
| `StaleEpochDropped` | 旧 epoch 的帧被丢弃 |
| `SendBackpressure` | 写队列满 |
| `ConfigApplied` / `ConfigRolledBack` | 配置事务结果 |
| `CleanupTimeout` | 停机等待子任务超时 |

---

## 八、配置类型

| 类型 | 关键字段 |
| --- | --- |
| `DriverConfig` | `Devices[]`、`Listeners[]` |
| `DeviceDescriptor` | `DeviceId`、`KindId`、`Endpoints[]`、`Retry`、`Heartbeat`、`Handshake`、`KindSettings` |
| `EndpointDescriptor` | `Host`、`Port`、`EndpointId`、`SettleMs` |
| `ListenerDescriptor` | `ListenerId`、`KindId`、`Host`、`Port`、`Backlog`、`MaxSessions`、`Handshake`、`Heartbeat`、`KindSettings` |
| `RetryOptions` | `InitialMs`、`MaxMs`、`Jitter`、`ConnectTimeoutMs`、`EndpointCooldownMs` |
| `HeartbeatOptions` | `Enabled`、`IntervalMs`、`Misses`、`SilentMs`、`ResponseTimeoutMs` |
| `HandshakeOptions` | `TimeoutMs` |
| `DriverLimits` | `MaxFrameSize`、`ReadBufferSize`、`MaxWriteQueue`、`MaxSessionsPerListener`、`MaxTotalSessions`、`StopTimeout`、`FrameCacheTimeout`、`MaxPushQueue` |

`KindSettings` 是 `JsonElement`，由该 kind 按固定键校验，不是任意对象图，也不是运行时类型发现入口。

### JSON 读取

```csharp
var config = JsonSerializer.Deserialize(json, LiteDriverJsonContext.Default.DriverConfig);
```

核心只提供源码生成上下文。反射式重载在 AOT 下不可用，库不提供。

---

## 九、入站会话

设备主动连入时，应用通过句柄读取上报帧：

```csharp
foreach (var session in driver.GetInboundSessions("gateway-01"))
{
    using var frame = await session.ReceiveAsync(ct);
    if (frame is null) break;              // 会话结束
    await session.SendFrameAsync(reply, ct);
}
```

入站会话只投递帧，不自动应答——如何回应由业务决定。
