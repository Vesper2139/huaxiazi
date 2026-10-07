# 全量测试基线失败分类（2026-10-02）

## 结论摘要

在工作区内将 `TEMP`/`TMP`/`TMPDIR` 指向 `out/test-temp` 后，最近一次**完整结束**的全量复跑（运行 D）为：**1047 项，1028 通过、11 跳过、8 失败**。2026-10-03 当前清单为 1126 项；三次新全量复核均在 WPF/Win32 测试宿主崩溃时中止，不能把运行 D 当作当前快照的完整结果；详见运行 E、F。此前修复数据目录 fallback 后的历史完整结果为 1045 项、1020 通过、11 跳过、14 失败。最初直接使用默认 `%TEMP%` 的运行在 235 项后因访问拒绝导致测试主机崩溃，也不是有效全量结果。

在全量复跑中发现并修正 `DataDirectoryPolicy` 的真实缺陷：配置路径为空且 `verifyWritable=true` 时原逻辑直接信任默认数据根，不进行写入检查。若默认根不可用，应用仍把归档放到不可用根，出现 89 个 SQLite “unable to open database file” 失败。修正后应用数据根能正确选择受限会话临时回退根，相关 SQLite 失败消失；`DataRoot_FallsBackToWritableDirectory_WhenDefaultRootIsNotUsable` 从失败转为通过，全量失败从 98 降到 14。

最新 8 项不是同一根因：4 项发布测试与当前发布脚本/安装器产物约定不一致；3 项悬浮球窗口尺寸测试与实现声明的透明交互外壳尺寸冲突；1 项猫蝶好奇状态测试与皮肤当前显式的 `Curious -> Warning` 帧映射冲突。后三类需产品/测试契约复核，不能在本 AI 定制阶段擅自改动发布或视觉行为。

## 可复核运行记录

### 运行 A：默认临时目录

- 命令：`dotnet test Huaxiazi.Tests/Huaxiazi.Tests.csproj --no-restore --logger 'trx;LogFileName=stage0-classification.trx' --results-directory out/test-artifacts/stage0-classification-20261002`
- 当时 `$env:TEMP`/`$env:TMP` 为 `%LOCALAPPDATA%\Temp`。
- 测试在 235 项后终止（169 通过、66 失败），测试主机因向默认 Temp 创建目录时 `UnauthorizedAccessException` 崩溃；执行已中止，不能据此判定剩余用例。
- 这 66 项中，45 项直接报告 Temp 路径访问拒绝；21 项归档初始化时报告 SQLite Error 14。该次运行不是完整基线。

### 运行 B：工作区临时目录、缺陷修复前

- `TEMP`、`TMP`、`TMPDIR` 设为绝对路径 `out/test-temp`，其余命令与运行 A 相同；TRX：`out/test-artifacts/stage0-classification-workspace-temp-20261002/stage0-classification-workspace-temp.trx`。
- 完整执行 1045 项：936 通过、11 跳过、98 失败。
- 89 项为 SQLite 数据库无法打开，发生于没有注入独立数据根、会使用 `App.DefaultDataRoot` 的应用/WPF 测试。
- 另 9 项：发布脚本/安装器约定 4 项，悬浮球窗口尺寸 3 项，猫蝶 Curious 资源映射 1 项，默认数据目录 fallback 1 项。

### 运行 C：工作区临时目录、修复后

- 命令仍为完整 `dotnet test`，`--no-restore`，新 TRX：`out/test-artifacts/stage0-postfix-20261002/stage0-postfix.trx`；console log：`out/test-artifacts/stage0-postfix-20261002/console.log`。
- 汇总：1045 项，1020 通过、11 跳过、14 失败，退出码 1。
- 目录 fallback 定向复跑：`dotnet test Huaxiazi.Tests/Huaxiazi.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeDeliveryRegressionTests`，结果 1/1 通过。
- 盲评相关回归独立复跑：筛选 `FullyQualifiedName~BlindEvaluation`，结果 52/52 通过。

`out/test-artifacts` 和 `out/test-temp` 属于本机忽略目录，原始 TRX 不纳入源码版本控制。若需长期保存本轮原始证据，应从该工作区另行归档；本文保留命令、计数和失败分类，不包含用户内容或测试机密。

### 运行 D：工作区临时目录、设置保存测试隔离后

- 设置 `TEMP`、`TMP`、`TMPDIR` 的方式不变；TRX：`out/test-artifacts/stage0-post-isolation/stage0-post-isolation.trx`。
- 完整执行 1047 项：1028 通过、11 跳过、8 失败。总数比运行 C 多 2 项，来自之后新增的回归。
- `SettingsViewModelTests` 相关筛选独立复跑：55/55 通过。此前 6 个保存用例未把 `ConfigService` 路径重定向到可写目录；新增测试专用 `UseIsolatedConfigDirectory` 作用域，在用例结束后恢复原配置路径并删除临时文件。该改动只隔离测试，不改变产品保存路径或写入门禁。
- 最新 8 项失败仍为发布 4 项、悬浮球尺寸 3 项、猫蝶资产映射 1 项。须先确认正式产品契约，再决定修复实现或同步测试，不因测试失败本身推断哪一侧错误。

