# 工程基础收口记录（2026-10-07）

参考：[项目进度复盘](../../deliverables/engineering-assurance/project-progress-review-2026-10-07.md)

## 本次处理

1. `.gitignore` 增加 `.tmp-test-env/`、全局 `*.gguf` 与两处测试子项目误生成 lock 文件的精确路径规则。当前 2.38 GB 隔离 GGUF 仍在 `out/` 中，未删除、移动或加入版本控制。
2. 移除已确认是临时误生成、未跟踪且不属于默认项目锁文件的 `DatasetBuilder/deploy/packages.tests.win-x64.lock.json` 与 `Huaxiazi.Tests/deploy/packages.tests.win-x64.lock.json`。项目正式的 `packages.lock.json` 和 `deploy/packages.win-x64.lock.json` 均保留。
3. `publish.ps1` 将脚本目录规范化为绝对路径，避免从不同工作目录调用时把 `NuGetLockFilePath` 及发布路径按调用者目录解释。用绝对路径从仓库父目录执行 `-ValidateReleasePolicyOnly` 成功。
4. 新增 [ADR 0001](adr/0001-user-scope-release-package-contract.md)，追溯记录 folder publish、per-user Setup、Setup `.bin` 分片、对应验收契约及变更后果；明确说明该 ADR 是追溯记录，不伪称在原始修改时已存在决策文档。
5. 移除本地运行时观察结果上的 `Backend` 别名。仓库调用方和测试均使用 `BackendCandidate`；`Huaxiazi` 是桌面应用而非公开 NuGet 库，项目内未发现该别名的序列化、配置或反射依赖。`DisplayBackend` 保留，因为它包含面向用户的后端解释逻辑。

## 验证

- 默认 `%TEMP%` 下运行复盘建议的 Release 单节点测试，稳定复现环境权限边界：测试在创建 `%LOCALAPPDATA%\Temp\...` 临时目录时抛 `UnauthorizedAccessException`；本次观察到 514 通过、81 失败后，WPF 宿主因同一路径问题崩溃并中止。此轮结果不作为代码测试失败率。
- 将 `TEMP`/`TMP` 指向仓库内隔离且可写的 `.tmp-test-env/` 后，执行相同的 `dotnet test .\Huaxiazi.Tests\Huaxiazi.Tests.csproj -c Release -m:1 -nodeReuse:false`：**2,029 通过、11 跳过、0 失败（共 2,040）**。
- 移除别名后，Release 构建成功（0 警告、0 错误），`LocalRuntimeBackendObservationTests` **8 通过、0 失败**；仓库源码中没有遗留 `Backend` 别名引用。
- `git diff --check` 退出码 0；临时 GGUF、测试输出和误生成 lock 路径均由 ignore 规则覆盖。

## 边界

- 没有 `git add`、提交或清理其它未跟踪数据；不改动盲评/训练数据、用户配置、系统运行库、release 交付物、模型权重或测试转储。
- 当前 shell 对系统临时目录的写权限仍受限。正常开发机控制台中的无环境变量复跑仍是发布前需要的人机环境验证；可写工作区下的 Release 测试已完整通过。
- 后续建议分批梳理 16k 行工作区改动并为版本号建立单一来源。它们涉及大量领域文件和发布语义，本次基础收口没有自动提交或批量重写。
