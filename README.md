# GachaOps

Windows 上的二游自动化总控，集中管理 BetterGI、MAA 和 MaaEnd，一键运行日常任务并汇总结果。

## 支持工具

| 游戏 | 工具 |
| --- | --- |
| 原神 | [BetterGI](https://github.com/babalae/better-genshin-impact) |
| 明日方舟 | [MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights) |
| 明日方舟：终末地 | [MaaEnd](https://github.com/MaaEnd/MaaEnd) |

各工具需自行安装和配置。GachaOps 负责启动与调度，游戏中的具体操作由对应工具完成。

## 主要功能

- **一键运行**：按需选择工具，拖动调整任务顺序。
- **双通道调度**：同通道串行，不同通道并行。
- **启动准备**：检查配置与运行状态，支持启动前更新工具。
- **每日定时**：设置多个运行时刻，无需保持 GachaOps 常驻。
- **结果汇总**：查看运行历史，集中提醒异常与待补做项目。
- **外部通知**：支持 Bark 和 ntfy。
- **自动运行与退出**：支持打开后自动运行、最小化及成功完成后退出。

## 下载与文档

支持 Windows x64。

- [下载最新版本](https://github.com/baozi-jpg/gacha-ops/releases/latest)
- [使用指南](docs/usage.md)：安装配置、日常运行、定时与通知
- [开发说明](docs/development.md)：源码构建与测试

## 问题反馈

请在 [Issues](https://github.com/baozi-jpg/gacha-ops/issues) 中提供问题描述、复现步骤、相关版本及截图。分享前请遮盖个人信息和通知凭据。

## 声明与许可

GachaOps 是独立的非官方项目，与上述工具及相关游戏的开发者或运营方无隶属或授权关系。使用前请自行确认相关工具及游戏的使用规则。

源码采用 [MIT License](LICENSE)。各工具分别遵循自身的许可证和使用条款。
