# 话匣子开发与发布规范

本规范的第一目标是：用户下载后可以直接使用，不因开发工具、模型工具或构建工具缺失而无法启动。

## 一、依赖分层

### 1. 终端用户依赖：必须为零

正式交付的安装包和 `Huaxiazi-Portable.zip` 必须是 self-contained 产物。终端用户不应被要求另外安装：

- .NET Runtime 或 .NET SDK；
- PowerShell 7、Node.js、Python、Visual C++ Redistributable；
- Ollama、LM Studio 或其他模型管理器；
- Inno Setup、Visual Studio 或任何构建工具。

便携 ZIP 只包含程序的自包含运行目录；解压后直接运行其中的 `Huaxiazi.exe`，不附带辅助安装脚本。

### 2. 可选能力依赖：不得阻塞基础功能

本地模型属于可选能力包，不是桌面程序启动前置条件：

- 没有 `runtimes/local/llama-server.exe` 时，仍必须保留“本地模型”设置入口，允许用户查看目录、导入模型和管理配置；仅将“本地生成”置为不可用，并显示明确的可操作提示；
- 不在启动时自动下载数 GB 模型；
- 云端 Provider、悬浮球、皮肤和基础设置不依赖本地模型；
- 启用本地模型前必须同时检查运行时、模型文件、SHA-256 和许可证说明；
- 缺失或损坏时显示可操作的中文提示，不得假报“健康”或“连接成功”。

如果未来提供“离线完整包”，运行时和 GGUF 必须作为同一版本化模型包交付，并通过发布门禁后才能对外宣称“安装即用”。

### 3. 开发与发布机依赖：与用户依赖分开

开发机只需要 Windows、.NET 8 SDK、Inno Setup 6.7.3+ 和仓库锁定的 NuGet 依赖。发布脚本兼容 Windows 自带 PowerShell 5.1；PowerShell 7 可以作为 CI 推荐环境，但不得成为用户安装条件。图形安装包由 `deploy/installer.iss` 生成，正式发布时应使用 `-RequireInstaller` 强制检查 Inno Setup。版本门禁读取卸载注册表 `DisplayVersion`（兼容 `Inno Setup 6_is1` 与 `Inno Setup_is1` 键），不依赖 `ISCC.exe` 的 PE `FileVersion`。代码签名工具是可选的发布增强项。

## 二、交付物规范

每次成功发布必须产生：

1. `Huaxiazi-Setup.exe`：用户可指定安装目录的图形安装包；
2. `Huaxiazi-Portable.zip`：完整 self-contained 文件夹，解压后可直接运行；
3. `SHA256SUMS.txt`：覆盖所有对外交付文件。

禁止把 framework-dependent 输出作为面向普通用户的默认下载项，也禁止在 README 或安装界面要求用户先安装 .NET。

## 三、发布门禁

发布脚本必须在交付前自动验证：

- locked restore 使用与 publish 完全一致的 Runtime 和 `PublishSingleFile=false` 属性；
- Release 构建为 0 警告、0 错误；
- 测试报告明确记录通过、失败、跳过三元组；
- 安装包和 Portable ZIP 均存在；
- ZIP 中包含 `Huaxiazi.exe`、`Config/default-config.json`、`Prompts/SystemPrompt.txt` 和品牌图标；
- 所有交付物都有 SHA-256；
- 若源码提供 `runtimes/local`，每个 `llama-server.exe` 都必须出现在发布目录中；
- 若源码未提供本地运行时，发布仍可成功；本地模型入口必须保留，运行时缺失只能门控生成能力，不能隐藏模型配置、导入和目录管理。

## 四、安装与升级稳定性

- 安装器必须幂等：重复运行执行覆盖升级，不删除 `%LocalAppData%\Huaxiazi` 外的用户资料；
- 默认采用 per-user 安装，避免不必要的 UAC 和 Program Files 写权限问题；
- 更新前不得覆盖正在运行的文件；
- 卸载只移除程序、快捷方式和卸载注册表项，必须保留用户配置、模型、历史和备份，并明确告知用户；
- 下载、安装、模型导入和升级均须支持取消、超时、失败重试或安全回滚；
- 任何可选依赖缺失都必须转化为局部不可用，而不是启动失败。

## 五、文档要求

README 必须明确区分“用户无需安装的内容”和“开发者构建所需的内容”。每次新增外部依赖时，必须回答：

1. 它是否会进入终端用户安装路径？
2. 能否改为 self-contained 或可选能力包？
3. 缺失时是否有降级路径？
4. 发布门禁和用户提示是否同步更新？

如果不能回答以上四项，功能不得进入默认发布路径。
