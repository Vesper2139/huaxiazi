# 话匣子

当前版本：1.2.2

“话匣子”是面向 Windows 10/11 x64 的轻量桌面表达工具，包含两个可独立启用的模式：

- 表达润色（默认）：把原始想法整理成自然、保真、可直接发送的中文成稿；遇到关键歧义时先提最多 3 个澄清问题。
- 提示词优化：保留原有类别、深度和历史能力，把模糊需求整理为可直接交给 AI 的提示词。

## 主要特性

- 默认 600×210 DIP、可调整尺寸的精灵便笺工作区；44×44 精灵视觉置于 60×60 阴影安全窗口内；系统托盘作为稳定入口。
- PerMonitorV2 DPI 感知、非透明正文窗口、ClearType、布局取整和 WPF 矢量图标。
- `RegisterHotKey` 全局快捷键默认只启用 `Ctrl+Shift+H` 呼出窗口；快速润色、Prompt 优化和复制结果由深度用户按需配置，单组冲突不会打断启动。生成快捷键为 `Ctrl+Enter`。
- OpenAI-Compatible `/chat/completions`，支持多个云端或本地 Ollama 配置档。
- API Key 使用 Windows DPAPI 保存，不写入 `config.json`。
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
config.json                 非敏感配置（v2）
secrets\                    DPAPI 加密的 API Key
data\huaxiazi.db            SQLite 索引、原文、背景与版本关系
data\drafts\                可直接使用的最终 .txt 成稿
data\trash\                 软删除文件（默认保留 30 天）
updates\                    已下载并校验的更新安装包
```

旧 `%AppData%\PromptFloat\config.json` 会在首次启动时以事务式顺序迁移：先加密密钥并写入新配置，成功后才脱敏旧文件。

## 构建与测试

需要 Windows 与 .NET 8 SDK：

```powershell
dotnet restore .\Huaxiazi.sln
dotnet build .\Huaxiazi.sln -c Release --no-restore
dotnet test .\PromptFloat.Tests\PromptFloat.Tests.csproj -c Release --no-build
.\publish.ps1 -Clean
```

发布过程只临时使用 `out/`，成功后自动清理。对外交付物仅保留在 `release/`：一个最新便携 ZIP 和对应 SHA-256 文件。源码目录不长期保留展开发布副本、编译缓存或测试运行目录。

也可运行 `publish.ps1` 完成恢复、Release 构建、全量测试、自包含发布和可选签名。无签名证书时产物仅用于测试分发。

## 隐私默认值

默认无遥测、无内容上传、不开机启动、不读取剪贴板。只有用户发起生成时，请求中的正文及明确填写的背景才会发送给当前选择的模型服务商。本项目不包含自营 AI 后端、账号或云同步。

详见 [隐私与数据](docs/隐私与数据.md) 和 [首次使用、备份与恢复](docs/首次使用与备份.md)。
智能编排的优先级、保真校验和隐私边界见 [智能编排与质量保障](docs/智能编排与质量保障.md)。
