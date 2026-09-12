# 话匣子 / Huaxiazi

当前版本：2.0.1

话匣子（技术标识 Huaxiazi）是面向 Windows 10/11 x64 的轻量桌面表达工具。它不替用户虚构事实，而是把口语化、零散或不专业的表达整理成可直接使用的专业成稿或专业提示词。

## 产品定位

话匣子包含两个可独立使用的核心任务：

- 表达润色（默认）：把原始想法整理成自然、保真、可直接发送的中文成稿；遇到关键歧义时先提最多 3 个澄清问题。
- 提示词优化：保留原有类别、深度和历史能力，把模糊需求整理为可直接交给 AI 的提示词。

项目采用本地优先设计：用户自带 API Key，密钥使用 Windows DPAPI 保存；无账号、无自营代理、无遥测、无知识库和无模型训练。外部 Agent Skill 仅作为受限的表达策略导入，不执行脚本、Shell、MCP、浏览器或任意第三方工具。

## 主要特性

- 默认 600×210 DIP、可调整尺寸的精灵便笺工作区；44×44 精灵视觉置于 60×60 阴影安全窗口内；系统托盘作为稳定入口。
- PerMonitorV2 DPI 感知、非透明正文窗口、ClearType、布局取整和 WPF 矢量图标。
- `RegisterHotKey` 全局快捷键默认只启用 `Ctrl+Shift+H` 呼出窗口；快速润色、提示词优化和复制结果由深度用户按需配置，单组冲突不会打断启动。生成快捷键为 `Ctrl+Enter`。
- 支持 OpenAI-Compatible、Anthropic Messages、Gemini GenerateContent，以及多个云端、本地 Ollama 或 LM Studio 配置档；配置卡片集中管理，常用厂商可直接选择，推荐模型与最终 Model ID 分开呈现。
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
config.json                 非敏感配置（schema v20）
secrets\                    DPAPI 加密的 API Key
data\huaxiazi.db            SQLite 索引、原文、背景与版本关系
data\drafts\                可直接使用的最终 .txt 成稿
data\trash\                 软删除文件（默认保留 30 天）
updates\                    已下载并校验的更新安装包
```

2.0.0 是全新品牌与工程体系，不读取旧版本配置、旧 Skill 元数据或旧更新清单；升级后需要重新填写 API Key。旧目录不会被程序自动删除。

## 构建与测试

需要 Windows 与 .NET 8 SDK：

```powershell
dotnet restore .\Huaxiazi.sln --locked-mode
dotnet build .\Huaxiazi.sln -c Release --no-restore
dotnet test .\Huaxiazi.Tests\Huaxiazi.Tests.csproj -c Release --no-build
.\publish.ps1 -AllowUnsigned -Clean
```

完整验证应使用：

```powershell
dotnet test .\Huaxiazi.Tests\Huaxiazi.Tests.csproj -c Release
```

发布过程只临时使用 `out/`，成功后自动清理。对外交付物保留在 `release/`：直接启动 EXE、便携 ZIP 和 Windows 安装包。源码目录不长期保留展开发布副本、编译缓存或测试运行目录。

也可运行 `publish.ps1` 完成锁定恢复、Release 构建、分组测试、自包含发布和 SHA-256 清单生成。开源发布使用显式的 `-AllowUnsigned`；如以后取得证书，仍可通过 `-CertPath` 或 `-CertSha1` 启用可选 Authenticode 签名。未签名版本可能触发 Windows“未知发布者”或 SmartScreen 提示，请仅从本仓库下载并核对 `SHA256SUMS.txt`。

## 隐私默认值

默认无遥测、无内容上传、不开机启动、不读取剪贴板。只有用户发起生成时，请求中的正文及明确填写的背景才会发送给当前选择的模型服务商。本项目不包含自营 AI 后端、账号或云同步。启用历史时，原文和成稿默认以明文保存在当前 Windows 用户的本机数据目录；无痕模式不恢复或写入工作区草稿，也不保存历史。卸载程序不会自动删除用户数据。

详见 [隐私与数据](docs/隐私与数据.md) 和 [首次使用、备份与恢复](docs/首次使用与备份.md)。
智能编排的优先级、保真校验和隐私边界见 [智能编排与质量保障](docs/智能编排与质量保障.md)。
