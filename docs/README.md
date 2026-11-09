# LiteDriver 架构审阅稿

本目录存放设计审阅材料，当前不包含实现代码。

## 文档

- [lite-driver-architecture-review.html](./lite-driver-architecture-review.html)：单文件 HTML 审阅稿。包含分层架构图、出站/入站设备拓扑图、生命周期与重连状态机、帧收发管线、配置事务流程、AOT 动态创建边界、公开 API 草案、测试验收矩阵与待确认决策。

## 边界

- 文档状态：待审阅，尚未实现。
- `LiteDriver` 是随工作区名称拟定的暂定项目名，尚未检查 NuGet 包名占用。
- 所有 CSS、SVG 图形与正文内嵌在 HTML 中，断网可打开；参考资料为外链，点击时才访问。
- 工作区本机 `.NET SDK` 为 `10.0.201`，但尚未创建 C# 项目，也未执行 AOT 发布或工业设备连接测试。
