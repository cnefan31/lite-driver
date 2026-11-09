# 发布流程

两个包按相同版本号发布：`LiteDriver`（核心）与 `LiteDriver.Hosting`（通用主机集成）。

## 一、一次性准备（只有第一次需要）

### 1. 创建 GitHub Environment

```bash
gh api -X PUT repos/cnefan31/lite-driver/environments/release
```

`release.yml` 里的 `environment: name: release` 必须与这里完全一致。

### 2. 写入 NuGet 用户名 secret

```bash
gh secret set NUGET_USER --env release --body "cnefan31" -R cnefan31/lite-driver
```

只存用户名，不存 API Key——密钥通过 OIDC 临时换取，不落长期凭据。

### 3. 在 nuget.org 配置受信任发布策略

打开 https://www.nuget.org/account/trustedpublishing ，新增策略：

| 字段 | 值 |
| --- | --- |
| Repository Owner | `cnefan31` |
| Repository | `lite-driver` ← **仓库名，不是包名** |
| Workflow File | `release.yml` ← **只填文件名，不带路径** |
| Environment | `release` |

三项任一不符，OIDC 令牌缺少对应声明，交换会报 401。`LiteDriver.Hosting` 若也要用同一策略发布，在策略里可另建一条或使用 Repository 通配；本仓库两个包共用同一 workflow，同一策略即可覆盖。

## 二、发布一个版本

```bash
# 1. 确认 main 干净且 CI 绿
git status
gh run list --limit 3

# 2. 打 tag 并推送
git tag v1.0.0
git push origin v1.0.0
```

workflow 自动执行：语义化版本校验 → 还原 → 构建（警告视为错误）→ 回归测试 → 示例端到端 → 打包 → 包内容校验 → OIDC 换 key → 推送 NuGet → 创建 GitHub Release。

## 三、验证发布成功

```bash
# 1. 网页可见（立即可见）
open https://www.nuget.org/packages/LiteDriver

# 2. flatcontainer 索引（约 5-6 分钟后）
curl -s -o /dev/null -w '%{http_code}\n' \
  https://api.nuget.org/v3-flatcontainer/litedriver/index.json

# 3. 真装一次
mkdir -p /tmp/verify-litedriver && cd /tmp/verify-litedriver
dotnet new console -o probe && cd probe
dotnet add package LiteDriver --version 1.0.0
dotnet build
```

只看到 workflow 绿不算发布成功——必须验证能从官方源安装并编译。

## 四、常见失败与排查

| 症状 | 原因 | 处理 |
| --- | --- | --- |
| 令牌交换 401 | 策略里 Repository / Workflow / Environment 与实际不符 | 逐字段核对第三节的三项约束 |
| 令牌交换 401 | workflow 的 job 未声明 `environment: release` | job 级必须有同名 environment |
| `NUGET_USER` 为空 | secret 建在仓库级而 job 用 environment 级（或反之） | `gh secret list --env release` 核对 |
| 包内容校验失败 | 核心包出现依赖 | 检查 csproj 是否被误加 `PackageReference` |
| AOT smoke 失败 | 新增代码引入反射或动态代码 | 看 CI 的 IL2026/IL3050/IL2072 告警，改用源生成或静态工厂 |
| `--skip-duplicate` 导致空推送 | 版本号已存在 | NuGet 不允许覆盖同版本，改版本号重新打 tag |

## 五、版本号规则

- 遵循语义化版本：`MAJOR.MINOR.PATCH`，可选 `-preview.1` 后缀
- 破坏性变更升 MAJOR；新增能力且兼容升 MINOR；修复升 PATCH
- 预发布版本用 `-beta.1`、`-preview.1` 这类后缀，nuget.org 上不显示为稳定版
