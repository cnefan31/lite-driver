# 配置参数与调优

所有默认值来自源码，逐项标注边界与实测影响。默认值未做真实设备压测，属于保守起点，现场需按设备特性校准。

---

## 一、DriverLimits

| 参数 | 默认 | 边界 | 影响 |
| --- | --- | --- | --- |
| `MaxFrameSize` | 1 MiB | ≥ 64 | 单帧上限。超出即关闭当前链路。协议帧远小于此值时调小可降低单连接内存占用 |
| `ReadBufferSize` | 8 KiB | ≥ 512 | 单次 socket 读取的池化缓冲大小。高频小帧可调小，大帧可调大以减少系统调用 |
| `MaxWriteQueue` | 128 | ≥ 1 | 单连接待发队列深度。超出返回 `BackpressureRejected`。队列越深，断链时被判 `Unknown` 的命令越多 |
| `MaxSessionsPerListener` | 64 | ≥ 1 | 单监听端点并发会话上限。它是 `ListenerDescriptor.MaxSessions` 的上界 |
| `MaxTotalSessions` | 512 | ≥ MaxSessionsPerListener | 全部监听端点会话总数上限 |
| `StopTimeout` | 10 s | > 0 | 优雅停机最长等待，超时强制中断并记 `CleanupTimeout` |
| `FrameCacheTimeout` | 1 s | > 0 | 半帧缓存保持上限 |
| `MaxPushQueue` | 256 | ≥ 1 | 入站推送帧通道容量，满时丢弃最旧并记 `FrameDropped` |

`DriverLimits` 在 `Build()` 时校验，非法直接抛 `ArgumentException`，不带病运行。

---

## 二、RetryOptions

| 参数 | 默认 | 边界 | 说明 |
| --- | --- | --- | --- |
| `InitialMs` | 500 | 1..600000 | 首次退避 |
| `MaxMs` | 30000 | 1..3600000，且 ≥ InitialMs | 退避上限 |
| `Jitter` | 0.2 | 0..1 | 抖动比例。多设备同时断线时应保留，避免同步重连风暴 |
| `ConnectTimeoutMs` | 3000 | 100..600000 | 单次拨号超时 |
| `EndpointCooldownMs` | 0 | 0..3600000 | 端点失败后的冷却时长 |

退避策略为指数增长：`min(Initial * 2^attempt, Max)`，再乘抖动因子。

**调优要点**：

- 设备重启通常需要数十秒，`MaxMs` 过小会造成无意义的重连轰炸，建议 ≥ 10 s
- 多设备场景下 `Jitter` 不要设为 0
- `ConnectTimeoutMs` 应小于业务的故障发现容忍时间

---

## 三、HeartbeatOptions

| 参数 | 默认 | 边界 | 说明 |
| --- | --- | --- | --- |
| `Enabled` | false | | 是否启用应用层心跳 |
| `IntervalMs` | 5000 | 100..3600000 | 探测间隔 |
| `Misses` | 3 | 1..100 | 连续未回应次数上限 |
| `SilentMs` | 20000 | 0..86400000，0 表示不限制 | 入站与探测均无数据的最长静默 |
| `ResponseTimeoutMs` | 2000 | 50..600000 | 单次探测等回应超时，同时作为 `ExchangeAsync` 的默认超时 |

判死时间约为 `IntervalMs × Misses`。它必须：

- **小于**业务可接受的故障发现时间
- **小于**内核 keepalive 判死时间，否则内核先断，应用层心跳失去意义

**重要**：`Enabled = true` 但设备种类未提供 `IHeartbeatPolicy` 时，库发 `ProtocolFault: heartbeat-unsupported-by-kind` 事件，不会静默假装心跳在工作。TCP keepalive 只能证明对端内核可能还活着，不能证明 PLC 应用仍正常。

---

## 四、HandshakeOptions

| 参数 | 默认 | 边界 | 说明 |
| --- | --- | --- | --- |
| `TimeoutMs` | 3000 | 50..600000 | 握手整体超时。超时后拆链并按瞬时错误重试 |

握手失败分两类：

- **超时**：`HandshakeTimeout`，按瞬时错误重试
- **明确拒绝**（认证失败、协议版本不符、设备身份不匹配）：`HandshakeRejected`，不进入无限紧循环

`Ready` 状态必须晚于 TCP 建连、应用握手、身份确认。仅 TCP 建连成功不代表设备可用。

---

## 四·五、起始/结束标记协议（kindId = 自定义）

用 `DelimitedKind` 实现「固定字节开始 / 固定字节结束」的帧，标记通过十六进制配置。两种配置方式二选一。

### 方式一：注册时固定（不依赖配置文件）

```csharp
builder.RegisterKind(
    "stx-etx",
    DelimitedKind.CreateFactory(startHex: "02", endHex: "03"),
    DelimitedKind.ValidateSettings);
```

### 方式二：由配置文件提供

```csharp
builder.RegisterKind("delimited", ctx => new DelimitedKind(ctx), DelimitedKind.ValidateSettings);
```

配置文件里写 `kindSettings`：

```json
{
  "devices": [
    {
      "deviceId": "meter-01",
      "kindId": "delimited",
      "endpoints": [{ "host": "192.168.1.20", "port": 4001 }],
      "kindSettings": { "start": "02", "end": "03" }
    }
  ]
}
```

| kindSettings 键 | 说明 |
| --- | --- |
| `start` | 起始标记，十六进制字符串；可省略 |
| `end` | 结束标记，十六进制字符串；可省略 |

