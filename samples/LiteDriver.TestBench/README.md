# LiteDriver 可视化测试台

用 [NanoServer](https://www.nuget.org/packages/NanoServer) 承载一个浏览器测试台，**同时**运行两个 LiteDriver 实例：

- **客户端驱动**（Outbound）：主动拨号到服务端
- **服务端驱动**（Inbound）：监听端口、接受连接、为每条连接建立独立会话

两者互连做连通测试，从而在同一屏内验证「出站」与「入站」两种模式，并能快速切换自定义协议。

## 运行

```bash
dotnet run --project samples/LiteDriver.TestBench -- --port 8099
# 打开 http://127.0.0.1:8099/
```

## 页面上能做什么

1. 分别选择客户端与服务端的协议
2. 点「启动双驱动」建立连接
3. 点「运行连通测试」，逐项查看断言结果
4. 切换协议后重新启动，立即验证另一套帧格式

连通测试的 5 项断言：

| 断言 | 验证目标 |
| --- | --- |
| 客户端链路 Ready | 出站模式：拨号 + 握手 + 身份确认走通 |
| 服务端出现入站会话 | 入站模式：监听 + accept + 会话建立走通 |
| 客户端收到应答 | 请求-响应关联正确 |
| 服务端应答内容符合协议约定 | 帧编解码在两侧一致 |
| 裸 socket 直连服务端可用 | **绕开客户端驱动**单独证明服务端可用 |

最后一项是关键：即使客户端驱动异常，也能独立确认服务端模式工作。

## 内置协议

| id | 帧格式 | 特点 |
| --- | --- | --- |
| `length-prefix` | 2 字节大端长度 + 载荷 | 最常见的工业私有协议 |
| `line-terminator` | 载荷 + `\n` | 带 HELLO/OK 握手与 PING/PONG 心跳 |
| `fixed-length` | 固定 16 字节，补空格 | 定长寄存器块传输 |
| `modbus-like` | MBAP 风格：事务ID + 协议ID + 长度 + 单元ID + 功能码 + 数据 | 带事务标识 |

## 加一个自定义协议

只需在 `Protocols/ProtocolCatalog.cs` 的 `ProtocolCatalog.List` 里加一项：

```csharp
new ProtocolDescriptor(
    "my-protocol",                    // 页面上的 id
    "我的协议",                        // 显示名
    "帧 = ...",                       // 说明
    "UTF-8 文本",                     // 编码
    SupportsHeartbeat: false,
    SupportsHandshake: false,
    static ctx => new MyKind(ctx),    // 工厂，必须静态可分析
    static payload => Encoding.UTF8.GetBytes("ECHO:" + Encoding.UTF8.GetString(payload))),
```

工厂用 `static ctx => new Xxx(ctx)`，保证 NativeAOT 下静态可达。加完重新构建，页面下拉框自动出现新协议，**无需改前端**。

## 自动化验证

```bash
python3 samples/LiteDriver.TestBench/verify-bench.py
```

假定测试台已在 `127.0.0.1:8099` 运行，逐协议启动双驱动并断言连通测试全通过。CI 里用的就是它。

## 为什么服务端要自己写应答泵

LiteDriver 的入站会话**只投递帧**，由业务决定如何回应——这是库的刻意设计，因为「收到帧后做什么」属于业务语义，不是连接层能决定的。

测试台在 `BenchRuntime.StartServerEchoPump` 里实现了应答循环：读取入站帧 → 按协议剥头 → 生成 ECHO 应答 → 写回。这也是一份「如何用入站会话」的可运行范例。
