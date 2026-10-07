# ADR 0001：采用当前用户安装与文件夹式发布契约

- 状态：Accepted（对当前已实现并用于 2026-10-06 交付物的契约作追溯记录）
- 日期：2026-10-07
- 范围：Windows Portable 与 Setup 交付方式

## 背景

旧版发布检查以单文件 `Huaxiazi.exe`、脚本安装器、`Program Files` 目标目录和管理员权限为前提。当前实现使用文件夹式 self-contained 发布：Portable ZIP 直接运行 `Huaxiazi.exe`；Inno Setup 安装包复用同一发布目录，写入当前用户的 `%LOCALAPPDATA%\Programs\话匣子`，不要求应用本体以管理员权限运行。`UseSetupLdr=no` 会生成 Setup EXE 与配套 `.bin` 数据分片，交付时必须将分片与 EXE 一起提供，并共同校验 SHA-256。

该契约已体现在 `publish.ps1`、`deploy/installer.iss`、发布检查清单和 2026-10-06 `release/` 制品中。测试中的四项旧断言随契约调整为验证文件夹发布、Setup 与分片、per-user 安装路径及无旧脚本安装器。这里记录的是现行工程决策；原始变更没有留下同期决策记录。

## 决策

1. Portable 与 Setup 共享同一份文件夹式 self-contained 发布输出。保持 `PublishSingleFile=false`，避免启动时依赖单文件自解压目录；也让两个交付方式使用同一组应用文件。
2. Setup 使用 Inno Setup per-user 安装，`PrivilegesRequired=lowest`，默认安装至当前用户 LocalAppData 下的 Programs。安装和应用运行不主动要求管理员权限。
3. Setup 使用 `UseSetupLdr=no` 的分片输出。发布目录必须包含 `Huaxiazi-Setup.exe` 与所有 `Huaxiazi-Setup-*.bin`；缺少任一分片即视为不可交付。Portable ZIP 独立提供，不依赖 Setup 分片。
4. `release/` 只保留 Portable ZIP、Setup EXE、Setup 分片、校验和及明示的交付文件；临时文件夹发布目录不进入 `release/`。
5. 发布契约测试以当前用户权限与实际分片交付契约为准。旧契约断言只能随经审查的发布决策更新，不以单纯消除测试失败为理由修改。

## 理由与取舍

- per-user 安装降低普通用户安装门槛，并避免安装应用本体时修改机器范围目录或注册表；代价是应用只对当前 Windows 用户安装。
- 文件夹发布便于 Portable 和 Setup 保持内容一致；代价是文件数量和目录体积增加，压缩/安装包仍可整体分发。
- Setup 分片便于交付构建保留 EXE 与数据块边界；代价是 EXE 单独无效，必须通过校验清单共同交付。
- 受管模型、推理运行时及 VC++ 运行库不随主安装包捆绑；它们按用户选择的功能分别下载或按需安装，保持主应用安装包与本地推理资源解耦。

## 验收规则

- Portable ZIP 可解压并包含默认配置、品牌图标、系统提示词与预设技能目录。
- Setup 安装包及分片均有校验和；`PrivilegesRequired=lowest`，默认路径属于当前用户 LocalAppData。
- 正式发布必须用 `publish.ps1 -RequireInstaller`；缺少 ISCC、缺少 `.bin` 分片或校验失败时发布应失败，不得以旧 Setup 冒充本次构建产物。
- 主安装产物不包含 GGUF、`llama-server.exe`、Ollama 可执行文件或 VC++ Redistributable 安装包。

## 后续复核

若将 Setup 改回机器级安装、改变发布形态、合并/移除 `.bin` 分片或把模型/运行时放入主安装包，需新增 ADR 并同步发布契约测试与用户文档。签名、自动更新与安装权限的改变须单独评估。