## 修复记录

### 运行 E：2026-10-03 当前工作区复核（两次均未完整结束）

- 当前测试清单为 **1125 项**（`dotnet test ... --list-tests`）；使用工作区 `out/test-temp`，不是系统 `%TEMP%`。
- 首次全量尝试执行到 802 项后中止：786 通过、11 跳过、5 失败。5 项为 4 项既有发布/交付契约断言分歧，及 1 项猫蝶 Curious 资源映射分歧；失败仍与运行 D 的已知类别一致。测试宿主随后在 `MS.Win32.HwndSubclass.SubclassWndProc` 发生 .NET 资源查找递归并 FailFast，故此结果不是全量结果。
- 第二次加 `--blame-crash` 留存 TRX、失败序列和转储；执行到 520 项后宿主以同一 WPF/Win32 子类窗口栈崩溃，结果为 519 通过、1 个既有 Curious 映射失败。Blame 标注崩溃时正在执行 `SettingsViewModelTests.TrySaveAsync_WhenConnectionSucceeds_PersistsAndMarksVerified`；该用例单独复跑 **1/1 通过**，因此不能据此认定它是崩溃根因。`Huaxiazi.Tests/TestRunConfig.cs` 已关闭 xUnit 并行执行，因此没有证据支持“测试类并发”是原因；失败序列显示该时段运行在 SettingsViewModel 测试类内，且其中存在多个自建 STA 线程。当前优先检查 WPF HWND/Dispatcher 的创建、消息泵与 STA 线程退出清理；两次崩溃前测试序号不同，具体触发因素仍未证实。
- `SettingsViewModelTests` 整类另行筛选 **55/55 通过**。这降低了“该类单独即可触发崩溃”的可能性，但不能排除前序测试留下的 WPF 进程级状态、消息泵退出时序或更广泛的宿主问题。
- AI 定制/盲评/候选执行/润色工作流筛选命令复跑为 **149/149 通过**。这只证明该筛选范围当前通过，不能替代完整测试结果或阶段 0 外部证据。
- TRX、blame 序列和 crash dump 放在本机忽略目录 `out/test-artifacts/current-full-20261003`；不纳入源码版本控制。下一步需要在隔离/诊断环境复现 WPF 测试宿主问题，再取得一次完整 1125 项结果；不要将 blame 的 in-flight 用例直接当成根因。

### 运行 F：Dispatcher teardown 契约修复后的复核

- 依据前述证据，`TestHelpers.ResetWpfApplication()` 原先会关闭窗口并清空 WPF `Application` 静态字段，但无窗口时不会关闭该应用拥有的 Dispatcher。新增真实 WPF lifecycle 回归，先观察到其失败（Dispatcher 未开始关闭），再补上 `Application.Shutdown()` 与必要时 `Dispatcher.InvokeShutdown()`；回归现为 **1/1 通过**。
- 之前可触发崩溃的 WPF/UI/Settings 组合复跑 **202 项**后正常结束：198 通过、4 失败；4 项仍是 3 个窗口外壳尺寸契约和 1 个 Curious 映射契约，不再崩溃。阶段 0 AI 定制筛选并加入新 lifecycle 用例后为 **150/150 通过**。
- 修复后全量复核仍在 859 项处崩溃：843 通过、11 跳过、5 失败后，测试宿主再次在 `MS.Win32.HwndSubclass.SubclassWndProc` 因 .NET 资源查找递归 FailFast；崩溃时正在执行的设置保存测试单独及整类筛选均通过。故 Dispatcher 清理修复补齐了已测得的辅助函数生命周期缺陷，但**没有消除全量宿主崩溃**，不能作为根因修复或全量结果。
- 本次运行清单由 `dotnet test ... --list-tests` 复核为 **1126 项**；全量完整性仍未通过。原始 TRX/blame/dump 仍只保存在本机 `out/test-artifacts` 忽略目录。

### 运行 G：最新 .NET 8 补丁运行时对照与 WPF 清理实验

