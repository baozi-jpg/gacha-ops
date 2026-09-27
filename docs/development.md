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
