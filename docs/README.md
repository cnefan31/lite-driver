# 文档索引

| 文档 | 内容 |
| --- | --- |
| [API.md](./API.md) | 公开 API 逐个说明：构建、生命周期、设备操作、协议扩展、分包器、观测、配置类型 |
| [CONFIGURATION.md](./CONFIGURATION.md) | 全部参数默认值、边界、调优要点与三档典型配置 |
| [PUBLISHING.md](./PUBLISHING.md) | NuGet 受信任发布的一次性准备与发版流程 |
| [lite-driver-architecture-review.html](./lite-driver-architecture-review.html) | 架构设计与审阅稿（单文件 HTML，含三张架构图，断网可开） |

## 代码结构

```
src/LiteDriver/                核心库，零 NuGet 依赖
  Configuration/               配置 DTO、源生成 JSON、校验与冻结
  Protocol/                    帧编解码、设备种类与会话协议接口
  Sessions/                    连接上下文、读写泵、心跳、在途请求
  Transport/                   拨号器、DNS 解析、出站/入站监督器
  Observability/               事件与观察者
src/LiteDriver.Hosting/        可选：通用主机与 DI 集成
samples/LiteDriver.SampleHost/ 通用主机示例 + 本地模拟设备
tests/LiteDriver.Tests/        单元与环回集成测试
tests/LiteDriver.AotSmoke/     NativeAOT 原生二进制 smoke（零依赖核心）
tests/LiteDriver.Hosting.AotSmoke/  NativeAOT smoke（通用主机集成）
```

## 关键设计约束

1. 核心不引用任何 NuGet 包（CI 会解包校验依赖组为空）；Hosting 包只引用 `*Abstractions` 抽象包
2. 设备种类只能通过启动期冻结的显式工厂注册，禁止反射与运行时代码生成
3. 每条物理连接拥有独立 decoder、缓冲、写锁、心跳计数与在途表
4. 写命令断链后只返回 `SentOutcomeUnknown`，绝不自动重放
5. `Ready` 必须晚于 TCP 建连、应用握手与身份确认
