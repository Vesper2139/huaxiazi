# 话匣子模型调优数据流水线

> 本目录只服务于离线架构评测与数据生成，不属于桌面应用运行时。生产 Agent 的分层架构与编排边界见 [Agent 架构与工程编排](../docs/AGENT_ARCHITECTURE.md)。

## 提示架构调优（当前目标）

本项目的“调优”首先针对 agent 架构，而不是本地模型权重。`architecture-dataset` 生成用于提示层消融、权重搜索和 harness 门禁评估的数据：每条记录都显式包含 `system`、`developer`、`skill`、`harness`、`output_contract` 五层的具体措辞、权重向量、场景、风险、失败模式、期望决策和 gold 输出。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-dataset --output .\datasets\architecture-v1
```

输出 `architecture_train.jsonl`（候选架构拟合集）、`architecture_dev.jsonl`（选择变体/权重的开发集）、`architecture_test.jsonl`（冻结的最终架构测试集）和 `manifest.json`。默认规模仍为 10,000/1,000/1,000；场景覆盖普通请求、歧义、提示注入、高风险事实、工具失败、格式违规、超长上下文、Skill 冲突、记忆冲突和工具并行。生成器会校验五层权重总和为 1、场景风险标注、场景与 `expected_decision` 的语义一致性、跨 split 输入去重，以及 `generalization_family` 词族隔离（dev/test 使用训练集未出现的措辞族）。`seed` 会确定性地参与场景、变体和措辞族抽样：同 seed 完全复现，不同 seed 产生不同样本，可用于多种子稳定性实验。记录还包含 `required_constraints`、`forbidden_constraints`、`fidelity_anchors`，manifest 会声明字段、场景和 split 隔离策略。该数据集不是 SFT 训练文件，也不会触发本地模型权重训练。

`datasets/architecture-v1/candidate-configs.json` 提供四个可直接用于实验的候选架构（完整五层措辞与权重），可作为 dev 阶段的起始搜索空间。
批量展开前会严格校验候选 ID 唯一、五层措辞非空、权重有限且非负并总和为 1；不合格配置直接拒绝，避免生成不可比较的实验批次。

将候选架构展开为供真实 Provider 批量推理的请求（不包含 gold，避免评测泄漏）：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-batch --input .\datasets\architecture-v1\architecture_dev.jsonl --candidates .\datasets\architecture-v1\candidate-configs.json --output .\datasets\architecture-v1\architecture_dev_requests.jsonl
```

`architecture-batch` 明确拒绝 `test` split；候选搜索只能使用 train/dev，冻结 test 只能在架构锁定后一次性评估，防止测试集反馈造成选择偏差。

Provider 返回后，将每行整理为 `id/architecture_id/split/output`，再使用 `architecture-evaluate` 计算候选架构得分。`split` 用于审计请求来源，Provider 脚本拒绝 test 或缺失 split，评测器拒绝与 gold 不匹配的 split。`format_violation` 场景按严格 answer-only JSON schema 评分，而不是仅检查 JSON 是否可解析。报告保留按样本的 constraint/anchor 等描述率，同时增加按 generalization family 的完整通过率及 Wilson 下界：一个家族只有在该家族所有样本通过相应指标时才记为家族通过。`PromotionEligible` 的置信门槛使用家族完整通过率，避免同一语义模板族的变体重复抬高置信度；Pareto 前沿仍对同分布样本点估计比较。
评测器会按 `(architecture_id, id)` 去重并报告 `coverage_rate`；缺失或重复预测不会抬高分数，覆盖率低于 99% 的候选自动禁止晋级。
使用 `architecture-compare` 可对两个候选在相同样本 ID 上做成对比较。报告保留逐样本胜负与描述性胜率，并按 `generalization_family` 合并成每族一个胜/负/平结果；Wilson 下界和显著性在族级计算，至少 30 个决定性独立家族且下界高于 50% 才标记为显著。当前 architecture-v1 只有 12 个总家族（test 仅 2 个），因此适合内部架构诊断，不足以据其作“跨任务家族显著提升”结论；要做晋级决策需先扩充独立家族并验证其来源/任务覆盖。