- 为区分代码顺序问题与旧运行时问题，依据微软官方 .NET 8 下载/支持页，在 Git 忽略的 `out/test-dotnet` 安装 SDK **8.0.425** 与 Windows Desktop Runtime **8.0.31**；系统级 SDK/runtime 未更改。Microsoft 当前发布页列出 .NET 8.0.31（2026-09-08）为最新 8.0 servicing 版本；测试机原来只有 8.0.21。
- 使用本地 SDK/runtime 重跑相同 WPF/UI/Settings 过滤集：执行至 164 项（160 通过、4 个既有 UI 契约失败）后，仍在 `HwndSubclass.SubclassWndProc` 以 `Arg_NullReferenceException` 资源递归崩溃。故 8.0.21→8.0.31 的差异不是该崩溃的充分解释；尚不能排除 SDK、VSTest、环境或代码交互的其他影响。
- 调查发现 `WpfViewSmokeTests` 的 21 个 STA 线程没有对称清理 `Application.Current`。为其逐个加 finally shutdown 后，单类加 lifecycle tests 25/25 通过，但包含主题/WPF/Settings 的组合又崩溃；撤回这 21 处改动与依赖它的集成断言，避免保留没有组合验证支持的修复。前述 `TestHelpers.ResetWpfApplication` 的 Dispatcher shutdown 实现及其独立生命周期测试仍保留。
- 该运行仅是过滤集对照，不是 1126 项完整运行；当前全量基线仍未通过。`out/test-dotnet`、下载脚本、TRX 与转储均位于 Git 忽略的 `out/` 下。

`Services/DataDirectoryPolicy.cs`：空 `configuredPath` 且 `verifyWritable=false` 时仍快速返回默认根，保持配置加载只做语法路径校验；空路径且要求可写时改为进入与显式路径相同的存在性、文件冲突及写探针检查。这样 `App.DataRoot` 可在默认根不可写时进入既有 `RestrictedSessionDataRoot` 回退；显式目录无效时的拒绝语义不变。

该修复没有使当前受限执行环境可以写 Windows LocalAppData。6 个设置保存测试仍因它们要求把默认配置写回该系统位置而失败；这是当前测试主机/配置保存边界证据，需在可写的真实 Windows 用户配置目录环境下复验，或未来为测试注入配置目录。不能把这些失败报告成产品无缺陷的证据。

### 运行 H：WPF/设置组合缩小复现

- 采用工作区隔离的 SDK 8.0.425、Windows Desktop Runtime 8.0.31、`DOTNET_CLI_HOME=out/dotnet-cli-home` 和工作区 Temp，对现存 `current-full-20261003` 的 blame 顺序做小组复现；没有改测试或产品代码。
- 完整 `CompanionUiContractTests` 单类执行 36 项后正常结束（35 通过、1 个已知 Curious→Warning 资源映射断言失败）。完整 `SettingsViewModelTests` 与保存用例单独此前均通过。两类组合则在 `TrySaveAsync_WhenConnectionSucceeds_PersistsAndMarksVerified` 开始时复现 `MS.Win32.HwndSubclass.SubclassWndProc` / `Arg_NullReferenceException` 资源查找递归。
- 当前两次复现的宽过滤集合是**完整** `CompanionUiContractTests` + `SettingFieldToSameLoadedValue_DoesNotMarkHasChanges` + 设置保存用例；排除 Curious→Warning 失败用例后，该组合 37/37 通过且不崩。把集合收窄到“Curious→Warning 失败用例 + `MainWindow_InterruptedCollapseStillInvokesCompletionExactlyOnce` + 设置保存用例”时不崩（1 个已知映射失败、2 个通过）；加入字段用例后首次崩溃，但完全相同过滤集合立刻复跑又正常完成（1 失败、3 通过）。两次实际顺序不同：崩溃运行先执行失败用例、折叠用例、字段用例、保存用例；通过运行先执行两个 Settings 用例，再执行两个 Companion 用例。这是目前最小的**一次性崩溃组合**，不能称为稳定根因复现。
- 其他顺序对照：原序列前 9 个测试项（8 个独立 Companion 方法，含该失败项）+ 两条 Settings 用例可崩；其中失败项之前前三个独立方法 + 失败项 + 两条 Settings 用例不崩；失败项、`HoverFeedback_DoesNotOverrideBusyState`、`FloatingBall_UsesTheCompanionFaceAsItsPrimaryVisual`、`ExternalStateChangeCancelsPendingExpandCompletion`、`MainWindow_InterruptedCollapseStillInvokesCompletionExactlyOnce` + 两条 Settings 用例也不崩。因此当前证据支持**进程内执行顺序/状态交互**，尚不能归因于某一个断言、动画计时器或保存方法。另一拆分“`InvalidArchiveDate_UsesDedicatedValidationWithoutOverwritingLibraryStatus` + `FloatingBallDependentOptions_FollowMasterSwitch` + 保存用例”在 Companion 类后未崩溃。
- `SettingFieldToSameLoadedValue_DoesNotMarkHasChanges` 本身只构造 `SettingsViewModel` 并检查 `HasChanges`，当前证据不够解释其如何与 STA 保存及先前 WPF 测试相互作用。blame 指出的保存用例仍只表示崩溃时正在执行，不能据此认定该方法有缺陷。尚未定位可安全修复的具体清理缺口，因此没有修改代码。
- 原始结果和转储保存在 `out/test-artifacts/wpf-*20261003` 忽略目录。下一步需要受控固定用例顺序或重复运行相同的确定性工作流，区分顺序效应与偶发宿主故障；当前测试项目使用 xUnit 2.9.3，未发现自定义 case orderer，测试选择器本身不固定执行顺序。若无法稳定归因，比较分进程运行相关测试类的结果。运行 H 仍不是完整 1126 项基线，也不改变阶段 0 的外部数据准入结论。