**两者至少要有一个非空**，否则配置校验阶段即报错（`kind_settings_invalid`），不会拖到首次收包才失败。

支持的十六进制写法：`"02"`、`"03ff"`、`"0x02"`、`"0x02,0x03"`、`"FF FF FF FF"`。标记上限 16 字节。

### 三种配置的切帧语义

| start | end | 切帧规则 | 适用场景 |
| --- | --- | --- | --- |
| `02` | `03` | 起始到结束 | 最常见的 STX/ETX 协议 |
| `02` | 空 | 本起始到下一起始之前 | 定长或自身可定界的帧 |
| 空 | `ffffffff` | 缓冲区开头到结束标记 | 尾部有固定终止符的协议 |

> 仅配结束标记时，结束标记之前的全部字节都算一帧内容，断线后残留的半截数据会并入下一帧。这是缺少起始标记的固有约束，不是缺陷。

## 五、EndpointDescriptor

| 参数 | 默认 | 边界 | 说明 |
| --- | --- | --- | --- |
| `Host` | 必填 | 非空 | 主机名或 IP。每次重连重新解析 DNS |
| `Port` | 必填 | 1..65535 | 设备端口 |
| `EndpointId` | 自动 | 同一设备内唯一 | 仅用于日志与冷却区分 |
| `SettleMs` | 0 | 0..60000 | 连接成功后的额外静默，用于限制连接速率敏感的设备 |

多端点按轮转选择，配合 `EndpointCooldownMs` 实现容灾切换。

---

## 六、ListenerDescriptor

| 参数 | 默认 | 边界 | 说明 |
| --- | --- | --- | --- |
| `ListenerId` | 必填 | 非空、唯一 | 监听端点标识 |
| `KindId` | 必填 | 已注册 | 入站会话使用的设备种类 |
| `Host` | `0.0.0.0` | 非空 | `0.0.0.0` 监听全部网卡，测试建议 `127.0.0.1` |
| `Port` | 必填 | 0..65535，0 表示系统分配 | 测试与容器环境用 0，实际端口用 `GetListenerPorts()` 读取 |
| `Backlog` | 32 | 1..4096 | listen backlog |
| `MaxSessions` | 64 | 1..MaxSessionsPerListener | 该端点会话上限，超出显式拒绝 |

同一 `Host:Port`（非 0）被两个端点占用时，配置校验阶段即报 `listener_port_conflict`。

---

## 七、三档典型配置

### 开发调试

```csharp
new DeviceDescriptor
{
    Retry = new RetryOptions { InitialMs = 200, MaxMs = 2000, Jitter = 0.2 },
    Heartbeat = new HeartbeatOptions { Enabled = false },
    Handshake = new HandshakeOptions { TimeoutMs = 2000 }
}
```

### 生产车间（多数 PLC）

```csharp
new DeviceDescriptor
{
    Retry = new RetryOptions { InitialMs = 1000, MaxMs = 30000, Jitter = 0.3, ConnectTimeoutMs = 3000 },
    Heartbeat = new HeartbeatOptions
    {
        Enabled = true,
        IntervalMs = 5000,
        Misses = 3,              // 15 s 判死
        SilentMs = 20000,
        ResponseTimeoutMs = 2000
    },
    Handshake = new HandshakeOptions { TimeoutMs = 5000 }
}
```

### 弱网 / 无线网关

```csharp
new DeviceDescriptor
{
    Retry = new RetryOptions { InitialMs = 2000, MaxMs = 60000, Jitter = 0.4, ConnectTimeoutMs = 8000 },
    Heartbeat = new HeartbeatOptions
    {
        Enabled = true,
        IntervalMs = 10000,
        Misses = 4,              // 40 s 判死
        SilentMs = 60000,
        ResponseTimeoutMs = 5000
    },
    Handshake = new HandshakeOptions { TimeoutMs = 10000 }
}
```

配合 `DriverLimits.MaxWriteQueue` 调小（如 16），缩短断链时可判 `Unknown` 的命令窗口。

---

## 八、配置变更语义

`ApplyAsync` 是单写者事务：

1. **Validate**：ID 唯一、kind 已注册、端口范围、时间范围、端口冲突、kind 私有参数
2. **Freeze**：克隆集合与 `JsonElement`，生成不可变快照
3. **Stage**：为新增监听端点预绑定端口；任一失败即释放本轮新建资源并返回 `stage_failed`
4. **Commit**：原子替换快照并递增 `Revision`
5. **Retire**：停用旧代，等待其任务退出

任一步失败，驱动继续用上一次成功的快照服务。

**更新判定**：设备端点与 `KindSettings` 不变时复用传输（soft update，不重建 socket）；端点或种类变化则停旧代建新代（break-before-make）。首版不支持同一逻辑设备同时持有两条活动连接。

---

## 九、可观测指标建议

用 `DriverMetrics.Snapshot()` 或自定义 `IDriverObserver` 采集：

| 指标 | 异常信号 |
| --- | --- |
| `RetryScheduled` 频率 | 持续高频说明设备或网络长期不稳 |
| `HeartbeatTimeout` | 应用层已判死，但可能 TCP 仍连着 |
| `PendingAbandoned` | 断链打断了在途命令，业务需对账 |
| `SendBackpressure` | 写队列不足或下游消费过慢 |
| `StaleEpochDropped` | 正常现象；持续为 0 反而要怀疑隔离逻辑 |
| `FrameLimitExceeded` | 协议参数与设备实际不符 |
| `ConfigRolledBack` | 配置来源有错，需检查推送方 |
| `CleanupTimeout` | 停机时有任务未按时退出，需排查泄漏 |
