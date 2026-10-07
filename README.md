# 话匣子 / Huaxiazi

当前版本：2.0.4

话匣子（技术标识 Huaxiazi）是面向 Windows 10/11 x64 的轻量桌面表达工具。它不替用户虚构事实，而是把口语化、零散或不专业的表达整理成可直接使用的专业成稿或专业提示词。

## 产品定位

话匣子包含两个可独立使用的核心任务：

- 表达润色（默认）：把原始想法整理成自然、保真、可直接发送的中文成稿；遇到关键歧义时先提最多 3 个澄清问题。
- 提示词优化：保留原有类别、深度和历史能力，把模糊需求整理为可直接交给 AI 的提示词。

项目采用本地优先设计：用户自带 API Key，密钥使用 Windows DPAPI 保存；无账号、无自营代理或外发遥测、无知识库和无模型训练。可选的生成诊断默认关闭、仅存于本机且无痕模式下停用。外部 Agent Skill 仅作为受限的表达策略导入，不执行脚本、Shell、MCP、浏览器或任意第三方工具。

## Agent 架构与工程编排

话匣子不是把用户文本直接转发给模型的薄客户端，而是一个由本地代码控制边界的中文表达 Agent。运行时先由规划器提取场景、风险与事实锚点，再由 Skill 路由器选择最多三个兼容的表达策略；上下文管线按 `system → developer → facts → skills → memory → knowledge → tools → task` 分层、转义和预算裁剪，最后交给对应 Provider 生成。结果必须经过协议解析、事实/质量门禁和有限修复，成功后才进入 UI 与本地版本化归档。

系统同时提供可复用的工具 Harness：Workflow 模式串行执行声明计划，Autonomous 模式只并行只读工具；变更工具需要幂等键，所有调用受超时、重试、总调用预算和 trace 约束。默认中文表达工作流不开放任意机器工具权限，Agent 能力与桌面应用权限保持分离。

完整的分层架构、状态模型、调用时序和模块边界见 [Agent 架构与工程编排](docs/AGENT_ARCHITECTURE.md)。

## 主要特性

- 默认 600×210 DIP、可调整尺寸的精灵便笺工作区；44×44 精灵视觉置于 60×60 阴影安全窗口内；系统托盘作为稳定入口。
- PerMonitorV2 DPI 感知、非透明正文窗口、ClearType、布局取整和 WPF 矢量图标。
- `RegisterHotKey` 全局快捷键默认只启用 `Ctrl+Shift+H` 呼出窗口；快速润色、提示词优化和复制结果由深度用户按需配置，单组冲突不会打断启动。生成快捷键为 `Ctrl+Enter`。
- 支持 OpenAI-Compatible、Anthropic Messages、Gemini GenerateContent，以及多个云端、本地 Ollama、LM Studio 和受管本地 GGUF 配置档；配置卡片集中管理，常用厂商可直接选择，推荐模型与最终 Model ID 分开呈现。
- API Key 使用 Windows DPAPI 保存，不写入 `config.json`；结构化连接诊断不包含请求正文、完整响应或请求头。
- 表达成稿自动版本化归档，可搜索、导出、软删除、恢复和永久清空。
- 本地智能编排自动推断场景、渠道与目的；日期、金额、数量和不确定承诺经过保真检查，失败时按需修复一次。
- 用户主动编辑形成的重复禁用词和“偏好简洁”等结构化行为可在本地复用；至少需要多次一致证据，无痕、关闭历史或关闭成稿保留时自动停止相应学习。
- 单实例限制在当前 Windows 桌面会话；重复启动会唤醒已有窗口，受限环境无法建立互斥时优先保证应用可启动。
- 错误日志单文件达到 5MB 后轮转，并仅保留最近 5 个归档，避免长期无界增长。
- 浅色、深色、跟随系统及暖橙（WarmOrange）、冰蓝（IceBlue）、雾蓝（MistBlue）、灰紫（GrayPurple）强调色；服从系统高对比度。
- 用户确认后下载更新，安装包必须通过 SHA-256 校验；更新源默认留空并安全禁用。

## 数据位置

所有用户数据位于 `%LocalAppData%\Huaxiazi`：

```text
config.json                 非敏感配置（schema v21）
secrets\                    DPAPI 加密的 API Key
data\huaxiazi.db            SQLite 索引、原文、背景与版本关系
data\drafts\                可直接使用的最终 .txt 成稿
data\trash\                 软删除文件（默认保留 30 天）
updates\                    已下载并校验的更新安装包
models\                     受管 GGUF 模型、LoRA、目录与断点下载文件（需随包提供本地推理运行时才启用）
```

