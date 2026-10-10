# 开发说明

[返回 README](../README.md)

## 环境

- Windows x64
- PowerShell
- .NET 10 SDK

项目使用 C# 和 WPF。普通用户运行发行包无需安装 SDK。

## 目录结构

| 路径 | 用途 |
| --- | --- |
| `src/GachaOps.App/` | WPF 界面与交互 |
| `src/GachaOps.Core/` | 调度、工具适配、日志、设置与历史 |
| `tests/GachaOps.Core.Tests/` | 离线测试 |
| `scripts/` | 测试、构建与发布脚本 |

GachaOps 负责外部工具的启动、调度与结果监控，不直接执行游戏任务。工具专属参数、进程识别和日志判断放在相应适配器或工具服务中。

## 从源码构建

安装 .NET 10 SDK 后，在仓库根目录运行：

```powershell
dotnet restore GachaOps.slnx --configfile NuGet.Config
dotnet build GachaOps.slnx --configuration Release --no-restore
dotnet run --project tests/GachaOps.Core.Tests/GachaOps.Core.Tests.csproj --configuration Release --no-build
```

## 使用项目脚本

项目脚本使用仓库内的 `.dotnet/` SDK，并配置隔离的 CLI、应用数据及 NuGet 环境。使用前需准备项目本地 SDK。

在仓库根目录运行：

```powershell
# 离线测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1

# 构建
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

# 生成本地发行包
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1

# 检查差异格式
git diff --check
```

本地发布产物位于 `artifacts/GachaOps-win-x64/`。分发时应保留整个目录，不能只复制 EXE。发布脚本生成本地产物，不会上传到 GitHub Releases。

首次恢复运行包可能需要访问官方 NuGet 源。

## 官网下载同步

`.github/workflows/sync-download.yml` 在正式 Release 发布后将完整 Windows ZIP 同步到 Cloudflare R2，也可在 Actions 中手动运行以补同步当前最新正式版。先将 ZIP 上传到 Release 草稿，再发布；使用 `GITHUB_TOKEN` 自动创建 Release 时，应在发布工作流中直接调用同步步骤，该令牌触发的发布事件不会另行启动此工作流。

在仓库的 Actions 配置中添加以下变量和密钥：

| 类型 | 名称 | 内容 |
| --- | --- | --- |
| Variable | `R2_ACCOUNT_ID` | Cloudflare 账户 ID |
| Variable | `R2_BUCKET` | 下载桶名称 |
| Variable | `R2_PUBLIC_BASE_URL` | 下载域名的 HTTPS 地址，不含路径或末尾斜杠 |
| Secret | `R2_ACCESS_KEY_ID` | 限定下载桶的 Access Key ID |
| Secret | `R2_SECRET_ACCESS_KEY` | 对应的 Secret Access Key |

R2 凭据仅授予该桶的对象读取和写入权限。下载域名绑定 R2；对 `/latest/GachaOps-win-x64.zip` 配置绕过缓存，不设置覆盖 `no-store` 的缓存规则。

同步脚本调用 `gh`、AWS CLI 和 Python 标准库，使用 GitHub Ubuntu runner 已有工具。它核对发布附件的大小、SHA-256、ZIP 完整性及必要程序文件，上传版本包并回读核验；再次确认最新发布未变化后，才更新固定下载地址。最后从公网下载完整 ZIP，核对 SHA-256 和 `Cache-Control: no-store`。上传或版本包核验失败时不会更新最新包；更新后的公网验收失败会报告错误，保留版本包供检查，不自动删除或回滚。

工作流串行运行，每次都解析当前最新正式版；手动补同步旧任务也不会主动选取旧版本。历史版本包保留在 `releases/<版本>/<包名>`，固定入口为 `latest/GachaOps-win-x64.zip`。同版本重跑会重新上传、校验，不要求更改附件名称。

只读验证发布附件时，在已安装 `gh` 和 Python 的环境运行：

```powershell
$env:GITHUB_REPOSITORY = 'baozi-jpg/gacha-ops'
python -B scripts/sync-download.py --verify-only
python -B -m unittest discover -s scripts -p 'test_sync_download.py'
```

离线测试使用独立临时目录和模拟的 GitHub、R2 响应，不访问真实桶。首次上线还须完成一次实际同步，并核对官网下载结果；离线测试不能代替此项验收。

## 验证要求

| 改动类型 | 检查 |
| --- | --- |
| 文档 | 内容、链接及 `git diff --check` |
| UI 文案或布局 | 编译，并检查实际界面 |
| 队列、适配器、日志、设置或历史 | 完整离线测试与构建 |
| 影响运行行为或界面的代码 | 测试与构建通过后，生成本地发行包 |

涉及工具行为的改动，应另外记录需要人工验证的场景及实际结果，将离线测试和真实运行分别报告。

## 开发边界

- 测试使用独立临时目录，不读取或修改真实用户设置、历史及游戏数据。
- 不在普通构建或测试中启动真实工具、运行游戏或执行工具更新。
- 保留项目 SDK、NuGet 隔离及现有依赖版本，不为构建修改全局环境。
- 不手工修改 `bin/`、`obj/`、`artifacts/` 等生成内容。
- 保留“停止后续任务”和“关闭程序”的现有语义，不强制结束外部工具。