如果本机使用 Ollama，可直接运行 `training/invoke-ollama-architecture-eval.ps1`；它只做推理，不训练或修改模型权重：

```powershell
powershell -ExecutionPolicy Bypass -File .\training\invoke-ollama-architecture-eval.ps1 -Model qwen3:4b -Limit 24
```

推荐实验顺序：固定基础模型和工具环境，先在 train 上筛掉明显失败的层级措辞，再只用 dev 搜索层权重/组合，锁定候选架构后一次性在 test 上评估；任何 test 结果都不得反馈回候选集。

RAG 的稠密检索可以通过 `IEmbeddingIndex` 注入。内置 `InMemoryEmbeddingIndex` 用于离线验证向量维度、有限值和余弦相似度；生产环境只需替换查询编码器/索引实现，不改变 `HybridContextRetriever` 的词法+稠密混合排序契约。

架构预测文件每行格式为 `{"id":"hxa-v1-test-...","architecture_id":"policy-first-v2","output":"模型最终输出"}`。可用以下命令计算安全、决策、格式、简洁度和综合效用，并标记 Pareto 最优候选：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-evaluate --gold .\datasets\architecture-v1\architecture_dev.jsonl --predictions .\architecture-predictions.jsonl --output .\architecture-report.json
```

该项目先固定“标准数据层”，再由训练侧绑定具体基础模型或 LoRA 适配器。默认生成：

- `train` 10,000 条、`dev` 1,000 条、冻结 `test` 1,000 条
- `polish` 与 `prompt_optimize` 各 50%
- `sft.jsonl`、`preference.jsonl` 和可审计的 `canonical.jsonl`

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- generate --output .\datasets\v1
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- validate --input .\datasets\v1\canonical.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-agent-validate --input .\datasets\polish-agent-v2\canonical.jsonl --sft .\datasets\polish-agent-v2\sft.jsonl --manifest .\datasets\polish-agent-v2\manifest.json --source .\datasets\v1\canonical.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-validate --input .\datasets\ai-evaluation\blind-eval.jsonl --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- source-register-validate --register .\datasets\ai-evaluation\source-register.json --dataset .\datasets\ai-evaluation\blind-eval.jsonl --output .\datasets\ai-evaluation\runs\<new-run>\source-validation.json
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- leakage-check --input .\datasets\v1\canonical.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- report --input .\datasets\v1\canonical.jsonl --output .\datasets\v1\report.json
# predictions.jsonl 每行格式：{"id":"hxz-v1-...","output":"模型输出"}
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- evaluate --gold .\datasets\v1\canonical.jsonl --predictions .\predictions.jsonl --output .\evaluation.json
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- mine-failures --gold .\datasets\v1\canonical_dev.jsonl --predictions .\predictions.jsonl --output .\training\dpo-failures.jsonl
```

`blind-validate` 的 JSON `coverage.task_split_coverage` 会固定报告 `polish` / `prompt_optimize` × `development` / `frozen_test` 四格的样本数、高风险、常规、澄清、格式、事实/约束锚点和输入风格计数，包括空格。`product_slice_counts` 仅对已知产品场景/类别输出固定白名单计数；未知、自定义或非字符串值合并到“未指定或自定义”，不会回显 `context` 原值。缺失的 prompt 类别按产品默认值计入“通用任务”。该矩阵是只读诊断，不额外拒绝当前准入协议接受的数据；产品场景/提示词类别没有独立 schema 字段，所以计数可供受控 cross-tab 复核，但不能代替样本来源审查或经批准的覆盖准入规则。

多轮润色记录可选提供 `conversation_id` 和 `turns`，每轮含从 1 开始连续编号的 `turn_index` 与 `user_input`；顶层 `input` 必须与最后一轮完全一致。多轮只支持 `polish`，评测器按用户轮次顺序调用产品工作流，并把此前用户输入及助手成稿作为独立历史上下文传入下一轮。历史成稿被提示为草稿上下文而非事实来源，当前轮的更正优先于早先要求。审计会对每轮输入执行隐私扫描和训练集/开发集重叠检查。提示快照逐轮保存实际静态提示；此前助手回复用明确占位符表示，因此快照哈希不伪称可预知运行时成稿。缺省 `turns` 的记录维持原单轮格式和提示行为。