2.0.0 是全新品牌与工程体系，不读取旧版本配置、旧 Skill 元数据或旧更新清单；升级后需要重新填写 API Key。旧目录不会被程序自动删除。

## 安装与使用

正式用户不需要另外安装 .NET、PowerShell 7、Ollama、Python、Node.js 或 Visual Studio。两种交付形态任选其一：

- **安装包** `Huaxiazi-Setup.exe`：双击向导安装，默认安装到当前用户可写目录（无需管理员），**安装路径可在向导中指定**；也支持静默安装 `Huaxiazi-Setup.exe /VERYSILENT /DIR="D:\Apps\Huaxiazi"`。
- **免安装** `Huaxiazi-Portable.zip`：完整解压后直接运行其中的 `Huaxiazi.exe`。

**用户数据始终保存在 C 盘** `%LocalAppData%\Huaxiazi`，与程序安装到哪个盘无关；需要改动时在「设置 → 数据管理」中调整。完整说明（静默安装参数、卸载、发布核对清单）见 [安装与部署](docs/安装与部署.md)。

本地 GGUF 模型属于可选能力，不是基础程序的安装前置条件。当前仓库没有随包提供 `llama-server.exe` 和 GGUF，因此没有本地运行时的版本仍会保留“本地模型”设置入口，允许导入模型和管理配置；只有本地生成能力会被明确置为不可用，不会要求用户额外安装 Ollama，也不会在启动时下载大模型。

## 构建与测试

开发构建需要 Windows 与 .NET 8 SDK。用户分发包使用 self-contained 发布，不要求目标机器安装 .NET：

```powershell
dotnet restore .\Huaxiazi.sln --locked-mode
dotnet build .\Huaxiazi.sln -c Release --no-restore
dotnet test .\Huaxiazi.Tests\Huaxiazi.Tests.csproj -c Release --no-build
.\publish.ps1 -AllowUnsigned -Clean -RequireInstaller
```

`publish.ps1` 一次产出 `release\` 下的全部交付物：`Huaxiazi-Setup.exe` 及其 Inno 数据分片 `Huaxiazi-Setup-*.bin`（完整安装包，默认安装到当前用户 C 盘目录，向导可指定路径）、`Huaxiazi-Portable.zip`（便携包）和 `SHA256SUMS.txt`。安装包与便携包共用同一份自包含文件夹发布，不交付脚本安装器或展开发布副本；`.bin` 是 Inno 安装数据，不是辅助安装代码；加 `-RequireInstaller` 可让找不到 Inno Setup 时直接终止。

完整验证应使用：

```powershell
dotnet test .\Huaxiazi.Tests\Huaxiazi.Tests.csproj -c Release
```

开发路线和当前交付状态见 [路线图](docs/ROADMAP.md) 与 [状态页](docs/STATUS.md)。发布脚本把构建和测试产物放入 Git 忽略的 `out/`，成功后只清理发布暂存，不删除报告与评测证据。对外交付物由发布流程写入 `release/`；发布前应核对版本、文件清单与 SHA-256。

正式交付前应保留 ZIP 的 SHA-256 校验值。未签名版本可能触发 Windows“未知发布者”或 SmartScreen 提示，请仅从可信来源获取并核对校验值。

## 隐私默认值

默认无外发遥测、无内容上传、不开机启动、不读取剪贴板。可选生成诊断默认关闭且仅保存在本机；无痕模式下不记录。只有用户发起生成时，请求中的正文及明确填写的背景才会发送给当前选择的模型服务商。本项目不包含自营 AI 后端、账号或云同步。启用历史时，原文和成稿默认以明文保存在当前 Windows 用户的本机数据目录；无痕模式不恢复或写入工作区草稿，也不保存历史。卸载程序不会自动删除用户数据。

详见 [隐私与数据](docs/隐私与数据.md) 和 [首次使用、备份与恢复](docs/首次使用与备份.md)。
智能编排的优先级、保真校验和隐私边界见 [智能编排与质量保障](docs/智能编排与质量保障.md)。
开发、发布和依赖边界见 [开发与发布规范](docs/development-standards.md)。

项目目录与模块边界见[项目结构地图](docs/PROJECT_MAP.md)；文档导航见[docs/README.md](docs/README.md)；直接采用成熟开源模型权重的选择、兼容验证和交付步骤见[开源模型采用路线](docs/OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md)。项目当前不计划训练自有权重。
