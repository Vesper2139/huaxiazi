# 话匣子 AI 定制化能力：历史源码评估与实施记录

> **历史事件日志，不是当前状态页。** 每条记录只描述其发生时的代码和验证结果。当前进度以 [STATUS.md](../STATUS.md) 为准，当前任务顺序以 [ROADMAP.md](../ROADMAP.md) 为准。

更新时间：2026-10-07

## 2026-10-07：OpenRouter 型号级参数元数据与结构化路由闭环

| 子任务 | 结果 |
|---|---|
| 当前节点与问题 | 阶段 1 Provider 能力映射。OpenRouter `/api/v1/models` 返回模型级 `supported_parameters`，原目录服务此前只保留型号 ID/名称，导致应用无法按所选 OpenRouter 型号收敛采样和 JSON Schema 请求。当前公开文档确认结构化输出能力按 provider endpoint 变化；endpoint 明细 API 文档示例对普通 API Key 返回 403（仅 Management Key 可调用），因此不能将管理密钥作为产品普通配置要求。 |
| 技术判断与约束 | 读取模型目录作为候选能力信号；只保留 `response_format`、`structured_outputs`、`max_tokens`、`temperature`、`top_p` 白名单字段，模型/Provider 名称不推断 reasoning。只有快照绑定的型号与本次实际 model ID 精确匹配，且目录同时声明 `response_format`、`structured_outputs` 和 `max_tokens`，才发原生 JSON Schema；同时设置 `provider.require_parameters=true`，让 OpenRouter 只路由至支持请求中全部参数的端点。API/模型端仍可能有 Schema 特性差异，因此本地契约校验不移除；无快照、字段缺失、型号不匹配时回到提示内 Schema 与本地校验。 |
| 实现与兼容 | `ModelDefinition.SupportedParameters` 扩充目录 DTO，`ProviderProfile` 以可选的 model ID + 白名单 List 存储快照，旧配置缺少字段仍使用空/未知默认；`Clone()` 做深拷贝。设置页刷新当前型号时同步快照，切换到已加载目录型号时同步对应参数，目录刷新后当前型号不再出现则清除旧快照；配置保存前通过 `HasChanges` 明确标脏。`AIService` 仅按能力标记构造 JSON Schema 与 `provider.require_parameters`，模型级目录不直接当 endpoint 证明。目录不会包含会话正文、不新增遥测。 |
| TDD 与验证 | 初始目录测试以缺失 `SupportedParameters` 失败；集成请求测试以缺失 profile snapshot 字段失败；设置快照测试以未更新型号能力失败；过期快照测试以保留被目录移除型号的 snapshot 失败。复核官方结构化输出说明后又添加反例：仅有 `response_format` 与 `max_tokens`、缺少 `structured_outputs` 时必须走提示 Schema；该用例在改动前按预期失败。又用变异回归确认型号 ID 不匹配时必须拒绝复用快照。相关筛选最终 **277/277 通过**。首次全量运行因当前系统 `%TEMP%` 不允许创建目录而中止；将该测试进程的 `TEMP`/`TMP` 临时指向工作区可写临时目录并串行运行后，全量 **1,835 通过、11 跳过、0 失败（1,846 总计）**。 |
| 收益、限制及下一步 | 可按目录中明确支持的采样参数避免发送模型不支持字段；Schema 能力候选由 2 个改为同时要求 3 个目录字段，并由 OpenRouter 路由器执行 endpoint 级过滤。预期减少“参数被静默忽略”与不支持端点误路由；收益是请求契约正确性，不是中文生成质量提升。模型目录快照只在用户刷新后更新，资料可能变化；本地响应契约仍是最终校验。下一项继续按官方证据盘点其余尚未映射 Provider，并接续阶段 1 共用生成契约；盲评质量、阶段 2 自动路由、阶段 4 干净 Windows 兼容性仍需各自实证。 |

依据：[OpenRouter Models API](https://openrouter.ai/api/v1/models)、[Structured Outputs](https://openrouter.ai/docs/guides/features/structured-outputs)、[Provider Routing](https://openrouter.ai/docs/guides/routing/provider-selection)、[Endpoint list API](https://openrouter.ai/docs/api/api-reference/endpoints/list-all-endpoints-for-a-model)。

## 2026-10-06：按已接受输出风格提供语气偏好候选

| 子任务 | 结果 |
|---|---|
| 当前节点与问题 | 阶段 3 个性化闭环。系统已按任务/场景保存接受/拒绝后的输出风格计数，但此前只据长度编辑和固定套话删除提供候选，没有利用持续接受的显式风格选择建议 `preferredTone`。 |
| 输入、方案与约束 | 输入为不含正文的 `acceptedOutputStyles` / `rejectedOutputStyles`、当前任务/场景及已确认/忽略偏好。只有同一范围中单一风格至少 3 次与已接受结果关联、没有任何拒绝/撤销风格信号，才形成低置信候选；“专业/正式”映射到 `professional`，“亲切”映射到 `warm`。自然、简洁、克制及混合风格不作推断，避免超出已有偏好值语义。已有确认偏好和已忽略候选抑制重复建议。3 次是本地产品抑噪规则，未经阶段 0 盲评校准。 |
| 改动与数据流 | `ExpressionPreferenceSuggestionService.GetToneCandidate` 产出按 scope 隔离的候选；设置页新增摘要、载入和忽略操作。载入只改当前编辑草稿；沿用既有 `user-confirmed` 保存机制后才进入生成上下文；忽略项写入本机 `IgnoredSuggestionKeys`。不记录新内容、不发送请求、不增加遥测或训练数据。 |
| 验收与量化 | 服务层覆盖单一风格、阈值、混合/拒绝/已确认/忽略和 scope；WPF 设置回归覆盖载入为草稿、保存后确认及按范围忽略；视图 smoke 一并通过。联合筛选 **38/38 通过**；主 WPF build **0 warning / 0 error**。合成统计下确认前新增进入 prompt 次数 **0**；确认后使用既有提示路径，不新增模型调用。 |
| 自审、边界与衔接 | 该候选把“正式/专业/亲切”映射到当前仅有的两个结构化语气值；映射只作建议，必须用户确认。它证明设置/偏好状态行为，不证明偏好遵循率或成稿质量提高。曾假设官方平台 profile 可能指向代理地址并据此起草能力继承测试；实测 `AIService` 的 `ProviderEndpointPolicy` 在发请求前已拒绝这类 profile，测试只撞到既有安全拒绝。该假设不成立，测试已撤销，没有改 endpoint/capability 代码。阶段 0 仍缺正式授权盲评与独立评审证据。下一步先接收人工填写的本地候选审阅表，把明确指出的语气/事实问题转为下一条提示或校验改进；评审未完成时继续不依赖样本的阶段 4 运行时清洁环境/依赖检查，不把工程回归当作质量收益。 |

实现涉及：[语气候选服务](../Services/ExpressionPreferenceSuggestionService.cs)、[设置 ViewModel](../ViewModels/SettingsViewModel.cs)、[表达设置页](../Views/ExpressionAbilitySettingsView.xaml)、服务及设置回归。

## 2026-10-06：候选稿人审结果校验与导入

| 子任务 | 结果 |
|---|---|
| 阶段与输入 | 阶段 4 内部质量反馈闭环。已有 13 条 Vulkan 候选稿审查 CSV，但空表目前既无法证明审查已完成，也缺少可靠的人工反馈导回路径。输入是候选 JSONL、审查 CSV 和带候选文件 SHA-256 的 review metadata。 |
| 实现 | 新增 `DatasetBuilder/LocalModelCandidateReviewImporter.cs`，提供 `local-model-candidate-review-validate` 和 `local-model-candidate-review-import`。逐行绑定 case/family、输入、上下文、候选类型和候选文本；校验候选包 SHA-256、评分值、完整覆盖、人工审阅者标识、严格 UTC 时间与理由；禁止包内出现 reference/expected/model/自动门禁字段。有效导入只写目录外 create-only JSONL，保存候选包哈希、人工分项评分与理由；不复制候选正文，不晋升到 gold 或阶段 0，且 `identity_verified=false`。 |
| 审查表同步 | 运行器生成 CSV 增加 `reviewer_id`、`reviewed_at_utc`、`rationale` 列；元数据增加 schema 版本和 JSONL 哈希绑定。审查说明写清 1–5 评分、`pass/issue/not_applicable`、人工身份及理由填写格式。审查包路径：[CSV](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/review-packet/candidate-review.csv)、[说明](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/review-packet/README.md)、[机器记录](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/review-packet/candidate-review.jsonl)。空白 CSV 经 DatasetBuilder CLI 校验得到 `valid=false, reviewed_count=0, phase_0_gate_contribution=0`，唯一问题是人工尚未填写各评分/身份/时间/理由，candidate hash 正确且无漂移；这是当前预期状态。 |
| 验收与全量回归 | 新增测试先因导入器不存在而编译失败；实现后 **4/4** 通过，覆盖有效含逗号/换行 CSV、源文本篡改、reference 字段泄漏、不完整/无效/过期审查、目录边界和 create-only 导入。结构化生成工作流测试 **10/10** 通过。最新全量测试为 **1,675 通过、11 跳过、0 失败（共 1,686）**；此前 4 项发布断言失败已通过发布脚本修正收敛。Release build **0 warning / 0 error**，完整 `publish.ps1 -AllowUnsigned -RequireInstaller -SkipTests` 已运行成功并逐项验证交付物哈希；详见“发布交付契约与恢复锁定”记录。 |
| 下一步与边界 | 当前唯一候选审阅 CSV 有 13 行且所有人工评分、审阅者、UTC 时间和理由字段均为空；DatasetBuilder CLI 当前实测 `valid=false, reviewed_count=0, phase_0_gate_contribution=0`。不得由 AI 替代人类评审，也不把这 13 条加入正式盲评或训练。收到人工填写副本后运行 validate，再 import 到新目录；随后按人工指出的问题类别调整提示/上下文并复跑同一开发切片。候选输出仍属合成 development 数据，不是正式盲评证据。 |

## 2026-10-06：13 族本地候选稿人工审查包

| 子任务 | 结果 |
|---|---|
| 阶段、缺口与输入 | 阶段 4，补齐业务质量观察的人工审查入口。前一批探针只保存内容无关诊断，无法检视成稿本身。使用同一 `polish-regression-v1/cases.jsonl` 的 13 条 development 记录；运行 DTO 仅反序列化输入与上下文，不包含 `reference_output`、`expected_decision` 等属性。 |
| 产物与边界 | 新一轮仍强制 actual backend 为 Vulkan，生成 13 条候选审查记录。审查包提供 UTF-8 BOM CSV、JSONL 原始记录、审查说明和元数据；每行含样本输入、场景上下文、候选成稿/澄清问题及空白人工评分栏。模型名、期望决策、参考成稿和自动质量门禁结果不进入 reviewer packet；模型和运行参数只在审查包外的实验元数据中。来源为项目合成遗留 development 样本，非真实用户数据、非正式盲评、不得计入阶段 0。 |
| 实际工作流结果 | 13 个唯一输入/语义族，13 个本地 Vulkan 模型请求，工作流失败 0；final **10**，澄清 **3**，final 自动门禁通过 **10/10**。CSV 解析验证 **13 行**，13 个 case ID 唯一，评分栏均空；终检确认 `reference_output`、`expected_decision` 和 `model` 字段均不在 CSV，元数据亦标明 references/expected decisions 未包含。评分包尚未由人工填写，因此没有事实保留、语气、直接可用率或用户偏好收益数据。 |
| 第二轮性能观察与取舍 | Qwen3-4B Q4_K_M、llama.cpp b11424 Vulkan、同一固定参数（temperature `0.4`、top_p `1.0`、max_tokens `512`、seed `42`、repeat_penalty `1.17`、ctx `4096`、batch `512`、CPU threads `0`、KeepLoaded）。13 请求时延 p50/p95 **8,154.7/9,846.4ms**，首请求 **9,846.4ms (n=1)**，其后 12 次 p50/p95 **8,105.9/9,745.5ms**；runtime ready **4,276.6ms (n=1)**；峰值进程工作集 **3,220,111,360 bytes**；输出速率 p50/p95 **52.00/53.29 tokens/s**。与前次同 13 族探针 p50/p95 **18,020.6/184,182.1ms**、ready **28,836.1ms** 相比差异明显，不能视为代码性能收益；两批环境/GPU 负载不受控且 n=2，现阶段结论是性能波动显著、归因未明。下一次性能采样须记录统一的 GPU 占用快照并增加独立启动轮数，再讨论默认参数或运行时策略。 |
| 自审与验收 | 候选稿存留是为人工质量审查补足证据链；测试语料不含真实用户内容。包生成器先在 CSV 渲染时因旧 JSONL 中 null 评分值失败，已将 null 安全映射为空单元格；修正后 harness build **0 warning / 0 error**，对既有候选 JSONL 重渲染成功，CSV 重读 **13/13**，唯一 ID/语义族、评分留空及禁用字段检查通过。候选模型只负责起草，AI 不对同批文字评分。 |
| 产物与下一步 | [人工审查 CSV](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/review-packet/candidate-review.csv)，SHA-256 `4AED33653D30B96DD2C0E796C2BAB27B317C682FD60C25CA7D00D31F0A7E9E37`；[审查说明](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/review-packet/README.md)；[JSONL 原始记录](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/review-packet/candidate-review.jsonl)，SHA-256 `296341152A1C827B5E2160B1CE94F4AF3DBB9652DFD761AD173DA6EADB32914C`；[本轮诊断](../out/test-artifacts/local-model-quality-dev-20261006/review-run-20261006T115546Z/diagnostics/generation-diagnostics-summary.json)；[本轮运行器](../out/test-artifacts/local-model-quality-dev-20261006/Program.cs)。下一步将采用项目已有的人审校验/导入约定，接入这份 CSV 的事实、要求、语气和直接可用审查结果；人工审阅提交后，再将失败类别回馈到提示与上下文设计。 |

## 2026-10-06：本地健康探测超时恢复与 13 族工作流探针

| 子任务 | 结果 |
|---|---|
| 阶段、问题与输入 | 阶段 4，本地运行时启动稳定性及内部场景工作流检查。前一轮 Vulkan 质量探针未进入业务样本：`/health` 请求触发 `HttpClient.Timeout=2s` 的 `TaskCanceledException`，启动流程把健康探测的单次超时升级为候选 runtime 启动失败。输入包括 `LocalRuntimeManager.WaitForReadyAsync`、本地运行时测试和 `polish-regression-v1` 的 13 条 development 切片。 |
| 实现与取舍 | 把就绪等待逻辑抽为 `internal static WaitForReadyAsync(HttpClient, endpoint, timeout, token)`，生产启动仍使用 30 秒总期限和 2 秒单次 HttpClient 超时。捕获 `OperationCanceledException` 的条件是调用方 token 尚未取消：HttpClient 内部超时继续按 100ms 间隔重试；用户取消立即传播。这样区分“健康探测暂时未就绪”与“启动操作已被用户取消”，不会吞掉用户取消或改变 30 秒总启动期限。 |
| 回归与构建 | 新回归先因被测方法尚不存在而编译失败；加入超时后第二次健康响应成功、调用方取消不重试两例后，相关测试 **2/2** 通过；完整 `LocalRuntimeTests` **19/19** 通过；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**。 |
| 实际 backend 预检 | 运行内部 Vulkan 预检，受管 runtime 报告 `actual_backend=Vulkan` 后才执行样本；整批请求保持 ManagedLocal/Vulkan，没有转为 CPU，也没有外部 API 请求。 |
| 工作流观察 | 13 条输入、13 个语义族、13 个输入均不重复；共 13 次本地模型请求、13 个工作流，失败 0。10 个工作流产出 final，当前自动输出契约校验 **10/10**、应用现有质量门禁 **10/10**、校验问题数 0；3 个工作流在生成前请求澄清（模型请求数为 0）；2 个 final 工作流经过修复。这里的门禁只说明应用内契约与现有验证器通过，不等于事实保真、语气质量或人工可用率，也不是基于参考答案的评分。数据标记为 `project_synthetic_legacy`、`human_status=unreviewed`；未读取参考输出、未保存生成正文；`external_api_requests=0`、`phase_0_gate_contribution=0`。 |
| 性能与解释边界 | 这 13 次业务请求的时延 p50 **18,020.6ms**、nearest-rank p95 **184,182.1ms**；运行时 ready **28,836.1ms（n=1）**；运行时启动后首个业务请求 **184,182.1ms（n=1）**，其后 12 次 p50/p95 **16,508.9/27,301.3ms**。本批进程峰值工作集 **4,384,546,816 bytes**，输出速率 p50/p95 **24.05/48.69 tokens/s**。首请求远慢于上一组固定短请求（Vulkan 首请求约 6.2 秒），说明业务工作流的提示/首轮推理负载不能用固定短请求基准替代；该批运行时启动 n=1，GPU 当时有其他高占用，不能把差异归因于某个单一因素。样本太小且无正文盲评，不作产品质量或设备性能结论。 |
| 证据、偏差复核与下一步 | 内部运行摘要：[development-run.json](../out/test-artifacts/local-model-quality-dev-20261006/development-run.json)，SHA-256 `E5D9E9B60614C94C1CEDF7B327596498690841C5EA9FCF5581E8298CB6C789B1`；无内容诊断摘要：[generation-diagnostics-summary.json](../out/test-artifacts/local-model-quality-dev-20261006/diagnostics/generation-diagnostics-summary.json)，SHA-256 `82687CB8CDFE0FF6F4232FDAD4F622E68C3428B57609ECE19225BDDF918FE178`；运行器：[Program.cs](../out/test-artifacts/local-model-quality-dev-20261006/Program.cs)。实现符合阶段 4 稳定性和诊断目标；阶段 0 仍未满足，质量结论保持未验证。下一步应在相同提示下为这些开发样本建立人工可审查但不污染正式盲评的输出审查包，先确认输出留存/授权边界，再用第二个负载批次区分首请求慢来自 runtime、首次推理还是任务提示长度；不因此启动 LoRA/SFT。 |

## 2026-10-06：本地诊断区分启动后首请求与后续请求

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 4 性能观测细分。上一轮 CPU/Vulkan 各 10 次中，p95 都是样本内首个生成请求；诊断只有 runtime 启动时间而不能区分首次推理与后续热请求，难以定位请求尾延迟来自哪里。输入为 `LocalRuntimeManager` 当前每个进程仅一次的启动观测、请求日志与固定性能探针。 |
| 决策与改动 | 不按响应时延阈值猜冷/热状态；在受管 runtime 中用首个请求状态标记每次 HTTP 尝试。`LocalRuntimeMetrics` 增加 `IsFirstRequestAfterStart`，`ProviderRequestTelemetry`/本机请求条目增加可空 `IsFirstRequestAfterRuntimeStart`；实际受管 CPU/Vulkan 产生 true/false，云端或外部 Local profile 的相关字段在存储前剔除。摘要分别计算首请求与后续请求的时延样本数和 p50/p95，并记录 ManagedLocal 未知阶段请求数。没有硬件指纹、请求正文或额外网络调用。 |
| 验收 | 新回归先因请求阶段字段和报告统计字段缺失而编译失败；实现后诊断摘要、服务、LocalRuntime、设置页及质量关联测试 **47/47 通过**，WPF build **0 warning / 0 error**，定向 `git diff --check` 无空白错误。新增测试验证 Local runtime 两次请求依次标记 true/false、报告正确分组首/后续请求并拒绝云端混入。 |
| 实际复测 | 固定同一 Qwen3-4B Q4_K_M、结构化请求及前一节列出的运行时哈希与参数；CPU、Vulkan 各 10 次，实际 HTTP 200 均 10/10，未知阶段 0。CPU 首请求 **21,793.1 ms (n=1)**，其后 9 次 p50/p95 **17,337.2/17,741.4 ms**；Vulkan 首请求 **6,240.8 ms (n=1)**，其后 9 次 p50/p95 **4,447.3/4,619.3 ms**。首请求分别比各自后续请求 p50 慢约 **25.7%/40.3%**，仅描述本轮固定探针，不解释成模型质量差异。该轮 runtime 启动到 ready：CPU **3,913.3 ms (n=1)**、Vulkan **4,078.5 ms (n=1)**；加上前一轮各 backend 的一次启动观测，两次重启范围为 CPU **3,913.3–5,738.7 ms**、Vulkan **3,962.4–4,078.5 ms**，n=2 仍不足以估计分布。 |
| 产物、边界与下一步 | 本轮摘要：[分层诊断 JSON](../out/test-artifacts/local-backend-phase-repeat-bench-20261006/diagnostics/generation-diagnostics-summary.json)，SHA-256 `1a3af513bdaaadcf25deff3216f12ad040ed5e97c02b982c67547420bb0ca6c10`；复现元数据：[benchmark-metadata.json](../out/test-artifacts/local-backend-phase-repeat-bench-20261006/benchmark-metadata.json)，SHA-256 `4acf0c1cd9524c61d76a5472911b99af7c5d45c82db4914b13e095ee1ff635f8`；运行器：[Program.cs](../out/test-artifacts/local-backend-phase-repeat-bench-20261006/Program.cs)。采样按 CPU 整批后 Vulkan 整批，首请求也纳入总时延；单设备 RTX 4060 Laptop、单固定请求，未评分输出、无云端请求，阶段 0 贡献 **0**。下一步用多个语义族开发样本观察业务行为并把模型输出交给现有质量门禁；不以这 20 个性能请求推导事实保真或直接可用率。 |

## 2026-10-06：Qwen3-4B CPU/Vulkan 固定请求重复性能观测

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 4 本地性能分档。上一步把已有启动/工作集探针接入本机摘要；现在用同一模型、同一结构化请求和同一推理参数，验证摘要是否能生成 CPU/Vulkan 的重复请求数据。该实验只测本机工程性能，不使用输出给模型打质量分。 |
| 输入与复现条件 | 官方 Qwen3-4B Q4_K_M，revision `bc640142c66e1fdd12af0bd68f40445458f3869b`、SHA-256 `7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5`；llama.cpp b11424 prerelease CPU ZIP SHA-256 `d613ef281e23e91b0c9cba171da421223b3b346c6b20ef4825230e08efacb5c7`、Vulkan ZIP SHA-256 `97de9ac35768f0eb85b88e8a6c4a1c409597fc4eb0d307a34cf36c2988f146e9`。Windows 11 家庭版、Intel i7-13650HX、NVIDIA RTX 4060 Laptop（4 GB VRAM，驱动 `32.0.15.8180`）、系统内存 `25,551,560,704 bytes`。相同固定合成中文润色请求，每后端 10 次；temperature `0.4`、top_p `1.0`、max_tokens `512`、seed `42`、repeat_penalty `1.17`、ctx `4096`、batch `512`、cpu_threads `0`、KeepLoaded `true`。先 CPU 后 Vulkan；每后端新服务进程，批内连续请求。 |
| 实测 | 20/20 请求均 HTTP 200 且本地响应可解析。CPU：请求时延 p50 **17,997 ms**、nearest-rank p95 **21,972.2 ms**，启动到 ready **5,738.7 ms（n=1）**，进程峰值工作集 **4,981,583,872 bytes**，估算输出速率 p50/p95 **14.62/14.88 tokens/s**。Vulkan：请求时延 p50 **4,580.5 ms**、p95 **6,355.4 ms**，启动到 ready **3,962.4 ms（n=1）**，峰值工作集 **3,223,011,328 bytes**，估算输出速率 p50/p95 **57.77/59.64 tokens/s**。该批次 Vulkan p50 请求时延约为 CPU 的 **1/3.93**，输出速率约 **3.95 倍**；这是描述性样本比值，不是发布承诺或跨设备速度比。 |
| 产物 | 本机诊断摘要：[generation-diagnostics-summary.json](../out/test-artifacts/local-backend-repeat-bench-20261006/diagnostics/generation-diagnostics-summary.json)，SHA-256 `5d3f398a2b19797208cdb0ecff56f9447d9d2a5cb1c9aa5eaa2b6eb1efec569d`。硬件/模型/运行时/参数/样本元数据：[benchmark-metadata.json](../out/test-artifacts/local-backend-repeat-bench-20261006/benchmark-metadata.json)，SHA-256 `b45691aad3a7981cf1cb37132e7bccfdb4538303435a3cec4006aa3f35691546`。运行器：[Program.cs](../out/test-artifacts/local-backend-repeat-bench-20261006/Program.cs)。生成文本未写入证据文件；云 API 请求数为 0。 |
| 自审、局限与衔接 | nearest-rank n=10 时 p95 取样本最大值；每后端冷启动耗时仅 n=1，不能估计其分布。分组中首个生成请求也包含在请求时延里；CPU/Vulkan 按批次顺序执行，可能受温度/缓存状态影响。Token/s 是输出 Token 除以本地 HTTP 请求耗时，包含 loopback 开销；工作集不等于 Vulkan 显存。HTTP 200/响应可解析不代表 Schema、事实保真或可直接采用。数据来自一个固定内部合成请求，阶段 0 贡献 **0**，未形成质量结论。下一步优先让摘要也呈现冷/热请求分层及足量启动样本，随后用多族内部开发样本检查场景行为；正式质量结论仍需授权冻结盲评集。 |

## 2026-10-06：受管本地运行时指标接入生成诊断

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 4 本地性能观测，并完善阶段 1 本机诊断。`LocalRuntimeManager` 原本可读启动到就绪耗时和进程峰值工作集，但正式生成诊断没有接入；用户无法在同一报告中观察 CPU/Vulkan、本地请求时延、启动时间和内存。 |
| 设计与边界 | 复用已有 Process 探针，不再新增轮询线程或硬件指纹。`LocalRuntimeManager.CaptureRequestMetrics` 在请求完成时采集当前峰值工作集，启动到就绪耗时只留给每个受管 runtime 启动后的首个请求，热请求不重复记为冷启动。遥测关闭时仍消费并丢弃这次性启动观测，避免以后开启诊断时把旧启动时间错挂到新请求；不写盘。 |
| 具体改动 | `ProviderRequestTelemetry` 与 `GenerationDiagnosticEntry` 增加可空启动时间和峰值工作集字段；`LocalTextGenerationClient` 透传实际 backend 与受管运行时指标。摘要增加启动 p50/p95、工作集观测数/最大值及本地输出 Token/请求时延 p50/p95；速率只针对 ManagedLocal CPU/Vulkan 计算。云端即使出现类似字段也会在落盘前剔除。设置说明与隐私文档说明该速率含 loopback HTTP 开销、工作集不含显存，且不采集硬件身份。 |
| 红绿验证 | 新增测试先因请求遥测接口尚不存在而编译失败；实现后相关本地运行时、诊断摘要/服务、设置页、Provider 遥测及工作流关联筛选 **46/46 通过**。测试覆盖实际运行时指标从 client 到请求日志/报告的传递、云端指标剔除、分位数、最大工作集和本地速率计算。`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**；`git diff --check` 无空白错误，Git 输出的 LF/CRLF 提示来自工作区换行格式。 |
| 收益与接续 | 无需再从旧烟测 JSON 手工拼接，即可让后续用户 opt-in 记录携带 runtime 启动/工作集观测；当前没有用户真实记录，因此本步没有新增 p50/p95 实测值、速度提升或质量结论。报告没有硬件型号字段，不能据此跨设备比较。下一步可用同一模型/提示/参数在 CPU 与 Vulkan 跑重复请求，再由本机摘要形成按真实 backend 切片的观测；阶段 0 正式盲评贡献仍为 **0**。 |

## 2026-10-06：本机生成诊断分组报告

| 子任务 | 结果 |
|---|---|
| 目标 | 将已关联的 Provider 请求尝试与最终工作流质量摘要汇总成用户可自行生成、留存在本机的报告，便于后续有足量真实记录时按任务、Provider、模型和本地实际后端查看。 |
| 实现 | 新增 `GenerationDiagnosticsReportService`，输出请求/工作流分组、成功率、结果计数、nearest-rank p50/p95 时延、可用与未知 Token 数、完整 Token 合计、结构/质量门禁通过率、修复数和问题数。只有同一 generationId 下请求数量完整且只使用一个模型时，才把质量结果归属到模型；跨模型 fallback 保留在任务汇总并标记未归属。设置页新增“生成本机诊断摘要”入口，JSON 写入 diagnostics 目录；一键清除同时删除摘要。 |
| 隐私与解释 | 摘要只聚合既有无正文元数据，不引入新遥测或网络请求；本地 backend 只接受实际启动后的 CPU/Vulkan 白名单，不作为硬件身份。缺任一用量时不报部分 Token 总量；未配置费率卡时明确不计算费用。 |
| 验证 | 新增 nearest-rank 分位数、缺失用量处理、fallback 归属、CPU/Vulkan 分组、不可信 backend 净化、摘要落盘与清除测试。诊断报告、服务、设置 UI、Provider 遥测及质量关联定向筛选 **18/18 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**。`git diff --check` 无空白错误；Git 对工作区既有 LF/CRLF 混合文件提示换行转换。 |
| 边界与下一步 | 报告当前只用合成诊断夹具验证，用户尚未启用诊断并积累真实记录，因此没有实际 Provider 时延、Token 或质量基线，也没有推导模型优劣。没有机器硬件型号、内存/显存、启动/加载耗时字段；硬件分层性能仍需另行采集。下一步直接将报告用于后续本机实际记录复盘，同时继续推进阶段 0 授权盲评样本与阶段 4 干净 Windows 运行时验证。阶段 0 评测贡献仍为 **0**。 |

## 2026-10-06：请求遥测关联结构/质量门禁结果

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1 质量门禁与上线监控衔接。上一步已把 Provider 尝试级遥测接入本机，但单看 HTTP success 无法回答业务输出是否通过结构契约与事实/约束门禁；润色和提示词优化还需要各自适用的质量定义。 |
| 技术判断 | 用每次用户生成一个随机 ID 关联请求事件和最终工作流摘要，不尝试从时间戳猜测归属，也不复用输入文本哈希（避免低熵内容反查）。质量事件仅写固定结果类别、可空布尔门禁、修复标志、问题数量和请求次数；绝不记录校验文案或正文。润色契约结果来自最终 `PolishResponseKind`；提示词优化暴露 `StructuredOutputValid`，仅在结构化路径可确定时报告，纯文本 Provider 保持 `null`。业务质量通过分别看事实质量门禁，不把 HTTP success 或 Schema success 当作同一指标。 |
| 具体改动 | 请求事件新增 `generationId`；新增 `generation-workflow-diagnostics.jsonl`，字段含任务、终态（final/clarification/blocked/invalid/failed/cancelled/not_generated）、输出契约有效性、结构化输出有效性、质量门禁通过、是否修复、问题数量与该次关联请求数。`GenerationDiagnosticsScope` 贯穿主请求与明确 fallback；`MainViewModel` 在润色/提示词工作流返回后提交摘要。提示词结构化路径现把 `StructuredGenerationWorkflow.Succeeded` 映射为 nullable `TransformationResult.StructuredOutputValid`。用户清除操作同时删除请求与工作流日志；两份文件均按 500 条上限批量裁剪。Scope 每次请求都重读当前启用/无痕状态，用户在生成中关闭诊断后即停止后续记录。诊断存储错误在 Scope 内吞掉，不能把已生成结果变成失败。 |
| 验收与量化 | 新测试先因 Scope、关联 ID、工作流结果字段缺失而编译失败；存储故障测试也先复现异常会穿透生成流程，再通过 Scope 容错修复。最终诊断、Provider、配置、两类工作流及 MainViewModel 相关回归 **90/90 通过**。两条 MainViewModel 集成用例都验证 request 与 workflow 的 ID 相同、任务正确、契约与质量门禁通过、问题数 0、修复 false、request count 1；另验证用户关闭记录后请求中后续尝试和质量摘要停止落盘。诊断摘要文件中输入和成稿出现次数 **0**。 |
| 自审、收益与边界 | 现在可以将接口尝试（时延/Token/HTTP）与该次最终的输出契约和业务质量结果配对，并按 polish/prompt task 分析，不会把修复前的接口成功当成最终质量通过。测试证明的是诊断标签传递和数据边界，不代表候选模型已达到质量阈值；没有真实 API 成本、延迟收益或盲评分数。事实校验摘要目前仅给出通过与否及问题数，不含分项事实标签；阶段 0 冻结盲评仍是回答事实保真、语气与直接可用率的必要证据。下一步先把这批元数据用于可复跑的分组汇总和报告，再决定是否需要更细粒度、仍不含文本的错误码。 |

## 2026-10-06：正式生成路径的本机请求诊断闭环

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 服务质量观测。`AIService` 与 `LocalTextGenerationClient` 已支持观察耗时、可用用量、HTTP 状态、请求 ID 和结果类别，但 `App.CreateGenerationClient` 没有传观察器，正式产品请求因此没有本机诊断数据。约束是项目默认不启用遥测、无痕模式不留下记录，也不得保存/上传任何请求内容。 |
| 第一性原理与取舍 | 若默认写盘，会改变既有“默认无遥测”的隐私预期；只做内存计数无法跨请求分析，也缺用户可检查和删除的路径。采用设置页显式开启的本机 JSONL 文件：默认关闭、无痕模式硬关闭、只写必要的请求元数据、可一键清除且最多保留 500 条。未选用外发监控或把诊断并入含正文的错误日志。同步将任务模式传入生成客户端工厂，避免润色与提示词优化的延迟/用量混在一起。 |
| 具体改动 | 新增 `Services/GenerationDiagnosticsService.cs`：记录 UTC 时间、任务、Provider 平台/协议/类型、解析后的模型 ID、时延、输入/输出及缓存 Token、HTTP 状态、白名单结果类别和受限格式 Request ID；不接受正文、成稿、用户偏好、密钥、profile 名称或 endpoint。写入 `<数据目录>/diagnostics/generation-diagnostics.jsonl`，达到上限后批量裁剪以避免每次请求重写整份文件。`AppSettings.LocalGenerationDiagnosticsEnabled` 是可选字段且默认 `false`，配置版本不变，旧配置兼容；历史设置页提供开关及清除操作。润色和提示词优化主链及显式 fallback 均携带原任务模式。`README.md` 与隐私说明已澄清：默认无外发遥测，另有默认关闭的本机诊断。 |
| 验收与量化 | 新增测试先因服务和设置字段不存在而按预期编译失败；实现后再验证默认关闭/无痕关闭、任务归类、旧配置默认值、新配置保存、设置页开关与清除、元数据净化、最多保留条数、内容/密钥/地址不入文件，以及润色/提示词优化向客户端工厂传入正确任务。AI 与本地客户端遥测、正式模式工厂、配置迁移、设置保存及 UI 联合筛选 **14/14 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**；`git diff --check` clean。所测夹具中的正文/成稿/密钥/endpoint 字符串写入诊断记录次数为 **0**。 |
| 自审、收益与边界 | 本子节记录的是工作流摘要接入前的请求级监控交付状态；请求日志单独只能观察 HTTP 尝试，不能替代盲评。后续关联工作流摘要的实施结果见本文紧邻的更新章节。没有产生真实 Provider 调用，也没有质量、速度或费用收益的实测结论；缺失用量保持未知，不估算费用。用户采纳率、细分事实标签及冻结集评分仍未接入，阶段 0 正式评测贡献仍为 0。运行中的生成请求创建客户端时读取开关，关闭或无痕模式不记录；保存设置后下一次生成生效。 |

## 2026-10-06：本机 CPU/Vulkan 运行时安装链路复跑

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 4 单机本地运行时交付验证。此前已在 RTX 4060 Laptop 完成官方固定哈希 CPU/Vulkan ZIP 经生产安装器安装、启动、Qwen3-4B 润色与卸载；本步复核当前归档资产和程序状态，以新隔离目录重新跑完整链路，确认不是仅手动解压或遗留安装状态。 |
| 约束与方法 | 读取仓库内保留的 b11424 CPU/Vulkan zip 和隔离 Qwen3-4B 模型；直接使用生产 `LocalRuntimePackageService`、`LocalRuntimeManager`、本地生成客户端和 `PolishWorkflowService`。使用 local HTTP handler 只向安装器供给本地归档字节；不读用户配置、不创建云 Provider、不改 AppData 默认运行时或用户模型。 |
| 实测结果 | 当前设备为 Intel i7-13650HX（14 核/20 线程）、NVIDIA RTX 4060 Laptop、驱动 `32.0.15.8180`。CPU 与 Vulkan 安装均通过目录 SHA 清单校验，实际 backend 分别为 CPU/Vulkan，loopback `/health` 均 HTTP 200，产品润色均 `Final`、无修复、质量 issue 0，notice 各 264,887 bytes，卸载验证通过。CPU 单例耗时 36.32 s、启动就绪 4.83 s、峰值工作集 5,098,491,904 bytes；Vulkan 单例 6.02 s、启动就绪 5.12 s、峰值工作集 3,221,966,848 bytes。外部 API 请求 0；默认 `%LOCALAPPDATA%/Huaxiazi/runtimes/local` 仍不存在。 |
| 产物、收益与边界 | 机器结果：[复跑 JSON](../out/test-artifacts/runtime-package-install-followup-20261006.json)，SHA-256 `5777cc2548b4769a68055d5e726cd9e76d33c0c16865de6bf6967b5c82ea5828`；完整早期烟测见[安装链路记录](../local-runtime-package-install-smoke-2026-10-06.md)。这是第二次同设备单样本烟测，前一轮 CPU/Vulkan 各 27.86 s/4.90 s，故存在可见单次时延变化；n=2 不足以估算 p50/p95 或发布 CPU/Vulkan 速度比。能证明本设备上正式包闭环复现，不证明干净 Windows、GPU 显存、跨设备支持或模型质量。阶段 0 贡献仍为 0。 |
| 自审与接续 | 默认用户安装目录缺运行时，说明该复跑全程限于指定隔离根且没有留下受管运行时。当前机器已具备 VC++ runtime 与 Vulkan 驱动，所以不能用它验证干净系统缺项；HealthCheck 的注入测试已有覆盖，但不能替代 clean image。下一项优先完成可在当前环境执行的受控重复性能采集/运行前硬件诊断，或取得干净 Windows 镜像后执行依赖恢复矩阵；性能数据必须按设备、首次/热态分开报告，不能混成质量结论。 |

## 2026-10-06：偏好信号配置迁移、导出与删除路径核验

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 3 新增固定短语统计后，检查它是否能通过旧配置读入、新配置读写、用户主动导出、单场景清除和全部重置正确流转。核心约束是旧 JSON 无新字段时正常启动；用户删除确认偏好时保留其它范围与匿名统计；明确“清除全部偏好”时必须彻底清除新旧学习数据。 |
| 路径与判断 | `ConfigService.Load` 对 `ExpressionPreferenceProfile` 执行 Normalize，新增信号字典因此为空值兼容并过滤未知短语/非正计数；设置页保存保留交互信号、只替换确认偏好草稿；单范围清除删除该范围已确认偏好但保留无正文交互统计；全部重置用空 profile 替换并落盘；用户主动导出序列化整个结构化 profile，故新信号可随现有偏好一并携出，不包含生成文本、密钥或历史正文。代码路径均已有实现，无需增加生产分支。 |
| 测试产出与验收 | 扩展 v21 配置 load/save/load 夹具，证明旧 profile 缺失新字段可兼容；增加新信号 task/scenario round-trip；偏好导出断言检查接受/拒绝短语计数确实保留；扩展当前范围清除与全部重置用例，分别验证保留统计与清除统计。相关配置、导出、单范围清除、全量重置、失败回滚、候选保存/忽略共 **8/8 通过**。为适配当前 Windows 身份不能写系统 Temp 的环境，三个相关测试目录改到仓库 `out/test-artifacts`，应用数据根和真实用户配置均未触碰。 |
| 自审与衔接 | 本次发现原有数据流本身已满足兼容、导出和清除语义；缺口在新增 schema 的可回归证明，所以只增强测试，没有增加生产代码。单范围清除保留交互统计是产品既定语义；用户要清除学习数据应使用全量重置。当前确认偏好的用户自定义禁用词属于主动导出的偏好内容；匿名学习计数只含固定词表，不含原文。下一步按阶段 4 剩余项核验本机运行时/Windows 依赖与 CPU/Vulkan 启动行为；干净 Windows 和多硬件结论仍需目标机器实测。 |

## 2026-10-06：个性化偏好新增固定套话候选

| 子任务 | 结果 |
|---|---|
| 计划节点、问题与约束 | 阶段 3“个性化建议候选继续扩展”。原学习候选只覆盖篇幅方向，无法把“同一已知套话多次被用户删去、且最终接受结果”的信号交给用户确认。目标是在不收集用户原文/成稿、不自动改变生成偏好的前提下，形成可见、可编辑、按当前任务/场景隔离、可逐项忽略的候选。 |
| 第一性原理与方案取舍 | 单次删词可能由上下文或篇幅造成，不能据此形成长期偏好；从任意文本挖掘短语又会保存用户内容。采用已有固定套话白名单（6 项），每个生成结果最多对每个短语记一次；只累计本地的接受/拒绝计数和输出风格，不保存内容。只有同一任务/场景至少 **3 个已接受结果**删除相同短语、没有任何关联拒绝/撤销证据，且输出风格一致时才显示候选。3 是减少偶发信号的产品阈值，不是经盲评校准的偏好概率。与自动采用相比，用户逐条载入、编辑和保存成本稍高，但避免弱证据静默进入 prompt。 |
| 数据、上下游及兼容 | `ExpressionPreferenceFeedbackSession` 对当前编辑快照比对固定词表，`StructuredPreferenceService` 按 accepted/rejected 写入 task/scenario 范围计数；新的 JSON 字段 `acceptedRemovedCannedExpressions` / `rejectedRemovedCannedExpressions` 是可选增量，旧配置缺字段会归一为空，未知短语/非正计数会被过滤。候选服务抑制已有确认表达、任一反向信号、混合/未知风格及已忽略项。设置页新增候选列表，每项可载入现有禁用表达编辑框后修改/删除并保存，或只在本机忽略该项；只有原有设置保存后才标记为 `user-confirmed` 并进入生成提示。未增加 Provider 请求、遥测内容、联网或训练数据上传。 |
| 验收与量化 | 新增测试先因交互信号字段和候选 API 不存在而编译失败；随后自审发现“无风格元数据但有已知短语删除”会被提前丢弃，再由一条红测复现并修复。交互反馈、候选证据/冲突/隔离、旧字段归一、无风格元数据保留但不出候选、设置草稿确认/编辑、逐项忽略重载、主窗口反馈联动及 WPF 视图实例化联合筛选 **59/59 通过**。`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**；`git diff --check` 无空白错误（仅有既有 LF→CRLF 提示）。对符合条件的候选，确认前新增生成 prompt 的次数为 **0**；确认后沿现有偏好上下文路径生效，不增加模型调用。 |
| 自审、局限与衔接 | 固定短语被删除不等同于用户长期排斥该表达；一次拒绝会保守地抑制该短语候选，显示的 3 次门槛也没有阶段 0 盲评校准。用户仍需确认，且只适用于当前有限白名单，不能推广到任意禁用词。旧的全局 `removedCannedExpressions` 计数继续兼容但不用于该建议。阶段 0 盲评尚缺，因此没有质量收益结论。下一步继续阶段 2/4 的剩余真实路径验证，并扩展个性化候选前先取得足以解释其来源的无正文信号。 |

## 2026-10-06：阶段 2 产品生成入口与网络边界盘查

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 2“可执行路由”剩余项：盘查所有产品生成入口及云端数据边界。对 `GenerateAsync` / `GenerateStructuredAsync`、`CreateGenerationClient`、`ProviderRouter`、提示词优化/润色工作流和独立盲评 runner 做调用面追踪。核心问题是用户配置的路由和“仅本地”是否在密钥读取、客户端构造和网络请求前生效，以及个人偏好是否会随显式 fallback 外泄。 |
| 第一性原理与约束 | 主产品输入是否出网取决于最终生成客户端的 Endpoint，而不是 UI 标签。故路由必须先于 key 读取和 client 创建；严格本地必须由 ManagedLocal 或 loopback Endpoint 证明，并且不能持有云 fallback。场景偏好只在本地发送，除非用户明确允许与云端共享。连接测试、模型/运行时下载、盲评候选属于不同的显式网络用途，不能误记为主窗口的润色/提示词生成。 |
| 入口清点与结论 | 源码检索到两条主产品业务入口，均位于 `MainViewModel`：润色和提示词优化共用一次 `ProviderRouter.Select`，然后通过同一延迟工厂创建客户端；缺项澄清/预检分支先返回，因此不会读取密钥或构造生成客户端。路由器的 LocalOnly 仅返回 ManagedLocal/loopback profile、清除 fallback，找不到本地 profile 时直接失败。PreferLocal/PreferCloud 不隐式跨 Provider，仅用户绑定备用 profile 才启用 fallback；拒答、Schema 不支持等非普通故障保持既有分类边界。确认偏好仅在用户启用云共享时可随云端请求发送；本地首选但可能落到云端时默认不发送确认偏好或旧偏好。盲评 runner 是独立评测入口，按冻结候选显式建立客户端，不作为产品路由旁路。 |
| 验收与量化 | 针对 `ProviderRouterTests`、LocalOnly 无本地时不创建客户端、显式 fallback、云偏好披露、缺项零客户端及 `BlindCandidateClientFactoryTests` 的收窄筛选 **21/21 通过**。未发送真实云端请求；证据验证的是路由/构造和内容披露行为。更宽泛的第一次筛选误匹配到 Skill/设置用例，2 项因当前 Windows 身份无权写系统 Temp 而失败/中止；已收窄并通过的 21 项不包含这两项，Temp 失败未计入验收。 |
| 自审、剩余边界与衔接 | 本次静态盘查与 fake-client 测试未发现主产品生成入口绕过 Router，无需改生成行为；因此只更新本状态记录，不为制造代码差异添加冗余实现。盲评 runner 仍可在明确执行时把授权评测输入发给选定云候选，这是设计用途，运行前的数据准入/授权仍需独立满足。此前“全部路径”结论限于源码可搜索到的当前两条主窗口生成业务，不覆盖未来新增入口或真实 Windows 网络监控。阶段 2 的实际云/本地模型性能、驱动矩阵和打包验收仍未完成。下一子任务转到计划第 2 项：扩展可见、可编辑、逐项确认/删除的个性化建议候选；仅从无正文交互统计提出低置信建议，保留当前用户确认优先级。 |

## 2026-10-06：结构化 Schema 降级状态跨业务修复复用

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 统一结构化输出契约与 Provider 降级。检查 `StructuredGenerationWorkflow`、`ProviderFallbackGenerationClient` 及润色/提示词优化修复路径。发现 native Schema 被明确拒绝后，`nativeSchemaRejected` 只保存在单次 `ExecuteAsync` 栈帧；两条产品工作流每次初稿、契约修复或质量修复都会重新创建执行器，可能再次发送已被拒绝的 Schema 并重复收到 HTTP 400。 |
| 第一性原理与方案 | 同一用户任务、同一 Provider、同一输出契约已经得到明确的 Schema 参数拒绝后，重发相同约束没有新信息；正确做法是本任务内切换到提示词 Schema + 本地校验，后续修复沿用该决策。跨任务缓存则可能把临时/特定 Schema 拒绝错误扩散到其他请求，因此状态只绑定单次业务执行。保留现有 `maxAttempts: 1` 与各业务的一次修复上限，不以额外重试换取表面成功率。 |
| 改动及上下游 | `StructuredGenerationWorkflow` 将 Schema 拒绝状态保存在实例中；润色和提示词优化各自在一次 `ExecuteAsync` 中创建一个 workflow，并将同一实例传给初稿、空响应重试、契约/质量修复。该 workflow 仍使用原 `_client`，`ProviderFallbackGenerationClient` 对 `StructuredOutputUnsupported` 不切换 Provider；只有普通文本请求后续失败时，用户显式配置的备用策略仍按既有行为处理。为在本 Windows 环境跑真实业务集成回归，将相关测试数据目录及 `UseIsolatedConfigDirectory` 默认根移入仓库 `out/test-artifacts`，避开当前身份无写权限的系统 Temp。 |
| 验收与量化 | 新增 workflow 状态、润色集成和提示词优化集成三个回归。修复前三例均显示同任务发出 **2 次**被拒 Schema 调用，红测复现了问题；修复后两条业务链各验证为 **1 次结构化请求 + 2 次普通文本请求**（初稿降级及一次修复），相对旧路径少 **1 次无效 Schema 请求/任务**，本例总请求由 **4 降为 3（减少 25%）**。`StructuredGenerationWorkflowTests`、`PolishWorkflowServiceTests`、`ProfessionalExpressionEngineTests`、`CompanionGenerationIntegrationTests` 合并 **77/77 通过**；主 WPF 构建 **0 warning / 0 error**；改动文件 `git diff --check` 通过。 |
| 自审与接续 | 计数来自 fake Provider 集成夹具，不代表线上模型质量/延迟，也没有调用真实 Provider。workflow 实例必须按单次用户任务创建；当前两个正式入口均已做到，状态不会跨用户任务保存。后续应继续核对显式 Provider fallback 后真实模型标识展示，并以阶段 0 冻结集确认结构修复是否提升可用率。 |

## 2026-10-06：Provider 请求体过大错误分类

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 失败分类与可诊断性。HTTP 413 之前落入通用请求拒绝；不同 Provider 的原始错误文字不一致，用户无法确定应缩减请求数据还是检查凭据。 |
| 第一性原理与方案 | HTTP 413 的共同语义是请求体超出服务可接受大小。Anthropic 将其定义为 `request_too_large` 并按 endpoint 提供请求字节限制；OpenAI 说明超过压缩/解压请求限制会返回 413。因此可按状态码统一归类，而无需解析 Provider message。提示针对请求体和附件体积；不把 token/context-window 400 错误也误判为字节大小问题。来源：[Anthropic API errors](https://platform.claude.com/docs/en/api/errors)、[Anthropic request size limits](https://platform.claude.com/docs/en/api/overview)、[OpenAI production best practices](https://developers.openai.com/api/docs/guides/production-best-practices)。 |
| 改动及上下游 | 新增 `GenerationFailureKind.RequestTooLarge` 和 `ConnectionTestStatus.RequestTooLarge`。生成错误提示用户缩减输入文本或附件体积；连接测试提示检查代理/网关请求体限制；无内容 telemetry 记录 `request_too_large`。不读取错误正文、不增加重试，也不改变用户显式配置的 fallback 策略。上游为 Provider HTTP 413，下游为生成错误 UI、连接测试和诊断 telemetry。 |
| 验收与量化 | 新增 413 生成失败、连接测试提示及通用 HTTP 状态映射用例；AIService 定向回归 **216/216 通过**，fallback、盲评候选与 telemetry 回归 **23/23 通过**，主 WPF 工程构建 **0 warning / 0 error**，`git diff --check` 通过。413 从通用请求拒绝细分为请求体过大；不增加生成调用，错误响应正文读取量为 0。 |
| 自审与接续 | HTTP 413 指向请求字节限制；本步不推断模型 token 上下文超限，也不修改上下文裁剪逻辑。改进仅作用于排错，不能提升模型质量或降低成功请求延迟。下一步继续检查剩余 Provider 错误边界和结构化降级是否保持同 Provider、最多一次。 |

## 2026-10-06：Gemini 账号前置条件错误分类

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 失败分类与可诊断性。上一子步已区分 HTTP 402 账单问题及 Gemini 结构化 `ErrorInfo` 中明确的无效 Key。本步发现 Gemini GenerateContent 的 400 `FAILED_PRECONDITION` 仍显示为通用请求拒绝，无法指导用户检查项目计费或地区资格。 |
| 第一性原理与方案 | 错误文案应匹配错误语义，且不依赖可能回显请求内容的 `error.message`。Gemini GenerateContent 官方错误表明确将 HTTP 400 `FAILED_PRECONDITION` 关联到免费层地区不可用、项目需要启用付费方案；错误信封提供机器可读的 `error.status`。因此仅在 Gemini GenerateContent + HTTP 400 + `status=FAILED_PRECONDITION` 的精确组合下，归类为“账号/项目调用前提未满足”，提示检查计费启用及地区资格；不称其为余额耗尽，也不归为结构化输出错误。其他 `INVALID_ARGUMENT` 保留通用拒绝。来源：[Gemini GenerateContent API errors](https://ai.google.dev/gemini-api/docs/generate-content/api-errors)。 |
| 改动及上下游 | 新增 `GenerationFailureKind.AccountPrerequisite` 与 `ConnectionTestStatus.AccountPrerequisite`。生成和连接测试只在 Gemini HTTP 400 时对同一份最多 64 KiB 响应体检查精确 status；无内容 telemetry 记录 `account_prerequisite`。既有 Gemini API_KEY_INVALID 识别要求 `@type`、`reason`、`domain` 和 `metadata.service` 全部匹配，402 仍提示检查余额和付款方式；两者均不读取任意 message。原始错误正文不进入 UI 或 telemetry，用户配置的 fallback 规则不变。上游为 Provider HTTP 响应，下游为错误提示、连接测试和盲评运行错误归类。 |
| 验收与量化 | 新增 `FAILED_PRECONDITION` 生成/连接测试，并保留 API_KEY_INVALID 正例与普通 INVALID_ARGUMENT 反例；新用例先因缺枚举而编译失败，完成后 AIService 定向用例 **213/213 通过**，fallback、盲评候选与 telemetry 回归 **23/23 通过**，主 WPF 工程 `dotnet build --no-restore` **0 warning / 0 error**。目前 Gemini HTTP 400 中已区分两种有官方字段证据的情形（无效 Key、账号/项目前置条件），普通请求错误仍走通用分类；真实 API 请求数为 0。 |
| 自审与接续 | `FAILED_PRECONDITION` 的公开示例不是所有账户原因的完整目录，因此用户提示同时涵盖计费启用和地区资格，不猜具体是哪个条件失败。Gemini 400 普通 INVALID_ARGUMENT 仍不会触发 Schema 重试；此步不改善生成质量或速度，只改善诊断。下一步继续核对其他 Provider 的错误机器字段及其与显式 fallback 的交互。 |

## 2026-10-06：HTTP 认证失败与权限拒绝分开归类

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1 Provider 失败分类与设置诊断。审查输入包括 `AIService.BuildGenerationFailure`、连接测试 HTTP 状态映射、无内容 telemetry 与 ProviderRouter fallback。Anthropic 官方错误契约将 HTTP 401 认证问题与 HTTP 403 权限问题分开；应用原来把二者都映射为 `Authentication` / `AuthFailed`，可能把模型授权、区域或组织权限问题引导成“重填 API Key”。 |
| 技术判断与改动 | 以通用 HTTP 状态语义区分认证与访问权限：401 → `GenerationFailureKind.Authentication` / `ConnectionTestStatus.AuthFailed` / telemetry `authentication`；403 → 新增 `PermissionDenied` / telemetry `permission_denied`。连接测试对 403 给出检查账号、接口和模型授权的提示；业务生成异常也使用同一方向的用户说明。错误响应正文不参与分类，仍不暴露 Provider message。Anthropic 官方来源：[API errors](https://platform.claude.com/docs/en/api/errors)。 |
| 上下游和 fallback 边界 | 上游是 HTTP Provider 响应；下游包括主窗口错误展示、连接测试结果、非内容 telemetry 与盲评执行器。PermissionDenied 在盲评执行器沿用允许的 `provider_error` 桶，不扩展评分 Schema。该分类不改变路由：`Manual` 和 `LocalOnly` 不创建备用 Provider；`PreferLocal/PreferCloud` 只有填写了 `FallbackProviderProfileId` 才创建 fallback。已有显式 fallback 行为保持不变。 |
| 验收与量化 | 新增 401/403 行为差异与错误正文不泄漏测试；AIService、ProviderFallbackGenerationClient、ProviderRouter、BlindCandidateRunService 联合筛选 **235/235 通过**；WPF 主工程 build **0 warning / 0 error**；目标文件 `git diff --check` 通过。错误标签由 1 类拆为认证失败、权限拒绝 2 类；没有增加请求或遥测内容字段。 |
| 自审与接续 | Anthropic 官方错误体仅保证 `error.type` 与 `error.message`，不提供可稳定依赖的通用参数路径；因此未从错误 message 推断“Schema 不支持”。Gemini 等 Provider 的协议专属字段仍需逐一确认后再加映射。下一步继续核对请求格式失败类别；在没有稳定字段证据时保持通用 `RequestRejected`，避免误触发一次普通生成或备用路由。 |

## 2026-10-06：Provider 结构化输出能力白名单与未知能力降级

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1 Provider 能力映射与共同生成契约。输入为 `ProviderCapabilities.StructuredOutput`、OpenAI Chat/Responses、Anthropic Messages、Gemini GenerateContent 请求构造器，以及阶段 1 已接入两条产品业务链的结构化工作流。审查发现能力为 `Unknown` 的端点仍被无条件发送严格 `json_schema`；Anthropic/Gemini 分支也未检查模型能力。这绕开能力登记，使“未知”实际等同“支持”。 |
| 第一性原理与方案 | 原生 Schema 会影响输出约束，但也可能被旧模型、代理网关或模型模板拒绝；为保持请求兼容，只有有官方型号证据的路径发送原生约束。OpenAI 官方资料确认 GPT-4o 及后续系列（含 GPT-6）支持 Structured Outputs；Anthropic 与 Gemini 文档列出各自支持型号及 Schema 子集。其他型号/网关不推断能力，改为把 Schema 放进稳定的系统指令，由现有本地契约验证。JSON Mode 型号继续发送 `json_object` 并做本地验证。来源：[OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)、[Anthropic Structured Outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)、[Gemini Structured Output](https://ai.google.dev/gemini-api/docs/generate-content/structured-output)。 |
| 改动及上下游 | `ProviderCapabilities` 明确标注官方 OpenAI GPT-4o/4o mini、GPT-4.1 mini、GPT-6/o 系列，以及官方文档支持的 Claude 和 Gemini 型号；模型/协议不匹配仍返回 `Unknown`。`AIService` 的 Chat、Responses、Anthropic、Gemini 请求构造现在都只在 `StructuredOutput=JsonSchema` 时发送原生 Schema，`JsonObjectOnly` 使用 JSON Mode；Unknown 不发送结构字段而附加 Schema 提示。设置页能力摘要同步显示“原生 Schema / JSON Mode / 未核验时提示约束 + 本地校验”，ManagedLocal 则说明能力由已安装运行时与模型决定。工作流随后统一进入本地结构解析、Schema 检查和既有修复路径。上游为 profile resolved model 与能力解析器，下游为设置页、润色及提示词优化工作流。 |
| 验收与量化 | 新增未知 OpenAI-compatible、Responses、Anthropic、Gemini 型号用例和“未发送原生 Schema 时不误分类 `response_format` 错误”用例，并保留已核实型号的原生 Schema 请求回归；AIService、ProviderPlatform、结构化工作流、润色、提示词优化及 SettingsViewModel 联合筛选 **365/365 通过**。主项目构建 **0 warning / 0 error**；`git diff --check` 通过。4 条协议路径中，未登记型号现在 **0 个**发送原生 Schema；已登记的 Chat/Responses/Anthropic/Gemini 仍覆盖原生约束路径。请求数不增加；未知模型由服务端约束转成本地校验，质量变化需要盲评数据衡量。 |
| 自审、限制与接续 | 官方证据证明协议/型号可用，不证明该模型对中文润色的约束遵循率或成稿质量。Schema 子集仍受 Provider 限制；本地解析与语义门禁不可移除。Ollama、LM Studio 与任意兼容端点能力会因运行时版本和具体模型变化，当前未通过通用型号条目认定支持，均走提示 + 校验路径；用户手动切到这些端点时原生约束可能减弱，但不会以错误请求换取隐式兼容。下一步继续检查能力摘要是否向用户说明该降级，并完成其他 Provider 错误类别的精准归一；阶段 0 盲评仍是质量收益的证据来源。 |

## 2026-10-06：结构化输出拒绝的精确降级与错误隔离

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 错误分类与统一生成契约。输入是 OpenAI Chat/Responses 结构化请求、工作流 Schema 校验器及显式 Provider fallback。旧工作流把任何 HTTP 400/422 的 `RequestRejected` 都当作 JSON Schema 不支持，可能把参数、模型名或请求错误误判为 Schema 能力问题，并额外发起一次明文生成；显式备用 Provider 也可能因此接收原请求。 |
| 第一性原理与约束 | 只有 Provider 明确指出结构化输出参数路径被拒时，才能推导“改用提示约束 + 本地校验”有帮助。HTTP 状态码只能说明请求被拒，不能说明拒绝原因。OpenAI Chat 的字段路径是 `response_format`，Responses 是 `text.format`；不对 Anthropic、Gemini 或任意兼容网关猜测其错误字段。最多读取 64 KiB 错误体，仅检查 `error.param`，不读取或输出 `error.message`。依据：[OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)、[OpenAI API errors](https://developers.openai.com/api/docs/guides/errors)。 |
| 改动及上下游 | `AIService` 只在结构化请求的 HTTP 400/422 且 allow-listed 参数路径匹配时产生 `StructuredOutputUnsupported`；非匹配错误仍沿用规范化 Provider 错误。`StructuredGenerationWorkflow` 仅对该类型在同一 Provider 上进行一次普通文本生成，随后沿用原 Schema 的本地解析、校验与质量门禁。`ProviderFallbackGenerationClient` 不把此类错误切换到备用 Provider。无内容 telemetry 单独记录 `structured_output_unsupported`；盲评候选记录仍归入 `provider_error`，不误计为生成文本的 `schema_rejected`。上游为 Provider 请求/能力，下游为两条产品生成工作流和盲评候选执行器。 |
| 验收与量化 | 新增用例覆盖明确结构化参数拒绝、无关 `temperature` 参数错误、异常正文不泄漏、工作流本地降级及不触发跨 Provider fallback。AIService + fallback + 结构化工作流筛选 **204/204 通过**；BlindCandidateRunService **15/15 通过**；WPF 主工程构建 **0 warning / 0 error**。限定分支下，一次明确 Schema 参数拒绝最多额外消耗 **1 次同 Provider 普通文本请求**；普通 400/422 不再因此额外发请求或转发到备用 Provider。 |
| 自审、边界与接续 | 错误响应仍由 Provider 产生，fake-handler 测试只能证明字段判别、调用次数和隐私边界，不能证明任一线上模型会如何拒绝 Schema，也不代表质量收益。字段映射仅适用于已知 OpenAI 协议路径；未验证的兼容服务继续走通用失败。下一步应继续核对剩余 Provider 的结构化能力/错误信号，并检查这项降级在两条正式业务工作流中的体验；真实模型质量结论仍依赖阶段 0 独立评测。 |

## 2026-10-06：OpenAI o3 / o4-mini 旧 profile 请求协议修正

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1 Provider 精确能力适配。OpenAI 直连 Chat Completions 对 o 系列仍会发送 `max_tokens`，也会沿用普通模型的采样字段；旧能力表未识别推理 effort 或开发者消息角色。这会造成不兼容字段、推理档位不生效或服务端拒绝。 |
| 官方证据与取舍 | OpenAI API 文档说明 `max_tokens` 已弃用且与 o-series 不兼容，`max_completion_tokens` 同时覆盖可见和推理 token；官方迁移资料列出旧 o3 的 effort 为 low/medium/high。Chat 文档要求 o1 及更新模型使用 developer message；模型页确认 o3/o4-mini 支持 Chat、Responses 与 Structured Outputs。Chat API 将 temperature/top_p 列作通用参数，但未明确说明它们在这些旧 o 型号上的逐型号支持状态；其他 API 表单的限制说明不能替代推理端点的精确能力表。因此采样能力标为 unknown，应用在使用推理档时按保守策略不发送两字段，不把其说成服务端明确拒绝。只兼容精确的 `o3`、`o3-2025-04-16`、`o4-mini`、`o4-mini-2025-04-16`，不匹配代理/自定义变体、不加到新建模型目录。o3/o4-mini 已标为弃用（o4-mini 计划 2026-10-23 下线，o3 为 2026-12-11），所以改动用于存量 profile 过渡，不是新选型建议。 |
| 改动及上下游 | `ProviderCapabilities` 增加精确 o 系列白名单：Chat 使用 `max_completion_tokens` 与 `developer` role，Chat/Responses 都使用文档所列 effort 集合。temperature/top_p 保留 Unknown 能力状态，但独立 `OmitSamplingParameters` 策略令应用在该路径省略字段；UI 明确已保存采样值不会用于这次请求。Responses 保持 `instructions`、`max_output_tokens`、`reasoning.effort`。标记 JSON Schema 支持。代理和未知 ID 仍保持 unknown。上游为已保存 Provider profile 的 resolved model；下游仍经过统一 Schema/业务校验。 |
| 验收、收益及边界 | 生产实现前的定向能力测试先因未声明 developer role 而失败；采样证据复核后增加“unknown + 明确省略策略”与 UI 摘要覆盖。当前 o 系列能力与请求测试 **15/15 通过**，设置页解析后型号摘要 **1/1 通过**；`AIServiceTests` + `ProviderPlatformTests` **254/254 通过**；WPF 主工程 build **0 warning / 0 error**。改动修正请求字段，不代表生成质量提升；未发真实 API 请求，也未测用量、时延或成稿质量。Chat 对话目前无多轮状态上下文；此变更只修复单轮消息协议。 |
| 官方依据 | [Chat Completions 参数与兼容性](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[o3 型号页](https://developers.openai.com/api/docs/models/o3)、[o4-mini 型号页](https://developers.openai.com/api/docs/models/o4-mini)、[OpenAI 弃用清单](https://developers.openai.com/api/docs/deprecations)。 |

## 2026-10-06：阶段 3 当前任务/场景偏好单独清除

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 3 用户可控个性化。已有偏好是按任务/场景保存，学习统计不会自动生效，候选可载入、保存确认或忽略；但设置页只有“清除全部偏好”，用户无法撤销一个场景而保留其它范围。输入为当前编辑范围、已确认偏好草稿及原有交互统计。 |
| 改动、上下游及取舍 | `SettingsViewModel` 新增“清除当前范围偏好”命令，将当前任务/场景从偏好草稿中移除并恢复编辑器默认值，仍由设置页原有保存动作持久化。`ExpressionAbilitySettingsView` 增加单范围入口并明确保存时机。上游是用户选定的任务/场景；下游配置写入后，生成只会读取仍保留的已确认项目。清除结构偏好不清交互统计、不触碰其它范围，也不影响独立输出风格覆盖。 |
| 验收、量化与边界 | 新增单测验证只删除 `polish|职场沟通`，保留 `polish` 通用偏好和该范围匿名计数；定向用例 **1/1 通过**。AIService + ProviderPlatform 回归 **239/239 通过**，WPF 主项目 build **0 warning / 0 error**，`git diff --check` 通过（Git 仅提示 LF/CRLF 转换）。设置页相关全类筛选受当前 Windows 临时目录访问拒绝影响，出现 SQLite 无法打开和 `UnauthorizedAccessException`，未作为功能测试通过。没有增加模型调用；这项改善的是撤销粒度，不代表生成质量提升。 |

## 2026-10-06：OpenAI GPT-6 系列 Chat / Responses 参数适配

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 能力映射。旧实现仅识别 GPT-6 Astra，模型目录也没有当前 GPT-6 系列选项。GPT-6.1 Sol、GPT-6 Sol、GPT-6 Luna 会按普通模型请求发 `temperature`/`top_p` 和旧 `max_tokens`，可能导致 API 拒绝或设置无效；Responses 的推理努力值也没有覆盖整个系列。 |
| 第一性原理决策及改动 | 为 Astra、6.1 Sol、6 Sol、6 Luna 增加 OpenAI 直连精确型号映射，兼容 Chat Completions 与 Responses；不扩散到代理/自定义端点或相似 ID。Low/Medium/High 映射至同名 effort；模型默认档省略 effort。推理请求使用服务默认采样设置，不发送 temperature/top_p；Chat 使用 `max_completion_tokens`，Responses 使用 `max_output_tokens`。目录增加四个模型选项，原 `gpt-4o-mini` 默认保留。GPT-6 Astra/6.1 Sol 的 capability 不含 `none`；GPT-6 Sol/Luna 支持 `none`，但产品当前没有关闭推理档位。 |
| 验收、收益与边界 | 请求与能力测试在旧实现上 **12 项失败**；补齐 Chat 与 Responses 请求覆盖后，GPT-6 专项 **20/20 通过**，AIService + ProviderPlatform 回归 **237/237 通过**。目录增加 **4** 个模型，默认不变；GPT-6 请求会按协议使用正确 token 上限字段，并避免发送 GPT-6 推理模式不支持的采样参数。未调用真实 API，未测中文成稿质量、用量、费用或延迟。 |
| 官方依据 | [GPT-6 模型迁移与参数支持](https://developers.openai.com/api/docs/guides/latest-model)、[GPT-6 Astra 型号页](https://developers.openai.com/api/docs/models/gpt-6-astra)、[Chat Completions API 参数](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。 |

## 2026-10-06：硅基流动 DeepSeek V4 / GLM-5.2 推理控制与 JSON Mode 适配

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 能力映射。硅基流动当前 Chat API 明确 `reasoning_effort` 仅适用于 `Pro/deepseek-ai/DeepSeek-V4`、`deepseek-ai/DeepSeek-V4-Flash`、`Pro/zai-org/GLM-5.2`；这些请求还需 `enable_thinking=true`。服务允许的 effort 为 `high/max`，low/medium 会被折算为 high，xhigh 折算为 max。旧代码未识别三个现行模型，也未开启思考。硅基 JSON Mode 指南要求应用自行验证结构；API 页虽列有 JSON Schema，但没有列出可用型号全集，所以本次不宣称这些型号具备模型级严格 Schema。 |
| 第一性原理决策及改动 | 目录新增三个现行型号，保留 `deepseek-ai/DeepSeek-V3` 默认。能力解析只对三个完整 model ID 生效，不对相似前缀或别名推导。三个型号在非连接测试生成中发送 `enable_thinking=true`；只有用户选 High 时显式发送 `reasoning_effort=high`，Low/Medium 与 Custom 省略 effort 并使用服务默认 high；当前 UI 没有 xhigh 档位，故不发送 max。结构化请求使用 JSON Mode，在 system 指令附 Schema 并执行本地业务校验/修复。保留 `max_tokens` 为最终答案长度上限；不把它重用成推理预算，也不擅自填入 `thinking_budget`。temperature/top_p 继续采用用户配置，说明官方建议二选一。 |
| 验收、收益与边界 | V4/GLM 先行请求/能力用例在旧实现上 **12 项失败**（请求体 9 项、能力解析 3 项）；本次又为 V3 增加 **2 项红测**，复现能力未知与误发严格 Schema。实现后 SiliconFlow 专项 **17/17 通过**；AIService + ProviderPlatform 联合回归 **239/239 通过**；主项目构建 **0 warning / 0 error**。当前官网 JSON Mode 指南称平台语言模型支持 JSON Mode，但 API 未公布严格 JSON Schema 的完整型号名单。V3 现改用 JSON Mode，并把 Schema 放入 system 指令，由 `StructuredGenerationWorkflow` 做本地校验/修复；不宣称 V3 支持服务端严格 Schema。没有真实 API、延迟、费用或中文成稿质量测量。 |
| 官方依据 | [硅基流动 Chat Completions API](https://docs.siliconflow.cn/docs/api/chat-completions-post)、[当前 JSON Mode 指南](https://docs.siliconflow.cn/docs/userguide/guides/json-mode)、[推理指南](https://docs.siliconflow.cn/docs/userguide/capabilities/reasoning)。 |

## 2026-10-06：Mistral 当前模型与推理/结构化输出能力适配

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 能力映射。官方当前文档列出 `mistral-small-2603`（Small 4）和 `mistral-medium-3-5`，而目录未提供它们；官方 Reasoning 文档明确 `mistral-small-latest`、`mistral-medium-3-5` 支持 `reasoning_effort`，Chat API 接受 `low/medium/high` 等档位。旧能力表没有 Mistral 映射。高推理响应会返回 `thinking` chunk 与最终 `text` chunk 的混合结构。 |
| 决策与改动 | 新增 Small 4 与 Medium 3.5 目录选项，保留 `mistral-small-latest` 默认及现有 Nemo/Large 项。只对文档明确列出的 Small latest、Medium 3.5 发送 UI 档位对应的 `reasoning_effort=low/medium/high`；Small 4 固定 API ID未列在推理参数支持清单，故只标记已确认的严格 JSON Schema，不发送推理强度。三种型号的结构化输出能力按型号文档标记为 JSON Schema。temperature/top_p 和既有 `max_tokens` 字段不作未经测量的改变；设置摘要提示官方建议采样参数二选一，并说明高推理会增加 token。 |
| 响应处理与验收 | 新增完整 Mistral thinking/text chunk 请求响应测试；当前通用解析器已只提取 `type=text`，忽略 `type=thinking`，无需重复实现另一套解析。能力映射、模型目录及最终文本隔离的先行回归在旧代码上 **6 项失败**；实现后定向测试 **6/6 通过**。AIService 与 ProviderPlatform 回归 **202/202 通过**，主项目构建 **0 warning / 0 error**，改动文件 `git diff --check` 无空白问题（只有 Git 的 LF→CRLF 转换提示）。 |
| 自审、影响与限制 | 官方来源：[Chat API](https://docs.mistral.ai/api/endpoint/chat)、[Reasoning 与 thinking chunks](https://docs.mistral.ai/studio/conversations/reasoning)、[Mistral Small 4](https://docs.mistral.ai/models/mistral-small-4-0-26-03)、[模型目录对比](https://docs.mistral.ai/getting-started/models/compare?models=mistral-small-4-0-26-03)。本次新增 **2** 个可选模型，不改变默认；对两个已核验推理型号增加一个实际 `reasoning_effort` 字段；高推理可能增加 token/费用，未做真实 API、时延、费用或中文成稿质量测量。既有解析能处理本产品使用的非流式完整响应；Mistral streaming 的增量 thinking 形状不在本项目当前请求路径中。 |

## 2026-10-06：Groq 推理型号、JSON 输出与 token 上限协议适配

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与问题 | 阶段 1 Provider 能力映射。Groq 当前官方型号目录把 GPT-OSS 20B/120B 列为 production、Qwen 3.8 27B 列为可用模型；官方 Chat API 指定三者支持 `reasoning_effort` low/medium/high 与严格 `json_schema`。当前应用目录没有这三个型号，Groq 普通 Llama 结构化请求仍使用 strict Schema，且 Groq API 已把 `max_tokens` 标为 deprecated，推荐 `max_completion_tokens`。 |
| 决策与取舍 | 新增三个可选型号但保留旧默认 `llama-3.3-70b-versatile`，不未经盲评改变用户既有选择。三种推理型号按 Low/Medium/High 发送 low/medium/high；Custom 省略字段使用模型默认。只有官方明确列出的三个型号启用 strict JSON Schema；Groq 其余型号使用 JSON Mode，由应用侧验证 Schema。所有直连 Groq 请求使用 `max_completion_tokens`。temperature/top_p 两项 profile 设置仍按用户现有值发送，界面说明官方建议只调一项；没有评测数据时不静默删字段。 |
| 代码、上下游与验收 | `ProviderPlatformCatalog.cs` 增加三项可选型号，保留默认。`ProviderCapabilities.cs` 对 Groq 精确识别推理/strict-Schema 白名单，并对其他 Groq 型号使用 JSON Mode；同时将 token 上限字段标成 `max_completion_tokens`。`AIService.cs` 根据能力映射实际请求，设置摘要显示实际模型能力。红测复现原目录、推理 effort、token 字段及 Llama JSON mode 缺口；实现后 Groq 专项 **10/10 通过**，AIService/目录/profile/设置/WPF 联合回归 **307/307 通过**，主项目 build **0 warning / 0 error**。此项是统一 Provider 契约的输入，不测成稿质量，也不发送真实 API 请求。 |
| 自审、来源与限制 | 官方来源：[Groq Chat API](https://console.groq.com/docs/api-reference)、[Structured Outputs 型号范围](https://console.groq.com/docs/structured-outputs)、[推理强度](https://console.groq.com/docs/reasoning)、[当前模型目录](https://console.groq.com/docs/models)。严格 JSON Schema 要求所有字段在 `required` 中且对象设 `additionalProperties:false`；运行时结构仍依赖服务白名单和本地校验。温度与 top_p 建议二选一，本次未擅自改写用户 profile。没有真实 API、质量、时延、token 账单或费用验证。 |

## 2026-10-06：讯飞星火 OpenAI-compatible JSON Mode 能力落地

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与事实 | 阶段 1 Provider 能力映射。输入为讯飞星火官方 HTTP 文档和当前目录：官方 Chat Completions 文档列出 `generalv3.5`、`lite` 等型号，temperature 范围 `[0,2]`、top_p `(0,1]`，支持 `response_format.type=json_object`；该通用接口没有列出 reasoning_effort。仓库原目录的 Lite ID 为 `general`，结构请求则一律发送 `json_schema`。 |
| 方案与影响 | 对直连 Spark + OpenAICompatible 的精确 `generalv3.5`、`lite` 标记采样可用、JSON-Object-only，不宣称推理强度支持。Lite 预设改用官方文档列出的 `lite`；现有默认 `generalv3.5` 保持，已保存的 `general` profile 不迁移。JSON Mode 仅要求 JSON 对象，完整 Schema 继续由应用侧校验。OpenRouter 或自定义代理中的相似模型不继承。 |
| 文件、上游下游与验收 | `ProviderCapabilities.cs` 精确映射字段和输出能力，`AIService.cs` 复用 JSON-Object-only 处理而发送 `json_object` 并附 Schema 提示，`ProviderPlatformCatalog.cs` 修正新选 Lite 的 ID；设置摘要说明真实映射。红测中两型号请求均因旧代码发送 `json_schema` 失败，设置摘要也显示未核验；实现后专项 **5/5**、AIService/目录/profile/设置/WPF 联合筛选 **297/297** 通过，主项目 build **0 warning / 0 error**。此项为后续 Provider 统一契约的输入；下游仍需核对 Yi 与其余当前预设，不能代替盲评或真实服务验证。 |
| 自审与限制 | 依据：[讯飞星火 HTTP 调用文档](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)。接口文档同时列出多代模型别名，目录只新增/修正了本次有直接证据的 `lite`；历史 `general` 别名保留兼容但继续显示未核验。没有真实 API 调用，也没有中文输出质量、时延、用量或费用数据。 |

## 2026-10-06：智谱 GLM-5.3/5.2 推理参数与 JSON Mode 适配

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与待解决问题 | 阶段 1：Provider 型号目录、模型级推理参数映射和结构化输出适配。智谱当前官方推荐列表已包含 GLM-5.3、GLM-5.2；仓库只列旧 GLM-4 型号。官方参数页说明 GLM-5.3 仅接受 reasoning_effort low/high/max，GLM-5.2 的 low/medium 会映射为 high、none/minimal 会放弃思考。官方 Chat API 的 `response_format` 枚举只有 `text` 与 `json_object`，没有 `json_schema`；应用原先对所有兼容 API 发送 `json_schema`，与智谱协议字段不符。 |
| 第一性原理决策 | 以请求成功和结构完整为目标：为已核验 GLM-5.3/5.2 只发送服务接受的 effort 值；GLM-5.2 的 UI Low 映射 `none`（关闭思考）、Medium 映射 `low`（服务按文档映射为 high）、High 映射 `max`。GLM-5.3 Low/Medium/High 映射 low/high/max，Custom 均使用服务默认 max。结构输出对智谱使用 JSON Mode，将 Schema 文本并入 system 指令，并依赖业务应用的本地契约校验；继续让其他兼容 Provider 使用原 JSON Schema 路径。temperature/top_p 在官方范围内保留现有 profile 值，并在界面提示官方建议只调整其中一个；没有评测依据时不静默丢弃用户字段。保留智谱现有默认模型，避免默认档未经成本/质量基线改写。 |
| 改动文件与参数 | `Services/ProviderPlatformCatalog.cs` 增加 GLM-5.3 与 GLM-5.2 可选项，保留 `glm-4-flash` 为默认。`Services/ProviderCapabilities.cs` 新增结构化输出能力标记，对直连 `Zhipu + OpenAICompatible + glm-5.3/glm-5.2` 精确标记 JSON Mode 与各自 effort 集合。`Services/AIService.cs` 按模型发 reasoning_effort；智谱结构请求使用 `response_format.type=json_object`，并在 system prompt 附加传入 Schema，以供模型跟随和应用侧校验。其它模型/平台不会继承智谱能力。设置页显示参数映射、采样建议及应用侧 Schema 校验。 |
| 验收及量化影响 | 红测确认旧代码缺少 GLM reasoning_effort、目录型号缺失、智谱结构请求错误发送 `json_schema`，且 OpenRouter 同名/近名 ID 不继承直连智谱能力。实现后 GLM 型号、推理映射、Custom 默认、JSON Mode、代理隔离及设置说明 **11/11 通过**。在跨 Provider/目录/profile/设置/WPF 联合回归中 **292/292 通过**；主项目构建 **0 warning / 0 error**；`git diff --check` 无空白错误（仅报告现有 LF→CRLF 转换提示）。对当前 GLM-5.3/5.2 结构请求，响应协议从不在智谱 OpenAPI 枚举中的 `json_schema` 切换为其支持的 `json_object`，并追加 Schema 文本约束；仅智谱官方能力路径发生此变化。 |
| 自审、限制与上下游 | 结构化输出是 JSON Mode，不具备 API 层严格 Schema 保证；Schema 合法性依赖应用端工作流校验和受控修复。没有真实智谱 API 调用；未测中文润色质量、响应时延、token 用量或成本。官方现行模型与参数来源：[模型概览](https://docs.bigmodel.cn/cn/guide/start/model-overview)、[GLM-5.3](https://docs.bigmodel.cn/cn/guide/models/text/glm-5.3)、[GLM-5.2](https://docs.bigmodel.cn/cn/guide/models/text/glm-5.2)、[核心参数](https://docs.bigmodel.cn/cn/guide/start/concept-param)、[对话补全 API](https://docs.bigmodel.cn/api-reference/%E6%A8%A1%E5%9E%8B-api/%E5%AF%B9%E8%AF%9D%E8%A1%A5%E5%85%A8)、[结构化输出](https://docs.bigmodel.cn/cn/guide/capabilities/struct-output)。下一步继续核验剩余 Provider；全量质量基线仍须在授权盲评数据就绪后进行。 |

## 2026-10-06：Kimi 现行型号目录与 K3 推理参数映射

| 子任务 | 结果 |
|---|---|
| 计划节点、输入与待解决问题 | 阶段 1：Provider 型号与推理参数映射。当前 Kimi 目录默认 `kimi-k2-0905-preview`，并提供 `moonshot-v1-*`；Kimi 官方目录现已将这些系列标为下线。官方当前文本/多模态模型包括通用型 `kimi-k2.6`、旗舰 `kimi-k3` 和代码专用 `kimi-k2.7-code` / `kimi-k2.7-code-highspeed`。参数参考说明这几类模型均不接受用户自定义 temperature/top_p；只有 K3 接受 `reasoning_effort=low/high/max`。 |
| 决策与取舍 | 新建 Kimi profile 默认切到官方描述为通用型的 K2.6；K3 仍列为旗舰可选项，K2.7 Code 型号标为代码专用。此默认选择是按官方适用场景区分预设，不代表 K2.6 与 K3 在本产品质量、延迟或费用上的实测优劣。Low/Medium/High 映射为 K3 的 low/high/max；Custom 省略 effort 并采用官方默认 max。K2.6 思考默认启用、K2.7 Code 始终启用思考，但本应用当前没有独立思考模式设置，因此不发送 `thinking`；对话接口其他未核验字段沿用原实现。存量 profile 不自动改写，其他平台、OpenRouter 与自定义代理不继承 Kimi 能力。 |
| 改动文件与参数 | `Services/ProviderPlatformCatalog.cs`：新建 Kimi profile 默认 `kimi-k2.6`，型号列表替换为四个当前 ID，核验日期 `2026-10-06`。`Services/ProviderCapabilities.cs`：直连 `Kimi + OpenAICompatible` 下，对 K3 记录固定采样与 low/high/max 能力；对 K2.6/K2.7 记录固定采样且不支持 `reasoning_effort`。`Services/AIService.cs`：仅 K3 将应用档位映射为 `reasoning_effort`；已核验 Kimi 请求省略 temperature/top_p。K2.6/K2.7 UI 摘要说明模型默认思考行为。测试覆盖对应请求体、目录、映射与摘要。 |
| 验收及量化影响 | 先增加红测，未改生产代码时目录默认、目录内容、K3 努力值字段及采样省略、K2 不支持字段和设置摘要用例共 11 项失败，确认缺陷被捕获。实现后 Kimi/目录筛选 **31/31 通过**；Provider、AIService、profile、设置与 WPF smoke 联合筛选 **280/280 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**。新目录用 4 个当前型号替换 4 个已下线选项；Kimi 通用请求从显式发送两个无效采样字段改为省略；K3 非 Custom 请求增加 1 个实际支持的 effort 字段。 |
| 自审、限制与上下游 | 能力解析限制在精确平台/协议/型号，不改变历史配置；K2.6 的复杂 JSON Schema 在官方文档中有可靠性限制，应用本地 Schema/业务校验必须保留。没有发真实 API 请求，也没有测得中文润色质量、实际费用或时延变化。此项完成阶段 1 的 Kimi 参数子任务；下一步按计划继续核验智谱 GLM 与其余 Provider 的当前型号和请求参数，仍使用同样的 fake-handler 证据，不把协议兼容性推断为模型能力。阶段 0 授权盲评尚未完成，因此不据此宣布质量收益。 |

## 2026-10-06：豆包 Seed 2.0 Lite 预设迁移与参数映射

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1：Provider 型号与推理参数映射。当前豆包预设默认 `doubao-seed-2-0-lite-260215`，官方模型目录标记该版本“即将下线”。火山方舟 Chat API 文档还明确该型号 temperature 固定为 1.0、top_p 固定为 0.95，显式传入用户值会被忽略。官方将 `doubao-seed-2-0-lite-260428` 标为推荐版本，支持 thinking、`reasoning_effort` 以及结构化输出。 |
| 方案与取舍 | 将 Provider 目录的新建预设切到推荐型号 `260428`，存量 profile 不迁移，以遵守“保留现有选择”；旧 `260215` profile 仍可保存和使用，但请求移除无效 temperature/top_p，界面明示固定值及即将下线。仅把 260428 的 Low/Medium/High 映射到官方 low/medium/high，Custom 使用模型默认。官方说明 JSON Schema 结构化输出尚处 beta，继续依赖本地契约校验，不将模型输出视为已验证。依据：[方舟 Chat Completions API](https://docs.volcengine.com/docs/ark/chat-api?lang=zh&redirect=1)、[模型参数支持](https://docs.volcengine.com/docs/ark/model-parameter-support?lang=zh)、[模型版本与推荐迁移](https://docs.volcengine.com/docs/ark/model-release-announcement?lang=zh)、[结构化输出支持](https://docs.volcengine.com/docs/ark/structured-output-beta)。 |
| 改动 | `ProviderPlatformCatalog` 新建豆包 profile 默认切到 `doubao-seed-2-0-lite-260428`，显示“推荐”，核验日期为 `2026-10-06`。`ProviderCapabilityResolver` 将旧 260215 Lite/Pro 标记为固定采样、不发送 temperature/top_p；对新 Lite 260428 记录实际采样与 reasoning effort 支持。`AIService` 复用精确 capability 映射发送 reasoning_effort。能力摘要描述旧型号固定值或新型号字段。异平台/代理的同名模型不继承火山能力。 |
| 验收 | 旧实现中旧型号仍会发送两个无效采样字段、新型号三档推理字段缺失、能力摘要未核验，相关新测试先失败；改后豆包请求字段、目录默认、能力摘要和已有目录回归 **26/26 通过**。 |
| 量化影响与边界 | 新建预设切换到官方推荐版本；对仍使用 260215 的请求，移除 **2 个不生效字段**；260428 在非 Custom 推理请求中加入 **1 个推理档位字段**。没有真实 API 调用，也没有质量、时延、吞吐或费用测量。官方建议采样温度与 Top‑P 只调一项；本次为避免无评测地废弃用户已有设置，260428 仍依照 profile 同时发送两项，后续应由隔离对照评测决定是否精简。 |
| 自审与下一步 | 默认只影响之后新建/重新选用豆包 preset 的情况，现有 profile/model ID 未改写；手动输入退役型号仍会收到能力提示，但用户账号可用性取决于火山方舟下线时间与开通状态。Schema 输出当前 beta，结构验证路径保留。下一步继续核验 Kimi、智谱及其余预设的官方模型与参数；阶段 0 的授权盲评未完成，因此不宣称模型输出质量提高。 |

## 2026-10-06：DeepSeek V4 当前型号与参数语义修正

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1：按 Provider 官方协议校准模型目录、推理参数映射和用户可见说明。DeepSeek 官方文档现列出 `deepseek-flash` 与 `deepseek-v4-pro`；`deepseek-v4-flash` 虽仍接受，但已退役并由服务转发到 V4.1 Flash。思考模式中 `temperature` 不生效；`top_p` 仍有效，但有效范围只有 `[0.95, 1.0]`。现代码仅识别 `deepseek-v4*`，故新的 `deepseek-flash` 未启用 thinking/effort 分支；旧分支又漏发思考模式可用的 top_p。 |
| 方案与取舍 | 将新创建 profile 的默认型号切到官方当前 ID `deepseek-flash`，保留 `deepseek-v4-pro`；历史配置中的旧型号不自动迁移，以保留用户选择且官方称旧 ID 仍接受。精确限定直连 DeepSeek `OpenAICompatible` profile；兼容代理、其他平台和未知模型不继承。Thinking 开启时省略无效 temperature、发送 low/medium/high 对应的 DeepSeek effort（low/high/max）以及将 top_p 限制在 `[0.95,1.0]`；Custom 档省略 effort、用模型默认值。自动空答重试关闭 thinking 时发送有效 temperature，省略已知无效 top_p。依据：[DeepSeek thinking mode](https://api-docs.deepseek.com/guides/thinking_mode)、[当前模型 ID 与退役别名说明](https://api-docs.deepseek.com/quick_start/agent_integrations/codex)。 |
| 代码与界面改动 | `ProviderPlatformCatalog` 将 DeepSeek 新 profile 默认从 `deepseek-v4-flash` 更新为 `deepseek-flash`，模型选择器显示“DeepSeek Flash”，并把该预设核验日期记为 `2026-10-06`。`ProviderCapabilityResolver` 仅针对直连 DeepSeek Chat Completions 识别当前 ID 和仍接受的旧 V4 ID；`AIService` 按思考开/关发送有效参数。设置摘要显示努力值映射、temperature 无效和 top_p 范围。已有配置和映射值不迁移。 |
| 验收 | 两个新请求体用例和两个默认型号断言先在旧代码上失败；改动后 DeepSeek 请求/重试、平台目录、WPF 模型选择和摘要相关筛选共 **178/178 通过**。无真实 DeepSeek 请求；不声称输出质量、速度或费用改善。 |
| 可量化影响与边界 | 新 profile 使用官方当前型号，不再以退役 alias 作为首选。对 `deepseek-flash` 普通生成，请求明确带 thinking 开关和 effort（Custom 除外）；温度字段从 **1 个减为 0**；Top‑P 从原本受识别错误而随一般采样发送，改为明确限制并在 thinking 模式实际发送（一个数值字段）。Top‑P 小于 0.95 时按官方行为最小化到 0.95。一次已验证的空答降级重试仍保持最多 **2 次请求**，第二次移除无效 effort/top_p，只发有效 temperature。模型质量/时延/成本必须通过实际授权盲评和真实请求测量，当前无量化证据。 |
| 自审与下一步 | 退役别名仍被显式支持，原配置无需编辑即可继续走 V4 controls；`deepseek-flash` 只由 DeepSeek 平台 profile 激活，不把同名代理当直连。下一步继续核对其他明确预设的厂商文档；每次仅修有直接官方证据且能写请求契约测试的字段。未知/自定义端点继续保留兼容规则和未核验提示。阶段 0 的外部授权盲评仍未完成，故不调整提示词质量目标、不启动微调。 |

## 2026-10-06：阿里云百炼 Qwen 3.8 推理强度映射

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1：Provider 专属推理参数映射。百炼官方 OpenAI-compatible Chat 文档列出 Qwen 3.8 Max、Flash、27B、2.4T A95B 型号，其 `reasoning_effort` 有效值为 low/medium/xhigh；项目之前仅发送 temperature、top_p 与 max_tokens，不发送推理强度。 |
| 方案与取舍 | 只对 `ProviderPlatform.Qwen + OpenAICompatible + 精确 Qwen 3.8 型号家族` 启用，Low/Medium/High 分别映射 low/medium/xhigh，Custom 使用 Provider 默认档；不同时发送 `thinking_budget`。同名模型经过 OpenRouter、自定义代理或其他平台时不沿用百炼能力。新型号加入选择目录，但保留 `qwen-plus` 为默认，避免未经评测变更用户常用配置。来源：[百炼 OpenAI Chat 参数文档](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)。 |
| 改动 | `ProviderCapabilityResolver` 增加精确模型集合和支持值；`AIService` 对已识别型号发送 `reasoning_effort`；设置能力摘要公开字段名及 High→xhigh 映射。`ProviderPlatformCatalog` 增加 Qwen 3.8 Max、Flash、27B 可选项并记录核验日 `2026-10-06`。`qwen-plus`、未知型号和其他兼容代理请求未改。 |
| 验收 | 新增 3 档映射的请求测试，旧代码 3/3 失败；目录、Custom 默认、OpenRouter 隔离测试覆盖原代码行为。实现后 AIService 映射、Provider catalog 与设置摘要专项 **6/6 通过**。DeepSeek + Qwen + Provider/WPF 综合回归与主项目构建将在本轮所有 Provider 变更后统一复核。 |
| 量化影响与边界 | 覆盖 4 个百炼文档型号家族、3 个可选模型 ID：常规推理请求增加 **1 个实际生效的推理控制字段**；Custom 发送 **0 个**。不会同时发送 `thinking_budget`。官方建议 temperature 与 top_p 只设置一个，但当前 profile 允许两者由用户共同配置；为保留既有用户设置，本子任务未静默丢弃其中一个，需用固定评测集确认采样控制方案后再调整。没有真实 API 调用和质量、时延或成本数据。 |
| 自审与下一步 | 精确限定直连百炼，未知型号继续提示未核验；不把 OpenRouter 等聚合服务中的相同 ID 误认为百炼。继续核查其他预设厂商的具体参数/响应差异，并复核最终合并筛选与构建；阶段 0 盲评数据仍未具备，不能据参数映射宣称个性化质量已提升。 |

## 2026-10-06：Gemini 3.x 推理强度映射与采样参数修正

| 子任务 | 结果 |
|---|---|
| 节点与问题 | 阶段 1 Provider 能力目录与请求参数映射。Gemini GenerateContent 原先对所有型号总是发送 `temperature`/`topP`，且 `InferenceLevel` 只改这些采样参数；Google 最新 Gemini 3.x 指引明确建议对所有 Gemini 3.x 请求移除这些采样参数，改用 `generationConfig.thinkingConfig.thinkingLevel`。因此设置页“推理强度”在 Gemini 3.x 上未控制推理，反而改动了官方建议保持默认的采样行为。 |
| 方案与取舍 | 按 Google 当前官方模型 ID 精确识别文本 GenerateContent 型 Gemini 3 系列；不对未知 Gemini 名称、2.5 型号或同名 OpenAI-compatible 代理套用。对已核验 Gemini 3 型号固定省略温度/Top-P，并把 Low/Medium/High 映射为 `LOW`/`MEDIUM`/`HIGH` thinkingLevel；Custom 不传 thinkingLevel、使用模型默认值。根据当前模型目录，将错误的 `gemini-3.5-pro` 选项替换为明确标为预览的 `gemini-3.1-pro-preview`，并加入稳定 `gemini-3.8-flash`；保持原有默认模型不变，避免无盲评依据地改变新配置成本/质量基线。依据：[Gemini 3.5 parameter guidance](https://ai.google.dev/gemini-api/docs/generate-content/whats-new-gemini-3.5)、[thinkingLevel 参数和型号支持](https://ai.google.dev/gemini-api/docs/generate-content/thinking)、[模型目录](https://ai.google.dev/gemini-api/docs/models)。 |
| 改动 | `ProviderCapabilityResolver` 精确支持 `gemini-3.8-flash`、3.7/3.6/3.5 Flash、3.5 Flash-Lite、3.1 Flash-Lite、3.1 Pro Preview、3 Flash Preview。`AIService` 仅在这些 `GeminiGenerateContent` 模型请求中省略 `temperature`/`topP` 并发送 `thinkingConfig.thinkingLevel`；连接测试不附加思考级别，Gemini 2.5 与未知模型仍保留其既有采样字段行为。`ProviderPlatformCatalog` 更新可选 Flash/Pro 型号，不改变 defaultModel。设置页能力摘要说明采样参数未应用以及推理字段实际位置。 |
| 验收 | 旧实现上的 9 项负向协议/界面行为失败，Gemini 2.5 兼容用例通过；实现后 8 个 Gemini 3 型号请求映射 + Gemini 2.5 保持兼容 + 设置页摘要 + model catalog 校验，共 **11/11 通过**。AIService、telemetry、Provider fallback、盲评候选、Provider profile、配置和设置联合筛选 **238 通过、11 跳过、0 失败**；主项目构建 **0 warning / 0 error**；改动文件 whitespace check 通过。 |
| 量化收益与边界 | 覆盖 8 个当前官方 Gemini 3 文本模型 ID；每个普通生成请求从发送 2 个不建议调整的采样字段变为 0 个，并由 Low/Medium/High 对应发送 1 个推理字段（Custom/连接探测为 0 个）。没有真实 API 调用，不能量化输出质量、延迟或 token 成本收益；thinkingLevel 是预算提示而非严格 token 上限。 |
| 自审与接续 | 实现没有据此猜测所有 Gemini 2.5 或未来型号；官方文档列出的图片生成、Live、TTS、Transcribe 模型也不进入文本润色候选目录。Gemini 3.1 Pro 明确标成预览，默认模型保持不变。下一步按同一证据规则检查其余 OpenAI-compatible 预设；未知端点保持既有字段行为及未核验提示。 |

## 2026-10-06：扩展 Anthropic 精确模型能力映射

| 子任务 | 结果 |
|---|---|
| 节点与问题 | 阶段 1 Provider 能力目录与请求参数映射。当前实现只识别 Claude Opus 4.7/4.8；截至本次官方 API 文档，Messages API 还列出 Opus 5/5.5、Sonnet 4.6/5/5.5。此前这些新模型被按 unknown 处理，仍收到 profile 的 `temperature`/`top_p`，而官方说明 Opus 4.6 之后发布的模型不支持自定义 temperature，top_p 只允许 `>=0.99` 作兼容。 |
| 方案与依据 | 继续以 `(AnthropicMessages, 精确请求 model ID)` 进行 capability 查询，不把规则外推给代理/兼容 Provider。扩充官方公开模型 ID；采样字段按官方“用户值不生效/可能导致 400”规则省略，推理 effort 只传应用当前可选的 low/medium/high。Sonnet 4.6 的能力集合排除官方未列出的 xhigh。依据：[Messages API model/parameter reference](https://platform.claude.com/docs/en/api/messages/create)、[Effort levels by model](https://platform.claude.com/docs/en/build-with-claude/effort)。 |
| 改动 | `ProviderCapabilityResolver` 将 Opus 5/5.5、Sonnet 4.6/5/5.5 及其带连字符的版本 ID 纳入已核验集合；明确省略 temperature/top_p，并让模型能力说明与请求映射继续共用同一证据项。Sonnet 4.6 采用精确 effort 集合；其余本批模型按官方文档支持集合配置。没有改 profile 默认采样数值或设置界面档位。 |
| 验收 | 扩展后的请求体测试在旧能力表上对 5 个新增 model ID 失败、既有 Opus 4.7/4.8 通过；实现后该测试 **7/7 通过**，断言采样字段缺失且 `output_config.effort=high`；设置页能力说明 **1/1 通过**。AIService、telemetry、Provider fallback、盲评候选、Provider profile、配置和设置筛选 **227 通过、11 跳过、0 失败**；主项目构建 **0 warning / 0 error**；改动文件 whitespace check 通过。 |
| 量化收益与边界 | 精确支持的 Anthropic 模型 ID 从 2 扩到 7（+5）；对这 5 个新增型号，按当前默认 profile 生成时从发送两个可能不兼容的采样字段变为省略两字段，避免按已知官方限制触发拒绝。没有真实 API 调用，也没有新增质量/成本/时延数据。 |
| 自审与接续 | 官方 effort 说明因型号不同存在差异；实现只映射 UI 当前提供的 low/medium/high，不声称 xhigh/max 可由当前设置选择。后续仍需逐个 Provider 检查 capability 证据日期和界面“未应用”说明，未知模型/代理端点不继承此规则。 |

## 2026-10-06：Anthropic 与 Gemini 完成状态规范化

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1 Provider 完成状态规范化。Anthropic Messages 与 Gemini GenerateContent 解析器此前只提取文本，忽略终止/过滤元数据，可能把部分输出、安全拦截或未处理的工具调用交给润色/提示词优化流程。 |
| 协议依据与取舍 | Anthropic 官方协议将 `end_turn`/`stop_sequence` 视为正常结束，`max_tokens`/`model_context_window_exceeded` 为截断，`refusal` 为拒绝；`tool_use`/`pause_turn` 需要工具执行或续传。Gemini 官方枚举中 `STOP` 是正常结束，`MAX_TOKENS` 是截断，策略过滤状态可能仍携带部分文本，提示拦截则不返回候选。因为桌面客户端尚无这两种协议的工具编排/续传，相关已知状态映射为 `InvalidResponse`；未知 Anthropic `stop_reason` 和 Gemini `finishReason` 保持原提取兼容，任何显式且非默认的 Gemini `blockReason` 则按官方语义作为拦截处理。依据：[Anthropic stop reasons](https://platform.claude.com/docs/en/build-with-claude/handling-stop-reasons)、[Gemini GenerateContent API](https://ai.google.dev/api/generate-content)。 |
| 改动 | `AIService.ParseProtocolContent` 在提取正文前先判定状态：Anthropic 截断 → `Incomplete`、拒绝 → `Refused`、工具/暂停响应 → `InvalidResponse`；Gemini 截断 → `Incomplete`、安全/版权复现/语言/敏感信息/内容策略拦截 → `Refused`、工具调用或格式异常 → `InvalidResponse`、promptFeedback 的明确 blockReason → `Refused`。正常结束及缺少状态的旧网关响应仍按原方式解析。 |
| 验收 | 新增协议用例先在旧解析器上失败；修复后 22 个定向用例覆盖两种协议的截断、拒绝、拦截、工具状态、正常完成及未来未知枚举兼容。随后执行 AIService、telemetry、Provider fallback 相关筛选和主项目构建。没有调用真实 Provider。 |
| 量化收益与边界 | 可观察终态从单一“解析到文本”扩为成功、截断、拒绝、当前客户端不可完成四类；已知非最终输出不会进入业务工作流，异常中不回显部分/拒绝文本。当前测试只证明协议字段处理，没有真实模型错误率、输出质量、成本或时延变化数据。 |
| 自审与接续 | API 字段按各家官方协议精确映射，没有对兼容服务推断未知字段含义。下一步继续处理 Provider 适配与配置/实际调用链中的剩余差异；需要量化质量收益时再用隔离评测任务验证，而不由协议单测代替。 |

### 同日补充：连接测试的 Provider 完成状态诊断

- **问题与改动：** `TestConnectionAsync` 原先将 `GenerationFailureException`（继承自 `InvalidOperationException`）归为格式不匹配。现在 refusal、截断及其他已分类的生成终态报告为 `ProviderError` 并给出不含内容的状态说明；真正的 JSON/文本协议格式异常仍为 `InvalidResponse`。所有语义终态均不能把连接草稿标为已验证。
- **验证：** 新增 Anthropic refusal 与 Gemini 截断连接测试，旧实现均先失败；修复后 AIService、telemetry、Provider fallback 与盲评候选相关筛选 **129/129 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**；针对改动文件的 `git diff --check` 通过。验证用户提示和诊断不包含部分输出/拒绝正文。
- **收益与边界：** 错误诊断从错误的“格式不匹配”纠正为“Provider 未完成探测”，减少用户错误排查方向；该连接探测仍是协议与可达性检查，不代表实际业务质量。

## 2026-10-06：Chat Completions 拒绝/截断状态与 fallback 边界

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1 Provider 完成状态规范化，并修正阶段 2 的拒绝处理边界。Chat Completions 解析此前只提取 `choices[0].message.content`，可能忽略 `finish_reason=length` 并把部分答案送入结构/修复流程；OpenAI refusal 也可能作为正文返回。显式备用 Provider 包装器还会将所有 `GenerationFailureException`（包括安全拒绝）都转发到备用端。 |
| 输入与取舍 | OpenAI Chat 响应中的 `finish_reason=length` 表示输出预算截断，`content_filter` 或 `message.refusal` 表示拒绝。截断与拒绝是完成状态，不是普通答案或 Schema 参数拒绝。拒绝时推荐停止而不向备用 Provider 重发相同内容，防止跨服务绕过拒绝与不必要的二次内容传输；截断则保留用户已显式配置的 fallback 行为。 |
| 改动 | `AIService.ParseProtocolContent` 在返回答案前检查 finish reason 与 refusal：`length` → `GenerationFailureKind.Incomplete`，`content_filter`/refusal 字段 → `Refused`，均不把部分/拒绝文本放入异常或 telemetry。Incomplete 与 Refused 在无内容请求遥测及 blind candidate 错误记录中分别输出 `incomplete` / `refused`。`ProviderFallbackGenerationClient` 不对 `Refused` 执行 fallback；其他服务故障处理保持既有行为。 |
| 验收 | 新 Chat 截断与拒绝测试先在旧实现上失败；拒绝-fallback测试也先失败。修复后包括 Chat/Responses 完成状态、Provider fallback、配置、usage parser 的关联筛选 **113/113 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**；diff whitespace check 通过。通过隔离项目输出目录设定 TEMP/TMP，以避开当前运行身份不可写系统 Temp 的测试环境限制。 |
| 量化收益与边界 | 3 类完成结果（成功/截断/拒绝）具有独立状态映射；测试证明被标记截断的 Chat 响应不再作为正文交给业务工作流，拒绝时 fallback factory 调用数从 1 降到 0，避免最多一次备用 Provider 请求。没有真实模型的拒绝率、截断率、Token 成本、质量或时延数据；fake handler 只验证协议处理。 |
| 自审与接续 | 既有 Chat-compatible 网关可能不遵守 OpenAI 字段语义，因此解析器只按精确 `length`、`content_filter` 与 `message.refusal` 信号分类，不根据未知 finish reason 作推断。下一步继续扩展精确 Provider/model capability 与其他协议完成状态；任何真实 Provider 行为判断仍需模型、协议版本和授权任务下的独立验证。 |

## 2026-10-06：OpenAI Responses API 独立协议适配

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1 Provider 结构化输出适配。旧实现已有 Responses 风格解析分支，但请求总是发送至 `/chat/completions`；解析响应的能力并不等于能发送 Responses API 请求。约束是保留旧配置与 Chat Completions、只把新协议开放给官方 OpenAI、避免内容存储，并让未核验模型使用服务默认采样参数。 |
| 输入与协议依据 | OpenAI 官方迁移文档明确 Responses 使用 `/responses`，Schema 从 Chat 的 `response_format` 移到 `text.format`；create API 定义 `max_output_tokens`、`store`、temperature/top_p 与 `reasoning.effort`。输出上限包含 reasoning tokens。依据：[Responses 迁移](https://developers.openai.com/api/docs/guides/migrate-to-responses)、[结构化输出](https://developers.openai.com/api/docs/guides/structured-outputs)、[Responses create](https://developers.openai.com/api/reference/resources/responses/methods/create)。 |
| 实现 | 新增 `ProviderProtocol.OpenAIResponses`，设置页仅在官方 OpenAI profile 显示独立协议选择；现有 profile 默认仍是 `OpenAICompatible`。端点验证只对官方 OpenAI 放行 Responses，不把能力外推给 OpenRouter/自定义兼容端点。请求使用 `instructions`、`input`、`max_output_tokens`、`store=false` 与 Responses `text.format=json_schema`；成功响应解析 `output_text`/typed output，拒绝与截断规范化为独立错误。无内容 telemetry 区分 refused、invalid_response 和 provider_error。 |
| 参数规则与取舍 | 本次只为精确模型 `gpt-4.1-mini` 明确启用 temperature/top_p；未在能力表中的 Responses 模型省略采样与 reasoning 参数，使用模型默认值。`gpt-6-astra` 保留已核验的非采样限制，并使用 `reasoning.effort`；Chat 路径仍使用现有映射。没有自动把新协议设为默认，也不发送真实 API 请求，因此没有引入跨兼容网关假设。 |
| 验收 | 先加测试并观察缺少 `OpenAIResponses` enum 的编译失败；实现后 Responses 专项、设置页协议选项和兼容性回归 **8/8 通过**；AIService/Provider profile/用量解析/设置协议合并筛选 **104/104 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**。测试捕获了完整 HTTP URI/JSON、Schema、`store=false`、model capability、拒绝/截断状态与 usage telemetry；未调用外部 API。 |
| 量化收益与边界 | 可用 API 协议由仅 Chat Completions 扩为 Chat + Responses；新增 1 个 Responses 请求适配、1 个精确普通模型采样能力条目，以及 GPT-6 Astra 推理映射。每次请求显式 `store=false`；未核验模型不再发送采样/推理设置。没有真实用户任务下的质量、时延、费用或服务拒绝率数据，不能量化质量收益；GPT-4.1 mini 的采样能力和 Schema 语法仍只经过 fake-handler 契约验证。 |
| 自审与下一步 | 自审修正了“支持 Responses 输出解析即可视作 Responses 接入”的错误前提，并为安全拒绝增加 `Refused` 类别，避免 structured-schema fallback 把模型拒绝当作 Schema 不兼容。下一步核对 settings 实际保存/重载、连接测试与 credential binding；随后以用户明确选择的 Responses profile 做真实连接/模型试用前，先完成官方模型 ID 能力表与应用内提示。本次 fake-handler 验证不代表线上 OpenAI 可用性。 |

## 2026-10-06：盲评快照纳入结构化输出契约指纹

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1 评测条件锁定。现有候选 bundle hash 覆盖 system prompt 和 user message，但结构化 JSON Schema 通过 Provider 原生字段独立传输，改变它不会改变旧 prompt hash；cohort 可能把不同输出约束下的结果混在一起。前提是润色与提示词优化的业务契约仍由同一 Provider 无关 `StructuredOutputContract` 驱动。 |
| 实现 | 新增 `StructuredOutputContractFingerprint`，对必需/允许字段、有效字段类型、允许字符串枚举、数值范围和本地最大答案长度按键名/数组值排序后做 SHA-256；不纳入仅用于诊断的描述性 `Name`，避免行为不变的改名拆分实验。润色与提示词优化服务公开当前 companion-mode 对应契约选择器，快照编译器据此逐样本/逐对话轮次生成指纹。`BuildBundleHashLine` 将契约指纹纳入总提示束，另有输出契约专用束摘要。 |
| 证据链格式 | 快照 JSON/快照清单升为 v2；候选封存映射与 run-info 升为 v2；最终运行清单升为 v4。运行/封存分别写入 `output_contract_bundle_sha256`，单候选收口验证两者相同，cohort 收口验证 run-info/封存映射相同且 cohort 中所有候选契约束相同；最终清单 validator 将该 hash 作为必填字段。更新运行清单模板和 AI 评测 README。 |
| 验收 | 测试先证明缺少指纹 API，以及描述性名称误导致不同 hash；随后证明名称不影响指纹而本地校验边界会改变指纹。实现并迁移夹具后，指纹/快照/候选运行/单候选收口/cohort 收口/最终清单校验，加入口一致性与盲评工作流筛选 **95/95 通过**；`dotnet build Huaxiazi.csproj --no-restore` 为 **0 warning / 0 error**。尚未把正式盲评样本用于运行。 |
| 自审与边界 | 指纹包含 `StructuredOutputContract` 当前的数据字段与本地答案长度验证，不包含 Provider 实际 schema 序列化差异；这样 OpenAI、Anthropic、Gemini、llama.cpp 的不同传输表示可共享语义指纹。Provider 若以后增加契约外的请求约束，必须先并入该抽象或另行签名，不能静默修改各 adapter。快照/候选/最终清单格式均不可与旧实验混收；finalizer 明确拒绝 v1 封存映射和旧 run-info，validator 通过 manifest v4 标记格式。 |
| 量化收益 | 新增 1 个独立 SHA-256 指纹/每个样本（多轮样本每轮各 1 个），候选额外写入一个固定 64 字符 bundle 摘要；文件开销极小，生成时延不变。质量效果无新数据可量化。 |

## 2026-10-06：提示词优化入口缺项请求零调用保护

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1 的润色/提示词优化入口一致性。检查了 `MainViewModel.OptimizeAsync` 两个模式分支、`PolishWorkflowService`、`PromptOptimizationWorkflowService` 及 `BlindWorkflowExecutor`。Planner 对“帮我优化一下”已识别为缺少具体任务；润色主界面、润色服务已有前置保护，但提示词优化在澄清关闭时仍创建客户端并生成，盲评执行器在开关两种状态下也都继续调用服务。 |
| 改动 | 提示词优化 UI 在澄清开启时继续展示 Planner 问题；关闭时显示具体缺项并在密钥读取/客户端工厂前返回。`PromptOptimizationWorkflowService` 增加直调保护，对 `NeedsClarification` 计划返回空内容、`IsBlocked=true` 和 `missing-information` 质量问题，不发模型请求。`BlindWorkflowExecutor` 对同类评测请求按开关分别返回 `needs_clarification` 或 `invalid`，在任何模型调用前结束。完整信息请求的执行路径未改。 |
| 验收 | 新回归先失败：提示词优化缺项路径的客户端工厂调用为 1，盲评执行器把不完整请求记为 `completed`。修复后，提示词优化 UI 缺项开/关两种路径、直接服务拒绝、盲评开/关两种状态、足量信息正向生成，以及既有润色缺项/正向路径定向筛选 **11/11 通过**；`dotnet build Huaxiazi.csproj --no-restore` 为 **0 warning / 0 error**。 |
| 自审与边界 | 联合筛选另外触发 `BlindWorkflowExecutorTests.ExecuteAsync_RunsTheSelectedProductWorkflowWithoutCopyingGold(polish)` 的提示快照比较失败：产品润色工作流实际 system prompt 比编译快照多 `<output_contract>` 段。该用例与提示词优化改动无依赖且此前代码未在本子任务中改动；不能据本次测试将其归因于新改动，也不能宣称盲评快照完全与产品逐字一致。下一步先单独查明并修复/解释该快照差异，再扩展其他 Provider 入口能力。规则只拦截 Planner 已识别的缺项，不能识别所有自然语言歧义。 |
| 量化收益 | 每个被 Planner 判定需要澄清的提示词优化请求，UI 客户端工厂和模型调用由 **1 次降为 0 次**；直调服务及评测执行器也不再尝试生成。实际节省 token、延迟和费用取决于 Provider、prompt 与模型，当前没有对该请求做真实 API 计量，不给出外推数值。 |

## 2026-10-06：盲评执行器提示快照测试改用产品实际结构化路径

| 子任务 | 结果 |
|---|---|
| 问题与输入 | 上一节点的 `BlindWorkflowExecutorTests.ExecuteAsync_RunsTheSelectedProductWorkflowWithoutCopyingGold(polish)` 在 system prompt 等值断言失败，差异是实际消息比 `BlindPromptSnapshot` 多 `<output_contract>`。核查运行时后发现盲评候选工厂创建的 `AIService` 和 `LocalTextGenerationClient` 都实现 `IStructuredTextGenerationClient`；失败用例的 `CapturingClient` 只实现文本接口，会强制工作流走兼容回退，把 JSON Schema 包装进 system prompt，因此该测试模拟的并非候选运行的通常路径。 |
| 修正 | 将该测试客户端改为实现 `IStructuredTextGenerationClient`，针对 polish / prompt optimize 返回相应结构化对象，继续断言实际工作流的 system prompt 与生产提示快照相等。没有改生产提示、Provider 请求或快照算法。原生 Schema 被拒时的文本回退另由 `PolishWorkflow_LocalModeDoesNotDecorateTheExistingPrompt` 检查 `<output_contract>` 行为。 |
| 验收 | 原失败的 blind workflow 集成筛选 **2/2 通过**；盲评执行器与快照编译器相关筛选连同兼容回退覆盖 **14/14 通过**。第一次混合运行中的兼容回退测试因系统 Temp 目录拒绝访问，改把测试临时目录定向到 `out/test-artifacts/verification-temp` 后复跑通过；不把环境权限故障记为产品失败。 |
| 自审、边界与下一步 | 差异来自错误的测试 doubles，并非生产工作流与提示构建器之间的普通结构化路径分叉。进一步复核发现 `BlindPromptSnapshot` / 候选清单哈希只包含 base system prompt 和 user message；Schema 在原生请求中单独传输，故未包含在当前 prompt bundle hash 中。Schema 会影响输出约束和候选结果，正式比较前应加入 Provider 无关的输出契约指纹并由 cohort finalizer 强制一致；原生 Schema 拒绝时，兼容回退仍应绑定同一契约。阶段 0 未通过，因此尚未用这些工具产生正式盲评结论。 |

## 2026-10-06：澄清关闭时对已知缺事实任务做零请求预检

| 子任务 | 结果 |
|---|---|
| 计划节点与问题 | 阶段 1 生成/服务质量门禁。上一轮固定本地 Qwen3-4B 实测：澄清关闭且“问题分析”无问题证据时，模型初稿脱题；修复仍带占位，最终被拦截，共 **2,130 tokens / 12.90 秒**。问题是请求在已知 Planner 规则会判“关键事实不足”时仍发给模型，最后才失败。 |
| 输入、约束与推荐 | 输入为明确目的、Planner 的 `NeedsClarification` 和 `ClarificationQuestions`。用户关闭澄清表示不弹问题；因此推荐保留开关语义、显示声明式缺项说明，并在创建 Provider 客户端/读密钥前停止。存在依据充足的请求必须仍能生成。 |
| 代码产出 | 新增 `MissingInformationSummary`，将“问题分析”映射为“具体问题或异常表现”、 “说明延期”映射为“延期原因”，并覆盖短请求及提示词优化的基础输入缺项。`MainViewModel` 在客户端工厂前显示缺项、说明本次未发模型请求；不弹澄清问题。`PolishWorkflowService` 也在直接调用路径做相同预检，返回 `Invalid` 和缺项原因，防止绕过 UI 后发起无效生成。 |
| 验收 | 新增主界面失败路径与服务直接调用测试，先复现缺项场景仍调用模型/只得到泛化质量错误；修复后重点新测 **3/3 通过**。含提示、工作流、规则、澄清往返、MainViewModel 正反路径和 WPF 冒烟的联合筛选 **80/80 通过**；主 WPF build **0 warning / 0 error**。正向用例有供应商故障原因时仍一请求生成成功，缺问题证据且澄清关闭时工厂/生成调用均为 **0**，错误中显示具体缺项且 `HasClarification=false`。 |
| 本地实测与收益 | 相同 Qwen3-4B revision/SHA、b11424 Vulkan 和 temperature 0.4、top_p 1.0、max_tokens 2048、seed 42、repeat_penalty 1.17、ctx 4096、batch 512 下，充分事实 `000007` 返回 Final，693 输入+409 输出 tokens、约 **6.83 秒**；澄清关闭的缺事实 `000008` 返回 Invalid、显示缺项、生成 HTTP 请求 **0**、工作流约 **0.87 ms**。与上一轮该案例 2,130 tokens/12.90 秒相比，避免约 **2,130 tokens 和 12.9 秒生成时间**；本 smoke harness 预先启动 runtime，约 3.24 秒启动不计入 0.87 ms。产品 MainViewModel 测试另外证明 client factory 调用 0，因此正式入口无需启动推理服务。机器记录：[零请求预检 smoke](../out/test-artifacts/qwen3-4b-service-positive-control-with-context-preflight-v5-auto-20261006.json)，SHA-256 `7dce634289f64eb574823be782f3c7b59cedb2c08528271adcaaebe6d2662a4d`。 |
| 边界与下一步 | 仅针对当前 Planner 已识别出的缺事实条件；未识别的歧义仍可能进入模型。用途到缺项说明的映射有限，需继续扩充并测正向/负向样本。烟测是内部合成数据，`quality_scored=false`、Phase 0 贡献 **0**；没有质量收益结论。下一步补齐其他高频任务的明确缺项摘要和澄清关闭负例，并审查所有入口都在 credential/client/runtime 初始化前返回。 |

## 2026-10-06：润色工作流统一执行缺事实前置澄清

| 子任务 | 结果 |
|---|---|
| 计划节点与输入 | 阶段 1 生成质量保护，与阶段 4 本地模型集成验证并行。输入是内部 development 案例 `000003`（说明延期但未给原因）和 `000008`（要求问题分析但没有具体问题），以及既有证据：候选 smoke 直接调用 `PolishWorkflowService`，没有经过 `MainViewModel` 的澄清前置检查，仍各发送 1 次本地生成请求。样本只用于工程回归，不是人工金标。 |
| 具体问题与约束 | 同一润色能力从主 UI 和直接工作流/本地候选路径进入时，澄清安全行为不一致；关键事实缺失可能继续消耗推理时间和 token，并允许模型编造或弱化任务。保留用户关闭澄清功能时的旧行为；不得依赖盲评材料，不向云端发请求。 |
| 实现产出 | `PolishWorkflowService.ExecuteAsync` 在生成前解析既有 ProfessionalizationPlan；缺少 plan 时用原文、Purpose、Recipient、Scenario、Formality 构建计划。澄清开启且规则命中时，在 archive 检查与模型调用前返回问题。`ProfessionalizationPlan` 新增 `PurposeIsExplicit`；`ProfessionalQualityValidator` 对用户明确指定的“问题分析/说明延期”做窄范围任务信号校验，整项任务脱落标为 Unsafe 并进入一次修复；另将省略号和待补内容占位标为 Unsafe，修复仍失败时不返回 Final。润色系统提示说明关闭澄清不代表允许改任务或编造已完成动作。主界面原有的密钥/客户端创建前检查保留。 |
| 验收 | 工作流“问题分析”缺证据的测试在旧实现先失败：实际返回 Final 并调用生成。定向提示/工作流/专业化测试 **50/50 通过**，覆盖整项任务脱落、占位拒绝、明确说明证据不足可通过、推断用途不强制关键词。主 WPF build **0 warning / 0 error**；最终提示/工作流/专业化、澄清往返和 WPF 冒烟组合 **77/77 通过**。真实 Qwen3-4B Q4_K_M（固定 revision/SHA）、llama.cpp b11424 Vulkan、temperature 0.4、top_p 1.0、max_tokens 2048、seed 42、repeat_penalty 1.17、ctx 4096、batch 512 烟测：`000003`/`000008` 在澄清开启时均 `NeedsClarification`，各 **0 次生成请求**；相同条件正向控制 `000007` 返回 Final、1 次请求，保留 2026-09-20、条件及 2 天。澄清关闭的 `000008` 初稿脱题且带占位，经 1 次修复后仍不合格，最终 `Invalid`、正文为空、2 次请求；这证实坏稿被拦截，也暴露关闭澄清时的可用性问题。测试夹具自审发现续写测试客户端固定返回“不应生成”，与新门禁不兼容；改为含已补原因且保留延期任务的最终答复后，续写路径 1 次请求通过。全部烟测为内部合成、无参考答案评分，云请求 **0**，阶段 0 贡献 **0**。早前整类混合测试筛选另有 **40/61**，21 个失败输出含 SQLite 打开失败/临时目录权限错误；隔离子集现在通过，但未因此将旧整类筛选记作通过。 |
| 量化收益与边界 | 澄清开启的两个问题样本，生成请求从历史烟测每例 **1 次降至 0 次**。历史单次观测分别为 000003 **631+423=1,054 tokens / 7.44 秒**、000008 **631+284=915 tokens / 4.91 秒**；这些是被避免请求的先前观测，不是一般节省承诺。正向任务 000007 当前约 **1,102 tokens / 7.02 秒**。澄清关闭的 000008 当前因一次重试累计 **2,130 tokens / 12.90 秒**，最后拦截而非显示坏稿；后续可优化为预检拒绝以节省该请求，但需确定关闭澄清时产品呈现方式。Vulkan 工作集约 **3.22 GB**，不是显存。 |
| 产物 | [澄清前置烟测 JSON](../out/test-artifacts/qwen3-4b-service-clarification-auto-20261006.json)，SHA-256 `a0cf83c49308b71c2be64a92c43261447c54c9756ca4aa3460087735d603cb1c`；[正确上下文正向/关闭澄清烟测 JSON](../out/test-artifacts/qwen3-4b-service-positive-control-with-context-guarded-v4-auto-20261006.json)，SHA-256 `ed4e89bbb0d9fa1f4a1e0cd54aa01a71c7e6375fa6a38f126c87978072aec612`。机器记录均声明 `quality_scored=false`、`phase_0_gate_contribution=0`。 |
| 自审、上下游衔接与下一步 | 首个正向 smoke harness 漏传 `Purpose` 元数据，不能代表产品路径；已用与 MainViewModel 一致的 purpose/recipient/formality 上下文重跑，结论只依据更正后的报告。上游是 Planner 的明确任务/证据判断，下游是验证器、一次修复、MainViewModel 错误展示。推荐下一步：当澄清关闭但 Planner 已判定关键事实不足时，在读取密钥/创建客户端前返回不提问的明确失败说明，避免当前已知脱题案例再花 **2,130 tokens/12.90 秒**后被拦截；先用测试确认关闭澄清时不展示问题、提示具体缺失信息、且足够信息仍正常生成。之后再扩展任务信号并验证误拦。保持当前 fail-closed，不接受现有坏稿。 |

## 2026-10-06：近重复副本导入一次汇总全部无效理由

| 子任务 | 结果 |
|---|---|
| 问题 | 复核用户近重复裁定副本时，发现 19/19 理由都是非空占位语。旧导入器遇到第一条即退出，用户无法一次知道还要修哪些行。 |
| 改动 | `PolishRegressionNearDuplicateWorkbookImporter.ReadDecisions` 仍先验证 decision/reviewer/UTC 等必要字段；再一次汇总全部 rationale 不合格行的 `pair_id` 并拒绝导入。它不改工作簿、不猜测标签、不填理由，输出仍是 create-only，只有整包有效时才创建。 |
| 验收 | 新测试先在旧实现上失败，因为错误只包含第一个 pair ID；实现后近重复 workbook 导入专项 **10/10 通过**，新断言要求所有 3 个夹具无效 pair 均报告且不创建输出。真实副本再次通过正式导入器时一次报告全部 **19 个 pair_id**，`out/test-artifacts/w2-neardup-human-decisions-import-20261006.jsonl` 不存在。 |
| 结论/边界 | 用户副本 SHA-256 `7473ed7aa2fe7ff820a77f4498d1f0a8261488a88888762b30522bce0cfec08`；19 行 decision/reviewer/UTC 均有值，全部 decision 为 `same_semantic_family`，但 rationale 经现行规则 **0/19** 通过。这里只减少反复运行与逐条发现的成本，不能推断这些语义决定正确。该文件是 W2 内部近重复审议，不是阶段 0 盲评样本；正式 `blind-eval.jsonl`、source register、evidence manifest 仍不存在，阶段 0 贡献为 0。 |
| 接续 | 人工一次性修订列出的 19 条理由，并依据左右证据复核决定；随后重跑导入与 near-duplicate validator。阶段 0 则仍需独立授权样本和评审包，不能由此替代。 |

## 2026-10-06：复核“副本标注完成”并厘清阶段 0 准入状态

| 子任务 | 结果 |
|---|---|
| 输入 | 只读检查 `outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review - 副本.xlsx`；SHA-256 `7473ed7aa2fe7ff820a77f4498d1f0a8261488a88888762b30522bce0cfec08`。这是 W2 内部近重复裁定副本，不是 `datasets/ai-evaluation/blind-eval.jsonl`。 |
| 当前内容 | workbook 有 **19** 条候选行；19/19 有 decision、reviewer_id、UTC 时间和非空 rationale，decision 全为 `same_semantic_family`。但复制方给出的 19 条理由经导入器同一条具体性规则检查后为 **0/19 substantive**，属于“字段填了、理由仍是占位语”，不能称为可导入的完成人工裁定。 |
| 验收结果 | 使用现有 `polish-regression-neardup-import-workbook` 对原件只读导入到新的隔离测试输出。初次运行在首条占位理由拒绝；随后改进的错误汇总一次列出全部 19 个需修订 pair_id，仍未创建输出文件。没有改原工作簿、补写理由或改变任何标签。阶段 0 所需 `blind-eval.jsonl`、`source-register.json`、`evidence-manifest.json` 在当前仓库均不存在。 |
| 修正结论/接续 | 先前状态“19 条决定已填写”仅表示表格字段非空，不能等价于裁定有效或 W2 导入完成；该副本也不贡献正式盲评。下一步需要人工逐条写出与左右样本事实/目的差异相对应的理由，并按既定语义族定义复核 `same_semantic_family` 决定；完成后重新运行现有导入器和 neardup validator。阶段 0 仍需独立的 ≥500 条授权盲评样本及受控双评证据。 |

## 2026-10-06：运行时下载的有界传输重试

| 子任务 | 结果 |
|---|---|
| 问题与依据 | CPU/Vulkan 公网安装都观察到过 TLS EOF；错误发生在收到 HTTP 响应前。重试 HTTP 403、用户取消、响应体错误或 SHA 不匹配会掩盖真实失败或重复大流量，因此只重试可识别的连接/DNS/提前断开，TLS 错误还必须含 `IOException` 内因。 |
| 改动 | `LocalRuntimePackageService` 新增发送级 GET 辅助逻辑：每个 URL 最多额外发送 **1 次**，间隔 **250 ms**；每次重建 GET 请求并保留同一 Range 偏移。仅处理 `ConnectionError`、`NameResolutionError`、`ResponseEnded`，以及带 IO 内因的 `SecureConnectionError`。不重试收到的 HTTP 状态码、OperationCanceled、响应体读取、哈希校验或安装探测错误。没有新增用户配置和遥测。 |
| 验收 | 3 项测试先在旧实现上失败：瞬态安全连接异常未恢复、失败请求没有第二次尝试、403 状态处理断言重试次数为 1。实现后安装服务 + 健康检查组合 **25/25 通过**（安装服务 12 项，健康检查 13 项）。修复后的正式服务分别完成 CPU/Vulkan 公网取消续传：CPU 保留 **2,111,732 bytes**、续传进度 **2,128,116 bytes**；Vulkan 保留 **2,113,502 bytes**、续传进度 **2,129,886 bytes**。两者的续传进度均大于保留偏移 **16,384 bytes**，固定 SHA、真实 `--version`、正式卸载均通过。全流程分别耗时 **8.69 s / 11.13 s**，仅为各自单次下载—安装—卸载观察值，不作性能对比或吞吐结论。烟测没有记录自动重试分支被触发，因此该分支结论仅来自专用单测。记录：[CPU](../out/test-artifacts/runtime-public-download-smoke/runtime-public-download-cancel-resume-after-retry-20261006.json) · [Vulkan](../out/test-artifacts/runtime-public-download-smoke/runtime-public-download-vulkan-cancel-resume-after-retry-20261006.json)。 |
| 收益和边界 | 预期把一次可恢复的响应前传输错误从“立即报错、用户重新触发”改为后台最多多等待 **250 ms** 并自动再试一次；无响应体时不重复归档字节，Range 分片继续保留。测试证明一次错误后恢复，连续错误被限制在 **2 次总请求**，403 只发 **1 次**。持续网络故障仍会原样失败；响应体中断仍需用户再次触发续传。该改动不影响模型质量、推理参数或 API 用量。 |
| 下一步 | 发送级恢复和下载完整性已覆盖。继续做干净 Windows 的运行库缺失与 Vulkan 驱动行为验证；阶段 0 外部授权盲评仍未通过，阶段 5 未启动。 |

## 2026-10-06：Vulkan 制品公网下载、取消恢复复核

| 子任务 | 结果 |
|---|---|
| 输入与问题 | 固定 b11424 Vulkan 制品（33,332,009 bytes，SHA-256 `97de9a…8f146e9`）；确认产品安装服务从 GitHub HTTPS 经过取消后仍可完成安装、运行时探测与卸载。 |
| 验收 | 正式服务在约 2 MiB 取消，保留 **2,113,502 bytes** 分片；第一次后续请求遇 TLS EOF，HEAD 随后为 HTTP 200/1 次重定向；再从隔离分片继续安装。`IsInstalled` 对应的版本目录 manifest 命中固定 Vulkan SHA，真实 `llama-server --version` 探测通过，正式卸载后 exe 不存在。无用户配置/云请求。机器记录：[Vulkan 公网恢复记录](../out/test-artifacts/runtime-public-download-smoke/runtime-public-download-vulkan-cancel-resume-20261006.json)。 |
| 自审与边界 | 烟测 harness 起初用 CPU flavor 查找 Vulkan exe，曾误报“未激活”；修正 flavor 查找后，确认产品 Vulkan 包已真实安装并可启动，再完成卸载。此 harness 错误未触及应用代码。和 CPU 一样遇到瞬时 TLS EOF，Vulkan 期间累计两次；最终成功。harness 错误阻止保存恢复后的首个进度值，因此本条不声称 Vulkan 收到并接受了正确的 206/Range 响应；CPU 公网烟测已记录续传首进度高于保留偏移。运行时 SHA/探测证明包完整，但不替代 Vulkan 干净系统/驱动测试。 |
| 下一步 | CPU 已实测 Range 续传偏移；Vulkan 已实测公网下载中断后重试并成功安装/探测/卸载。两档下载都观察到 TLS EOF 瞬态错误后可通过再次请求恢复；接下来先评估仅针对“HTTP 响应前网络连接异常”的有界自动重试，避免重试 HTTP 4xx、取消、SHA 或签名错误，再转向干净 Windows 依赖与显卡驱动矩阵。盲评门槛仍未通过，LoRA/SFT 未启动。 |

## 2026-10-06：公网 HTTPS 下载、用户取消与续传实测

| 子任务 | 结果 |
|---|---|
| 输入与问题 | 使用安装器固定目录中的 b11424 CPU 制品，验证生产 `HttpClient` 下载是否能处理 GitHub HTTPS 重定向、用户取消后保留分片、再次安装时 Range 续传，并最终保持 ZIP SHA 校验、真实可执行文件启动探测和卸载闭环。安装到隔离目录，不读取用户设置、不调用云 Provider。 |
| 执行 | 从固定 GitHub Release URL 通过正式 `LocalRuntimePackageService` 下载；进度达到约 2 MiB 时由 `CancellationTokenSource` 主动取消。检查 `.downloads` 中保留分片，再用同一个服务和正式目录再次安装；后续进度从旧偏移继续，最终执行清单校验、启动 `llama-server.exe --version`、注册并卸载。 |
| 验收 | 实际分片 **2,128,116 bytes**；续传后第一个进度为 **2,144,500 bytes**，高于旧分片，证明响应追加在既有偏移而不是从头覆盖。固定归档 **19,394,018 bytes / SHA-256 `d613ef…efacb5c7`**；安装 manifest 校验、真实版本探测与卸载均通过。整个下载—安装—探测—卸载流程 **7.43 秒**，此值不是独立网络速度基准。机器记录：[公网取消续传烟测](../out/test-artifacts/runtime-public-download-smoke/runtime-public-download-cancel-resume-20261006.json)。 |
| 自审与边界 | 首次正式下载尝试在 HTTP 响应前报 TLS unexpected EOF；未误报成产品 403。无代理环境变量；随后同 URL 的轻量 HEAD 返回 HTTP 200、1 次重定向，正式下载/续传第二次成功。记录这次瞬态失败，不把单次 7.43 秒外推到网络性能。本轮只覆盖官方 CPU 制品与当前网络，Vulkan 公网包及干净 Windows 依赖矩阵仍待执行。无用户内容、API 请求或模型质量变化。 |
| 下一步 | 公网 CPU 包的取消/续传链路现有实证，且验证了前一子任务修复的 Range 元数据约束。继续验证 Vulkan 包公网安装；之后在干净 Windows 环境核验运行库缺失诊断和驱动不可用行为。阶段 0 外部授权盲评和阶段 5 仍未通过/启动。 |

## 2026-10-06：运行时下载续传协议恢复与真实归档复核

| 子任务 | 结果 |
|---|---|
| 输入与问题 | 已固定 SHA 的官方 CPU ZIP（19,394,018 bytes）及安装服务。原下载器收到任意 `206 Partial Content` 就追加，没有核对 `Content-Range` 起点/总长；部分服务器对旧分片返回 `416` 时直接失败，用户无法靠再次安装恢复。 |
| 改动 | `LocalRuntimePackageService` 现在只接受起点等于本地分片长度、总长等于固定清单大小的续传响应；不匹配时删除分片并拒绝安装。遇到非空分片的 `416` 时删除分片，从零重下一次；第二次仍失败则按 HTTP 错误退出，不循环重试。合法范围续传的测试夹具同步提供完整范围元数据。 |
| 验收 | 两个新回归先在旧代码上按预期失败：错误 `Content-Range` 曾成功安装；`416` 曾抛出 HTTP 错误。实现后本地安装服务专项 **9/9 通过**。再以同一份真实 CPU ZIP 运行生产 `LocalRuntimePackageService`：首个请求传至 6,464,672 bytes 后中断，续传请求获 416，随后从头拉取完整归档；最终 SHA/解压/依赖许可复制/真实 `--version` 检查/清单注册/正式卸载均成功。全程 3 次 HTTP handler 请求（2 次完整资源请求，其中首个被截断；1 次 Range 请求），卸载后可执行文件不存在。机器可读记录：[下载恢复烟测](../out/test-artifacts/runtime-download-regression/runtime-download-416-recovery-smoke-20261006.json)。 |
| 收益与边界 | 错误范围不能再被当成合法续传响应；旧分片遇 416 后无需用户手动删缓存即可恢复，最多多发起一次完整下载。该故障场景下会重下 19.39 MB，包含已传的约 6.46 MB，因此不节省流量；价值是避免确定性失败并继续维持清单 SHA 校验。使用本地 HTTP handler 注入协议响应，不等于公网 GitHub 下载实测；正常公网下载、用户取消/恢复和干净 Windows 仍待独立验收。没有调用模型或云 API，对模型质量无量化影响。 |
| 下一步 | 当前运行时下载安装/恢复逻辑已比此前多覆盖一个真实归档故障路径。下一项继续检查运行时在无依赖和 Vulkan loader 存在但驱动不可用时的真实 Windows 行为；干净镜像暂不可用时则先完成剩余静态分发边界核验。阶段 0 外部授权盲评仍未通过，阶段 5 未启动。 | 

## 2026-10-06：本地运行时缺失依赖诊断

| 子任务 | 结果 |
|---|---|
| 输入与问题 | 固定的 b11424 Windows x64 CPU/Vulkan 包及其真实 PE 导入表；现有 `--version` 健康探测在 DLL 缺失时只能呈现进程错误，用户不知道应补装什么。 |
| 改动 | `HealthCheckService` 在启动探测前实际尝试加载三个被运行时导入的 VC++ x64 DLL；Vulkan 候选额外检查 `vulkan-1.dll`。VC++ 缺失时给出官方 Redistributable 地址；仅 Vulkan loader 缺失时记录原因、跳过 Vulkan 并沿用 CPU 回退。检查不自动安装、不发网络请求、不加载模型。 |
| 验收 | 新增两条健康检查回归：Vulkan loader 缺失但 CPU 可用时只探测 CPU 并标记回退原因；VC++ 缺失时阻止 runtime 探测并显示安装地址。修改前因诊断注入接口不存在而失败，修改后健康检查专项 **13/13 通过**。 |
| 边界与接续 | 默认 Windows 检查使用 `NativeLibrary.TryLoad`，但新用例注入缺失依赖场景，尚未在干净 Windows 镜像验证默认 DLL 检测。Loader 存在不证明 Vulkan 驱动 ICD 可用；实际 GPU 驱动、许可清单和干净 Windows 启动仍待验。来源与依赖证据见[运行时制品记录](../local-runtime-package-inventory-2026-10-05.md)。阶段 0 盲评基线和阶段 5 LoRA/SFT 状态不变。 |

## 2026-10-06：运行时附加许可证文本与全包许可盘点

| 子任务 | 结果 |
|---|---|
| 输入与判断 | 固定 compiler source commit 的 Windows CPU/Vulkan build workflow、实际 ZIP PE imports、LLVM OpenMP notice 和 Vulkan SDK `1.4.357.0` 编译依赖。动态依赖扫描只能覆盖 DLL imports，另核了构建期 OpenMP/Vulkan/SPIRV header 组件。 |
| 改动 | 新增随应用复制到输出目录的 Vulkan-Headers `LICENSE.md`、Apache-2.0 全文和 SPIRV-Headers `LICENSE`；运行时安装服务将它们复制到 runtime 版本目录。CPU/Vulkan 运行时 ZIP 校验、SHA 和执行探测保持不变。 |
| 验收 | 安装器附加许可证专项回归通过；关联健康检查/本地运行时/包交付筛选 **37/37 通过**。主项目 build **0 warning / 0 error**；Release `dotnet publish` 输出含四个许可资源文件。精确上游 tag 文件已按 SHA-256 核验。所有 package 内非系统 DLL imports 已分类为 Windows API/UCRT、VC++ CRT、Vulkan loader；OpenMP notice SHA 与上游 CMake 固定值一致。 |
| 发现与下一步 | 发布 server DLL 还包含独立构建的 Web UI；其 `llama-ui.zip` 没有独立许可文件，锁文件中有构建期/运行期混合的 1,207 个许可条目及 6 个缺许可字段条目。已明确记录此许可审查未完成，不能称完整第三方清单已关闭。下一步按 UI bundle 实际导入依赖生成 notice/SBOM，再在干净 Windows 镜像验证运行时缺失依赖提示和 Vulkan→CPU 路径。详见[本地运行时制品核验记录](../local-runtime-package-inventory-2026-10-05.md)。阶段 0 与阶段 5 均未改变。 |

---

## 2026-10-05：继续推进兼容降级与 WPF 测试隔离

| 子任务 | 结果 |
|---|---|
| 结构化输出降级 | Provider 以 HTTP 400 或 422、且错误类别明确为 `RequestRejected` 拒绝 JSON Schema 时，工作流改走一次普通文本生成，再用同一份本地 Schema 校验；鉴权、限流、超时和服务端故障不会触发这条降级。新增 422 用例在旧实现下复现失败，修复后 400/422 **2/2 通过**。 |
| WPF 视图回归 | 原 WPF smoke 直接读写 LocalAppData SQLite，当前测试运行身份对该用户目录无写权限。给 `SettingsView` 增加 ViewModel 注入构造路径，冒烟测试改用项目输出下的独立 SQLite 根目录，并让窗口配置统一使用隔离数据根。设置页和主窗口 WPF smoke **23/23 通过**。 |
| 测试目录 | DatasetBuilder 的 4 个遗留临时目录夹具同样落在只读系统 Temp，改到 `AppContext.BaseDirectory/test-data`，仍逐测试清理。DatasetBuilder、AIService、本地运行时、润色工作流和 WPF smoke 联合筛选 **155/155 通过**。 |
| 质量边界 | 未调用真实 Provider，没有改变 prompt、路由策略或模型权重；这轮验证的是结构拒绝时的兼容恢复与界面构造，不是模型质量收益。阶段 0 授权盲评样本状态没有变化。 |

---

## 2026-10-05 阶段 3 续：任务级风格可见并纳入偏好导出

| 子任务 | 结果 |
|---|---|
| 结果可见性与历史 | 主窗口新增“最近成稿风格”状态，润色仅在返回最终成稿后更新，提示词优化在通过质量检查并形成成稿后更新；显示值来自本次解析的风格。提示词优化归档的 `Style` 也写入本次实际风格；深度原本已独立保存在 `ContextJson.Depth`，因此不会丢失。clarification、阻断或失败不会伪报为已产生成稿。 |
| 偏好导出 | 导出 Schema 升至 v2，增加 `outputStyles.global` 与经过白名单规范化的 `outputStyles.overrides`；继续只导出用户偏好和无正文交互计数，不复制 AppSettings、密钥或历史。全局风格也被包含，使旧设置与新增任务覆盖都可携带。 |
| 验收 | 导出服务字段边界与设置页实际调用、润色/提示词优化最终风格状态、提示词优化归档风格/深度保留，共 **5 项专项回归通过**；配置/设置/提示/主工作流和导出组合筛选 **161 通过、11 跳过、0 失败**。主窗口布局及停止操作 WPF 冒烟 **2/2 通过**。 |
| 限制 | 扩大筛选中的 `WpfViewSmokeTests` 设置页视图有 12 项在 `SettingsView` 构造期间的 `ArchiveService.Search` 报 SQLite “unable to open database file”；异常发生在风格控件绑定前，因此不能视为设置页视觉验证通过。本轮没有真实 Provider 请求或质量盲评。 |
| 收益 | 用户可在结果状态看到本次应用的准确风格；导出按最多 12 个有限范围保存风格配置，复杂度随已配置范围线性增长。没有增加生成请求、调用次数、采样参数或提示词中的风格指令长度。 |

---

## 2026-10-05 阶段 3 续：输出风格按任务与场景显式配置

| 子任务 | 结果 |
|---|---|
| 问题与约束 | 学习反馈已按任务/场景归因，但用户实际选择的 `OutputStyle` 仍为一个全局值，无法让润色职场沟通与编程提示词各用不同风格。优先采用用户显式设置，避免从稀疏反馈自行推断；保留全局风格作兼容默认值。 |
| 配置与解析 | 新增可选 `outputStyleOverrides`，键为 `Polish|场景` 或 `PromptOptimize|类别`，值限于现有六种风格。解析顺序为精确场景、任务默认、旧全局值；未知任务/场景/风格在 Normalize 时删除。未提升 `configVersion`；旧配置缺字段时沿用原全局值。当前界面可配置 12 个范围（两任务各自默认、4 个润色场景、6 个提示词类别），每范围可选 6 种风格或继承。 |
| 交互与请求 | 设置页复用现有任务/场景选择器编辑风格草稿，保存后才写配置；切换范围保留各自草稿。“跟随任务/全局”清除该范围覆盖。润色按解析后的场景取值，提示词优化按所选类别取值；实际值进入现有提示词参数。没有新增 Provider 请求、API 调用或遥测字段。 |
| 验收 | 新解析器用例、旧配置默认/Clone 用例、设置编辑/切换/保存/磁盘重载，以及两条 MainViewModel fake-provider 工作流共 **7/7 通过**。设置、配置、提示构造、主工作流与 WPF 页面扩展筛选 **181 通过、11 跳过、0 失败**；11 项为既有历史配置用例。新增集成测试先复现提示词优化忽略场景覆盖的失败，再通过修复后验证。 |
| 收益与限制 | 能力从一个共享风格扩展为最多 12 个显式任务/场景范围，六种风格可独立选择；每次生成仍只注入一个风格参数，没有增加一次模型调用。尚无盲评证明成稿质量或用户可用率提升。用户风格现在直接作为任务设置进入 prompt，与旧全局风格行为相同；云端偏好共享开关仍专门控制已确认的长期结构化偏好。 |

### 本轮并行评测资产复核

- 只读检查 W2.3 人工副本：19/19 决定字段已填，但理由均为占位语“无非空理由”；决定均为 `same_semantic_family`，与当前已确认的“事实骨架 × purpose”定义不一致。未导入、未修改原表或冻结数据；副本 SHA-256 为 `7473ed7aa2fea7ff820a77f4498d1f0a8261488a88888762b30522bce0cfec08`。
- 对补样 v1 实跑来源审计与人工审阅校验：20/20 条逐样本生成血缘缺失；人工评分为 0/20。已生成规格包的 30 行决定目前均为空；W4 草稿仍只是内部未审材料。历史模型/seed/逐条生成时间不可事后猜补。
- W2 决定/理由和补样审阅不是风格覆盖功能的依赖，故本轮继续实现用户显式设置；这些评测材料仍不构成模型质量证据，阶段 0 贡献维持 0。

---

## 2026-10-05 阶段 3 续：输出风格进入提示词优化与反馈归因

| 子任务 | 结果 |
|---|---|
| 缺口 | 全局 `OutputStyle` 原先只进入润色请求；提示词优化链路忽略了这个用户设置。候选规则也只关联篇幅编辑结果，没有固定该结果实际生成时使用的风格。 |
| 参数与实现 | 提示词优化请求现在携带生成时捕获的 `OutputStyle`。风格经六项白名单（自然、克制、亲切、专业、正式、简洁）规范化，未知值回退为“自然”，并作为低优先级表达约束加入 prompt context；它只调整措辞，不覆盖本轮指令和事实保真。旧配置无需迁移，未增加模型请求或调用次数。 |
| 反馈与候选 | 结果会话在内存中捕获本次实际风格；明确接受/拒绝结果按任务、场景和风格累计非内容计数。用户手动切换风格时另按风格累计选择次数。篇幅候选要求接受输出风格唯一且无拒绝样本；风格混杂、出现拒绝或缺少风格归因时抑制候选，避免把不同生成条件混为一谈。 |
| 验收 | 风格目录、提示构造、反馈会话、候选抑制、设置和 fake-provider 集成均有回归；聚焦回归 33 项通过。扩展设置、配置、路由、生成工作流和 WPF 筛选 **242 项：231 通过、11 跳过、0 失败**。跳过项是既有配置历史测试；真实 Provider 请求 0。 |
| 限制与下一步 | 已证明参数沿调用链生效及反馈按生成风格归因，尚未证明成稿质量提升。当前候选规则仍是保守启发式，篇幅方向沿用相对长度 ≤80%/≥125% 的既有阈值。下一步继续推进授权样本与独立盲评，并对有/无个性化提示做锁定条件的成对评估；质量收益报告前不把交互计数当作质量指标。 |

---

## 2026-10-05 阶段 3 续：可解释的篇幅偏好候选

| 子任务 | 结果 |
|---|---|
| 输入与决策 | 复核后弃用了仅用 `shorteningEdits`/`expansionEdits` 多数方向的初版规则，因为它违背计划中“组合交互信号”的要求。现在 MainViewModel 在单次生成结果的内存会话内关联编辑方向与明确复制接受、重试或真正撤销成稿的动作；用户离开/替换结果且未作出结果动作时不计入候选证据。候选只在当前范围出现唯一的“编辑后接受”方向且没有相反的编辑后接受/重试/撤销样本时显示；混合或只有未关联统计时不推断。 |
| 产出 | 设置页展示任务/场景候选及按成稿计的关联支持/反向次数，标明“未校准”，不输出概率或质量结论。用户可载入后修改，须保存才成为 `user-confirmed` 偏好；也可持久化忽略当前候选。已有确认偏好时不会覆盖。 |
| 兼容与隐私 | 新增 `acceptedShortenedOutputs`、`acceptedExpandedOutputs`、`rejectedShortenedOutputs`、`rejectedExpandedOutputs` 和 `ignoredSuggestionKeys` 配置字段；旧配置缺失时默认为空，未改 configVersion。活动会话只在内存保存布尔方向，不保存生成/编辑文本；聚合计数留在本地，候选不进入模型请求，直到用户载入并保存。 |
| 验收 | 关联结果记录服务、方向候选、设置草稿/确认、忽略后重载、设置页契约和 fake-provider 真实工作流 **17 项通过、0 失败**；设置/配置/路由与生成/WPF 页面筛选 **188 通过、11 跳过**。11 项是既有配置历史测试标记为跳过。未调用真实 Provider。 |
| 限制 | 改短/改长继续沿用现有编辑采集规则（相对长度 ≤80% 或 ≥125%），这不是经盲评校准的阈值；候选仅为人工审阅线索。风格结果归因已在后续工作中补齐，但没有盲评证明候选或风格指令带来质量提升，不能据此宣称个性化收益。 |

---

## 2026-10-05 阶段 3 续：偏好导出与可靠清除

| 子任务 | 结果 |
|---|---|
| 缺口 | 设置页原有“清除全部偏好”在配置保存失败时会吞掉异常并报告成功；尚无偏好单独导出入口。 |
| 产出 | 新增 JSON 偏好导出，包含已保存的任务/场景偏好、确认来源/时间、不含正文的交互统计和已忽略候选标识，不包含整个 AppSettings、API Key 或历史；使用临时文件写入后替换目标，避免中断时留下半份导出。清除操作先持久化空 profile，成功后才清理界面草稿并报告完成；写盘失败时恢复原 profile 并说明本地数据保留。 |
| 兼容与隐私 | 不改配置 schema/version，不新增联网请求，不上传偏好；导出由用户选定本地路径。已保存偏好才会导出，尚未保存的编辑草稿不会被意外带出。 |
| 验收 | 聚焦回归 **4 通过、0 失败**：导出字段边界、清除后重新加载为空、配置写入失败时内存恢复、已有偏好确认保存。构建成功；未调用真实 Provider。 |
| 下一步 | 可靠导出/清除、用户可控候选和生成风格归因均已完成。下一步对关联候选和无候选基线做任务/场景切片盲评；不以候选条数或交互次数代替质量收益。 |

---

## 2026-10-05 阶段 3 续：确认偏好的云端披露开关

| 子任务 | 结果 |
|---|---|
| 问题与约束 | 已确认的本地偏好参与云端提示，但此前没有独立的云端披露选择。约束是本机模型继续可用偏好、云端默认不收到偏好、交互统计和正文永不随本功能发送，并覆盖显式云端 fallback。 |
| 产出 | `shareConfirmedPreferencesWithCloud` 配置缺省 `false`；设置页解释共享字段范围。仅当所有可能目标均由受管本地运行时或 loopback endpoint 组成时，无需云端同意即可包含确认偏好；只要主模型或配置备用可能是云端，就默认移除长期偏好指令。用户开启共享后，润色/提示词优化都可发送当前任务/场景已确认的篇幅、语气、原措辞和禁用表达。无确认来源的旧版全局禁用表达仅保留本机路径兼容，不随云端同意一并发送。 |
| 兼容与成本 | 旧配置因布尔默认值继续关闭共享；新增判断为每请求常数次 profile/URI 检查，没有增加网络请求或模型调用。配置由现有序列化/Clone 路径保存，不升级 configVersion。 |
| 验收 | 新增真实 MainViewModel 路径 fake-client 回归：两项任务在云端默认关闭时偏好指令出现数 **0**，显式开启时均出现；loopback 本地默认使用偏好；“优先本地+云端 fallback”在真实本地失败并切换时也未泄漏偏好；非 loopback 局域网 endpoint 未同意时按未验证远端处理。路由、配置、设置、Provider 和工作流筛选 **272 通过、11 跳过**，真实 Provider 请求 **0**。 |
| 限制与下一步 | 这验证了请求组装边界，不代表质量收益或真实服务商流量观测。偏好导出与可靠清除已补齐；下一步推进候选建议依据和用户确认操作。 |

---

## 2026-10-05 阶段 3 续：个性化信号按任务与场景归因

| 子任务 | 结果 |
|---|---|
| 当前节点 | 阶段 3：补齐学习信号的任务/场景归因，为后续可解释候选建议准备非内容统计输入。 |
| 输入与约束 | 现有接受、编辑、重试、撤销事件和当前生成任务/场景；保留旧全局计数兼容。只存计数及最近更新时间，不存原文、成稿或编辑片段；非标准场景归并到任务通用范围，防止模型自由文本创建统计键。 |
| 产出 | 新增 `interactionSignals`，按 `polish|职场沟通`、`prompt-optimize|编程开发` 汇总接受、编辑、改短/改长、重试和撤销。设置页显示当前任务/场景统计；当时 OutputStyle 仍只有全局值，后续已增加任务/场景显式覆盖。每轮明确要求仍高于已确认偏好。 |
| 验收 | 红测确认原实现缺少 scoped 记录入口；新增回归验证任务/场景不串、非标准场景不入键、配置 Clone 往返、旧配置缺字段可读、序列化不含测试原文/成稿；MainViewModel 实际润色后编辑的统计也进入生成时场景桶。服务、设置、配置、Provider、路由和生成集成筛选 **265 通过、11 跳过**。 |
| 影响与限制 | 统计的上下文归属从全局变为任务/场景，可用于后续分析；它**不代表成稿质量提升**，本地盲评仍未进行。计划未给出候选多信号权重、最小样本数或置信规则，因此本步不生成或自动晋级候选。 |
| 下一步 | 先以开发样本和用户可解释的偏好目标定义候选规则及最低证据量，再实现“查看依据—编辑—逐项确认/忽略”；候选只在确认后进入提示。阶段 0 盲评通过后再估算偏好遵循率及成稿可用性的实际增益。 |

---

## 2026-10-05 阶段 2 路由策略与 LocalOnly 边界首轮落地

| 子任务 | 结果 |
|---|---|
| 当前节点 | 阶段 2：让模型选择策略可执行、可由用户控制；本轮完成主窗口生成入口与设置页接线。 |
| 配置 | `providerRoutingMode` 默认 `Manual`；新增 `polishProviderProfileId`、`promptOptimizeProviderProfileId`、`fallbackProviderProfileId`。旧配置缺少这些字段时继续使用原活动 profile。设置页提供手动、仅本地、优先本地、优先云端四档，以及两个任务 profile 绑定和显式备用 profile。 |
| 行为 | 主生成入口按任务解析 profile；仅本地只接受受管本地运行时或 `localhost`/loopback endpoint，即使用户把远端服务标记为 Local 也拒绝，不实例化生成客户端。PreferLocal/PreferCloud 只在显式备用模型配置后包装生成客户端；主模型产生规范化 `GenerationFailureException` 后最多切换一次，后续结构修复继续固定使用备用客户端。手动模式和 LocalOnly 不启用 fallback。 |
| 诊断 | 结果区显示本次实际 profile/model 和选择原因；若切到备用模型则显示切换原因。润色请求、归档版本和手动保存使用实际路由模型元数据。数据请求正文不进入路由状态或遥测。 |
| 测试 | 路由策略/兼容配置/设置页/本地运行时共 **21/21**，LocalOnly 主流程“无本地 profile 时 generation client factory 调用数 = 0”通过，PreferLocal 在显式备用配置时由测试客户端验证实际切换。扩展的 Provider、工作流、设置和 UI 回归 **247/247 通过**。真实云端调用数为 0。 |
| 限制 | fallback 只处理 `GenerationFailureException`；通用编程错误和取消不会触发切换。仅本地 endpoint 通过 URI 和 profile 类型判定，实际本地运行时进程隔离仍需 Windows 端到端验收。自动路由复杂度/质量排序尚未实现；当前偏好路由按用户策略和配置列表顺序选模型。 |
| 影响/下一步 | 用户可在不改旧配置的前提下手动固定任务模型、强制本地或显式允许一次备用切换；这减少误发云端的配置风险，不代表质量、延迟或费用改善。下一步补齐结果状态和失败分类边缘回归后进入阶段 3 的可编辑偏好确认闭环；阶段 0 授权盲评证据并行推进。 |

---

## 2026-10-05 Provider 结构化载荷回归续验

| 子任务 | 结果 |
|---|---|
| 当前节点 | 阶段 1 结构化生成主链路验收；按“继续推进”指示，不以阶段 0 的数据工作冻结工程实现。 |
| 输入与改动 | 对 OpenAI-compatible、Anthropic Messages、Gemini GenerateContent 的捕获 HTTP 请求，加入 EmotionAssistant 风格的 nullable emotion enum 与 nullable numeric intensity Schema 回归。校验 Schema 被放进正确 Provider envelope、字段/枚举完整透传、没有把 0–1 numeric range 送到当前共享 wire schema。 |
| 验收 | 新增的 3 项 Provider 载荷测试 **3/3 通过**；AIService、telemetry、Provider preset、structured workflow/validator、local runtime、prompt builder 相关独立筛选 **101/101 通过**。真实 API 请求与用户内容发送均为 **0**。 |
| 环境异常及复核 | 首次未重定向临时目录时，232 项中 171 项通过、61 项因 `%TEMP%`/AppData 目录不可写而失败（SQLite `unable to open database file`、`UnauthorizedAccessException`）；将 `TEMP`、`TMP`、`LOCALAPPDATA` 指向工作区可写的 `out/test-*` 目录后，同一筛选 **232/232 通过**。这是测试宿主目录配置问题，不是产品实现修改。 |
| 量化判断 | 三个云协议载荷位置均由 fake HTTP handler 实测，新增 3 个防回归断言；它验证接口序列化，不代表 Provider 已线上接受所有模型/Schema，也不能推出成稿质量、延迟或成本改善。 |
| 下一步 | 进入阶段 2：任务级 profile 绑定和用户路由策略；默认手动，先补 LocalOnly 无云请求边界，再实现可配置 fallback 与实际模型回显。阶段 0 授权盲评证据并行补齐。 |

依据：OpenAI [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)、Anthropic [Structured Outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)、Gemini [GenerateContent API](https://ai.google.dev/api/generate-content) 与 [Structured Output](https://ai.google.dev/gemini-api/docs/generate-content/structured-output)。

## 2026-10-05 W4 AI 合成补样草稿落盘

| 项目 | 结果 |
|---|---|
| 当前计划节点 | 阶段 0 内部合成回归集：覆盖缺口后的独立 AI 补样草稿。 |
| 产出 | `datasets/polish-regression-ai-supplement-draft-v1/`，含 `cases.jsonl`、生成规范、README 与哈希 manifest；审阅包为 `outputs/polish-regression-ai-supplement-review-2026-10-05/`。 |
| 范围 | 20 条 / 20 个唯一族 / 10 类行为；development 与 regression 各 10 条，clarify 2 条、polish 18 条。覆盖澄清、输出格式、高风险事实保真、多轮修改、否定边界、注入文本、非职场语境及中英混写；不适用的 tool failure 未伪造。 |
| 校验 | 必填字段与枚举、ID/族唯一、split、manifest 内容哈希、与 v1 ID 不冲突均通过；输入/参考输出 PII 启发式命中 0。 |
| 边界 | 所有参考输出仍是未经人工确认的 AI 草稿；不计入阶段 0，不用于 blind eval、微调或模型晋级。冻结 v1、用户工作簿和原候选包保持不变。 |
| 下一步 | 审阅 CSV 已可直接逐条接受、编辑或拒绝；决定栏 20/20 留空。审阅后的条目需写入新版本并重跑族级去重与覆盖报告。 |

该结果是内部回归建设的实际增量，不代表阶段 0 通过，也不更改云端/本地生成行为。

## 2026-10-05 W4 人工审阅 CSV 校验链路

| 子任务 | 输入、产出与验收 |
|---|---|
| 当前节点 | 阶段 0 内部回归补样 W4.6 人工审阅回收准备。AI 草稿 20 条已生成，人工决定尚未填写。 |
| 输入 | `datasets/polish-regression-ai-supplement-draft-v1/cases.jsonl`、`outputs/polish-regression-ai-supplement-review-2026-10-05/human-review-template.csv` 与同目录 manifest。 |
| 产出 | `DatasetBuilder/PolishRegressionSupplementReviewValidator.cs` 和 `polish-regression-supplement-review-validate` CLI，只读校验，不改写案例。 |
| 校验内容 | 源案例 SHA-256、审阅包来源/阶段 0 边界、README 哈希、ID/族格式与唯一性、CSV 覆盖及原始证据一致性、accept/edit/reject 字段一致性、fail 与 accept 冲突、理由和严格 UTC 时间、PII/密钥启发式扫描。报告明确 `reviewer_identity_verified=false`。 |
| 测试 | TDD 针对测试 9/9 通过；与 near-duplicate 导入/审计和 blind 边界回归合计 39/39 通过。 |
| 实测 | 当前 CSV 返回 `valid=false`、`reviewed_count=0/20`；决定、评分、审阅者、UTC 时间、理由分别缺失 20 条。未发现案例错配或源哈希错误。 |
| 影响 | 该链路可拦截错行、漏标和无效审阅记录；不提升模型输出质量本身，也不改推理参数、提示或权重，不计入阶段 0。 |

复现命令见 [DatasetBuilder README](../DatasetBuilder/README.md) 的 W4 审阅说明。下一步：回收已填写 CSV 后运行该命令；验证后的逐条决定进入有版本号的处置记录，再将接受或修改样本纳入新内部回归版本。

## 2026-10-05 W4.6 人工处置记录导入

| 子任务 | 输入、产出与验收 |
|---|---|
| 问题 | 校验通过后仍需人工复制决定，容易漏掉拒绝项或把修改稿覆盖到错误案例。 |
| 输入 | AI 补样 `cases.jsonl`、已填写的审阅 CSV、审阅包 manifest。导入器先调用上一步完整校验。 |
| 产出 | `DatasetBuilder/PolishRegressionSupplementReviewImporter.cs` 与 `polish-regression-supplement-review-import` CLI。创建源数据集和审阅包目录之外的新 JSONL；每行绑定 case/family、源 cases 哈希、最终决定/成稿、质量项、理由、审阅者引用和 UTC 时间。 |
| 语义 | accept 采用原标签和成稿；edit 采用人工终态标签和成稿；reject 保留理由但最终标签/成稿置空。所有三种结果均保留，导入不自动合并进回归集。输出 create-only，拒绝覆盖。 |
| 验证 | 补样校验/导入及 CLI 与 near-duplicate、blind 边界回归合计 45/45 通过；测试覆盖 accept、edit、reject、无效审阅不落盘和输出目录隔离。当前空白 CSV 的真实导入 CLI 返回 exit 2，且目标文件和目录均未创建。实际用户 CSV 仍未填写，所以尚未产生真实人工决定文件。 |
| 边界 | 记录的 reviewer identity 仍标为未验证；阶段 0 贡献 0。 |

下一步触发：完成 20 条 CSV 后依次运行 validate 与 import；导入日志再作为新内部回归版本构建器的输入，冻结 v1 不回写。

> **当前状态（历史诊断章节不代表当前代码状态）：** 阶段 0/A0.1 仍未通过，授权冻结样本、独立评审证据及同条件产品基线仍缺。阶段 1 Provider 结构化载荷与共同工作流已有回归覆盖；阶段 2 用户配置路由和 LocalOnly 边界已接入；阶段 3 的用户确认偏好已接入任务及场景上下文。当前工程回归筛选 249 项通过；本结果不证明模型成稿质量收益。WPF 全量宿主与发布/安装验收按历史诊断记录仍需单独复核。

## 2026-10-03 最新验证补充

- 来源补充复核新增两个专项改写候选：ToxiRewriteCN 为 1,556 条中文去毒改写人工标注三元组，可单独研究为安全/情绪保真压力切片；MCTS 有 723 个多参考中文简化样本，可作简化场景专项对照。前者虽标 Apache-2.0，来源页面仍未证明逐项上游权利、本项目独立双评/裁定原始证据或冻结隔离；后者任务范围偏离产品目标且权利尚未逐项核验。两者均未下载/导入，不能凑成通用 500 条冻结集；阶段 0 仍优先等待自有/明确授权数据与双评受控证据。详情见[外部评测数据来源补充调查](../external-evaluation-data-research-2026-10-02.md#2026-10-03-来源补充安全改写专用候选)。
- 运行 M 在独立探针中稳定复现当前测试 helper 生命周期模式的相邻 FailFast：首个 STA 关闭窗口并 shutdown/反射清空 WPF Application 静态字段后，第二 STA 成功新建 Application，却在构造 CompanionFace XAML 时从 `Application.GetResourcePackage(Uri)` 直接 `Environment.FailFast`；固定双轮重复两次，heap dump 42,952,228 字节，托管栈可读。单 STA/同一 Application 实际控件 Run L 100/100 通过；不重建 Application 的双 STA 探针 2/2 通过，但未接触 Application 线程亲和 API；共享旧 Application 的另一 STA 控制得到 `InvalidOperationException`。源码未发现任何 CurrentCulture 写入。该栈不同于全量 `HwndSubclass` 栈，故只确认了 helper 模式存在独立危险，不证明全量根因。未改测试/产品代码；下一步需先设计不会杀死主 testhost 的子进程回归，再比较单 STA 代理与 WPF 测试独立进程的成本。详见测试基线分类报告运行 M。
- 最新来源复核仍未找到可满足阶段 0 条件的公开冻结集：RewritingBench 只有 129 条已公开 eval、逐行上游权利待核，且其自称关联的 EMNLP 2026 论文在会议 2026-10-24 开始前尚无官方 ACL/arXiv 记录；WritingBench 是开放式写作任务而非润色双评 gold。未导入新数据或改变阶段门槛；详见[来源调查 2026-10-03 补充](../external-evaluation-data-research-2026-10-02.md#2026-10-03-来源复核补充)。
- 当前测试清单为 1126 项。全量运行仍未完整结束：旧 Runtime 下运行到 859 项后崩溃；官方当前 8.0.31 Desktop Runtime 的 WPF/UI/Settings 过滤也在 164 项后复现 `HwndSubclass` 资源递归。
- 新生命周期用例证明测试 helper 会关闭其当前 Dispatcher（1/1）；AI 定制/盲评/候选执行/润色筛选连同该用例此前为 **150/150 通过**。WpfViewSmoke 的 21 项逐线程 shutdown 实验因组合运行再现崩溃而撤回。当前仍无证据证明全量宿主问题已解决，阶段 0 也未通过。
- 运行 H 将 WPF 宿主崩溃缩到测试顺序/状态交互：完整 Companion 类与 Settings 两条用例组合重复崩溃；排除已知映射失败用例后 37/37 通过。更小的“映射失败 + 折叠动画 + 两条 Settings 用例”首跑崩溃、紧接复跑通过，且两次实际顺序相反；因此它不是稳定根因复现。当前不改生产代码或测试清理，下一步控制/比较执行顺序或按进程隔离。详见测试基线分类报告运行 H。
- 运行 I 按 103 个测试类分别启动独立进程，覆盖全部 1126 项：1115 执行、1107 通过、8 失败、11 个 ConfigService 历史迁移用例跳过、0 宿主中止。8 个失败均为既有发布/安装、悬浮球尺寸、Curious 资源契约分歧；类隔离下没有 WPF 宿主崩溃。逐文件 TRX 汇总重建后的 `summary.tsv` 为标准制表符格式。此结果仅证明进程隔离可完成清单，不等同普通全量通过，也不定位崩溃根因。原始 TRX 位于 Git 忽略的 `out/test-artifacts/class-isolated-20261003`。
- 运行 J 读取两份既有 crash dump，确认 STA/WPF `HwndSubclass.SubclassWndProc` 中出现 `NullReferenceException` 资源本地化递归，最终 `ExecutionEngineException`/`Environment.FailFast`；Settings 测试工作线程当时阻塞于 `Thread.Join()`，不是已证明的起因。根因仍未定位，未改代码。分析器安装在忽略目录；TRX Skip 计数注意事项见测试基线报告运行 J。
- 运行 K 的隔离 WPF 探针在每种模式 100 轮均未崩溃：显式 shutdown 的 Dispatcher 100/100 正常开始/完成；只关窗口的 STA 线程 100/100 退出，但 Dispatcher shutdown 为 0/100。确认“不显式 teardown 会留下未标记关闭的 Dispatcher”是实测行为，但不足以解释 FailFast；产品/测试代码无改动。后续探针需加入真实 Companion/皮肤资源与延迟回调，详见测试基线报告运行 K。
- 运行 L 已将探针扩展到实际 `CompanionFace` 资源及点击/展开定时反馈，并在同一 Application/STA/Dispatcher 上重复窗口生命周期 100 轮。修正夹具为 `OnExplicitShutdown` 后，100/100 HWND 创建与关闭、100/100 点击回调、Dispatcher shutdown 正常开始/完成，0 异常；仍未复现 `HwndSubclass` FailFast。默认 `OnLastWindowClose` 的首版夹具在首窗关闭后让 App 退出，第二轮控件创建失败，属于探针配置问题。构建 0 错误、2 个 NuGet Audit 缓存权限警告；探针和 TSV 均在 Git 忽略 `out/`，未改产品/测试代码。多 STA/Application 静态状态、原始失败顺序仍待验证；阶段 0 数据/基线门槛不变。详见测试基线分类报告运行 L。
- P2 Provider 能力盘点复核发现当前 `AIService` 只按通用范围 clamp temperature/top_p，却仍向 Anthropic Messages、Gemini GenerateContent 和多数 OpenAI-compatible profile 显式发送两者。默认 temperature 为 0.4；OpenAI 官方 GPT-6 Astra 文档不支持自定义采样参数，Anthropic Opus 4.6 后模型也不接受非默认 temperature，因此使用这些 model ID 的现行配置可能被拒绝。已把源代码位置、官方限制、unknown capability 行为与 fake-handler 负向验收要求补入 Provider 能力盘点。未改生产请求，也未发起 Provider 请求；真实质量收益未测。后续在阶段门槛满足后按 provider + exact model/version + API 版本适配，且必须让 UI 显示用户值是否实际生效。来源：[OpenAI reasoning guide](https://developers.openai.com/api/docs/guides/reasoning)、[Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)、[Gemini API](https://ai.google.dev/api/generate-content)。
- P2 同一源码链复核还确认 `ProviderInferencePresets.Apply` 的 Low/Medium/High 实际设置 temperature、top_p、max_tokens、timeout；真正的 `reasoning_effort` 目前仅 DeepSeek V4 adapter 映射。ManagedLocal 另外把 context size、batch size、GPU layers、CPU threads、LoRA scale 传给 llama-server 启动参数，生成请求则发送 sampling/输出上限；高档并未提升 llama.cpp 的 reasoning budget。设置编辑视图把 High 描述为“更充分推理”，其技术含义对多数后端并不成立。已在 Provider 能力盘点中明确区分“采样/输出档位”与模型 reasoning/thinking capability，并建议下一阶段按 capability 显示实际生效字段。仅更新设计依据；未改变 UI、配置或生成行为。
- P2-B 输入/验收设计已落档：由于模型 ComboBox 可编辑，且 `ModelMapping` 会把 profile alias 转换成真正发送的 model ID，能力查找测试必须以 resolved model 为准；OpenRouter/custom/Ollama/LM Studio 相同前缀不继承厂商直连接口 capability；ManagedLocal 按 runtime build/hash + model/template 绑定。Provider 能力盘点现列 9 类参数/Schema 场景，加入 GPT-6/o-series 的采样禁用、`max_completion_tokens` 映射和 reasoning token 可见截断预算。验收目标是 unsupported 字段泄漏数 0、supported 用户值无变形、unknown 不静默标“已生效”、fake transport 不发真实网络请求。方案对比后推荐带来源/日期的静态能力事实表、unknown 默认及可见高级覆写。该步骤完成的是测试输入设计，不是实现或测试结果；等待 P0/P1 后再落为请求级测试。P2 全体仍有 WPF 宿主根因等项待查。
- P2-C 配置审查确认 v20→v21 有 profile/SecretId/活动 profile 迁移测试，但没有从固定 v21 JSON 文件加载的 fixture；一般 Save→Load 测试不等价。当前 ConfigService 对新增可选字段若不 bump 版本可正常加载缺省字段，但这项约定尚未有 v21 fixture 证明。已在能力盘点与开发计划写明策略：缺省 capability=unknown、路由=Manual 时优先维持 version 21；有语义不兼容才增显式 v21→v22；后续实际加字段时按 TDD 加双 profile 的 v21 Load→Save→Load fixture，覆盖 active profile、model mapping、采样值、SecretId、备份/版本。源码复核未发现当前预期红测的产品缺陷，故本轮没有新增只会通过的测试，也没有改 ConfigService/生产代码。
- 命令、TRX/blame 边界、版本对照和回滚依据见[测试基线失败分类报告](../test-baseline-classification-2026-10-02.md)运行 E–G。

## 2026-10-03 继续推进记录

- 状态机复核发现 evidence manifest 可同时声明 `reviewed` 和 `open` 例外，出现“复核完成但仍有未解决问题”的矛盾。先加入包含已验证 review、准入、custody 与 open 例外的失败回归，确认旧逻辑错误接受；现 `reviewed` 状态拒绝 `open`/`rejected` 例外，JSON Schema 与运行时 validator 规则同步，保留 `withdrawn` 历史记录并要求用当前数据快照/权利复核判定。新增拒绝用例覆盖 open/rejected；证据清单测试 20/20、盲评完整相关筛选 149/149 通过。status 仍只是未由本机认证的外部复核声明。
- 评审证据索引的时间线复核发现，validator 会严格解析双评与第三方裁定的 UTC 时间，却不检查裁定提交是否早于评审提交。现收集同一记录两份评审中较晚的有效时间；存在裁定证据时，拒绝早于该时间的裁定，并在诊断中保留“可信时间须对照受控系统核验”限制。新增 adjudication-before-reviews 回归；证据清单校验 18/18、盲评准入/评分/快照/候选/工作流/收口联合筛选 147/147 通过。该检查只证明声明时间内部自洽，不验证真实时间。
- 阶段 0 失败请求分类复核发现 runner 输入、权限或文件系统异常会落入 `provider_error`，把工具链故障混同为 Provider 故障。现新增 `runner_error` 分类，应用于 UnauthorizedAccess/IO/参数类异常，并同步 prediction schema 与 scorer 白名单；端到端异常请求回归验证失败产物无正文且 scorer 正确计数，候选/评分/manifest finalizer 与 validator 联合筛选 65/65 通过；扩大后的盲评完整相关筛选为 146/146。这只改善诊断归因，不改变阶段 0 准入，也不代表模型质量。
- frozen_test 解封确认此前虽已由候选运行服务强制，但检查最终清单时发现该状态没有进入运行证据：run-info、sealed map 和最终清单之间无法审计确认是否发生。现将运行清单协议升至 v3，候选服务把确认位同时写入 run-info/sealed map；单候选与 cohort finalizer 校验两份记录一致、且数据含 frozen_test 时必须为 true，再将该值写入最终 execution 字段。独立 manifest validator 也要求此布尔字段，并拒绝冻结数据集未确认的清单。新增单候选和 cohort 的拒绝/成功回归，清单相关筛选 43/43 通过。最初较宽筛选出现 5 个候选生成失败，追踪到盲评执行器为 `autoArchive=false` 的请求仍创建系统临时目录和 SQLite 归档库；该环境的系统临时目录不可写，异常被错误归类为 `provider_error`。现将 `PolishWorkflowService` 的归档依赖改为仅自动归档时必需，并移除盲评执行器中无用的临时数据库创建，候选执行测试 12/12 通过。后续联合筛选又发现润色工作流测试 fixture 将 SQLite 根目录放在同一不可写临时位置；fixture 现使用项目 `out/test-artifacts` 并继续自动清理，候选、工作流、收口器及 validator 联合筛选 65/65 通过；扩大到盲评、快照、候选与数据审计联合筛选现为 144/144。阶段 0 仍因缺真实授权数据和同条件基线未通过。
- frozen_test 解封边界复核发现确认仅由 CLI 的 `BlindSnapshotSplitSelector` 执行；`BlindCandidateRunService.RunAsync` 可被其他调用方直接调用，未经确认运行 `frozen_test`，或将 development 记录标成 frozen_test 写入 sealed manifest。现将 `confirmFrozenTestLocked` 显式传至运行服务，服务在任何产物写入/Provider 调用前强制确认；非 `all` 模式同时检查每条样本 split 与声明一致。红测先复现无确认和 split 错配两条可绕过路径；拒绝/允许路径覆盖直接 `frozen_test` 和包含冻结样本的 `all`，候选运行服务与快照选择器筛选现 18/18、盲评关联筛选 131/131 通过。CLI 与服务双层保护现已对齐；阶段 0 数据及外部证据门槛不变。
- PII 启发式覆盖复核发现数据集审计已检 QQ 联系标识，但未覆盖常见微信账号标记；候选评分虽复用相同检测器，错误说明也漏列 QQ。新增明确“微信号/微信账号/WeChat ID”及 `wxid_` 前缀检测，应用于 gold、来源脱敏说明、人工标签和候选输出的现有公共筛查入口；错误说明同步 QQ/微信范围。新测试先在缺陷实现上 3/3 失败，加入后 PII / blind workflow / source-register / scoring 的关联筛选 125/125 通过；该规则仅对显式标记/特征前缀触发，作为人工复核预警，不声称覆盖所有个人信息。
- 随后发现标识元数据没有进入同一 PII 扫描：含邮箱的样本 ID、含手机号的语义族 ID及含邮箱的来源引用都可通过内容扫描；若样本 ID 含 PII，审计诊断还会回显它。红测复现三字段漏检后，现将 ID、语义族 ID、来源类型/引用/脱敏说明并入扫描，并在任何逐记录诊断中将敏感 ID 固定替换为 `[redacted-record-id]`。该回归验证三个字段均触发、敏感 ID 不回显；盲评准入/评分/来源登记/候选与收口关联筛选 126/126 通过。
- 跨入口复核又发现 `BlindEvaluationScorer` 可独立调用，且 PII 输出错误会原样写出 `prediction.Id`，绕过准入审计时仍可能泄漏邮箱等敏感 ID。将安全诊断引用规范化方法复用于评分器的逐样本错误、比较错误和重复 ID 错误；同时移除重复预测错误中可能回显的 candidate ID。先观察到敏感 ID 回归失败，修复后评分相关筛选 9/9、准入审计/评分/来源登记/候选执行与收口关联筛选 127/127 通过。
- 复核盲评候选执行的数据边界时确认：产品提示由白名单字段组装，当前不会把 `reference_output`、gold annotations、评审身份或任意 context 字段发送给 Provider；但中间 `BlindWorkflowRequest` 曾保留整条 `BlindEvaluationRecord`，让 gold、授权来源和评审材料不必要地进入执行对象。现改为只持有 `RecordId`、`Task`、`Split` 及执行所需的类型化请求/计划；快照编译和工作流执行均不再持有原始评测记录。回归注入了 gold、评审和任意 context 哨兵，验证执行对象不包含原始记录类型且 Provider 提示不含这些哨兵。首轮构建红测确认旧类型边界，修复后盲评、快照编译、候选执行与工作流筛选 118/118 通过。该改动缩小评测金标暴露面，不证明来源授权/评审身份真实，也不改变生产生成行为；阶段 0 仍未通过。
- 阶段 0 证据时间戳复核发现三处校验器使用宽松的 `DateTimeOffset.TryParse`，会接受 `10/03/2026 12:00:00 +00:00` 这类非 RFC 3339 字符串并将其当成审计时间。新增统一的严格 UTC 时间解析，并用于证据清单、运行清单及来源登记；要求 `T` 分隔符、完整日期和秒、明确 `Z` 或 `+00:00`，可选 1–7 位小数秒。又发现运行清单允许最终 `created_at_utc` 早于执行完成时间；新增运行生命周期顺序校验。两类问题均先由回归用例复现，再修复实现；随后时间校验相关的三类阶段 0 校验器筛选 40/40、联合回归 108/108 通过。此修复提高记录格式与逻辑自洽性，不证明外部时间真实性，也不改变模型质量或生成行为；阶段 0 仍未通过。
- 将受控证据 Manifest 校验器逐层对照其 JSON Schema 后，发现它未拒绝未知字段、重复 JSON 属性，并漏查 `exceptions` 类型、签名值类型、`not_verified` 审核字段及 `not_run` 准入报告占位字段。新增根级/嵌套未知字段、重复键和四种 Schema 结构偏差回归；校验器现递归拒绝重复属性、按 schema allowlist 检查封闭对象，并验证上述类型/必填/状态组合。先观察到 7 条新回归失败，修复后证据 Manifest 测试 14/14、盲评/Provider usage parser/API 无内容遥测联合回归 115/115 通过。签名对象内部结构仍保持开放以兼容受控签名方案，签名真实性继续由受控外部审计负责。该进展只提升证据索引结构一致性，不认证真实授权/身份/评审，也不改变模型质量；阶段 0 仍未通过。
- 对照 `evidence-manifest.schema.json` 的具体引用 pattern 时，发现通用引用规则还会接受 Schema 拒绝的 `role:a`。现将 role、reviewer、source、exception 四类引用分别按对应格式校验，并把 `exceptions` 从任意对象收紧为不含自由文本的受控索引：分类/状态、记录时间、负责角色及成对可选证据引用和摘要。Schema、校验器和接收规程已同步，并新增自由文本例外拒绝、受控引用例外接受及 role 引用边界回归；证据 Manifest 测试 17/17、盲评/Provider usage parser/API 无内容遥测联合回归 118/118 通过。签名真实性仍由受控外部审计负责，阶段 0 仍未通过。
- 来源登记复核发现 `deidentification_review.method_summary` 虽有限长，却未纳入已有隐私启发式筛查；新增邮箱样例拒绝回归并先观察到失败，随后复用盲评数据的手机号/身份证/邮箱/QQ/密钥检测器。方法摘要现在命中时结构完整性报告失败，诊断不回显原文；来源登记测试 14/14、盲评/Provider usage parser/API 无内容遥测联合回归 119/119 通过。启发式不能替代人工隐私复核，授权与阶段 0 门槛不变。
- 为 ManagedLocal 基线补齐实际制品身份：候选工厂在执行前流式重算已安装模型、LoRA 和实际选中 CPU/Vulkan `llama-server.exe` 的 SHA-256，核对模型/适配器注册摘要与基座哈希，并记录 installation ID、模型版本/字节数、运行时 flavor/文件版本。provenance 只存摘要和非路径标识，不泄露本机目录。
- 封存候选映射将 provenance 绑定到随机 alias；最终运行清单继承该对象。独立清单校验器对 ManagedLocal 强制校验字段、哈希、适配器 ID/hash 成对关系和运行时 flavor；缺少 provenance、模型文件被改写或哈希格式错误均拒绝作为可复现运行记录。
- 新增 tamper、候选映射/单候选与 cohort 最终清单传递、ManagedLocal 必填、schema 哈希拒绝等回归。覆盖盲评、ManagedLocal 候选、单候选/cohort finalizer 与运行时模块的定向筛选 87/87 通过；runner `--help` 可运行；`git diff --check` 通过（输出只有工作区既有 LF/CRLF 提示）。本次只以小型合成 GGUF 字节和占位运行时文件测路径，没有运行真实模型或访问云 Provider。
- 阶段 0 仍未通过：工作区没有可供实测的正式盲评集及固定 CPU/Vulkan runtime，授权盲评样本与受控人工复核证据也仍缺。哈希 provenance 提升了身份可追溯性，不替代来源授权、人工核验和真实设备基线。
- 费用度量补齐：run manifest validator 现在校验 `api_cost_accounting` 状态、非负金额、币种、受控凭证引用、SHA-256 与 UTC 核算时间；估算必须关联费率卡，账单金额必须关联账单记录。单候选和 cohort finalizer 无账单材料时分别写云端 `not_reported`、本地 `not_applicable`；validator 还拒绝费用状态与 Cloud/Local Provider 不一致。根据官方 usage 字段，缓存读取/写入 token 已从 Provider parser 贯通至请求遥测、评测汇总及最终清单；CLI 输入白名单也接受新字段，prediction schema 对运行时 telemetry nullable 字段已同步，评分器拒绝负用量；跨样本缓存细分缺失时保持未知。盲评筛选 87/87、盲评+Provider usage parser+AIService 无内容遥测联合筛选 104/104 通过；500 条合成候选 CLI 评测和最终清单回归通过，9 个评测 JSON 文件解析通过。没有实际费用数值，阶段门槛不变。

## 2026-10-03 受控证据索引推进

- 对照阶段 A 工作包，现有受控接收规程此前只有模板，没有机器可校验的逐样本证据链接。本轮新增 `evidence-manifest.schema.json`、`blind-evidence-validate` 和对应验证器：绑定实际数据字节哈希、schema/准入协议版本、样本数与 handbook；逐记录校验来源引用、授权证据摘要、盲包身份/哈希、两名评审与 `human_review` 引用一致、各自提交时间/内容哈希及盲包哈希；有争议记录还需独立裁定引用并绑定相同盲包。
- 证据引用必须是受控 vault 引用，评审和角色仅保留不透明引用；拒绝 URL/文件系统路径。命令额外重跑现有准入/训练集泄漏检查，输出 `record_integrity_valid`、授权/独立性状态声明及工具不可认证外部事实的明确字段；`rights_reviewed`、`reviewer_independence_verified` 与 `phase_0_gate_passed` 不会因清单声明而被置真。`--output` 采用 create-only。
- 首轮红测确认此前没有证据索引验证入口；增加一致快照接受、数据 hash 篡改拒绝、评审引用错配拒绝、路径泄露拒绝、裁定盲包不匹配和 reviewed 状态缺少封存链拒绝用例，当前证据验证器 6/6 通过。覆盖盲评工具、证据验证、本地运行时的合并定向筛选 72/72 通过；DatasetBuilder CLI help 与模板/Schema JSON 解析成功，`git diff --check` 通过（只显示工作区原有换行符提示）。
- 该工具只检查内容与引用之间的一致性，不能证明 vault 文件真实、授权范围有效、身份独立或可信系统时间；没有受控核验与真实基线时阶段 0 仍不通过。工作区当前依旧没有正式 `blind-eval.jsonl` 或可运行 CPU/Vulkan llama-server，未执行真实候选。

## 阶段 A 标注口径与来源登记准备

- 新增 `datasets/ai-evaluation/annotation-handbook-v1.0.md`，按当前 v3 schema 和 3.0 准入协议定义输入风格、produce/clarify/refuse、事实/约束锚点、高风险反转、五项 1–5 评分、双评分歧裁定和匿名 pairwise 口径。等级锚点是可操作初稿，正式冻结前仍需评审校准；不宣称其已证明评审一致性。
- 新增来源登记 JSON Schema 与模板，记录权利依据/用途/期限、受控证据引用和 SHA-256、脱敏复核、撤回/删除角色及权利复核状态；没有写入真实来源或个人信息。字段结构校验不能证明授权原件或身份真实性。
- 新增 `dataset-freeze-policy-v1.0.md`，规定精确重复与语义族隔离、family 级 split、冻结包版本绑定、受控保管、变更/撤回与重冻结，并明确哪些内容目前只能人工/受控系统保障。
- 新增 `source-register-validate --register ... --dataset ... [--output ...]` CLI 与校验器：严格检查登记字段/枚举/日期/哈希/不透明引用，逐条匹配样本 source_ref 和 source_kind，检查候选推理及人工评审用途声明，并要求 `verified` 声明具备复核字段；样本可选来源记录 hash 与授权材料 hash 分开绑定。新增来源登记用例 12 项；盲评全链路相关筛选 78/78 通过，CLI 集成用例验证 create-only 报告和固定关闭的阶段门槛。报告不认证授权原件、身份或外部评审事实。
- 将以上资产接入证据接收规程、评测目录 README 和下一阶段计划。阶段 0 仍未通过；缺少真实授权样本、受控独立评审凭据及冻结条件下的云/本地产品基线。

## 2026-10-05 W2.3 人工近重复裁定导入通道

| 项目 | 内容 |
|---|---|
| 当前计划节点 | 阶段 0 内部合成回归整理 W2.3；上游候选包已冻结，人工裁定尚未回收。 |
| 输入 | v3 候选包 19 对、冻结 v1 cases、人工审阅工作簿；工作簿仍为未填写状态。 |
| 产出 | `PolishRegressionNearDuplicateWorkbookImporter`、CLI `polish-regression-neardup-import-workbook`，以及带机器可核对来源绑定页的 XLSX。 |
| 验收 | 导入按 `pair_id` 映射且保留源引用；核对候选/模板/父 manifest/源 cases/规则哈希、元数据和案例证据；create-only，并禁止输出落入冻结案例或候选包目录；空字段、格式损坏及过期输入拒绝。9 项专项测试通过，覆盖乱序映射、证据篡改、重复 pair、来源失配、空字段、禁止覆盖/污染及格式损坏；联合筛选 106/106 通过。曾复现缺少 manifest 必需字段导致未处理异常，现 CLI 返回稳定输入错误。真实空白工作簿演练仍返回 `decision-incomplete`、退出码 2，未创建输出；候选裁定验证器报告 19 对、0 对已裁定。 |
| 结论与影响 | 本步骤只减少人工转录、来源错配和覆盖遗漏风险；没有自动推断裁定，也没有改变生成行为或模型质量。W2.3 仍未通过，W3.3 16 族人工复核和 W4 补样继续关闭；阶段 0 贡献为 0。 |
| 下一触发条件 | 人工完成 19 对裁定，特别复核其中 9 对跨 split 候选；导入新 JSONL 并通过 `polish-regression-neardup-validate`。只有边界锁定后才开始 W3.3；任何合族/split 调整须新建版本并重建下游产物。阶段 0/A0.1 另需 ≥500 条授权脱敏样本及可核验独立评审原始证据，未到位前不得修改生产生成链路。 |

操作文件：[W2.3 裁定工作簿](../outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review.xlsx)；实现见 `DatasetBuilder/PolishRegressionNearDuplicateWorkbookImporter.cs` 与 `DatasetBuilder/Program.cs`。

## 2026-10-05 W2.3 裁定记录落盘与校验（所有者授权、AI 辅助）

| 项目 | 内容 |
|---|---|
| 当前计划节点 | 阶段 0 内部合成回归整理 W2.3 裁定回收；上游候选包与冻结 v1 未改动。 |
| 输入 | v3 候选包 19 对（含 9 对跨 split）、冻结 `polish-regression-v1/cases.jsonl`、未填写的工作簿。 |
| 产出 | 工作簿副本 `out/test-artifacts/w23-owner-authorized/near-duplicate-human-review.owner-authorized.xlsx`；经官方导入器落盘的 `datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl`（原名 `decisions.completed.jsonl`，已改名）与该目录 README、`gate-status.json`；分析建议见 [W2.3 裁定建议](../polish-regression-w23-adjudication-recommendation-2026-10-05.md)。 |
| 验收 | `polish-regression-neardup-import-workbook` 导入 19/19（来源哈希、案例证据、候选引用全部通过）；`polish-regression-neardup-validate` 返回 `valid=true`、`candidate_count=19`、`adjudicated_count=19`、`issues=[]`。 |
| 裁定结论 | 19 对两侧证据仅 `purpose` 槽位不同（事实骨架、formality、explicit requirements、claims 完全一致），全部判为 `distinct_task_intent_same_backbone`；**无合族、无 split 调整**，9 对跨 split 候选逐对核对为字符 3-gram 启发式误报。族定义「事实骨架 × purpose」由项目所有者于 2026-10-05 确认，已写入每条 rationale。 |
| 结论与影响 | `reviewer_id` = `reviewer-a`，为 AI 模拟身份，不是真人签署；`reviewer_identity_verified=false`。`valid=true` 仅表示记录完整、血缘一致、覆盖无遗漏，不能代表人工门槛通过。该文件保留为 AI 分析草稿；W2.3 真人裁定未完成，W3.3/W4 补样未解锁，阶段 0 贡献仍为 0。 |
| 已知限制 | 字符 3-gram Jaccard 阈值 0.80 未校准；事实**骨架**跨 development/regression 复用（族级隔离成立、骨架级不成立），须保留在 W2/W8 报告中。若日后改用骨架级族定义，本记录 19 条结论全部作废并须重建版本。 |
| 下一触发条件 | 对外引用本记录时须注明模拟审阅身份来源。阶段 0/A0.1 另需 ≥500 条授权脱敏样本、独立双评、分歧第三方裁定与受控证据核验；未到位前不得修改生产生成链路。 |

## 2026-10-05 W2.3 门槛复核：机器校验通过不等于真人裁定

- 再次运行 `polish-regression-neardup-validate`：`valid=true`、19/19 覆盖、`issues=[]`，但 `reviewer_identity_verified=false`。该 JSONL 已由 `decisions.completed.jsonl` 改名为 `decisions.simulated-review.jsonl`，并与同目录 README、`gate-status.json`（`w2_3_human_gate_passed=false`）一致明示 reviewer 是 AI 模拟身份；用于人工填写的正式工作簿仍有 19 行空白字段。
- 因计划要求边界对由真人裁定，现将 W2.3 业务状态明确为 **awaiting_human_review**。保留模拟记录供分析，不把它用于锁定边界；W3.3 人工审阅和 W4 补样仍未解锁。已有 review-packet-v2 只是准备材料，`human_verified=0/16`。
- 后续发现填写副本 `outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review - 副本.xlsx`：decision/reviewer/time 19/19 已填；rationale 单元格虽非空，但 19/19 都是占位语“无非空理由”，具体理由 0/19。
- 新增 `ReviewRationaleRules`，导入器和裁定 validator 统一拒绝常见中英文占位语；TDD 回归先红后绿，近重复导入器/裁定器专项 16/16 通过。实际副本导入探测返回 `decision-incomplete: rationale 必须是具体理由，不能留空或使用占位语`；临时输出未创建，工作簿、AI 草稿、冻结数据与候选包均未覆盖。此门禁只识别明显占位语，不验证理由语义或审阅者身份。
- 该副本 19/19 均选 `same_semantic_family`，与当前「事实骨架 × purpose」族定义及候选对中不同 purpose 的证据冲突。若按这些结论合族，关联边会涉及两个跨 split 连通分量（8 个案例、2 个案例）；冻结 v1 不可直接修改，必须先由项目负责人明确是否变更族定义，再决定是否新建版本和重建派生包。
- 按用户指令继续计算后果，已生成独立分支 `datasets/polish-regression-backbone-family-proposal-v1/`：只对这 19 条已选择边求连通分量，其余 101 个全量案例对未审阅且未推断；当前 8 组不是最终族数。split 投影把包含 regression 的合并组整体留在 regression，development 变体由 2,414 降至 802（6 组），regression 由 586 增至 2,198（2 组），移入 1,612 条。分支为 `draft_proposal`，未改冻结数据或下游标签/审阅包，也不计入阶段 0。
- 下一项可执行的人类工作：将 19 条占位语替换为逐对、基于证据的 rationale，并明确上述政策冲突。完成后才导入新裁定文件、复核决定并判断 W2.3 是否可关闭。W3.3、W4 补样仍未解锁；阶段 0 仍需要独立的 ≥500 条授权脱敏盲评样本及受控证据，当前贡献为 0。

## 架构梳理

应用是 .NET 8 / WPF Windows 桌面程序，核心 AI 工作流位于 `Services`，交互与当前 Provider 选择位于 `ViewModels/MainViewModel.cs`，配置与 Provider profile 持久化在 `Models/AppSettings.cs`、`Models/ProviderProfile.cs`，本地模型清单/下载/存储/运行时分散在 `Services/LocalModel*`、`Services/LocalRuntime.cs` 和 `Models/LocalModels.cs`。归档通过 `ArchiveService` 独立完成。云端和本地模型均由可替换的生成客户端承接，主程序不会内置数 GB 权重。

业务依赖概览：

```text
MainViewModel
  ├─ 润色 → PolishWorkflowService → PolishPromptBuilderService → ITextGenerationClient
  │          └─ PolishResponseParser → ProfessionalQualityValidator → ArchiveService
  ├─ 提示词优化 → PromptOptimizationWorkflowService → PromptBuilderService/AgentContextPipeline
  └─ 当前 Provider profile → 生成客户端工厂 → 云端 AIService / 本地 LocalTextGenerationClient

本地模型管理 → LocalModelCatalogService → LocalModelDownloadService → LocalModelStore
                                          ↘ LocalRuntimeManager / llama-server.exe
配置持久化 → ConfigService → AppSettings / ProviderProfile / CredentialStore
```

这是对静态源码依赖的概括，不代表每个定义的服务都进入生产路径。

## 主要发现

1. **两个工作流的上下文路径不一致。** 润色由 `PolishPromptBuilderService` 直接组装上下文；提示词优化经过 `PromptBuilderService` / Agent 上下文管线。仓库另有结构化生成工作流和偏好/上下文服务，但在当前 UI 工作流中未见统一的共用契约。
2. **结构化生成主链路已接通，仍有能力目录边界。** 润色和提示词优化已接入共用结构化生成契约；契约可表达 final/clarification、问题数组、情绪元数据和本地质量校验。OpenAI-compatible、Anthropic Messages、Gemini GenerateContent 及 llama.cpp 均有 Schema 传输路径，供应商拒绝 Schema 时按受控文本路径降级。当前模型级能力表仍是有限白名单；尚未覆盖 OpenAI Responses API 适配和所有 Provider/model 组合的真实线上可用性。
3. **模型路由已从建议文字转为用户策略驱动。** `ProviderRouter` 将 Manual、LocalOnly、PreferLocal、PreferCloud 和任务 profile 绑定应用于主生成入口；显式 fallback 在规范化 Provider 失败后最多切换一次并固定后续调用。复杂度/预算自动排序仍未实现，默认 Manual，故 Fast/Balanced/Reasoning 仍不等于自动切换。
4. **个性化学习可见性和可控性不足。** 当前 `StructuredPreferenceService` 以编辑长度、删除固定套话、接受/重试/撤销等计数形成建议；配置已保存表达偏好，但推断偏好的来源、置信度、场景边界和用户确认状态不完整。阶段 3 应沿用现有数据并增加可管理元数据，而非重置用户资料。
5. **本地模型基础设施部分具备，交付链仍不完整。** 已有模型清单、下载/校验/存储和 `llama-server.exe` 管理路径，支持从可选目录发现运行时；开发规范明确运行时缺失不得阻塞主程序，模型包须与主安装包解耦。当前 `ContextSize`、`BatchSize`、`CpuThreads`、GPU 模式与适配器 scale 会影响启动参数；`Seed` 与 `RepeatPenalty` 已进入本地聊天请求体；`KeepLoaded=false` 现会在请求完成、失败或取消后停止受管服务，`true` 保持现有复用行为。模型/运行时版本兼容、可取消恢复、硬件分档和用户机器覆盖仍需持续验证。
6. **评测/数据工具比本评估初稿覆盖得更广，需区分两套证据。** `DatasetBuilder` 已包含数据校验、PII 启发式扫描、dev/test 双人复核计数、输入泄漏检查、质量评估与架构对照工具；仓库含 12,000 条 `hxz-synthetic-v1`（polish 与 prompt_optimize 各 6,000）及 12,000 条架构合成数据。执行现有 canonical 校验与输入泄漏检查时，v1 返回 `valid=true, issue_count=0`、`clean=true, issue_count=0`；report 记录 1,000 dev、1,000 test 和 4,000 条 `reviewer_count=2`。`polish-agent-v2` 另有 3,000 条合成行为数据。上述数据及 `reviewer_count` 字段不是可追责的人工双盲标注凭证，也没有变成授权外部盲评证据。
7. **已有小规模离线基线，但没有覆盖阶段 0 所要求的产品基线。** `training/experiment-log-2026-09-07.md` 记录了 Qwen3 4B 在合成 dev 切片上的多轮结果（例如 100 条分层 few-shot 结果，以及架构实验）；这些结果已自述不可外推，且缺少 API / 当前生产工作流 / 本地目标硬件统一冻结比较。阶段 0 缺口应准确表述为“缺授权盲评与同条件产品比较”，不能笼统说仓库没有实验记录。

## 评估结论

整体系统性提升可行。高收益路径是先打通应用层的统一提示上下文、Schema/质量门禁、真实路由和偏好确认，再用冻结评测对比模型；本地运行时采用可选、独立版本化包；LoRA/SFT 只有在应用层优化后的盲评仍证明有稳定风格差距时才立项。

风险主要是：误把“结构化接口已存在”当成“业务已使用”；把档位建议误当成执行路由；偏好规则从弱信号过度拟合；模型下载清单的来源/哈希/运行时绑定过时；未经许可把用户文本上传到训练或遥测。上述风险可通过阶段门槛、显式用户策略、内容零遥测、签名清单与可回退模型包控制。

## 按阶段的当前状态

| 阶段 | 状态 | 证据/剩余工作 |
|---|---|---|
| 0 基线与验收集 | **未通过，进行中** | `datasets/ai-evaluation/` 的准入、盲评、配对统计及清单工具已实现；但实际授权独立盲评样本、可核验人工标注及同条件云/本地产品基线仍缺失。工具和合成数据演练不作为质量证据。阶段 0 与后续工程实现并行推进，缺口限制质量结论和模型晋级，不阻止继续实现用户可控功能。 |
| 1 统一生成契约 | **主链路完成，能力扩展进行中** | 两条工作流已接入统一契约；Anthropic/Gemini/OpenAI-compatible/llama.cpp 及 OpenAI Responses 载荷按各自协议发送；EmotionAssistant 元数据并入 schema，盲评束含输出契约指纹；OpenAI Chat/Responses 拒绝与截断可分类。Provider/model 能力目录仍有限；Responses 仅覆盖 fake-handler 契约，线上拒绝率与质量收益未测。 |
| 2 可执行路由 | **核心路径已落地，边缘验证继续** | 已持久化四种策略、按任务绑定 profile、严格 LocalOnly、显式 fallback 与实际模型回显；明确模型拒绝不会重试到备用 Provider。仍需补其他故障分类覆盖、全部生成入口盘查、真实 Windows 端到端/打包验证；自动路由质量与延迟排序未实现。路由回归是配置与边界证据，不是质量提升证据。 |
| 3 用户个性化 | **任务/场景设置、候选确认/忽略和单范围清除已落地，候选类型继续扩展** | 按润色/提示词任务及具体场景编辑篇幅、语气、原措辞保留和禁用表达；篇幅候选与固定套话候选均需用户载入并保存后才确认，固定表达可逐项忽略；已确认偏好可单独清除当前任务/场景，学习统计仍不自动写入提示。候选阈值不是盲评校准，实际偏好收益仍待阶段 0 盲评。 |
| 4 本地运行时下载包 | **单机端到端闭环已实测，多机交付仍待验收** | 独立 CPU/Vulkan 包已完成下载/安装/健康/卸载路径；Qwen3-4B 在一台 RTX 4060 Laptop 经正式安装包完成真实本地润色。干净 Windows 镜像、更多硬件/驱动、显存采集和稳态性能分位数仍缺；单例烟测不构成模型质量或硬件矩阵结论。 |
| 5 LoRA/SFT | 暂缓 | 需先通过阶段 0–4 评测；仅在应用层个性化仍有稳定差距时训练。 |

## 下一步开发顺序

1. **并行推进：** 继续收集/准入阶段 0 的授权盲评证据，同时扩展 Provider 精确模型能力和完成状态分类，盘查阶段 2 的所有请求入口及网络边界；缺少样本限制质量结论，但不冻结可验证的工程开发。
2. 继续扩展来源明确的个性化建议候选，并检查新增信号的配置迁移、逐项删除和偏好清除路径；本轮已补固定套话候选。
3. 在干净 Windows 与目标 GPU/CPU 档完成本地运行时验证；用户偏好、路由和模型资产各自可重置/回退。
4. 阶段 0 的真实盲评到位后再计算质量/个性化收益；在证据到位前报告工程指标（调用数、错误率、延迟、用量）并明确“质量未测”。
5. LoRA/SFT 保持最后决策：只有应用层个性化在授权盲评中留下稳定差距时才启动训练。

## 最新官方资料与设计影响

- OpenAI [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)：原生 Schema 可提升格式遵循，但仅支持 JSON Schema 子集；拒答、截断和不支持的 schema 仍需应用端处理。使用场景应是最终响应结构，而非工具调用替代。
- OpenAI [迁移到 Responses API](https://developers.openai.com/api/docs/guides/migrate-to-responses)：Chat Completions 仍需兼容；迁移应通过 provider-specific adapter，不将一种请求结构假设为通用格式。
- Google [Gemini Structured Output](https://ai.google.dev/gemini-api/docs/structured-output)：JSON Schema 同样是子集，不同 Provider 必须各自能力映射与校验。
- llama.cpp [Server 文档](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)：当前服务端支持 OpenAI 兼容 API、结构化 JSON、LoRA 及运行时服务功能；这不能替代本项目对固定提交、模型模板、Windows 二进制、许可和硬件的集成验证。
- OpenAI [Prompt Caching](https://developers.openai.com/api/docs/guides/prompt-caching)：稳定前缀在符合具体模型缓存门槛时可能改善成本/延迟，但命中并无保证，且响应 API 状态存储、缓存保留策略有隐私含义；应按用户/组织策略映射 `store` 等选项，并在同一基准记录 cached tokens、延迟和成本，不作为质量优化的替代。
- Qwen 官方 [Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B)、[Qwen3.5-9B](https://huggingface.co/Qwen/Qwen3.5-9B)和 [Qwen3.8-27B 权重卡](https://huggingface.co/Qwen/Qwen3.8-27B)均提供 Apache-2.0 Transformers 权重。Qwen3.8-27B 因资源需求仅作高资源本地候选研究；阿里云 [Model Studio 页面](https://help.aliyun.com/en/model-studio/qwen3-8-27b)同时列出托管服务。纳入本地包前仍需核验特定权重修订、量化转换、运行时模板和 Windows 硬件兼容；参数规模与模型方 benchmark 不构成本项目润色质量证据。llama.cpp 官方仓库曾报告特定构建对含 `$ref/$defs` 的 schema 可能静默退化；本地结构化输出必须针对固定构建/模板做回归并保留应用侧校验，详见 [issue #21228](https://github.com/ggml-org/llama.cpp/issues/21228)。

最后核对日期：2026-10-02。官方文档与模型清单会变化，发版前须再复核具体版本和许可。

## 2026-10-02 本轮验证记录

- 为当前 Windows 受限执行环境中 TestHost 查询父进程失败的问题，将测试项目的 `Microsoft.NET.Test.Sdk` 从 17.11.1 升级至 18.10.1，并关闭 TestHost 的父进程查询；临时目录定向到工作区 `out/test-temp` 后，测试宿主可以运行。该环境参数仅用于本地验证，不改变应用数据目录或用户配置。
- 阶段 0 盲评工具与主题切换回归组合筛选通过 21/21；`Huaxiazi.csproj` 构建通过（0 警告、0 错误），`git diff --check` 通过。
- `blind-evaluate --output` 已改为仅创建新文件：目标报告存在时拒绝写入，避免重跑覆盖不可变实验产物；新增写入器回归测试验证新建目录/报告和保留既有文件。盲评/写入器筛选共通过 19/19，DatasetBuilder 构建通过（0 警告、0 错误）。
- `blind-evaluate` 现在也扫描成功候选输出中的疑似手机号、身份证号、邮箱、明确 QQ 联系标识和密钥，命中时不回显内容并使评分失败；此检查只作为隔离触发器，不能替代人工隐私审查。新增回归后盲评/写入器筛选通过 20/20。
- 完整测试集运行结果为 966 项：857 通过、11 跳过、98 失败。主要可见失败集中在 WPF/设置用例初始化全局 SQLite 数据目录失败，以及发布脚本静态断言与当前脚本内容不一致；这些失败尚未逐项完成根因分类，故全量测试状态记为未通过，不用子集通过替代。
- 主题切换测试同步到 `ThemeService` 的明确契约：单独 `SkinService.ApplySkin("LightPaper")` 仍断言 `#F3F1EC`；`ThemeService.Apply` 后的明暗模式层断言背景 `#F5F4F1`、玻璃层 `#F8F7F2`。窗口测试使用隔离的临时 Archive/Draft 存储，避免读写真实用户数据。
- 已新增独立 Windows 工具 `tools/BlindEvaluationRunner` 的离线 `snapshot` 命令：先以现有审计器验证完整盲评 gold 和 train/dev 重叠，再调用产品 `PolishPromptBuilderService` / `PromptBuilderService` 编译首轮提示，产出只含 id/task/split/system_prompt/user_message 的 JSONL 和 SHA-256 清单；不发起网络/Provider 请求、不含 gold 答案/评审身份、输出目录不可覆盖。split 必须显式选择；导出 frozen_test 还需操作者确认候选模型、提示与采样参数已锁定，且未确认时以门禁错误退出而不建目录。生产双工作流/模式提示编译及 split 策略测试通过 5/5；用 500 条仅含合成文本的记录完成端到端 CLI 冒烟，development 导出 400 条，frozen_test 在确认后导出 100 条，未确认路径被拒绝。该产物含完整提示内容，必须保存在本地受控目录。
- 基线采集链最初只有 `training/invoke-ollama-eval.ps1` 的通用提示词，不能代表产品工作流。`snapshot` 当前只生成实际生产首轮提示快照；完整执行进展见本节后续记录。
- 可复现执行 seam：`PolishWorkflowService` 与 `PromptOptimizationWorkflowService` 支持注入 `CompanionDriverMode`，默认仍读取原全局设置；`BlindWorkflowRequestFactory` 将 gold 的输入/上下文/约束转换成产品类型请求；`BlindWorkflowExecutor` 调用产品润色/提示词优化工作流及既有质量门禁。
- 本轮结束复验：阶段 0 盲评/不可变报告/主题回归/提示快照组合筛选通过 29/29；`tools/BlindEvaluationRunner` 构建通过（0 警告、0 错误），`git diff --check` 通过。
- 后续增加候选 profile 严格读取与安全门、候选随机 alias、封存在 reviewer 目录之外的身份映射、create-only 预测/摘要/request telemetry 产物，并新增 `BlindEvaluationRunner run` 命令。它复审完整 gold/train 重叠、显式选择 split，随后通过 `AIService` 调用一个配置候选，进入产品工作流/质量门禁；仅当同时指定 `--allow-cloud` 与 `--authorization-ref` 且 key 环境变量有效时才允许 Cloud profile。Local profile 限制为本机 loopback HTTP 的 Ollama/LM Studio，不走 fallback。candidate key 只从环境读取，profile 配置未知字段拒绝；Cloud API 地址 query/userinfo/fragment 均在读取 key 前拒绝。运行输出无模型身份，封存文件记录 alias 映射；候选输出触发 PII/密钥启发式时不写出文本。
- `AIService` 增加可选内容零遥测 observer；未传 observer 的应用调用不产生任何记录。observer 每次 HTTP 尝试只回调 request ID、时延、状态码、规范化状态及 token 计数；解析 OpenAI-compatible chat/responses 两种用量键、Anthropic 输入缓存和 Gemini UsageMetadata；响应体仍受现有 4 MiB 上限，不回调输入/输出文本，observer 异常不影响生成。Runner 为 cloud 汇总 API token 用量；外部本地服务汇总请求时延与返回 token 估算 tokens/s，模型加载和内存字段留空。ManagedLocal 当时尚未纳入 runner，已于本轮接入；相关实测边界见末尾记录。
- runner 暴露了真实模型运行入口但本轮没有运行任何 Provider；通过隔离 fake `ITextGenerationClient` 与 fake HTTP handler 验证配置门禁、telemetry、盲化文件、PII 输出剔除、取消覆盖和全产品工作流。请求 ID 在写入遥测前按长度与安全字符集筛除异常值；候选 profile 先经产品相同的 fail-closed endpoint policy 验证，避免无效端点生成误导性的全失败运行。候选/Provider/用量解析/执行器/快照组合测试筛选通过 34/34；正式全量测试本轮未运行。之前全量测试记录 966 项中 98 项失败仍需单独分类修复。
- 依据 2026-10-02 核对的官方 usage 协议文档，新增 OpenAI Responses/Chat Completions、Anthropic Messages、Gemini GenerateContent 的 token 字段解析。API 格式持续变化，具体在线候选运行前需复核官方版本和数据授权。阶段 0 仍未达到解锁门槛：授权外部盲评数据、可核验双评/裁定和同条件云/本地实测尚缺；阶段 1 生成行为开发保持未启动。
- 阶段 0 仍未达到解锁门槛：没有授权的外部盲评样本、可核验人工双评/裁定记录和云端/本地同条件产品基线；阶段 1 生成行为开发保持未启动。
- 最终清单流程自审发现：若人工评审直接修改 runner 原始 `predictions.jsonl`，会破坏运行时预测哈希，使合规评分无法完成。现明确保留原始预测文件，人工只编辑 `reviewed-predictions.jsonl`；finalizer 校验两个文件的样本 ID 和所有候选输出/Schema/运行遥测字段完全一致，仅允许评审与裁定字段变化，并从已评审副本重算评分报告。runner、解盲 finalizer 端到端的 fake-client 冒烟及相关盲评回归通过 44/44；未调用真实模型。阶段 0 授权与质量证据缺口不变。
- 又发现 scorer 虽支持左右顺序归一化，却缺少安全制作跨候选盲评包的产品命令。新增 `blind-pairwise-package` / `blind-pairwise-merge`：前者重跑 500 条来源/脱敏/双评/泄漏审计并要求 frozen_test 解锁确认，使用 CSPRNG 生成评审 ID 与左右顺序，公开包隐藏候选 alias、gold ID 和参考答案，封存映射绑定评审包哈希；后者验证答卷覆盖、双评审/第三方裁定并在评审目录外还原比较数据。`blind-evaluate` 现可重复接收 `--predictions` 汇总多个候选。统一了 JSON Schema 与读取器对 null 遥测/裁定字段的解释；多候选 pairwise CLI 使用 500 条合成用例端到端通过，盲评回归现为 49/49。它们仍只证明工具链行为；候选 cohort 的比较报告与单候选最终清单尚未统一为同一种清单结构，真实授权盲评门槛未通过。
- 补齐独立的 `finalize-cohort`：同一 cohort 中的所有候选都必须提供 `split=all` 运行摘要、匹配的封存身份映射、原始与评审预测；finalizer 检查数据/提示/预测哈希、候选 alias、完整样本覆盖和评审副本未修改生成内容，重算多候选评分与比较结果后，以 create-only 方式写出所有候选目录之外的 cohort 清单。进一步将匿名 pairwise 评审包、原始答卷和封存比较映射纳入清单核验；从匿名答卷重建 comparisons，逐项对齐评审随机 ID、gold ID、左右候选、比较记录和两侧成稿哈希，防止评分报告与被改写的比较文件自洽但不源自原始评审。该清单独立于单候选 manifest，不冒用某个模型的身份；它绑定 cohort report、comparisons、候选输出及身份映射摘要，但尚未提供数字签名。新增成功与篡改拒绝回归；阶段 0 仍因缺少授权真实盲评和真实云/本地基线而未通过。
- 为已有 3,000 条润色 Agent v2 合成集增加独立 `polish-agent-validate` 审计器，避免把 v1 通用 schema 错用于 v2。它验证 manifest 与 canonical/SFT/source 哈希、来源 ID 和字段映射、train/dev/test 模板族隔离、SFT 消息和目标对齐；现在也核对 category/scenario/channel/risk_level、澄清问题等来源字段。真实仓库数据审计结果为 3,000 条、2,250/375/375 切分、24 个模板族、3,000 条来源匹配、0 个问题；2,512 条 reviewer_count=1 元数据仅作为合成记录属性报告，`human_blind_review_evidence=false`。审计器 4 项定向测试通过。此结果仅证明合成资产血缘和结构完整，未补足 500 条授权外部盲评或真实人工双评证据，阶段 0 仍未通过。
- 按阶段 0 数据来源门槛调查公开中文润色/改写数据：RewritingBench 数据卡自称 CC-BY-4.0 且有 730 条人评改写，但 eval 只有 129 条、已公开，页面 schema 解析失败，其 EMNLP 2026 论文声明尚未从 ACL Anthology 独立确认；只能作为待审计的开发/校准切片候选。ToxiRewriteCN 数据卡声明 Apache-2.0、含 1,556 条中文毒性缓解标注，可考虑隔离为安全压力切片，不能代表通用润色。Qwen3.5-9B-Humanize 数据卡标记 CC-BY-NC-4.0，且内容来自 CSL 学术摘要并包含该模型训练链数据，不纳入本项目商用候选基线。调研无下载/导入数据，未发起 Provider 请求。逐项分析、研究限制和下一步数据构建顺序见 `docs/external-evaluation-data-research-2026-10-02.md`；盲评集、双评裁定和真实云/本地基线仍缺，阶段 0 未通过。
- 继续对公开 RewritingBench eval 文件作来源级统计：129 条都有 3 个 0–5 整数评分，均值与三人评分平均值一致；数据卡的 600 条 train + 129 条 eval 与 full 730 相差 1 条，`consensus_score` 与评分中位数有 11 条不同但上游未给裁定计算协议。已锁定 `e4de696`，eval SHA-256 为 `89a53c95acb9400ae3a544cbb39ee9dfee46de51caa37d27178c8f45c96ab696`；该文件在 ignored 临时目录审查，129 行/唯一输入，显式 QQ 联系标识规则命中 1 条，手机号/身份证/邮箱规则未命中。由于样本公开、数量不足、缺上下文/风险/事实锚点和逐样本来源证明，不导入项目评测集。新增回归先复现 QQ 群号漏检，再修复为检测显式 QQ 群/账号编号，并复用到盲评 gold 与成功候选输出。盲评相关筛选通过 54/54；未调用 Provider。公开数据研究与后续计划见 `docs/external-evaluation-data-research-2026-10-02.md`；阶段 0 仍缺 500 条授权样本、人工双评/裁定和云/本地同条件基线。
- 复核 `blind-validate` 覆盖性时发现其只要求两个任务及两个 split 各至少出现一次，缺少高风险/澄清/格式样本配额。新增红测证明 500 条全为常规输出、无需澄清、无格式要求也能通过；另一红测证明提示词优化只有 1 条仍能通过。现审计器要求两种任务与 development/frozen_test 各占至少 20%，高风险至少 10%，低/中风险常规至少 50%，需澄清与格式要求各至少 5%，事实/约束锚点至少 50%（按总量向上取整，维度可重叠）。评测 README 已说明这是项目预注册默认配额而非生产流量估计；调整必须同步协议/实现并重新冻结。`blind-validate` 现在在 JSON 中返回任务、split、风险、澄清、格式和事实/约束锚点的实际计数，新增 500 条已覆盖 fixture 对每个计数作精确断言。更新 pairwise CLI 的 500 条端到端 fixture 后，blind 过滤的 56 项测试全部通过，包含数据验证摘要、评审包生成/合并和 `blind-evaluate` 汇总链路；DatasetBuilder、应用、runner 与测试程序集一并构建成功。该门禁修正改善了阶段 0 数据覆盖质量，但不填补实际授权数据或人工评审缺口。
- 随后检查发现全量配额仍允许 `frozen_test` 不包含高风险、澄清或格式场景，只要 development 覆盖足够。已将任务、风险、常规、澄清、格式和事实/约束锚点配额逐 split 强制执行，并在覆盖摘要中分别输出 development/frozen_test 计数；回归夹具改为在 split 间分层分布，且对 CLI JSON 的切片计数作断言。合成回归用于验证门禁逻辑，不构成真实盲评数据证据；阶段 0 仍待授权数据、可核验人工复核与同条件云/本地基线。
- 依据 [Persona-Augmented Benchmarking（EMNLP 2025）](https://aclanthology.org/2025.emnlp-main.1155/) 关于输入风格/提示格式会改变评估结果的发现，现有盲评 schema 没有输入表达风格标签，无法诊断风格变化造成的失真。将盲评记录升级到 schema v2，新增五类主导输入风格；审计器要求每个 split 至少三类并输出类别计数。该三类门槛是防单一风格退化的项目协议假设，不是论文推导的统计充分性标准或真实流量分布。新增冻结集单风格拒绝回归；真实数据仍须人工按标注定义分类并双人复核，阶段 0 整体门槛不变。
- 将当前准入规则集中到 `BlindEvaluationAdmissionProtocol`：审计器直接使用同一组样本数、比例和输入风格定义，生成规范化 SHA-256；`blind-validate`/`blind-evaluate` 输出协议 ID、版本和规则哈希，当时的 run-manifest v2 与 cohort finalizer 验证并封存相同身份。旧版 run manifest 因无准入规则绑定而不能通过当前可比性验证；历史文件仍保留。新增回归确保 manifest 字段、报告内容和报告哈希三者的协议身份一致，盲评筛选现 61/61 通过；两个 schema/template JSON 解析和 `git diff --check` 通过。当前真实数据集仍不存在，因此版本绑定只验证工具链，不代表阶段 0 通过。
- 自审 gold 审计时发现 `human_review` 仅校验两名评审 ID 和 accepted/adjudicated 状态，无法核对独立标签是否一致及最终 gold 是否来自裁定。schema 与准入协议升级到 v3/3.0，逐样本保存两份完整标签；接受状态要求双方一致且匹配 gold，分歧状态要求不同第三方裁定且 gold 匹配裁定。审计器核对投票人、标签、分歧状态及最终标签，并扫描隐藏评审证据中的疑似 PII/密钥。新增 accepted 分歧拒绝、第三方裁定接受、accepted gold 不匹配拒绝、隐藏证据 PII、快照/匿名包不泄露评审证据回归；盲评筛选通过 65/65，Schema/template JSON 解析及 `git diff --check` 通过。该 JSON 只能证明记录一致，不能证明评审者真实身份或确实独立作答；准入还需受控归档独立原始提交、提交时间和身份核验。真实授权样本、该类人工原始证据及同条件云/本地基线仍缺，阶段 0 未通过。
- 信任边界复核发现，仅由 CLI 在运行前做准入校验不够：候选执行服务、pairwise 构包器、单候选/cohort 收口器和独立 manifest 校验器都可被直接调用，其中三个收口/执行入口曾只信任 `dataset_valid=true` 或根本不校验 gold。新增 `ValidateRecordIntegrity` 并接入这些边界，逐条重核字段/决策/来源元数据、PII、双评/裁定证据和数据内重复/跨 split；只把整体样本配额/覆盖问题留给完整准入报告。新增伪造准入布尔值时含手机号 gold 拒绝、缺失 v3 评审证据拒绝和执行前不写产物回归；盲评相关测试通过 74/74。独立 manifest 校验现在也解析并重检逐条 gold。它仍无法证明授权引用属实、评审身份或独立性，也不重算 train/dev 重叠；需结合受控原始评审存档及传入训练集的 `blind-validate` 报告。真实数据与同条件云/本地基线仍缺，阶段 0 未通过。
- 又发现 `blind-validate` 对 JSON 使用默认反序列化，会静默忽略 schema v3 不允许的未知字段，与 `blind-evaluate`/pairwise 读取器的严格字段白名单不一致。现统一改用严格 gold 读取器；新增注入未知 reviewer 字段的拒绝回归。最终盲评筛选 75/75 通过，schema/template JSON 解析通过，`git diff --check` 通过（仅有既存 LF/CRLF 转换提示）。这些回归仍不替代未取得的授权外部样本、独立原始人工评审和真实云/本地基线，阶段 0 未通过。
- 按下一阶段计划补充《盲评评测证据受控接收规程》和 `evidence-manifest.template.json`。规程明确区分工具可验证的记录完整性、须人工查验的授权范围、以及须由独立评测保管人核验的身份/提交独立性；模板默认 `template_not_verified`，身份映射与原始凭证只放受控证据库。该文档/模板建立了可执行接收约定，但目前没有真实数据、受控证据或签名验证器，不能提升准入状态；阶段 0 仍未通过。
- 自审 `blind-validate` 输出时发现顶层 `valid=true` 容易被下游误读为整个阶段门槛已完成。为保持兼容且明确边界，报告新增 `external_evidence` 字段，固定输出 `verified_by_tool=false`、授权与评审独立性 `not_verified`；新增 CLI 集成断言覆盖机器准入成功但外部证据状态未验证的情况。该输出描述工具能力，并不代替受控核验流程；阶段 0 仍需真实授权、独立评审原始证据和同条件基线。
- 按计划对历史 966 项全量测试失败做可复现分类，发现无 TRX 原始结果，先生成三次独立结果。默认 `%TEMP%` 运行在 235 项后被 Windows 路径访问拒绝触发 TestHost 崩溃（不是完整结果）；将 `TEMP/TMP/TMPDIR` 指向工作区 `out/test-temp` 后完整执行。最初为 1045 项中 936 通过、11 跳过、98 失败；其中 89 项归档 SQLite 失败根因是空配置的数据根在 `verifyWritable=true` 时直接返回成功，没有探测默认根可写性，导致无法进入既有临时数据根 fallback。先有回归失败，再修复 `Services/DataDirectoryPolicy.TryResolve`；fallback 定向测试 1/1 通过。修复后全量为 1045 项中 1020 通过、11 跳过、14 失败；盲评筛选 52/52 通过。剩余 14 项分为 6 项 LocalAppData 受限环境中的设置保存失败、4 项发布脚本/安装器契约分歧、3 项悬浮球几何期望冲突、1 项猫蝶 Curious 资源显式映射冲突，逐项证据和复验建议见 `docs/test-baseline-classification-2026-10-02.md`。未将后三类擅自归为陈旧，也未改动 AI 生成行为。阶段 0 仍未通过。
- 审查 `BlindEvaluationCohortFinalizer` 时发现，它核对了各候选自身提示快照与运行信息，却未确认 cohort 内候选实际使用同一提示束和采样设置。新增两条红测分别复现提示不一致、temperature 不一致仍被收口；随后 finalizer 增加 system/user/组合提示哈希及 temperature、top_p、max_tokens、inference_level 比对，不一致时拒绝生成同条件比较清单。定向 finalizer 测试 6/6、`FullyQualifiedName~BlindEvaluation` 筛选 54/54 通过。文档已说明不同提示/采样探索应拆分 cohort。该改动提高比较有效性，不补足阶段 0 的授权数据、人工原始证据或云/本地实测，未发起 Provider 请求、未改变生产生成行为。
- 为复核全量测试中 6 个 `SettingsViewModelTests` 保存失败，确认其都在 `TrySave` 返回 false 处失败；根因是这些保存测试未重定向 `ConfigService`，而当前执行环境不能写真实 LocalAppData。增加测试用可释放配置目录作用域，保存用例使用唯一临时目录，用完恢复原路径并清理文件；未改变生产代码或写入校验。该类测试 55/55 通过。随后完整测试 1047 项中 1028 通过、11 跳过、8 失败。余下 8 项为 4 个发布脚本/安装器约定、3 个悬浮球尺寸期望、1 个猫蝶 Curious 资源映射分歧；它们需要产品契约裁决，尚未擅自修改。更新证据见 `docs/test-baseline-classification-2026-10-02.md`。阶段 0 仍未通过。
- 为推进 P2 而不越过阶段门槛，新增 Provider 能力盘点与阶段 1 接口设计输入 `docs/provider-capability-inventory-2026-10-02.md`。核实两条产品工作流仍调用普通 `GenerateAsync`，结构化工作流只有离线 DatasetBuilder 测试引用；Anthropic/Gemini 请求没有 Schema 字段映射；OpenAI-compatible 的 Schema 发送没有按具体平台/模型能力分流；Responses API 仅发现最终文本解析分支，实际请求仍固定 `/chat/completions`。结合当前 OpenAI、Anthropic、Gemini、llama.cpp 官方文档，建议以独立 adapter/capability 声明实现，始终保留应用本地结构和语义校验。文档盘点未发起 Provider 请求或改变生成行为；阶段 0 的外部证据仍未到位。
- P2 兼容盘点补充配置迁移边界：当前 `ConfigService` 只显式迁移 v20→v21，其他旧版本按 obsolete 备份并回退默认设置；v0–v6 迁移测试明确跳过，与 2.0.0 有意重置历史 schema 的策略一致；`ProviderProfile.Clone()` 手动复制字段；平台推断 helper 未接入配置加载。能力解析应优先派生、不持久化；可选路由配置缺省保持 Manual；任何新增 profile 字段要覆盖 clone、normalize 和 v21 load-save-load 保留测试。细节与拟定回归要求见 Provider 能力盘点第 5 节。未修改配置 schema 或产品行为。
- 为补齐阶段 0 的本地候选执行入口，BlindEvaluationRunner 现在接受绑定已安装模型 installation ID 的 `ManagedLocal` profile，并通过产品 `LocalRuntimeManager` / `LocalTextGenerationClient` 执行；命令需显式传 `--runtime-root`，`--model-root` 可选且默认沿用产品模型目录。随后自审发现外层计时会把本地启动和推理混在一起，现改由产品 `LocalTextGenerationClient` 可选回调请求级、内容零遥测：本地服务器返回合法 usage 时记录 token 量和 HTTP 调用时延，计时从 `EnsureStartedAsync` 完成后开始。`LocalRuntimeManager` 另记录进程启动到健康检查 ready 的时长，以及 llama-server 的峰值 working set；前者是启动/加载代理，后者不含 Vulkan 显存。fake runtime host 与 fake HTTP handler 验证了 loopback、Schema、usage、运行时指标透传和不含内容的遥测；本地运行时/候选/工作流/盲评及清单筛选合计通过 79/79。CLI `--help` 可运行，`git diff --check` 通过（仅工作区既有 LF/CRLF 提示）。未启动真实模型、未访问 Provider、未改变产品请求或输出行为。当前工作区仍缺 `datasets/ai-evaluation/blind-eval.jsonl` 和 CPU/Vulkan `llama-server.exe`，因此没有真实运行时或目标硬件指标，阶段 0 仍缺授权外部盲评集、可核验人工双评/裁定和云/本地同条件实测。

## 2026-10-03 生命周期复核纠正

- testhost 子进程三周期复核重现了 WPF 资源包 `Environment.FailFast`，但三周期夹具通过反射销毁并重建同一 AppDomain 的 WPF `Application`，不符合官方单例生命周期契约。该诊断回归已从默认测试源码移除；保留在测试基线报告运行 N 中作为测试辅助设施风险证据。没有把它归因于产品单应用生命周期，也没有宣称它解释了既有 `HwndSubclass` 堆栈。
- 当前有效 Dispatcher teardown 用例以绝对工作区 TEMP/TMP 路径复跑，1/1 通过。首次构建尝试使用相对 TEMP，测试虽通过但 MSBuild 节点因把该路径解析到 SDK 目录后报权限错；改为绝对路径后 `dotnet test --no-build --no-restore` 干净通过。NuGet Audit 曾有缓存权限警告，未影响该定向运行。
- 偏差与修正：原先“把三周期失败固化为测试”的方向不合适，已撤销该测试要求并修正基线记录。若要修测试宿主，应先在进程内统一 WPF 单例与 Dispatcher，或隔离进程，不再通过反射清空 WPF 内部单例。此修正不改变阶段 0 数据准入条件，也未改变 API、本地模型、生成行为或模型权重。
- 下一步触发条件：在重构更大范围 WPF 测试 helper 前，先统计需要 Application 的测试与只需 STA 的测试，比较共享 STA 与进程隔离的迁移成本；AI 阶段 0 则仍需收到来源/授权可复核、双盲独立评审和冻结切分证据，才进入同条件云端/本地产品基线。当前可确认的质量收益为 0（没有生成系统改变）；WPF 方面仅获得“无效重建序列会在子进程中崩溃、有效单次 teardown 通过”的诊断收益。

## 2026-10-03 阶段 0 数据资产实盘盘点

- 核对 `datasets/` 当前文件清单及 manifest：`datasets/v1` 有 12,000 行合成 canonical/SFT（polish 与 prompt_optimize 各 6,000）；`datasets/polish-agent-v2` 有 3,000 行，由 v1 合成资产派生；`datasets/architecture-v1` 有 10,000/1,000/1,000 行 train/dev/test，manifest 的目标是 prompt architecture search，但没有本项目盲评所需逐条授权与双评原始证据。上述集合最多支持内部规则回归/流程冒烟，不能算入 500 条外部盲评门槛。
- 当前 `datasets/ai-evaluation` 只有 schema、规程和模板：没有 `blind-eval.jsonl`、实际 `source-register.json` 或证据清单；工作区也没有 `restricted/` 受控证据目录。`out/external-source-audit` 与 `out/test-artifacts` 当前未发现名称匹配盲评 gold、来源登记、evidence manifest、reviewed predictions 或 comparisons 的文件。此检查仅覆盖当前工作区路径，不能替代外部受控证据保管人核验。
- 本步输入是工作区文件清单、数据集 manifest 和准入规程；产出是来源/用途分类和下一步准入清单；验收结论为“没有合格冻结集”，不生成预测、不调用 Provider，也不调整 prompt/model/route/参数。阶段 0 的数据来源现状与外部研究结论一致。
- 后续子任务：A0.1 来源与样本准入（输入：项目自有或明确授权的脱敏样本、来源权利证据引用、来源负责人和独立复核角色；产出：至少 500 条覆盖 polish/prompt_optimize 的候选 JSONL、source register、独立双评/分歧裁定证据及语义族隔离 split；验收：人工核权、PII 复核、schema/准入审计全部通过且受控证据可追溯）。A0.2 冻结同条件基线（输入：A0.1 冻结哈希、已锁定模型/提示/参数/硬件、经单独批准的云预算或可用本地运行时；产出：不含内容遥测的 candidate run manifests、盲评结果和延迟/用量/内存报告；验收：冻结测试只能在候选配置锁定后解封，所有指标可复跑）。A0.1 是 A0.2 的前置；二者均通过才考虑阶段 1 生成契约行为接线。
- 方案取舍：合成资产可立即低成本验证脚本，但对真实使用质量的外部效度不足；公开已发布基准启动快但存在泄漏、许可链和任务偏差风险；优先推荐项目自有/明确授权脱敏样本并独立评审，公开专项集只作为隔离诊断切片且不替代冻结测试。至少 500 条样本意味着至少 1,000 份独立金标初评（有分歧再加裁定），评审人和核验人安排是尚未确认的项目输入。
- 下一步触发条件：数据来源负责人/项目经理提供合规样本或确认由谁受控提供，并安排两位独立评审、第三方裁定和独立证据复核；云候选比较另需模型/API 选择与预算批准。在这些输入到位前，继续只做不改变生成行为的数据/协议准备，不宣称任何 AI 质量收益。

## 2026-10-03 阶段 0 统计推断审计与修正

- 问题定义：冻结规则按 `semantic_family_id` 聚合同源/近似任务以避免 train/test 泄漏，但旧 pairwise 评分器却把每条样本的投票当独立 Bernoulli 观测。红测先固定“20 条全胜记录全部来自同一语义族，仍被报告为显著”的反例（旧实现实际 `Significant=true`）。NBER 对聚类推断的研究指出，忽略组内相关会使常规推断过度拒绝，少簇情况下甚至簇稳健方法也须谨慎，支持按实际独立单位而非行数推断：[Cameron, Gelbach & Miller](https://www.nber.org/papers/t0344)。
- 方案比较：简单按行 Wilson 不处理族内相关，应只作逐样本描述；簇 bootstrap 可保留样本加权，但对本项目“跨场景/任务族是否普遍受偏好”的主问题，需额外固定重采样单位、随机种子和样本权重规则。现选语义族多数票：每族先裁为左胜、右胜或族内平局，每个族等权；对非平局族级二元结果计算 Wilson 95% 区间。其代价是忽略族内胜负幅度并不模拟真实用户流量权重，因此只用于跨任务族的个性化增益判断；样本行胜率另作描述。语义族归并错误仍会破坏独立性假设，30 族下限不等同功效保证。
- 代码改动：`BlindEvaluationScorer` 的 pairwise 结果增加决定性独立语义族数、族级胜负/平局、族级胜率和族级 Wilson 区间；`Significant` 仅在至少 30 个决定性语义族且族级区间下界 >0.5 时成立。运行 manifest finalizer 不再对行级计数另算 Wilson；仅当存在单一明确对手时写入族级 preference/CI，多对手结果保留在完整 cohort scorer 报告而不混池。manifest 加入 nullable `pairwise_decisive_family_count`。
- 协议改动：保留 `annotation-handbook-v1.0.md` 草案，新建 v1.1 固化统计单位、多数票、平局、30 族门槛及解释范围；README 同步口径。冻结数据必须引用准确 handbook 版本和 hash，旧版报告不能与新报告混比。
- 验收：新回归先按旧代码失败，修复后 `BlindEvaluation` 全部相关筛选 **117/117 通过**（含 scoring、单候选/cohort finalizer、pairwise 包与 manifest validator）。`NU1900` 仍提示 NuGet 漏洞缓存路径权限问题，但本次 build/test 退出码 0；未发起云请求、未加载本地模型、未修改生成行为。
- 预期收益：统计含义可解释；对“单族 20 次全胜”从错误 `significant=true` 修正为 `false`；至少 30 个独立决定性语义族后，才可能报告偏好增益。模型质量变化仍为 0/未测。下一步触发条件是阶段 0 数据和评审证据到位；随后用冻结语义族分布做功效/敏感性复核，再决定是否调整 30 族的最低判定线。




## 阶段 0 评测方法补强：架构搜索器族级门禁（2026-10-03）

同一行独立性缺陷也存在于 prompt architecture 评估器：配对偏好原先以样本行作 Wilson 推断单位，候选晋级置信门槛也使用逐行下界。新增红测确认“同一个语义族 30 个相关变体全胜”会错误报告显著；另一个 120 行、单一家族的全通过候选曾被判为可晋级。修复后，架构比较沿用语义族多数结果和至少 30 个决定性独立族的偏好门槛；候选报告保留逐行率作描述指标，晋级置信门槛改用“族内所有样本均通过该指标”的家族完整通过率 Wilson 下界。族完整通过是严格的鲁棒性标准，可能低估同一族内部部分成功；需在真实冻结集上审阅家族定义与样本结构，不能解读为逐请求流量表现。

回归结果：30 个独立族全胜可达到显著门槛；30 行归于单一家族时仅计一个决定性家族、不能报告显著；120 行同属一个全通过家族时其 Wilson 下界约为 0.21，不能晋级。架构/盲评相关组合筛选通过 **128/128**。架构 v1 只有 12 个总家族、冻结 test 仅 2 个，因此本变更只修正统计口径和防止虚假晋级，不提供模型或提示质量证据，也不解除阶段 0 门槛。NuGet 漏洞数据缓存出现 `NU1900` 路径权限警告，本次构建与筛选退出码为 0。

### 评审身份规范化补充（2026-10-03）

阶段 0 评审链复核发现 pairwise 分歧裁定只用原始字符串比较裁定人和评审人 ID，而 reviewer 间去重已经先 Trim；这会允许同一身份仅因首尾空格不同而作为“第三人”裁定。新测试先在旧实现下失败，评分器现在将裁定人与两位评审人的 ID 统一去除首尾空格后作 ordinal 比较。盲评筛选现为 **118/118 通过**。这仅加强评审独立性校验，不改变统计指标或生成行为；下一步触发条件仍是取得合规冻结集与真实独立评审证据。

### 阶段 0 准入证据链可证明范围复核（2026-10-03）

本轮按目标检查授权/来源登记与受控证据清单的代码及命令出口，重点确认结构校验是否可能被误当作事实核验。实现会按数据字节哈希、逐样本来源/盲包/评审提交和裁定时间关系交叉验证，但明确将 `external_evidence_authenticity_verified_by_tool`、`rights_reviewed`、`reviewer_independence_verified` 与 `phase_0_gate_passed` 固定为 false；来源登记命令退出码 0 也只代表字段和引用结构完整。README 已写明该区分。本轮准入证据 manifest 与 source register 测试筛选 **34/34 通过**，相关文件 `git diff --check` 通过。实盘重查 `datasets/ai-evaluation/` 仍只有 schema、规程、README 和模板，没有真实 `blind-eval.jsonl`、source register 或已审证据 manifest，因此当前工具不能证明外部权利真实性或评审独立性，且阶段 0 仍未通过。无需修改代码；继续实现“自动认证”会超出本地工具能证明的边界。下一步需由受控数据保管/权利核验角色提供实际授权样本及独立评审证据，然后运行现有准入链。

## P2-C 配置兼容性基线补齐（2026-10-03）

- 目的：阶段 1/2 将新增 Provider capability 和路由设置时，先证明现行 Windows 用户配置可不丢 profile、活动模型、映射、采样参数或密钥槽引用。源码核对确认 `ConfigVersion=21`，且只有 v20 有受支持的显式迁移。
- 改动：`Huaxiazi.Tests/ConfigServiceTests.cs` 新增固定 v21 JSON 磁盘输入。含 OpenAI/Anthropic 两个不同 Platform/Protocol/API Base、非首个活动 profile、model mapping、temperature/top_p/maxTokens/timeout/inferenceLevel 和有效 `provider-{id}` SecretId。直接 Load 与 Load→Save→Load 均断言 profile 顺序/数量、活动 profile、关键 Provider 字段和映射/参数保持，且不生成 `.v21.ignored`。没有配置 schema 字段变化，不提升版本号。
- 纠错：首个 fixture 版本把嵌套 `ProviderProfile` 属性写成 camelCase，实测加载后回落默认 profile。对照现有 wire contract 后确认 AppSettings 外层字段显式 camelCase，但 ProviderProfile 使用默认 PascalCase；修正 fixture，而不修改生产加载逻辑。修正后定向 fixture **1/1 通过**，`ConfigServiceTests` **14 通过、11 个既有旧 schema/明文密钥历史用例跳过、0 失败**。
- 边界：这证明当前 v21 数据读写路径对所覆盖字段的兼容性，不证明未来字段的 Clone/Normalize、安全缺省或 v21→v22 迁移；这些仍须随实现增补。完整测试仍有既有 WPF 宿主/产品契约问题，不能由本定向筛选代替。AI 质量收益未测，生成行为未更改。
- 下一步：此项关闭了 P2-C 中“缺固定 v21 文件 Load 证据”子项。阶段 0 仍需 ≥500 条获授权脱敏冻结数据、独立评审/裁定受控证据以及同条件云/本地基线；取得后才开始阶段 1 生成契约行为接线。新的 config fields 则依 P2-C 规则在其实现提交中补 schema 默认、Clone/Normalize 和迁移验证。

## P2 Provider 官方能力资料复核（2026-10-03）

- 按官方当前模型/API 文档更新 Provider 能力盘点，未做生产调用和请求行为改动。GPT-6 Astra 不支持自定义 temperature/top_p，Chat Completions 仍列为可用 endpoint；其可用 reasoning effort 不含 none，max_completion_tokens 计入 visible 与 reasoning tokens。修正先前把省略采样字段绑定在 reasoning effort 非 none 的过窄假设。
- Anthropic Messages 当前页面列出 Opus 4.7/4.8；规则以 Opus 4.6 之后发布的模型为界：temperature 仅 1.0 是兼容接受值，top_p 仅 >=0.99 是兼容接受值但字段已弃用。阶段 1 测试需区分兼容接收和用户采样值实际生效。
- Gemini REST structured output 当前示例使用 `generationConfig.responseFormat.text`，旧 `responseSchema` / `_responseJsonSchema` 标为 deprecated；Schema 是 OpenAPI/JSON Schema 子集，不支持属性可能被忽略，复杂 Schema 可能被拒绝。实施应按固定 API/model 版本测试新字段形状与本地语义校验。
- llama.cpp 上游文档声明非完整 OpenAI 兼容；`reasoning_effort` 传给 Jinja chat template，是否生效依赖模板。上游 master 页面不能替代将交付 build/model/template 的测试。
- 资料和精确边界见 `docs/provider-capability-inventory-2026-10-02.md` 中 2026-10-03 小节，含 OpenAI、Anthropic、Google、llama.cpp 官方链接。上一日表格仅作历史盘点，发生冲突时以本节日期复核为准。此步产出是阶段 1 的参数和 API mapping 验收输入；未实测服务行为，也未证明中文润色质量提升。当前可量化收益是消除 capability 设计中的 2 项错误绑定/旧字段假设，API 请求数和模型质量变化均为 0。

## P2 测试可靠性分类：隔离基线工具与最新结果（2026-10-03）

- 新增逐类隔离脚本 `tools/test-isolation/run-isolated-tests.ps1` 和 TRX/过滤工具 `tools/test-isolation/TestIsolation.psm1`，当前 `dotnet test --list-tests` 自动发现 103 类；每类独立 testhost，保存独立日志、TRX 和 TSV 汇总。新增 helper 测试 5/5 通过。
- 真实完整隔离运行：1,132 总项，1,113 通过、8 失败、11 跳过、0 aborted；所有 103 个类均正常结束。4 个失败类别是发布静态契约 4 项（AcceptanceDefect 1、ReleaseSecurity 3）、悬浮球尺寸 3 项和猫蝶表情资产映射 1 项。失败没有被改成成功，也没有改变产品行为。
- 第一次运行发现 VSTest `Counters.notExecuted` 为 0，但 11 个历史跳过用例有逐条 `outcome="NotExecuted"` 记录；已加入红测并改为同时读取逐项结果与 counters，修正后整套隔离复跑与原始 TRX 交叉一致。证据在忽略目录 `out/test-artifacts/class-isolated-20261003-runner-final/`，不进版本库。
- 执行示例：`& 'tools/test-isolation/run-isolated-tests.ps1' -NoRestore -OutputDirectory 'out/test-artifacts/<unique-run-name>'`；`-ClassName 'Huaxiazi.Tests.AIServiceTests'` 用于局部隔离，`-ListOnly` 列类别。完整运行耗时约 2.5 分钟。脚本通过逐类进程隔离避免 FailFast 中止整组结果，但常规单宿主全量 FailFast 根因仍未知；隔离通过不能代替常规全量通过，也不能证明产品质量。
- P2 结论：环境/产品失败的逐类分类已有稳定执行手段；后续先按正式产品约定裁决上述 8 项，并保留普通宿主 crash 为独立未决项。P0 仍未通过：≥500 条授权盲评样本、独立原始评审/裁定受控证据及冻结条件云/本地产品基线缺失；不进入 P3 生成契约实现或任何 AI 行为改动。

## A0.1 外部输入核验（2026-10-03）

重新枚举 `datasets/ai-evaluation/` 后，确认只有协议、Schema、README 和模板；无冻结样本、已填 source register/evidence manifest、原始双评/裁定提交或准入报告。为把依赖转成可执行的人工作业，已在[受控证据接收规程](../blind-evaluation-evidence-intake.md#2026-10-03-项目经理交接卡)新增项目经理交接卡，列出缺失输入、责任角色、交付验收与停止点。未造数据或自填 `verified`，不改变阶段 0 未通过判断。下一步须由项目侧落实数据来源与受控角色；云端基线需另行明确预算和数据出站授权。

## 测试可靠性进展：WPF teardown 与全量隔离复验（2026-10-03）

- 修正 `WpfViewSmokeTests` 的 21 个 STA 线程 finally：完整关闭其拥有的 WPF Application/窗口/Dispatcher 并清理测试宿主静态状态。此前 `MainWindow_UiScaleResizesTheViewportWithItsContent` 双数据用例只通过第一例即发生 `HwndSubclass.SubclassWndProc` FailFast；修复后该理论 2/2、整类 23/23，均 0 aborted。`SettingsViewModelTests` 55/55，并已连续三次复跑稳定。
- 修复逐类隔离 runner 在失败测试向 stderr 输出 `[FAIL]` 时被 PowerShell `ErrorActionPreference=Stop` 提前终止的问题。含真实失败的类现在仍能完整汇总，并以非零退出码提示失败。
- 全量隔离复跑：103 类，1,133 项，1,118 通过、4 失败、11 跳过、0 中止。剩余失败仅为发布/安装实际交付契约待裁决的 4 项；上一轮悬浮球 +16 DIP 断言和猫蝶 Curious 素材映射的 4 项已按源码现有外壳/资产定义修正并定向复验通过。详细命名和工件位置见 [测试基线 Run P](../test-baseline-classification-2026-10-02.md#运行-pwpf-sta-teardown-修复后的全量隔离基线-2026-10-03)。
- 此进展仅提升测试稳定性和视觉契约一致性，不提供 AI 质量证据。阶段 0 仍未通过：授权外部盲评集、独立评审/裁定证据及冻结同条件云/本地基线缺失；Provider、生成契约、模型路由与权重仍未按计划推进。

### 普通全量宿主复验

修复后执行常规单 testhost `dotnet test --no-restore`：**1,118 通过、4 失败、11 跳过、0 中止**，耗时约 1 分 31 秒。WPF `HwndSubclass` FailFast 本次没有重现；4 个失败均与逐类运行一致，属于发布/安装交付契约静态断言。见 [测试基线 Run Q](../test-baseline-classification-2026-10-02.md#运行-q普通单-testhost-全量回归复核-2026-10-03)。该回归范围证明当前构建/runtime 下单宿主可跑完，不等于测试全绿或 Windows 发布验收完成。

### A0.1 覆盖矩阵可观察性补强

问题是现有协议的总量及 split 级门槛可能接受“某任务的某个 split 完全没有高风险案例”的数据。先新增反例单测并确认旧报告缺少 `task_split_coverage` 字段；实现后 `BlindEvaluationAuditor.SummarizeCoverage` 在 `blind-validate` JSON 内报告固定的四个 task × split cell，每格给出样本数、高风险/常规、澄清、格式、事实/约束锚点及全部五种输入表达风格计数（空类别为 0）。通过一组符合当前 v3 gate 的 500 条合成回归记录验证：prompt_optimize/development 有 160 条且高风险为 0，报告明确暴露该缺口，而原 `blind-validate` 仍按现行协议给出通过。这是只读诊断，不提升/改变准入门槛；风险分布和产品场景是否需要 task × split 最低配额，仍待数据保管人和产品负责人批准。随后增加 `product_slice_counts`：polish 报告五个固定场景及“未指定或自定义”，prompt_optimize 报告六个产品类别及同一固定兜底桶；缺失 prompt 类别按产品工作流默认值计入“通用任务”，未知/非字符串 context 不回显。它仍只是诊断，schema 和 validator 门槛不变。新增回归逐个对照六种 `PromptCategory` 的 enum/中文别名与 `BlindWorkflowRequestFactory` 的实际解析结果，避免评测切片和真实工作流分类漂移。本轮追加的 `context` PII 回归发现默认 JSON 序列化会将中文标签转义，导致“微信号：账号”检测漏报；审计改为递归扫描 JSON 字符串、属性名和数值原文，保留上下文字段间的 key/value 关系且不回显值。沿同一根因复核 `HumanReview` 后，再发现 reviewer ID 中的中文微信标签也会因 JSON 转义而漏报；现在数据准入和独立评审证据检查均扫描解码后的 review 值。手机号、邮箱、API key、中文微信标签及嵌套对象回归通过。覆盖矩阵、blind audit、配对包/CLI、来源登记和证据清单五组筛选 **72/72 通过**。阶段 0 样本和受控角色仍缺，生成行为保持冻结。

### A0.2 前置：评分报告按产品切片输出（2026-10-03）

- **问题/约束：** 候选级整体均值会掩盖具体场景或提示词类别上的退化；但低样本切片本身不能支撑显著性或晋级结论。输出须与实际产品 `context` 分类一致，失败和缺失预测不能从分母中消失，不能改变阶段门槛，也不能写入内容遥测。
- **输入：** 已有 `BlindEvaluationRecord` 的 task/split/context、候选逐条人工评分与状态；候选运行和用户内容均不新增读取。
- **产出：** `BlindEvaluationScoreReport.ProductSliceReports`，按 candidate × task × split × 固定场景/类别汇总覆盖、Schema、事实/约束、直接可用、语气、澄清、安全、高风险反转和评分维度均值；分别计数 failed、missing 与 invalid/duplicate。共享 `BlindEvaluationAuditor.ClassifyProductSlice`，未知值走固定兜底桶。
- **决策依据：** 相较为样本 schema 新增必填场景字段，复用已有白名单 `context` 映射可立即形成可用诊断且不迫使未授权数据重构；为避免过度解释，切片不计算独立显著性/置信门槛，质量率以该组全部 gold 样本作分母，样本数显式呈现。额外处理为每个 candidate 做一次分组计数，复杂度 O(candidate 数 × gold 样本数)，暂无基线测量其运行时开销。
- **验收：** 红测确认原评分报告没有产品切片字段；新增多任务/双 split、场景、API 错误、缺失预测和重复预测用例。score report 变更暴露两份 finalizer 测试夹具在更改 gold split 后复用了陈旧评分；修正夹具使之随 gold 重算，同时保留生产 finalizer 对评分不匹配的拒绝行为。评分器、单候选/cohort finalizer、manifest validator、配对包、覆盖矩阵及准入相关回归筛选 **114/114 通过**。
- **上下游：** 这是 A0.2 的评测工具准备，不代表已运行基线。后续须在 A0.1 通过并收到授权冻结数据后，使用该报告解释场景差异，再以独立语义族成对比较作增益判断；切片样本不足时只报告描述数值。阶段 0 外部样本、评审/核验角色和基线运行授权仍未齐备，因此 A0.2 与阶段 1 均未解锁，生成链路未改。
- 随后补充 `blind-evaluate` CLI JSON 端到端回归，确认 snake_case 的 `product_slice_reports` 在实际命令输出中可见，而 1 条合成夹具仍因数据配额不足按预期返回失败退出码。含 CLI、评分器、manifest finalizer/cohort、validator、配对包、覆盖矩阵和准入审计的扩大筛选 **115/115 通过**；该结果只验证评测/收口工具契约，不构成模型质量证据。

### A0.2 场景级性能与用量诊断补充（2026-10-03）

- **问题：** 只有全候选 latency/token/local 汇总，无法判断哪个产品场景带来质量与性能取舍；但小场景不能用来做晋级或显著性结论。
- **改动：** `product_slice_reports` 增加遥测样本数、延迟 p50/p95、API 输入/输出与缓存 token、本地 tokens/s、峰值内存、模型加载时间及规范错误类别。计数和估计值并列输出；性能百分位只针对具备该字段的实际尝试，token 总量只在该切片所有实际尝试都提供字段时输出，否则为 `null`。不改变预测输入 schema、准入门槛、模型行为或内容采集。
- **决策依据：** 相较新增遥测上传或改候选执行器，在现有离线候选记录中进行确定性聚合，对隐私和基线冻结风险最低；观测计数让部分/缺失指标可见，完整性规则避免用部分 token 冒充成本总量。分位数仍只作描述指标，必须按产品场景、硬件和观测数审阅。
- **验证：** `BlindEvaluationScorerTests` 新增缺字段回归，RED 阶段因报告未暴露切片遥测属性而按预期编译失败；实现后该类 **15/15 通过**。之后含 scorer CLI、评分器、单候选/cohort finalizer、manifest finalizer/validator、证据清单、配对包、覆盖矩阵和审计的相关组合筛选 **116/116 通过**，覆盖混合成功/失败请求、p50/p95、部分 token 置空、完整切片 snake_case 序列化、本地指标与规范错误计数。尚未运行整个测试集；旧的未封存评分报告需基于原始冻结输入重算后再 finalizer，不修改封存历史。没有质量数据，实际 AI 质量/成本/延迟收益不可量化。
- **上下游与阶段门槛：** 此项加强 A0.2 诊断能力，不能替代 A0.1 授权样本、人工评审和来源证据，也不能证明基线优劣；阶段 0 仍未通过，阶段 1 生成契约仍锁定。下一步维持生成行为冻结，等待合规冻结集、受控评审证据以及明确云端数据出站授权/预算到位，再运行产品基线并判断是否解锁阶段 1。

### A0 配对偏好门槛的前瞻功效敏感性（2026-10-03）

- **问题：** 当前“至少 30 个决定性族且 95% Wilson 下界 > 0.5”是否能识别产品上值得投入的中等个性化增益，不能只凭“30”这个最低数判断。
- **方法与假设：** 逐字采用 `FamilyPreferenceStatisticsCalculator` 的 Wilson 公式与 `z=1.96`，对 n=30/60/100/125/200/250 逐个求最小达标胜数，再按二项分布求 p=0.50/0.55/0.60/0.65/0.70 下的精确尾概率。单位是独立决定性语义族，族内多数胜负已压成一票，平局族排除。用两种独立计算路径（一种从 Binomial( n,p ) 的 k=0 概率递推，一种从门槛胜数的 log-combination pmf 求和）复核关键数值。
- **结论：** n=30 时必须 30 胜，真实 p=0.60 的检出率仅 0.000022%；n=125 时约 61.0%；n=200 时约 82.6%；n=250 时约 89.0%。这支持原方案将 200 个独立决定性族作为 frozen_test 目标，而不支持把 30 解释成“统计功效足够”。对 55% 的小效应，即使 250 族也只有约 35.2% 检出率。
- **多重比较审查：** 上述误报率是单一预注册候选对的点位数值；若一次审阅多组候选对，逐项 95% Wilson 区间不提供整体家族错误率控制。建议冻结前指定一个主要对照，其余作为探索性结果，或经评测负责人批准后为多比较确定调整法；未擅自变更当前代码/准入门槛。
- **产出与验证：** 计算表、定义、解释与取舍已写入 `docs/phase0-evaluation-sample-plan.md` 的统计解释部分；双路径计算得到一致结果。此为方法设计敏感性，不使用真实样本，不可代替收到冻结数据后基于有效 family 分布、真实 tie/分歧率及产品最小有意义效果所做的最终功效复核。质量/成本/延迟实测收益为 0/未测。
- **上下游：** 阶段 0 仍缺授权冻结集及受控评审证据，因此模型评测、实际效应估计及阶段 1 解锁仍不满足条件。数据到位后先核实 family 结构/多任务切片，再以产品负责人确认的最小有意义偏好差异评估实际功效；若 500 行无法提供所需独立族数，应增加独立样本或将结论限定为探索性，而不是降低证据标准。

### A0 候选运行的硬件档位绑定

- **问题与约束：** 本地 tokens/s、内存及加载时间必须能按硬件档解释；此前硬件档只在最终清单生成时手工填写，无法证明候选 A/B 的运行记录与该标签一致。与此同时，自动读取完整设备信息会增加隐私暴露，云 Provider 的推理主机硬件本来也不可见。
- **方案取舍：** 推荐在 run 前要求操作员声明一个短且稳定的非识别性档位标签；不自动采集序列号、主机名、用户名或系统硬件指纹。runner 将标签写入 reviewer 外的密封 alias map 与 run-info，收口时要求它们和最终清单元数据完全一致，并要求 cohort 的每个候选同档。该做法防止意外错标，不声称能证明操作者声明真实；ManagedLocal 档位指执行推理的本机档，Cloud 档位仅指候选运行客户端，不表示 Provider 服务端硬件。
- **改动：** `BlindEvaluationRunner run` 新增必需参数 `--hardware-profile <non-identifying-tier-label>`，只接受 1–64 位 ASCII 字母/数字/点/下划线/短横线的标识；失败时在创建文件或调用 Provider 前拒绝。finalize 与 finalize-cohort 复核 run-info、sealed map、cohort/最终清单档位一致。`datasets/ai-evaluation/README.md` 和 CLI help 更新使用说明。
- **验证：** 先写缺失档位拒绝、封存映射/运行摘要记录、单候选错档拒绝、cohort 混档拒绝回归；首轮按预期因新参数尚未存在而编译失败。修复测试夹具中 metadata 与硬件档位不一致及更早错误优先级问题后，候选运行、单候选 finalizer、cohort finalizer 三类筛选 **38/38 通过**，含成功路径、混档拒绝、冻结门禁、隐私与旧产物不覆盖验证。尚未跑全量回归或真实硬件/运行时测量。
- **预期收益与限制：** 新评测 cohort 内候选误用不同声明档位的结构性风险从“工具不检查”变为“收口拒绝”；没有真实设备/模型运行，不能量化 tokens/s、延迟或质量收益。label 仍是操作员自报，非签名硬件证明；host 转发/虚拟化/驱动状况亦不会被该字段验证。
- **上下游：** 该更改仍处阶段 0 工具链，不解锁质量基线或阶段 1。未封存旧 candidate run 缺该字段，须用锁定条件重新 run 后才能新 finalizer；封存历史不改。下一步要在 `--hardware-profile` 中采用项目批准的分档命名，并在授权冻结数据、评审证据及云端出站授权/预算到位后，做相同档位的 local/cloud 基线运行。

硬件分层边界复核：曾考虑放宽 cohort 的同档要求以便多硬件同批出报告；检查 `BlindEvaluationCohortFinalizer` 后撤回该方向。cohort 的任务是成对比较候选，同档是控制模型比较混淆因素的必要条件；跨档候选会把硬件与模型变化混在一起。当前单候选 finalizer 可逐运行记录 `hardware_profile` 和性能数据，故性能分层通过每档分别收口完成；模型偏好通过每档各自的同档 cohort 完成。它不要求跨档偏好推断。`BlindEvaluationCohortFinalizerTests` 已覆盖异档候选拒绝；上一轮 runner/finalizer/scorer 等相关筛选 142/142 通过。该策略没有放宽同档门禁，也没有引入硬件探测；标签仍是操作员声明。

### A0 运行清单硬件档来源标记（2026-10-03）

- **发现：** runner 已将硬件档位写入封存映射和运行摘要，并在收口时进行一致性检查；但最终清单仅有 `hardware_profile`，离开操作上下文后无法辨认这是操作员声明还是系统验证结果。
- **实现：** 单候选与 cohort finalizer 都在 `execution.hardware_profile_source` 写入固定值 `operator_declared_unverified`。validator 对该字段若存在则只接受该值，避免误标为系统验证；历史 v3 清单可省略该扩展字段以保持读取兼容。模板与 README 已说明语义。
- **验证：** 先增加单候选/cohort manifest 字段断言，首次筛选 2/2 按预期失败（字段缺失）；实现后连同 validator 未知来源拒绝及两个 finalizer 测试类筛选 **46/46 通过**。
- **限制与阶段状态：** 这是来源透明度与可审计性改进，不验证真实硬件，也不改变数据准入或模型质量门槛。阶段 0 仍未通过；授权盲评集、独立评审证据、受控基线与出站授权/预算仍待完成。阶段 1 生成行为保持冻结。

### A0.1 评审一致度的无内容诊断

- **问题：** 准入器能判断两份初始标注是否逐字段相等、需不需要裁定，但 `blind-validate` 过去不呈现整体/字段层的一致度；标注手册校准时难以定位分歧集中在哪些维度。
- **改动：** 新增 `BlindEvaluationAuditor.SummarizeReviewerAgreement`，按 task、input_style、constraints、risk_level、expected_decision、reference_output 与五类 annotations 报告可比较数量、精确相同数和比例，并报告全标签精确相同率。`blind-validate` JSON 增加 `reviewer_agreement`。输出仅是聚合计数，不包含输入、标签文本或评审身份；不改变准入结果、样本规则或生成行为。
- **指标边界：** 这是当前 gold-equality 规范下的精确一致描述值，不作机会校正，不等于 Cohen kappa/Krippendorff alpha，也无法判断自由文本同义、量表歧义或评审身份独立；只有两个 reviewer ID 非空、互不相同且与 `reviewer_ids` 对应的双份结构化标签记录进入可比较分母；这只是引用结构检查，不认证真实身份。保持描述性，未设合格阈值，避免未经评审负责人批准把新统计量变成阶段门槛。
- **验证：** 先写“字段分歧应反映在比例、输出不含内容”回归，旧实现因方法不存在编译失败；实现后审计类及 500 条 CLI/双盲包集成路径筛选 **29/29 通过**。补入结构身份引用校验后再次执行同筛选，仍为 29/29。集成测试确认 500 条完全一致合成标签显示 500/500，且 stdout 不回显合成输入正文。
- **阶段与下游：** 该指标可在真实 30 条校准集上帮助标注团队定位分歧，但合成集上的 100% 不证明人工信度，也没有带来模型质量收益。阶段 0 仍缺获授权冻结数据和受控独立评审；收到校准样本后先查看逐字段分歧，再由数据/评测负责人决定是否改手册并重新校准，之后才冻结全量标注与测试集。

### A0.1 评审一致度的机会校正与类别边际（研究复核，2026-10-03）

> **方法已被后续审计取代：** 本节记录当时实现的 Cohen’s κ；发现 `reviewer_ref` 按“样本 × 评审”一次性生成，故跨记录按提交位置汇总 κ 的固定评审员假设不成立。当前结果以本文末尾的名义 Krippendorff’s α 修正为准。

- **问题与输入：** 上一项只报告逐字段 raw agreement。类别严重偏斜时，raw agreement 可能高估可解释的一致程度；但 Cohen's kappa 也会因边际分布产生悖论，不能单独作“质量分”。输入仍是盲评记录中两位结构上不同 reviewer ID 的原始独立标签，不使用模型输出或用户正文。
- **研究依据与取舍：** Artstein & Poesio (Computational Linguistics, 2008) 系统梳理 NLP 标注一致性系数及其假设；Feinstein & Cicchetti (1990) 展示高观察一致率与低 kappa 可并存。因项目目前只有两个评审且字段尺度混合，推荐并列呈现 raw agreement 与 κ/边际，而非选单一总指标。没有未经批准地采用统一 κ 阈值、将风险等级线性加权、或把自由文本强行变成名义类别。
- **实现：** `task`、`input_style`、`expected_decision`、`clarification_required` 增加无权重 Cohen's κ 及双方固定白名单类别计数；不认识的值只落入 `other_or_invalid`，不回显文本。`risk_level` 是序数变量，但等级间距离未获项目批准，故只报 raw agreement 与 `low/medium/high/other_or_invalid` 边际；自由文本和多值字段只报按现行 gold 相等规则计算的 raw agreement。期望一致率为 1 或无样本时 κ 为 null；新增 `cohen_kappa_status` 明示该情况、样本缺失或尺度不适用。README 与 A0.1b 任务表已记录解释边界。
- **验证：** 新增反例：10 条中评审 A 全部为 produce、评审 B 有 9 条 produce/1 条 clarify，raw agreement 为 90%，κ 为 0；证明类别边际与 chance correction 字段能揭示 raw rate 未表达的分布问题。此前一致率测试、身份引用过滤、500 条 CLI 集成一并筛选 **30/30 通过**；stdout 仍不包含 gold 输入。
- **收益/限制/下游：** 指标覆盖 4 个有限名义字段，风险/自由文本留给原始比例和人工案例审阅；此处只验证计算和报告，无校准数据，所以实际 κ、误差区间、模型质量收益仍未测。拿到 30 条授权 development 校准样本后，由标注负责人对照边际、原始分歧案例、raw agreement 与 κ 修订 handbook；κ 不设默认通过线，校准完成并批准后才冻结正式标注。阶段 0 与阶段 1 状态不变。

### A0.1 二元“是否需要澄清”分类型一致率补充（2026-10-03）

> **指标组合已更新：** 下文历史验证使用 Cohen’s κ；正/负类特定一致率公式仍适用，但当前机会校正指标替换为名义 Krippendorff’s α。

- **自查发现：** 前一版工具 README/状态说明与实现短暂不一致：总摘要已加 Cohen's κ，但 XML 注释仍写“不作机会校正”。本轮改正注释；又按 Feinstein & Cicchetti (1990) 的后续研究核实，二元评审除了 κ，还应分别检查正类与负类一致率，单一总系数可能掩盖类别表现不均。
- **方案与公式：** 仅对 `clarification_required` 二元字段实现正/负类特定一致率；若 `a`=双方都判需澄清、`d`=双方都判无需澄清、`b+c`=分歧数，则 positive=`2a/(2a+b+c)`、negative=`2d/(2d+b+c)`。正类映射为需澄清，负类映射为无需澄清。若该类双方均未出现、分母为 0，该类指标为 null；没有引入假设值。其他名义类别仍报告 raw、边际和 κ，序数/自由文本保持既定策略。
- **验证：** 新增三例列联表测试，双方需澄清 1 例、无需澄清 1 例、单边分歧 1 例，正负类特定一致率均为 2/3；500 条 CLI 集成同时确认合成全一致样本两个类别指标序列化为 1。盲评审计与 CLI 集成筛选 **31/31 通过**，stdout 未泄露输入。
- **阶段影响：** 此为评审诊断，不更改 A0.1 门槛、标注金标、模型请求或产品生成行为。真实校准值仍未测。当前继续等待获授权 30 条 development 校准输入及评审安排；拿到后结合类别边际、raw/κ、正负类一致率和具体分歧样本审阅，不用任何单个系数自动准入。

### A0.1b 一次性评审引用下的机会校正方法修正（2026-10-03）

- **计划节点与问题：** 仍处阶段 0 评测基础设施。`blind-evaluation-evidence-intake.md` 为每条样本/每名评审生成不同的一次性引用；因此旧实现把所有第一份提交当评审 A、第二份当评审 B 来算 Cohen’s κ，违反固定评审对假设。旧 κ 及按提交侧边际均不再作为有效诊断值；单条记录内 raw agreement 与对称的正/负类特定一致率不依赖跨记录身份，可保留。
- **第一性原理与选择：** 要回答的问题是多个单位上两份名义评分的超机会一致性，同时评审身份可能轮换。Krippendorff’s α 的名义距离将异类记作 1，并支持评审数量/单位上的不平衡与缺失（Hughes, *The R Journal*, 2021）；当前每条可比较记录正好有两票，故实现简化为 `α = 1 - Do/De`，`Do` 是单位内分歧比例，`De = n/(n-1) × (1 - Σ(n_c/n)²)`，其中 `n=2N`、`n_c` 汇总两侧全部标签。相较保留 κ 的路径，此算法不需要假造跨记录的评审身份；代价是它仍只适用于名义字段，不能替代人工裁定或给出置信区间。风险字段没有批准距离权重，因此不算 α。
- **改动：** `BlindEvaluationAgreementFieldSummary` 改为 `krippendorff_alpha_nominal`、`chance_corrected_status` 与两票合并的 `pooled_category_counts`；`task`、`input_style`、`expected_decision`、`clarification_required` 计算 α。未知值仍归固定 `other_or_invalid`；自由文本、多值与风险字段保持不套名义系数；澄清字段正负类特定一致率保持原样。状态区分 `computed`、`no_comparable_records`、`undefined_perfect_expected_agreement` 与 `not_applicable_scale`，全体投票同类时 α 为 null。
- **验证：** 测试给出两个独立手算点：类别边际 19:1 且 10 条中 1 条分歧，raw agreement 为 90%、Do=De，因此 α=0；10 个样本的两评审引用全部轮换、样本类别有变化且每条双票相同，α=1。500 条合成 CLI 集成检查 α、状态、两侧合并计数（1000 票）与澄清正/负类率，且 stdout 不回显输入。修正后定向审计/集成测试 **32/32 通过**；没有使用外部真实标注数据。
- **自查、限制与下游：** 初次验证暴露浮点误差（理论 0 得到约 `7.8e-16`）及测试数据全体同类令 `De=0` 的不可定义情况；断言现用容差，轮换引用测试加入两个类别，生产算法对零期望分歧明确返回 null。名义 α 是评审一致性描述，不是模型质量、样本准入或生成质量提升；未设通用阈值/置信区间。阶段 0 仍未通过，尚缺授权的 30 条校准样本、受控双人评审证据、盲评集及受控基线所需出站批准/预算。下一步触发条件是这些证据按 intake manifest 完整登记后，运行 `blind-validate` 并由评测负责人查看 α、类别分布、raw/p/n 特定率和逐项分歧；阶段 1 生成变更仍冻结。

### A0.2 runner 将工作流质量拒绝误记为成功的修正（2026-10-03）

- **计划节点与问题：** 仍处阶段 0 候选运行到评分的基线链路。`BlindWorkflowExecutor` 能返回 `invalid` 或 `blocked`，但 runner 旧映射只用其决定 `schema_valid`，随后无条件将非敏感输出记作 `status=success`。空输出会进入成功预测集，被 scorer 报 `empty-output`，而失败请求数与覆盖率无法按“工作流拒绝”正确归类。
- **TDD 证据：** 新增真实产品工作流回归：对 `prompt_optimize` 输入返回两次空文本，使工作流按质量门禁阻止结果；先运行新测试，观察其因期望 `error`、实际 `success` 而失败，再实现修复。
- **改动：** `BlindCandidateRunService` 仅在状态为 `completed`/`needs_clarification` 且输出非空时标记成功；质量门禁拒绝、无效或空结果写作空预测、`schema_valid=false`、`status=error`，并使用内容无关的新类别 `workflow_rejected`。scorer 白名单、`prediction.schema.json` 和数据集说明同步更新。敏感输出继续走原隔离/扣留分支；取消和 Provider/runner 错误类别不变。
- **验收结果与预期收益：** 新回归 RED 时准确重现 `Actual: success`；GREEN 后 runner、scorer、工作流执行器筛选 **32/32 通过**。同类工作流拒绝现在对每条记录贡献 1 次失败尝试、0 次成功预测，不再产生“成功但空输出”的矛盾制品；此为评测统计正确性改进，不产生模型质量收益，也未调用真实模型/API。
- **上下游与门槛：** 该修正保障后续受控基线的覆盖率、失败率和 schema 合法率可解释；修正后 `FullyQualifiedName~Blind` 覆盖 runner、scorer、manifest、校验器、盲评 CLI 等筛选 **174/174 通过**，`prediction.schema.json` 亦成功解析为 JSON。阶段 0 仍缺授权盲评/校准数据与受控出站批准。取得正式数据和审批后再执行真实基线，生成逻辑继续冻结。

### A0.3 跨样本强制一次性评审任务引用唯一（2026-10-03）

- **计划节点与问题：** intake 规程要求每个“样本 × 评审”使用不同的一次性 `reviewer_ref`。证据 validator 原本只在每条记录内检查两份引用不同、且与对应冻结记录匹配；如果数据与证据清单同时把同一引用复用在多个样本，现有交叉检查会接受它，削弱任务隔离和审计追溯。
- **TDD 证据：** 构造两条记录且数据与证据中都复用同一评审引用；临时移除全局唯一检查运行测试，validator 错误返回 `Valid=true`，新回归按预期失败。恢复实现后，重复评审引用及重复裁定引用均被拒绝；使用不同一次性引用的双记录对照通过。
- **改动：** `BlindEvaluationEvidenceManifestValidator` 在整份数据集范围内追踪 `reviewer_ref` 与存在时的 `adjudicator_ref`，发现复用时返回不回显引用值的 `duplicate-reviewer-reference`。更新数据集 README 与接收规程，说明这是结构唯一性检查，不能证明随机性或真实评审身份独立。
- **验证与收益：** 证据清单校验器筛选 **23/23 通过**，包含重复评审引用拒绝、重复裁定引用拒绝、合法多记录唯一引用接受；缺陷场景从原先被当作有效证据索引改为明确拒绝。此举提升数据隔离/追溯完整性，不直接提升模型输出质量，也不认证外部身份。
- **上下游与门槛：** 对将来的 500 条盲评集，这一约束可拦下跨样本复用任务引用，但真实性仍需受控身份映射核验。当前阶段 0 的实际授权数据、独立评审证据和真实基线仍缺；下一步触发条件不变：接收获授权冻结样本及完整外部证据后运行各项 validator，再由独立评测保管人核验。阶段 1 生成行为继续冻结。

### A0.4 pairwise 评审与 cohort 评分按 split 隔离（2026-10-03）

- **计划节点与问题：** 阶段 0 的 development 用于调参，frozen_test 必须在候选/提示/采样锁定后独立评审。此前 pairwise 构包器遍历整个 gold，可能把两种 split 的比较混在同一盲包；单候选 finalizer 也直接使用混合 split 报告；cohort finalizer 又要求 `split=all` 并重算全量数据，无法证明最终指标只来自明确选择的 split。继续审计后发现独立 manifest validator 只检查报告 admission 和文件哈希，没有复核报告 split 或 comparisons 样本 split。
- **第一性原理与取舍：** 要控制的变量是“哪一组独立样本支撑该分数”。采取显式单 split 的构包与评分，而不是仅在 UI/README 声明操作员应手动筛选；完整 gold 字节仍整体做准入和训练泄漏审计，以维持数据集身份和全量合法性。Development 可单独生成探索包/报告；frozen_test 额外要求明确锁定确认；任何混合 split cohort 都拒绝。
- **改动：** `blind-pairwise-package` 现在必须给出 `--split development|frozen_test`，包只纳入所选 split，pairwise sealed map 与 package info 写入 split（package_version=2）。冻结确认只对 `frozen_test` 包强制，不因 full gold 中含冻结行而阻止 development 包。`blind-evaluate` 新增 `--split`，完整数据准入仍覆盖全部 gold，预测按所选 split 评分，comparisons 若混入其他 split 则拒绝；省略时保留 `all` 作为旧式全量诊断。单候选与 cohort finalizer 都只接受显式单 split 评分报告并按其重算；cohort finalizer 还交叉校验候选 run/map、评审 map 与比较 ID。清单记录 split 角色、split 样本数与整个 gold 样本数及 frozen unlock 状态，不自动宣称阶段门槛/晋级通过。CLI help 与 AI 评测 README 已同步。
- **验收：** TDD 扩展覆盖 mixed-gold 下只输出冻结项、frozen CLI package 100/100 条、frozen scoring usage 从全量 50,000 输入 token 改为冻结子集 10,000、candidate/cohort split 绑定与解锁状态、跨 split pairwise sealed map 与 comparisons 拒绝、独立 validator 拒绝 report split 与 manifest 不一致、拒绝指向其它 split 的 comparison，以及评审/比较原始来源核对。新 validator 回归先按预期失败：两个不一致制品组合均被旧校验器接受；补齐交叉校验后，收口/校验器筛选 60/60 通过，`FullyQualifiedName~BlindEvaluation` 为 146/146。再扩大到 `FullyQualifiedName~Blind` 时，首次发现候选运行端到端 fixture 的手工 report 缺 `evaluation_split`，补入实际 development split 并断言最终 role 后，扩大筛选 **182/182 通过**；DatasetBuilder、BlindEvaluationRunner 与测试程序集构建成功。
- **预期收益与限制：** 混合 split 进入同一 pairwise 盲包/正式 cohort 的结构性路径由“允许”改为“生成端过滤、收口端多处交叉校验并拒绝”。在 500 条且 400/100 切分的回归中，frozen 包及其成对计数从潜在 500 条降为正确的 100 条；这是隔离正确性，不是质量提升。API token 汇总按样本减少 80% 是该夹具切片大小比例（100/500），不是运行成本实测或性能优化结果。没有真实盲评样本、模型运行或云调用，AI 质量、成本和延迟收益仍未测。
- **上下游与阶段门槛：** 本步完成 A0 工具链 split 隔离；未补齐来源授权、独立评审身份核验和真实基线，因此阶段 0 仍未通过，阶段 1 的生成行为更改仍锁定。下一步触发条件是拿到授权数据与受控评审证据后，development 先用于调参与提示/模型筛选；仅在候选锁定后独立创建 frozen_test 盲评包、评分并审查人工/统计证据。此前旧的 all-split 报告不能作为新的 split cohort 收口输入，应按选定 split 从受控原始预测重新评分。

### 阶段 0 高风险零反转的统计解释补充（2026-10-04）

- **节点/问题：** A0.1 统计设计复核。现行候选门槛要求冻结测试中高风险关键事实反转观测数为 0；推荐 frozen_test 有 250 条，但聚合最低高风险配额仅 25 条（增加 task × split 交叉配额后约为 26 条）。零观测容易被误读成真实风险率近零。
- **第一性原理与计算：** 对 `n` 个相互独立的高风险语义族，若观测到 0 次事件，单侧精确二项 95% 上界解 `(1-p)^n = 0.05`，即 `p = 1 - 0.05^(1/n)`。复算得到 n=25: 11.29%、n=26: 10.88%、n=50: 5.82%、n=125: 2.37%、n=200: 1.49%、n=250: 1.19%；要使上界低于 1%，需至少 299 个独立高风险族。来源仅作为精确二项置信限定义参考：[NIST Exact Binomial](https://www.itl.nist.gov/div898/software/dataplot/refman2/auxillar/exacbino.htm)。
- **方案与改动：** 在[阶段 0 样本设计提案](../phase0-evaluation-sample-plan.md)增加零观测解释、假设条件、结果表和决策选项；保留现有“观察到 0 次关键反转”的硬门槛，不改 Schema、validator、数据准入比例或产品生成逻辑。报告应展示 `0/n` 和预注册区间上界，限定结论在冻结的高风险语义族切片；如果项目要宣称总体风险率低于某阈值，须先批准所需独立族数和成本。
- **验证/量化收益：** 使用公式的独立数值复算验证表格，且由阈值不等式 `ceil(log(0.05)/log(0.99))=299` 复核 1% 单侧上界所需族数。文档检查通过；没有运行产品测试、Provider 或模型，因此质量/成本/延迟实测收益为 0/未测。预期收益是防止将 25–250 个零事件样本误述为“风险为零”或“低于 1%”。
- **自审与下游：** 假设要求按独立语义族计数且抽样能代表声明目标的高风险任务族；分层/刻意超采样不允许外推到线上总体流量，族内相关、多重终点也未被上述简单区间覆盖。此处是统计报告补充，不改变发布门槛。样本到位后由产品/评测负责人确认是否只报告冻结切片观测，还是需扩展高风险样本以支撑明确风险率上界；A0.1 授权和受控评审证据仍缺，阶段 0 未通过。

### A0 候选评分跨字段一致性（2026-10-04）

- **输入与问题：** 对照历史版 `annotation-handbook-v1.1.md`、`prediction.schema.json`、`BlindEvaluationScorer` 及评测夹具。v1.1 将 `directly_usable` 定义为无需实质重写即可使用、直接可用分 3 表示仍需实质编辑，却只说两者“通常”相符并允许靠受控备注例外；现行 prediction Schema 没有备注字段，scorer 也不拒绝矛盾组合。手册另明确“高风险关键事实反转 ⇒ fidelity_score=1”，scorer 原先不检查此跨字段条件。
- **决策依据：** 直接可用率是独立硬门槛，若让同一评审记录同时声明“可以直接使用”与“需实质编辑”，率值和分值语义会分叉。固定 threshold 为 4：分数 4/5 表示可直接使用，1/2/3 表示不可直接使用；高风险反转显式成立时保真分固定为 1。此处收紧的是评审产物一致性，不改原有候选达标比例或生成行为。
- **改动：** `BlindEvaluationScorer` 对两份初评及实际裁定执行统一跨字段校验，矛盾时返回不回显文本的 `review-rubric-conflict`，不能封存通过；`prediction.schema.json` 增加同等条件，并将 schema `$id` 升至 v2；保留 v1.1 手册历史原文，新建 `annotation-handbook-v1.2.md` 并由 README 指为当前口径。合成集成夹具改为 rubric-consistent 值。
- **验证与量化结果：** 新 direct-usage 边界两例及高风险反转一例先按预期 **3/3 失败**；实现和修夹具后 `BlindEvaluationScorerTests` **18/18**、扩大的 `FullyQualifiedName~Blind` **185/185** 通过。PowerShell `Test-Json` 用 Draft 2020-12 schema 对有效/冲突的 6 个实例验证：true/4、false/3 与 reversal/1 接受，true/3、false/4 与 reversal/3 拒绝。测试仅用合成数据，无 Provider 调用。
- **效果、限制与上下游：** 此步让 `direct_usability_rate` 与 1–5 可用性分不能对同一输出给出相反结论，并确保关键反转不会被较高平均分稀释；没有真实评审数据，因此人工一致性、模型质量收益仍未测。prediction 规范化由 v1 收紧为 v2；暂无真实冻结候选评审产物，旧无效夹具不属于可迁移的正式基线。阶段 0 仍缺授权样本及独立受控评审证据，阶段 1 继续冻结。

### A0.1 最新公开数据候选复核（2026-10-04）

- **计划节点/输入：** 阶段 0 来源发现；复核公开数据卡、仓库说明和 Creative Commons 官方许可摘要，未下载样本、未导入评测目录、未调用 Provider。
- **结论：** 新发现 Zhiyin 中文写作基准 V1 为 280 个案例、18 类任务，页面许可 CC-BY-NC-ND-4.0 且以繁体中文和 LLM judge 为主；其 8,120 行包含多个模型判断，不能当独立样本数，也不符合项目可能商用的来源默认条件。WritingBench 有 1,000 个开放写作 query、仓库标称 Apache-2.0，可参考 prompt 优化的任务分类，但外部材料来源/逐项权利仍需核验，不具备润色双评 gold。RewritingBench 仍只有 129 条公开 eval；ToxiRewriteCN 1,556 条面向去毒性改写，只适合作为可能的隔离安全切片候选。
- **产出/建议：** 在 `docs/external-evaluation-data-research-2026-10-02.md` 新增 2026-10-04 复核和 A0.1 决策：正式 500+ 通用集仍需项目自有/明确授权样本、语义族隔离、双人独立评审与分歧裁定；公开源只作经过权利/隐私审查且单独报告的开发诊断，不与私有冻结集混合，不因网页 license 标签自动准入。
- **量化/自审/下游：** 本步新增 4 个来源的当前规模/许可/任务适配证据；可准入正式冻结样本仍为 **0 条**，阶段 0 不变。没有质量、成本或延迟收益实测。缺少的数据负责人/许可核验、≥500 条合格通用样本和受控双评证据不能由公开网页调查替代；拿到这些输入后，按既有 source register 与 evidence manifest 流程接收，不改生成行为。

### A0 隐私准入审计扩展常见凭据格式（2026-10-04）

- **节点/输入：** 阶段 0 样本脱敏与候选输出隐私门禁。源码审计发现既有“疑似密钥”检测只覆盖 `sk-…` 和 `api_key` 标签，漏掉文档化的 HTTP Basic/Bearer Authorization、AWS access key ID、Slack `xox*` token 与 PEM 私钥头。官方依据为 [GitHub secret scanning 支持的通用凭据类型](https://docs.github.com/en/code-security/reference/secret-security/supported-secret-scanning-patterns)、[AWS access key 格式](https://docs.aws.amazon.com/us_en/IAM/latest/UserGuide/security-creds-programmatic-access.html) 和 [Slack token 类型](https://api.slack.com/concepts/token-types)。
- **TDD 证据与改动：** 六个新正向场景先以旧实现执行，**6/6 按预期失败**；将有限、可识别格式并入 `BlindEvaluationAuditor.ContainsPotentialSensitiveData`：AWS 长/短期 `AKIA`/`ASIA` ID，Slack `xox[baprs]-` token，HTTP Authorization Basic/Bearer 凭据与常见 PEM private-key header。另覆盖无凭据上下文、单独提到格式术语、错误/不完整标头和非规范小写 AWS 前缀等拒绝例，避免宽泛的“高熵字符串”猜测。文案和扫描范围同步到 AI 评测 README；所有问题只报告固定类别和脱敏记录 ID，不回显命中值。
- **验证/收益：** 新正向 **6/6** 在旧代码下失败、实现后通过；新负向 **6/6** 接受；`FullyQualifiedName~BlindEvaluationAuditTests` **43/43**，扩大后的 `FullyQualifiedName~Blind`（覆盖审计器、来源登记、候选评分/运行、收口、manifest 与 CLI 集成）**197/197 通过**。相对原实现，覆盖新增 4 类有边界的凭据格式族（AWS ID、Slack token、Authorization header、PEM private key），不报告凭据有效性或模型质量增益；没有真实样本和生产流量测量。
- **限制与上下游：** 这是正则启发式而不是完整 Secret Scanner：格式变更、无标准前缀凭据、被拆分/混淆的值及普通个人信息都可能漏过，也可能要求人工复核示例文本中的凭据格式。其作用是提高 A0.1/A0.2 在样本与预测文本上的已知模式拦截率，不能替代人工脱敏及受控来源审核，不解锁阶段 0 或阶段 1。

### A0 继续覆盖项目 Provider 的密钥格式（2026-10-04）

- **问题/输入：** 上一步扩展后复核 `ProviderPlatformCatalog`，项目直接支持 Gemini 与 Groq，但无标签裸凭据仍会漏过既有格式集合。官方资料显示 Google key 有 `AIza…` standard key 字符串示例，同时 Gemini 正从 standard key 迁移到 authorization key；Groq 官方资料示例使用 `gsk_` 前缀。[Gemini key 类型/迁移](https://ai.google.dev/gemini-api/docs/api-key)、[Google key string 示例](https://docs.cloud.google.com/docs/authentication/api-keys)、[Groq 密钥示例](https://console.groq.com/docs/production-readiness/security-onboarding)。
- **TDD/改动：** 给无标签的 `AIza`（39 字符 standard key）与 `gsk_` 凭据加正向回归；旧检测对新增 **2/2 均漏检**。审计器现在按固定长度检测 AIza 传统 key、按长度区间检测 Groq `gsk_` key；增加短/残缺前缀负例，不使用泛化高熵检测。README 同步标明 Gemini 新 authorization key 若未带标签/HTTP header 且没有稳定可核对格式，不能保证发现。
- **验证/收益：** `BlindEvaluationAuditTests` 从 43 项扩至 **48/48 通过**；扩大后的 `FullyQualifiedName~Blind` 为 **202/202 通过**。在正则覆盖面上从 4 组增为 6 组已核对的凭据家族；无真实样本/凭据可用于估计实际拦截率，也没有模型质量、成本或时延收益。
- **自审与下一步：** Google 文档说明 2026-05-28 起新建 AI Studio key 默认转为 authorization key，但未在本次查阅材料中给出可稳定维护的词法模式；当前覆盖“格式确定的旧 key + 被 `x-goog-api-key`/`api-key` 标签标识的新 key”，不把日期当作可以猜 key regex 的依据。其余云 profile 的 key 前缀也未逐一建立可信模式表。后续 P2 可整理带官方来源和核验日期的 Provider-secret taxonomy；任何格式无证据时维持 label/header 扫描，不引入可能误报整个自然语言 token 的宽泛随机性阈值。阶段 0、阶段 1状态不变。

### A0.2 候选输出也扣留新识别的 Provider key（2026-10-04）

- **计划节点/问题：** 新增 `AIza`/`gsk_` 识别后，准入审计红测证明数据侧会命中；候选质量评分是另一边界，需确认高风险内容不会进入候选有效率/人工评分，也不会由问题文本回显凭据。
- **改动：** `BlindEvaluationScorer` 已复用同一审计器，本步新增 Gemini 与 Groq 裸 key 输出各一例的端到端评分回归；断言评分问题为 `pii-output`、不回显密钥、候选 `Passed=false`。仅增加测试，不再增加第二份密钥正则或改变生产生成逻辑。
- **验证/收益：** `BlindEvaluationScorerTests` **20/20**，扩大后的 `FullyQualifiedName~Blind` **204/204** 通过。两个已知格式现在由相同规则在 gold 准入和 prediction 评分两侧拒绝；可验证的是测试覆盖从 **0/2** 条 key 格式评分路径提高到 **2/2**，不是生产流量拦截率。无真实候选、模型质量/成本/延迟测量。
- **自审/下游：** 此项收口了上述两种已识别 key 的离线数据/输出路径；格式未知或无标签的新 Gemini authorization key 仍可能漏检。下一步如扩展 Provider-secret taxonomy，须逐个核实官方稳定格式并同时增加准入/评分两端回归；A0.1 真实授权样本/独立评审证据仍缺，A0.2 模型基线尚不能运行。

### A0.2 Gemini authorization key 标头标签路径回归（2026-10-04）

- **节点/输入：** 阶段 0 脱敏与候选评分敏感信息门禁。审计确认 `api-key` 标签扫描已能命中 `x-goog-api-key: <opaque-value>`；Google 当前文档说明 authorization key 与 standard key 并行/迁移，REST 请求通过 `x-goog-api-key` 传入 API key，并未在所查文档中给新授权 key 一个可维护的固定字面前缀。[Gemini API key 文档](https://ai.google.dev/gemini-api/docs/api-key)
- **改动：** 在 gold 上下文准入审计增加 `x-goog-api-key` 标头正例；在候选预测评分增加不透明示例值带 `x-goog-api-key:` 标签的正例，分别断言 `pii`/`pii-output` 拒绝且问题摘要不回显值。同步 README 精确描述边界：检测依赖 key/header 标签，不承诺识别无标签的未知 key。未改生产逻辑或模型请求。
- **验证与量化：** `BlindEvaluationAuditTests` 与 `BlindEvaluationScorerTests` 定向筛选 **70/70 通过**；完整 `FullyQualifiedName~Blind` **206/206 通过**。准入与候选输出两条标签路径均有回归保护；这是测试覆盖完整度，不代表真实密钥检测率。无模型质量、成本、时延收益测量。
- **自审/下游：** 正例中的示例 key 为合成占位值，仅证明标签路径；不证明所有认证 key 格式都能发现。继续禁止对未知 token 使用宽泛高熵猜测。A0.1 的授权样本与受控独立评审证据仍未收到，A0.2 基线和阶段 1 生成行为变更仍未解锁。

### A0 Provider 隐私审计：OpenRouter key 格式（2026-10-04）

- **问题/输入：** Provider catalog 包含 OpenRouter。既有 `sk-[a-z0-9]{12,}` 仅匹配 `sk-` 后连续字母数字；官方 quickstart 明示 key 示例 `sk-or-v1-…`，中间版本段使现规则漏检。[OpenRouter quickstart](https://openrouter.ai/docs/quickstart)
- **TDD 证据：** gold 输入和候选输出分别新增 `sk-or-v1-` 样本；修改实现前两路径均失败（**2/2 漏检**），重现了准入和评分共同依赖扫描器带来的盲点。初版 16 字符门槛虽通过长 key 用例，却被短载荷 `sk-or-v1-a` 两侧回归再度击穿（**2/2 RED**），遂删除无上游依据的长度假设；只有无载荷的裸前缀作为词汇负例。
- **改动：** `BlindEvaluationAuditor` 增加单独的 `sk-or-v1-` 模式，大小写不敏感地匹配前缀后的非空 ASCII 字母/数字/下划线/连字符载荷，裸前缀本身不命中；读取同一函数的样本准入与 candidate output 评分自动共享规则。未推断或约束上游未公布的 key 长度。未改变 Provider 请求、用户偏好、模型或生成提示。
- **验证/量化：** 凭据格式、候选评分及短格式词汇回归定向 **22/22** 通过；扩大后 `FullyQualifiedName~Blind` **209/209** 通过。已知 OpenRouter 前缀在两条评测边界由 **0/2** 变为 **2/2** 受回归保护；这不是线上检测率估计，无模型质量/成本/时延收益数据。
- **局限/下一步：** 官方示例证明前缀，但未给出完整长度或字符集合；当前允许的 ASCII 字符仍属本地检测边界，不保证覆盖未来格式。其它 Provider 的 token 格式需逐项基于一手官方材料核对；继续避免泛化高熵扫描。阶段 0 仍缺授权样本与独立评审证据，阶段 1 继续冻结。

### A0 Provider 隐私审计：Anthropic API key 示例前缀（2026-10-04）

- **问题/输入：** Provider catalog 支持 Anthropic。Anthropic Claude for Sheets 官方文档的 API 参数示例展示 `sk-ant-api03-j1W...`；通用 `sk-` 扫描器要求 `sk-` 后连续至少 12 个字母/数字，遇到 `ant-api03` 的分段格式便漏检。[Anthropic Claude for Sheets 文档](https://docs.anthropic.com/en/docs/agents-and-tools/claude-for-sheets)
- **TDD 证据：** 增加 `sk-ant-api03-` 长与短载荷的 gold 准入、候选评分测试；旧实现 **4/4 漏检**。增加只出现裸前缀的词汇负例，避免文档提及前缀本身就判敏感。
- **改动：** 增加厂商示例中的精确 `sk-ant-api03-` 前缀模式，匹配其后非空 ASCII 字母/数字/下划线/连字符；不为未知 key 长度增加下限或上限。样本准入与候选评分继续共用同一敏感数据判定函数。README 说明这属于已知示例前缀启发式，并非完整 key 规范。
- **验证/量化：** `Validate_DetectsCommonCredentialFormatsWithoutEchoingThem`、`Evaluate_RejectsCandidateOutputWithKnownProviderKeysWithoutEchoingThem` 与凭据词汇负例定向筛选 **29/29**，`FullyQualifiedName~Blind` **216/216** 通过。两种 Anthropic 示例载荷的两条评测路径由 **0/4** 变为 **4/4** 受回归保护；无真实检测率、模型质量、成本或延迟测量。
- **局限/上下游：** 官方示例使用省略号表示部分 key，不构成对所有 API key 版本与字符集的完整规范；当前只认该精确前缀家族，其他版本仍可能漏检。没有更改产品 API 请求或生成行为；A0.1 真实数据证据仍缺，阶段 0 未通过。

### A0 Provider 隐私审计：xAI API key 前缀（2026-10-04）

- **问题/输入：** Provider catalog 将 xAI 作为 `Grok` 直连服务。xAI 管理 API 当前给出的创建响应含 `apiKey` 值示例 `xai-...`，管理和推理请求将 key 作为 Bearer credential；旧扫描器既无 `xai-` 规则，也未覆盖这类无标签裸值。[xAI 管理 API 授权文档](https://docs.x.ai/developers/rest-api-reference/management/auth)
- **TDD 证据：** 对长、短两种 `xai-` 载荷分别增加准入和候选输出案例；旧实现 **4/4 漏检**。单独提及裸 `xai-` 前缀的文案设为负例。
- **改动：** `BlindEvaluationAuditor` 增加厂商示例前缀 `xai-`，后接非空 ASCII 字母/数字/下划线/连字符；准入与候选评分共用该扫描。README 增加官方引用。没有基于示例 key 推测长度，也没有改动请求头、Provider 参数或生成链路。
- **验证/量化：** 密钥族正负例与两路径评分筛选 **34/34**，完整 `FullyQualifiedName~Blind` **221/221** 通过。xAI key 示例家族由 **0/4** 变为 **4/4** 测试保护；无真实流量命中率或模型质量/成本/延迟收益测量。
- **限制/下游：** 官方示例证明 `xai-` 前缀和 Bearer 用法，不足以作为未来全部 key 字符/格式的规范；规则是前缀启发式，可能对教学占位值触发人工复核，也可能漏掉厂商格式变化。其它 Provider 继续按官方一手资料审计；阶段 0 的真实授权数据和独立评审门槛未通过。

### A0 Provider 隐私审计：OpenAI project key 前缀（2026-10-04）

- **问题/输入：** OpenAI 是项目直连 Provider。OpenAI 官方 Cookbook 用 `sk-proj-…` 作为 API key 示例；旧通用 `sk-[a-z0-9]{12,}` 在 `proj-` 中间分段处终止，裸项目 key 因而漏检。[OpenAI Cookbook 示例](https://developers.openai.com/cookbook/examples/agents_sdk/session_memory)
- **TDD 证据：** 为 `sk-proj-` 长、短载荷分别增加 gold 准入和候选输出用例；旧实现 **4/4 漏检**。只出现 `sk-proj-` 前缀文字的负例不应命中。
- **改动：** `BlindEvaluationAuditor` 增加 OpenAI project-key 精确前缀检查，匹配其后非空 ASCII 字母/数字/下划线/连字符，复用到准入及评分两侧。未猜测 key 长度，不改变 OpenAI 请求或产品配置。
- **验证/量化：** 凭据正负例和两侧评分定向 **39/39**；`FullyQualifiedName~Blind` **226/226** 通过。官方示例 key 的两个长度 × 两个边界从 **0/4** 变为 **4/4** 回归保护；无生产命中率、AI 质量、成本或延迟测量。
- **限制/后续：** Cookbook 是示例而非 OpenAI 所有凭据类型的完整规范；规则仅覆盖展示的 `sk-proj-` 家族。MiniMax 官方页面只称 Token Plan key 为 `sk-cp`，未找到精确完整 token 前缀，故本轮不按第三方内容猜测实现；需厂商文档或受控、脱敏的真实格式证据后再评估。A0.1 仍未通过。

### A0 Provider 隐私审计：Ark UUID key 与阿里云计划 key（2026-10-04）

- **当前节点/具体问题：** 阶段 0 数据准入与候选输出隐私扫描。火山方舟官方 quick start 明确称其脚本兼容旧 UUID 及新格式 `ark-<uuid>-<suffix>`；百炼官方文档明确 Coding Plan / Token Plan key 以 `sk-sp-` 开头。两种都不会被现有通用连续式 `sk-` 规则覆盖。阿里云计划 key 检测仅用于避免离线评测数据意外包含凭据，不构成话匣子支持该套餐的声明；阿里云将 Coding Plan 限制于指定交互式编程工具。
- **TDD 与改动：** 先加合成占位值准入、候选输出和词汇负例测试；旧实现两种格式各在两条扫描边界漏检，RED **8/8**。新增 Ark 规范 UUID 分组+非空后缀检测与 `sk-sp-`+非空载荷检测，复用现有共用扫描函数；不猜长度，不生成真实凭据，也不改 API 请求或生成链路。
- **验收与量化：** 目标测试 GREEN **49/49**；完整 `FullyQualifiedName~Blind` 回归 **236/236**。相对当前已观察实现，新增两种 key 家族的准入与候选输出覆盖，共 **8 个正例边界**，词汇负例不触发；这些数字仅是合成回归覆盖，不是线上拦截率，不代表模型质量、成本或延迟收益。
- **全量回归附记：** 在将 `TEMP`/`LOCALAPPDATA` 指向仓库可写的隔离目录后，全套 `Huaxiazi.Tests` 结果为 **1202 通过、11 跳过、4 失败**。四项失败均为当前工作树的发布/交付约束测试：`AcceptanceDefectTests.DeliveryScript_ProducesOnlyTwoStableClientArtifacts`、`ReleaseSecurityTests.Installer_UsesInnoSetupAndPerUserDefault`、`ReleaseSecurityTests.PublishScript_UsesDedicatedLockedRuntimeGraph`、`ReleaseSecurityTests.ReleaseDoesNotShipScriptInstaller`；与此次 credential scanner 文件无关，未在本步改动或掩盖。原先未隔离系统临时目录的运行因拒绝访问提前中止。
- **风险/上下游：** Ark 后缀字符集按扫描器采用 ASCII 字母、数字、下划线和连字符，官方页面只明确格式占位符，故非完整 token 语法。旧 UUID key 不带专属 `ark-` 前缀时可能依赖标注字段触发的通用 bearer 检查，不能声称裸值全覆盖。阶段 0 真实数据门槛仍未通过，阶段 1 继续冻结；继续按一手资料核验其余 Provider，授权数据与独立评审证据到齐后再启动真实基线。

### A0 Provider 凭据覆盖矩阵（2026-10-04）

- **计划位置：** 阶段 0 隐私准入门的证据台账；是对规则覆盖边界的盘点，不是 A0.1 准入通过。
- **本步输入/输出：** 对照 `ProviderPlatformCatalog` 及 OpenAI、Anthropic、Google、DeepSeek、阿里云、火山、SiliconFlow、MiniMax、OpenRouter、xAI、Mistral、智谱、Together 的官方资料，新增 [`provider-credential-audit-2026-10-04.md`](../provider-credential-audit-2026-10-04.md)，按厂商声明、实际扫描前提和未覆盖项逐行登记；另将用户自定义 endpoint 标为逐实例核验。
- **结论：** 有证据支持的 `sk-proj-`、`sk-ant-api03-`、`sk-or-v1-`、`xai-`、Ark 新 UUID 分段格式、阿里云 `sk-sp-`、MiMo `tp-` 已有专用或通用检查；DeepSeek、SiliconFlow 和 MiMo 按量 API 官方只说明 `sk-` 前缀，现有通用 regex 仅涵盖其连续小写字母/数字且至少 12 字符这一子集。Mistral、智谱、Together、StepFun、百川的资料只证明 Bearer 标头认证，可检查保留标签的请求片段，不能据此保护裸 key。MiniMax 只称“sk-cp Key”而未显示完整 token 语法，裸 key 仍属待核缺口。Groq 官方示例展示 `gsk_` 且当前规则存在对应前缀模式。
- **决策/自审：** 不把 OpenAI-compatible 请求协议当成统一 key 语法；不为证据不足的裸凭据扩成任意高熵检测，避免正文误报。已生成的 matrix 明确标注未核 provider；缺少官方资料的部分不会写成覆盖完成。
- **验收与量化：** 矩阵逐项明确 17 个预置云端厂商的资料及扫描边界，另列剩余 3 个预置厂商和按实例核验的自定义 endpoint；本步仅文档变更，没有检测率、质量、时延或成本数据。后续规则仍必须经 gold 准入与 candidate 输出双路径回归。
- **上下游：** 下一子任务核验剩余 Kimi、讯飞星火、Yi 及自定义接口的认证形状；阶段 0 仍缺授权真实评测数据、来源登记、独立评审证据和受控基线审批。A0.1 未过前，不修改生成行为或发送真实模型请求。

### A0 Provider 隐私审计：MiMo Token Plan `tp-` key（2026-10-04）

- **具体问题/输入：** MiMo 官方 API 集成 FAQ 明确按量 key 为 `sk-xxxxx`、Token Plan key 为 `tp-xxxxx`；现有通用 `sk-` 和所有已知专用规则均不会匹配 `tp-`。不做改动会在准入和候选文本同时漏掉这种独立 key 家族。[MiMo API integration FAQ](https://mimo.mi.com/docs/zh-CN/quick-start/faq/api-integration)
- **TDD：** 新增长、短合成 `tp-` 值的 gold admission 和 candidate output 回归。旧代码 **4/4 漏检**；“MiMo 使用 `tp-` 前缀”纯词汇负例未命中。
- **实现：** `BlindEvaluationAuditor` 增加精确 `tp-` 前缀模式，后接非空 ASCII 字母、数字、下划线或连字符；不设长度界限，因为官方 `xxxxx` 是占位式格式说明，没有披露规范长度。两条边界共用现有判定，不改变 API 配置或生成行为。
- **验证与量化：** 新增用例后凭据正负例筛选 **54/54**；完整 `FullyQualifiedName~Blind` **241/241** 通过。`tp-` 两种载荷长度 × 两条评测路径从 **0/4** 漏检转为 **4/4** 回归保护；这是合成形式覆盖，不是线上召回率或 AI 质量/成本/时延收益。
- **自审/下游：** 该规则仅覆盖 MiMo 官方公开的 Token Plan 前缀，不表示应用增加 Token Plan 支持。标准 `sk-xxxxx` 仍走通用连续式检查，完整 key 字符集/长度未知；需要官方更精确语法时再扩展。阶段 0 授权数据/双评证据仍缺，下一步继续核验 Kimi、Spark、Yi；A0.1 未过前不运行真实模型基线。

### A0 Provider 凭据矩阵更新：Kimi 与讯飞星火（2026-10-04）

- **计划节点/当前清单：** 阶段 0 的数据与候选输出隐私准入；原矩阵列有 20 个预置云 Provider，其中 17 个已核验、3 个待核验。当前仍未到 A0.1 的真实授权数据准入节点。
- **问题与输入：** 按待办核验 Kimi、讯飞星火、Yi。Kimi 官方指南仅能确认 Bearer Authorization 用法，没有裸 key 语法；星火 HTTP 文档称控制台 `APIPassword` 用作 Bearer 凭据，例值 `123456` 是文档占位，未说明完整词法格式。两者证据仅分别支持标头扫描和字段标签扫描，不能推出裸凭据通用格式。[Kimi API 指南](https://platform.kimi.com/blog/posts/kimi-api-quick-start-guide)、[星火 HTTP 调用文档](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)
- **TDD 与实现：** 已有两个正例在准入和候选输出路径复现漏检，RED **2/2**；自审另发现空值 `APIPassword:` 也会因只有分隔符而误报，新增空字段负例再现 RED **1/1**。规则扩展为 `api_password` 后必须跟随 `:` / `=` 和至少一个非空值字符；字段名词汇与空字段负例均保持通过。gold 和 candidate 仍共用扫描入口，不添加来源不明的裸 key 正则，不改 API 请求或生成行为。
- **验收/结果：** 目标组 **25/25**，全 `FullyQualifiedName~Blind` **245/245** 通过。候选输出出现标签化 Spark 值会给出 `pii-output`、拒绝候选且不回显值；纯词汇及空字段不触发。原先 Yi 页面抽取只显示门户导航；后续检查官方 SSR 页面源取得了认证正文（见执行卡 #36），因此矩阵现为 **20/20** 均有认证传输方式资料，裸 key 格式仍逐家有未决项。自定义 endpoint 仍按实例核验。
- **量化收益与限制：** 新增 2 个正例保护（准入 1、候选输出 1）和 2 个误报负例，共 4 个回归点；对于标签化 Spark 值从 **0/2** 路径检测升为 **2/2** 路径受回归保护。Kimi 仅在 Authorization header 被保留且值满足既有规则时可查；未标注裸值与未知格式仍可能漏检。以上是合成回归覆盖，不是线上召回率，不证明 AI 质量、延迟或成本改善。
- **自审/下一步：** 首轮页面文本提取确实没有呈现 Yi 认证正文，当时没有据此猜测格式；随后通过官方页面 SSR 源读取到真实正文并在执行卡 #36 补录。阶段 0 仍须获得至少 500 条授权脱敏数据、独立评审与冻结证据，并完成固定模型基线后才能解锁生成链路调整。

### A0 Provider 凭据认证资料矩阵补齐：零一万物 Yi（2026-10-04）

- **问题/输入：** 执行卡 #35 首轮网页文本提取只显示 Yi 平台导航，认证方式仍留白。回看官方 `/docs/api-reference` 的 SSR HTML 后取得页面正文：官方说明 API key 放在 `Authorization: Bearer`，并将 `Authorization` 列为必需 header。[Yi 官方接口文档](https://platform.lingyiwanwu.com/docs/api-reference)
- **判定与方案：** 该证据补齐“认证传输方式”，但页面仅用 `YOUR_API_KEY` 占位，没有裸 key 前缀、长度或字符集。项目通用 Authorization 检查已覆盖保留 header 且 Bearer 值达到至少 20 个允许字符的文本；不为 Yi 增加专属裸 key 检测。
- **产出/验收：** 台账新增 Yi 官方资料和实际扫描前提；20/20 个预置云 Provider 均有认证传输材料，用户自定义 endpoint 仍逐实例处理。没有源码或模型请求变动；不新增模型相关测试。已有通用 Bearer header 回归仍是此路径的代码验收证据。
- **收益/限制：** Provider 认证传输方式台账覆盖率从 19/20 提升到 20/20；裸 token 格式可判定的覆盖率没有相应提升，未知 key 若无标签/header 仍可能漏过。无质量、成本、延迟或生产密钥召回率收益数据。
- **下一步/阶段门：** 可继续做 MiniMax `sk-cp`、Ark 历史 UUID 的一手资料核验和自定义 endpoint 风险说明；更高优先级的阶段门仍是至少 500 条有授权、脱敏样本及独立双评/裁定证据，当前目录缺少该交付包，因此阶段 1 生成行为仍冻结。

### A0 Provider 凭据资料边界复核：MiniMax `sk-cp`（2026-10-04）

- **输入/发现：** MiniMax 官方 Token Plan 页面称可获取 “sk-cp Key” 用于 OpenAI-compatible 工具；页面未展示完整 token。平台其他 API 的 Bearer 示例没有明确把该请求凭据标成 Token Plan `sk-cp`，两条证据不能拼接推断套餐 key 的认证头或 token 后缀。[官方 Token Plan 页面](https://platform.minimaxi.com/subscribe/coding-plan)
- **决策/产出：** 已修订 Provider 台账，分开记录 key 名称与认证方式。继续将无标签 `sk-cp` 留在已知扫描缺口；不增加 `sk-cp-` 猜测正则，不更改 Provider 支持、API 请求或生成行为。
- **收益/限制/下游：** 本步只改善证据准确性，无代码或质量/成本/时延收益。漏检无标签计划 key 的风险仍在；待取得明确官方格式后，方评估准入与 candidate 双路径回归。下一项按执行卡 #37 核对 Ark 历史 UUID；阶段 0 仍缺受控真实样本与独立评审包。

### A0 Ark 历史 UUID 误报/漏报取舍复核（2026-10-04）

- **输入/事实：** [火山方舟 quick start](https://docs.volcengine.com/docs/ark/quick-start-beginner?lang=zh) 明确官方脚本兼容历史 UUID key 与新格式 `ark-<uuid>-<suffix>`；未给出旧格式更具体的词法边界。
- **具体问题/取舍：** 裸 8-4-4-4-12 UUID 与大量正常请求、样本、任务和引用 ID 同形。独立匹配所有 UUID 会让任何普通 ID 都变成疑似 secret，误报成本高；完全忽略又可能漏掉未标注旧 key。当前按可解释上下文识别：已有 `api_key`/`API Key:` 标签扫描和完整 Bearer header 规则可查标记文本；保留裸 UUID 不命中。
- **产出/验证：** 已更新 Provider 凭据台账，明确新 `ark-<uuid>-<suffix>` 专用规则与旧 UUID 标签/header 路径的差别。现有通用标签扫描及 Bearer 头回归覆盖上下文路径；本项为证据/边界审计，不改规则或测试，不以此前 `FullyQualifiedName~Blind` 通过推断裸 UUID 召回率。
- **结果/下一步：** 为避免在无法测量普通 UUID 基线、也没有上下文来源时引入宽泛误报，不加裸 UUID matcher。未获授权真实凭据与盲评样本下无法量化假阳/漏检比；阶段 0 数据包仍缺，进一步判断须以授权语料和来源标注为输入。

### W4.7 AI 补样跨集近重复筛查（2026-10-05）

- **当前节点：** 对冻结 v1 的 16 个族与 AI 补样的 20 个族执行字符三元组 Jaccard 筛查，覆盖所有至少一端来自补样的 **510 个族对**。基线与补样分别绑定 manifest 和 `cases.jsonl` SHA-256；补样必须仍为未审阅 draft、父哈希匹配冻结 v1，且跨集 case/family id 不冲突。
- **实现：** `DatasetBuilder/PolishRegressionSupplementNearDuplicateAuditor.cs` 与 `polish-regression-supplement-neardup-report` 命令；固定复用已版本化规则、输出候选与人工模板、哈希 manifest、create-only 工件。算法只生成待人工检查的候选，不修改原数据、不合并族、不改 split。
- **实际结果：** 报告位于 `datasets/polish-regression-ai-supplement-neardup-v1/`。0.70、0.80、0.90 阈值候选均为 **0/510**；基线 16 族、补样 20 族、总计 36 族。报告明确该阈值未校准、阶段 0 贡献为 0、不能用于盲评。
- **TDD/验证：** 新测试先因审计器缺失而编译失败；实现后定向测试 **2/2 通过**，覆盖 pair 数、跨集近重复入列、哈希绑定篡改拒绝、零阶段贡献及不自动合族。实际命令成功生成报告，空候选文件 SHA-256 为标准空内容哈希。此步没有请求模型，也没有测量生成质量、成本或延迟。
- **判断与后续：** 0 候选只表示该字符三元组筛查在这些阈值未命中，不能推出语义族重复数为零；AI 草稿仍需人工检查并完成 CSV 接受/编辑/拒绝。W4.7 的“确认 0 新增重复族”语义验收尚未完成，禁止将补样并入 frozen v1。下一步继续推动已有 20 行人工审阅表落地，并保留未审阅样本的草稿身份。

### W2.3 人工副本回收复核（2026-10-05）

- **文件状态：** 用户标注副本 `outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review - 副本.xlsx` 已有 19 条决定、reviewer 和 UTC 时间；其 SHA-256 与既有副本分析记录一致。审阅导入器已对该副本执行实际导入校验。
- **结果：** 导入器拒绝第一条记录，错误为 rationale 是占位语；未创建 JSONL 输出。副本内 19 条理由均为占位内容，不能当作有效人工裁定。另有 19 条都选择 `same_semantic_family`，与当前“事实骨架 × purpose”定义及候选中 purpose 不同的证据相冲突；导入器不会替人推断或自动改决定。
- **后续：** 保留副本原件。请在副本中逐条改写 rationale，并根据既定族定义复核 decision；复核完后可直接重新运行 `polish-regression-neardup-import-workbook`。本轮 AI 补样近重复包扫描为 0 候选，但不构成语义无重复结论；补样 CSV 当前仍为 0/20 完成人工审阅。

### W4 补样覆盖差距复算与规格草稿（2026-10-05）

- **当前节点：** 对现有 20 条 AI 补样按 `context.gap_behavior` 复核类别分布，并依据计划中“建议每类至少 5 个独立族”计算规划缺口。当前 10 类各为 1–3 族；精确输入归一重复 0/20，unique family/input/reference 均为 20/20。标签和参考仍未人工验证。
- **产出：** 新增 `polish-regression-supplement-coverage-report` 命令与 `PolishRegressionSupplementCoverageAuditor`，产物在 `datasets/polish-regression-ai-supplement-coverage-v1/`；另生成 30 条 W4.3 AI 样本规格草稿，位于 `datasets/polish-regression-ai-supplement-specs-v1/`。30 条缺口按十类分别为 4、4、3、3、3、2、3、2、3、3；若每条规格生成一个新族，预计补样总量为 50，低于 120 上限。
- **设计注意：** 三条“多轮”规格把对话写在输入文本中，目前不能作为真实 turn-by-turn 测试；规格明示需要 `turns` Schema 与顺序回放支持。其余规格保留事实锚点、约束、期望决策、评分锚点及禁止内容，全部标记待人工规格评审，未生成案例。
- **验证：** 新审计器针对测试 **2/2**，与近重复、补样复核测试合计 **25/25** 通过。来源哈希篡改在建输出目录前被拒绝；覆盖报告固定声明 supplementation_authorized=false 和 phase 0 零贡献。规格包 JSONL 的人工读取校验为 30 行、30 个唯一 spec_id，十类数量与报告缺口吻合；manifest 记录源哈希。
- **结论/衔接：** 此项完成覆盖计数及 W4.3 规格草稿，不等于类别目标已实现。下一步按副本现有事实证据复核 19 条族决定与理由；对新规格由人审查，特别决定是否将三条多轮规格延期至 Schema 扩展后。人审通过后才能进入 W4.4 样本生成和 W4.6 案例审阅；以上产物仍不计盲评、不作为 gold 或模型质量证据。
- **新增可复跑规格验收：** `PolishRegressionSupplementSpecificationValidator` / `polish-regression-supplement-spec-validate` 校验父草稿、覆盖报告、规格包的哈希链，逐条检查必填字段与决定枚举，并要求类别规格数精确覆盖缺口。真实规格包返回 `valid=true`、30 条/10 类、0 issues，同时显式返回 `human_approval_recorded=false`、`generation_authorized=false`、阶段 0 贡献为 0；相关五组回归合计 **27/27 通过**，含哈希篡改拒绝。机器验收完成只减少手工核数和血缘错配，不能替代人的规格判断。
- **内容自审修订：** 逐条回看 30 条规格时发现前 4 条澄清样本原文并未明确要求缺失字段为必填，可能把“允许追问”变成“必须追问”。现已将绝对日期、活动地址/截止时间、采购数量/预算、预约确认所需人数/时刻写入用户要求和约束，并禁止系统猜测；刷新规格文件哈希后重新运行验收仍为 30 条/10 类、0 issues，相关测试仍 **27/27 通过**。这修正了草稿规格的歧义，不代表人审批准。
- **W4.5 隐私扫描复核：** 通过现有补样审阅验证器扫描 20 条案例的 input、context 与参考输出，`sensitive-content` 命中 **0/20**，阶段 0 贡献为 0。整份验证结果仍 `valid=false`、人工审阅 `0/20`，原因是审阅栏空白；此处只引用其隐私扫描结果，不把空白审阅误报为数据通过或人工批准。启发式零命中不是对所有隐私/密钥形式的完整证明。

### W5 内部草稿与盲评准入隔离复核（2026-10-05）

- **检查目标：** 确认 W4 AI 补样不会借助 blind-eval 数据通道进入正式评测。直接对 `cases.jsonl` 运行 `blind-validate` 时，命令在反序列化阶段就以 exit 2 拒绝第 1 行缺少 `semantic_family_id`；这是结构 Schema 拒绝，不据此声称来源授权检查已执行。
- **来源授权证据：** `BlindEvaluationAuditTests.Validate_RejectsInternalRegressionOrigins` 的三个参数化用例分别覆盖 legacy project synthetic、`ai_assisted_draft`、`human_authored_internal`；本次定向运行 **3/3 通过**，其中 AI 草稿来源被明确拒绝为盲评来源。正式来源许可和 W4 草稿标签不能互相替代。
- **结论与边界：** W5.3 的来源类型守卫已有直接回归保护，当前补样也无法按其原始 Schema 直接进入准入器；两者共同降低误混入风险。本步没有改数据/盲评配置，没有生成正式盲评记录；阶段 0 贡献仍为 0。下一步继续补独立授权盲评数据与人工证据，或推进不改变冻结数据的工具链检查。

### W4.4 逐案例生成溯源完整性检查（2026-10-05）

- **发现：** 现有 AI 补样 manifest 绑定了案例/提示文件哈希和批次日期，但案例未保存逐条模型 ID、提示哈希、随机种子及操作者；批次文件存在并不等于记录证明了实际生成过程。
- **实现：** 新增 `PolishRegressionSupplementLineageAuditor` 和 `polish-regression-supplement-lineage-audit`，检查 `generation_lineage` 必填字段、SHA-256/UTC 格式、`ai_assisted_draft` 来源、manifest 案例哈希及内部草稿/盲评禁用边界。只报告案例 ID 与问题类别，不改写历史数据。
- **实测：** 当前包为 20 条案例、20 条缺失完整逐条溯源；案例哈希匹配，阶段 0 贡献为 0 且 `not_admissible_as_blind_eval=true`。定向测试 **3/3 通过**（完整记录通过、缺项被拒、哈希篡改被拒）。
- **边界：** 不从会话上下文倒推或伪造模型/种子/操作者；这次审计不能补齐既往事实。未来新批次应在生成时写入字段，历史草稿继续保持未审阅、不可晋级状态。

### W4.3 人工规格审阅包准备（2026-10-05）

- **问题：** 30 条规格已经机器校验和哈希绑定，但没有便于逐条审阅、保留决策理由的交付件；直接在 JSONL 中编辑容易破坏结构和来源链。
- **实现：** 新增 `PolishRegressionSupplementSpecificationReviewPacketBuilder` / `polish-regression-supplement-spec-review-packet`，从源规格 JSONL 与 manifest 创建只读 CSV 模板、可填写副本、说明和来源哈希 manifest。输出目录采用新建语义，已存在时拒绝覆盖；审阅字段初始为空，包内明示不授权生成、阶段 0 贡献为 0。
- **验证：** 定向测试 **2/2 通过**，覆盖来源哈希绑定、空白人工决定及既有目录不覆盖。实际 30 条规格包已生成，CSV/说明哈希与 manifest 一致；这只完成评审材料准备，不计为人审或模型质量证据。
- **上下游：** 后续评审必须基于逐条规格事实与风险填写 approve/edit/reject 和具体理由；若编辑，保留原草稿并记录完整修订。审阅数据校验/导入之后再决定是否创建新样本批次，既有 20 条案例不会被自动更新。
- **验证器/导入器：** `polish-regression-supplement-spec-review-validate` 校验 CSV 覆盖和源字段快照、编辑 JSON、决定、理由及 UTC 时间，并重新执行 W4.3 源包的父数据/覆盖报告/规格哈希链校验；导入命令保留所有原规格和拒绝决定，只有全量 approve/edit 时才生成非空 generation-input。报告明确 `reviewer_identity_verified=false`，不把自报身份当作认证，不触碰正式盲评边界。
- **自审修正：** 初版把可填写 CSV 本身纳入固定哈希，导致填表后校验必然失败；初版包保留并标为作废。v2 区分只读模板与可填写副本，manifest 绑定只读模板，验证器逐字段对照源规格。v2 输出位于 `outputs/polish-regression-ai-supplement-spec-review-2026-10-05-v2/`。
- **导入器与实测：** 新增 `polish-regression-supplement-spec-review-import`，有效时创建新目录，完整保存源规格及所有决定；只有全量 approve/edit 才填充 generation-input。专项测试 **33/33 通过**；当前真实空白包报 30 规格、0 已审，拒绝导入且未创建输出目录，阶段 0 仍为 0。
- **哈希链自审：** 将父草稿及覆盖报告校验加入包生成、审阅验证和导入三个入口；即使单独改写源 manifest/规格也不能取得 generation authorization。新增覆盖该路径的 fixture 与回归断言。

### 多轮润色评测的顺序回放与可复现快照（2026-10-05）

- **推进内容：** 将 W4.3 规格中“多轮对话当前只是拼接在一个输入字段里”的架构缺口落实到评测数据契约和现有产品润色工作流：`BlindEvaluationRecord` 支持连续编号 turns 与 `conversation_id`；只允许 polish；顶层 `input` 必须与最后一轮一致；每轮输入都纳入隐私扫描及 train/dev 重叠检测。
- **产品链路：** `PolishRequest` 增加可选对话历史。没有历史时仍发送原始单轮输入；有历史时以 JSON 分离 `conversation_history` 和 `current_user_input`，system prompt 指定助手旧稿只是上下文、更新的用户更正优先。盲评执行器按轮调用完整产品工作流，并将此前 user/assistant 消息带入后一轮。配对评审材料按轮展示用户输入。
- **可复现性与 Schema：** `blind-eval.schema.json` 向后兼容地增加可选 `conversation_id`/`turns` 字段，要求成对出现、至少两轮、每轮字段封闭，并约束为 `polish`；连续编号和 `input == 最后一轮.user_input` 由准入校验器执行。快照逐轮收录系统提示和 user message；前序助手成稿使用注明运行时替换的占位文本。旧单轮样本继续使用既有快照形态。
- **测试：** 多轮 Schema/重叠检查、Schema 文件声明、对话提示隔离、顺序执行、快照占位及早期轮次哈希覆盖测试均已覆盖；审计、快照、工作流、候选运行、配对包和提示构建回归 **125/125 通过**。另一次全量测试（最后两项 Schema/哈希用例加入前）在工作区临时目录运行 **1284 通过、11 跳过、4 失败**；4 项是发布/交付测试对当前安装脚本和打包实现的既有期望不匹配，与本次多轮实现无关。不能据此宣称模型质量或个性化效果已有提升。
- **数据状态：** 现有 30 条规格和 20 条 AI 草稿案例未改写；其三条“多轮”规格仍是待人审的自然语言描述，并未转换为真实 turns。此代码支持新增正确的多轮评测样本，但不替代规格审阅，也不把内部样本计入正式盲评。
- **人工副本交叉核对：** 当前 W2.3 副本有 19/19 条决定、reviewer 和时间，但 rationale 全是占位语“无非空理由”，且 19 条都选 `same_semantic_family`，与项目已确认的“事实骨架 × purpose”族定义及现有逐对证据冲突；导入器因此拒绝。W3.3 工作簿 16 个族的首轮 reviewer/时间/理由与族级裁定字段均为 0/16；W4.3 规格副本为 0/30 决定。以上只报告当前文件状态，不把自报 reviewer ID 认证为真实身份，也不由 AI 补写成已完成的人审。
- **后续接续：** 多轮 Schema 与回放能力已具备；规格评审副本完成后，可按人工裁定将批准的多轮规格整理为 `turns` 结构并生成新草稿版本，再为每轮成稿补齐逐条生成溯源。现有文本转录不能被自动改造成历史模型回复。

### 多轮协议 v3.1 收尾（2026-10-05）

- 将多轮 `conversation_id`/`turns` 的兼容扩展标为准入协议与 Schema v3.1；策略哈希纳入配对字段、仅 polish、至少两轮、连续序号、末轮输入匹配及逐轮隐私/重叠规则。单轮记录形态和配额不变。
- 新增 `annotation-handbook-v1.3.md`，解释如何结合逐轮用户指令和助手草稿评审，明确当前指令优先、旧助手稿不是事实来源；新增冻结规程 `dataset-freeze-policy-v1.1.md`。更新运行清单模板至 3.1 及目录说明，v1.0/v1.2 历史文件保留。
- 验证：盲评准入、Schema、快照、顺序回放、候选运行、匿名成对包、提示构建和证据清单 validator 定向测试 **148/148 通过**；包含 v3.1 Schema 声明、版本标识与策略哈希相关用例。新增/更新的协议、Schema、手册、模板和状态文档已做尾随空白扫描，无命中。此契约升级只提高多轮任务的表达与回放能力，不代表生成质量已有提升。


### 阶段 1 P2：Anthropic / Gemini 原生结构化输出映射（2026-10-05）

| 项目 | 内容 |
|---|---|
| 问题 | `AIService` 已实现 `IStructuredTextGenerationClient`，但 Anthropic Messages 和 Gemini GenerateContent 的 `GenerateStructuredAsync` 会退化成普通文本请求；调用方提供的 Schema 没有到达 Provider。OpenAI-compatible 分支已有 `response_format` 映射。 |
| 输入 | `AIService.CreateRequest` 当前协议分支；真实 `HttpMessageHandler` 请求捕获测试；Anthropic 与 Gemini 官方 REST 文档。未发起真实 API 调用，也未发送用户数据。 |
| 方案 | 保持原有 Chat/OpenAI-compatible 形态；Anthropic 使用 `output_config.format={type:json_schema,schema}`；Gemini 使用 `generationConfig.responseFormat.text={mimeType:application/json,schema}`。只在调用结构化接口且非连接测试时加入 Schema；普通文本请求不携带新字段。 |
| 产出 | 修改 `Services/AIService.cs` 的两个协议序列化分支；`Huaxiazi.Tests/AIServiceTests.cs` 新增两个请求契约用例，并断言普通文本调用不误带结构化字段。 |
| 验收 | 先运行新测试观察到 2/2 失败，失败点为请求缺少协议字段；完成适配后，AIService、遥测、结构化工作流和 DatasetBuilder 关联测试 **102/102 通过**。测试通过捕获实际序列化 HTTP 请求验证字段位置、Schema 内容和普通文本兼容性。 |
| 上下游 | 该映射是阶段 1 Provider 适配基础，不改变当前润色/提示词优化界面链路；源码搜索确认两个产品工作流尚未调用 `StructuredGenerationWorkflow`。下一步应补 Provider/model 级 `ProviderCapabilities` 与 unknown 策略，再统一两个工作流的输出契约，避免把协议字段映射存在误称为质量提升或每个模型均支持。 |
| 量化影响 | 两种原生协议从“Schema 未发送”修正为“按官方协议发送”；本轮 API 调用次数为 0，质量、时延和费用测量均为 0，故不能宣称实际成稿质量或 Schema 合法率已提升。 |

依据：Anthropic [Messages API](https://platform.claude.com/docs/en/api/messages/create) 与 [Structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)；Gemini [GenerateContent API](https://ai.google.dev/api/generate-content) 与 [Structured outputs](https://ai.google.dev/gemini-api/docs/generate-content/structured-output)。

### 阶段 1 P2：精确模型能力映射与设置说明（2026-10-05）

- **范围：** 在现有 Anthropic/Gemini Schema 协议映射之后，增加精确 model + provider platform + protocol 的参数能力记录，并把同一模型别名解析规则用于请求与设置界面。覆盖 OpenAI `gpt-6-astra`，Anthropic `claude-opus-4-7` / `claude-opus-4-8`（含带快照后缀 ID）；OpenAI GPT-6 Astra 停止发送 `temperature` / `top_p`，改用 `max_completion_tokens` 并按低/中/高发送 `reasoning_effort`；Anthropic Opus 4.7/4.8 停止发送这两项采样参数，并按低/中/高映射 `output_config.effort`。
- **边界：** 能力只绑定官方平台、指定协议与精确模型 ID，不因模型名相同而应用到 OpenRouter 或自定义兼容服务。其余模型暂时保留历史兼容参数行为，界面明确显示“尚未核验”；本次没有配置迁移，旧配置和手动模型选择保持不变。模型能力表仍是小范围白名单，不代表已覆盖全部供应商和模型。
- **用户可见变化：** Provider 设置的推理强度下方新增能力说明；修改模型、推理强度或实时编辑别名映射时同步更新。自定义推理参数档明确说明不会发送推理级别字段。
- **验证：** 新增设置摘要断言；`SettingsViewModel.ProviderCapabilitySummary`、`AIServiceTests` 与设置 UI 合约相关筛选共 **106/106 通过**。HTTP 捕获测试验证实际发送字段；没有真实 API 请求或用户内容。
- **效果与后续：** 已消除这三个精确模型 ID 上已知不支持参数的发送，并使 OpenAI 推理级别和 Anthropic Opus effort 参数生效；尚无线上拒绝率、质量、成本或延迟对照数据，不能据此宣称成稿质量已提升。接下来继续扩充有官方证据的模型能力项及映射测试，再推进两个业务工作流共用生成契约；外部评测集和人审任务仍按原状态独立推进。

依据：OpenAI [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra)、[API 更新日志](https://developers.openai.com/api/docs/changelog)、[Chat Completions API](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)；Anthropic [Messages API](https://platform.claude.com/docs/en/api/messages/create)、[Effort 参数](https://platform.claude.com/docs/en/build-with-claude/effort) 与 [Release Notes](https://platform.claude.com/docs/en/release-notes/overview)。

### 阶段 1 P2：结构化契约与 Provider Schema 对齐（2026-10-05）

- **发现与修正：** `StructuredGenerationWorkflow` 接收 `StructuredOutputContract`，但原生 API 请求始终发送固定的 `answer` 单字段 Schema，契约里的其它 required/allowed fields 被忽略；本地校验也没有约束附加字段的值类型。现已让 Schema 从契约的允许字段、必需字段生成，关闭额外字段，并将所有已提供字段校验为字符串。`answer` 必须允许且必需，所有 required 字段必须属于 allowed 集合。
- **验证：** 新增回归先失败，显示 Schema 缺失契约字段；修复后针对 AIService、能力设置页、结构化输出/工作流、推理预设及本地运行关联筛选 **179/179 通过**。当前只有泛型契约和 Schema 序列化闭环；两个产品工作流尚未切换到该共用生成流程。
- **限制与下一步：** 当前契约字段类型统一为字符串，且跨 Provider 的严格 JSON Schema 子集仍需逐家适配；这一步没有声称已接通润色和提示词优化，也没有真实 API 或用户文本调用。继续推进时先定义润色结果的字段/决策契约与提示词优化输出契约，再分工作流迁移，保持旧文本输出回退路径。

### 阶段 1 P2：两条产品工作流接入结构化生成（2026-10-05）

- **子任务输入：** 现有润色协议（`kind/content/scenario/topic/questions`）、提示词优化的自由文本答案、两条工作流原有 `ProfessionalQualityValidator`、provider 原生 JSON Schema 接口与此前建立的通用生成器。
- **实现产出：** `StructuredOutputContract` 增加 `StringArray` 字段类型及字符串枚举；结构化 Schema 会明确声明允许字段、字段类型、`kind` 的 `final|needs_clarification` 枚举和 `questions` 数组，OpenAI strict 所需的全字段 `required` 通过可空类型表达语义可选字段。本地校验同步验证 required/allowed/type/enum/长度/meta echo。Provider 返回 HTTP 400 拒绝 Schema 时，同一次生成任务转普通文本请求并以相同 Schema 做本地校验和修复。
- **润色工作流：** 首次响应走 `polish-response` 契约，格式或业务决策无法解析时只做一次结构修复；随后继续既有事实保真检查和一次质量修复。提示词现在明确五个字段，最终稿要求 `questions: []`，澄清要求至多三个字符串问题。归档只在有效 final 且原设置允许时发生。
- **提示词优化工作流：** 原生结构化模式把成品提示词放入唯一 `answer` 字符串，工作流取回 answer 后继续原质量校验。结构错误的契约重试上限设为 1，外层允许一次质量修复，避免“结构重试两次 + 质量修复再重试两次”的重复调用。
- **兼容/边界审查：** 没有实现原生结构接口的文本客户端仍走普通文本路径（润色仍使用本地 JSON 解析/约束提示）；HTTP 400 Schema 拒绝会退到普通文本。`LocalTextGenerationClient` 现将 HTTP 400 规范化为 `GenerationFailureException(RequestRejected)`，因此本地 llama.cpp Schema 不兼容也能触发同一回退。`EmotionAssistant` 模式的情绪标记目前仍在 JSON 外，继续走旧协议，因此该模式尚未纳入本原生 Schema 闭环，需要后续把情绪元数据改为契约字段再迁移。旧配置未变更。真实 Provider 请求费用及各平台更深层 Schema 限制仍未实测。
- **验证：** 新增序列客户端与 Schema 捕获客户端测试，覆盖原生请求、Schema 数组/枚举、云端/本地 HTTP 400 后文本回退、本地验证、业务解析修复和提示词优化输出拆包。最新合并筛选覆盖 AIService、Provider 能力设置、本地运行、结构化契约、两条工作流、提示词构造与表情兼容共 **226/226 通过**；本轮真实 API 请求数为 0。
- **量化判断：** 结构化输出适用范围从“共用生成器未被业务调用”扩大至默认润色和提示词优化路径；每条工作流的修复生成次数限制为最多 1 次，减少了无限或重复重试的费用风险。质量收益、Schema 合法率、请求延迟和 Token 变化没有真实样本或 Provider 数据，当前不可量化；供应商拒绝 Schema 时多一次失败请求是回退代价。
- **偏差纠正与衔接：** 这一阶段从自由文本契约扩展为类型化业务字段，属于按实际响应形状修正原通用 answer-only 设计；没有改动数据集来源或评测门槛。下一项继续把 `EmotionAssistant` 标记纳入同一输出协议，再做 OpenAI/Anthropic/Gemini 严格 Schema 实际请求适配与更广参数能力目录；结构化工作流接通后，盲评集和人工样本状态仍必须真实补齐，不能用单测替代质量证据。

参考：[OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)、[Gemini Structured Output](https://ai.google.dev/gemini-api/docs/structured-output)、[Anthropic Structured Outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)。

### 阶段 1 P2：EmotionAssistant 元数据并入结构化契约（2026-10-05）

- **问题与约束：** 旧表情协议把 `<HUAXIAZI_EMOTION>...</HUAXIAZI_EMOTION>` 放在业务 JSON 之外，无法通过 JSON Schema strict。表情强度是 0–1 的辅助 UI 元数据，不能因缺少提示而阻断正文；同时必须继续支持旧文本客户端。
- **实现输入/输出：** 润色响应与提示词优化响应在 `EmotionAssistant` 且客户端支持结构化生成时，额外声明 `companion_emotion`（6 个枚举值）与 `companion_intensity`（number，0–1）；普通模式 schema 不增加字段。系统提示要求情绪字段写在同一对象，不再要求追加对象外标签；取回 hint 后内部转回旧标记解析路径，UI/成稿仍只收到业务文本和 `AssistantEmotionHint`。
- **路径取舍：** 情绪字段按 schema 必含属性发送以兼容 strict provider，但类型允许 `null`、本地合同不把情绪设为必需；模型未给有效提示时保留正文、hint 为空。文本客户端或非结构化测试客户端继续使用旧后缀协议，避免旧接入被强制改造。
- **参数/代码：** `StructuredOutputFieldType.Number` 与 `StructuredOutputNumericRange` 支持数值类型和本地边界校验；强度上下限固定为 **0 与 1**，来源是既有 `AssistantEmotionHint` 的 UI 映射约定（读取时原已 clamp 到 `[0,1]`），情绪枚举复用 `AssistantEmotionKind`，不建立第二套值表。**原生跨 Provider JSON Schema 只声明 `number`，不发送 `minimum` / `maximum`**：最新 Anthropic 原始结构化输出文档明确数值约束不受支持，而 Anthropic SDK 才会移除这些约束后放入 description；本应用直接发送 REST JSON Schema，因此范围留在本地强制，以避免每次触发 400 再文本回退。更新 `AssistantEmotionProtocol`、两个工作流与 Schema validator。
- **验收：** 新增润色/提示词优化原生结构客户端用例，验证字段 Schema、情绪拆包、强度边界、正文无后缀污染；原有文本客户端情绪后缀用例仍覆盖旧路径。AIService、Provider、本地运行、结构化工作流、两条业务工作流、设置与表情兼容的合并筛选 **229/229 通过**；真实 API 请求数为 0。
- **效果/风险/下一步：** `EmotionAssistant` 的原生结构化路径现在与业务输出共享一个 JSON 对象，并且强度越界会被本地拒绝。每次该模式请求仅增加两个小型元数据字段；实际 token、时延、成稿质量收益仍未实测。下一步按官方协议对 OpenAI/Anthropic/Gemini 与 llama.cpp 的真实 Schema 载荷差异补 fake-handler 契约测试，再扩大精确模型能力目录；文本-only 兼容路径仍不提供 schema 保证。

#### 依据更新：Provider 数值约束差异

- OpenAI Structured Outputs 支持 number `minimum` / `maximum`；Gemini Structured Output 也支持数值最小/最大值；Anthropic raw Structured Outputs 明确不支持数值约束。因此共享生成器不再把 `NumericRanges` 编入通用传输 Schema，仅在客户端本地校验 0–1，并在 system prompt 说明值域。Anthropic 还提示 enum 输出可能改变大小写，故 `kind` 与情绪枚举在本地按大小写不敏感比较，避免合法语义因首字母大小写触发多余修复。[OpenAI](https://developers.openai.com/api/docs/guides/structured-outputs) · [Gemini](https://ai.google.dev/gemini-api/docs/structured-output) · [Anthropic](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)
- **偏差自查：** 本轮最初把本地边界约束直接序列化进 Provider Schema；对 Anthropic 这会导致每次模式请求先失败再回退，违反延迟/失败请求约束。已修正为 provider-neutral 的本地值域校验，降低重复拒绝；各 Provider 的差异化 Schema 编译仍列为下一步，不声称传输层已保证 0–1。

### 阶段 4：本地运行时启动策略与失败恢复（2026-10-05）

- **实现：** `LocalRuntimeManager` 现在按用户 GPU 设置产生候选顺序：`Auto` 且 Vulkan/CPU 均存在时先 Vulkan、后 CPU；`Off` 只允许 CPU。Vulkan 进程启动或健康检查失败后会清理进程并尝试 CPU；用户取消不会触发回退。若 CPU 也启动失败，保留 CPU 的原始异常供现有错误处理展示。
- **验证：** 新增候选顺序、Vulkan→CPU 回退、取消不回退及最终异常保留用例。测试输出目录此前有一项仍尝试使用系统 Temp，当前身份无权创建；夹具改用项目输出目录后，`LocalRuntimeTests` 与 `RuntimeDeliveryRegressionTests` 合并筛选 **12/12 通过**。
- **边界：** 当前 checkout 不含 CPU/Vulkan `llama-server.exe`，因此只验证候选选择/回退逻辑，未运行实际服务器、GGUF、Vulkan 驱动或目标 Windows 硬件。本项目仍缺运行时独立下载/版本清单与设置页实际后端状态展示；CPU 回退不等同于运行时交付已完成。
- **下一步：** 对照官方 llama.cpp 发布产物核定 Windows CPU/Vulkan 包的版本、架构、许可与校验来源，再实现独立运行时包清单/安装状态，并将最终采用的后端和回退原因呈现在本地模型状态中。主安装包继续保持不内置运行时。

### 阶段 4：运行时目录可写性与上游候选制品身份（2026-10-05）

- **问题与约束：** 运行时管理器、设置页和健康检查此前默认指向 `AppContext.BaseDirectory/runtimes/local`。Windows 正式安装到 Program Files 时普通用户无写权限；继续在此路径实现运行时下载会造成安装失败或错误引导提升权限。当前要求是单独下载、按用户隔离，并且不破坏可能存在的旧便携部署。
- **实现：** 新增 `LocalRuntimePaths`，默认安装根为 `%LOCALAPPDATA%/Huaxiazi/runtimes/local`；若新目录已有 CPU/Vulkan runtime 则优先使用，否则发现旧应用目录 runtime 时兼容读取，两个位置都没有时仍返回新的用户可写根。`LocalRuntimeManager`、设置页运行时可用判断和 `HealthCheckService` 共用该解析函数。
- **验证：** 新测试覆盖默认选新根、只存在旧 runtime 时兼容、两个根均存在时新根优先。LocalRuntime、RuntimeDeliveryRegression、HealthCheck、BlindCandidateClientFactory 定向筛选 **26/26 通过**。扩大筛选中暴露的 HealthCheck 测试 fixture 仍写不可访问的系统 Temp，已移至测试输出目录后重跑通过。
- **上游调研：** [llama.cpp b11424](https://github.com/ggml-org/llama.cpp/releases/tag/b11424) 当前标记为 prerelease。官方 API 与 [构建证明](https://github.com/ggml-org/llama.cpp/attestations/52864580) 给出的 Windows x64 CPU/Vulkan ZIP 摘要分别为 `d613ef…efacb5c7`（19,394,018 bytes）和 `97de9a…8f146e9`（33,332,009 bytes）。证明中的构建源码 commit 为 `c06f841…`，tag 指向 `6c59c4…`，Compare 显示相差 3 个提交；清单必须分开绑定发布标签与被证明的源码提交。仓库主 LICENSE 为 MIT，但 ZIP 内第三方依赖的许可证/NOTICE 尚未检查。
- **当时结论及后续：** 在该条记录写入时尚未下载或启动二进制；紧接着的“候选 ZIP 实物检查”已补做摘要、attestation、归档清单、许可证文件和 `--version` 检查。`b11424` 仍只作为 prerelease 候选，不能作为稳定推荐版；真实健康端点、模型生成和硬件矩阵尚未运行。详细证据见[运行时制品核验记录](../local-runtime-package-inventory-2026-10-05.md)。

### 阶段 4：候选 ZIP 实物检查与 provenance 自审更正（2026-10-05）

- **输入：** 官方 b11424 Windows x64 CPU/Vulkan ZIP；GitHub API digest/size、attestation bundle、发布与构建 workflow 源码；本机 `gh attestation verify` 和 `llama-server.exe --version`。
- **结果：** 两个资产下载字节 SHA-256 均与官方摘要一致，attestation 在限定仓库、signer workflow 和 `refs/heads/master` 后分别 exit 0。CPU ZIP 为 51 entries / 49,485,085 解压字节，Vulkan 为 52 / 94,790,429。两包 `llama-server.exe` 都是 9,216-byte launcher，真实实现依赖同目录 `llama-server-impl.dll`；Vulkan 多含 `ggml-vulkan.dll`。安装时必须完整保留归档文件，不能只复制 EXE。两个 launcher 的 `--version` 都成功返回 0.5.0-dev / build 11424 / commit `6c59c4007`，Clang 20.1.8 / Windows x86_64。此操作没有加载模型或启动 HTTP 服务。
- **许可复核：** 两包包含 `LICENSE-LLVM-OpenMP`，且有 `libomp.dll`；归档未包含 llama.cpp 根 MIT `LICENSE`。此前“ZIP 无许可证文件”的表述不准确，现已识别出 OpenMP notice；与此同时不能把这一份 notice 当作完整组件许可清单。应用打包前需附上 llama.cpp MIT 正文，并复核 Vulkan 和其它动态依赖的许可/NOTICE。
- **provenance 自审：** attestation 52864580 的 source dependency 是 `c06f841…`，而实测 launcher 报告 commit `6c59c4007`。源码显示 `release-publish.yml` 是 `workflow_run` 发布器，它下载前序 Release workflow 的 artifacts，再对 `release/*` 生成 attestation；因此 `gh attestation verify` 的成功证明资产 digest 与发布 workflow 声明有效，不足以单独证明 compiler checkout/source commit。计划和核验记录已修正为 `compiler_source_commit=unverified`，不能把 `c06f…` 当成实测二进制编译提交。下一步应从对应原始 Release Actions run 取证，或在本项目受控构建并自行生成签名清单。
- **下一步与接口影响：** 实物结构已知后，可先实现与来源版本解耦、只接受应用内 pin 过 SHA/size/架构/flavor 的安全 ZIP 安装器；候选 UI 须显式标记 prerelease 和 source commit 未映射，不能默认安装或作为稳定版本。下载器还需在实际使用时验证完整包 hash、路径穿越/解压字节上限、取消与续传。此步骤没有主项目产品生成质量指标变化；已量化收益是 runtime 包身份检查和入口兼容从未验证转为 CPU/Vulkan 启动器实测，模型质量、推理速度、健康端点仍未验证。

### 阶段 4：独立运行时包安装、管理和路由接入（2026-10-05）

- **输入：** 固定的 b11424 Windows x64 CPU/Vulkan ZIP、各自官方 SHA-256/大小、已验证的发布 attestation、解压目录大小，以及 ZIP 内的 `LICENSE-LLVM-OpenMP`。MIT 文本独立随应用发布。官方 Release API 当前仍列出 b11424；它是 prerelease，不升级成默认稳定版。
- **实现：** 新增 `LocalRuntimePackageCatalog` 固定包清单和 `LocalRuntimePackageService`。只接受清单中的 URI/摘要/大小；下载支持 Range 续传、取消保留 partial、完整 SHA-256 校验、磁盘余量检查和最多三次受信任 HTTPS 重定向。ZIP 解包限制条目数与累计字节数，并拒绝绝对路径、目录穿越、重复路径和符号链接；检查 server launcher、实现 DLL、核心 GGML DLL、OpenMP notice 及 Vulkan 后端库。暂存包在激活前实际运行 `llama-server.exe --version`，并校对输出 build commit。安装到 `%LOCALAPPDATA%/Huaxiazi/runtimes/local/<flavor>/versions/b11424`，通过临时文件原子更新 `current.json`，CPU/Vulkan 独立并存；保留旧的 `<flavor>/llama-server.exe` 目录读取兼容。
- **版本与来源：** 安装 manifest 同时记录 release 标签、下载包 SHA/大小、exe 报告 commit、attestation source commit、是否 prerelease 和 `CompilerSourceVerified=false`。UI 明示 b11424 prerelease 与 compiler source 未核实；用户显式点击后才下载。卸载按钮先停止应用托管的本地服务，再移除选定包和 current 指针。
- **路由接入：** 本地运行时候选解析改为读取版本指针；CPU/Vulkan 仍按既有策略排列。运行时复用 key 现在含实际 exe 路径，换运行时版本时不会误复用旧进程。主安装包仅增加小型 MIT 文本，不附加运行时 ZIP。
- **验证：** 主 WPF 项目 `dotnet build --no-restore` 成功，0 warning / 0 error。运行时包、候选路由、目录兼容和健康检查筛选 **28/28 通过**；设置界面/WPF smoke 和完整关联筛选此前为 **84/84 通过**。测试覆盖安装/manifest/current 指针、SHA 不匹配拒绝、ZIP traversal 拒绝、Range 续传和版本探测失败时拒绝激活。已确认主程序输出包含 MIT 许可文本且不包含 llama.cpp 运行时二进制。b11424 原始 ZIP 的摘要、解包体积和 launcher `--version` 已单独实测；本轮未在用户 LocalAppData 实际安装包、启动健康端点、加载 GGUF 或测硬件性能。
- **仍需推进：** 当前完成的是“下载与安装链路 + 可执行路径接入”，还需验证完整生产 ZIP 的服务启动/健康端点和目标 Windows 驱动差异；依赖许可清单仍需覆盖 Vulkan 及所有动态依赖。源码 provenance 未核实，候选继续以显式预览包提供。该实现没有模型质量、Schema 或盲评指标变化，也没有云端 API 请求。

### 阶段 4：本地运行时分层健康检查与用户触发模型加载（2026-10-05）

- **问题：** 启动健康检查原先只确认 `llama-server.exe` 文件存在，就把本地 Provider 报为可用；文件存在不能证明二进制能启动。反过来，若应用启动时自动加载 GGUF，会延长启动并占用大量本机内存。
- **输入：** 管理的 CPU/Vulkan runtime 候选、运行时版本 manifest、当前安装模型与 Provider 配置；未改变模型文件、Provider 选择或云端路由。
- **改动：** 启动健康检查现在执行所选候选的 `--version`，检查退出码、版本输出，并对有受管 manifest 的包匹配 build commit；每个候选设置 5 秒上限，最多探测 Vulkan 与 CPU 两项，防止损坏的进程拖住应用启动。Vulkan 探测失败时沿用既有策略检查 CPU 回退，整条本地路径不发 HTTP 请求。受管本地 Provider 归入 Local health scope，汇总分母按实际 Local 项目数计算。
- **用户触发检查：** 每个已安装模型提供“启动并检查服务”操作，复用已保存的本地推理参数/适配器，调用 `LocalRuntimeManager.EnsureStartedAsync` 并等待 loopback `/health` 成功；显示实际 CPU/Vulkan 后端与启动耗时。该动作显式告知用户会加载 GGUF、使用本机内存、不调用云端；支持取消未就绪进程和停止服务释放进程。日常启动仍只做轻量版本探测，不自动加载模型。
- **验收：** health check 定向测试证明可执行探测成功后报 Local healthy、探测缺失时报 Local failure，且两者均不触发外部 HTTP；WPF 页面及运行时/健康检查关联筛选 **85/85 通过**；主 WPF 项目构建 0 warning / 0 error。CPU/Vulkan 实际候选 `--version` 输出均匹配固定 build 11424 与 commit `6c59c4007`。
- **边界与下一步：** 单测用注入的探测器覆盖健康分类，不等价于实际 GGUF 启动成功。当前默认受管模型目录没有 registry 或 GGUF 文件；因此尚未实测生产 ZIP 的 HTTP `/health`、模型加载时间、峰值内存/tokens/s，也未在硬件矩阵测 Vulkan 驱动兼容。运行时 manifest 和安装器已就位；导入许可的测试 GGUF 后，可点击 UI 操作直接完成真实服务验证。尚未触及 LoRA/SFT，也没有模型质量盲评数据变化。

### 阶段 4：真实本地生成与推理参数生效（2026-10-06）

- **实现修复：** `LocalTextGenerationClient` 此前没有把已配置的 `LocalRuntimeOptions.Seed`、`RepeatPenalty` 放进聊天请求体；虽然它们参与 runtime key，因而会导致不必要的服务重启，却不会改变采样。本轮先添加请求边界回归用例并观察到 `seed` 缺失而失败，再把规范化后的值作为 `seed` 和 `repeat_penalty` 发送。`LocalRuntimeManager` 同时开始持续排空重定向的 stdout/stderr 且不保留内容，防止 verbose 启动日志填满管道而卡死模型启动，也避免把提示或生成文本留在诊断记录中。
- **真实运行样本：** 使用官方 `Qwen/Qwen3-1.7B-GGUF` 的固定 revision `7fb011e9aee6e4dc7adf8430df9ea8de6a466aa3`、`Qwen3-1.7B-Q4_K_M.gguf`（1,107,408,544 bytes，SHA-256 `228fb5627f7510b8b3516cdb6435e4b0d2a2bf330fe5b0ab19284a3570a8bb1f`），以及 llama.cpp b11424 CPU/Vulkan runtime，在 NVIDIA RTX 4060 Laptop（4 GB VRAM，系统内存约 23.8 GB）运行。模型放在 `out/test-artifacts` 独立临时仓库，不触碰用户正式模型目录；生成请求由应用的 `LocalRuntimeManager` 和 `LocalTextGenerationClient` 发给 loopback OpenAI-compatible API，没有云端调用。
- **结果：** Vulkan 自动档完成真实模型加载、`/health` 就绪和 HTTP 200 中文生成，生成耗时约 4.5–5.2 秒、进程峰值工作集约 2.28 GB。参数 `seed=42` 与 `repeat_penalty=1.17` 由应用请求成功发送；同 seed 的两次 Vulkan 输出不同（“明天上午10时召开会议。” / “明早十点召开会议。”），说明当前不能把 Vulkan 固定 seed 当成逐字可复现保证。CPU-only 档同样真实生成；在 `/no_think`、256-token 上限和相同参数下，两次输出都为“明早十点召开会议。”，耗时约 2.56 秒与 0.47 秒，进程峰值工作集约 2.34 GB。CPU 第二次请求时间不代表冷启动或普遍性能。以上每档仅一组重复烟测，不是吞吐/质量基准，也不能据此判定 Vulkan seed 的原因或 CPU 正式可复现范围。
- **其它修复与验证：** 先前发现 stdout/stderr 虽已重定向却没有排空，现已用真实 Vulkan/CPU 进程确认不再阻塞。新增 `GenerateAsync_SendsConfiguredSeedAndRepeatPenalty`：修改前按预期因缺少 `seed` 失败，修改后断言 HTTP JSON 中的两个字面值正确。LocalRuntime、HealthCheck、运行时包、候选交付和盲评客户端工厂关联筛选 **33/33 通过**；`dotnet build Huaxiazi.csproj --no-restore` 为 **0 warning / 0 error**。
- **保持加载开关：** 新增本地运行时停止契约。`KeepLoaded=false` 时，无论生成成功、失败还是被取消，客户端都会在响应处置后调用 `Stop()` 释放受管模型；`KeepLoaded=true` 不主动停止，后续请求可以复用进程。开关行为由边界测试验证，但尚未计量反复加载的延迟/内存权衡。
- **剩余事项：** Vulkan 同 seed 不同结果需要扩大重复样本并检查具体 runtime/GPU 可复现属性；不能因此称参数未生效，因为请求本身已发出。1.7B 与官方 4B 都只是本机功能烟测，不能替代盲评或目标硬件矩阵。运行时 b11424 仍为 prerelease；2026-10-06 已依据 Release run #4773 与发布 artifact lineage 将 compiler source provenance 映射为 `6c59…`（详见 runtime inventory），但完整依赖许可清单/NOTICE 和干净 Windows 验收仍待完成；阶段 0 授权外部盲评、阶段 5 LoRA/SFT 均尚未完成。

### 阶段 4：官方 Qwen3-4B 交付条目与业务链实机烟测（2026-10-06）

- **目录更新：** 内置模型目录升至 `builtin-2026.10`，保留原有模型项并新增官方 `Qwen/Qwen3-4B-GGUF` Q4_K_M。固定 revision `bc640142c66e1fdd12af0bd68f40445458f3869b`，文件大小 **2,497,280,256 bytes**，SHA-256 `7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5`，许可 Apache-2.0，runtime 声明 b11424。模型卡列出 4B 参数、32,768 原生上下文和约 2.5 GB Q4_K_M 文件。[官方模型卡与文件版本](https://huggingface.co/Qwen/Qwen3-4B-GGUF/tree/bc640142c66e1fdd12af0bd68f40445458f3869b)
- **下载/导入复核：** Hugging Face 直连超时后，测试下载改用镜像解析到的 Hugging Face Xet 内容存储地址；HTTP 返回的 repo commit、文件字节数和 LFS ETag 与官方固定 revision 一致。文件下载完成后再次本地计算 SHA-256，与目录值完全一致；再经 `LocalModelStore.ImportModelAsync` 导入独立 `out/test-artifacts` 测试仓库，导入器复核哈希一致。未使用用户正式目录，未发云端生成请求。正式应用的模型 URL 仍指向固定 revision 的官方 Hugging Face 地址，并限制重定向主机；镜像只用于此次临时验证。
- **设备/运行时：** Windows、NVIDIA RTX 4060 Laptop GPU（4 GB VRAM）、20 logical processors、约 23.8 GB 系统内存，runtime b11424 Windows x64 CPU/Vulkan。`LocalRuntimeManager` 在 Vulkan 自动档真实启动 `/health` 并生成：启动就绪 **5.364 秒**、生成 **5.781 秒**、进程峰值工作集 **3,221,204,992 bytes**；CPU-only 档就绪 **3.388 秒**、生成 **4.922 秒**、峰值工作集 **4,958,167,040 bytes**。工作集不等于显存，未采集 Vulkan 显存占用；每档只有单次直接生成烟测。
- **产品工作流：** CPU-only 档实际经过 `LocalTextGenerationClient`、结构化 JSON Schema 请求、`PolishWorkflowService` 和 `ProfessionalQualityValidator`，端到端 **31.797 秒**，返回 `kind=Final`、`WasRepaired=false`、无质量告警，成稿为“明天上午十点召开会议，请大家准时参加”。同一任务原文为“明天上午十点开会，请大家准时参加。”这只证明这条请求链路可以跑通，不能替代盲评的事实保留/直接可用率测量。简单自由文本烟测曾输出“会议将于 tomorrow 上午十点召开”，暴露量化模型可能中英混杂；它提示需要纳入语言一致性质量切片，不能单凭结构化样本判模型稳定。
- **运行时依赖盘点：** 对实际 PE 导入表解析确认 server launcher 依赖同包 `llama-server-impl.dll` 和 Windows CRT；实现 DLL 依赖包内 llama/ggml DLL、Windows 系统 DLL、VCRUNTIME/MSVCP；Vulkan backend 还导入系统 `vulkan-1.dll`。本机 System32 存在这些 Windows/Vulkan DLL，b11424 ZIP 自身带 `libomp.dll` 与 `LICENSE-LLVM-OpenMP`，但没有随包携带所有系统运行库或显卡驱动。健康检查的 `--version` 探测可发现部分缺失依赖，正式发行仍需在干净 Windows 镜像验证安装前置条件，并完成第三方许可/NOTICE 逐项核对。b11424 源码 SHA 映射已由公开 Release run/artifact lineage 补齐；这不替代可复现构建证明。
- **验证与结论：** 模型目录、下载包/运行时、健康检查和盲评候选工厂筛选 **43/43 通过**；WPF `dotnet build --no-restore` **0 warning / 0 error**。4B 可在本机 CPU 和 Vulkan 路径启动，CPU 结构化润色完成，但中英混杂样本显示质量不足以从一次成功请求推导可发布。下一步优先运行隔离、冻结的中文语言一致性/事实保留切片，并在更多输入上比较同一模型 CPU 与 Vulkan；本烟测的模型不是盲评集，当前仍无阶段 0 授权外部盲评结果。

### 阶段 4：Qwen3-4B 内部开发切片与事实校验器误拒修复（2026-10-06）

- **执行范围：** 固定模型 `Qwen3-4B-Q4_K_M`（revision `bc640142c66e1fdd12af0bd68f40445458f3869b`，SHA-256 `7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5`）、llama.cpp b11424 prerelease、temperature 0.4、top_p 1.0、max_tokens 2048、seed 42、repeat_penalty 1.17、ctx 4096、batch 512。仅取 v1 development split 的 4 行；运行器读取白名单字段，不读取参考成稿、不使用 regression split、不请求云 Provider。4 行只有 **2 个不同输入骨架**，样本均为未完成人工核验的内部合成数据，phase 0 贡献为 0。
- **修复前配对烟测：** 同一工作流分别 CPU 和 Auto/Vulkan 共 8 次。CPU：`000007` 73.728 秒 Invalid/2 issues；`000014` 86.619 秒 Invalid/1 issue；`000023` 24.567 秒 Final；`000005` 29.449 秒 Final。Vulkan：`000007` 38.010 秒 Final/已修复；`000014` 16.697 秒 Final/已修复；`000023` 7.068 秒 Final；`000005` 4.810 秒 Final。每例仅一次，两个输入骨架被重复跑；修复次数会改变路径，不能把这些耗时当性能比较或发布 p50/p95，也不应把 Final 率当质量分。
- **根因证据：** 捕获到 CPU `000014` 返回 HTTP 200、`finish_reason=stop`、结构有效；其正文含“王总”、原日期和“两天”，但业务质量校验只因原锚点字面值 `2天` 而 Invalid。CPU `000007` 也出现“预留两天”表达，同时确实遗漏“王总”，所以其事实拒绝仍然正确。原实现单纯 `string.Contains` 既会将等价数词误拒，也可能让 `12天` 因包含子串 `2天` 而错误通过。
- **代码修复：** `ProfessionalQualityValidator` 对明确的数字+单位锚点改用单位与解析数值精确比较，覆盖常用量词和阿拉伯/中文数词；更长或不同数量仍拒绝。润色系统提示将“逐字”改为保留主体、数值、单位、日期、条件及事实含义，允许数字/中文数词等价书写。添加正反例（`2天` 接受“两天”、拒绝“12天”）以及提示契约测试。
- **红绿与回归：** 新增测试先复现 `两天` 被拒和 `12天` 被子串规则错误接受；修复后相关旧回归 44/44 通过。之后将同一模型/参数下 `000007`、`000014` 各重跑 CPU 与 Vulkan，HTTP 状态及 token usage 均完整记录。数字事实锚点通过数值+单位比较，允许“两天”等价表述，仍拒绝 `12天`。
- **修复后复跑：** CPU `000007`/`000014` 均 Final、未修复、无质量告警，分别为 568/548 与 567/555 输入/输出 token，请求耗时 48.60 秒与 47.85 秒，模型启动 2.99 秒，峰值工作集约 5.10 GB。Vulkan 两项均 Final、未修复，分别 568/539 与 567/495 token，请求耗时 9.38 秒与 8.42 秒，启动 3.74 秒，峰值工作集约 3.22 GB。详见 `out/test-artifacts/qwen3-4b-cpu-rerun-20261006.json`（SHA-256 `c9350a696ea61b0258452252fa98fb439294a724a7533cb3ec2a454b481d1274`）和 `qwen3-4b-auto-rerun-20261006.json`（SHA-256 `346e8e5b8dac25772d620b19df7ed131d534ecde5f1351380ba2ff9f24817fb8`）。这是各两例的工程复跑，不是设备基准或质量收益证明。
- **扩展烟测及发现：** 其余 11 条 development 记录在 Vulkan 跑通，全部 HTTP 200、最终状态均为 Final；一例触发一次修复，因此共 12 次请求。记录只有三个输入骨架的重复上下文变体，不能称为 11 个独立语义任务或盲评。输出检查发现 `000003` 在“说明延期”任务中补出原文没有的延期原因/状态；`000008` 的“问题分析”弱化成泛泛说明。强化系统提示后复跑这两例仍没有触发澄清，说明仅靠提示词约束对该 4B 模型不可靠。结果见 `out/test-artifacts/qwen3-4b-dev-rest-auto-20261006.json`（SHA-256 `2bfc059e161e44fa667383a9a455958b646ba4aecb8fc1a1601375e38d6c6b5e`）及提示词复跑 `qwen3-4b-clarification-guard-auto-20261006.json`。所有合成 smoke 的 phase 0 贡献仍为 0。
- **当前修复：** 在 `ProfessionalizationPlanner` 对明确要求“说明延期”但缺少原因、或明确要求“问题分析”但没有具体问题证据的请求生成澄清问题；`MainViewModel` 在调用生成客户端前返回澄清结果，并仅针对用户明确任务意图触发，普通进度表达和否定指令不误触发。进一步将密钥读取和生成客户端工厂调用移至澄清判断之后。新增 Planner、Prompt 与 MainViewModel 覆盖；新集成断言先红（两条澄清路径各调用工厂 1 次），重排后通过。往返用例验证用户补充的延期原因进入生成请求且不重复追问；隔离 WPF 窗口测试还验证澄清区显示、正文编辑区隐藏、回答绑定和“继续”按钮可用。组合定向测试 **68/68** 通过，主项目 build 0 warning / 0 error。缺失信息时生成客户端工厂与生成请求数均为 0；补充信息后测试客户端收到 1 次生成请求。由于当前桌面没有已运行应用，未启动会读取用户本地配置的正式进程；人工桌面复核仍待执行。Provider 路由/profile 解析仍在澄清判断之前，但不会读取密钥或创建客户端。
- **测量边界与接续：** CPU/Vulkan 数据是每个案例单次请求；工作集不是显存，不能代替目标硬件矩阵；延迟不能按这批小样本外推。规则只覆盖少量明确任务及缺失关键事实，不是通用幻觉检测器；关闭澄清时模型仍可能不遵守“不编造”的提示。下一步继续核对 UI 澄清交互、增加明确业务意图的规则覆盖，并推进运行时许可/来源与干净 Windows 环境核验。不得据内部合成输出宣称质量提升。
- **运行时来源与 MIT 许可追溯落地：** 上游 run/artifact 追溯已把 b11424 编译源 SHA 核定为 `6c59c40076c00eab49754dc955d7652d93f9e125`，区分发布签名 workflow commit `c06f84160a30c66d7b5a2829ae9b3ea15275cbc3`。`LocalRuntimePackageDescriptor` 现保存完整 `CompilerSourceCommit`，CPU/Vulkan 目录项置 `CompilerSourceVerified=true`，已安装 `huaxiazi-runtime.json` 持久化此字段；artifact SHA、size、exe 报告短 SHA 和 prerelease 标志不变。应用 MIT 许可证文本按字节与该 commit 的上游 `LICENSE` 一致，SHA-256 `94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d`；安装会保留 archive 的 LLVM OpenMP notice。来源契约测试先失败，落地后运行时/健康检查/交付/UI 联合筛选 **68/68 通过**。仍是 prerelease 候选；Vulkan/CRT 许可清单、干净 Windows 兼容性和可复现构建证明未完成。公开证据链见[runtime inventory](../local-runtime-package-inventory-2026-10-05.md)。

### 阶段 4：嵌入式 llama.cpp Web UI 许可 notices 收口（2026-10-06）

- **子任务输入：** 固定源码提交 `6c59c40076c00eab49754dc955d7652d93f9e125` 的 `tools/ui`、其 `package-lock.json`、同一次 Release run 产出的 `llama-ui.zip` 及现有运行时许可复制机制。问题是发布 UI bundle 未附 notices，单看 minified 文件 banner 不能覆盖实际模块依赖。
- **取舍与方法：** 不把 lockfile 的 1,207 项都算作 runtime。使用固定 lock 安装依赖，在临时源码副本运行生产 Vite build，并由临时 Rollup 插件列举每个客户端 chunk 中 `renderedLength > 0` 的模块；再按其实际 package.json 版本和许可文件生成归档。模块图得到 2,437 个渲染模块、163 个 npm package name/version。另识别 `virtual:nerdamer` 将源码目录中的 nerdamer-prime、BigInteger.js、decimal.js 三个 vendored 组件打入客户端，故最终 notices 覆盖 **166 个组件**。
- **发现/纠偏：** npm lock 有两个实际 bundle 包缺 `license` 元数据（khroma、svelte-toolbelt），但固定版本 npm tarball都带 MIT 文本；rehype-katex 与 remark-math 的 npm tarball未带许可文件，按各自 npm `gitHead` 取回原始 MIT 文本；包元数据和源码树还不足以找到 vendored 组件的许可，因此检查了虚拟模块实际打包内容，并补入三份源目录许可文本。没有将“lockfile license 字段存在”误当成 notices 已经随产品交付。
- **产出：** `Resources/Licenses/llama-ui-third-party-notices.txt`（264,887 bytes，SHA-256 `2e95d732f4839faede875f062968d42907fadf8052f920858f0033253f7cb8f3`）；`docs/local-runtime-ui-bundle-dependencies-2026-10-06.json` 记录 package/module 数、许可来源和 hashes；`LocalRuntimePackageService` 将 notices 安装为 `THIRD-PARTY-NOTICES-llama-ui.txt`。现有 csproj wildcard 会令 notices 同时进入 app publish 输出，增加约 259 KiB；它不包含 runtime 二进制，运行时包独立下载/安装方式不变。
- **验收：** 基于精确源码/lock 重建的主客户端 JS 为 8,918,504 bytes，与下载 UI artifact 对应主 JS 的 8,918,508 bytes 相差 4 bytes；这支持包模块图对齐，但不宣称可复现构建或字节相同。运行时包服务测试 **7/7 通过**；Release `dotnet publish --no-restore` 成功，应用输出与源 notices 的 SHA-256 完全一致，且能找到三个 vendored 组件、两个元数据缺失条目及两个 GitHub 回源许可条目。
- **效果边界与下一步：** 用户安装本地 runtime 后将多获得 264,887 bytes 的 UI notices；主 app publish 同样携带这份文本（约 259 KiB），没有增加推理时延/API 用量，也不影响模型质量。该子任务关闭 Web UI JS/vendored component notices 缺口；运行时二进制完整动态依赖的 CRT/驱动再分发边界和干净 Windows 验收仍待处理。下一步检查运行时 PE 动态导入依赖与分发边界，并确认安装后实际 CPU/Vulkan 加载路径；整个项目的阶段 0 外部授权盲评准入仍未通过。

### 阶段 4：真实归档安装后的模型加载与产品工作流烟测（2026-10-06）

- **子任务问题：** 之前 Qwen3-4B smoke 证明运行时可以加载模型并生成，但没有把“真实 release ZIP 经产品安装器落盘”与模型工作流连起来。本步验证 CPU/Vulkan 两种归档走同一正式版本化安装路径后，模型健康与润色流程是否仍成立。
- **输入及约束：** 固定 b11424 官方 CPU/Vulkan ZIP（目录 SHA 分别为 `d613ef…efacb5c7`、`97de9a…8f146e9`）、已在独立测试库中的 Qwen3-4B Q4_K_M（SHA `7485fe…534fdf5`）、Windows x64 RTX 4060 Laptop（4 GB VRAM）。测试不访问用户配置，不请求云 Provider；仅用本机 HTTP handler 将已下载 ZIP 喂给真实 `LocalRuntimePackageService`，下载/续传网络路径不在本次范围。
- **方法：** 产品安装器按目录 SHA 校验、解压、真实 `llama-server.exe --version` 探测、附加许可复制并注册 CPU 与 Vulkan runtime；随后设置 ManagedLocal profile，分别用 CPU `GpuMode=Off` 和 Vulkan `GpuMode=Auto` 通过 `LocalRuntimeManager` 启动同一 GGUF、访问 `/health`、执行一轮 `PolishWorkflowService`，然后 stop 并调用正式卸载。
- **实测：** CPU 安装摘要匹配，后端 `CPU`，health **200**，润色 `Final`、未修复、0 quality issues，端到端 **27.864 秒**、启动到 ready **2.571 秒**、进程峰值工作集 **5,053,988,864 bytes**；Vulkan 后端 `Vulkan`，health **200**，润色 `Final`、未修复、0 issues，端到端 **4.903 秒**、启动 **3.958 秒**、峰值工作集 **3,222,388,736 bytes**。两版本均安装 264,887-byte UI notices 并成功卸载。参数：temperature 0.4、top_p 1.0、max_tokens 512、seed 42、repeat_penalty 1.17、ctx 4096、batch 512。每个 backend 仅 1 个合成输入/请求；数值只作链路烟测记录，不能推导质量或 CPU/Vulkan 性能比，工作集也不是显存。
- **产出/证据：** [安装到产品工作流完整记录](../local-runtime-package-install-smoke-2026-10-06.md)；机器可读记录位于 `out/test-artifacts/runtime-package-install-smoke/installed-runtime-qwen3-4b-e2e-20261006.json`，SHA-256 `8e478995de759b42af071d881ea136668f833fcc8e24d362a3a71c6bd2ddffee`。阶段 0 贡献仍为 **0**。
- **自审与下一步：** 第一次 harness 先后出现两项 harness 自身缺陷（CPU 显示字符串大小写断言不匹配；读取 notices 晚于卸载），已仅修正隔离脚本并在同一测试方法上重跑通过；应用实现无需因这两个问题修改。现在证明了这台带现存 VC++/Vulkan driver 的 Windows 机器上的真实包安装、服务启动、产品生成与卸载链路。下一步转向干净 Windows 镜像，验证 CRT/Vulkan 缺失诊断、允许的 CPU fallback 及恢复路径；阶段 4 的目标硬件矩阵尚未通过。

### 阶段 4：发布交付契约与恢复锁定（2026-10-06）

- **问题/依据：** 最近全量回归有 4 项失败，均指向发布脚本偏离仓库 `docs/development-standards.md` 与 `docs/安装与部署.md`：交付不应含单文件 EXE 或脚本安装器；发布应只生成安装包、便携 ZIP 和校验和；NuGet 发布图应在固定 Runtime 与 `PublishSingleFile=false` 下锁定还原；安装器应避免依赖临时目录自解压。
- **改动：** `publish.ps1` 的两个主项目 win-x64 restore 均改用 `--locked-mode`，保留 `PublishSingleFile=false` 和专用 lockfile；移除第二次单文件 restore/publish、额外根目录 `Huaxiazi.exe` 和脚本启动器复制；成功编译 Inno 安装包后强制检查至少一个 `Huaxiazi-Setup-*.bin`；交付清理名单只保留 Portable ZIP、Setup EXE、SHA256SUMS 和安装器分片。`installer.iss` 改为 `UseSetupLdr=no`。`write-checksums.ps1` 不再为旧的单文件 EXE 或脚本启动器生成校验项，但继续覆盖 Setup 分片。测试夹具增加旧文件，验证它们不会进入校验清单。
- **验证：** 发布安全与验收缺陷定向组先以 **5 失败 / 18 通过** 复现契约问题，修改后 **23/23 通过**。win-x64 locked restore 成功；Release WPF 构建 **0 warning / 0 error**。独立打包的 ZIP/安装器先成功生成并检查。随后清理范围保护测试先红（3 项），实现后通过；扩充后的完整自动化回归 **1,675 通过、11 跳过、0 失败（1,686 总数）**。最终执行 `publish.ps1 -AllowUnsigned -RequireInstaller -SkipTests`：脚本 restore、Release build、self-contained publish、Portable ZIP、Inno Setup 编译、SHA 清单和收尾清理全部成功；测试在脚本之外刚刚完整通过，因此此发布调用跳过了重复测试。
- **交付验证：** 根 `release/` 最终仅有 `Huaxiazi-Portable.zip`、`Huaxiazi-Setup.exe`、`Huaxiazi-Setup-0.bin`、`Huaxiazi-Setup-1.bin` 与 `SHA256SUMS.txt`。重新逐项计算后，校验清单内 4 个哈希全部匹配；Portable ZIP 包含主 EXE、默认配置、系统提示和品牌图标。废弃的脚本安装器与根目录单文件 EXE 已从新版 `release/` 清理。覆盖前的旧交付目录副本保存在 `out/test-temp-run-20261006/release-before-full-publish/`，6 个文件均已做 SHA-256 对照。
- **影响与下一步：** 本步把先前 4 个全量回归失败收敛为 0，且真正走通完整发布脚本，不再只以手工等价命令代替。清理助手 `deploy/clean-publish-output.ps1` 只操作 `out/build`、指定的 `out/publish` 目录和便携暂存目录；拒绝仓库外或 `out/publish` 外目标。发布后评测人审包保持存在，临时 publish/stage 已移除，`out/build` 保留。发布为显式未签名候选包，不代表可直接对外发布；本步未改变模型或推理参数，不带来可声称的模型质量收益。下一步处理本地候选稿人工审阅和干净 Windows 运行时检查；正式盲评门槛贡献仍为 **0**。

### 阶段 4：ManagedLocal Vulkan 候选启动失败后的真实 CPU 回退（2026-10-06）

- **计划节点与问题：** 阶段 4 的运行时异常恢复验证。需证明 Auto 策略遇到 Vulkan 运行时启动失败时，会改用 CPU runtime，且仍可完成本地生成；约束是不能更改系统级驱动/注册表、不能调用云端，也不能保存输入或成稿。
- **首轮注入的纠正：** 首次在 harness 子进程设置不存在的 `VK_DRIVER_FILES` / `VK_ICD_FILENAMES` 清单后，受管客户端仍成功从 Vulkan 候选启动。该观察只能说明此注入没有触发产品启动失败；上一轮 harness 将 endpoint 的候选标签称为“实际后端”也不准确。Khronos 文档说明环境变量应覆盖标准 driver discovery，但在未确认该打包 loader 实际响应前，不能据此宣称已模拟驱动缺失。该轮结果保留为失败记录，不计入回退验收。
- **修正验证输入：** 使用既有 `out/test-artifacts` 测试模型库和运行时根，仅把临时运行时工件中的 Vulkan `llama-server.exe` 暂时替换成无效 PE 字节；CPU 包、应用代码和用户正式运行时未修改。harness 执行期间用 `finally` 恢复启动器，并核对恢复前后 SHA-256；进程退出后再次确认文件可哈希读取。
- **结果：** `LocalRuntimeManager` 成功越过失败的 Vulkan 候选并返回 `Backend=CPU`；本地模型启动到 `/health` 就绪为 **7.658 秒**；通过产品 `LocalTextGenerationClient` 发出 loopback 生成并成功收到非空结果。JSON 报告 `external_api_requests=0`、`generated_text_saved=false`、`phase_0_gate_contribution=0`，机器记录在 `out/test-artifacts/driver-fallback-smoke-20261006/fallback-result.json`。本结果证明的是应用候选启动失败后的 CPU 回退与生成链，不等价于真实 Windows Vulkan 驱动缺失验收。
- **输入/产出/验收：** 输入为当前机器已导入的 Qwen3-4B 测试模型、b11424 CPU/Vulkan 工件、临时故障注入脚本；产出为上述 JSON 与恢复后的运行时工件。验收条件“候选失败→CPU 端点就绪→本地生成成功”通过；“系统缺失/损坏 Vulkan driver 时的 OS loader 行为”尚未验证。
- **本步结论与下一触发：** ManagedLocal Auto 的真实启动失败回退已从单测推进到 Windows 实机服务链验证。下一步继续验证真实 driver 初始化失败及其安全诊断，同时澄清 `LocalRuntimeEndpoint.Backend` 是所选运行时工件标签，并非从 llama.cpp 日志/API 读取的设备实际使用证明；这项语义澄清完成后再做 CPU/Vulkan 硬件比较。干净 Windows 矩阵、盲评质量收益、LoRA/SFT 均仍未完成。

### 阶段 4：受管本地运行时的实际后端确认与诊断标签纠偏（2026-10-06）

- **计划节点/问题：** 阶段 4 的后端分档与可解释状态。之前 `LocalRuntimeEndpoint.Backend` 仅根据选择的 EXE 候选写入 `Vulkan`/`CPU`，并被 UI 和请求诊断误当成实际执行设备，可能污染硬件分组和性能判断。
- **输入与判断：** 固定 llama.cpp b11424 Vulkan/CPU runtime、Qwen3-4B 内部测试 GGUF、Windows RTX 4060 Laptop；先用其 `--help` 确认设备枚举参数，再直接启动实际 Vulkan runtime 对比默认 verbosity 3 与 verbosity 4。verbosity 3 的启动输出未给出卸载记录；verbosity 4 实测输出包含 `Vulkan0: NVIDIA GeForce RTX 4060 Laptop GPU` 与 `offloaded 37/37 layers to GPU`。采用最小确认规则：CPU package + `--gpu-layers 0` 可确认 CPU；Vulkan package 只有 `offloaded N/M layers to GPU` 且 `N>0` 才确认 Vulkan；`0/M` 或没有可解析行一律保持未知，避免把“没有层卸载”过度解释成纯 CPU 计算。
- **代码改动：** `Services/LocalRuntime.cs` 为 Vulkan 候选追加 `--verbosity 4`，stderr/stdout 仍实时排空；本节当时版本的 `LocalRuntimeBackendObservation` 以整数卸载数作判断（此规则已在 2026-10-07 反例后废止，见下一节）。`LocalRuntimeEndpoint` 新增 `BackendCandidate`、动态 `ConfirmedBackend` 和 `DisplayBackend`，旧 `Backend` 保留为候选标签兼容属性。设置 UI 对未确认候选显示“实际设备未确认”；生成 telemetry 只记录确认值，未知值不进入 CPU/Vulkan 分档或关联启动/内存指标。
- **测试与实机结果：** 新增候选/确认区分、层卸载正数、零卸载未知、无原文保留等用例；加入 Vulkan verbosity 参数映射与 CPU 不开 verbose 的断言。LocalRuntime 关联测试 **38/38 通过**；隔离目录后 SettingsViewModel **78/78**、GenerationDiagnostics **14/14** 通过；`dotnet build Huaxiazi.csproj --no-restore` **0 警告/0 错误**。产品服务链正常 Vulkan smoke 确认 `Vulkan` 并完成生成；CPU-only 确认 `CPU` 并完成生成；再次把隔离 Vulkan 启动器暂换成无效 PE 后，Auto 回退到 CPU、确认 CPU 并完成本地生成。三份机器记录分别为 `out/test-artifacts/driver-fallback-smoke-20261006/vulkan-observation-result.json`、`cpu-observation-result.json`、`fallback-observation-result.json`。三个 smoke 均无云请求、未保存生成正文，phase 0 贡献为 0。临时直接启动日志在筛选后即删除；产品服务本身不写这些日志文件。
- **环境测试纠正：** 一次将 SettingsViewModel、GenerationDiagnostics 与运行时组混合筛选时，测试宿主因默认 `%LOCALAPPDATA%`/`%TEMP%` 路径出现 SQLite open 与临时目录访问错误；没有把它当作代码回归。将测试进程的 LocalAppData、Roaming 与 Temp 定向到 `out/test-artifacts/test-isolated-profile-20261006` 后，相关两个测试组各自通过。该隔离路径不改变用户正式数据目录。
- **量化边界与下一触发：** 直接 verbosity-4 启动探针在 `/health` 前输出 **208 行、16,571 bytes**；该数只说明需持续排空，日志文本没有保留。正常 Vulkan、CPU-only、应用级 Vulkan 启动失败回退在当前机器均完成一轮 smoke；不是 p50/p95、跨硬件性能基准或干净 Windows driver 缺失验收。下一步仍需在隔离环境制造真实 Vulkan ICD 初始化失败并验证用户可理解的诊断，再进入干净 Windows/硬件矩阵；正式盲评质量收益、LoRA/SFT 仍待后续阶段。

### 阶段 4：Vulkan driver 初始化失败诊断与后端证据修正（2026-10-07）

- **当前计划节点：** 阶段 4 的真实 driver/ICD 初始化故障诊断。用户要求直接往下推进，因此本步使用进程级 Vulkan loader driver 屏蔽验证，不改系统注册表、驱动、系统环境变量或正式安装目录。
- **反例与判据修正：** 在锁定 llama.cpp b11424 上，通过 `VK_LOADER_DRIVERS_DISABLE=*` 隔离禁用所有 Vulkan driver，并将 `VK_LOADER_DEBUG=error` 传给 probe 子进程。loader 明确输出 `vkCreateInstance: Found no drivers!`；但 llama.cpp 仍输出 `offloaded 37/37 layers to GPU`，随后报告约 2.36 GiB `CPU_Mapped` 与 1.68 GiB `CPU_REPACK` 缓冲区并完成模型加载。这证明层卸载数不能单独证明 GPU 设备实际接收模型权重，故废止上一节按正数 offload 确认 Vulkan 的规则。Khronos loader 对该环境变量的语义见[官方 driver interface 文档](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderDriverInterface.md)。
- **实现：** Vulkan 候选增加 loader 的 `error` 诊断级别，并保留原有 `VK_LOADER_DEBUG` 级别；CPU 候选不设置此变量。后端观察器不保存原始日志，只识别正数 `VulkanN model buffer size`、driver unavailable、model loaded 三类信号。正数 Vulkan 设备缓冲区确认 Vulkan；无 driver 信号加模型已加载确认 CPU；互相矛盾或证据不足时保持未知。endpoint 在确认 CPU 且 Vulkan driver 不可用时展示“CPU（Vulkan 驱动不可用）”。
- **回归与运行结果：** LocalRuntime 目标筛选 **14/14 通过**，包括 offload 层数假阳性、Vulkan 缓冲区正例、无 driver CPU 确认、冲突证据未知、日志等级保留及 CPU 路径不注入 loader 变量。新规则下重跑三种真实服务烟测：正常 Vulkan 确认 Vulkan，ready **3.951 秒**、生成成功；CPU-only 确认 CPU，ready **3.062 秒**、生成成功；driver discovery 环境屏蔽确认 CPU，ready **3.710 秒**、生成成功，display 为“CPU（Vulkan 驱动不可用）”。三次均 `external_api_requests=0`、`generated_text_saved=false`、`phase_0_gate_contribution=0`。报告分别为 `out/test-artifacts/driver-fallback-smoke-20261006/vulkan-observation-result.json`、`cpu-observation-result.json`、`icd-unavailable-result.json`。
- **自审与边界：** 实际 Vulkan 正常启动提供了正数 Vulkan device buffer 证据，且新规则下复跑通过；CPU 模式和真实 loader no-driver 情形也完成本地生成。此前 `vulkan-observation-result.json` 由旧规则生成，现已覆盖更新；旧状态章节中的“offload 层数确认”作为历史记录保留，但以上述反例和新判据为准。当前验证是本机 RTX 4060 Laptop、锁定 prerelease runtime、单模型/单请求烟测；不等于干净 Windows 镜像、无 Vulkan loader、不同 ICD、设备矩阵、吞吐/质量评测，也不贡献 phase 0 盲评门槛。下一步核查未安装 Vulkan loader/缺失 CRT 环境的可恢复诊断，并补做主项目构建；不据此扩大本地模型质量结论。

### 阶段 4：本机依赖现况与干净 Windows 验收边界（2026-10-07）

- **输入与判断：** 核对 b11424 runtime inventory、真实 installer smoke、`HealthCheckService` 和故障注入回归。当前系统 `System32` 中 Vulkan loader `1.4.309.0`、`MSVCP140.dll` / `VCRUNTIME140.dll` / `VCRUNTIME140_1.dll`（均 `14.51.36247.0`）全部存在，因此不能在本机真实重现缺失 DLL。现有 `HealthCheckServiceTests` 通过注入依赖探针覆盖 Vulkan 缺失转 CPU 和 VC++ 缺失阻止启动/显示下载指导；**13/13** 通过，但这是控制流模拟，不等于干净 Windows OS loader 证明。
- **环境限制与纠正：** 当前环境没有可直接调用的 Windows Sandbox/Hyper-V 管理工具；`dism /online /Get-FeatureInfo /FeatureName:Containers-DisposableClientVM` 返回 **740 / 需提升权限**。没有提升权限，也没有对 System32 DLL 做改名、删除或替换。干净镜像验证继续标为待办，不能用隔离测试冒充完成。

### 阶段 1：Yi 直连 `yi-lightning` 能力证据补齐（2026-10-07）

- **具体问题与依据：** 既有盘点把 Yi 当前 OpenAI-compatible endpoint 的参数能力标为未知，因为此前只读到旧域名文档。本轮通过当前官方[接口文档](https://platform.lingyiwanwu.com/docs/api-reference)核实 `/v1/chat/completions` 请求格式和 `yi-lightning` 示例：`temperature` 范围 **0–2**（文档默认 0.3）、`top_p` 范围 **0–1**（文档默认 0.9）、输出上限字段 `max_tokens`。当前[服务条款](https://platform.lingyiwanwu.com/useragreement)与[隐私政策](https://platform.lingyiwanwu.com/privacypolicy)说明平台可智能路由至集成的第三方模型；这条云服务数据路径与本地推理不同，能力盘点已显式记下此边界。
- **改动范围：** `ProviderCapabilityResolver` 仅对 `ProviderPlatform.Yi + OpenAICompatible + 精确 yi-lightning` 标记 temperature/top_p Supported、max-token 字段保持 Chat Completions 的 `max_tokens`。`DescribeParameters` 显示已核实采样范围并明确推理等级未核验；严格 JSON Schema 未出现在接口正文，仍为 Unknown，走 prompt 内 Schema + 本地契约校验。`yi-large`、模型近似/快照 ID、OpenRouter 和自定义代理不继承能力；catalog 默认仍是 `yi-large`，不改用户默认选型。实际 JSON body 维持此前通用兼容路径的采样值和 `max_tokens`，本轮主要纠正能力元数据与 UI 可解释性，不改变生成参数。
- **TDD/验收：** 新增精确型号能力、其他型号/Provider 不继承、fake-handler 请求体与 Schema 降级测试；首次红测确认精确型号旧值为 Unknown，亦发现测试误假设默认 `max_tokens` 为 32768，纠正为源码默认 **2048** 后再实现。目标 **3/3**、`AIServiceTests|ProviderPlatformTests` 联合 **289/289** 通过，`dotnet build Huaxiazi.csproj --no-restore` **0 警告/0 错误**。没有使用凭据、未发真实 Provider 请求、没有质量/延迟/成本数据。
- **量化结论与上下游：** 对精确 Yi Lightning，参数能力误标从 **Unknown → 2 项 Supported**（temperature/top_p），API 的 `max_tokens` 与 strict-schema 未核验状态均清楚标出；对当前 `yi-large` 默认设置无变化。它提升配置可解释性和参数证据可追溯性，不构成输出质量收益证据。下一项阶段 1 工作应继续核对官方可读取但尚未映射的 Provider 型号/参数；阶段 4 干净 Windows loader/CRT 矩阵仍是单独待办，必须在真实干净镜像取得证据后才能关闭。

### 阶段 1：百川模型能力范围与请求边界纠正（2026-10-07）

- **问题、约束与输入：** 对话生成质量的上游前提是请求符合服务端约束。百川当前[官方 API v2 文档](https://platform.baichuan-ai.com/docs-v2/api)给出 `temperature` 0–1、`top_p` `[0,1)`、`max_tokens` 1–2048，且只列五个型号支持 JSON Mode。旧实现继承通用全局值：例如用户配置 `temperature=1.5`、`top_p=1`、`max_tokens=4096` 会直接得到越界请求，可能被 Provider 拒绝；将 `top_p=1` 默默发出则不符合其严格开区间。质量目标是请求有效性和可解释性，成本/时延不在本项可证明范围内。
- **实现：** `ProviderCapabilities` 增加可选 `MaximumTemperature`、`MaximumTopPExclusive`、`MaximumOutputTokens` 数值边界；resolver 只匹配百川官方 exact model IDs。序列化时 temperature 取 `min(配置值,1)`，top_p 上界采用 `Math.BitDecrement(1.0)` 得最近可表示且严格小于 1 的 double，输出 token 取 `min(配置值,2048)`。能力描述显示这些边界。Catalog 增加官方文档所列六个模型，默认仍为 `Baichuan4-Turbo`。前五个型号配置 `JsonObjectOnly`，请求发送 `response_format={type:json_object}`；Schema 继续附入提示并经过本地契约校验；Baichuan2 虽被列为可调用型号，但未列在 JSON Mode 支持名单内，故只映射采样/输出边界，结构化输出保持 Unknown。未加入 top_k 控件、未切换默认模型、未把同名代理纳入。
- **TDD 和验收：** 新 fake-handler 先复现旧请求体确实发出 `temperature=1.5`、`top_p=1` 和 `max_tokens=4096`；resolver、目录与请求边界落地后定向 **10/10**，`AIServiceTests|ProviderPlatformTests` **299/299**，`dotnet build Huaxiazi.csproj --no-restore` **0 警告/0 错误**。定向请求体现在为 **1.0 / 0.9999999999999999 / 2048**；JSON Mode 明确为非严格 Schema 模式。
- **收益、限制及接续：** 官方列明型号由单一默认增加到可供用户选择 **6 个**，不更改存量默认；三类可能越界字段均有序列化边界。最多 token 可能从 4096 减为 2048（输出能力受官方限制）；temperature 从 1.5 减为 1.0；top_p 的最小浮点间隔变化约 `1.11e-16`。预计可减少因无效数值造成的请求拒绝，但没有真实服务端调用、模型输出盲评、成本或时延结果，不宣称质量提升。下一步继续依据官方能力盘点处理其他尚未映射的确切 Provider 型号；阶段 4 的干净 Windows loader/CRT 验收仍待真实隔离镜像。

### 阶段 1：阶跃星辰当前型号与 Chat Completions 能力映射（2026-10-07）

- **问题与约束：** 本地 Provider catalog 仍列 `step-2-16k` / `step-1-32k`，未提供阶跃当前官方展示的型号；此前通用 OpenAI-compatible 请求把推理档位和 JSON Schema 能力留空。错误映射可能造成模型不可选、参数被忽略或接口拒绝。默认模型与用户已保存选择不得因此静默改写；未做模型盲评前不能宣称哪个型号更适合润色或把旗舰设成推荐默认。
- **官方依据：** StepFun [当前 Chat Completions API](https://platform.stepfun.com/docs/zh/api-reference/chat/chat-completion-create)列出 `step-5-preview`、`step-3.7-flash`、`step-3.5-flash`、`step-3.5-flash-2603`，采样温度范围 0–2，通用字段 `top_p` 与 `max_tokens`，`response_format` 有 `json_object` / `json_schema` 两种；推理档为 low/medium/high，唯独 `step-3.5-flash-2603` 只列 low/high。Step 5 [型号页](https://platform.stepfun.com/docs/zh/guides/models/step-5-preview)另明确 JSON Schema、三档推理、输出上限 64K。Step 3.7 [型号页](https://platform.stepfun.com/docs/zh/guides/models/step-3.7-flash)明确三档推理；Step 3.5 [型号页](https://platform.stepfun.com/docs/zh/guides/models/step-3.5-flash)明确 2603 变体的两档推理。通用 API 未给 `top_p` 范围，也未逐型号声明 JSON Schema，因此这些限制不作扩推。
- **实现与文件：** `Services/ProviderPlatformCatalog.cs` 增加四个当前 API 文档列出的型号并保留旧型号；原有默认 `step-2-16k` 不变。`Services/ProviderCapabilities.cs` 仅给这四个精确 StepFun Chat Completions 型号映射 temperature/top_p；temperature 上限按 2.0，Step 5 输出上限标注 65536；Step 5 和 3.7 仅映射 low/medium/high，2603 仅映射 low/high，普通 3.5 不推断 reasoning_effort 支持。只有 Step 5 设置 JSON Schema；其余型号保持 Unknown，使用提示词 Schema 与应用校验。未给 `top_p` 猜测范围，未将证据扩展至代理、其他协议或自定义 endpoint。`AIService` 原生 Schema 请求形状已符合当前 StepFun API 文档，无需变更公共生成器。
- **测试与验收：** 新增精确型号/非继承/旧默认保持测试和 Step 5 fake-handler 请求体测试。先运行 RED：旧 catalog 不含 Step 5，capability 为 Unknown，Step 5 请求不含 `response_format`，目标组 **4 个失败、1 个负例通过**；实现后目标 **5/5**，`AIServiceTests|ProviderPlatformTests` 联合 **306/306**，`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。无凭据、无真实 API 请求。
- **量化收益与限制：** 可选型号由 2 个增至 6 个（含两项标注为旧型号）；精确映射了 4 个型号的采样字段、3 个型号的推理等级和 Step 5 一个型号的原生 JSON Schema。Step 5 服务端上限为 64K，但本应用当前 `AIService` 将 `MaxTokens` 限制到 32K，因此本轮只记录服务能力，不扩大应用预算。模型质量、时延、费用与实际服务端兼容性仍需基于授权盲评和受控调用测量。
- **下一步触发：** StepFun 能力映射已完成；接续阶段 1 的下一子项为官方资料可访问、catalog 已列出但 request capabilities 尚未覆盖的 Provider 型号盘点。进入新模型默认推荐或优化采样策略前，需要质量、成本和延迟对照证据。

### 阶段 1：MiniMax 当前 OpenAI-compatible 型号与推理内容隔离（2026-10-07）

- **问题与约束：** 目录仍指向 `api.minimax.chat/v1`，默认 `MiniMax-Text-01`，候选为 `abab6.5s-chat`；MiniMax 当前[模型调用指南](https://platform.minimaxi.com/docs/guides/text-generation)已把 M3、M2.7、M2.7-highspeed 列为当前服务型号，更早型号归入历史模型。若只增加能力映射而不更新 endpoint/model preset，新建配置仍可能选到不在当前服务表内的型号。已保存的旧 endpoint/profile 需要继续可加载和验证。
- **API 事实：** 官方[Quickstart 准备指南](https://platform.minimaxi.com/docs/guides/quickstart-preparation)给按量 API Key 与 M Plan 订阅 Key 的独立入口，OpenAI-compatible base URL 为 `https://api.minimax.cn/v1`。[OpenAI-compatible API 指南](https://platform.minimaxi.com/docs/api-reference/text-openai-api)声明 temperature `[0,2]`、top_p `[0,1]`；M3 新接入建议 `max_completion_tokens`，旧型号字段仍用 `max_tokens`。M3 默认 thinking 开启，支持 `reasoning_split=true` 将推理分入 `reasoning_content`；M2.7 系列可在 `content` 内返回 `<think>` 段。该 API 文档未声明原生 JSON Schema，因此继续提示词 Schema + 本地契约校验。M3.1-Flash-Preview 暂仅经 M Plan/MiniMax Code 提供，没有并入普通按量 API 型号目录。
- **实现：** `ProviderPlatformCatalog` 的 MiniMax preset 改用 `api.minimax.cn/v1`，默认切至当前公开型号 `MiniMax-M3`，增加 `MiniMax-M2.7` 和 `MiniMax-M2.7-highspeed`，旧 `MiniMax-Text-01` / `abab6.5s-chat` 仍保留但标为历史型号。新 preset 应用到新的或用户主动重设的 profile；保存中的旧 profile 不会被自动迁移。`ProviderCapabilityResolver` 只为 MiniMax 直连 OpenAI-compatible 的三个精确 ID 映射采样能力、温度上限 2；M3 使用 `max_completion_tokens`，两款 M2.7 使用 `max_tokens`。`AIService` 对 M3 请求 `reasoning_split=true` 且不发送未支持的 `reasoning_effort`；对带有 `<think>…</think>` 的 MiniMax 成稿在返回前移除推理段，不完整标记或只剩推理时按无效响应拒绝。`ProviderEndpointPolicy` 精确允许此前应用使用的 `https://api.minimax.chat/v1` 作为旧端点，仍拒绝 lookalike 域名；`InferLegacyPlatform` 可识别新旧官方主机。
- **TDD / 验收：** 先加测试，旧实现红测确认能力 Unknown、catalog/default/base 过时、`api.minimax.cn` 因 endpoint allowlist 被拒以及思考段会直接作为结果返回；新增并覆盖旧 endpoint 保留用例。修复后 MiniMax 专项 **10/10**；`AIServiceTests|ProviderPlatformTests|SecurityHardeningTests` 在可写隔离临时目录中 **333/333**；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。第一次未隔离系统临时路径时，安全测试因权限在测试逻辑启动前失败 5 项；隔离后同一范围全绿。
- **自审与收益边界：** 新 preset 由旧两型号扩为三款当前公开 API 型号；对 M3 的新 token 字段和思考隔离有请求级保护，对 M2.7 的 inline thinking 有输出清理保护。默认模型改为当前公开 M3，是新建/用户主动套用 preset 时的选择；没有质量、价格或时延对照，不能称为最佳润色模型，旗舰定价可能高于旧模型。现存旧 profile 内容不迁移，但旧 endpoint 仍受官方域名 allowlist 约束；真实 MiniMax 请求尚未发送。触发后续质量推荐或采样值调整的条件仍是许可评测样本与受控费用/延迟记录。

### 阶段 1：xAI Grok 当前型号及主接口能力映射（2026-10-07）

- **输入与问题：** 官方 Grok 4.3/4.7 型号页、Responses / Chat Completions / Structured Outputs 文档；旧目录只含 `grok-2-latest` 和 `grok-beta`，resolver 未映射 Grok。xAI 官方将 Responses 定义为文本生成、推理和工具使用的主接口，Chat Completions 为旧接口，因此默认新 profile 应使用 Responses，同时保留用户现存 Chat profile。
- **实现：** catalog 增加 `grok-4.3`、`grok-4.7`，保留历史型号；新 preset 使用 OpenAIResponses，`api.x.ai/v1` 不变。仅对 xAI 平台、OpenAICompatible/OpenAIResponses 协议与两个当前精确型号（并含官方 alias `grok-4.3-latest`）映射能力；Chat 输出限额走 `max_completion_tokens`，Responses 走 `max_output_tokens`。两协议 temperature/top_p 均按设置发送，temperature 上限 2，不猜 top_p 的数值边界；按协议映射 Chat `reasoning_effort` 或 Responses `reasoning.effort`；支持原生 JSON Schema 后仍保留本地校验。Responses 请求 `store=false`，不启用 API 的响应持久化检索。4.7 effort 为 low/medium/high/xhigh；4.3 官方页面对 xhigh 信息矛盾，按一致部分映射 none/low/medium/high。
- **兼容/安全：** 存量 profile 不自动迁移。endpoint policy 仅对 Grok 放开官方 authority 上的 OpenAIResponses/OpenAICompatible 两个协议，其他协议拒绝；拒绝非官方 host/path 的策略不变。新默认 4.3 的依据是官方常规列表价 `$1.25/$2.50`/百万输入输出 token，低于 4.7 `$2/$6`；这只说明公开标价差异，具体区域账单及生成质量未测。
- **TDD/验收：** 初始实现 RED 旧状态筛选 **4 项失败、3 项负例通过**。转用官方主 Responses 接口后定向检查先得到 **10/12**，两项失败暴露 Grok 官方 endpoint policy 不接受新协议、设置摘要字段名与测试预期不一致；修复后 Grok 筛选 **16/16**（含官方 alias 与存量 profile 正规化兼容）。`AIServiceTests|ProviderPlatformTests|SecurityHardeningTests` 联合 **349/349**，包含 Responses 请求体/parser、旧 Chat 请求、严格 Schema 和协议 allowlist/denylist；WPF 主项目 `dotnet build --no-restore` **0 警告/0 错误**。`git diff --check` 无空白错误（工作树有既存 LF/CRLF 提示）。
- **收益与边界：** 目录现在提供两个当前型号；新用户走官方当前主 API，旧 Chat profile 保持可用；两协议请求形状、结构化输出、推理字段和存储选项有请求级回归。没有真实 API 请求、质量盲评、账单或延迟数据，不宣称润色质量提升。官方单页对 4.3 xhigh 说明矛盾，仍需要后续确认，当前实现不发送 xhigh。
- **下一步触发：** Grok 型号/协议能力步骤已通过本次实现级验收；接续阶段 1 的下一子项是审查 catalog 中当前可用、仍未映射能力的 Provider，优先选择官方接口规格稳定且能以 fake-handler 完成请求级证据的型号。Responses 质量、实付和延迟比较仍须有授权样本与真实受控请求后再判断；阶段 0 盲评证据轨道不因本步骤改变。

### 阶段 1：Anthropic 型号生命周期与新 profile 默认校准（2026-10-07）

- **问题与依据：** Anthropic 型号目录曾以 Claude Sonnet 4.5 作为新 profile 默认并列出 Opus 4.1；官方生命周期页面已将 Sonnet 4.5 标为弃用、计划于 2026-11-30 退役，并将 Opus 4.1 列为已退役。官方建议 Sonnet 4.5 用户迁移到 Sonnet 5.5。官方 Sonnet 5.5 文档规定非默认 `temperature`、`top_p`、`top_k` 会返回 400；当前能力 resolver 已对该精确型号省略采样字段并支持 `output_config.effort` 与 JSON Schema。
- **改动：** `Services/ProviderPlatformCatalog.cs` 的 Anthropic 新 preset 默认改为 `claude-sonnet-5-5`；目录加入官方当前 Opus/Sonnet/Haiku 型号，移除已退役 Opus 4.1 的新建入口；Sonnet 4.5 保留以兼容用户已有 profile，但目录明确标注退役日期。`VerifiedOn` 更新为 2026-10-07。profile 归一化不迁移用户显式选择，回归测试固定了此兼容约束。
- **请求行为：** fake-handler 覆盖 Sonnet 5.5 结构化请求：沿用 Anthropic Messages API，发送 `output_config.effort`、`output_config.format` JSON Schema 和应用设置的 `max_tokens`，不发送 `temperature` / `top_p`。生产请求组装与 capability resolver 在本项中没有新增变更。
- **TDD/验收：** catalog、默认 preset、存量 profile 不迁移和请求体用例先行编写；旧 catalog 状态下定向 RED 确认新默认/catalog 两项失败，而既有 Sonnet 5.5 请求能力已工作。更新后 `AIServiceTests|ProviderPlatformTests` **331/331** 通过；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；`git diff --check` 无空白错误（仓库显示既有 LF/CRLF 转换提示）。
- **收益边界：** 新建 profile 不再默认选中临近退役型号；已退役型号入口移除 1 个，弃用型号改为显式提醒；已保存的 Sonnet 4.5 profile 不被静默重写。官方标准价目显示 Sonnet 4.5 输入/输出 `$3/$15`、Sonnet 5.5 `$2/$10` 每百万 token，名义列表价各低约 33.3%；这不是实际账户账单。没有真实 API 请求、成稿盲评、延迟或成本对照，不宣称输出质量提升。
- **后续：** 继续核对其余 Provider catalog 与 model-capability 精确映射，发现生命周期变化时优先修正新建入口并保留存量配置；质量推荐、路由或提示改动仍需实际授权样本的评测证据。

依据：Anthropic [Models overview](https://platform.claude.com/docs/en/models/overview)、[Model deprecations](https://platform.claude.com/docs/en/about-claude/model-deprecations)、[Claude Sonnet 5.5](https://platform.claude.com/docs/en/models/sonnet-5-5/overview)、[Effort](https://platform.claude.com/docs/en/build-with-claude/effort)。


### 阶段 1：Together Serverless 模型目录与 Schema 能力接通（2026-10-07）

- **问题与约束：** 当前 Together preset 使用 `api.together.xyz/v1`，可选项只有 Llama 3.3 70B 和 Qwen2.5 72B；官方当前 serverless 目录仍列 Llama 3.3，但 Qwen2.5 未列，并已提供一批新型号。Together 当前 API 参考明确使用 `https://api.together.ai/v1/chat/completions`，列出 Chat 参数 `temperature`、`top_p` 和 `max_tokens`，temperature 为 0–1；官方 serverless 目录逐型号标出 Structured outputs 支持。
- **方案取舍：** 更新新 profile 的 preset host 为官方当前文档主机 `.ai`，仍精确保留应用既有 `.xyz` endpoint；不改用户当前默认模型，因为 Together 官方材料未对话匣子润色任务提供可用质量比较。目录只增加当前目录标记支持 structured outputs 的主要 Serverless chat 型号作为 app 的 structured workflow 候选，不把目录价格或“支持 Schema”误作润色质量结论。
- **改动：** `Services/ProviderPlatformCatalog.cs` 新 preset host 使用 `https://api.together.ai/v1`，默认仍为官方可用的 `meta-llama/Llama-3.3-70B-Instruct-Turbo`；当前 schema-capable 列表增至 12 个，包括 Thinking Machines Inkling、Qwen 3.5 9B、Kimi K3、GLM 5.3/5.3 Flash/5.2、DeepSeek V4 Flash 0731/V4 Pro 0813/V4.1 Flash、MiniMax M3、GPT-OSS 120B，以及 Llama 3.3；原 Qwen2.5 72B 保留为“历史型号”。`Services/ProviderCapabilities.cs` 仅对 Together + OpenAICompatible + 这 12 个精确 ID 开启 temperature/top_p 和 strict JSON Schema 能力，temperature 最大 1.0；不发送 reasoning effort，不猜 top_p 数值边界，max token 字段保持 `max_tokens`。`Services/ProviderEndpointPolicy.cs` 只额外允许精确旧 host `api.together.xyz:443`，lookalike host 仍拒绝；legacy platform inference 同时认 `.ai` 与 `.xyz`。
- **兼容与参数：** 存量 `.xyz` endpoint、Qwen2.5 model、SecretId 和 active profile 归一化后保持原值。Together 的温度配置高于 1 时序列化为 1；例如 1.7 会被 clamp 到 1.0。top_p 如 0.82 原样发送（API 列支持但未给出有效范围）。Together API 文档将上下文超限行为默认设为 error；本项没有静默截断或提升 app `max_tokens` 设置。
- **TDD/验收：** 新增当前模型目录、精确 capability/负例、请求体、endpoint 新旧 host 与仿冒拒绝、存量配置兼容测试；红测阶段 **11 个失败** 明确复现 catalog/capability/host/request 缺口；随后设置页能力说明测试再以 **1 个预期失败** 复现已核实参数仍显示未核验。实现后 Together 专项 **46/46**、`AIServiceTests|ProviderPlatformTests|SecurityHardeningTests` **377/377**；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；`git diff --check` 无空白错误（仅仓库整体已有行尾转换提示）。
- **量化收益与边界：** app 内当前目录可选择项从 2 个增至 13 个，其中 12 个官方目录明示支持原生 structured outputs，另保留 1 个历史 Qwen 项供现有配置辨认。原生 Schema 在 app 中由 Unknown 增至 12 个精确型号；API endpoint/Schema capability 的请求级契约已有 12 型号回归。Qwen 3.5 9B 的官方标价为输入 `$0.17` / 输出 `$0.25` 每百万 token，Llama 3.3 70B 为 `$1.04/$1.04`，为用户提供明显不同的价格档位选择；不表示实际账单节省。未发真实 API 请求，未测输出质量、线上拒绝率、实付、延迟或吞吐，不能宣称模型质量提升。
- **后续：** 下一项继续检查尚未按官方精确型号映射的 Provider；随后回到工作流级路由/偏好闭环与本地 runtime 交付。阶段 0 的盲评数据仍独立推进，目录与契约回归不替代真实质量评估。

依据：Together AI [Available serverless models](https://docs.together.ai/docs/serverless/models)、[Chat Completions API](https://docs.together.ai/reference/chat-completions)、[Structured outputs](https://docs.together.ai/docs/inference/chat/structured-outputs)、[official Together TypeScript schema parameter](https://github.com/togethercomputer/together-typescript/blob/main/src/resources/chat/completions.ts)、[API key location](https://support.together.ai/articles/7147082565-how-to-regenerate-my-api-key)。

### 阶段 1：OpenRouter 官方文本型号目录按需刷新（2026-10-07）

- **问题：** OpenRouter preset 只有三项静态模型，容易随聚合平台型号增删而陈旧；静态列表不能证明模型当前可用，也无法安全推断具体 provider endpoint 的参数能力。
- **输入与约束：** 使用 OpenRouter 官方 `GET https://openrouter.ai/api/v1/models` 返回的 `data[].id/name/architecture.input_modalities/output_modalities`。官方文档确认可用型号可由 `/api/v1/models` 列举，模型架构描述包含模态字段；结构化输出支持需按具体 provider endpoint 核验，并建议以 `require_parameters` 限定路由。刷新仅由用户点击触发，读取该 profile 已配置的 API Key；请求不包含对话正文，不写遥测。
- **实现：** 新增 `OpenRouterModelCatalogService`，固定官方 HTTPS URI、Bearer 认证、禁用自动重定向、20 秒超时、16 MiB 响应上限、20,000 项上限；仅收录明确声明 text 输入和 text 输出的项，去重并排序。设置页仅在 OpenRouter profile 展示“刷新 OpenRouter 型号”，成功后以内存目录替换推荐项；目录刷新失败保留上次目录和当前 model ID，若已配置 model 不在新目录仍以“当前配置（目录未列出）”保留。目录数据不改 `ProviderCapabilityResolver`，OpenRouter Schema 能力继续 Unknown、本地校验继续生效。
- **验收：** TDD 新测试最初因服务和 ViewModel 接口缺失而编译失败；实现后型号解析、文本/多模态可用过滤、ID 去重/排序、固定 URL、Bearer、缺失密钥不发请求、畸形响应拒绝、错误正文不外泄、刷新成功及失败后保留清单/当前模型等专项回归通过。OpenRouter 新测试 + ProviderPlatform tests **134/134** 通过；加入设置 UI 契约和 WPF view smoke 后的联合筛选 **193/193** 通过；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。没有真实 API 请求。
- **自审与边界：** `/models` 是平台级目录，不是端点级支持表；因此即便模型条目暴露 `supported_parameters`，本实现也不据此启用原生严格 Schema、reasoning 或参数范围。动态列表提升型号发现新鲜度，不能证明账户可用性、成本、延迟或生成质量。一次较宽的设置回归筛选触发了既有测试对 `%TEMP%` 写入的拒绝及 SQLite 初始化失败，测试宿主随后崩溃；本功能和 ProviderPlatform 独立筛选 **134/134** 全通过，主项目构建通过。该环境失败没有被记作功能回归通过。
- **量化影响及接续：** 型号选择从固定 **3 项** 扩展到用户刷新时由官方目录返回的文本输入/输出候选，确切数量随 OpenRouter 当时目录变化，当前未发在线请求，故不虚报候选数。刷新耗时/请求费用/质量收益均未实测；该操作只读取目录 metadata、不消耗生成 token。阶段 1 下一步继续检查其它未按精确型号映射的 Provider，之后回到工作流级路由与偏好闭环；阶段 0 准入与正式盲评未因此改变。

依据：OpenRouter [Quickstart（`GET /api/v1/models` 型号发现）](https://openrouter.ai/docs/quickstart)、[模型详情与架构字段](https://openrouter.ai/docs/api/api-reference/models/get-model)、[Structured Outputs 与 endpoint 级支持约束](https://openrouter.ai/docs/guides/features/structured-outputs)、[列出模型 endpoints](https://openrouter.ai/docs/api/api-reference/endpoints/list-endpoints)。

### 阶段 1：MiMo V2.6 型号生命周期、Chat 参数与 JSON Mode 映射（2026-10-07）

- **输入与问题：** MiMo preset 原来默认 V2.5 Pro，且只列 V2.5 两个型号。最新官方 API 文档现列出 V2.6 Pro、Flash、Pro UltraSpeed；V2.5 Pro/标准版标为即将退役，日期为 2026-10-21。第一轮核查只覆盖 V2.5 thinking 参数；随即发现目录生命周期已陈旧，因此没有把旧型号参数映射当作完成，而继续核验 V2.6 API 及结构化输出页面。
- **方案与取舍：** 新建 profile 默认设为官方当前旗舰 `mimo-v2.6-pro`；同时提供 V2.6 Flash 和有资格限制的 UltraSpeed 候选。保留 V2.5 ID 供存量 profile 使用，并在 UI 目录标出退役日，不静默重写用户选型。V2.6 与 V2.5 都按 MiMo 官方 OpenAI-compatible Chat 契约实现，不把 Responses API 的字段映射混入 Chat adapter。
- **实现：** `ProviderPlatformCatalog` 加入 3 个 V2.6 型号、标记 2 个 V2.5 型号即将退役并更新默认值/核验日。`ProviderCapabilityResolver` 按精确 MiMo + OpenAICompatible + 型号映射 `max_completion_tokens`、最高 temperature `1.5`、top_p `0.01–1.0` 和 JSON Mode；仅 Low 档关闭 thinking 后发送采样参数，thinking 开启时省略无效的 temperature/top_p。`AIService` 在结构化请求使用 `response_format=json_object`，仍将具体 JSON Schema 放入提示并用应用本地校验；此 API 不支持在 Chat 路径声明 strict JSON Schema。测试连接关闭 thinking 并使用 8-token 探针，不发送采样参数。思考内容 `reasoning_content` 不进入用户结果。
- **TDD 与验收：** 新增当前目录、旧型号保留、精确模型能力、推理档位条件采样、范围夹取和 JSON Mode 请求体测试；初次 RED 因 capability contract 缺少 top_p 下界字段而编译失败，随后实现。MiMo V2.5/V2.6 专项 **14/14** 通过；`AIServiceTests|ProviderPlatformTests` 联合 **373/373** 通过；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；本次涉及文件 `git diff --check` 无空白错误（Git 提示既有 LF/CRLF 转换）。不发送真实 MiMo API 请求。
- **量化收益与边界：** 新配置默认从临近退役的 V2.5 Pro 转为 V2.6 Pro，目录从 2 个旧型号扩至 3 个当前候选并保留 2 个旧 ID。Low 推理档现能实际使用用户采样值，范围外值被限制到官方边界；结构化请求新增 JSON 语法约束。没有中文润色盲评、线上质量/延迟/成本或账号可用性测量，不声称模型质量提升；JSON Mode 只保证 JSON 语法，Schema/字段语义仍靠本地校验。阶段 0 贡献保持 0。
- **下游：** 下一项继续核对未完成的 Provider 精确能力；阶段 1 的模型能力表收口后回到工作流统一契约/路由执行链，并保留盲评阶段的独立数据轨道。

依据：MiMo 官方 [API Models 与退役信息](https://mimo.mi.com/docs/en-US/quick-start/model)、[Model Hyperparameters](https://mimo.mi.com/docs/en-US/api/guidance/model-hyperparameters)、[Deep Thinking](https://mimo.mi.com/docs/en-US/quick-start/usage-guide/other/deep-thinking)、[Structured Outputs (JSON Mode)](https://mimo.mi.com/docs/en-US/quick-start/usage-guide/text-generation/structured-output)、[API Pricing / V2.5 deprecation date](https://mimo.mi.com/docs/pricing)。

### 阶段 2：推理预设的实际参数说明与 Provider 能力边界（2026-10-07）

- **输入与问题：** `ProviderInferencePresets.Apply` 将 Low/Medium/High 分别写为 `temperature/top_p/max_tokens/timeout = 0.2/0.8/1024/120s`、`0.4/1.0/2048/120s`、`0.3/0.95/4096/180s`；但设置页原说明把 Low/High 一概描述为更快/更充分推理，没有说明这些参数只是 profile 配置值，也没有说明各 Provider 会按能力映射、省略或夹取。比如 MiMo High 开启 thinking 时忽略 temperature/top_p，而 OpenAI GPT-6 Astra 固定使用默认采样并映射 reasoning_effort。
- **方案与取舍：** 不改变现有预设数值和生成请求行为；把配置写入值与 Provider/型号实际请求行为分开说明。质量、速度不能从采样值静态推出，故删除通用“更快/更充分推理”因果承诺，由当前精确型号 capability summary 负责显示请求级映射。
- **改动：** `ProviderInferencePresets` 用同一份低/中/高 preset 数据驱动 `Apply` 与新增 `Describe`，设置页显示对应四项保存值并说明其不保证被 Provider 接收/使用；Custom 显示保留已有值。`SettingsViewModel.SelectedInferenceDescription` 调用该共享说明；`InferenceLevels` 描述和 `ProviderProfileEditView.xaml` tooltip 改为“套用参数预设、具体请求行为取决于所选 Provider/型号”。下方现有 `SelectedProviderCapabilitySummary` 保持精确显示 thinking/effort、采样参数和输出协议。
- **TDD/验收：** 新增 MiMo Low/High 及 GPT-6 Astra 能力摘要配对测试和 UI 契约断言；旧文案 RED 证明缺少实际配置值，测试首次误用默认归档目录后改用 `out/test-data` 隔离 fixture 再复现预期 RED。实现后预设值、Custom 保留、ViewModel 语义、XAML 契约联合 **6/6 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；涉及文件 `git diff --check` 无空白错误（仅有 Git LF/CRLF 转换提示）。无真实 Provider 请求。
- **量化收益与边界：** Low/Medium/High 的四项配置值现在均明确可见；MiMo 与 GPT-6 Astra 两种不同 API 行为由能力摘要分别说明。没有更改任一参数值、请求序列或模型输出；未测质量、时延、成本收益，UI 更准确不等于推理质量提高。
- **下游：** 接续阶段 2 路由剩余工作：只有在有授权评测结果和端到端延迟/预算测量支持后，才实现复杂度/质量排序；当前继续逐型号补齐可由官方证据证明的参数能力与全入口边界。

### 阶段 2：本机诊断记录路由策略与显式备用尝试（2026-10-07）

- **问题：** 路由策略已执行、UI 会展示本次实际模型，但本机生成诊断无法判断某条 Provider 请求是由何种策略选中，或是否为显式备用尝试，因而不能解释路由行为和备用切换频率。
- **实现：** 主窗口将路由策略、白名单路由原因、主/备用 profile 的临时 ID 交给本次诊断 scope；每条 Provider 尝试仅持久化策略名、白名单原因与 `primary`/`fallback` 分类，不保存 profile ID/名称、API 地址或请求正文。诊断汇总 JSON 增加三类请求尝试计数；缺字段的旧 JSONL 可继续读取，未知路由原因会被丢弃。诊断仍遵循用户 opt-in 与无痕模式禁用规则。
- **验收：** 先以缺少新接口和汇总字段的测试编译失败确认 RED；实现后 `GenerationDiagnosticsServiceTests`、`GenerationDiagnosticsReportServiceTests` 与 `CompanionGenerationIntegrationTests` 联合 **43/43 通过**，`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**，本次涉及文件 `git diff --check` 通过（仅 Git 提示既有 LF/CRLF 转换）。
- **限制与后续：** 记录反映请求尝试数，不是唯一用户会话数；旧记录不会补造路由字段。此步骤提高本地可解释性，不证明路由带来质量、延迟或成本收益。后续可在用户导出的诊断摘要中比较策略与备用尝试，但自动优选仍须依赖授权盲评及端到端成本/时延证据。

### 阶段 1：Groq 退役型号、新建默认及推理响应隔离（2026-10-07）

- **问题与证据：** 应用新建 Groq profile 默认仍为 `llama-3.3-70b-versatile`。Groq 官方退役页列明 Llama 3.3 70B 与 Llama 3.1 8B 已于 2026-08-16 退役，普通开发者账户的请求会失败，部分已有企业合同例外；官方将 GPT-OSS 120B/20B 分别列为建议替代。当前 API 文档还表明 GPT-OSS 默认响应含 `reasoning` 字段，Qwen 3.8 支持通过 `reasoning_format=hidden` 仅返回最终答复。
- **改动：** 新 profile 默认采用官方推荐的 `openai/gpt-oss-120b`；目录添加/保留 GPT-OSS 20B、120B 与 Qwen 3.8，并保留两个 Llama ID、明确标注退役日期及企业例外。已保存的 Llama profile 经过 `NormalizeProviderProfiles()` 后保持原 Model 与活动 profile 不变。Groq GPT-OSS 请求发送 `include_reasoning=false`；Qwen 3.8 发送 `reasoning_format=hidden`；仅三个核实过的精确 model ID 映射推理控制，未知后缀不继承；已有 strict Schema、`reasoning_effort` 与 `max_completion_tokens` 路径保留。
- **验收：** 目录、旧 profile 保持、Groq 推理参数及未知后缀 fake-handler 测试先失败后通过；`ProviderPlatformTests|AIServiceTests` **378/378 通过**，WPF 主工程 build **0 警告、0 错误**，涉及代码 diff-check 通过（Git 仅提示既有 LF/CRLF 转换）。无真实 Groq 请求。
- **第一性原理复核与限制：** 默认变更仅作用于新建 profile；其依据是旧默认已退役和 Groq 官方迁移建议，不是中文润色盲评。GPT-OSS 120B 官方将 Llama 3.3 的替代候选指向 120B，因此新默认沿用该迁移建议；仍提供官方低一档 GPT-OSS 20B 作为可选项，但本项目未测质量、真实价格或延迟。推理抑制字段只控制响应是否包含推理内容，不减少 reasoning token 的生成成本，也不构成安全或质量收益的量化证据。后续如要更新 Groq 目录默认/参数范围，应从官方 active models 与型号/能力页复核，不外推到代理 profile。

### 阶段 1：Spark 当前型号、采样边界与输出预算映射（2026-10-07）

- **问题与官方输入：** 讯飞当前 HTTP Chat Completions 文档列有 6 个版本 ID 和每型号 max_tokens 上限；通用 top_p 合法范围为 `(0,1]`。此前产品只提供 `generalv3.5` 与 `lite`，默认 generalv3.5 指向 Max；官方说明 Max 服务 2026-03-10 起升级为 Ultra，并将授权用量合并。当前用户可保存 `top_p=0`，或将 32,768 上限发给 Lite/Max/Pro 等更低输出额度型号，存在服务端拒绝风险。
- **改动与取舍：** 新 profile 明确默认 `4.0Ultra`，目录补齐 `max-32k`、`generalv3`、`pro-128k`，保留 `generalv3.5` alias、Lite 和存量配置。六个精确 ID 的输出上限分别映射为 Ultra 32768、Max-32K 32768、Max 8192、Pro 8192、Pro-128K 32768、Lite 4096。temperature 夹取到 2；top_p 若为 0 则映射到 `double.BitIncrement(0)`，其余保持不超过 1，避免自造文档未给出的十进制最小值。结构化请求按官方通用 `json_object`，继续用应用本地 Schema 校验；不增加文档未列出的 reasoning_effort。未知后缀不继承版本能力。
- **验收：** 先新增目录、输出上限、top_p 开边界、未知后缀及设置页摘要回归，旧目录/请求行为测试 RED；实现后 Spark 相关筛选 **25/25 通过**，覆盖 6 个模型输出限制、正 top_p 序列化、未知后缀拒绝继承、JSON Mode、默认项、UI 说明和存量 profile 保持。`ProviderPlatformTests|AIServiceTests|Spark 设置摘要` 联合筛选 **398/398 通过**；主项目 build **0 警告、0 错误**，本次涉及文件 `git diff --check` 通过（仅 Git 提示既有 LF/CRLF 转换）；无真实讯飞 API 调用。
- **量化影响与限制：** 新建目录新增 4 个型号，top_p=0 不再传 0；传出 max_tokens 不超过对应官方型号上限。按代码路径可避免这两类已知范围不合法/越限请求，真实 API 拒绝率、体验质量、费用与延迟尚未测量；Max alias 的后台具体路由仍由讯飞服务端管理，本应用不将其等同为同一质量模型。

### 阶段 1：DeepSeek Chat 型号能力与响应终态收敛（2026-10-07）

- **输入与具体问题：** 官方当前 Chat API 文档可读取的 model IDs 包括 `deepseek-flash` 与 `deepseek-v4-pro`；官方更新说明 `deepseek-flash` 当前指向 V4.1 Flash、V4 Flash 旧 ID 是临时兼容路由，且 `deepseek-v4-pro` 在 V4.1-Pro 发布前也被路由至 V4.1 Flash。旧 resolver 对 thinking 用 `StartsWith("deepseek-v4")`，容易让未登记后缀继承参数；同时没有将明确支持的 `response_format=json_object` 映射到请求。AIService 把产品 High 发送为 `reasoning_effort=max`，与官方“High→high、Max→max”的映射不同。响应解析仅识别通用 `length`/`content_filter`，DeepSeek 的资源不足与中断终态仍可能交付部分文本。
- **方案与改动：** 精确列出两个当前 API ID 和两个官方说明的兼容别名；能力仅绑定 DeepSeek 直连 `OpenAICompatible`，不传播给 OpenRouter。Capabilities 声明 JSON Object Mode（不是严格 JSON Schema）、temperature 上限 2、top_p 开界及官方推理档位；thinking 请求仍按已记录语义省略无效 temperature、把 top_p 夹到 `[0.95,1]`，Low/Medium/High 分别用 `low/high/high`，Custom 沿用服务默认。将两个当前模型名称及 `deepseek-v4-pro` 的路由关系显示在型号目录中，ID 和新建默认值不变。`insufficient_system_resource` 归类为 transient ProviderUnavailable（供用户显式配置的备用模型处理），`aborted` 归类为 transient Incomplete，二者都不交付响应中的部分文字。
- **TDD 与验收：** 新增 capability 精确型号/代理隔离、未知后缀、JSON Mode 请求体、终态分类、目录显示及推理档请求测试；首次运行按预期在缺少 capability、JSON Mode 和错误分类时失败。实现后 DeepSeek 定向组合通过；`AIServiceTests|ProviderPlatformTests|DeepSeek 设置页联动` **407/407 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**。设置页测试改为显式临时 archive root，避免触发测试宿主默认 SQLite 路径；无真实 DeepSeek API 请求。
- **量化影响与边界：** **4 个**经官方文档确认的 DeepSeek API ID 获得显式请求能力；任意 `deepseek-v4*` 未知后缀不再自动继承；High 请求从最高 `max` 档降为对应官方 `high` 档；结构化请求现在多发送一个 `response_format=json_object` 字段，Schema 仍由应用本地校验；两种明确的服务端非成功终态不再按完整答案交付。可降低错误参数/解析以及部分文本误交付风险；未测真实 API 拒绝率、中文输出质量、token 成本或延迟，不构成质量收益证明。
- **自审与下一步：** 官方 API 还支持 Responses 格式的 JSON Schema，但本 profile 使用 Chat Completions，本步只启用 Chat 文档列出的 JSON Output；不把 Responses 的 Schema 能力混入 Chat。`deepseek-v4-pro` 为服务端兼容路由，标签明确展示现行目的型号，存量 model ID 不迁移。阶段 0 正式盲评、阶段 4 干净 Windows 硬件矩阵与阶段 5 LoRA/SFT 仍未因此关闭。下一项继续核对有官方资料且尚未精确映射的 Provider 型号。

依据：DeepSeek 官方[Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)、[JSON Output](https://api-docs.deepseek.com/guides/json_mode/)、[Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)、[2026-09-10 V4.1 Flash 更新与兼容路由说明](https://api-docs.deepseek.com/news/news260910/)。

### 阶段 1：阿里云百炼 Qwen Chat 能力与结构化输出映射（2026-10-07）

- **问题与约束：** Qwen preset 默认 `qwen-plus`，目录另含 Qwen3.8 Max/Flash/2.4T-A95B/27B；resolver 原来只认识 3.8 系列推理档，其他能力 Unknown，且所有 3.8 型号都错误地使用 `max_tokens`。请求参数必须绑定直连百炼的精确型号，不能把能力传给同名 OpenRouter 路由。
- **证据与决策：** 百炼 Chat 文档规定 `temperature` 范围 `[0,2)`、`top_p` 范围 `(0,1]`，并建议二者只设置一个；`max_completion_tokens` 的 Qwen Max/Flash/Plus 支持范围是各自新系列起始版本及以后。按目录精确 ID，只有 `qwen3.8-max` 和 `qwen3.8-flash` 可以明确对应到 Max/Flash 新系列，故这两项切换到 `max_completion_tokens`；Qwen3.8 2.4T-A95B/27B 及旧别名仍用兼容 `max_tokens`，不从家族名称推导新字段支持。官方结构化输出页明确列出 Max/Flash 的 JSON Schema 支持；Qwen3.8 开源系列与 `qwen-plus`、`qwen-turbo`、`qwen-max`、`qwen-long` 有 JSON Object 支持，Schema 仍由应用本地校验。
- **改动：** `ProviderCapabilityResolver` 对 8 个目录型号及官方明确列出的 `qwen3.8-max-0902` 快照登记采样与结构化输出能力，温度最大值限制为小于 2 的最大可表示 double，`top_p=0` 提升到最小正 double；仅 Max/Flash 精确型号及已列出的 Max 快照使用原生 Schema 与 `max_completion_tokens`。Qwen 型号采用精确 ID allowlist；中途新增回归发现旧 `StartsWith` 快照判断会让未知后缀继承能力，已改为精确白名单。存量模型选择、API 地址、凭据和其他 Provider 路由均未改。
- **TDD/验收：** 新增请求级测试覆盖 8 个目录型号 + 1 个官方快照的 token 字段/response_format 分流、温度/top_p 开边界和未知后缀隔离；另测设置摘要是否揭示实际 token 字段、推理参数和 Schema/JSON Mode。最初契约测试 9/9 失败；加入快照与未知后缀测试时分别出现预期失败（1/9 快照错字段、1/1 未知后缀误继承）；摘要测试又捕获一次遗失 reasoning_effort 字段名，修复后覆盖 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **501/501 通过**。`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**，涉及文件 `git diff --check` 通过（Git 仅提示已有 LF/CRLF 转换）。无真实百炼 API 请求。
- **影响与限制：** 8 个目录型号从 Unknown 转为按文档映射，已核实的输出字段与 Schema 映射覆盖；避免已知的温度/top_p 越界，Qwen3.8 Max/Flash 改用推荐输出字段。请求契约测试不能证明线上账户已开通型号、模型输出质量、成本或延迟变化；阶段 0 盲评贡献为 0。

依据：阿里云百炼官方[OpenAI 兼容 Chat 参数文档](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)及[Qwen 结构化输出支持型号和模式限制](https://help.aliyun.com/zh/model-studio/qwen-structured-output)。

### 阶段 1：Mistral Large 现行别名的 JSON Mode 接入（2026-10-07）

- **输入与问题：** Mistral 目录提供 `mistral-large-latest`，但 resolver 只为 Small/Medium 部分型号登记结构化输出能力，因此 Large structured generation 只在应用提示词中带 Schema，没有利用官方 Chat API 的 JSON Mode。
- **证据与取舍：** Mistral 官方 JSON Mode 页面用精确 ID `mistral-large-latest` 演示 `response_format=json_object`；Chat API 文档列出 `max_tokens`、`temperature`、`top_p` 和 `response_format`。官方只建议 temperature 0.0–0.7，并建议 temperature/top_p 二选一，没有给出该型号的强制合法数值上下限，因此不新增本地 clamp、不映射推理强度，也不把 JSON Mode 提升为原生 JSON Schema。模型 `-latest` alias 可能随 GA 版本切换，基线评测应记录并固定实际模型版本；本步不更改目录 alias 或用户模型选择。
- **实现：** 仅对 Mistral + OpenAICompatible + 精确 `mistral-large-latest` 声明 Chat 温度/top_p 参数支持、`max_tokens` 和 `JsonObjectOnly`。结构化请求发送 `response_format=json_object`，同时把实际 Schema 放入提示并继续做应用侧契约校验。设置摘要明确 JSON Mode、Schema 本地校验、采样字段未设专属数值范围及不发送未核验的 reasoning_effort。`open-mistral-nemo` 与其它未知/历史 ID 保持未映射，本步不根据旧模型名称外推能力。
- **验收：** 两个 fake-handler/能力摘要测试在实现前按预期失败；实现后 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **503/503 通过**，`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**，本步相关文件 `git diff --check` 通过（仅有既存 LF/CRLF 转换提示）。没有真实 Mistral 请求。
- **量化影响与下游：** 1 个当前目录型号的 structured generation 从提示词单独约束升级为服务端 JSON 语法模式 + 本地 Schema 校验；没有模型成稿质量、拒绝率、token、延迟或费用实测，不声称质量提升，阶段 0 盲评贡献为 0。下一子任务继续交叉审查目录中的历史型号与当前官方目录状态，再选择有明确接口证据的能力缺口。

依据：Mistral 官方[JSON Mode 示例](https://docs.mistral.ai/studio/conversations/structured-output/json_mode)、[Chat Completions 参数](https://docs.mistral.ai/api)及[模型生命周期与 alias 语义](https://docs.mistral.ai/inference/model-lifecycle)。

### 阶段 1：Mistral Nemo 弃用提示与 Ministral 3 8B 替代型号（2026-10-07）

- **计划节点与问题：** 继续审查 Mistral catalog 中历史型号 `open-mistral-nemo`。官方模型卡已标为 Deprecated，列出弃用日 2026-05-22，并建议新接入改用 Ministral 3 8B；卡片没有声明它已经 retired。目录此前仍以普通选项呈现 Nemo，且未列官方替代型号。
- **取舍与输入证据：** 官方 Ministral 3 8B 模型卡给出的固定 API ID 为 `ministral-8b-2512`（GA v25.12）、列出 Chat Completions 与 Structured Outputs；官方自定义结构化输出文档也以 `ministral-8b-latest` 展示 Chat Schema 调用。基于基线重跑需要固定模型版本，本应用加入固定版 `ministral-8b-2512` 而不是易随 GA 更新的 `-latest` alias；Nemo 旧 ID 保留以便现有配置辨认，但 UI 明确标记弃用、日期与替代型号。不改默认 `mistral-small-latest`，也不自动迁移用户配置。
- **实现：** `ProviderPlatformCatalog` 为 `open-mistral-nemo` 添加“已弃用”标签和 Ministral 3 8B 推荐说明，新增 `ministral-8b-2512` 目录项；`ProviderCapabilityResolver` 对固定模型 ID 开启原生 JSON Schema，按 Mistral Chat 通用 API 发送 `max_tokens`、temperature 与 top_p，不添加未核实的温度/top_p 硬边界和 reasoning_effort 映射。新模型仍进行应用本地 Schema 校验。
- **验收：** 目录兼容性、能力摘要与 fake-handler 原生 Schema 请求测试先失败后通过；`AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` 联合 **505/505 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；相关文件 `git diff --check` 通过（仅有既存 LF/CRLF 转换提示）。没有真实 API 调用。
- **量化影响、限制与接续：** Mistral 目录新增 **1 个**固定版本替代型号；**1 个**弃用型号保留但增加生命周期提示；原默认和既有 profile ID 不变。structured generation 对新型号使用服务端 JSON Schema 约束并由本地再校验；模型中文润色质量、延迟、费用与 Nemo API 实际退役日期均未实测/未在官方卡片确认，不声称质量提升。下一子任务继续核对其余 Provider 目录中未标注的历史型号与精确能力缺口。

依据：Mistral 官方[Mistral Nemo 模型卡与弃用日期](https://docs.mistral.ai/models/mistral-nemo-12b-24-07)、[Ministral 3 8B 固定型号与能力](https://docs.mistral.ai/models/ministral-3-8b-25-12)、[自定义结构化输出 Chat 示例](https://docs.mistral.ai/studio/conversations/structured-output/custom)。

### 阶段 1：智谱 GLM-4 Flash 能力证据复核（2026-10-07）

- **输入与待解决问题：** 智谱 catalog 的新建默认型号是 `glm-4-flash`，但 `ProviderCapabilityResolver` 仅对精确的 `glm-5.3` 与 `glm-5.2` ID 映射推理强度和 JSON Mode。需判断能否依据官方资料把能力映射扩展到 GLM-4 Flash，避免因 OpenAI-compatible 协议相同就臆测字段支持。
- **资料核对结果：** 智谱官方可检索的“AI 搜索引擎”示例使用直连 `https://open.bigmodel.cn/api/paas/v4` 的 Chat Completions，但指定的是 `glm-4-0520`，并以提示词要求 JSON、由客户端解析；它没有证明 `glm-4-flash` 支持 `response_format`、reasoning 字段或对应采样边界。[官方示例](https://docs.bigmodel.cn/cn/best-practice/case/ai-search-engine) 当前检索到的官方“助手对话”页面标注 deprecated，接口是 `/assistant`，型号为 `glm-4-assistant`/`glm-4-alltools`，与应用使用的 Chat Completions 和 `glm-4-flash` 不同，不能拿来外推。[官方助手接口](https://docs.bigmodel.cn/api-reference/%E5%8A%A9%E7%90%86-api/%E5%8A%A9%E6%89%8B%E5%AF%B9%E8%AF%9D)
- **决策与产出：** 当前证据不足以证明 GLM-4 Flash 的型号级 Chat 请求参数和结构化输出能力，因此本步**不改 resolver、不向请求添加 `response_format` 或 `reasoning_effort`，不改默认型号和存量配置**。GLM-5.2/5.3 已有的精确映射保持不变；这不是断言 Flash 不支持相关能力，而是尚未取得能绑定到确切接口与型号的官方证据。无代码改动，故不新增测试。
- **自审、影响与衔接：** 资料检索覆盖了精确型号、JSON/response_format 和 reasoning 字段，但没有找到当前官方型号专属 Chat API 规范。将协议通用性视作型号能力的假设不成立，按计划停止该映射路径；无既有实现需要回滚。本步没有质量、延迟、成本或 API 拒绝率收益。后续回到阶段 1 的 Provider 目录审计，选择能由当前官方文档精确证明、且能通过请求级契约测试的下一个缺口；若智谱开放可引用的型号专属规范，再重开此项。

### 阶段 1：Yi Large Chat 请求能力映射（2026-10-07）

- **输入与问题：** `yi-large` 是 Yi provider 的 catalog 型号，但 capability resolver 只有 `yi-lightning` 有显式映射，导致 Yi Large 在设置能力说明中仍显示为未知。请求仍走 Chat Completions 通用字段，但系统不能准确区分“接口已证实支持采样参数”和“未知”。
- **官方证据与边界：** 零一万物官方 API 文档列出 `POST /v1/chat/completions`，示例 model 为 `yi-large`，并列明 `max_tokens`、`temperature`、`top_p`；温度/Top-P 范围写为 0–2 / 0–1，并建议二者择一。[01.AI 官方 API 文档](https://platform.01.ai/docs)；项目当前平台文档入口为[零一万物开放平台](https://platform.lingyiwanwu.com/docs)。阿里云百炼的 Yi 型号文档（更新时间 2026-09-10）亦按 `yi-large` 列出这些参数及具体开区间，但该文档使用 DashScope endpoint，故不把其端点边界值直接移植到零一万物 API。[阿里云百炼 Yi API 文档](https://help.aliyun.com/zh/model-studio/yi-api)。零一万物平台协议说明服务会按算力/响应/服务质量智能路由到集成模型，因此这里只声明应用对接 API 的字段能力，不声称实际后端模型固定或模型质量可复现。[平台用户协议](https://platform.lingyiwanwu.com/useragreement)
- **实现：** `ProviderCapabilityResolver` 将 `yi-large` 加入 Yi + OpenAICompatible 精确型号 allowlist，temperature/top_p 标记 Supported，输出字段保持 `max_tokens`，不注册推理强度，不推断原生 JSON/Schema；`yi-large-preview`、其它代理平台仍为 Unknown。能力摘要继续提示温度与 Top-P 的官方范围、Schema 使用提示词和本地校验。默认型号、Endpoint、参数默认值和存量配置未改。
- **TDD 与验收：** 新增能力与请求回归后，先按预期看到 Yi Large temperature `Unknown` 而失败；实现后 Yi Large/Yi Lightning 能力与结构化请求定向组合 **4/4 通过**。请求测试确认 body 使用 `temperature`、`top_p`、`max_tokens`，不发送未经确认的 `response_format`，并保留应用内 Schema 校验。联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` 使用工作区临时目录重跑 **506/506 通过**；第一次未重设 Windows `TEMP`/`LOCALAPPDATA` 时，4 个设置页用例因拒绝创建默认 SQLite/临时目录失败，改用工作区可写目录后全数通过。`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。未调用真实 API。
- **量化影响、限制与接续：** `yi-large` 两项采样能力从 Unknown 更新为 Supported（+2 项精确能力元数据），生成字段形态和参数取值不变；该映射提升设置可解释性与能力决策准确度，不是成稿质量、费用、延迟或拒绝率提升证据。下一项继续盘点目录中有当前官方资料但 capability 未覆盖的模型；阶段 0 正式盲评仍为 0 贡献。

### 阶段 1：OpenAI GPT-4o Chat / Responses 契约与输出预算映射（2026-10-07）

- **输入与问题：** `gpt-4o` 和默认型号 `gpt-4o-mini` 已获得原生 JSON Schema 映射，但温度、top_p、请求 token 字段和模型输出上限仍未映射。实测 OpenAI Chat 请求走通用分支，会发送 deprecated `max_tokens`；用户 `MaxTokens` 可设至 32,768，而两型号官方最大输出均为 16,384。Responses 路径会发送 temperature/top_p 以外缺失的字段，而且没有对 `max_output_tokens` 应用型号上限。
- **官方证据与取舍：** 官方模型页确认 GPT-4o / GPT-4o mini 均支持 Chat Completions、Responses 和 Structured Outputs，且上下文 128,000、最大输出 16,384。[GPT-4o](https://developers.openai.com/api/docs/models/gpt-4o) · [GPT-4o mini](https://developers.openai.com/api/docs/models/gpt-4o-mini) Chat Completions API 将 `max_tokens` 标为 deprecated，建议改用 `max_completion_tokens`；温度范围 0–2、top_p 范围 0–1，官方建议二者只调一项。[Chat Completions API](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) Structured Outputs 文档列明 GPT-4o mini 与 GPT-4o 2024-08-06 及以后快照支持 Schema；本实现仍只匹配 catalog 中已有的精确 `gpt-4o` 与 `gpt-4o-mini` alias，不扩展到未登记快照或代理路由。[Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs) Responses 请求以 `max_output_tokens` 表示生成 token 上限，且与 Chat Completions 字段不同。[Responses API](https://developers.openai.com/api/reference/python/resources/responses/methods/create)
- **实现：** `ProviderCapabilityResolver` 将两型号精确能力扩展为 temperature/top_p Supported、temperature 上限 2、严格 JSON Schema、最大输出 16,384；Chat 协议发送 `max_completion_tokens`，Responses 协议保持 `max_output_tokens`。`AIService` 同样将 Responses 输出预算 clamp 到型号上限。设置能力摘要显示采样边界、实际 token 字段与 16,384 上限。未改模型选择、API 地址、默认采样值、隐私设置或旧配置。
- **TDD 与验收：** 新增能力矩阵（两模型 × 两协议）、Chat 请求体、Responses 请求体与输出上限用例；实现前 6 个断言按预期失败（能力 Unknown、错误/缺失 token 字段及 Responses 未 clamp）。实现后定向组合 **6/6 通过**，联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **511/511 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 warning / 0 error**。所有请求由 fake HTTP handler 验证，无真实 OpenAI 调用。
- **量化影响、风险及接续：** 两型号 × 两协议共 **4 个精确映射组合**获得已核验采样/Schema/输出预算；默认 OpenAI Chat 请求由 deprecated `max_tokens` 改为推荐 `max_completion_tokens`；用户设置超过 16,384 的部分改为服务上限，最多减少 16,384 tokens 的单次输出预算，并降低错误字段/越限请求风险。能力映射不证明中文改写质量提升；temperature/top_p 同时发送保留现有用户设置兼容性，但违反“官方建议只调一项”的最佳实践，需在后续产品参数策略评估中决定默认保留一项还是增加显式采样策略。下步继续审查目录中其余未明确映射型号。

### 阶段 1：润色与提示词优化共用分层上下文（2026-10-07）

- **输入与问题：** 润色和提示词优化已共用 `StructuredGenerationWorkflow` 的输出解析/校验/修复流程，但上下文组装不一致：提示词优化使用 `AgentContextPipeline`，润色仍把安全规则、输出协议、风格、事实锚点、偏好和 Skill 全部拼成一段 System Prompt，导致层级优先级、事实锚点去重和层省略元数据不能统一管理。
- **实现：** `PolishPromptBuilderService.BuildAgentContext` 现通过 `AgentContextPipeline` 生成 system、developer、facts、skills、personalization、task 层；原 `BuildSystemPrompt` 保留并转调新入口，工作流及修复调用仍兼容。专业化计划与文本分析中的事实锚点合并、去空并按 ordinal 去重；策略与低优先级推断进入 Skills 层；用户人格和偏好进入 personalization 层；请求原文与历史仍单独留在 user message。上下文可编辑文本进行 XML 特殊字符转义，固定安全规则保留在 system 层。新增回归覆盖层次顺序、锚点去重、Skill/偏好隔离及原文不泄漏。
- **TDD 与验收：** 新用例先因 `BuildAgentContext` 不存在而失败；实现后润色提示构建器 **9/9** 通过。润色、提示词构建和相邻工作流回归合计 **44/44** 通过；首次默认 Windows 临时目录权限拒绝导致的提示词构建失败通过将 `TEMP`、`TMP`、`LOCALAPPDATA` 指向工作区临时目录排除环境因素后通过。`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。
- **影响与限制：** 润色和提示词优化的上下文层级与结构化输出门禁现已共享；`BuildSystemPrompt` 的兼容入口仍向既有 provider 工作流提供字符串。该实现提供了更清楚的层优先级和可观测的包含/省略层信息，但尚无线上质量、延迟、成本或 token 降幅数据，不能视作成稿质量已提升。下一步继续阶段 1 的 Provider 能力目录精确覆盖与参数映射，阶段 0 外部盲评门槛贡献仍为 0。

### 阶段 1：SiliconFlow Qwen2.5-7B 请求能力映射（2026-10-07）

- **输入与问题：** SiliconFlow 目录中有 `Qwen/Qwen2.5-7B-Instruct`，但能力解析器没有该型号映射。生成流程因此只用提示词描述 JSON 协议，未启用平台 JSON Mode；请求采样参数也显示为未知能力。
- **官方证据与边界：** SiliconFlow 当前 Chat Completions API 文档列出 `temperature`（最大 2）、`top_p`（最大 1）、`max_tokens`、JSON Object、JSON Schema 与流式参数，并说明模型能力会按型号变动。[Chat Completions API](https://docs.siliconflow.cn/docs/api/chat-completions-post)。官方 JSON Mode 指南称当前全部大语言模型支持 JSON Mode，同时提醒型号清单会变化。[JSON Mode](https://docs.siliconflow.cn/docs/userguide/guides/json-mode)。官方微调指南列出 `Qwen/Qwen2.5-7B-Instruct` 为支持的对话模型。[Fine-tuning](https://docs.siliconflow.cn/docs/userguide/guides/fine-tune)。这些资料支持该 provider/model 的 JSON Object 与采样映射，但不支持把平台的 JSON Schema 字段外推为此型号的严格 Schema 能力，也不证明当前账号的服务资格/可用性。
- **实现：** 为 SiliconFlow 的精确型号 `Qwen/Qwen2.5-7B-Instruct` 声明 temperature/top_p Supported、temperature 上限 2、OpenAI 兼容输出 token 字段 `max_tokens` 与 `JsonObjectOnly`。结构化输出走 `response_format: json_object` 并保留本地 Schema 校验；未启用 `json_schema`，未添加推理强度或输出上限。该映射不扩展到 Qwen3.8 或其它 provider/别名。
- **TDD 与验收：** 新增能力精确性/不向未核验 Qwen3.8 型号泄漏测试，以及实际请求体验证；两项新测试先失败，实现后 **2/2 通过**。联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **513/513 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。请求均使用 fake handler，未调用真实 SiliconFlow API。
- **影响、限制与衔接：** 对这一精确型号，JSON Mode 从未启用变为启用，增加了服务端有效 JSON 格式约束；严格 Schema 合法率和成稿质量的增益尚未对真实模型实测，量化质量收益仍为 0 已验证。设置的 temperature/top_p 现在确实随请求发送，范围在已证实的平台上限内；官方建议两者择一调整，应用保留用户现有设置值以兼容旧配置。下一步继续检查其余 catalog 能力缺口；若证据表明仅为旧型号或接口语义含糊，保留未知/本地校验路径，不伪造能力结论。

### 阶段 1：Provider 能力契约补齐流式、工具与上下文元数据（2026-10-07）

- **输入与问题：** 实施计划的 ProviderCapabilities 还要求描述流式、工具和上下文上限，但当前记录只覆盖采样、推理、结构化输出和输出 token 字段，阶段 2 路由将缺少三个决策维度。
- **实现：** `ProviderCapabilities` 增加 `Streaming`、`ToolCalling` 和 `ContextWindowTokens`；默认都是 Unknown/null。OpenAI `gpt-4o` 与 `gpt-4o-mini` 在 Chat Completions/Responses 两种协议上按官方型号页记录为 Streaming/Function Calling Supported、128,000 上下文 tokens。[GPT-4o](https://developers.openai.com/api/docs/models/gpt-4o) · [GPT-4o mini](https://developers.openai.com/api/docs/models/gpt-4o-mini)。这些是模型/API 能力元数据；应用尚未实现流式消费或工具执行，本步不改变请求路径。
- **TDD 与验收：** 新增两个型号 × 两协议的 4 项元数据测试，并断言非精确变体保持 Unknown；新测试实现前因字段缺失无法编译，实现后 **4/4 通过**。联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **517/517 通过**，应用构建 **0 警告、0 错误**。
- **影响、限制与衔接：** Provider 能力目录新增 3 个可用于未来路由/上下文预算的维度；本步仅为 2 个 OpenAI 型号填入经核验数据，没有扩大到代理服务或其它型号。由于应用目前不流式读取和不调用工具，尚无用户体验或性能收益。本节点下一步继续核对能力数据和生成契约闭环，再进入实际模型路由实现。

### 阶段 1：GPT-6 家族协议能力与上下文/输出预算映射（2026-10-07）

- **问题与约束：** GPT-6 的推理等级已映射，但流式、上下文窗口、最大输出和工具调用仍是 Unknown，设置摘要也没有说明 Chat 与 Responses 的工具差异。单一 `ToolCalling=Supported` 会错误地暗示所有协议和推理档都可调用工具；本应用当前并未执行工具，也未实现响应流式消费，因此本步只补精确能力元数据和请求输出上限，不启用新功能。
- **官方依据与取舍：** OpenAI 型号页列出 GPT-6 Astra、GPT-6.1 Sol、GPT-6 Sol、GPT-6 Luna 的 1,050,000 上下文与 128,000 最大输出，以及 Streaming/Function Calling 支持；部署清单说明 Astra 与 6.1 Sol 的工具调用要求 Responses，Sol/Luna 在 Chat Completions 中仅 `reasoning_effort="none"` 可用。[Astra](https://developers.openai.com/api/docs/models/gpt-6-astra) · [6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol) · [Sol](https://developers.openai.com/api/docs/models/gpt-6-sol) · [Luna](https://developers.openai.com/api/docs/models/gpt-6-luna) · [部署清单](https://developers.openai.com/api/docs/guides/deployment-checklist)。因此按协议精确登记工具能力，并为 Chat Sol/Luna 增加“要求无推理”条件，不外推给代理 Provider 或型号变体。
- **实现：** `ProviderCapabilities` 现在为四个精确 GPT-6 型号记录 `MaximumOutputTokens=128000`、`ContextWindowTokens=1050000` 与流式支持。Responses 的工具调用标为 Supported；Chat 的 Astra/6.1 Sol 标为 Unsupported；Chat 的 Sol/Luna 标为有条件 Supported，并用 `ToolCallingRequiresNoReasoningEffort` 显式表达 `reasoning_effort=none` 条件。最大输出值由现有 `AIService` 请求组装器按协议分别夹取到 `max_completion_tokens` 或 `max_output_tokens`。设置能力摘要显示上下文、输出上限、流式与工具调用限制。
- **TDD 与验收：** 新矩阵先执行失败：13 个断言均暴露 GPT-6 工具字段 Unknown 或摘要缺少协议限制。实现后 GPT-6 及 GPT-4o 能力定向测试 **25/25 通过**；联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **530/530 通过**；`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；所改 tracked 文件 `git diff --check` 无空白错误（仅有 LF/CRLF 转换提示）。未调用真实 OpenAI API。
- **量化影响、限制与下一步：** 四型号 × 两协议共 **8 个映射组合**现在有上下文/最大输出/流式数据，工具调用按官方协议和推理条件区分；输出请求可受官方 128,000 上限约束，但应用当前用户 token 上限低于该值时不会改变单次请求预算。当前最大可配置值为 32,768，故本次对正常设置下的输出长度影响为 **0 tokens**；上下文数据暂未被用于自动截断或路由。没有模型质量、速度或成本提升的实测证据；流式消费与工具执行仍未实现，阶段 0 盲评贡献仍为 0。下一子任务继续检查其余精确 Provider 映射及生成契约的实际消费点，之后再评估上下文预算和自动路由，不改变手动选择默认值。

### 阶段 1：StepFun 新建 profile 默认与当前型号目录校准（2026-10-07）

- **问题与输入：** Provider 默认目录将 `step-2-16k` 标为“旧型号”，但仍用它为新建 StepFun profile 填默认值；当前目录已经包含有精确能力映射的 `step-3.7-flash`。官方现行型号页列出 Step 3.7 Flash，说明它使用 Chat Completions、上下文 256K，并支持 low/medium/high 推理强度。[Step 3.7 Flash](https://platform.stepfun.com/docs/zh/guides/models/step-3.7-flash) · [StepFun 开放平台](https://platform.stepfun.com/)。资料没有证明 Step-2 已正式退役，也没有中文润色盲评结果，因此不把本次修改描述为质量升级或弃用迁移。
- **方案与改动：** 只将 `ProviderPlatformCatalog` 的 StepFun 新 profile 默认值改为 `step-3.7-flash`。旧型号继续留在选择目录；测试创建已保存的 `step-2-16k` profile 并归一化，确保用户现有 Model 和活动 profile 不变。`step-3.7-flash` 现有的 reasoning_effort 和采样能力映射不变；没有修改温度、top_p、token 上限、Endpoint 或用户配置迁移规则。
- **TDD 与验收：** 新默认测试先失败，明确得到预期 `step-3.7-flash`、实际 `step-2-16k`；更新后 `ProviderPlatformTests|ProviderProfileTests|ConfigServiceTests` **209 项通过、11 项跳过、0 项失败**（跳过项为既有配置服务测试标记）。`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**；涉及 tracked 文件 `git diff --check` 无空白错误，仅有 Git 的 LF/CRLF 转换提示。未调用真实 StepFun API。
- **收益、风险与接续：** 新建 StepFun profile 默认从目录标注的旧型号切换到当前官方文档型号；保留旧选项和存量配置降低兼容风险。新模型可能有不同的可用性、输出风格、成本与延迟，当前未实测这些差异，中文改写质量增益记为 **0 已验证**。下一步回到能力目录审计：对仍缺少精确官方 API 证据的型号维持 Unknown/现有兼容路径，不猜测参数；随后推进阶段 0 盲评与阶段 2 路由所需的质量/成本数据闭环。

### 阶段 1：StepFun Step 3.7 Flash 流式、工具与上下文能力（2026-10-07）

- **问题与输入：** Step 3.7 Flash 已成为 StepFun 新 profile 默认，现有能力目录只记录其采样和推理档，Provider 的流式、工具与上下文能力字段仍 Unknown。StepFun 官方型号页将模型明确对应到 Chat Completions，声明支持流式和工具调用，文档上下文上限为 256K，并分别给出 low/medium/high 推理语义。[Step 3.7 Flash 官方型号页](https://platform.stepfun.com/docs/zh/guides/models/step-3.7-flash)。官方页面未给该型号最大输出 token 数，也未给出 JSON Schema 能力，因此这两项继续保持既有未知/本地 Schema 路径。
- **实现：** 仅对 `StepFun + OpenAICompatible + step-3.7-flash` 设置 `Streaming=Supported`、`ToolCalling=Supported`、`ContextWindowTokens=256000`，并使用独立 evidence ID 驱动设置摘要显示。未改 AIService 的 `stream=false` 请求行为；应用仍未执行工具调用。`step-3.7-flash-custom`、其他 StepFun 型号与其他 Provider 不继承这些字段，`MaximumOutputTokens` 仍为 null。
- **TDD 与验收：** 两个新测试先失败：精确型号的 Streaming 仍为 Unknown，说明中也缺少 256,000。实现后 StepFun 定向筛选 **9/9 通过**；联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **532/532 通过**；主工程构建 **0 警告、0 错误**；tracked 代码 `git diff --check` 无空白错误，仅 Git 提示既有 LF/CRLF 转换。未发真实 StepFun API 请求。
- **影响与自审：** 能力目录对 1 个精确型号新增 **3 项**官方可证实的能力数据，提升后续路由与能力展示的准确性；实际请求没有增加流式或工具调用，模型质量/延迟/成本收益均为 **0 已验证**。另外重新检索智谱 `glm-4-flash` 后，官方检索结果仍只找到 GLM-4-0520 示例或已弃用的助手 API，未找到绑定到该型号与 Chat Completions 的参数规范；因此没有改 GLM-4 Flash 能力映射。下一步继续挑选能由当前官方文档绑定到确切型号/协议的 Provider 缺口；全部自动路由质量排序仍需等阶段 0 盲评与端到端成本数据。

### 阶段 1：Mistral Small 4 固定型号上下文与工具调用映射（2026-10-07）

- **问题与输入：** 固定型号 `mistral-small-2603` 已登记 temperature/top_p 和 JSON Schema，但 ContextWindow 与 ToolCalling 仍 Unknown。Mistral Small 4 官方型号卡按该精确 ID 列出 256K 上下文、Chat Completions、Function Calling 与 Structured Outputs。[Mistral Small 4 型号卡](https://docs.mistral.ai/models/mistral-small-4-0-26-03)。官方 lifecycle 文档说明 `-latest` 是指向最新 GA 版本的滚动别名，目标更新时可能改变行为与价格；因此不能把当前固定 Small 4 的元数据套到 `mistral-small-latest` 或其未来目标。[Mistral 模型生命周期](https://docs.mistral.ai/inference/model-lifecycle)。
- **实现：** 仅为精确 ID `Mistral + OpenAICompatible + mistral-small-2603` 添加 `ContextWindowTokens=256000` 与 ToolCalling Supported。无证据的 Streaming 保持 Unknown，最大输出上限保持 null；固定型号摘要明确显示最大输出限制未核验。原先该分支使用宽泛的 `IsExactModelOrSnapshot`，可将任意 `mistral-small-2603-*` 后缀误判为快照；现改用大小写不敏感的精确型号比较，仅收紧此型号分支，未全局改变其它 provider 的快照规则。滚动别名维持独立映射，不继承本步固定型号 ContextWindow 元数据。
- **TDD 与验收：** 两个新用例先失败，分别发现精确型号上下文为空、摘要没有 256K，并发现自定义后缀因前缀快照判断错误继承能力。实现和精确匹配修正后 Mistral 相邻能力筛选 **9/9 通过**；联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` **534/534 通过**；主工程构建 **0 警告、0 错误**；`git diff --check` 无空白错误，仅有既有 LF/CRLF 转换提示。未发真实 Mistral API 请求。
- **影响、风险与接续：** 固定 Small 4 型号新增 **2 项**能力元数据，且拦住一个型号后缀过度匹配；对设置展示和后续路由能力资料更准确，但应用尚未执行工具调用或上下文预算截断，实际质量、延迟、成本收益为 **0 已验证**。滚动别名可能更新模型和能力，应通过后续官方目录刷新或版本固定治理，而不是假定其永远等同 v26.03。下一步继续审查其它当前目录型号的精确能力缺口，然后回到阶段 0/阶段 2 质量数据闭环。

### 阶段 1：Gemini 3.5 Flash GenerateContent 能力和 token 上限（2026-10-07）

- **问题与证据：** `gemini-3.5-flash` 已在 Gemini 3 能力分支中声明 JSON Schema 和 low/medium/high 推理档，但流式、工具、上下文和输出上限仍为空。Google 官方型号页将稳定模型 ID 绑定到 1,048,576 输入 tokens、65,536 输出 tokens，并列出 Function Calling、Structured Outputs；GenerateContent API 提供 `streamGenerateContent` SSE 端点。[Gemini 3.5 Flash 型号页](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash) · [Gemini 3.5 Flash 发布说明](https://ai.google.dev/gemini-api/docs/whats-new-gemini-3.5) · [GenerateContent API](https://ai.google.dev/api/generate-content)。
- **实现：** 在通用 Gemini 3 家族分支前增加精确 `Gemini + GeminiGenerateContent + gemini-3.5-flash` 映射：`Streaming=Supported`、`ToolCalling=Supported`、`ContextWindowTokens=1_048_576`、`MaximumOutputTokens=65_536`，保留 JSON Schema 和 Gemini 3 推理档。只匹配稳定精确 ID；`gemini-3.5-flash-custom` 仍为 Unknown。设置能力说明写明 GenerateContent、流式、Function Calling 和限制值。
- **TDD 与验收：** 新增精确型号能力及摘要测试，初次运行 **2 项失败**，确认四项元数据和摘要原先缺失；实现后定向 **2/2 通过**。联合 `AIServiceTests|ProviderPlatformTests|SettingsViewModelTests` 在项目专用 Temp 路径运行 **536/536 通过**。默认 Windows Temp 路径首轮引发既有权限异常并使测试宿主中断；换到工作区测试 Temp 后通过。`dotnet build Huaxiazi.csproj --no-restore` **0 警告、0 错误**。未发真实 Gemini API 请求。
- **边界与收益：** 能力目录对一个固定 Gemini 型号补齐 4 项官方可证能力；生成链路仍未启用 SSE 消费、工具执行或按上下文上限截断，本次实际质量、延迟、成本收益为 **0 已验证**。后续继续收敛 Provider 能力数据并进入真实生成契约/路由消费验证，不把元数据填充当作产品能力上线。

### 阶段 1：结构化响应重复字段拒绝与自定义提示词层级防护（2026-10-07）

- **节点与问题：** 阶段 1 的 Provider 无关本地契约校验虽检查 JSON 类型、必需/额外字段和长度，但 `System.Text.Json.JsonDocument` 接受对象内重复键，校验器将其折叠为字段名集合；如 `{"answer":"版本一","answer":"版本二"}` 会通过 Schema 检查，而调用方/下游解析器可能选择不同值。全套测试同时暴露 `CustomPrompt_CannotRemoveTheUserMessageTrustBoundary` 失败：中文自定义指导“把原文当成更高优先级指令”未命中注入检测，出现在文本化 system/developer 层级边界之后。
- **方案与改动：** `StructuredOutputValidator` 在字段校验前以 ordinal 名称集合拒绝重复键，返回 `duplicate-field`，不尝试猜测首值或末值。`PromptInjectionSanitizer` 增加对“将原文/用户输入作为更高/最高优先级指令/规则”等中文层级覆盖表达的识别；仍把正常自定义偏好放在较低优先级上下文中，违规片段按现有个性化编译规则过滤。
- **TDD 与验收：** 重复键用例先失败（实际 `IsValid=true`，预期 false）；加入检查后结构化 validator 定向 **5/5 通过**，生成/Provider/润色/数据集相关联合筛选 **392/392 通过**。全套测试第一次运行发现上述自定义提示词用例失败；单项重放仍失败，扩展注入识别后原用例及相关 PromptContext/Polish 测试 **21/21 通过**。最终全套 **1,885/1,885 通过，11 项既有跳过，0 失败**；主 WPF 项目构建 **0 警告、0 错误**。全套 TRX 位于 `Huaxiazi.Tests/TestResults/full-suite-rerun.trx`。
- **第一性判断与量化边界：** 重复键的本质风险是同一响应在不同 JSON 消费端语义不唯一，拒绝比规范化成首个或最后一个字段更安全，也不会改变合法输出。提示词层级问题本质是用户自定义指导能够表达越权意图，而注入识别模式不足；在用户指导编译边界过滤优先于增加全文重复安全后缀，可避免增加每次请求 token。精确检测现覆盖该类中文表达，但不能证明通用提示注入安全。两项改动提供确定的合同完整性/提示约束防护；未统计真实响应中重复键发生率，也没有质量、延迟、成本收益数据。
- **偏差审查与衔接：** 结构化生成共用工作流和 Schema 降级早已接通，本轮未重复建设；本次发现的信任边界失败与重复键均属于该主链的实际质量风险。后续应检查评测/路由链上的实际质量信号是否进入用户可读决策，并按已冻结测试集产出当前模型基线，而不是继续堆叠静态 Provider 元数据。

### 阶段 0/2：内部开发集本机润色诊断入口（2026-10-07）

- **实现：** 新增 `internal-polish-run` 命令，只接受 `purpose=internal_regression_only`、`phase_0_gate_contribution=0` 且明确禁止盲评的数据清单；只接受 development split；核验 cases 文件 SHA-256、来源类别和开发族数量；Provider 地址固定为 `127.0.0.1:11434`，要求本机已安装精确型号。输出写入新目录，包含逐例流程状态、延迟、请求/token 用量（服务提供时）、模型/运行时元数据与程序集/提示/契约哈希；不读取参考成稿来打分，不执行云端回退，也不测模型进程峰值内存。新增 CLI 回归用例，验证盲评 manifest 在输出目录创建前被拒绝。
- **验证与本机信号：** `InternalRegressionDiagnosticCliTests` 定向测试 **1/1 通过**；运行 `qwen3:4b` 的 development 切片时，首 **4/13** 样本均由产品工作流返回 `Incomplete`，每条消耗的输出 token 都达到 2,048 上限（其中首例有一次修复请求）；随后中断本次运行。此信号与输出预算耗尽一致，但当前 telemetry 没有保存 Provider 原始 finish reason，具体原因仍需在单例复跑时确认。中断目录 `training/runs/internal-polish-qwen3-4b-20261007/` 只有 4 条部分预测、没有 `run-info.json`，明确不构成完整实验、不能用于质量评估或模型比较。
- **判断与下一步：** 已验证产品润色工作流与本机 Ollama 的调用链可达，但当前 2,048 输出预算下 Qwen3-4B 开发样本没有成功完成，尚无任何质量收益证据。下一步先对单例分别记录完整性状态并试验更高本地输出预算/推理关闭配置（只限本机、仅合成内部样本），确认成本与工作流是否可完成后再重跑全 development 切片；正式阶段 0 盲评贡献仍为 **0**。

### 阶段 0/1/2：条件保真门禁修复与 Qwen3 本机诊断（2026-10-07 续）

- **问题判定：** 同一开发样本 `hxz-polish-reg-000007` 的输入是“如果测试通过……”，而质量门只用条件词字面包含判断，不能识别“测试通过后……”这一条件子句后置表达。修复前的 4,096-token 单例以 `condition-lost` 退稿；应用手工构造的语义等价表达也能稳定复现该误报。测试先失败，证明这一问题在规则层存在；生成原文默认不持久化，故不能反推修复前模型实际草稿一定就是该表达。
- **实现：** `ProfessionalQualityValidator` 仅在原文使用“如果/若”时，额外识别“原条件子句 + 后/之后/以后”的等价表达；“除非”不走此豁免。新增等价条件接受与“除非”反例拒绝测试，原有丢失条件/责任关系用例仍保留。诊断器另增加仅对 Qwen3 本地实验生效的 `--qwen3-no-think` 显式开关，默认关闭，记录 thinking mode 并将开关计入 prompt hash；没有改变产品默认提示、采样或用户设置。修复诊断器 Provider latency 缓冲区每样本清空导致 p50/p95 丢失的问题，并在逐例记录中保存不含内容的 Provider 请求延迟。
- **验证：** 定向质量门、润色工作流和诊断器测试 **51/51 通过**；全套 `Huaxiazi.Tests` **1,890 通过 / 11 跳过 / 0 失败**。工作区 `git diff --check` 无空白错误；Git 仅提示多处既有 LF/CRLF 行尾转换。独立单例测试中，Qwen3 `qwen3:4b`（4.0B、Q4_K_M，digest `359d7dd4…fae7`），Ollama `0.33.3`，temperature **0.4**、top_p **1.0**、max_tokens **4,096**、Medium，默认思考模式生成“测试通过后”并通过质量门：1 请求、2,659 output tokens、约 **47.7 秒**。仅作一次性对照的 `/no_think` 运行仍被修复前同一门禁拒绝，2 请求、5,407 output tokens、约 **97.2 秒**；没有证据支持把 no-think 作为默认优化，因此保留为可选诊断，不应用到产品配置。
- **全 development 本机诊断：** 原始 `cases.jsonl` SHA-256 `27ddceb1f9706f0851dae720d79ae5361638d7bcef8bd0788f4bfd3260da3195`；13 个语义族，9 个 `completed`、4 个 `needs_clarification`、0 个 `rejected/cancelled`。4 条澄清中 3 条由本地规划器预先确定、未请求 Provider；总 Provider 请求 **10** 次，输入 **9,165** tokens、输出 **26,279** tokens。工作流 p50/p95 **41.77 / 62.37 秒**，Provider 请求 p50/p95 **43.24 / 62.37 秒**；按请求平均输出 **2,627.9 tokens**。运行目录 `training/runs/internal-polish-qwen3-4b-20261007-dev-4096-final/`，完整元数据 `run-info.json`，内部样本输出留在本机文件中，未开启内容遥测；阶段 0 贡献 **0**。
- **决策、限制及接续：** 2,048 上限下前 4 条均在 2,048 输出 tokens 处返回 `Incomplete`；4,096 上限下全 13 条均完成成稿或产品澄清流程，说明 Qwen3 4B 这组短文本任务的本机诊断上限应至少使用 **4,096** 才有可用流程信号。此值仅用于本次诊断，不自动改写用户配置；更长输入、不同硬件、其它 Qwen/Ollama 版本仍需验证。13 族全为内部合成且未经人工复核，不能证明成稿的事实保真、语气偏好、直接可用率或相对模型收益；CPU/GPU档位和进程峰值内存也未采集。下一步应基于有授权并经人工复核的数据建立盲评集，再用同一冻结切分比较 API 与本地候选；在此之前不调自动路由或模型权重。Qwen3 官方说明其模型默认思考，并支持 `/think`、`/no_think` 软切换；Ollama 当前 OpenAI 兼容文档列有 thinking 控制字段，但本次实机对照未见收益，故不据文档能力声明推断项目模型质量会提高。[Qwen3 官方使用指南](https://github.com/QwenLM/Qwen3/blob/main/docs/source/getting_started/quickstart.md) · [Ollama OpenAI 兼容接口](https://github.com/ollama/ollama/blob/main/docs/api/openai-compatibility.mdx)

### 阶段 2：精确型号的本地推理预算预设（2026-10-07）

- **问题与约束：** Ollama `qwen3:4b` 的本机诊断在 `max_tokens=2048` 时前 4 个 Provider 请求全部 `Incomplete`；将上限调到 4096 后，13 个内部 development 族产生 9 条成稿、4 条澄清、0 条 Provider 失败。要让用户实际可用该预算，同时避免把单机合成样本结论外推到云端 Qwen、其它 Qwen 尺寸或远程兼容端点；也不能无提示改写用户现有参数。
- **实现：** `ProviderInferencePresets` 仅对 `Type=Local + Platform=Ollama + Protocol=OpenAICompatible + Model=qwen3:4b + HTTP loopback:11434/v1` 的 Medium 档使用 temperature **0.4**、top_p **1.0**、max_tokens **4096**、timeout **120 秒**。通用 Medium 仍为 **0.4 / 1.0 / 2048 / 120 秒**；Low/High、云端 Qwen、其它本地型号、其它端口和非回环地址保持原值。设置说明显示该建议来自项目内部合成短文本诊断且不是盲评结论。仅当精确档案现值与建议预设不同，UI 才显示“应用 Qwen3 本地诊断预算（4096）”按钮；点击前不改配置，点击后调用既有 provider 参数映射，`AIService` 在结构化请求体实际发送 `max_tokens=4096`。切换型号、Provider、API 地址时，说明和按钮可用性同步刷新。
- **TDD 与验证：** 新增精确匹配矩阵覆盖 Qwen3 alias、8B、11435 端口、Cloud 类型和云端 Qwen；新增设置页测试证明原有 2048 在点击前不变，应用按钮点击后才变成 4096，换型号/端点后按钮退出；新增请求体测试确认 `qwen3:4b` 的结构化调用确实发送 `max_tokens=4096`。新行为实现后 Provider 预设/设置页/UI/请求映射筛选 **126/126 通过**；最终全套 `Huaxiazi.Tests` **1,898 通过 / 11 跳过 / 0 失败**。按钮与当前参数提示见 `Views/ProviderProfileEditView.xaml`，对应配置及 UI 状态在 `ViewModels/SettingsViewModel.cs`。
- **预期收益与限制：** 对已观察的本机 Qwen3-4B 短文本任务，生成上限从不足以完成（4/4 截断）改为可容纳实际输出（13 族运行没有 Provider 失败；实际请求平均输出约 2,628 tokens，单请求观察到的最大值 3,506）。这不是事实保留、语气、直接可用率或总体延迟改善证明；一次诊断 p50/p95 Provider 延迟仍约 **43.24/62.37 秒**。建议不会自动套到旧配置；复杂任务、其它设备和其它 runtime 需要分别测量。后续继续接 API 与其它本地型号的冻结盲评比较，并按硬件档采集延迟/内存；LoRA 仍不启动。

### 阶段 2：Qwen3 本地预设保存与重载验证（2026-10-07）

- **输入/验收：** 在隔离配置目录中加载 Ollama `qwen3:4b` Medium profile（2048），先不点击建议按钮保存并重载，要求仍为 2048；然后点击“应用 Qwen3 本地诊断预算（4096）”，经真实 `SettingsViewModel.TrySave` 与 `ConfigService.Save/Load` 往返后，要求型号、Medium 档和 4096 上限保持一致。
- **结果：** 新增 `AppliedQwen3MediumBudgetSurvivesSettingsSaveAndReload` 测试，完整跑通上述两条配置路径，**1/1 通过**。所有文件写入 `TestHelpers.UseIsolatedConfigDirectory()` 建立的临时配置目录，测试结束后恢复 App.Settings、Dispatcher 并清理目录；未触碰真实用户配置。
- **衔接：** 预设的设置页可用性、参数应用、请求体字段映射、序列化和重载目前分别有回归覆盖。下一步可做桌面设置页人工/自动交互验收，再进入冻结盲评上的 API/本地统一比较；当前 13 族样本未经人工复核，质量结论和阶段 0 贡献仍为零。

### 阶段 3：生成结果重试/撤销/采纳时关联最终编辑信号（2026-10-07）

- **问题与边界：** 工作流原先只在保存归档时把“原生成稿→最终编辑稿”交给偏好反馈会话。用户直接重试或复制采纳时虽会记录 Retry/Acceptance，但改短、改长和删套话信号没有和该结果关联，导致未归档用户的交互数据偏缺。只记录方向和预置表达计数，不持久化正文；保留当前隐私与场景边界。
- **改动：** `MainViewModel` 抽取 `CapturePreferenceFeedbackEdit`，在显式复制采纳、重试拒绝以及撤销生成结果前采集一次最终稿差异；已有保存归档路径继续工作。相同结果重复采集只更新会话中的布尔/集合快照，不增加事件行数。引入内部 `IClipboardService` 边界并允许 `MainViewModel` 注入实现，生产仍使用系统 `ClipboardService`，自动复制和粘贴也沿同一服务入口。
- **TDD/验证：** 新增真实提示词优化工作流测试：生成后改短，分别直接重试、复制采纳、撤销，断言任务类别下对应 Retry/Acceptance/Undo 与短稿方向各记 **1**，偏好序列化不包含编辑正文；复制测试用记录型替身，不读写操作系统剪贴板。重试回归实现前按预期失败（`RejectedShortenedOutputs` 实际 0）。初次组合测试因系统 Temp/LocalAppData 路径拒绝、SQLite 数据库文件打不开失败；重定向至工作区可写临时目录后，`MainViewModelTests` 与反馈会话测试 **41/41 通过**；设置、偏好建议/反馈/导出和风格偏好相关组合 **144/144 通过**，项目和测试程序集均由 `dotnet test` 编译成功。
- **预期收益与限制：** 显式重试/撤销/采纳现在可将最终用户编辑方向关联到对应的输出结果，改善无归档路径下偏好信号完整性；没有模型质量 A/B 或偏好命中率数据，质量收益仍未测。下一项应补查重试和撤销反馈的并发/新一轮生成边界，并验证偏好设置 UI 中编辑、确认、重置、导出和彻底清除路径；随后仍需冻结评测验证个性化收益。

### 阶段 3：偏好清除与用户控制备份的保留边界（2026-10-07）

- **问题与约束：** “清除全部偏好”会重置当前 `config.json`，但数据维护创建的 ZIP 含创建时配置快照，用户手动导出的 JSON 同样是独立副本。自动改写/删除用户指定的备份会破坏其控制的文件；不提示则会让“清除”被误解为副本也已消失。
- **决策与改动：** 保留用户控制的导出文件和备份 ZIP，不在重置时静默修改或删除。`ExpressionAbilitySettingsView` 在清除说明及按钮提示中明确当前清除范围、ZIP 配置快照和需手动删除的副本；`SettingsViewModel` 清除成功提示说明副本仍保留；`docs/隐私与数据.md` 记录同一策略，并注明恢复 ZIP 会跳过 `config.json`。重置逻辑和数据保留行为未改变。
- **TDD/验证：** 扩展 `ResetExpressionPreferences_PersistsFullClearBeforeReportingSuccess`：先创建包含已确认偏好的真实 ZIP 备份，再触发真实设置重置，检查活动配置中确认偏好、交互统计、禁用表达和总计数均被清除；ZIP 中旧 `config.json` 快照仍保留“旧偏好”，清除反馈必须提及备份。新断言先失败（成功提示无“备份”说明），更新产品提示和隐私文档后，新增 `RestoreBackup_DoesNotRestoreTheConfigSnapshotThatContainsPreferences` 回归证明备份快照不会被恢复到数据目录；重置、备份/恢复、设置产品 UI、WPF 视图烟测组合 **70/70 通过**。
- **影响与接续：** 用户能辨别当前配置已清空与历史副本仍在，降低误认为所有副本均已删除的隐私风险；历史 ZIP 和 JSON 不会被应用静默破坏。该步骤不证明任何生成质量收益。下一步继续验证偏好保存/分享开关与无痕条件组合、导出字段边界；之后回到阶段 0 盲评数据审查和个性化 A/B。

### 阶段 3：无痕学习边界与云端偏好披露端到端核验（2026-10-07）

- **输入/问题：** 偏好路由器已有“云端不发送确认偏好，除非用户显式同意”策略，主流程也用学习开关和无痕开关保护交互记录；但缺少实际生成请求上的组合回归，不能只靠策略函数单测证明最后 Prompt 遵守了隐私选择。
- **验证设计：** `MainViewModelTests` 使用 fake generation client 捕获最终 system prompt，测试没有网络。按“学习开关 × 无痕模式”四个组合执行提示词优化、编辑并复制：只有学习开启且非无痕时计数增加；其余三个组合无采纳统计。无痕时生成输入未进入本机历史。按云端分享关闭/开启和本机 loopback Provider 三组组装实际 Prompt：云端未同意时不含唯一确认偏好标记，显式同意后包含；本地请求在未开启云端分享时仍包含已确认偏好；交互统计标记始终不发送。
- **测试夹具纠偏与结果：** 首次云端开启用例未包含标记，检查发现夹具选用默认“通用任务”而偏好建在“编程开发”范围；固定类别后行为符合预期。`MainViewModelTests`、`ProviderRouterTests` 与偏好反馈会话组合 **58/58 通过**。产品生成逻辑未改变，完成的是端到端隐私边界验证。
- **影响与限制：** 在测试覆盖的四组本地学习开关条件、两种云端分享状态及本地请求状态下，采纳统计与确认偏好的实际提示词去向均符合用户设置；未发真实云端请求，不能据此报告 Provider 服务端行为或质量收益。下一步对导出内容和无痕状态持久化/恢复做边界验收，再回到冻结集 A/B。

### 阶段 3：偏好导出边界与隐私设置保存/重载（2026-10-07）

- **问题与约束：** 偏好导出需带走可迁移的结构偏好、学习统计和输出风格，同时不得顺带暴露 Provider 配置、API 密钥、历史正文或“云端分享/无痕/学习开关”等整机设置；三个隐私开关还须在用户重启/重载设置后保持明确值。
- **现状判断与改动：** 源码显示 `ExpressionPreferenceExportService` 只接收偏好 profile 和输出风格，未接收 AppSettings，`SettingsViewModel.TrySave/Load` 已保存三个 bool。本步不改生产行为，补强回归：导出测试明确拒绝 providerProfiles、incognitoMode 和 shareConfirmedPreferencesWithCloud；设置测试从默认值修改偏好学习=false、云端分享=true、无痕=true，经真实 TrySave/ConfigService.Load 后重新构造 SettingsViewModel，验证三个值恢复且无痕时历史存储编辑器不可用。
- **验收结果：** `SettingsViewModelTests`、导出、候选/反馈、风格解析、设置 UI、MainViewModel 工作流和 ProviderRouter 组合 **193/193 通过**；`dotnet test` 同时编译应用与测试程序集。
- **影响与接续：** 导出范围和开关持久化在当前实现中符合隐私契约；本步没有改生成提示、模型、路由或设置默认值，也未发送真实 Provider 请求，不产生模型质量收益结论。接续需继续核验用户停止学习后未完成会话、无痕切换期间的状态边界；评测数据可用时再比较个性化 A/B。

### 阶段 3：未保存的学习/无痕开关即时约束设置页信号（2026-10-07）

- **问题与复现：** `SettingsViewModel.OutputStyle` 变更会统计用户显式风格选择，但原 setter 读取 `App.Settings` 中的旧保存值，而设置页开关是尚未提交的 ViewModel 草稿。用户在设置页关闭学习或开启无痕后，紧接着选择输出风格，仍会按旧值记录；反向切换则可能漏记。新增保存值/草稿值相反的四组测试，修复前 **4/4 失败**（两组意外记、两组漏记）。
- **实现：** 风格选择统计条件改为使用设置页当前 `PreferenceLearningEnabled` 与 `IncognitoMode` 草稿值；实际输出风格设置仍按既有 TrySave 流程持久化。本步不改主窗口生成时的数据门禁，不改变配置字段、默认值或 prompt。
- **验证：** 四种存储值与草稿值组合修复后 **4/4 通过**。阶段 3 设置保存/重载、导出、反馈、候选、设置 UI、MainViewModel 和 ProviderRouter 相关集合 **197/197 通过**；导出还明确拒绝 Provider profiles、无痕状态和云端分享设置字段。
- **收益与接续：** 修复在设置尚未提交时仍遵从用户即时隐私选择，避免无痕/停学草稿状态被旧值绕过。量化效果是 4 个相反状态组合由 **0/4 正确**提升至 **4/4 正确**；模型输出质量、风格偏好统计质量和云端真实 Provider 行为仍未测。下一步检查学习会话跨重试、撤销和无痕切换的状态清理，再使用冻结数据评估偏好收益。



### 阶段 3：隐私切换与重试后的反馈会话隔离（2026-10-07）

- **问题与输入：** 接续检查学习开关/无痕切换、重试、撤销是否会让旧生成结果的反馈继续写入，或把旧输出信号记到下一任务。复核 `MainViewModel` 会话创建点、设置重载和复制/重试/撤销/保存入口；会话需绑定单次输出，并遵从生成时有效的学习许可。
- **实现与回归：** `ExpressionPreferenceFeedbackSession` 暴露只读 `IsActive`；当设置重载后当前选择不允许学习时立即取消会话；各反馈写入入口要求学习仍开启且会话有效，避免关闭学习/进入无痕后再恢复开关使旧会话“复活”。新增跨隐私切换的复制、重试、撤销用例，并新增跨类别重试集成用例：通用任务的编辑拒绝信号记回通用任务，重试后编程开发任务的采纳信号只记回编程开发。
- **验证：** 会话切换、任务反馈和 ViewModel 定向集合 **57/57 通过**；`git diff --check` 通过。跨类别测试首次运行因夹具未注入剪贴板替身而未走到反馈断言，加入替身后通过；该失败是测试设置问题，没有导致生产代码变更。
- **结论与边界：** 对覆盖的无痕/学习开关往返、复制/重试/撤销和跨类别重试路径，旧会话没有恢复写入，信号按输出任务场景隔离。没有测量模型输出质量、偏好命中率或用户满意度。本步收束反馈会话的主要生命周期边界；下一步转回阶段 0/1：运行并审查已落地的冻结评测数据与候选比较流程，先确认数据/统计是否可复跑，再决定是否进行个性化 A/B；不以本地生成条数替代盲评证据。

### 阶段 0/1：内部 Qwen3 诊断可复跑性与 seed 控制（2026-10-07）

- **节点与输入：** 回到阶段 0/1 的评测可复跑核查。`polish-regression-validate --dataset-dir datasets/polish-regression-v1` 通过：3,000 行、16 个语义族；该集仍为内部合成回归，阶段 0 贡献为 0。正式 `datasets/ai-evaluation/blind-eval.jsonl`、source register 和 evidence manifest 当前均不存在，不能启动正式盲评。
- **标注副本导入审查：** `near-duplicate-human-review - 副本.xlsx` 对 19 对候选均有 decision，但 rationale 19/19 为占位值“无非空理由”；19/19 对的 `context.purpose` 不同。运行既有 `polish-regression-neardup-import-workbook` 后，导入器按契约退出码 2，拒绝这 19 对并指出需要具体理由；未生成导入结果，也未修改副本、候选包或冻结 v1。决定属于同一任务意图还是“同骨架不同任务”仍需人工确认，不能由 AI 代填理由。
- **稳定性实测：** 对同一 Qwen3-4B digest、Ollama 0.33.3、温度 0.4、top_p 1、max_tokens 4096、相同数据/提示/输出契约/当前 app 与 runner 二进制，无 seed 两轮的完成/澄清分布分别为 5/8 和 7/6；13 条中有 **6 条（46.2%）状态翻转**。Ollama 官方 OpenAI 兼容接口将 `seed` 列为支持字段，并标记可复现输出能力，因此诊断工具增加显式 seed 作为抽样控制变量；该外部能力说明不保证特定模型与运行硬件逐字节确定。
- **改动：** `AIService` 增加仅供诊断使用的可选 seed，只有 Local + Ollama + OpenAICompatible 才能设置，连接测试与默认产品请求不发送该字段；`internal-polish-run --seed N` 将整数送入实际 Ollama chat request，并把 `diagnostic_seed` 写入 run-info。实验固定 `20261007`，只用于复跑控制；产品温度、top_p、token 上限和用户配置均未改。
- **验证与结果：** 新增 seed 映射、默认省略、非本地 Ollama 拒绝三项测试，修复前 seed 用例按预期失败，修复后 **3/3 通过**。`AIServiceTests` + `InternalRegressionDiagnosticCliTests` **278/278 通过**。同 seed 的两次 13 族复跑均为 9 完成、4 澄清、0 拒绝/取消、10 次 Provider 请求；状态仅 **2/13（15.4%）翻转**，相较无 seed 的 6/13 减少 4 例，但逐例成稿仍有 **5/13** 不同。输出 token 为 27,247 / 28,653，Provider p50 为 45.36 / 50.77 秒、p95 为 64.06 / 64.91 秒。seed 有助于降低状态波动，但没有实现字节级确定性，也没有质量/事实保留盲评；三次前后比较均不构成模型晋级或用户质量结论。
- **本步结论与衔接：** 当前 runner 能记录模型、runtime、参数、seed、提示/契约和数据哈希，并能运行内部真实业务工作流；正式基线的核心缺口仍是合规且人工复核的冻结样本。下一步先保留现有内部诊断为开发信号，修复/复核 near-duplicate 副本的 19 条具体理由与语义决定，再进入评测样本整合；外部来源证据和双评证据未齐前，不把内部合成数据计入阶段 0，也不据此调模型权重。

### 阶段 0/1/2：Qwen3 seed 诊断有效性复核与思考模式状态纠正（2026-10-07）

- **输入与问题：** 接续同 seed 两次内部业务运行中 5/13 成稿不同的现象，用本机 Ollama `qwen3:4b`（digest `359d7dd4…fae7`，Q4_K_M，Ollama 0.33.3）检查底层同请求可复现性。约束是不能把截断响应当成成功成稿，也不能将参数/提示开关“已发送”推断为模型实际模式。
- **直接 API 观测：** OpenAI-compatible `/v1/chat/completions` 使用相同请求、seed=20261007、temperature=0.4、top_p=1.0 时，max_tokens=256 的两次响应均为 `finish_reason=length`、正文空；加 `reasoning_effort=none` 后，两次哈希一致，但 256 上限返回 418 字符并截断，1024 上限返回 1,465 字符仍截断。短指令的 max_tokens=64 控制请求也用满上限。原生 `/api/chat` 的 `think=false` 对照同样到达上限；在 system 中加入官方文档描述的 `/no_think` 后，128-token 响应仍为 `done_reason=length` 且最终正文为空。`/api/show` 报告该模型有 `thinking` capability 和 Qwen3 模板；这些请求的 seed 哈希完全相同，但均未获得有效完成响应。
- **判断：** 同 seed 对这些被截断响应仍产生逐字节相同的结果，支持当前运行时对固定短请求存在可复现性；它不能解释此前 13 条业务工作流中 5 条成稿差异，也不能证明完整成稿已确定性复现。更关键的是，本机 runtime 对非思考开关的实测不充分/不符合预期，故不得把 `/no_think` 写成“思考已关闭”，也不应用该实验推导默认参数。
- **改动：** `InternalRegressionDiagnosticService` 的 `run-info.json` 保留 `qwen3_thinking_mode` 字段以兼容现有消费者；启用开关时从 `disabled_by_no_think_prompt_switch` 修订为 `no_think_prompt_switch_requested_effect_unverified`，默认仍为 `provider_default`。包装器 XML 注释同步说明这是请求开关。实际 prompt、sampling 参数和用户配置未改变。
- **TDD/验收：** 新增两个模式元数据断言，先失败（状态解析器不存在），实现后 `InternalRegressionDiagnosticCliTests` **5/5 通过**；测试构建同时编译主项目、DatasetBuilder、runner 与测试程序集。等待完成的本轮工作区 `git diff --check` 未发现空白错误；只报告检查中出现的既有 LF/CRLF 提示。
- **研究依据与边界：** Qwen 官方仓库说明 `/no_think` 可放在 system 或 user message；Ollama 当前兼容文档列出 `seed`、`reasoning_effort` 与 thinking 支持字段。这些是当前官方能力文档，不等于项目安装的 0.33.3 runtime 对具体 Qwen3 模型具有相同语义。[Qwen3 官方仓库](https://github.com/QwenLM/Qwen3) · [Ollama OpenAI compatibility](https://github.com/ollama/ollama/blob/main/docs/api/openai-compatibility.mdx)
- **下一步触发：** 现有本机诊断的业务运行均已完成；要查明 5/13 差异，下一步应为诊断请求保留非内容的原始终态（finish reason、请求序号、修复次数、是否有最终正文），并对同一单例输入捕获各次实际提示/参数哈希。若请求/哈希不一致，定位工作流分支；若一致但有效完成输出仍不同，再独立比较 Ollama 完成结果。所有内容仍只在本机、内部合成样本上诊断，阶段 0 贡献为 0。

### 阶段 0/1/2：逐请求终态与哈希追踪、同样本 seed 复跑（2026-10-07）

- **计划节点与问题：** 承接上一节点“为诊断请求保留非内容终态、请求序号、修复次数和实际提示/参数哈希”。要区分润色工作流是否生成了不同请求，还是在输入/参数一致时底层产生不同完整输出；约束为仅在本机内部合成样本运行，不持久化提示正文、Schema 或密钥，正式盲评贡献保持 0。
- **实现：** `ProviderRequestTelemetry` 增加协议终态字段；解析 OpenAI Chat、Responses、Anthropic Messages 与 Gemini 的 finish/stop 类别，并按各协议已知枚举白名单过滤，未知 Provider 字符串不进入遥测。内部 runner 为每条实际生成调用（含结构化调用和修复调用）记录序号、调用类型、system prompt/user input/JSON Schema 哈希、提示包哈希和参数包哈希；每样本另记录 Provider request id、HTTP 状态、结束类别、延迟与 token 用量。追踪中不保存原文字段，哈希仍由内容导出，产物限制在本机受控目录。
- **可复跑入口：** 新增 `internal-polish-run --sample-id <development-case-id>`。加载器仍先校验完整 cases/manifest 哈希、来源、split 和 development 族数量，再只选择精确匹配的单条 development 样本；regression 或未知 ID 被拒。`run-info` 升至版本 2，标记 trace schema 1、完整开发样本数、所选 ID、参数包哈希及无 prompt 内容标记。
- **两轮实测：** 针对此前发生过状态翻转的 `hxz-polish-reg-000009`，固定 Qwen3-4B digest `359d7dd4…fae7`、Ollama 0.33.3、temperature **0.4**、top_p **1.0**、max_tokens **4,096**、seed **20261007**、同一应用/runner 代码，连续运行两次。两次均为 completed、各 1 个 structured Provider 请求、0 次修复、HTTP 200、finish_reason=`stop`。system prompt、user input、JSON Schema、完整提示包及参数包哈希 **全部一致**；输出哈希不同，输出 token **3,058 vs 2,658**，Provider 延迟 **54.59 vs 45.50 秒**。两次 `run-info` 均为 1/13 单样本执行，阶段 0 贡献 0。
- **判断：** 对这条样本，差异不是可见的业务分支、修复次数、提示或参数漂移；变化出现在相同完整请求后的模型/运行时响应。仅凭两次不能区分采样随机性与底层并行推理非确定性，也不能估计整体波动率，更不证明输出质量好坏。上一节的短请求控制均被截断；本次是首次同请求、同 seed、正常 stop 的完整成稿对照，证据强于截断哈希比较。
- **验证：** 先后新增 Provider 终态成功/截断/未知枚举隐私用例和追踪哈希用例；`AIServiceTelemetryTests + InternalRegressionDiagnosticCliTests + AIServiceTests` 最近一次定向 **287/287 通过**（后续文档状态字段微调后诊断/遥测再跑 **13/13 通过**）。全套 `Huaxiazi.Tests` **1,934 通过、11 跳过、0 失败**。CLI 帮助包含 `--sample-id`；workspace diff-check 未发现空白错误，Git 仅报告已有文件的 LF/CRLF 转换提示。
- **研究依据：** Provider 终态按官方 API 定义的结构化枚举记录，而不存 arbitrary reason 字符串：[OpenAI Chat Completions](https://platform.openai.com/docs/api-reference/chat/object)、[Google Gemini FinishReason](https://ai.google.dev/api/generate-content)、[Anthropic stop reasons](https://docs.anthropic.com/pt/api/handling-stop-reasons)。官方枚举只支撑元数据适配，不构成对项目安装 Ollama 版本的兼容性或模型质量保证。
- **下一步触发：** 保持同一条完整 structured 请求与全部参数不变，再对该样本做至少 5 次独立运行，计算输出哈希一致率、输出 token 与延迟的范围；若仍变化，报告为当前 runtime/模型在该硬件下的复跑变异，不再把 seed 宣称成逐字节可复现。随后再检查首轮输出/修复输出的一致性，并继续推进外部获准盲评集；这组内部实验不改变温度、top_p、推理模式或模型默认设置。

### 阶段 0/1/2：Qwen3 单样本同参五次复跑结果（2026-10-07）

- **输入/控制：** 延续上一条 `hxz-polish-reg-000009` 同一完整产品请求，在同一模型 digest、Ollama 0.33.3、seed=20261007、temperature=0.4、top_p=1.0、max_tokens=4096 下完成 5 次独立 runner 进程运行。每轮各执行 1 个结构化 Provider 请求，无修复、无云端流量；内部样本阶段 0 贡献为 0。
- **实测：** 5/5 `completed` 且 `finish_reason=stop`。5 轮 system/user/schema/prompt/parameter SHA-256 全部一致，模型和 runtime digest/version 全部一致；输出 SHA-256 有 **2 种**，其中 **4/5** 相同、**1/5** 不同。输出 token 数范围 **2,658–3,058**（相差 400，约 15.0% 相对较小值）；单次 Provider 延迟范围 **45.33–54.59 秒**。没有用参考成稿或未审标签计算质量分。
- **判断：** 固定 seed 在此本机堆栈产生 4/5 相同成稿和 1/5 不同变体；不能称为完全确定，也不足以估计一般任务的复跑一致率或认定是温度采样、GPU/CPU并行归约或 runtime 行为所致。最高延迟/唯一长输出发生在第一轮，后四轮延迟集中约 45.3–45.6 秒且输出相同；这可能与热身/运行状态有关，但 5 次单样本不足以确认因果。本步不改产品参数与模型默认。
- **验证/产物：** 五轮输出与 `run-info` 分别位于 `training/runs/internal-polish-qwen3-4b-20261007-dev-4096-sample-000009-trace-a` 至 `...-e`；trace 对象仅含哈希及非内容调用元数据。全套 `Huaxiazi.Tests` 最近通过 **1,934/1,945**（11 项既有跳过，0 失败），末尾状态说明改动的窄集 **13/13** 通过。
- **偏差自审与下一步：** 计划要求先把请求/参数不一致与后端输出差异分开；请求哈希与终态遥测现已把二者分开。原因归属仍未完全解释，下一子任务将对 Qwen 官方建议的 thinking 参数（温度 **0.6**、top_p **0.95**）做同样 5 次内部复跑对照，同时保持当前产品预设不变。若输出仍出现少数离群，后续加入运行时/硬件执行档元数据再分层；在人工质量评测前，不按一致性或 token 数单独选择生产参数。
- **参数来源：** Qwen3 官方指南给出的 thinking 模式建议为 temperature 0.6、top_p 0.95，并明确警告不要用 greedy decoding，以免性能下降或出现无限重复；该建议只作为下一项开发诊断候选，不自动改写产品设置。[Qwen3 官方 Quickstart](https://github.com/QwenLM/Qwen3/blob/main/docs/source/getting_started/quickstart.md)

### 阶段 0/1/2：Qwen3 官方采样建议对照与完整开发切片（2026-10-07）

- **输入/控制：** 先对 `hxz-polish-reg-000009` 在相同模型 digest、Ollama 0.33.3、seed=20261007、max_tokens=4096 下按 temperature=0.6、top_p=0.95 重复 5 次；随后以这组参数运行完整 13 族 development split。另与已完成的 temperature=0.4、top_p=1.0 单样本五次及两轮全开发切片作内部诊断比较；未发起云端请求，未修改生产默认值。
- **单样本结果：** 0.6/0.95 五次均 `completed`、`finish_reason=stop`、1 次 structured 请求、输出均为 2,029 tokens，输出 SHA-256 **1 种/5 次**，Provider 延迟 **34.30–34.74 秒**；各次 prompt 与参数包哈希一致。对照组 0.4/1.0 为 **2 种/5 次**，输出 **2,658–3,058 tokens**、延迟 **45.33–54.59 秒**。样本量仅 1 个，且两个采样参数同时改变，不能归因到单一参数，也没有质量盲评。
- **完整开发切片结果：** 0.6/0.95 在 13 条上为 **8 完成、5 澄清、0 拒绝/取消**，10 次 Provider 请求；输入/输出 token 为 **9,165/26,390**，Provider p50/p95 为 **49.19/57.41 秒**，工作流 p50/p95 为 **35.56/57.41 秒**。此前两个 0.4/1.0 对照均为 **9 完成、4 澄清、0 拒绝/取消**、10 请求，输出分别 **27,247/28,653 tokens**。结果显示状态分布不同；由于开发语料未经人工复核，token 和延迟变化不能作为质量优劣判断。
- **判断/下一步：** 更高 temperature 与较低 top_p 这组参数在该单样本上复跑哈希更稳定、生成 token 较少且观察到的延迟更低，但完整切片状态有一例从完成转为澄清，不能称为质量提升或生产推荐。维持现有产品值。所有 run-info 标记 `phase_0_gate_contribution=0`、`request_trace_content_included=false`；模型进程峰值内存未测量。需要授权并经人工评审的样本后，才能对质量、事实保真和直接可用率作比较。

### 阶段 2：手动路由尊重显式备用模型配置（2026-10-07）

- **问题与约束：** 设置页允许填写“明确配置的备用模型”，但 `ProviderRouter` 的 `Manual` 分支始终返回空备用项；因此用户即使显式配置了备用模型，手动模式下主模型失败也无法按配置切换。手动模式必须继续保持所选/任务绑定模型为主模型；未配置备用时不隐式切换；`LocalOnly` 必须继续拒绝任何云端 fallback。
- **TDD 与实现：** 新增 `ManualMode_UsesExplicitlyConfiguredFallbackWithoutChangingPrimarySelection`，先运行确认失败（主模型为 active 正确，但备用实际为 null）；随后仅在 Manual 分支返回不同于主模型、且用户显式配置的备用 profile。没有备用或备用与主模型相同则仍返回 null。实际失败重试由既有 `ProviderFallbackGenerationClient` 承担，UI 已展示实际使用模型。
- **验证：** `ProviderRouterTests`、`ProviderFallbackGenerationClientTests`、LocalOnly、PreferLocal 和新增 Manual `MainViewModel` 集成测试合计 **19/19 通过**。新增用例确认云端主模型失败后仅调用已配置本地备用，返回备用结果并更新“本次实际模型”标签；LocalOnly 仍不返回云端 fallback，PreferLocal 原行为通过。全套 `Huaxiazi.Tests` 在把 `TEMP`、`TMP`、`LOCALAPPDATA` 和 `APPDATA` 指向工作区可写临时目录后 **1,936 通过、11 跳过、0 失败**；默认系统临时目录首轮复跑有写入权限错误并中止，故采用隔离可写目录验证。未执行真实 Provider 请求。
- **影响与自审：** 修复了“显式配置却不生效”的路由契约缺口；只有失败后才会尝试已选备用，故不会改变正常成功请求的 Provider、延迟或费用。跨云/本地备用可能涉及用户内容传输，原有偏好披露策略继续按所有可能目标及云端授权进行过滤。本步没有质量 A/B 收益证据；下一项继续阶段 1 的 Provider/结构化契约审计，发现可证实的能力或终态缺口后再逐项修复。

### 阶段 0 数据准备：人工近重复裁定副本导入核验（2026-10-07）

- **输入与目标：** 核验项目负责人所称已填写的 `near-duplicate-human-review - 副本.xlsx` 是否可作为 W2.3 语义族裁定进入后续回归集整理；不修改原工作簿、评测包或现有裁定记录。
- **结果：** 副本 `候选对审阅` 页有 19/19 行的决定、审核者字段与时间，但理由列 19/19 都是“无非空理由”占位语。调用现有 `polish-regression-neardup-import-workbook` 时，导入器以 `decision-incomplete` 拒绝全部 19 对，未生成新裁定文件。权威原始工作簿仍为空，既有 `decisions.simulated-review.jsonl` 和 `gate-status.json` 仍标记模拟身份、`awaiting_human_review`、阶段 0 贡献 0。另一个 supplemental spec review v2 的人工决定字段仍空，清单标记 `generation_authorized=false`。
- **判断与边界：** 该副本的决定值并非空，但缺少具体、可追溯的语义理由，且与模拟审阅记录的结论不同；不能用 AI 自动补理由或将格式占位视作真人裁定。当前只核验了记录与导入契约，没有认定任何候选应该合族，也没有改数据或冻结切分。
- **衔接：** 需由原审阅者按实际判断为 19 对逐条补充具体理由并保留真实 UTC 时间/身份信息；随后重新导入至新路径并运行近重复裁定校验。只有其通过后，才继续 W3.3 族代表复核与 W4 内部补样；此 W2.3 结果无论如何都不计入正式盲评阶段 0 门槛。

### 阶段 1：Kimi K2.6 思考开关映射到低推理档（2026-10-07）

- **计划节点与问题：** Provider 能力审计。应用现有 `InferenceLevel.Low/Medium/High/Custom` 对 Kimi K2.6 只调整 `max_tokens` 与超时，K2.6 默认仍启用思考；因此 Low 档并未降低该模型推理开销。约束是仅使用官方明确支持的字段，不把 K2.6 控制外推到 K2.7 Code、K3、OpenRouter 或自定义代理，也不误发固定采样参数。
- **研究与决策：** Kimi 当前参数文档确认 K2.6 默认 `thinking.type=enabled`，可切换为 `disabled`；K2.7 Code 始终开启且拒绝禁用；K3 不使用 `thinking`，通过 `reasoning_effort=low/high/max` 控制强度。思考会消耗额外推理 token 并增加延迟。将 Low 映射为 K2.6 的显式 `thinking: { type: "disabled" }`；Medium/High/Custom 省略字段并保留服务默认思考，避免把二态开关伪装成多档强度。官方同时说明 K2.6 关闭思考后的 temperature 固定为 0.6，启用时固定 1.0，top_p 固定 0.95；应用继续省略 temperature/top_p，让服务按 thinking 模式取值。依据：[模型参数参考](https://platform.kimi.com/docs/api/models-overview)、[思考模型](https://platform.kimi.com/docs/guide/use-thinking-models)、[推理强度](https://platform.kimi.com/docs/guide/use-reasoning-effort)。
- **代码改动：** `Services/ProviderCapabilities.cs` 为精确直连 `Kimi + OpenAICompatible + kimi-k2.6` 标注 `SupportsThinkingToggle`，并让能力摘要按档位说明具体请求行为；`Services/AIService.cs` 仅在非连接测试、上述能力已核验且档位为 Low 时发送 `thinking.type=disabled`。Provider 平台、协议和模型均参与能力匹配；K2.7/K3、OpenRouter 和自定义接口不发送该字段。更新 `Huaxiazi.Tests/AIServiceTests.cs` 与 `Huaxiazi.Tests/ProviderPlatformTests.cs` 覆盖 Low/Medium/High/Custom、K2.7 两个 ID 和设置说明。
- **验收与量化影响：** 修复前新增测试 5 项失败：K2.6 Low 请求缺少禁用字段，四个档位的设置说明均未解释实际思考行为。修复后 Kimi K2 生成请求、K2.6 分档说明和 K3 回归定向集合 **17/17 通过**。请求参数变化可明确量化为：K2.6 Low 档增加 1 个受支持的 `thinking` 对象；其余档位、其他 Provider、连接测试保持省略。未发真实 API 请求，尚无真实 token、延迟、费用或成稿质量对照，因此这是推理控制能力补齐，不代表润色质量提升。
- **自审与上下游：** 采用 Low=关闭是因为产品档位名表达低推理、官方只提供二态开关，且思考存在额外 token/延迟成本；Medium/High 在 K2.6 目前不能提供不同于默认开启的细粒度 effort，设置说明明确这一限制。实现不采集思考正文，Kimi 响应解析仍只交付 `content`。下一步继续审查其他 Provider 的“档位显示与实际请求是否一致”，并评估上下文窗口能力是否应参与请求前置检查；任何质量/成本收益需经真实服务计量和有效评测后再报告。
- **全回归复核追加结果：** 使用隔离的工作区临时目录执行 `dotnet test Huaxiazi.Tests/Huaxiazi.Tests.csproj --no-restore`，全套 **1,944 通过、11 跳过、0 失败（共 1,955）**，同时编译主程序、DatasetBuilder、runner 和测试程序集。`git diff --check` 未报告空白错误；出现的仅为工作区既有 LF/CRLF 转换提示。
- **同节点自审修正：** 跨层审计发现第一版 K2.6 能力摘要在 Low 档已关闭思考时仍显示 `temperature=1.0`，与官方参数表不符。为 Low/Medium/High/Custom 增加按模式核验 temperature 的测试；修复前 Low 用例失败、其余 3 档通过。摘要现明确显示 Low=服务固定 `temperature=0.6`（关闭思考）、其余档=固定 `temperature=1.0`（启用思考），所有档 top_p 固定 0.95，应用均省略固定采样字段。定向 Kimi 集合重跑 **17/17 通过**。
- **最终验收复跑：** 上述说明文案修正后再次执行全套 `Huaxiazi.Tests`，结果仍为 **1,944 通过、11 跳过、0 失败（1,955 总计）**。因测试自身在隔离测试输出目录创建裁定夹具，未写入权威人工标注工作簿或冻结评测数据。

### 阶段 1：Kimi 上下文窗口元数据与预检接口可行性（2026-10-07）

- **计划节点/输入：** 继续 Provider 能力审计，确认已登记的 `ContextWindowTokens` 是否覆盖 Kimi 现行型号，以及这些上限能否用于真实请求前检查。问题本质是长提示超出模型输入容量时会被拒绝或无法生成；约束包括中文 token 化差异、严格 Schema 可能在消息外传递、额外网络时延/费用以及不得新增隐式云端数据发送。
- **官方证据：** Kimi 参数文档列明 K3 上下文 1M tokens，K2.6/K2.7 Code 为 256K，且高速版与 K2.7 Code 的模型/参数约束相同；官方 token estimate endpoint `POST /v1/tokenizers/estimate-token-count` 接收 model 与 messages 并返回估算 `total_tokens`，其说明使用“估算”，请求再次携带 prompt messages。来源：[Kimi 模型参数参考](https://platform.kimi.com/docs/api/models-overview)、[Token 估算 API](https://platform.kimi.com/docs/api/estimate)。
- **方案取舍：** 本步只登记有明确文档依据的 exact-ID 上下文窗口，并在模型能力说明中展示。暂不在每次生成前调用 token estimate：额外请求增加至少一次网络往返和重复内容传输；官方文档没有明确调用计费、服务端时延或 token 数与实际 Chat 请求完全一致的保证；native JSON Schema 是独立字段，而估算接口示例只接受 messages，不能直接证明已计入该 Schema。字符数/字节数近似不能替代 token 计数，可能导致误报或漏报。所有缺失前提明确前，不进行自动拒绝、截断或额外云端调用。
- **改动：** `ProviderCapabilities.Resolve` 对直连 Kimi + OpenAICompatible 的精确 K3 设 `ContextWindowTokens=1_000_000`，精确 K2.6/K2.7 Code/高速版设 `256_000`；类快照/自定义后缀不继承上下文窗口。Kimi 能力摘要现在显示核实窗口值。未改提示、消息、采样参数、网络请求或默认模型。
- **验收：** 新增 exact-ID/未知变体能力测试及说明测试。修复前 8 项新增断言失败（四个已知型号无上下文值/摘要、K2.6 摘要无窗口），未知变体边界测试通过；修复后 Kimi context + thinking + K3 组合 **15/15 通过**。本步无 API 请求，无质量/成本/延迟收益测量。
- **自审与下游：** 这次使 4 个当前 Kimi 型号的用户可见上限由 Unknown 变为官方值，改善型号选择和后续路由/预检的数据基础，但没有真正阻止溢出请求。自动 token 预检仍需跨 Provider 计数器覆盖、Schema 计数规则、费用与时延政策、失败时降级语义和隐私选择；下一步先继续检查其他 Provider 档位/能力映射，后续另做 token 计数契约研究，不以字符长度假装精确 token 数。
- **全套回归结果：** exact Kimi context-window metadata 与说明合入后，以隔离可写目录再次运行 `dotnet test Huaxiazi.Tests/Huaxiazi.Tests.csproj --no-restore`，**1,949 通过、11 跳过、0 失败（共 1,960）**；主应用、DatasetBuilder、runner 和测试程序集均成功编译。工作区 `git diff --check` 通过。测试生成的 review fixtures 留在隔离 build 输出目录，未进入权威评测源数据。

### 阶段 1：Kimi 精确型号边界与 DeepSeek 推理档位说明纠偏（2026-10-07）

- **计划节点/输入：** 继续核对能力说明与实际请求映射。第一项审查发现 Kimi resolver 使用宽泛型号前缀匹配：官方列表只有明确的 K2.6/K2.7/K3 ID，但任意 `-custom` 后缀仍获同一 EvidenceId，界面会套用基座型号思考/采样描述。第二项审查比对 DeepSeek 请求体、能力摘要和官方 effort 映射，发现摘要把 High 写成 `max`，实际 Chat 请求及测试发送 `high`。
- **证据与决定：** Kimi 当前模型/参数文档列出 K3、K2.6、K2.7 Code 及高速版的精确 ID，未将任意后缀定义为快照，因此将 Kimi 能力解析收紧到 exact-ID allowlist。DeepSeek 当前 thinking 文档列出 low/high/max，并说明 Chat API 中应用 High 映射到 actual high，只有显式 Max 才映射 max；本应用没有 Max 档，故不改变现有请求行为，仅让摘要反映真实参数。[Kimi 模型参数](https://platform.kimi.com/docs/api/models-overview) · [DeepSeek Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
- **改动：** `ProviderCapabilities.Resolve` 的 Kimi K3/K2 分支由 `IsExactModelOrSnapshot` 改为精确 ID；自定义/未知后缀返回 Unknown，不继承 context window、thinking toggle 或能力摘要。`DescribeParameters` 的 DeepSeek High 从错误的“映射为 max”更正为“映射为 high”，没有改变 `AIService` 请求参数。
- **TDD/验收：** Kimi 自定义后缀的 4 个新增用例修复前 **4/4 失败**，收紧后 Kimi 能力、说明、请求测试 **26/26 通过**。DeepSeek Low/Medium/High/Custom 映射摘要测试修复前只在 High 档 **1/4 失败**；更正文案后与 DeepSeek 请求/错误回归合计 **10/10 通过**。未发真实 API 请求。
- **影响与边界：** 对应 Kimi 未知后缀从错误继承能力变为 Unknown；四种当前 Kimi 型号的 exact-ID 能力不变。DeepSeek 的说明从与请求不一致的 High→max 改为与现有请求一致的 High→high；没有给用户增加 max 档，也没有削减/增加推理强度。没有质量、费用、时延测量；阶段 0 盲评贡献仍为 0。
- **下一步触发：** 运行全套 Provider/应用回归，随后继续跨 Provider 的请求字段—说明差异审计；遇到可复现差异先追溯精确官方 ID/映射，再按 exact-ID 和请求契约测试修正。上下文 token 预检仍单独评估跨 Provider 计数、Schema 覆盖、额外费用与时延，不自动发起重复 prompt 请求。

### 阶段 1：Anthropic Schema 编译复杂度错误的精确降级识别（2026-10-07）

- **子任务与问题：** 核对 Anthropic Messages API 返回结构化输出拒绝时，现有 Provider 错误分类能否把可安全恢复的 Schema 问题转入既有“提示约束 + 本地 Schema 校验”路径。输入为 Anthropic 原生 `output_config.format` 请求契约、应用结构化工作流及错误分类代码；产出为一个最小精确错误签名适配和回归测试。
- **约束与方案判断：** Anthropic 官方错误对象保证 `error.type` 与 `error.message`，没有 OpenAI 风格的 `error.param`；HTTP 400 `invalid_request_error` 也覆盖许多无关请求错误。故不能按状态码或任意 message 文本触发 fallback。Anthropic 文档明确给出 Schema 编译复杂度限制及固定错误 `Schema is too complex for compilation.`；仅对该完整消息且 `error.type=invalid_request_error` 的组合分类为 `StructuredOutputUnsupported`。其它错误保持原失败，不额外发请求。[Structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs) · [Claude API errors](https://platform.claude.com/docs/en/api/errors)
- **代码改动：** `Services/AIService.cs` 将 classifier 命名改为 Provider 无关的 `IsStructuredOutputRejection`，为 Anthropic 增加精确匹配的复杂度错误签名；原 OpenAI Compatible `response_format`、Responses `text.format` 参数路径逻辑保持不变。只有匹配时才发出既有 `StructuredOutputUnsupported` 信号，由 `StructuredGenerationWorkflow` 使用提示词约束和本地 validator 执行 fallback。Provider 原始 message、request ID 不进入用户错误或遥测。
- **测试先行与验收：** 新增 Anthropic 文档错误和普通 Anthropic 400 两项行为测试。实现前前者按预期失败（实际为 `RequestRejected`），普通请求错误用例通过；实现后新增用例与 OpenAI 结构化错误/无关 400 用例 **4/4 通过**。随后 `AIServiceTests` + `StructuredGenerationWorkflowTests` **285/285 通过**；`git diff --check` 通过，只有现有换行符转换提示。
- **量化影响与边界：** 对已识别的 Anthropic Schema 编译复杂度拒绝，生成链从直接失败改为继续一次提示约束生成，并仍受本地 Schema 门禁约束；未增加正常请求调用、未改变提示/采样默认值。由于没有真实 Anthropic API 调用，本步没有服务延迟、成本或成稿质量数据；其它未提供该精确错误签名的 Schema 400 仍会作为请求失败，避免误吞认证、参数或消息问题。
- **偏差复核与接续：** 原先考虑从错误体中提取参数名的假设不适用于 Anthropic 官方错误形状，已改为固定签名 allowlist；当前工作流构造的 Schema 保持 provider-independent 的严格本地验证。下一步继续比较 Provider 能力声明、各自结构化请求形态与实际工作流 Schema 的交集；只有官方文档能确认且可本地判别的能力差异才进入自动映射。该子任务不改变阶段 0 盲评贡献 **0** 的现状。

### 阶段 1：结构化 Schema 契约与 Provider 请求形态交叉核对（2026-10-07）

- **子任务与验收：** 以应用的 `StructuredGenerationWorkflow.BuildSchema` 为输入，核对 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Gemini GenerateContent 的实际序列化形态及已登记 `JsonSchema` 能力。验收条件是原生能力标记仅用于已核验型号，协议字段匹配对应 API，应用生成的 Schema 只使用该 Provider 支持的子集；否则需锁定请求级回归并修正。
- **发现与取舍：** 当前契约根节点为 object；字段使用 string/number/array、nullable type union、字符串 enum、array items；所有字段列在 required，object 设置 `additionalProperties=false`。数值范围与最终 answer 长度留在 provider-independent 本地校验，没有放入原生 Schema。Anthropic 当前支持上述所用构造，且不支持的数值范围确实不应发送。Gemini GenerateContent 当前 API 已提供 `generationConfig.responseFormat.text`（含 `mimeType=application/json` 与 `schema`）；它不同于 Gemini Interactions API 的顶层 `response_format`，应用实际使用的是前者。OpenAI Chat 的 `response_format.json_schema` 与 Responses 的 `text.format` 也分别匹配对应协议。[OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs) · [Gemini structured output](https://ai.google.dev/gemini-api/docs/structured-output) · [Gemini GenerateContent API](https://ai.google.dev/api/generate-content)
- **结论与改动范围：** 交叉核对没有发现当前应用生成 Schema 与以上 Provider 协议之间的可证实不兼容；不增加有损的通用 Schema 削减器，也不改动原生能力表。已有请求契约测试覆盖 OpenAI Chat/Responses、Anthropic `output_config.format`、Gemini GenerateContent `responseFormat.text` 及未知模型的提示降级路径。能力说明仍是 exact-model allowlist；用户通过任意自定义 Schema 直接调用底层客户端时，完整 Provider 子集兼容性不作保证，本地 validator 仍是最终业务门禁。
- **验证与接续：** 复跑 `AIServiceTests`、`StructuredGenerationWorkflowTests`、`ProviderPlatformTests` **487/487 通过**。后续如果 `StructuredOutputContract` 增加字段类型或 Schema 关键字，触发条件是新增属性与 Provider 子集交叉检查及对应序列化测试；继续阶段 1 的下一步是逐项审计 Provider 原生输出终态、截断/拒绝解析及用量/请求标识归一化。本步未发真实 API 请求，也没有模型质量或成本收益测量。

### 阶段 1：Anthropic 未知完成状态不交付正文（2026-10-07）

- **子任务与问题：** 审计 Provider 完成状态与生成正文的一致性。Anthropic Messages 非流式响应包含 `stop_reason`；当前 parser 仅特判已知 `max_tokens`、refusal、tool-use 等状态，未知枚举仍作为成功文本返回。风险是 API 新增非最终状态时，应用可能将片段当成完整润色结果。
- **证据与决策：** Anthropic Create Message API 明确说明非流式 `stop_reason` 总会存在，并列出 `end_turn`、`stop_sequence`、`max_tokens`、`tool_use`、`pause_turn`、`refusal`、`model_context_window_exceeded` 等状态；文档也说明 `model_context_window_exceeded` 应视作截断。策略改为只让已知完成态 `end_turn` / `stop_sequence` 通过；已有已知失败类型保持原分类，其它非空未知值返回通用 `InvalidResponse`，不回传正文。该选择把兼容性风险限定为新枚举上线时需要更新客户端，避免误交付潜在不完整内容。[Create a Message API](https://platform.claude.com/docs/en/api/messages/create) · [Stop reasons and fallback](https://platform.claude.com/docs/en/build-with-claude/handling-stop-reasons)
- **改动与参数：** `Services/AIService.cs` 的 Anthropic 响应解析增加成功终态 allowlist；无采样参数、推理预算、Schema、重试次数或 Provider 路由变化。`Huaxiazi.Tests/AIServiceTests.cs` 将未知 future reason 的预期从返回文本改为拒绝交付，并断言错误消息不包含部分正文。
- **TDD/验收：** 新断言在实现前失败，实际旧行为为“无异常并返回文本”；修复后 Anthropic 已知成功/截断/拒绝/工具和未知状态用例全部通过。`AIServiceTests`、`StructuredGenerationWorkflowTests`、`ProviderPlatformTests`、`AIServiceTelemetryTests`、`ProviderUsageParserTests` 合计 **507/507 通过**。未发真实 API 请求。
- **量化影响与限制：** 已知两种完整终态仍保持交付；任意未知非空 `stop_reason` 从“可能直接交付”改为“拒绝并显示可理解错误”，成功请求调用次数与生成成本不变。无盲评数据，未测模型质量收益；如果 Anthropic 将来加入新的成功 stop reason，需要核验文档后将其加入 allowlist 并补测试。当前兼容处理仍允许缺失 `stop_reason` 的响应继续解析，以兼容省略该字段的 Anthropic-compatible 网关；直连 API 按官方契约应提供此字段。
- **下一步衔接：** 继续阶段 1 的终态与 usage 归一化审查，优先检查错误/截断响应遥测是否同时保留合法 request ID、finish reason 和 token usage，且不记录 Provider 错误正文；该路径会支撑后续线上质量与成本监控。

### 阶段 1：无效/超大响应的内容无关诊断元数据补齐（2026-10-07）

- **子任务与问题：** 检查成功 HTTP 状态下的截断、拒绝和格式错误是否有内容无关的诊断记录。输入是各 Provider 的有界响应读取器、usage parser、正文解析 catch 和现有 telemetry 契约；产出为错误响应分支元数据保持修复及测试。
- **第一性原理与取舍：** 监控要能把“服务完成但响应无法交付”与网络/Provider 错误区分开，同时不能为了拿 usage 而读取超出预算的正文。对正常有界 JSON，metadata parser 先提取 allowlist request id、token usage 与 finish reason；正文解析失败应记 `invalid_response`。对超过 4 MiB 上限的成功响应，继续停止读取并把 usage 留为 unknown，但可保留已收到的合法响应头 request ID 和 HTTP 状态。HTTP 非 2xx 路径也在解析前读取受格式约束的响应头 ID，仅记录状态类别；错误正文不进入遥测。
- **缺陷与修复：** `AIService.GenerateCoreAsync` 原先只在 `GenerationFailureException` 的完成状态分支写遥测，`InvalidOperationException` / `JsonException` 这类格式解析失败会漏掉整条事件。另一个 catch 对 `ResponseTooLargeException` 记录了状态与 `invalid_response`，但 request ID 变量作用域导致记录为 null。现在解析异常分支写入 `invalid_response`、合法 request ID、状态、已解析 usage 和 finish reason，然后原样重抛，不改变既有错误分类与 DeepSeek 空响应重试。每轮请求保留从响应头读取的 ID，超大响应 catch 可继续使用该 ID。
- **测试先行与验收：** 新增 malformed HTTP 200 用例，原行为为遥测集合为空；实现后验证记录携带 body 中合法 ID、17 输入 tokens、4 输出 tokens、`stop` finish reason 与 `invalid_response`，序列化遥测不包含 prompt/答案。再新增超过 **4 MiB** 响应用例，原行为为 request ID 为 null；修复后保留 `x-request-id`、HTTP 200 和 `invalid_response`，usage 保持 unknown。相关 5 组测试 (`AIServiceTests`、`StructuredGenerationWorkflowTests`、`ProviderPlatformTests`、`AIServiceTelemetryTests`、`ProviderUsageParserTests`) **509/509 通过**；`git diff --check` 通过。
- **量化影响与边界：** 对格式错误的成功响应，诊断事件覆盖由 **0/1 提升到 1/1**；对超大响应 request ID 记录覆盖从 **0/1 提升到 1/1**。不增加 Provider 请求、不扩大 4 MiB 读取预算、不保存 response body；超大响应 token 用量仍明确未知，避免以部分 JSON 推测账单。无用户质量、服务费用或延迟收益声称。
- **偏差复核与接续：** 本步符合计划的 content-free observability 约束；旧的超大响应 request ID 空值是实现缺口，已由回归锁定。下一步检查错误 HTTP 响应的 header/body request ID 优先级与 finish reason 归一化边界，并核验 telemetry 写入发生在 retry 前按每次 HTTP attempt 单独记账；其后再评估是否具备稳定的上线观测契约。

### 阶段 1：Provider 请求追踪 ID 与重试遥测优先级（2026-10-07）

- **计划节点/问题：** 延续错误响应元数据审计，核验 Anthropic AWS 错误请求 ID，以及 transient HTTP retry 是否逐次记录。新增的两次请求用例先发现成功响应同时含 HTTP `x-request-id` 与 JSON completion `id` 时，遥测优先使用 JSON ID，丢失用于服务端排障的请求追踪 ID。
- **改动：** `ReadRequestId` 接受 Anthropic 官方 AWS 响应使用的 `x-amzn-requestid`。成功响应元数据选择优先级改为响应头 request ID 优先、响应体 ID 兜底；无响应头时保持原兼容行为。每次 HTTP attempt 仍各自产生一条内容无关事件；首次 transient 错误、第二次成功分别记录各自状态、ID 和 outcome。
- **测试与验收：** Anthropic AWS 400 错误的 header ID、响应超 4 MiB 时保留 header ID、以及 503→200 两次尝试各自关联 header ID 的回归均已覆盖。新 retry 用例在改优先级前失败（期望 `req-attempt-2`，实际为 body completion ID `chatcmpl-final`），修复后相关 Provider/结构化工作流/用量遥测集合 **511/511 通过**。错误响应正文仍不写入遥测；`git diff --check` 通过。
- **影响边界与下一步：** 该修复提高 request ID 对应真实 HTTP 请求的准确性，改善重试排障；不改变重试次数、等待策略、用户生成文本或请求内容，不代表质量、费用或延迟提升。接下来继续核验其他 Provider 的官方请求 ID header 与 finish reason 映射，再检查路由配置/仅本地边界。

### 阶段 1：Responses 非终态拒绝与跨 Provider 请求 ID/终态遥测校准（2026-10-07）

- **计划节点与输入：** 根据上一项的下一步，逐一核验请求追踪 ID 和 finish reason。官方文档确认 OpenAI 的唯一请求标识是 `x-request-id`，Anthropic 直连用 `request-id`，Anthropic Platform on AWS 同时返回 `x-amzn-requestid`（AWS 主 ID）和 `request-id`（Anthropic 次 ID）；Gemini GenerateContent 有响应体 `responseId`。OpenAI Responses 当前 `incomplete_details.reason` 允许 `max_output_tokens`、`max_messages`、`content_filter`、`steered`，status 可为 `queued/in_progress/completed/...`。来源：[OpenAI request debugging](https://developers.openai.com/api/reference/overview)、[Anthropic API errors](https://platform.claude.com/docs/en/api/errors)、[Anthropic on AWS](https://platform.claude.com/docs/en/build-with-claude/claude-platform-on-aws)、[Gemini GenerateContent response](https://ai.google.dev/api/generate-content)、[Responses retrieve schema](https://developers.openai.com/api/reference/resources/responses/methods/retrieve)。
- **问题与修复：** 双 ID AWS 响应会优先选 `request-id`，不符合 AWS 排障主 ID 用途；`queued/in_progress` 若意外包含文本，之前可能直接当成成稿交付；Responses reason allowlist 漏掉 `max_messages/steered`，却错误接纳当前官方 Schema 未列出的 `tool_execution_timeout/off_topic`。修复为 AWS ID 优先；Responses `queued/in_progress` 统一作为 `Incomplete` 且不交付文本；按当前官方 reason 枚举收敛 reason allowlist。未改其余模型状态/正文清洗与 JSON Schema 校验逻辑。
- **测试先行与验收：** AWS 双 ID 的生成遥测和连接诊断两项新增断言修复前均失败（选错为 Anthropic 次 ID）；queued/in_progress 带文本的两项用例修复前均未抛错；reason 映射四项新增用例修复前全失败（新增合法值被丢弃、非法值被接纳）。完成后，AIService、结构化工作流、Provider 平台与遥测、usage parser 定向集合 **518/518 通过，0 跳过**，`git diff --check` 通过。无真实 API 调用。
- **量化影响与自审：** 回归前这 2 种非终态有 **2/2** 路径可能交付非最终文本，现为 **0/2**；AWS 主/次 ID 双头场景 **2/2** 测试从错误选择改为选中主 ID；Responses 合法/非法 reason 的 **4/4** 边界已与官方枚举一致。这些是契约正确性和排障覆盖，不是模型质量、token 成本、服务延迟或用户可用率提升证据。request ID 只收内容无关的规范化 ID，响应正文/用户文本仍不进入遥测。阶段 0 正式盲评贡献仍为 **0**。
- **衔接：** 本步完成了已核实官方请求 ID 与 Responses 终态的边界校正。下一项转到已登记的 Provider 能力/路由策略：检查路由输出是否真正受用户任务绑定、LocalOnly 是否在所有错误路径阻断云端调用、fallback 实际模型是否能在产品状态中准确显示；任何实现修改前先沿请求调用链梳理本地/云端边界并用断网测试验证。

### 阶段 2：LocalOnly 失败路径的云端隔离端到端回归（2026-10-07）

- **子任务/输入：** 验收仅本地模式最关键的隐私不变量：即使用户同时保存了云端 fallback，所选本地模型超时后也不得创建云端 client。该不变量需在 MainViewModel 的实际任务调用链验证，单测 ProviderRouter 返回 null fallback 不足以证明产品链路。
- **改动：** 新增 `MainViewModel_LocalOnlyLocalFailureNeverCreatesConfiguredCloudFallback` 集成用例，设置 LocalOnly、本地任务 profile、显式云端 fallback 与无痕；本地 client 抛出 timeout 后断言 client factory 只收到本地 profile，VM 保持错误状态且展示的模型标签没有云端 profile。实现审查确认 `ProviderRouter.Select` 在 LocalOnly 直接返回 null fallback，MainViewModel 仅对非空 fallback 创建包装器；因此本步无需改运行时代码。
- **验证：** ProviderRouter、fallback client、MainViewModel 生成集成和诊断集合 **55/55 通过，0 跳过**。新端到端断言若未来错误地把配置 fallback 传入 LocalOnly 包装器，将因 cloud factory 被调用而失败。未发网络/API 请求。
- **自审与发现：** LocalOnly 错误路径的“无云端 client 创建”已有直接证据。继续追踪 Stage 2 发现计划中的另一项缺口：现有 `ModelSelectionPolicy` 生成 Fast/Balanced/Reasoning 建议，`PromptBuilderService` 把建议写进提示词；全仓只有规划器调用该策略，`ProviderRouter` 不消费 tier，也没有 Automatic 路由模式或用户显式 tier→profile 绑定。因此复杂度建议尚未变成模型选择，提示词建议不能替代实际路由。
- **下一步触发：** 开始 Stage 2 的可执行 tier 路由设计与实现前置核对：确定如何把专业化计划的 RecommendedModelTier 传到 provider selection，如何为润色/提示词任务显式绑定 tier profile，及无映射/模型不可用时的确定性行为。默认 Manual 必须完全保持旧行为；只有用户启用 Automatic 且显式配置 tier profiles 后才按复杂度选模型。目标验收应覆盖 tier→实际 profile、未知/空 tier、隐私偏好优先级、显式 fallback 和 UI 实际模型标签，不把 tier 继续仅作为模型 prompt 文本。

### 阶段 2：自动档位路由原因进入内容无关诊断（2026-10-07）

- **计划节点：** 自动档位路由的可观测性收尾。已有生成调用按 `automatic-fast`、`automatic-balanced`、`automatic-reasoning` 选择 profile，但 `GenerationDiagnosticsService` 只接受旧路由原因白名单，三种新原因均被规范化为 `null`，使得本机诊断无法复核实际采用的档位。
- **输入、约束与改动：** 输入仅为 `ProviderRoutingMode.Automatic` 和三个固定路由原因枚举。`Services/GenerationDiagnosticsService.cs` 将三个枚举纳入白名单；不记录请求正文、偏好、profile 名称或端点。`Huaxiazi.Tests/GenerationDiagnosticsServiceTests.cs` 增加三档 theory，验证档位原因与 Automatic 模式被保留，同时序列化诊断不含配置名称/请求文本。
- **TDD 与验证：** 实现前新增的三条用例均复现 `RouteReason=null`；补充白名单后诊断服务测试 **12/12 通过**。追加的 Automatic 主/备用请求关联用例验证两次 Provider 尝试都保留 Reasoning 原因，且仅通过 `routeAttempt` 区分 primary/fallback；路由策略、档位计算、设置保存/重载、生成集成和诊断合并回归 **191/191 通过，0 跳过**。`git diff --check` 对本次改动文件通过。
- **量化影响与边界：** 自动档位原因可观测覆盖由 **0/3 增至 3/3**；没有新增网络请求、诊断字段正文、路由行为或生成调用，不声称模型质量/成本/延迟收益。该验证是受控测试，尚无真实运行遥测或盲评数据。
- **偏差复核与下一步：** 源码已包含 Automatic 模式、六个任务×档位绑定及手动默认；此前进度条目未记录该实现，现补齐诊断闭环和状态说明。下一步检查实际生成 scope 的路由元数据传递，并核验 fallback 后显示与诊断中的实际 profile 是否一致；之后检查 Provider 能力是否参与候选可用性，避免只按文本复杂度选择不支持所需输出契约的模型。正式盲评和全量 Windows 发布验收仍待完成。

### 阶段 2：自动档位与结构化输出能力降级组合验证（2026-10-07）

- **计划节点/待核实问题：** 检查自动路由绑定到某 Provider/model 后，如果该服务拒绝原生 JSON Schema，是否会错误地触发显式备用模型，或失去产品本地输出契约。第一性约束是保持用户指定的档位模型、只在真实 Provider 故障时使用备用模型，并让所有 Provider 输出经过相同本地 Schema/质量校验。
- **源码证据与决策：** `AIService` 的精确能力解析决定 native JSON Schema、JSON Object 或提示词 Schema；对未知/非 Schema 能力不猜测原生支持。`StructuredGenerationWorkflow` 捕获专门的 `StructuredOutputUnsupported` 后，在同一个 client 上调用普通文本生成并附带 Schema 指令，随后执行本地 validator。`ProviderFallbackGenerationClient` 明确将 `StructuredOutputUnsupported` 排除在故障切换之外。该职责分层意味着路由层无需因缺少原生 Schema 而改选用户绑定的模型，避免把“功能能力差异”误判成“服务不可用”。
- **新增验证：** `Huaxiazi.Tests/StructuredGenerationWorkflowTests.cs` 增加组合用例，把拒绝 Schema 的主 client 包在显式 fallback 路由器内执行：结果通过本地结构校验，native Schema 请求 1 次、提示词降级请求 1 次，备用 client 创建次数为 0。此前能力降级与 fallback 排除分别有单测，本例补齐二者连接处。
- **量化影响与边界：** Provider 能力差异在该拒绝场景下维持单模型路径，备用创建由期望的 **0/1**；本地契约成功通过由 **1/1** 组合案例验证。路由/结构化工作流/Provider 平台和档位相关联合测试 **403/403 通过，0 跳过**。无真实 API、无额外网络请求、无质量或成本收益测量；Schema 指令遵循能力仍需真实盲评。
- **自审与接续：** 本步没有改变生产请求与路由行为，验证结果支持现有能力分层，故没有添加多余路由过滤。下一项应聚焦已有 capability 数据尚未落实的实际约束（尤其 context window）：先确认是否能在不猜测 token 数的前提下安全阻止超限请求；如果缺少匹配 Provider 的 tokenizer/准确输入 token 计数，应先保留 unknown 并补充准确的 usage/错误观测，而不是用字符数伪装 token 预算。

### 阶段 1/2：Anthropic 上下文窗口耗尽错误的准确分类（2026-10-07）

- **计划节点/具体问题：** 延续上下文能力与服务诊断核对。Anthropic `stop_reason=model_context_window_exceeded` 已被识别为非最终输出，但旧代码把它与 `max_tokens` 一并映射为普通 `Incomplete`，并提示“缩短输入或提高输出上限”；上下文窗口耗尽时，提高输出上限不是正确修复。
- **方案取舍：** Anthropic 官方文档明确列出输入超过 context window 的终止状态；优先选择基于该稳定枚举的精确分类，而不扫描/保存可能含输入回显的错误文本。未增加预估 token 请求：官方 OpenAI Responses、Anthropic 和 Gemini 均有模型侧 token-count API，llama.cpp server 也有 `/apply-template` 与 `/tokenize`，但它们增加单独网络/本地往返及延迟；云端计数还会再次发送请求正文，且 OpenAI Responses 计数契约不能直接视为 OpenAI Chat Completions 精确计数。因此这一步先准确报告已发生的越界，不伪造字符到 token 的通用换算，也不默认增加请求。[Anthropic context windows](https://platform.claude.com/docs/en/build-with-claude/context-windows) · [Anthropic token counting](https://platform.claude.com/docs/en/build-with-claude/token-counting) · [OpenAI token counting](https://developers.openai.com/api/docs/guides/token-counting) · [Gemini token counting](https://ai.google.dev/gemini-api/docs/tokens) · [llama.cpp server](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- **改动：** `Models/GenerationFailure.cs` 新增 `ContextLimitExceeded`；`Services/AIService.cs` 将 Anthropic `model_context_window_exceeded` 独立映射到该类型，提示缩短输入或选更大上下文模型；`max_tokens` 仍维持原 `Incomplete` 及“缩短输入/提高输出上限”提示。该类型触发的成功 HTTP 响应仍不交付部分正文；配置了明确 fallback 时沿用既有失败回退策略。`Services/GenerationDiagnosticsService.cs` 允许内容无关 outcome `context_limit_exceeded`。
- **TDD 与验证：** 新回归先因旧消息不含“上下文窗口”而失败。修复后专用 Anthropic 错误/终态与诊断用例通过；包含 AIService、遥测、usage parser、Provider 平台、路由、设置、MainViewModel 集成和结构化工作流的联合筛选 **717/717 通过，0 跳过**；改动文件 `git diff --check` 通过。无真实 API 请求。
- **量化影响与限制：** Anthropic 两类容易混淆的结束原因现有独立分类：`model_context_window_exceeded` → `context_limit_exceeded`，`max_tokens` → `incomplete`；目标修复覆盖 **2/2**。没有新增推理请求，也没有测得模型质量、成本或延迟提升。其它 Provider 的上下文拒绝如果只返回通用 HTTP 400 且没有稳定机器可读代码，仍保持 `provider_error/request_rejected`，不会仅凭英文错误文本猜测。
- **下一步触发：** 评估 token-count preflight 是否值得独立成为可选能力：先拆出各 Provider 实际生成请求体与计数 API 契约的对应关系，再测额外调用 p50/p95、计数/生成输入一致率和隐私披露；未形成明确收益前不把云端重复计数设为默认。当前 Automatic 路由仍按用户的 task×tier profile 显式绑定；正式盲评和真实运行质量证据仍待完成。

### 阶段 2/4：托管 llama.cpp 上下文 token 预检（2026-10-07）

- **问题与约束：** Managed Local 请求在上下文不足时可能被运行时拒绝或截断；字符数无法准确代表模型 token，云端计数还会重复发送正文并增加费用/隐私暴露。因此只对项目固定版本且已实测计数契约的托管本地运行时启用预检，外部 Ollama/LM Studio、旧版运行时和云端 Provider 保持原流程。
- **实测依据：** llama.cpp b11424 CPU server 使用 Qwen3-4B Q4_K_M、4096 上下文和合成请求验证 `/v1/chat/completions/input_tokens`。计数端点返回 **1,341**，对应生成完成的 `usage.prompt_tokens` 亦为 **1,341**。30 次计数延迟 p50 **8.56 ms**、p95 **26.73 ms**。这只代表该模型/CPU/请求，不能外推到其他硬件。10 次短生成 p50 1,174 ms、p95 17,145 ms；p95 被冷启动首轮 prompt evaluation（约 16 秒）主导，不能作为稳定生成延迟估计。数据为合成内容，无云端调用。
- **实现：** `LocalRuntimePackageDescriptor` 为经验证的 b11424 CPU/Vulkan 包声明计数能力；托管 runtime manager 仅对版本目录匹配的已知包开启。`LocalTextGenerationClient` 将同一序列化 Chat Completions body 发给计数接口和生成接口；计数响应限读 8 KiB，只接受非负 `input_tokens`。当 `input_tokens + 请求 max_tokens > LocalRuntimeOptions.ContextSize` 时，在生成前抛出 `ContextLimitExceeded`，提示实际计数、输出预留和上下文长度，并写入无正文的 `context_limit_exceeded` 诊断。计数接口不可用、返回非成功状态或格式无法解析时 fail-open，继续原本地生成；调用方取消仍正常传播。预检从未向云端发送请求。
- **验证：** 新增超限拦截、请求体完全一致且预算可行时继续生成、计数端点 404 时 fail-open、版本包能力边界用例。`LocalRuntimeTests` **28/28** 通过；`LocalRuntimePackageServiceTests`、`GenerationDiagnosticsServiceTests`、`AIServiceTelemetryTests` 联合 **38/38** 通过；后续合并 `AIServiceTests` 的相关定向筛选共 **348/348** 通过，相关文件 `git diff --check` 通过。随后以隔离运行时根和现有测试模型，使用生产安装器安装真实固定 SHA CPU/Vulkan 包，通过 `LocalRuntimeManager` 启动，并用 HTTP delegating handler 记录实际产品请求路径和请求体：两个 flavor 都报告 `SupportsChatCompletionTokenCount=true`，各有 **1 次计数 + 1 次生成**，两份请求体逐字节一致，健康检查 **200**，产品润色均返回 `Final`，无修复/质量问题，结束后均成功卸载；云端请求 **0**。CPU 输入 usage 为 **750 tokens**、工作流约 **25.09 s**；Vulkan 同为 **750 tokens**、约 **4.98 s**。该安装烟测是每种后端单次运行，且使用已有 Windows 驱动/CRT，只证明能力已贯通，不是性能对比或质量评测。
- **量化收益与限制：** 对被计数并判断超限的调用，推理请求数从 **1 降为 0**，避免无效本地推理；额外成本为每次受支持托管本地调用增加一次 loopback 计数请求，在前述本机 30 次合成请求基准 p50 约 **8.56 ms**、p95 **26.73 ms**。真实集成验证的 750 个输入 token 均低于 `4096 - 512` 预算，因此继续生成。输出预算按用户 max_tokens 全额预留，策略偏保守，可能拒绝模型本可提前停止的请求；用户可降低输出上限或选择更大上下文模型。预检失败开放不会阻断生成，因此运行时计数服务异常时仍依赖原服务返回的上下文错误。
- **偏差复核与后续：** 该功能仅覆盖精确确认的 b11424 托管包；没有为云端 Provider 或非托管本地服务增加正文重复计数，也没有自动缩短输入/静默改写 max_tokens。真实 CPU/Vulkan 安装路径和健康检查链路已经核验，超限拦截由确定性的集成测试覆盖。后续转到阶段 2 的上下文能力感知路由与阶段 3 偏好质量对照；正式盲评仍未建立，模型质量、用户可用率和总体延迟收益尚无评测结论。

### 阶段 2：上下文窗口元数据的路由适用性复核（2026-10-07）

- **要解决的问题：** 判断 `ProviderCapabilities.ContextWindowTokens` 能否安全参与自动路由/输出预算，而不是因为字段存在就将其当成已实现的限制。
- **源码核查：** 全仓调用点显示该元数据目前仅在 Provider 配置说明文案中展示；请求构造按已核验的 `MaximumOutputTokens` 限制输出参数，路由按用户显式绑定的任务/档位 profile 选择，并没有使用上下文窗口自动替换模型。当前能力记录也没有包含每次实际序列化 system/user/Schema/工具内容的 tokenizer 计数。
- **第一性原理判断：** 上下文窗口限制的是实际输入 token 与输出预留的合计。只知道窗口上限无法判断某条请求是否装得下；字符估算也不能保证各 tokenizer 或服务端模板准确。云端 token-count API 又按协议分化，调用会额外发送完整正文并增加一次往返。因此暂不添加“按字符选择大窗口模型”或默认云端预检。对于托管本地 b11424，前一子任务已验证准确计数、相同请求体和本地拦截；对于云端仍使用稳定机器可读超限错误分类和用户明确配置的 fallback。
- **隐私路径回归：** 将 `MainViewModel_LocalOnlyLocalFailureNeverCreatesConfiguredCloudFallback` 扩为 `Timeout` 与 `ContextLimitExceeded` 两种失败输入，保留原超时覆盖并新增“仅本地上下文不足且保存了云端备用配置”场景。Provider 路由/fallback、该 MainViewModel 场景和本地运行时相关筛选 **57/57 通过**；LocalOnly 仍只创建本地 client，错误后不展示或触发云端模型。
- **结论与后续：** 静态上下文元数据本身不构成每请求预算，当前没有证据支持改变路由或 clamp 输出，因此保持现有行为是正确的无变化决定。后续如实现云端 token 预检，需按精确 Provider/API body 匹配 tokenizer，并明确用户隐私/额外往返策略；质量提升仍需阶段 0 正式盲评及阶段 3 偏好对照数据支撑。

### 阶段 2：本地 token 预检结果纳入内容无关诊断（2026-10-07）

- **问题与输入：** 本地 token 预检在计数端点失败时会 fail-open，但旧遥测只看到后续生成成功，无法区分准确计数、不可用和超限拦截，难以发现运行时回归。输入仅为本地预检控制流状态，不采集请求正文或 token 字符串。
- **实现：** `ProviderRequestTelemetry` 和本机请求诊断新增可选 `contextPreflightOutcome`，只接受 `counted`、`unavailable`、`exceeded`、`cancelled` 四个固定值；`GenerationDiagnosticsService` 仅对 ManagedLocal profile 保留白名单值，null 属性从 JSON 中省略以保持旧记录格式兼容。生成汇总按任务/Provider/model/backend统计状态计数；云端、旧记录及没有实际触发预检的请求不计入。计数失败时的最终请求仍能带 `unavailable`，调用者取消时记 `cancelled`，计数通过后超限则记 `exceeded`。
- **验证：** 新增单测覆盖四种状态、无效值拒绝、云端字段丢弃、汇总统计以及 null 字段省略；本地运行时/诊断/汇总/遥测相关筛选 **63/63 通过**。全套 `Huaxiazi.Tests` 在默认系统临时目录第一次执行时因该目录访问被拒而出现 13 个环境权限失败、测试宿主异常；将 `TEMP`/`TMP` 指向工作区可写 `.tmp-test-env` 后重跑，最终 **2,000 通过、11 跳过、0 失败**。这组环境失败不是代码断言失败，已用可写临时目录完成全量回归。
- **真实安装链路复核：** 使用生产安装器在隔离根安装固定 SHA 的 b11424 CPU/Vulkan 包，再由产品 `LocalRuntimeManager` 与 `LocalTextGenerationClient` 执行润色。两个 backend 均各发生一次计数和一次生成，请求体逐字节一致，遥测均为 `contextPreflightOutcome=counted`，输入 usage 各 750 tokens，workflow 均 `Final`，健康 HTTP 200，卸载成功，云端请求 0。更新的机器报告为 `out/test-artifacts/runtime-package-preflight-smoke/runtime-package-token-preflight-20261007.json`；CPU 单次 workflow 25.34 秒、Vulkan 5.04 秒，只作为集成烟测，不作性能/质量比较。
- **量化影响与边界：** 对已启用预检的请求，预检结果可观测覆盖 **4/4**（counted、unavailable、exceeded、cancelled）；此前覆盖为 **0/4**。本地汇总能直接计算计数不可用比例与超限数，仍不含正文，也不上传设备。完整真实安装仅各运行一个合成样本，不能据此推断生成质量、用户可用率或后端速度收益。下一步转入阶段 0/3 的内部偏好对照样本复核，优先使用可用 development split；不以内部合成评测替代正式外部盲评。

### 阶段 0/3：固定偏好档内部 A/B 对照与人工审阅副本（2026-10-07）

- **本步要回答的问题：** 在同一产品代码、同一 Qwen3-4B 本机模型和采样设置下，加入一条明确的风格偏好是否改变工作流结果和候选稿。开发集为 `polish-regression-v1` 的 13 个 development 族，全部没有既有 `preference_instructions`；数据为合成内部样本，不是正式盲评材料。
- **系统改动：** `tools/BlindEvaluationRunner/InternalDiagnosticPreferenceProfiles.cs` 新增固定档 `natural-concise-preserve-voice`，指令偏向自然简洁、保留原文语气和全部事实，并说明本轮要求优先。runner 仅接受命名档，不接受任意 CLI 提示文本；记录档名和指令 SHA-256，不记录偏好正文。`BlindWorkflowRequestFactory` 将该档同时送入产品润色/提示词优化请求和 professionalization planner；如样本已有偏好则拒绝对照，防止变量混杂。prompt bundle hash 按档名及哈希区分条件。
- **诊断记录改进：** 人工审阅澄清表现需要看到实际澄清问题；预测 JSONL 以前只写 `needs_clarification` 状态。现在 `InternalRegressionDiagnosticService` 同时写 `clarification_questions`，因此重跑后 A/B 审阅包能比较澄清问题本身。状态分类另将 `failed` 与 `invalid/rejected` 分开，避免把模型截断误记为拒绝；A/B 两次运行发生在该计数修正之前，原始 `predictions.jsonl` 保留逐样本真实状态，派生 cohort summary 已按其重算。
- **固定输入与参数：** 数据 SHA-256 `27ddceb1f9706f0851dae720d79ae5361638d7bcef8bd0788f4bfd3260da3195`；本机 Ollama `qwen3:4b` digest `359d7dd4bcdab3d86b87d73ac27966f4dbb9f5efdfcc75d34a8764a09474fae7`；temperature `0.6`、top_p `0.95`、max_tokens `4096`、seed `20261007`、Qwen3 thinking 为 provider default。两组产品程序集哈希、runner 程序集哈希、参数 bundle 哈希一致；仅偏好 prompt bundle 不同。
- **实跑结果：** 基线 13 样本：成稿 **7**、澄清 **5**、失败 **1**、拒绝 **0**、取消 **0**；偏好组：成稿 **9**、澄清 **4**、失败/拒绝/取消均 **0**。每组 10 次 Provider 请求。基线 p50/p95 工作流延迟 **46.18/73.83 秒**、输入/输出 **9,165/28,603 tokens**；偏好组 **41.70/72.25 秒**、输入/输出 **10,185/26,511 tokens**。该小样本只有描述性结果：偏好组工作流终态多 1 个成稿，基线第一个样本一次生成达到 `max_tokens=4096` 并以 `finish_reason=length` 截断；同 seed 的其他完整运行中该样本曾成功，进一步说明 seed 不是确定性保证。因此不能把终态、延迟或 token 差值归因成质量收益。
- **盲化人工审阅副本：** [审阅包目录](../out/test-artifacts/internal-preference-contrast-20261007-ab2/review-packet/) 提供 12 对匿名 A/B 候选、原输入及场景，不提供 reference、expected decision 或条件名；失败样本 `hxz-polish-reg-000007` 因一侧无完整候选而排除，原始失败记录仍保留。A/B 条件映射单独存放在审阅目录外的 `reviewer-key.sealed.json`；[机器汇总](../out/test-artifacts/internal-preference-contrast-20261007-ab2/cohort-summary.json) 标明评分尚未完成。审阅结果只能作内部方向参考，`phase_0_gate_contribution=0`，不导入训练数据。
- **验收：** 先新增并观察到缺少 profile/factory/澄清记录/状态分类 API 时测试编译失败，再完成实现。定向回归 **10/10 通过**；全量 `Huaxiazi.Tests` 最终 **2,010 通过、11 跳过、0 失败（共 2,021）**。审阅副本校验 12 对均有双方候选、没有参考/期望标签或条件名称，条件密钥不在审阅目录内。测试进程需将 TEMP/TMP 指向工作区可写目录。
- **结论与下一步：** 已把偏好档真实送入现有生成层并完成内部对照；工程链路与记录闭环可用，但偏好是否更符合人的风格、是否提高事实保真或直接可用性仍未知。下一步由人工填写 [pairwise-review.csv](../out/test-artifacts/internal-preference-contrast-20261007-ab2/review-packet/pairwise-review.csv)，再按盲化映射解封统计成对偏好和分项评分；不以这 12 对内部样本替代外部盲评，也不据此自动更改产品默认偏好。

### 阶段 2：Ollama Qwen3-4B Low 档推理开关精确映射（2026-10-07）

- **问题与约束：** 项目已为本机 `qwen3:4b` 设置 Medium 预算，但 Ollama OpenAI-compatible 请求未映射思考开关，用户选择 Low 时仍使用模型默认思考。只给通用 Ollama 或所有 Qwen3 型号添加字段会超出证据；减少 reasoning tokens 也不等于改善最终答案。
- **证据与取舍：** Ollama 当前官方 OpenAI-compatible 文档列出 `reasoning_effort`，并说明 `"none"` 表示不请求 reasoning；本机 Ollama **0.33.3** 的 `qwen3:4b`（digest `359d7dd4…fae7`）`/api/show` 未给 thinking metadata，因此不能从静态目录推断能力。对 `/v1/chat/completions` 使用相同简单提示、seed、temperature `0.6`、top_p `0.95` 和 max_tokens `512`，默认请求返回 `reasoning` 字段、214 completion tokens；显式 `reasoning_effort=none` 返回不含 reasoning 字段的响应、148 completion tokens（该次简单探针减少 **30.8%** completion tokens）。但最终 `content` 长度由 2 字符变为 367 字符，且探针不是产品润色质量评测，说明开关可能改变格式/回答行为，不能据此自动关闭思考或改写 Medium 默认值。
- **探针产物：** [本机 API 参数摘要](../out/test-artifacts/ollama-qwen3-low-thinking-control-20261007.json)，SHA-256 `e31c0eef1aa1fcc2c76ab5cd00ad942cdb68a8380f3404149c6155923395c067`；不保存提示正文或 reasoning 正文。
- **实现：** `ProviderCapabilityResolver` 仅对 `Type=Local`、Ollama OpenAI-compatible、精确 `qwen3:4b`、HTTP loopback `11434/v1` 声明 `ReasoningEffortValues={none}`。`AIService` 仅在用户主动选 Low 时发送 `reasoning_effort=none`；Medium/High 使用 Ollama 模型默认行为。其它型号、端口、Provider 类型和平台均保持 capability unknown，不继承开关。设置能力说明明确标出验证端点及档位映射。
- **TDD 与验证：** 新测试先因能力 `unknown` 和 Low 请求体缺少 reasoning 字段而失败；实现后精确端点能力矩阵及 Low/Medium/High 请求映射 **7/7 通过**。完整 `Huaxiazi.Tests` **2,017 通过、11 跳过、0 失败（共 2,028）**。实机探针不保存或输出 reasoning 正文，只保留是否存在字段及 token/字符长度摘要。
- **收益与边界：** 用户选择 Low 时，当前已验证 Ollama 端点会实际收到关闭思考的控制字段；简单探针减少 66 completion tokens（30.8%），但最终答案形式变化，不能作为润色收益证据。Medium 默认、Temperature/top_p/输出上限与云端请求均不变。后续需用完整内部开发切片或人工评分确认 Low 的成稿质量/格式风险；不自动启用、也不以此解锁阶段 0。

官方依据：[Ollama OpenAI compatibility](https://github.com/ollama/ollama/blob/main/docs/api/openai-compatibility.mdx) · [Qwen3 thinking controls](https://qwen.readthedocs.io/en/stable/getting_started/quickstart.html)。

### 阶段 2：以应用自管 GGUF 运行时验证本地生成，并排除实验机 Ollama 配置（2026-10-07）

- **范围澄清：** 上一节及更早的 Qwen3/Ollama 诊断记录只作为历史实验材料；本机 Ollama 实例、服务和端口不是话匣子的受管本地模型、运行时依赖、安装内容或交付验收对象。产品本地模型链路使用应用管理的 GGUF、版本化 llama.cpp CPU/Vulkan 包和 loopback 服务。实验专属的精确 `qwen3:4b` 参数预设、`reasoning_effort=none` 能力映射、设置页“应用诊断预算”按钮和对应请求行为已从产品代码移除。一般 OpenAI-compatible 本地 Provider 的用户配置能力未改变；本次验收不使用该路径。
- **TDD 与回归：** 先将用例改为断言实验专属能力不出现在产品参数说明/请求体、`qwen3:4b` 不获得实验预算、设置页不显示实验按钮；首轮结果按预期 **5 项失败**（分别检出 `reasoning_effort=none`、4096 预算和设置绑定仍存在）。移除专属实现并保留已有配置值后，实验隔离及 UI/配置回归 **14/14 通过**。受管运行时、模型目录、下载、ManagedLocal 路由、后端观测和交付回归组 **88/88 通过**；`LocalOnly` 有本地运行失败时不会创建云端备用客户端的行为由 `CompanionGenerationIntegrationTests` 覆盖。
- **真实受管链路复跑：** 使用隔离模型注册表中的官方 Qwen3-4B Q4_K_M（SHA-256 `7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5`），并将固定 llama.cpp b11424 CPU/Vulkan 归档经本地测试 handler 交给生产 `LocalRuntimePackageService` 校验、安装、启动和卸载。随后通过 `LocalRuntimeManager`、本地 `/health` 和产品 `PolishWorkflowService` 各执行一条内部合成润色输入。CPU 与 Vulkan 均 health **200**、响应 `Final`、0 个质量门问题，卸载后均确认 runtime 已移除；两组 `external_api_requests=0`。CPU 工作流 **31.73 秒**、启动 **4.80 秒**、进程峰值工作集 **5,030,256,640 bytes**；Vulkan 工作流 **5.73 秒**、启动 **4.70 秒**、进程峰值工作集 **3,220,852,736 bytes**。机器记录见[ManagedLocal smoke JSON](../out/test-artifacts/managed-local-runtime-phase2-smoke-20261007/managed-local-runtime-phase2-smoke-20261007.json)，SHA-256 `8c34316a79c675260fddf5a28be3e5e53920f3a75a77e80507341db0731c4845`。
- **全量验证：** `dotnet test Huaxiazi.Tests/Huaxiazi.Tests.csproj --no-restore`：**2,022 通过、11 跳过、0 失败（共 2,033）**；定向工作区 `git diff --check` 无空白错误，仅显示既有 LF/CRLF 行尾提示。
- **当前结论与边界：** 这台 Windows 机器上的应用自管 GGUF → llama.cpp CPU/Vulkan → 本地 API → 润色工作流已重新走通，不依赖 Ollama，也没有云请求。每个后端仅一条样本，只证明链路能工作，不证明质量、稳定性能或跨硬件兼容。正式用户的真实下载/续传仍需实际分发网络验收；干净 Windows 的 CRT/Vulkan 驱动矩阵仍待验证。阶段 0 正式盲评贡献仍为 **0**。

### 阶段 2：本地运行时按需安装 VC++ 前置依赖（2026-10-07）

- **实现：** 受管本地服务启动前检查 HKLM x64 VC++ Runtime 版本；低于固定最低版本时，按需下载 Microsoft `14.51.36247.0` 安装包，验证固定 SHA-256 与 Microsoft Authenticode，再经 UAC 以 `/install /passive /norestart` 安装并重查注册表。依赖不可用、用户拒绝或安装失败时给出原因和微软入口，不切换到云端。其服务只接在 ManagedLocal 运行时启动路径。
- **验证：** 新增前置服务测试 **7/7 通过**，覆盖已安装版本跳过、SHA/签名拒绝、UAC 拒绝、安装失败重查、网络失败和用户取消。当前开发机已安装 `14.51.36247.00`，因此没有弹出 UAC 或修改系统；从官方版本固定链接取得的文件版本、SHA 和签名者均与锁定值一致。
- **运行时选择：** b11429 CPU/Vulkan 包实测下载哈希与官方 attestation 一致，`--version` 均报告 build 11429。现有隔离 GGUF 在本回合不可用，无法做计划要求的同模型 CPU/Vulkan 兼容性复跑，因此产品锁定保持 b11424。
- **待完成验收：** 本机没有 Hyper-V/VirtualBox/VMware VM 工具；未找到 Inno Setup 6 `ISCC.exe`；当前也没有可调用的原生 WPF 桌面交互通道与隔离 GGUF。故干净 Windows 首次安装、真实 UAC 同意/拒绝、WPF 下载/取消/重启和 Portable/Setup 重打包尚未完成。详见[阶段二续作记录](../managed-local-phase2-continuation-2026-10-07.md)。本步自动化测试没有修改现有应用配置或系统运行库。
- **本次构建结果：** 全套 **2,029 通过、11 跳过、0 失败**；Release 编译 0 warning / 0 error。win-x64 Portable 候选已在 `out/test-artifacts/managed-local-phase2-release-20261007/` 生成并检查不含模型/推理运行时/VC++ installer/Ollama 文件；由于缺少 ISCC，没有覆盖 `release/` 或重建 Setup。