### 运行 I：按测试类独立进程的完整清单复核

- 为验证跨类共享测试宿主是否为崩溃必要条件，使用同一工作区构建产物和 .NET 8.0.31 runtime，从 `--list-tests` 提取全部 1126 个用例、按 103 个测试类逐类启动独立 `dotnet test --no-build --no-restore` 进程；每类生成独立 TRX 与控制台日志，全部按序执行完毕。
- TRX 对账：**1126 total / 1115 executed / 1107 passed / 8 failed / 11 NotExecuted / 0 aborted**。11 个 `NotExecuted` 全部是 `ConfigServiceTests` 中已有的旧版本迁移测试跳过项。103 个进程均有 TRX，没有 `aborted`、crash dump 或 WPF 宿主崩溃。
- 8 个失败与既有分类一致：发布/安装契约 4 项（AcceptanceDefect 1、ReleaseSecurity 3），窗口尺寸 3 项，Curious→Warning 资产映射 1 项。没有新失败类别。AI/Provider/盲评/润色类的失败数为 0；此结果仍不是模型质量证据，也未发起 Provider 请求。
- 结论：类级进程隔离足以完成当前测试清单并避开宿主崩溃，支持“跨类共享进程的 WPF 状态/顺序交互”作为调查方向；它**不等同于**普通单进程全量通过，不能认定产品全量基线通过，也不能解释实际 WPF 根因。原始 103 份 TRX 与日志位于 `out/test-artifacts/class-isolated-20261003`（Git 忽略）。

### 运行 J：既有崩溃转储的托管栈复核

