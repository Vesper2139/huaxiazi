# 阶段二续作：本地运行时前置依赖交付闭环（2026-10-07）

## 已实施

- 新增 `LocalRuntimePrerequisiteService`，并接入 `LocalRuntimeManager` 的受管本地启动路径。只在确认 ManagedLocal 模型和运行时存在、且尚无复用中的本地进程后检查 VC++；云端 Provider 不会走此服务。
- 最低版本固定为 Microsoft Visual C++ x64 Redistributable `14.51.36247.0`。安装包按需从固定版本的 Microsoft aka.ms 链接下载，临时保存到用户下载目录；限制最大 64 MiB，验证 SHA-256 和 Microsoft Authenticode 信任签名后才可执行。
- 只有已安装版本低于最低版本时才下载和安装。启动参数固定为 `/install /passive /norestart`，通过 `Verb=runas` 触发系统 UAC；用户接受授权后等待安装结束，并重新读取 HKLM 64 位运行库版本。安装/下载失败、网络不可用或 UAC 拒绝时显示原因、官方手动安装地址，并明确本地推理停止、不切换云端。
- 用户取消下载会取消本次操作；校验未通过的下载不会启动；临时安装包在所有路径结束后清理。系统安装程序已获 UAC 同意并启动后，会等安装器结束再清理和复查，避免留下后台安装进程。
- b11429 CPU/Vulkan 候选的下载 SHA-256 与官方 GitHub attestation subjects 一致，两个归档解压后的 `llama-server.exe --version` 均报告 `0.6.0-dev (build 11429, commit d81235049)`，编译器报告 Clang 20.1.8。CPU/Vulkan zip 实测大小分别为 19,398,918 / 33,337,769 bytes。

## 回归验证

- 新增 7 项 VC++ 前置服务测试：已安装最新版跳过、校验后请求 UAC 并重查版本、SHA/签名拒绝、UAC 取消、安装退出失败、离线失败和用户取消。定向结果：**7/7 通过**。
- 当前开发机 HKLM x64 注册表报告 `v14.51.36247.00`，满足最低版本；没有触发安装/UAC，避免对开发环境做不必要的系统改动。
- 从 Microsoft 版本化链接下载的文件实测：版本 `14.51.36247.0`，SHA-256 `843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C`，Windows Authenticode 状态 `Valid`，签名者 `Microsoft Corporation`。该二进制只存于系统临时目录，没有进入仓库、安装目录或 release 产物。
- `LocalOnly` 本地失败不创建云端 fallback 的产品集成行为已有 `CompanionGenerationIntegrationTests` 回归覆盖；本次修改只在本地进程创建之前增加前置依赖检查。
- 全量 `Huaxiazi.Tests`：**2,029 通过、11 跳过、0 失败（共 2,040）**；`dotnet build Huaxiazi.csproj -c Release --no-restore`：0 warning / 0 error。
- 完成 win-x64 self-contained Release publish 并在隔离路径生成 Portable ZIP：[候选包](../out/test-artifacts/managed-local-phase2-release-20261007/Huaxiazi-Portable.zip)，SHA-256 `828446c701483bcfab4e562bf1c135badddebf33bec16c0debb44c3f27c15d430`。目录包含 511 个文件、约 207 MB；ZIP 512 entries、106,796,447 bytes。对目录和 ZIP 均检查未发现 GGUF、`llama-server.exe`、Ollama executable、VC++ 安装器或 `runtimes/local`。该 ZIP 留在 `out/test-artifacts/` 作 review candidate，**没有替换 `release/`**。

## 运行时版本决定

当前产品目录仍锁定 b11424。虽然 b11429 CPU/Vulkan 归档均匹配 attestation hash 且可执行文件版本报告一致，但本次无法取得已有隔离 Qwen3-4B GGUF，所以不能完成计划要求的同模型双后端兼容性运行。按计划的升级条件，b11429 保持候选而非产品锁定版本。其官方归档摘要：CPU `1283323272b04cd07905816a597a0da810918102de958f4ff6f7bbaa70ed2efe`；Vulkan `1bfe78ad9168b79fa02bf67f6af9f5e17a966d824d77238517f7bef12ac73b36`。

## 尚未完成的实机验收

- 本机未发现 Hyper-V `Get-VM`、VirtualBox `VBoxManage` 或 VMware `vmrun` 命令，当前没有可启动的干净 Windows VM。不能把本机已有的 VC++ 运行库状态伪装成“干净系统自动安装”验证。
- 本回合未能通过 WPF 界面建立隔离用户首次使用流程：运行环境没有可调用的原生桌面交互 API；仓库模型搜索和隔离 smoke 输出中也没有可复用的 GGUF。既有 2026-10-07 应用自管链路 smoke 仍只证明 b11424 产品服务和工作流在先前隔离模型注册表上工作，并不证明真实 UI 下载/取消或本回合 WPF 首启。
- 未找到 Inno Setup 6 `ISCC.exe`，因此不能重建 Setup。`publish.ps1` 会更新 `release/` 交付目录；由于完整安装器工具缺失，本回合没有运行该脚本，以免先移除既有 Setup 再在缺少编译器时失败。下一次有 ISCC 和干净 Windows VM 后，执行 Setup、正式 release 目录更新及首次使用验收。
- 本报告的自动化测试覆盖模拟安装器/下载结果；没有触发真实 UAC、修改系统 CRT、真实 GGUF 运行或 VM 自动安装。