`blind-validate` 还输出 `reviewer_agreement`，只汇总身份引用结构上由两名不同 reviewer ID 提交的初始标注之逐字段和全标签精确一致数/比例，不包含原始标签文本；这不能认证真实身份或独立性。多值列表按准入校验的规则去空白并忽略顺序，其他文本字段使用精确比较。由于证据流程为每个“样本 × 评审”生成一次性 `reviewer_ref`，不能把第一/第二份提交跨样本视为固定评审员；因此 `task`、`input_style`、`expected_decision` 与二元 `clarification_required` 报名义 Krippendorff’s α，并合并两份标注给出 `pooled_category_counts`。该 α 对轮换评审引用有效，但仍要求每个样本具有可比较的两份标注。`clarification_required` 另报正类（需要澄清）与负类（无需澄清）特定一致率，以免整体值隐藏类别差异；某类在双方都未出现时该类的特定一致率为 null。未知类别折叠为固定的 `other_or_invalid`，不回显任意输入。`risk_level` 具有序数含义但尚无经批准的距离权重，因此只提供精确率与固定类别计数，不计算名义 α；自由文本/多值字段同样不套用名义 α。`chance_corrected_status` 取 `computed`、`undefined_perfect_expected_agreement`、`no_comparable_records` 或 `not_applicable_scale`；全体评分同类时预期分歧为零，α 无定义并返回 null。精确一致率、类别计数和 α 必须结合具体分歧案例审阅；α 不是单独的质量裁决。本报告是量表校准诊断，不设准入阈值，不区分合理同义标注与量表歧义；正式决定前仍须双人校准和人工审阅。方法选择参考 [Artstein & Poesio 的计算语言学评审一致度综述](https://aclanthology.org/J08-4004/) 和 [Krippendorff’s α 的方法/实现说明](https://journal.r-project.org/articles/RJ-2021-046/)；二元类别另参考[建议同时报告正负类一致率的研究](https://pubmed.ncbi.nlm.nih.gov/2189948/)。首批样本须结合实际类别计数、分歧案例和评审负责人审阅，不沿用通用通过线。

`blind-evaluate` 的评分 JSON 另含 `product_slice_reports`，按候选、task、split 和同一白名单切片输出样本数、覆盖率、Schema 合法率、事实/约束保留率、直接可用率、语气匹配、澄清、安全与高风险关键事实反转指标。失败请求、缺失预测、重复/无效预测分别计数；所有质量率仍以该切片 gold 样本数为分母，失败与缺失不会被成功候选输出掩盖。切片也报告 API 延迟 p50/p95、输入/输出与缓存 token、错误类别，以及本地 tokens/s、峰值内存和模型加载时间；每类性能指标同时输出观测数，token 合计只在该切片所有实际尝试都提供该 token 字段时输出，否则为 `null`，防止把部分 token 当总成本。性能分位数仅基于有观测的尝试，必须连同观测数解释。它们是描述性切片，不运行独立显著性或晋级门槛；小样本切片必须结合样本数与语义族级比较解释。

该评分 JSON 新增切片字段后，manifest finalizer 仍会按当前 gold、预测和比较重新评分并要求完整匹配；此前生成但尚未封存的旧版报告须使用冻结输入重新运行 `blind-evaluate` 后再封存。已封存的历史工件保持原样，跨版本比较时按各自代码修订和报告哈希区分。

生成使用固定 seed，并在 `manifest.json` 写入完整数据集哈希。任何模板、比例或校验规则变更都应提升数据集版本并重新冻结测试集。当前样本为项目自有合成数据；在接入真实用户数据或教师模型前，必须经过脱敏、授权、双人抽检与独立测试集去重。

`polish-agent-validate` 是 v2 Agent 合成数据专用审计器，不可用旧版 `validate` 命令替代。它对照 v1 来源文件校验来源哈希、记录映射和字段转换，检查 train/dev/test 模板族隔离、manifest 数量、canonical/SFT 内容对齐及其哈希。审计结果中的 `human_blind_review_evidence=false` 是固定事实：此数据仍是合成回归/训练资产，不能替代 500 条授权外部盲评集，也不能把 `reviewer_count` 当作真实双盲证据。

`polish-regression-neardup-report` 仅对冻结内部族代表做精确成对字符 3-gram Jaccard，输出达到版本化阈值的人工候选清单；它校验 `cases.jsonl` 哈希和族数与父 manifest 一致，所有候选都保持 `pending_human_adjudication`，不会自动合并族或更改 split。当前规则用 0.80 作为未校准的送审队列阈值，同时输出 0.70/0.80/0.90 敏感性计数；这不是近重复分类器准确率或质量结论。命令示例：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-neardup-report --cases .\datasets\polish-regression-v1\cases.jsonl --parent-manifest .\datasets\polish-regression-v1\manifest.json --rules .\docs\polish-regression-near-duplicate-rules-v1.json --output .\datasets\polish-regression-w2-neardup-candidates-v3
```

`polish-regression-supplement-neardup-report` 使用同一算法比较冻结 v1 与 AI 草稿补充集，并覆盖所有至少一端来自补充集的族对。它同时校验两份案例文件哈希、草稿 manifest 对冻结父 manifest 的绑定、族数以及跨集 ID 唯一性。产物仅供内部排查，阶段 0 贡献为 0，禁止盲评，永不自动合族。若 0.70/0.80/0.90 的候选计数均为 0，只能说明字符三元组规则没有命中，不能证明语义族无重复，也不能代替人工终审。当前 36 族共 **510 对**的扫描结果在三个阈值均为 **0 对**；因此补充样本的语义重复仍未由此结论确认。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-neardup-report --base-cases .\datasets\polish-regression-v1\cases.jsonl --base-manifest .\datasets\polish-regression-v1\manifest.json --supplement-cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --supplement-manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json --rules .\docs\polish-regression-near-duplicate-rules-v1.json --output .\datasets\polish-regression-ai-supplement-neardup-v1
```

人工填写模板副本后，用 `polish-regression-neardup-validate --report-dir ... --decisions ...` 检查候选对覆盖、去重、决定枚举、理由、UTC 时间和引用一致性。`decision` 允许 `same_semantic_family`、`distinct_task_intent_same_backbone`、`distinct_semantic_families`、`uncertain_request_arbitration`。返回通过只证明裁定记录结构完整，不证明 reviewer 身份真实或语义判断正确；任何决定都不会自动更改冻结 v1，后续若要调整分组必须另建新版本并重新验证。

人工审阅 XLSX 可用 `polish-regression-neardup-import-workbook` 导入：它逐一核对候选包及工作簿中的来源哈希、元数据、pair_id 和冻结案例证据，仅复制人工填写的 decision/reviewer/time/rationale 到模板。输入只读，输出必须是不存在的新文件；缺项、过期工作簿、证据变动、候选错配或输出已存在都会拒绝。若只有理由字段存在占位语，会一次报告全部需要修订的 `pair_id`，不会创建部分结果。成功导入后仍须运行上述 `neardup-validate`，该通道不判断语义结论真伪。

AI 补样 CSV 回收后，可用 `polish-regression-supplement-review-validate` 检查源样本哈希、CSV 行与案例证据绑定、完整审阅字段、决策一致性、理由、严格 UTC 时间戳与隐私/密钥启发式命中。此命令只验证，不导入或改写样本；结果始终声明阶段 0 贡献为 0。只有校验完成且人工审阅通过的 accept/edit 行，才进入独立新版本整理。

`polish-regression-supplement-coverage-report` 可按行为类别核算 AI 草稿族数与到目标数的规格缺口。报告绑定案例与清单哈希，将类别明确标为未审阅草稿；它不授权生成样本，也不宣称模型具备该行为。示例将计划建议的每类 5 族作为规划目标：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-coverage-report --cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json --behaviors clarification_positive_examples,needs_clarification_outputs,format_and_schema_requirements,high_risk_fact_reversal,tone_and_scenario_diversity,non_workplace_scenarios,multi_turn_revision_and_user_negation,negation_quantity_time_condition_boundaries,prompt_injection_resistance,cross_language_mixing --minimum-per-behavior 5 --output .\datasets\polish-regression-ai-supplement-coverage-v1
```

W4.3 规格包可用 `polish-regression-supplement-spec-validate` 校验案例/覆盖/规格 manifest 哈希、必填规格字段，以及每类规格数量与覆盖缺口是否一致。结构有效不代表人工批准；报告始终返回 `human_approval_recorded=false`、`generation_authorized=false`，人工评审前不能进入样本生成。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-spec-validate --specs-dir .\datasets\polish-regression-ai-supplement-specs-v1 --coverage-dir .\datasets\polish-regression-ai-supplement-coverage-v1 --parent-cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --parent-manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json
```

为减少逐条规格评审的整理成本，可用 `polish-regression-supplement-spec-review-packet` 从已哈希绑定的规格包生成 CSV 审阅副本。它保留事实、输入、约束、评分锚点和禁止内容，并为每条规格留出人工接受/修改/拒绝、身份、UTC 时间和理由；创建命令本身不会批准规格或授权生成。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-spec-review-packet --specs-dir .\datasets\polish-regression-ai-supplement-specs-v1 --coverage-dir .\datasets\polish-regression-ai-supplement-coverage-v1 --parent-cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --parent-manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json --output .\outputs\polish-regression-ai-supplement-spec-review-2026-10-05-v2
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-spec-review-validate --specs-dir .\datasets\polish-regression-ai-supplement-specs-v1 --coverage-dir .\datasets\polish-regression-ai-supplement-coverage-v1 --parent-cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --parent-manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json --packet-dir .\outputs\polish-regression-ai-supplement-spec-review-2026-10-05-v2
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-spec-review-import --specs-dir .\datasets\polish-regression-ai-supplement-specs-v1 --coverage-dir .\datasets\polish-regression-ai-supplement-coverage-v1 --parent-cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --parent-manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json --packet-dir .\outputs\polish-regression-ai-supplement-spec-review-2026-10-05-v2 --output .\outputs\polish-regression-ai-supplement-spec-reviewed-v1
```

规格审阅 validator 校验 CSV 全量覆盖、源字段快照、修订 JSON、决定、理由和 UTC 时间；只有全部规格都被自报为人工 approve/edit 且无 reject 时，才报告 `generation_authorized=true`。该工具不能认证填写者身份（`reviewer_identity_verified=false`）。导入器保留所有源规格与拒绝决定；只有整批满足生成授权条件时才写入 generation input，且不执行模型生成、不改变阶段 0 计数。

W4.4 的既有草稿可用 `polish-regression-supplement-lineage-audit` 检查每条案例的生成模型 ID、提示 SHA-256、随机种子、严格 UTC 时间及操作者，并核验案例文件哈希和内部数据边界。报告无论通过与否都不授予盲评准入或阶段 0 贡献；历史元数据缺失时不得回填猜测值。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-lineage-audit --cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --manifest .\datasets\polish-regression-ai-supplement-draft-v1\manifest.json
```

校验有效后，`polish-regression-supplement-review-import` 会将全部 accept/edit/reject 决定写入目录外的 create-only JSONL，保留拒绝记录、最终标签与成稿、质量维度、理由、审阅者引用和源数据哈希。无效审阅不会创建输出；该导入器不把案例合并进数据集，也不认证审阅者身份或提升阶段 0 贡献。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-review-validate --cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --review-csv .\outputs\polish-regression-ai-supplement-review-2026-10-05\human-review-template.csv --manifest .\outputs\polish-regression-ai-supplement-review-2026-10-05\manifest.json
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-supplement-review-import --cases .\datasets\polish-regression-ai-supplement-draft-v1\cases.jsonl --review-csv .\outputs\polish-regression-ai-supplement-review-2026-10-05\human-review-template.csv --manifest .\outputs\polish-regression-ai-supplement-review-2026-10-05\manifest.json --output .\outputs\polish-regression-ai-supplement-review-results-2026-10-05\human-decisions.jsonl
```

本地模型候选稿使用独立审查流水线：候选 JSONL 与元数据哈希绑定；人工填写 CSV 时必须保持样本输入和候选文本不变，并记录评分、人工审阅者、UTC 时间和具体理由。校验器只汇总内部人工反馈；导入结果写入审查包外的 create-only JSONL，不合并为 gold 数据，也不产生阶段 0 贡献。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- local-model-candidate-review-validate --candidate-packet .\out\test-artifacts\local-model-quality-dev-20261006\review-run-20261006T115546Z\review-packet\candidate-review.jsonl --review-csv .\out\test-artifacts\local-model-quality-dev-20261006\review-run-20261006T115546Z\review-packet\candidate-review.csv --metadata .\out\test-artifacts\local-model-quality-dev-20261006\review-run-20261006T115546Z\review-packet\review-metadata.json
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- local-model-candidate-review-import --candidate-packet .\out\test-artifacts\local-model-quality-dev-20261006\review-run-20261006T115546Z\review-packet\candidate-review.jsonl --review-csv .\out\test-artifacts\local-model-quality-dev-20261006\review-run-20261006T115546Z\review-packet\candidate-review.csv --metadata .\out\test-artifacts\local-model-quality-dev-20261006\review-run-20261006T115546Z\review-packet\review-metadata.json --output .\outputs\local-model-human-review-2026-10-06\candidate-reviews.jsonl
```

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-neardup-import-workbook --report-dir .\datasets\polish-regression-w2-neardup-candidates-v3 --cases .\datasets\polish-regression-v1\cases.jsonl --workbook .\outputs\polish-regression-neardup-review-2026-10-05\near-duplicate-human-review.xlsx --output .\outputs\polish-regression-neardup-review-2026-10-05\human-decisions.completed.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-neardup-validate --report-dir .\datasets\polish-regression-w2-neardup-candidates-v3 --decisions .\outputs\polish-regression-neardup-review-2026-10-05\human-decisions.completed.jsonl
```

该流水线完成数据构造、校验和导出，不宣称已经完成某个具体基础模型的训练。实际 SFT/LoRA 训练需要另行提供基础模型、训练运行时、显存预算和超参数配置，并以 `dev` 指标门禁后才允许评测 `test`。

评测器会报告 exact match、直接可用率、安全通过率和澄清召回率；安全未满分、可用率低于 85% 或澄清召回率低于 80% 时返回非零退出码，适合作为训练流水线门禁。

`mine-failures` 会把可识别的低质量预测转成下一轮 DPO 偏好对：gold 为 `chosen`，失败预测为 `rejected`，并记录 `unsafe` 或 `not-directly-usable` 原因。它只处理实际存在的预测，不会把缺失结果伪造成负例。

可用 `training/iterate.ps1` 运行一轮可追溯迭代（默认 20 条 dev smoke）：

```powershell
powershell -ExecutionPolicy Bypass -File .\training\iterate.ps1 -Model qwen3:4b -Limit 20
```

每轮会保存 validation、gold、predictions、evaluation 和 dpo-failures，作为下一轮训练/调参的证据链。

真实 SFT 入口位于 `training/train_sft.py`。它只接受 HuggingFace 模型目录/Hub ID，遇到 `qwen3:4b` 这类 Ollama 名称会拒绝运行；先安装 `training/requirements-sft.txt`，再执行：

```powershell
python .\training\train_sft.py --model <hf-model-dir> --train .\datasets\v1\sft_train.jsonl --output .\training\artifacts\lora-v1
```

训练前可运行 `powershell -File .\training\preflight.ps1 -Model <hf-model-dir>`，检查依赖、CUDA、GPU 显存、磁盘和训练文件是否就绪。

若本机已安装 Ollama，可用 `training/invoke-ollama-eval.ps1` 通过 `/api/chat` 生成真实模型预测（显式设置 `think=false`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\training\invoke-ollama-eval.ps1 -Model qwen3:4b -Limit 20
```

脚本支持完整 test 集，但应先以小样本 smoke、再跑 dev、最后跑冻结 test。评测器会把缺失预测按失败处理；本次 1 条 qwen3:4b smoke 预测在完整 test 门禁上得到 `passed=false`，这是预期的保守行为，不代表模型完整测试分数。