- 为读取既有 blame dump，在 Git 忽略目录 `out/diagnostic-tools` 安装本地 `dotnet-dump` 10.0.745401；分析时将 `TEMP`/`TMP` 指向工作区 `out/test-temp`，未安装系统级组件、未改项目依赖。
- 两份不同运行的转储（`current-full-20261003` 与 `wpf-companion-settings-20261003`）均显示相同致命栈：STA 线程进入 WPF `MS.Win32.HwndSubclass.SubclassWndProc` 后，运行时在构造/本地化 `NullReferenceException` 时又于 `CultureInfo.get_CurrentUICulture()` 出现空引用，递归至 `Environment.FailFast`。转储异常对象为 `System.ExecutionEngineException`。这说明它不是普通断言失败，而是在 WPF 原生窗口过程回调中触发 CLR FailFast。
- 首份转储同时有 xUnit 工作线程停在 `SettingsViewModelTests.TrySaveAsync_WhenConnectionSucceeds_PersistsAndMarksVerified()` 的 `Thread.Join()`；该栈只能说明测试当时在等待其 STA 工作线程，不能证明保存逻辑是起因。失败序列表明本次运行前执行过 ThemeSwap 与多项 Companion UI 测试，但转储没有给出能确认具体 Dispatcher/HWND 所属对象的证据。
- 调查从“单个失败用例”收窄为 STA/WPF 窗口过程中的异常处理或线程/Dispatcher 生命周期交互，但根因仍未定位；转储没有保留可解释的原始托管异常栈，不能据此断言是产品缺陷、测试清理缺陷或 .NET servicing 缺陷。未做代码更改。下一步应在受控小型宿主中记录每个 WPF STA 线程的 Dispatcher/窗口句柄创建与销毁，并对比有无 Application/窗口过程消息的确定序列；稳定复现前不进行生命周期补丁。
- Run I 的 103 份 TRX 重新逐文件 XML 汇总仍为 1126 total / 1115 executed / 1107 passed / 8 failed / 11 skipped / 0 aborted。11 个跳过项目来自 `ConfigServiceTests` 中已有 Skip 标注；TRX `Counters.notExecuted` 为 0，需用 `total - executed` 并对照逐类控制台日志确认。已由逐份 TRX counters 重建 `out/test-artifacts/class-isolated-20261003/summary.tsv`，修复字面 `` `t `` 表头并补齐 skipped 列；总数与 ConfigService 日志一致。

### 运行 K：最小 WPF STA / Dispatcher 生命周期探针

- 依据运行 J 的转储栈，在 Git 忽略的 `out/wpf-lifecycle-probe` 创建独立 `net8.0-windows` WPF 诊断程序；本机 SDK 8.0.425、Windows Desktop Runtime 8.0.31。程序每轮用新 STA 线程创建 `Application`/`Window`，记录 managed/OS thread ID、Apartment、Dispatcher 身份与 shutdown 状态、Window HWND 及窗口/应用/Dispatcher 生命周期事件；不记录业务文本或调用 Provider。源文件和两份 TSV 都仅在 `out/`。
- **显式关闭模式 100 轮：** 100 个 STA 线程、100 个 HWND 创建/关闭、100 次 Dispatcher `ShutdownStarted` 与 `ShutdownFinished`、100 个线程正常退出，0 异常。
- **只关闭窗口模式 100 轮：** 100 个 STA 线程、100 个 HWND 创建/关闭，100 次 `Application.Exit`，但 0 次 Dispatcher shutdown；STA 线程均退出、进程无崩溃。这证明在未运行 `Application.Run()` 的短命测试宿主中，仅关闭最后一个 Window 不能作为 Dispatcher 已关闭的证据；显式调用 `Application.Shutdown()`/`Dispatcher.InvokeShutdown()` 确实改变生命周期状态。它与既有 WpfViewSmoke 测试未做 Dispatcher teardown 的观察相符，但不证明该状态会导致运行 J 的 CLR FailFast。
- 两种模式均未复现 `HwndSubclass` 资源递归，因此未把该发现升格为根因，也未改项目源码。下一步将以同一探针加入实际 Companion/皮肤资源和短时动画/延迟回调，然后分层增加窗口创建序列；每次只改变一个变量，要求同一序列重复触发后才按 TDD 修复。探针结果只检查诊断行为，不能代替普通 1126 项宿主稳定性验证或阶段 0 AI 质量门槛。

### 运行 L：实际 Companion 控件与定时反馈回调

- 扩展仅位于 Git 忽略目录 `out/wpf-lifecycle-probe` 的探针，引用当前应用程序集、加载实际 CompanionFace 资源，并执行展开/点击反馈的 Dispatcher 定时回调。`same-app` 模式在一个显式关闭策略的 WPF Application、同一 STA/Dispatcher 上连续创建并关闭 100 个实际 CompanionFace 窗口；每轮泵送 20ms 后关闭窗口，再泵送 260ms 等待回调。第一版夹具使用 WPF 默认 `OnLastWindowClose`，导致 Application 在第一窗关闭时退出，第二轮创建控件被拒绝；该夹具错误已修正为 `OnExplicitShutdown`，不是产品缺陷。
- 修正后的 100 轮全部完成：100 HWND 创建/关闭、100 次窗口周期、100 次点击回调、1 次 Dispatcher shutdown 开始/完成、`run-complete` 1 次，0 次异常。展开回调未执行，因为每轮窗口在其延迟触发前已卸载，符合控件 `IsLoaded` 防护；点击回调在窗口关闭后仍完成，说明短定时器回调会跨越窗口关闭时点。
- 该实际控件/资源/定时器序列仍未复现 `HwndSubclass` 资源递归，因此只能排除这条单 Dispatcher 确定序列作为充分复现条件，不能排除多 Dispatcher/Application 累积、测试顺序、WPF 原生窗口或测试宿主交互。未改产品或测试源码。探针构建 0 错误，但 NuGet Audit 有 2 个 `NU1900` 缓存权限警告；详细跟踪保存在忽略目录 `out/test-artifacts/class-isolated-20261003/companion-same-app-100.tsv`。下一步应对照原始失败顺序做逐窗口/Dispatcher 事件记录，或继续缩小多 STA 与 Application 静态重置变量；稳定复现前不做生命周期修复。此诊断不改变全量测试状态或阶段 0 AI 评测准入结论。

### 运行 M：跨 STA 重建 Application 后装载 Companion 的最小复现

- 为区分 Dispatcher 收尾与 WPF `Application` 单例状态，在忽略目录 `out/wpf-lifecycle-probe` 扩展探针；每轮在新 STA 创建 `Application`、构造实际 `CompanionFace` 并创建/关闭窗口，然后按当前 `TestHelpers.ResetWpfApplication()` 顺序调用 `Application.Shutdown()`、`Dispatcher.InvokeShutdown()` 并反射清空 `_appInstance`、`_appCreatedInThisAppDomain`、`_isShuttingDown`。随后第二个 STA 再次创建 `Application` 并装载同一应用控件。当前桌面 shell 使用 SDK 8.0.121 / Desktop Runtime 8.0.21；这与既有全量崩溃时使用过的旧 runtime 一致，不据此推断 8.0.31 行为。
- 固定两轮序列重复两次均在第二个 STA 构造 `CompanionFace` 时进程直接退出（probe exit `-2146232797`），发生在 `Application` 创建成功和控件资源初始化开始之后。设置 .NET heap minidump 后取得 42,952,228 字节 dump；`dotnet-dump` 托管栈为 `Environment.FailFast` → `System.Windows.Application.GetResourcePackage(Uri)` → `GetResourceOrContentPart(Uri)` → `LoadComponent(Object, Uri)` → Companion XAML 初始化。调用栈与原全量崩溃记录的 `HwndSubclass.SubclassWndProc` / culture 本地化递归不同，因此这是一个稳定的相邻测试夹具失败模式，**不能宣称已证明全量宿主崩溃根因**。
- 单变量控制：同一个 STA/Application 上重复实际 Companion 窗口是 Run L 的 100/100 成功对照；不创建/不重置 Application 的双 STA 探针 2/2 完成，但控件构造后 `Application.Current` 会非空，故它不是已验证的通用方案；保留跨线程的单一 Application 并在第二 STA 访问 `Application.Exit` 则得到预期 Dispatcher 线程亲和 `InvalidOperationException`。显式合并主题字典的第二轮另出现 `NotSupportedException` pack-URI 错误，已与 heap dump 的组件初始化 FailFast 区分。
- 源码全局检索未发现写入 `CurrentUICulture`、`CurrentCulture` 或 `DefaultThreadCurrentUICulture` 的代码；现有 culture 命中仅为不变文化格式化和当前文化排序。该负结果不能解释 CLR 自身在异常本地化中的空引用。
- 判定：当前 `TestHelpers` 私自重置 WPF 进程级单例并在同一 AppDomain 跨 STA 重建，是可独立稳定触发 XAML pack-resource FailFast 的测试基础设施缺陷候选。由于修复方案会影响多个 WPF 测试类（共享长期 STA/Dispatcher，或将会创建 Application 的测试移至独立进程），本轮没有仓促更改 helper。下一步先按 TDD 增加**子进程隔离**的实际生命周期回归，使预期崩溃不杀死主 testhost；随后比较统一 STA 代理与每个 WPF 用例独立进程两条路线的改动范围，并以已知 UI 测试覆盖/成本决定实现。原始 trace 与 dump 位于 Git 忽略的 `out/test-artifacts/wpf-companion-multi-sta-minidump-20261003` 和 `wpf-companion-multi-sta-heapdump-20261003`。这不改变全量测试基线或阶段 0 AI 评测门槛。

## 余下失败逐类清单

| 类别 | 数量 | 具体用例/观察 | 判定与下一步 |
|---|---:|---|---|
| 执行环境 LocalAppData 不可写 | 6（已解决） | 历史运行 C 中 `SettingsViewModelTests` 的 TrySave/密钥/模型映射保存用例曾因默认配置路径不可写而失败 | 运行 D 已使用可恢复的临时配置目录隔离测试；相关类 55/55 通过。产品保存仍写入实际用户配置路径，未放宽写入门禁。 |
| 发布契约断言与当前脚本不一致 | 4 | `AcceptanceDefectTests.DeliveryScript_ProducesOnlyTwoStableClientArtifacts`；`ReleaseSecurityTests.Installer_UsesInnoSetupAndPerUserDefault`、`PublishScript_UsesDedicatedLockedRuntimeGraph`、`ReleaseDoesNotShipScriptInstaller` | 测试预期不含 `IncludeNativeLibrariesForSelfExtract=true`、要求 `UseSetupLdr=no`、要求把某种 locked-mode 参数写法放在 publish 脚本中、且拒绝 launcher；当前脚本包含自解压原生库、使用 Setup Loader、restore/publish 分段传参数并交付 launcher。此处是契约分歧，不直接判为陈旧；需对照正式发布需求和真实安装/升级矩阵统一脚本与断言。 |
| 悬浮球窗口几何契约冲突 | 3 | `FloatingBallDisplayContractTests.CollapsedCompanion_WindowHitTargetAndViewportStayExactlySynchronized` 的 28/44/72 DIP 用例分别观察到窗口 44/60/88 DIP | `CompanionDisplayMetrics.ResolveLayout` 明确将窗口外壳设为可视尺寸 +16 DIP，HitTarget/Viewport 使用可视尺寸；测试把窗口宽高也要求等于 requested size。确认交互外壳究竟应保留 16 DIP 还是与可视区域齐平，再更新测试或实现。 |
| 猫蝶 Curious 资产映射冲突 | 1 | `CompanionFace_MaoDieHoverLoadsTheIndependentCuriousAsset` 实际加载 `Resources/Skins/MaoDie/States/Warning.png` | `SkinService.RegisterBuiltInSkins` 显式设置 `frameStateOverrides[Curious]=Warning`，但 `Curious.png` 也存在且测试期待独立素材。需由皮肤设计确认映射意图后再改；本阶段不改视觉行为。 |

## 阶段 0 对本项目计划的影响

这轮修复了数据根可写检查/回退，并将设置保存测试改为使用可写临时配置目录；其余发布/UI/素材契约失败仍待正式产品约定裁决。没有改 AI 生成、Provider、路由、用户偏好或模型权重；没有运行任何云 Provider。上述变更没有提供模型质量证据。阶段 0 仍缺授权盲评集、可核验的独立原始评审证据及冻结条件下的云/本地产品基线。


### 运行 N：testhost 中三次重建 Application 的子进程复核

- 为防止进程 FailFast 杀死主测试宿主，先把运行 M 序列封装成子 `dotnet test` 进程，并设置崩溃 dump 与完成标记。三次“新 STA → 新 Application → 实际 CompanionFace XAML → 窗口/动画 → Shutdown 并反射重置 WPF 私有静态字段”中，前两次完成，第三次在 `Application.GetResourcePackage(Uri)` / `CompanionFace.InitializeComponent()` 触发 `Environment.FailFast`；测试宿主捕获到退出码 1，未被子进程带崩。复核可重复 M 的夹具缺陷。
- 根因范围经官方契约再收窄：Microsoft 文档说明 `Application` 每个 AppDomain 只应有一个实例，并说明 `ResourceAssembly` 只能在 WPF 无法发现入口程序集时设置；testhost 有入口程序集，所以把该属性改为产品程序集不是合法的回避方法。来源：[Application 类](https://learn.microsoft.com/dotnet/api/system.windows.application?view=windowsdesktop-7.0)、[ResourceAssembly](https://learn.microsoft.com/dotnet/api/system.windows.application.resourceassembly?view=windowsdesktop-10.0)。
- 纠正：该子进程序列刻意违反 WPF 单例生命周期约束，证明的是测试 helper 当前的反射重置方式不安全，不是产品应用正常单 `Application` 生命周期缺陷，也不能直接代表原始 `HwndSubclass` 崩溃。三周期子进程测试已从默认测试源码移除，避免将无效生命周期要求固化为项目回归；只保留捕获证据。后续若要消除此类夹具风险，应将 WPF 测试改为进程内单一、长期 STA/Dispatcher 执行，或按测试进程隔离，需比较迁移范围并先建立同一生命周期的回归。阶段 0 的盲评与云/本地质量门槛不变。

### 运行 O：可复跑的逐测试类隔离基线（2026-10-03）

- **问题与约束：** 常规全量 `dotnet test` 曾在 WPF `HwndSubclass` 路径触发 testhost FailFast；xUnit 并行已关闭，不能仅凭单次 blame 测试认定原因。逐类进程隔离可避免一个 WPF 崩溃带走其他类别，但启动 103 个进程有约 2.5 分钟额外耗时，且不能修复/解释普通全量宿主崩溃。
- **实现：** 新增 `tools/test-isolation/run-isolated-tests.ps1`，使用实际 `dotnet test --list-tests` 发现类，以点号结尾的 `FullyQualifiedName~Namespace.Class.` 过滤器避免同前缀类串扰；每类启动独立 testhost，保存独立 TRX 和完整 dotnet 日志，汇总 `summary.tsv`，任何测试失败、aborted、TRX 缺失或 CLI 错误均返回失败状态。默认在项目 `out/test-temp` 运行，恢复进程原有 TEMP/TMP/TMPDIR。`-ClassName` 可限定类别，`-ListOnly` 仅列出类别，`-OutputDirectory` 指定新工件目录。
- **TDD 与纠错：** 5 项 Pester helper 验收先新增；红测分别暴露工具缺失、TRX 未执行计数解析缺口，修复后 **5/5 通过**。第一次真实全类运行揭示 VSTest Counters 的 `notExecuted=0`，但 TRX 中实际含 11 个 `UnitTestResult outcome="NotExecuted"`；因此汇总器现在兼读逐测试结果并按两种来源的较大值报告跳过数。第二次全类复跑已验证修正后的口径。
- **最新结果：** 103/103 类启动并正常结束；1,132 总项 = 1,113 通过 + 8 失败 + 11 跳过，0 aborted。失败精确落在 4 类：`AcceptanceDefectTests` 的 1 项发布产物静态契约、`ReleaseSecurityTests` 的 3 项安装/发布参数契约、`FloatingBallDisplayContractTests` 的 3 项窗口外壳比可视尺寸多 16 DIP 的断言、`CompanionUiContractTests` 的 1 项猫蝶 Curious→Warning 资产映射。逐类隔离复跑此前在常规宿主崩溃范围内的类时未再次崩溃；这证明隔离路径可取得全类别结果，不证明 8 项哪侧契约正确，也不构成普通全量 `dotnet test` 的通过结果。
- **产物：** 忽略目录 `out/test-artifacts/class-isolated-20261003-runner-final/` 含 summary、103 份 TRX 和日志；工具单测为 `tools/test-isolation/tests/TestIsolation.Tests.ps1`。复跑命令：`& 'tools/test-isolation/run-isolated-tests.ps1' -NoRestore -OutputDirectory 'out/test-artifacts/<unique-run-name>'`。`-NoRestore` 前提是依赖已还原；默认不传时由 `dotnet test --list-tests` 执行构建/还原。
- **边界与后续：** 这项工具提升的是测试结果可复跑性，不提升模型能力，也不关闭阶段 0 门槛。P2 下一步应核对并裁决 4 类产品契约，不应为消除红项而单方面改实现或断言；WPF 普通 testhost FailFast 的根因仍未证实。AI 盲评仍缺真实授权样本、外部独立评审证据及冻结条件云/本地产品基线。

### 运行 P：WPF STA teardown 修复后的全量隔离基线（2026-10-03）

- **根因范围与修复：** `WpfViewSmokeTests.MainWindow_UiScaleResizesTheViewportWithItsContent` 是双数据用例；旧清理只关闭 Dispatcher，第一个用例通过后，后续 WPF `HwndSubclass.SubclassWndProc` 回调可能在短命 STA 测试线程退出时 FailFast。将这 21 个 STA 用例的 finally 清理改为 `TestHelpers.ResetWpfApplication()`，使当前线程拥有的窗口关闭、Application/Dispatcher 生命周期结束并清理 WPF 静态单例状态。该类别此前 1/23 后崩溃；修复后该理论 2/2，通过全类 23/23 及全量隔离复跑验证。`SettingsViewModelTests` 7 个 STA 线程也有 Dispatcher finally，整类已 55/55，连续复跑 3 次均无中止。
- **隔离 runner 修正：** 测试失败向 stderr 输出 `[FAIL]` 时，PowerShell 全局 `ErrorActionPreference=Stop` 会令 runner 提前终止，无法汇总后续类别。runner 现只在执行 `dotnet test` 时暂用 `Continue` 并恢复原设置；用含真实断言失败的 `AcceptanceDefectTests` 验证仍能完整输出 7/8 并以非零码退出。
- **结果：** 103 类、1,133 项；1,118 通过、4 失败、11 跳过、**0 aborted**。原 8 个契约差异中，悬浮球几何 3 项已按 `CompanionDisplayMetrics` 既有 +16 DIP 外壳定义修正断言，猫蝶 Curious 1 项已让 `SkinService` 使用既有独立 Curious 资产；分别复验 16/16 与 36/36。剩余 4 项仍为发布/安装契约争议：`AcceptanceDefectTests.DeliveryScript_ProducesOnlyTwoStableClientArtifacts`，以及 `ReleaseSecurityTests.Installer_UsesInnoSetupAndPerUserDefault`、`PublishScript_UsesDedicatedLockedRuntimeGraph`、`ReleaseDoesNotShipScriptInstaller`。
- **产物与边界：** `out/test-artifacts/full-isolated-after-wpf-reset-20261003-r3/` 保存本轮 summary、TRX 和日志。该结果证明逐类隔离下全类别正常完成，不能等价为单 testhost 普通 `dotnet test` 通过，也不证明发布争议应由哪一方让步。AI 生成行为仍冻结，阶段 0 仍未通过。

### 运行 Q：普通单 testhost 全量回归复核（2026-10-03）

- 以与 Run P 相同源码执行常规单 testhost `dotnet test --no-restore`，结果 **1,118 通过、4 失败、11 跳过、0 中止**，耗时约 1 分 31 秒。此前 `WpfViewSmokeTests` 及其相邻 WPF 用例可能触发的 `HwndSubclass` FailFast 本次未复现；4 个失败与 Run P 完全一致，分别为 `DeliveryScript_ProducesOnlyTwoStableClientArtifacts`、`Installer_UsesInnoSetupAndPerUserDefault`、`PublishScript_UsesDedicatedLockedRuntimeGraph`、`ReleaseDoesNotShipScriptInstaller`。
- 这比逐类结果更强地验证了当前源码下 WPF testhost 可正常结束，但不能泛化到其他 .NET/WPF runtime 或 Windows 硬件；当前 SDK/runtime 见测试工件 metadata，发布 Windows 硬件验收仍未做。失败均为发布/安装文档与实际脚本的静态交付约定分歧；AI 阶段门槛及生成行为不受影响。
- TRX 与日志：`out/test-artifacts/standard-full-after-wpf-reset-20261003/standard-full-20261003.trx`。
