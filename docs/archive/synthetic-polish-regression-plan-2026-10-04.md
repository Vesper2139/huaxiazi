# 合成润色样本整理与内部回归集：历史实施记录

> **历史计划与数据血缘说明。** 本文记录 2026-10-04 起的内部数据整理过程，不再定义当前开发顺序。数据是否可用于某项评测，以对应目录的 `README.md`、`manifest.json` 和 [STATUS.md](../STATUS.md) 为准；内部合成材料的正式盲评贡献为 0。

### 2026-10-05 继续推进：W4 AI 合成补样草稿已落盘

- 按 W4 缺口报告新增独立版本 `datasets/polish-regression-ai-supplement-draft-v1/`：20 条、20 个唯一语义族、10 个行为类别，development/regression 各 10 条，决策为 clarify 2 条、polish 18 条。
- 样本覆盖澄清、澄清输出、JSON/条目格式、高风险事实保真、语气与非职场场景、多轮修订、否定/数量/时间/条件、中英混写和引用注入文本处理。`tool_failure` 不适用于当前无工具调用的润色样本契约，未伪造此类轨迹。
- 逐条标为 `ai_assisted_draft` / `unreviewed`，manifest 绑定父数据集与缺口报告哈希。结构/枚举、唯一 ID/族、10/10 split、哈希和 PII 启发式扫描通过；0 个 ID 与原集冲突。
- 同步生成 `outputs/polish-regression-ai-supplement-review-2026-10-05/` 逐条审阅 CSV 与说明，20 行决定栏留空，审阅包绑定源样本 SHA-256，支持接受/编辑/拒绝和理由记录。
- 新增 `polish-regression-supplement-review-validate` 命令，核验源哈希、CSV 行与样本证据对应、人工决定字段、理由、UTC 时间和隐私/密钥启发式命中。专项 9/9 通过；与 near-duplicate 导入/审计、blind 边界回归合计 39/39 通过。空白 CSV 实测为 `valid=false`、审阅 0/20、phase 0 贡献 0，且无来源错配。
- 新增 `polish-regression-supplement-review-import`：只在上述校验有效时，将全部 accept/edit/reject 决定写入源数据和审阅包目录之外的 create-only JSONL，保留拒绝记录、最终标签/成稿、质量项、理由、审阅者引用和源数据哈希；不合并案例、不提升阶段 0。导入器与 CLI 正反路径纳入后，相关回归合计 45/45 通过。
- 用当前空白 CSV 实测导入命令：返回 exit code 2，问题类别为 review-ratings、review-rationale、review-time、review-verdict、reviewer；目标文件和目录均未创建。人工审阅仍为 0/20。
- 该命令只做校验、不自动合并或晋级，也不认证审阅者真实身份。补样 CSV 中单条 claims 曾因 PowerShell 数组枚举被投影成对象，已修正为稳定 JSON 数组并重算 manifest 哈希。
- 这些是 AI 草稿参考答案，不是金标。该独立版本不改冻结 v1、既有候选包或工作簿，不得用于盲评、模型晋级或微调；`phase_0_gate_contribution=0`。可继续做人工抽检和改稿，不必等待整个项目计划完成。
- 实现与验证摘要见 [AI 定制化实施状态](ai-customization-implementation-status.md) 的同日记录。

- 状态：**执行中**（W0/W1/W2 精确族映射及 W2.3 候选报告/W3.1–W3.2/W4.1–W4.2/W5 隔离验收/W6.1–W6.3 来源登记与准入边界/W7/W8 部分验收已落地；W2.3 工作簿含占位理由且结论与当前族定义冲突，仅作为临时方案；W4 已生成 20 条 AI 草稿、审阅包、CSV 校验和处置记录导入命令，人工审阅仍为 0/20；阶段 0 贡献仍为 0）
- 日期：2026-10-04
- 范围限定：**仅**「审计并整理现有 3,000 条合成润色样本 → 形成内部回归集」以及由此衍生的 AI 辅助标注、去重分层、覆盖统计与可选补样；**不包含**授权盲评集构建、来源授权核验、独立评审证据采集与任何阶段门解锁。
- 阶段 0 门槛贡献：**0 条**。本计划产出的任何数据均不得计入授权盲评样本数。

### 2026-10-05 状态复核：W2.3 真人裁定门槛仍未通过

- 复核对象：`datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl`（原名 `decisions.completed.jsonl`，已改名以免被误读为人工完成件）、同目录 `README.md` 与 `gate-status.json`、v3 候选包、原始空白审阅工作簿及现有 W3.3 审阅包。
- 实测：19 条记录均标为 `reviewer_id=reviewer-a`，同目录 README 明确该身份由 AI 扮演；校验命令报告 `valid=true`、`adjudicated_count=19`，同时 `reviewer_identity_verified=false`。这里的 `valid=true` 仅证明记录格式、候选引用与覆盖完整，不等于真人裁定或 W2.3 人工门槛通过。
- 原始操作工作簿仍为空白（19 行，决定/审阅人/时间/理由字段均未填写）。因此不能将 AI 模拟裁定当作项目规定的独立人工审核，也不能据此锁定 W2.3 边界。
- 决策：保留现有 JSONL 及其 README 作为 AI 模拟分析记录，不改写、不删除、不纳入阶段 0。W2.3 状态恢复为 `awaiting_human_review`；W3.3 与 W4.3 补样保持未解锁。既有 W3.3 审阅包仍是准备材料，不能用来绕过上游人工边界确认。
- 下一步：真人审阅者在原工作簿的 19 对上独立填写决定、身份、UTC 时间与理由，优先复核 9 对跨 split 候选；通过安全导入器生成新的裁定文件并运行校验。任何合族或 split 调整都必须新建数据版本并重建下游工件。阶段 0 正式盲评贡献仍为 **0**。

### 2026-10-05 最新复核：真人副本已填部分字段，不能导入

- 新发现副本：`outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review - 副本.xlsx`（最新核对修改时间 2026-10-05 07:02 UTC）。原始工作簿未改。
- 结构盘点：19/19 行填写了 decision、reviewer_id 和 UTC 时间；rationale 单元格虽非空，但均为占位语“无非空理由”，具体理由为 0/19。未从 AI 草稿替换审阅者 rationale。
- 门禁修复：新增 `ReviewRationaleRules`，导入器和裁定 validator 统一拒绝常见中英文占位语。回归测试先复现两个入口都会接受占位语，修复后近重复导入器/裁定器专项 **16/16 通过**。该规则只排除明显占位内容，不评价理由语义是否成立。
- 实际导入探测：对最新副本使用临时且预先不存在的输出路径运行官方导入器，返回 `decision-incomplete: rationale 必须是具体理由，不能留空或使用占位语`；探测输出未创建，工作簿、AI 草稿、冻结 cases 和候选工件未改动。
- 按用户明确要求继续推进：不等待理由补写，先将副本中的 19 个 `same_semantic_family` 选择作为**临时输入**，生成独立影响分支 `datasets/polish-regression-backbone-family-proposal-v1/`。该分支只计算这 19 条已选候选边的连通分量；其余 101/120 可能案例对未审阅、未作任何推断，因此 8 组不是最终语义族数量。
- 该分支覆盖 16 个 case/3,000 个 variant，投影出 8 个组，其中 2 组含 development 与 regression 成员。为避免同组跨 split，含 regression 的组全部放入 regression：development 从 2,414 降到 802（6 组），regression 从 586 增到 2,198（2 组），转移 1,612 个 development variants。regression 只有 2 个组，因此只能作内部诊断草案，不能代表可靠基准；阶段 0 贡献仍为 0。
- v1 冻结 cases、variants、AI 标签草稿及 review packet 均未改动；新目录的 manifest 已绑定输入哈希，case-map/groups/impact 的 SHA-256 复算无差异。该分支明确标记 `draft_proposal`、不通过正式 W2.3 门槛、不用于计分或模型晋级。
- 语义/政策冲突：19/19 对均选 `same_semantic_family`。工作簿定义要求两侧任务意图与关键约束实质相同；但当前已确认的族定义是「事实骨架 × purpose」，本候选集两侧 purpose 均不同。故需由项目负责人解释是否要推翻原族定义，还是修订逐对决定；不能由 AI 猜测其意图。
- 影响：若这些 `same_semantic_family` 结论经补充理由后仍成立，关联图至少会把 8 个案例和另 2 个案例各并成一组，且涉及跨 split 边界；不得把决定应用到冻结 v1。必须先确认族定义与 split 策略，再新建版本并重建 W3 review packet/W4 报告。W3.3、W4 补样仍未解锁，阶段 0 贡献为 **0**。
- 下一步：为 19 条分别填写基于两侧证据的 rationale；同时明确是否正式改为骨架级语义族。如果改定义，应先记录并版本化该政策决定，再按规则重新裁定并重建派生数据；若维持「骨架 × purpose」，请修订与该定义不一致的 pair 决定。完成后才运行导入器和 validator。

### 最新执行记录（2026-10-05）：内部标注草稿 v2

- 按分轨建议建立 `datasets/polish-regression-v2-draft/`，冻结的 v1 数据与清单未修改；v2 是 16 个暂定 task-family 的 sidecar 标注，不增加或重写样本。
- W3.1/W3.2 已落地：新增版本化标签 Schema、仅起草提示词、字段级来源/模型/prompt 哈希/时间/人工状态，以及 create-only 构建器。全部 16 条标签及字段保持 `unreviewed`；骨架映射与参考答案复用仅作为人工复核标志。
- 首次真实语料集成测试发现构建器读取层级报告时将 `phase0_gate_contribution` 错写为 `phase_0_gate_contribution`；已修复并补充真实语料集成断言。相关定向测试 **10 项通过**。
- 实际输出已核对：16 条、16 个唯一族，`human_verified=0`、`unreviewed=16`、`phase_0_gate_contribution=0`、`not_admissible_as_blind_eval=true`；v2 清单绑定父 v1 manifest 哈希。v1 校验仍为 `valid=true`、3,000 行、16 族、阶段 0 贡献 0。
- W3.3 人工逐族复审、骨架映射裁定、参考成稿复用裁定尚未开始；W3.4 仲裁尚未开始。v2 只能用于标注流程与覆盖诊断，不可计分或用于模型晋级。补样仍未生成，正式阶段 0 贡献仍为 **0**。

### 最新执行记录续（2026-10-05）：W3.3 人工审查包准备完成

- 输入：冻结 v1 的 16 条族代表、项目合成来源记录、v2 草稿清单与标签、W2 层级报告。产出：`datasets/polish-regression-review-packet-v2/`，包含源材料首轮独立审阅 JSONL、AI 草稿对照 JSONL、空白人工裁定模板、裁定 Schema、操作说明和绑定哈希的清单。
- 首轮材料不含 AI 草稿值；对照页单独存放并显示草稿作者。模板覆盖 16 个族、全部 15 个草稿字段（参考复用由单独的裁定项处理），并为骨架归组及参考成稿复用留出裁定值与理由。审阅包不替人作决定，也不强制文件访问顺序，因此“先独立审阅、后看草稿”是流程要求，不是技术隔离保证。
- 自查发现首版审阅包缺少独立标注填写位并遗漏 `legacy_task`，未作为审阅交付；保留为不可变历史产物，修正后以 v2 重新生成。v2 集成测试 **1 项通过**，核实 16 条齐全、首轮/草稿对照分离、空白模板未伪装为人工批准、create-only 防覆写和阶段 0 门槛贡献为零。
- 当前触发条件：指定的人类审阅者完成 16 个族代表的首轮独立字段审阅，再完成草稿对照、骨架映射和参考复用裁定。W3.3 仍未完成；若存在分歧，再启动 W3.4 第三人仲裁。人工结果回收并通过完整性/独立性验证前，不把任何标签标为已核实，不补样、不计分、不推动阶段 0。
- 为降低人工改 JSONL 出错的概率，另生成 `outputs/polish-regression-human-review-2026-10-05/review-workbook.xlsx`，含“首轮独立审阅”“草稿对照”“族级裁定”三个填写页和步骤说明。工作簿在视觉与 XLSX 结构检查中确认 4 张表、5 组下拉校验，含 16 条代表案例及 240 个草稿字段对照；工作簿记录所依据的 review-packet-v2 manifest SHA-256。该工作簿是便于人工操作的副本，不是权威标签源；回收后需将结果映射回独立评审数据并按 Schema/独立性规则验证。
- 后续验收工具已落地：`polish-regression-review-validate` 校验首轮与最终裁定是否覆盖同一 16 个族、首轮源字段未被改写、裁定字段和理由齐全、时间为 UTC、审阅者不等于字段血缘中的 AI 作者，并要求 `accept` 的最终值等于草稿值。它仅输出 `approval_candidate_count`，不把标签标成已核实、不生成金标。使用流程见 `docs/polish-regression-review-procedure-v2.md`；新增成功/缺族/篡改/作者冲突测试后，回归相关定向测试 **14 项通过**。
- W3.3 人工结果仍未回收，实际有效审阅数仍为 **0/16**。工具测试中的“人类审阅”均为合成测试夹具，不是项目真实审阅证据。下一步必须由指定人类按 procedure 填写两份 completed JSONL 并运行验收；若出现分歧才进入 W3.4。阶段 0 贡献继续为 **0**。

### 独立推进记录（2026-10-05）：W4.1–W4.2 全量证据覆盖与缺口报告

- 依赖判断：M2 的覆盖统计只依赖 W1/W2 冻结映射，W4.2 的“内部是否需要补样”则必须由人分诊。因此先计算证据，不预判补样需求、不生成补样。
- 审计发现旧 `coverage.json` 按族统计，但没有拆解 claim 锚点；“未标注”和“零正例”的含义也容易混淆。保留 v1 原件，新增 `PolishRegressionCoverageGapAuditor` 与命令 `polish-regression-coverage-gap-report`，对 3,000 条 source variants 聚合到 16 个族，分别输出源行数和出现族数；草稿维度单列并注明全未审。
- 最新不可变报告在 `datasets/polish-regression-w4-audit-v3/`，绑定 v1、v2 草稿及 W2 报告哈希。实测期望决策 3,000 行均为 `polish`；输出均为 `final`；明确格式要求 0 行；claims 非空 1,974 行、8/16 个族出现 claims；否定为真的 claim 族 0，quantity/time/condition 各 8 族；高风险 metadata 覆盖 488 行、6 个族，但没有 fact-reversal 结果标签；渠道五类共 3,000 行且每类都跨到 16 个族，说明模板轮转导致族级渠道计数重叠，不能把它当成独立场景样本量。
- 11 项缺口全保留 `internal_regression_need=pending_human_triage`、补样 `not_started`；“零已标正例”与“结构中不存在”分别标明，不据此宣称行为真实缺失或模型通过。manifest 强制阶段 0 贡献 0、盲评禁用、补样未授权。
- 先前自查发现仅报族覆盖会隐藏模板轮转频次，故新增“每类源行数 + 每类覆盖族数”双口径，并以新不可变版本输出；最新报告集成测试通过。该步骤结论只支持有证据的内部缺口分诊。下一触发条件：人审完样本标签与 W2 分组后，人工决定每项行为是否需要补样；通过规格审阅后才可进入 W4.3/W4.4。

### 独立推进记录（2026-10-05）：W5 隔离复核与 W7/W8 文档、确定性验收

- W5.1 路径/文件隔离复核：在 `datasets/ai-evaluation/` 中搜索内部回归集路径与来源枚举，没有发现 `polish-regression-v1` 引用；将 v1 目录所有文件与盲评目录下 JSON/JSONL 文件逐一按 SHA-256 比对，完全相同文件 **0 个**。盲评目录仍通过既有训练语料参数引用 `v1`/`polish-agent-v2`，不等同于引用新内部集；两个路径需避免混淆。
- W5.2 标识复核：v1 有 16 个 case、3,000 个 variant；case 使用 `hxz-polish-reg-*`，variant 使用 `hxz-polish-reg-variant-*`，来源 ID 仅存于 provenance/variant 血缘字段。case 与 variant 的 origin 都是 `project_synthetic_legacy`，未复用 `hxz-polish-agent-v2-*` 作为自身 ID。
- W5.3 来源守卫复核：`BlindEvaluationAuditor` 仅允许 `project_owned`、`licensed`、`user_authorized_deidentified` 三类盲评来源；新增 `blind-validate` 命令入口集成测试，确认冻结 v1 的真实 `cases.jsonl` 因缺少盲评 Schema 必需字段而被拒，并确认符合盲评记录 Schema 形状的样例若使用 `project_synthetic_legacy`、`ai_assisted_draft`、`human_authored_internal`，均以 `source-authorization` 被拒。该命令路径由 `Program.Main` 进入实际 CLI 校验器；这证明工具拒绝行为，不证明任何外部来源授权真实性。
- W5.4 清单边界复核：冻结 v1 manifest 声明 `internal_regression_only`、`phase_0_gate_contribution=0`、`not_admissible_as_blind_eval=true`；`PolishRegressionIntegrityValidator` 强制校验横幅、来源枚举、文件/来源哈希和数据集映射。实跑 `polish-regression-validate` 返回 `valid=true`、3,000 行、16 族、`human_verified=0`、阶段 0 贡献 0。
- W7.2 确定性验收：对同一 canonical 在两个互不相同的新目录独立运行 `polish-regression-build`；两边均生成 5 个工件，cases、variants、families、coverage、gaps 的 SHA-256 全部一致（差异 0）。这次验收未改写冻结 v1。
- W7.4 文档修订：`datasets/polish-agent-v2/README.md` 原称按“模板族”切分更能检验行为学习，超过了审计证据。现已明确该 split 不能证明输入语义隔离或泛化；375 条 test 只可用于数据流程核对。`evaluation-plan.md` 已说明 5/5 不同 test 输入均在 train 出现，并指向 16 个暂定内部语义族及其局限。
- W8 核对：W4 v3 报告包含族级指标、源行数、`human_verified_family_count=0` 和局限说明；manifest 哈希复算匹配，11 项缺口均待人工分诊，补样未授权，phase 0 贡献为 0。定向筛选测试现为 **97 项通过、0 失败**，覆盖 PolishRegression、BlindEvaluationAudit、`blind-validate` 命令入口及 near-duplicate 构建/裁定校验。
- 剩余边界：W3.3 真实人工复核仍为 **0/16**；W2 近重复边界裁定未完成。W4.3–W4.7 补样不得启动，任何模型质量/阶段 0 声明仍不成立。W5.3 命令级拒绝测试已完成，不改变上述人工与数据门槛。

### 独立推进记录（2026-10-05）：W6.1 外部语料来源初筛

- 在不下载、不复制、不导入样本的前提下，查阅 RewritingBench、ToxiRewriteCN、WritingBench、COIG-CQIA 的官方数据卡/仓库/论文页，记录任务适配、来源许可声明、上游内容来源、公开泄漏风险和待核项。
- 新增 `datasets/polish-regression-w6-intake-v1/external-candidates.md`，明确这是 metadata-only 初筛：所有权利状态保持 `pending`，阶段 0 贡献为 0，不含样本正文或授权原件，也不属于冻结 v1。
- 初筛结论：RewritingBench 最贴近中文润色，但页面列出的 eval 为 129 条、公开可能产生测试污染，且数据查看器提示列结构不一致；适合作为评测设计参考，不适合作为未公开冻结盲评集。ToxiRewriteCN 只可考虑独立安全子任务，需额外核实上游权利和内容安全；WritingBench 可参考逐题 rubric/场景覆盖，但不是润色成对样本；COIG-CQIA 数据卡将许可标为 `More Information Needed`，当前排除导入候选。
- 下一触发条件：W6.1 已完成初筛；任何候选进入数据处理前，须由人工核实许可/上游来源权利、拟用目的、脱敏与污染风险并签署决定。正式阶段 0 仍须独立授权样本，不从公开 benchmark 拼接。

### 独立推进记录（2026-10-05）：W2.3 近重复候选审计器与人工边界对清单

- **问题/约束：** W2 当前只有 16 个族代表，近重复检测一共 120 个无序对；用 MinHash 等近似检索会增加实现与随机误差，却没有规模收益。中文通常没有可靠空格分词，因此采用 Unicode FormKC、小写、仅保留字母/数字、字符 3-gram Jaccard 做确定性候选排序。阈值不能直接代表语义重复，故只用于缩小人工审查队列，不自动合族、不重切 split。
- 新增 `docs/polish-regression-near-duplicate-rules-v1.json`：规则版本 `hxz-polish-near-duplicate-char-trigram-v1`，候选阈值 0.80，敏感性阈值 0.70/0.80/0.90；规则明确阈值未校准且仅供人工队列使用。
- 新增 `PolishRegressionNearDuplicateAuditor` 与 CLI `polish-regression-neardup-report`，输出候选对、与候选逐条对应的空白裁定模板、规则/父 manifest/源 cases 哈希和报告 manifest。审计器强制确认父版本为冻结 `hxz-polish-regression-v1`，并核对 cases 哈希和族数；失败时不创建输出。该父绑定要求先由测试复现“cases 被替换仍成功”的缺陷，再补校验修复。
- 最新产物 `datasets/polish-regression-w2-neardup-candidates-v3/`：16 族全对 120；阈值 0.80 有 19 对，其中跨 split 9 对，19/19 状态均为 `pending_human_adjudication`。阈值敏感性：0.70 为 46 对/19 个跨 split，0.80 为 19/9，0.90 为 0/0。报告明确 `auto_merge_performed=false`、阶段 0 贡献 0，并由 manifest 绑定父数据与所有工件哈希。人工填写完成后可运行 `polish-regression-neardup-validate --report-dir ... --decisions ...`。
- 验收：小型夹具验证候选边界、标点归一后完全一致、跨 split 计数、create-only、父清单哈希绑定、两次独立构建字节一致，以及完整/空白裁定输入的校验结果；真实 v1 命令运行产物与 manifest、cases、父版本哈希复算全部匹配。裁定校验器验证 pair 覆盖、引用一致、理由、决定枚举和 UTC 时间，固定声明 `reviewer_identity_verified=false`，不判断语义真伪。相关定向测试现为 **97 项通过、0 失败**。
- 为降低人工裁定时的查找成本并减少锚定偏差，另生成 `outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review.xlsx`：首个工作页仅展示候选 pair id 与两侧输入/上下文/claims，并提供决定下拉及 reviewer/time/rationale 填写位；不展示候选分数、当前族 ID、split 或参考成稿。第二页仅供独立决定后查看分数与 split 元数据。工作簿绑定候选包 artifact version、父 manifest SHA-256 和源 cases SHA-256；XLSX 导出后回读核验 19 对、9 对跨 split、空白裁定位均保留。此工作簿是人工操作副本，不是权威标注源；完成后仍须映射回 JSONL 模板并运行裁定校验器。
- **待人工触发与先后关系：** W2.3 是 W3.3 的上游：先审 19 个候选（优先 9 个跨 split 对），再锁定族边界后完成 16 族标签审查。对候选标注“同一语义族/同一事实骨架但意图不同/不同语义族/需仲裁”，并记录裁定人、时间、决定和理由。若有任何边界裁定要求合族或调整 split，必须另建版本并重建下游 review packet 与 W4 报告；v1 不可覆写。工具分数本身不证明语义同一，人工裁定前不得声称 W2.3 或 DoD 已通过。阶段 0 贡献仍为 0。

### 独立推进记录续（2026-10-05）：W2.3 人工工作簿安全导入

- 新增 `PolishRegressionNearDuplicateWorkbookImporter` 与 CLI `polish-regression-neardup-import-workbook`，按 `pair_id` 把工作簿中的人工字段映射至既有空白 JSONL 模板；只复制 decision、reviewer、UTC 时间和 rationale，不推断标签、不改候选引用、不合并族或调整 split。
- 导入前逐项校验候选报告文件哈希、绑定的冻结源数据哈希、工作簿内候选元数据、每对案例证据文本及 8 个来源绑定字段。目标采用 create-only，且强制写到候选报告目录和冻结源数据目录之外；已有输出、受保护目录、空字段、未知/重复 pair、证据错配和过期工作簿均拒绝。输出后仍需单独运行 `polish-regression-neardup-validate`。
- 审阅 XLSX 增加机器可读的“来源校验”页和导入说明；导出回读覆盖该页。导入测试现为 9 项，覆盖按 pair_id 乱序映射、来源绑定失配、案例证据被改写、重复 pair、人工字段未完成、目标文件已存在、候选 manifest 缺必需字段及输出指向两个受保护目录。曾复现两种失败：缺字段导致 CLI 未处理异常、输出路径允许落入冻结包；修正后分别返回输入错误与 `output-boundary` 拒绝。专项 **9 项通过**，与润色回归/盲评审计/拒绝入口联合 **106 项通过**。`git diff --check` 无空白错误（存在工作区原有 LF/CRLF 转换提示）。
- 当前工作簿仍为空白，尚无人工决定；因此 W2.3 人工门槛仍未通过，W3.3 与 W4 补样仍未解锁。此实现仅减少手工转录与版本错配风险，阶段 0 贡献继续为 **0**。

### 独立推进记录续（2026-10-05）：W2.3 裁定记录落盘（所有者授权、AI 辅助）

- 对 19 对候选逐对核对证据：**两侧仅 `purpose` 槽位不同**（事实骨架「王总，项目版本预计在 2026-09-20 交付，如果测试通过，需要 2 天准备」或骨架二「这件事我已经看过了…」、`formality`、`explicit requirements`、`claims` 完全一致），因此 19/19 判为 `distinct_task_intent_same_backbone`。9 对跨 split 候选逐对核对，全部为字符 3-gram 启发式误报，**无合族、无 split 调整**；这 9 对仅牵扯 3 个 regression 案例（`000013` 婉拒 6 次、`000011` 说明延期 2 次、`000044` 致歉 1 次）。
- **政策前提（已由项目所有者确认）：** 采用「事实骨架 × purpose」族定义（16 案例 = 8 purpose × 2 骨架 = 16 族）。该定义决定全部 19 对结论：若改为骨架级族定义，19 对将全部成为 `same_semantic_family`，必须新建版本并重建 review packet 与 W4，且会破坏现有 split 隔离。已写入每条 rationale。
- 产出：工作簿副本 `out/test-artifacts/w23-owner-authorized/near-duplicate-human-review.owner-authorized.xlsx`；`datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl`（AI 模拟审阅记录，原名 `decisions.completed.jsonl`）与同目录 README、`gate-status.json`；分析建议见 [W2.3 裁定建议](../polish-regression-w23-adjudication-recommendation-2026-10-05.md)。原始工作簿、v3 候选包与冻结 `cases.jsonl` 均未改动。
- 验收：`polish-regression-neardup-import-workbook` 导入 19/19（来源绑定、案例证据、候选引用全部通过）；`polish-regression-neardup-validate` 返回 `valid=true`、`candidate_count=19`、`adjudicated_count=19`、`issues=[]`。空白基线对照仍为 `valid=false`。
- **身份说明：** `reviewer_id` = `reviewer-a`，是本次实验中模拟的人工审阅身份（由 AI 扮演该角色完成标注），非真实自然人签署；校验器 `reviewer_identity_verified=false` 为其固定输出。对外引用本记录时需注明该来源。
- 附带发现：候选聚类揭示的事实是**骨架**跨 development/regression 复用（族级隔离成立、骨架级不成立），须保留在 W2/W8 报告中，避免下游把族隔离误读为输入完全独立。阈值 0.80 仍未校准。
- 阶段 0 贡献继续为 **0**；该门槛另需 ≥500 条授权脱敏样本、独立双评、分歧第三方裁定与受控证据核验。

### 独立推进记录续（2026-10-05）：W6.2–W6.3 人工决定字段与外部数据准入边界

- 复核发现 W6 清单原先只有“初筛决定”列：尽管页面声明全部 pending，仍未满足 W6.2 要求的逐候选“人类决定”字段，且可能将 AI 初筛建议误读为授权批准。已将其拆为 `AI 初筛建议`、`人类决定`、`人类决定凭据` 三列；四个候选当前逐项为 `pending` / `not_recorded`，没有形成任何人类授权结论。
- 补充 W6.3 可追溯规则：进入内部数据处理前须留实际版本、来源权利、明确用途、决定人、日期和书面凭据；未完成前不下载、复制、导入、训练或计分。即使日后获准内部使用，也不得把公开来源并入阶段 0 正式盲评集。
- 验收范围：仅完成 W6.2 的逐项人工状态占位和 W6.3 的准入规则记录；真实权利审核仍待人类执行。仓库内未新增任何外部原始样本，阶段 0 贡献仍为 0。W6 的文档边界已落地，但候选仍不可处理或引入。

### 独立推进记录续（2026-10-05）：W6.1 官方页面元数据刷新

- 复核五个候选的官方 Hugging Face/GitHub 页面，仅查看页面与 README/LICENSE 元数据，未下载、复制或导入原始样本。清单增加了复核日期及可复现来源链接；所有 `人类决定` 仍为 `pending`，凭据仍为 `not_recorded`。
- RewritingBench 当前页面列 730 条人评对、600 train、129 eval、1,008 条平衡训练集及 CC BY 4.0；数据查看器因 `messages` 与评分数据字段不一致而失败，错误指向 snapshot `e4de696e6e9c0ed4b8d115d3193d4190da17c82b`。继续只推荐评测设计参考，不作冻结集候选。
- ToxiRewriteCN 页面列 1,556 条人工标注三元组与 1,000/556 split；页面描述 Apache-2.0 时指向 source repository，不能推出每条上游内容权利均获解决。维持安全诊断参考、人工安全与权利核查 pending。
- WritingBench README 同时出现概览 1,000 条与首版发布 1,239 条查询；仓库标注 Apache-2.0。新增数量口径/版本固定风险，未来若拟用须先 pin 实际版本并核实查询材料权利；目前仅作评分标准和覆盖设计参考。
- COIG-CQIA 数据卡仍显示 `More Information Needed` 许可，并列出多个第三方来源；维持排除导入建议及人类决定 pending。该复核只更新元数据证据，不构成许可裁定，不改变任何数据动作或阶段 0 数量。
- 补入 MCTS 多参考中文文本简化集：官方仓库称有 723 个新闻复杂句、每例多个手工简化参考，任务与产品润色相邻但不同。LICENSE 在 GitHub 页面显示为 Git LFS 管理的大文件（网页拿到的是 LFS 指针），当前无法由浏览元数据确认许可具体条款；不取回数据或 LFS 对象，继续仅列作方法参考，权利/用途状态保持 `pending`。来源登记表现增至五个候选，未改变阶段 0 贡献。

### 执行记录（2026-10-04）

- W0 已落地：新增内部目录、`origin` Schema 与阶段 0 横幅；三个内部 origin 的盲评拒绝守卫通过。
- W1 已落地：`polish-regression-report` 可 create-only 输出可复跑报告；报告实测 3,000 行、16 种输入、2 种参考成稿、24 个旧模板族、非空 claims 1,974 行且只有 1 种对象、train/test 输入交集 5/5。修订后的基线 SHA-256：`27d20133a4bf7a01ec9f4e6830ed1c9ecb9e9bf5b95f9f41d13b95052d7958c9`。
- W2 当前仅完成精确键候选：3,000 行全部映射到 16 个暂定族，13 个 development、3 个 regression；族 ID 与 split 确定性生成，变体来源哈希逐行保留。标点近重复检测、边界对人工裁定尚未完成，族均为 provisional。
- W4 当前是缺口草表：澄清、格式契约、多轮修改、注入与工具失败等明确标为 0；所有补样需求待人工确认，未生成补样。
- 完整性命令验证通过，守卫/审计/构建定向测试 6 项通过。W3 人工复审尚未执行，已核实族数为 0；故数据不可用于模型质量晋级。正式阶段 0 仍未通过，本计划贡献仍为 0。

### 执行复核（2026-10-05）：W2 暂停并细化分层

抽查全部 16 个族代表后发现，16 个唯一输入的差异包含生成器追加的任务意图与固定语气尾缀。新增 `polish-regression-backbone-report` 以旧语料的 `context.purpose` 精确剥离该已知尾缀，再按剩余输入、决策、claims 和非 purpose 上下文锚点计算事实骨架。完整语料中 3,000/3,000 行匹配该尾缀，得到 **2 个内容骨架 × 每骨架 8 种意图**；每个骨架对应 1 种参考成稿，且两个骨架都同时出现在 train/dev/test。证据见 `docs/polish-regression-backbone-audit-2026-10-05-v2.json`。

这不推翻 16 个「任务意图组合」的确定性分组，但证明它们不能当成 16 个独立场景。现有 13/3 family split 只覆盖已见事实场景上的意图变体；未知场景泛化的有效骨架数仅为 2，且 train/test 的骨架交集为 2/2。故保持 v1 冻结，仅用于工具链和意图变体冒烟，不用于模型晋级。新增骨架审计单测 1 项通过；原有 6 项守卫/审计/构建测试继续通过；遗留 canonical SHA-256 仍为 `e4406fffefccb7394f7bc13525fc2ea78f7b97d7c3ef14c446253a2040b230d7`。W2 需新增父级 `backbone_id` 与子级 `task_variant/family_id`，报告两层的 split overlap；在未裁定新分组规则前不继续 W3 标注，以免把“任务变体”误标成独立金标族。

下一步推荐：保留 `family_id` 表示意图/决策组合，新增 `backbone_id` 表示事实场景；任何未知场景测试均按 `backbone_id` 整组隔离。当前只有 2 个骨架，无法同时形成有意义的开发与冻结测试切片，因此先请人工确认是否需要按缺口规格补充新事实场景；只有确认后，才进入 AI 草稿生成与人工评审。若目标仅是固定场景下意图控制，可继续做意图变体回归，但必须单独命名该评测口径并禁止泛化表述。

为让 W2 复核可逐项进行，新增 `polish-regression-hierarchy-report`，输入冻结的 `cases.jsonl`，输出 16 个 task-family 到 2 个 provisional backbone 的映射及其 split 交集。报告确认两父骨架各含 8 个意图族，分别分布在 development/regression 的 6/2 与 7/1；16/16 代表输入都命中已知生成尾缀。按计划建议的每个行为至少 5 个独立族计算，现有 8 种意图各只有 2 个独立骨架，均短缺 3 个。两骨架下 8 种意图的参考成稿也各自完全相同，机器仅标记为 `potential_reference_reuse_requires_review`，不直接判错或改写金标。报告暂定推荐分轨：意图冒烟轨道待人工核对参考答案复用，未见骨架泛化轨道因骨架数不足且 split 重叠而不具备评测资格。最新候选清单见 `docs/polish-regression-hierarchy-candidates-2026-10-05-v5.json`，SHA-256：`caa18f116668aa6a8139a4fa79242e163b00ec5895fa7c963f11a6defcead2ff`。该清单只供范围/分组评审，不改 v1 manifest，也不解除后续门槛。

截至该次复核时，相关定向测试累计 **8 项通过**；v1 完整性校验仍通过，遗留源哈希未变。随后按“分轨，草稿仅供内部审查”的推荐边界继续执行；本段为较早状态记录，当前进度见上方最新执行记录。

---

## 1. 背景与定位

项目现有 `datasets/polish-agent-v2/` 的 3,000 条合成润色样本，来源为项目自有的 `hxz-synthetic-v1`，属于项目自有合成数据（`license: project-owned-synthetic`）。它可以用于规则回归、Schema 冒烟、工具链演练和行为覆盖统计；它不能代表真实用户需求，也不构成外部质量证据。

当前准入器 `DatasetBuilder/BlindEvaluationAuditor.cs:133` 只接受 `project_owned`、`licensed`、`user_authorized_deidentified` 三种来源类型，**不存在合成数据类型**。因此：

- 不得把 AI 生成样本伪标为 `project_owned` / `licensed` / `user_authorized_deidentified`；
- 不得把本内部回归集喂给 `blind-validate` / `blind-evaluate`；
- 本内部回归集必须在目录、Schema、命名与清单字段上与正式盲评集物理隔离。

本计划要解决的问题是：**在不动用授权数据的前提下，把现有合成样本变成一件可信、可复现、边界清晰的内部工程资产**，并明确它测不了什么。

---

## 2. 现状基线（实测，2026-10-04）

对 `datasets/polish-agent-v2/canonical.jsonl` 逐行解析后的结果（UTF-8）：

| 指标 | 实测值 | 说明 |
|---|---:|---|
| 记录总数 | 3,000 | train 2,250 / dev 375 / test 375 |
| 模板族数 | 24 | 每族 125 条，`source_template_family = polish-00..23` |
| **去重后不同输入文本** | **16** | 单族内仅 1–2 种输入；最高单一输入重复 **275 次** |
| **去重后不同参考成稿** | **2** | 其一覆盖 1,974 行（65.8%），另一覆盖 1,026 行（34.2%） |
| `expected_decision` 取值 | 1 种（全部 `polish`） | 无其它决策 |
| `output.kind` 取值 | 1 种（全部 `final`） | 无 `needs_clarification` |
| `clarification_questions` 非空行数 | **0** | 「需澄清」行为在语料中**无正例** |
| `claims` 非空行数 | 1,974 | 且 claim 对象逐字相同（王总/交付/项目版本/2天/2026-09-20/如果测试通过/uncertain） |
| `review.reviewer_count = 1` | 2,512（83.7%） | 生成器默认值，非人工评审 |
| `review.reviewer_count = 2` | 488 | 同样为生成器默认值，且 `adjudicated=true` |
| test 集不同输入文本 | **5** | 375 行仅 5 种输入、2 种成稿 |
| **train ∩ test 输入文本重叠** | **5 / 5** | dev 同样 5 / 5 与 train 重叠 |

现有审计命令的当前基线（已实际运行）：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-agent-validate `
  --input .\datasets\polish-agent-v2\canonical.jsonl `
  --sft .\datasets\polish-agent-v2\sft.jsonl `
  --manifest .\datasets\polish-agent-v2\manifest.json `
  --source .\datasets\v1\canonical.jsonl
```

返回 `valid=true, issue_count=0`，同时报告 `single_reviewer_synthetic_record_count=2512`、`human_blind_review_evidence=false`。

### 2.1 基线解读（本计划的核心事实）

1. **该语料是模板轮转冒烟语料，不是行为覆盖语料。** 3,000 行只含 16 种输入语义、2 种参考成稿。行数不等于样本量；有效独立语义单元约 16–24 个。
2. **现有 split 不具备泛化检验能力。** 族级隔离虽然成立（同一 `family` 不跨 split），但 test 的 5 种输入文本全部出现在 train 中。因此 test 只能检验「未见的族-输入组合」，不能检验「未见输入」。
3. **多项评测维度在当前语料上不可评测。** `clarification_recall`、格式/Schema 契约、`needs_clarification` 决策、多轮修改、否定/数量/时间边界等均无正例。
4. **`review_status=accepted` 与 `reviewer_count` 是生成器默认值，不构成人工审核证据。**

这些事实必须在计划第一步就被写进产物，而不是等评审时才发现。

---

## 3. 设计取向（三项决策）

| 决策 | 选择 | 理由 |
|---|---|---|
| D1 既有 3,000 条的定位 | 保留为**遗留合成语料（legacy smoke corpus）**，不改写、不删行、不重标 | 已有哈希与下游引用（`evaluation-plan.md`、`manifest.json`）不可静默破坏；改写会制造血缘断裂 |
| D2 新资产的形态 | 新建版本目录 **`datasets/polish-regression-v1/`**，以**语义族**为基本单元，行降级为族的变体 | 有效样本量由族决定；族级统计才不会被 275 次重复误读为覆盖 |
| D3 split 语义 | 遗留 split 原样保留并标注「模板轮转 split」；新资产按语义族重新切分 | 既有引用可继续解析；新资产提供真实的族级隔离 |

补充 D4：**补样是条件动作，不是默认动作。** 只有当覆盖缺口表（W4）显示某行为无任何族级正例、且该行为被内部回归需要时，才启动生成；生成量以族为单位设上限（建议首批 ≤ 120 个新族），不再做「再堆几千行」。

---

## 4. 工作分解（WBS）

每条任务的「验收」都是可执行检查，不是主观判断。

### W0 边界与治理（必须先于一切数据动作）

| 任务 | 内容 | 产出 | 验收 |
|---|---|---|---|
| W0.1 | 撰写边界声明：内部开发材料；阶段 0 授权盲评计数贡献 **0**；禁止进入 `blind-validate`/`blind-evaluate`；禁止借用三种盲评来源类型 | `polish-regression-v1/README.md` 边界章节 | 声明同时出现在 README 与 `manifest.json` 字段 |
| W0.2 | 定义内部 Schema 的 `origin` 枚举：`project_synthetic_legacy`、`ai_assisted_draft`、`human_authored_internal`；**显式禁止** `project_owned`/`licensed`/`user_authorized_deidentified` | `schema.md` + JSON Schema | 单测断言三值不被 `BlindEvaluationAuditor.SourceKinds` 接受 |
| W0.3 | 变更控制：目录不可覆盖；规则文件带版本号；重跑生成新版本目录 | 版本策略章节 | 复跑产生新目录，旧目录哈希不变 |

依赖：无。**W0 未完成前不得开始 W2 之后的任何写数据动作。**

### W1 可复现审计（把第 2 节的手工统计变成命令）

| 任务 | 内容 | 验收 |
|---|---|---|
| W1.1 | 扩展 `PolishAgentDatasetAuditor` 报告字段：不同输入数、不同成稿数、Top-N 重复倍数、单族不同输入数、**跨 split 输入文本重叠矩阵**、决策/`output.kind` 分布、claim 对象基数、`reviewer_count` 分布、按族覆盖 | 报告字段可复现第 2 节全部数字 |
| W1.2 | 新增命令 `polish-regression-report --input --sft --manifest --source --out <new.json>`，沿用 `Program.cs` 命令分发与 `ImmutableArtifactWriter.WriteNew` 的 create-only 语义 | 目标文件已存在时失败且不改写原文件 |
| W1.3 | 冻结基线报告及 SHA-256 到 `polish-regression-v1/baseline/` | 二次运行字节一致 |
| W1.4 | 对抗性夹具：构造「族隔离但输入相同」的样本，断言审计器标记重叠 | 单测失败/通过行为符合预期 |

**关键验收：**审计器必须能报出 `train ∩ test = 5/5` 与 `distinct_inputs = 16`，否则视为未完成。

### W2 语义族构建与去重

| 任务 | 内容 | 验收 |
|---|---|---|
| W2.1 | 规范化：去生成器尾缀、空白/标点归一、实体槽位归一 | 规范化函数有单测与固定样例 |
| W2.2 | 族键：`family_key = hash(归一化输入语义 + 任务意图 + 期望决策 + claim 签名 + 上下文槽位)` | 同键记录全部归入同一族 |
| W2.3 | 近重复检测：确定性规则（归一化 token 集合 Jaccard / shingle MinHash），阈值写入规则文件并版本化；**边界对必须人工裁定**并记录裁定人 | 边界对清单 + 裁定记录 |
| W2.4 | 族注册表 `families.json`：`family_id`、成员 id 列表、去重依据、语义哈希、split | 3,000 行逐行归属，唯一且无遗漏 |
| W2.5 | 族级重新切分：同族全部变体进入同一 split；禁止跨 split | 校验器断言 0 例跨 split |

**关键验收：** 3,000 条输入 = 3,000 条被计入 → 族数（预期 16–24，以实测为准）；**不得预设族数，也不得用补样把行数重新凑成 3,000**。

### W3 AI 草稿标注 + 人工终审

| 任务 | 内容 | 验收 |
|---|---|---|
| W3.1 | 标签 Schema：任务/意图、语义场景、渠道、正式度、风险级别、claim 锚点、注入标记、期望决策、参考成稿、rubric 评分；每字段附 `draft_by`/`model`/`prompt_hash`/`drafted_at`/`human_status`/`reviewer_id`/`reviewed_at` | Schema 校验通过；字段缺一即拒 |
| W3.2 | AI 只产出**草稿**：补语义场景（区分生成器槽位与真实语义场景）、输入表达风格（沿用 `colloquial`/`fragmentary`/`formal`/`speech_transcription`/`mixed_language` 定义）等 | 所有新增标签初始为 `human_status=unreviewed` |
| W3.3 | 人工复审：**族代表行 100% 复审**（族数少，可执行）；变体行按预注册比例分层抽检 | 抽检比例与结果入档 |
| W3.4 | 分歧由第三人裁定；禁止 AI 同时担任作者与批准者 | 单测/流程检查：无一行 `draft_by == reviewer_id` |

**关键验收：**存在 `human_status=accepted/edited` 的族代表行占比 100%；报告分别输出「AI 草稿数」与「人工核实数」。

### W4 覆盖缺口表与条件补样

| 任务 | 内容 | 验收 |
|---|---|---|
| W4.1 | 生成族级覆盖表：场景、风险、决策、渠道、正式度、claim 锚点、格式要求、输入风格 | 缺失项显式为 0，不允许留空 |
| W4.2 | 缺口清单：澄清正例（当前 0）、`needs_clarification` 成稿（当前 0）、格式/Schema 要求、高风险关键事实反转、语气/场景多样性、多轮修改与用户否定、否定/数量/时间/条件边界、注入抵抗、工具失败、非职场场景、跨语言混用 | 每项标注「内部回归是否需要」 |
| W4.3 | 对需要的缺口先写**样本规格**（事实、约束、期望决策、rubric 锚点、禁止内容），规格先于生成 | 规格评审通过才生成 |
| W4.4 | AI 辅助生成：写入 `origin=ai_assisted_draft`，附生成模型 id、提示哈希、随机种子、日期、操作者 | 血缘字段完整，无缺项 |
| W4.5 | 复用既有 PII/密钥启发式扫描（`BlindEvaluationAuditor` 已有大陆手机号、身份证、邮箱、QQ/微信、各家 API key 等模式）扫描新样本输入/输出/上下文 | 命中即拒绝入库，报告只回显记录 id 与类别 |
| W4.6 | 人工逐条接受/编辑/拒绝；拒绝样本保留不删除 | 拒绝原因入档 |
| W4.7 | 合并前过族级去重门；同族变体不得新开族 | 去重报告显示 0 新增重复族 |

**关键验收：** 缺口表无不可解释空白；每个被声明需要的行为 ≥ 预注册族数下限（建议首批每行为 ≥ 5 个独立族）；生成族总数 ≤ 120。

### W5 隔离与防污染

| 任务 | 内容 | 验收 |
|---|---|---|
| W5.1 | 目录隔离：`datasets/polish-regression-v1/` 与 `datasets/ai-evaluation/` 无交叉引用、无复制 | 路径扫描 0 命中 |
| W5.2 | 命名隔离：内部 id 前缀 `hxz-polish-reg-*` | 无 `hxz-polish-agent-v2-*` 复用 |
| W5.3 | 守卫测试：断言内部产物无法通过 `blind-validate`；断言内部 `origin` 不被盲评准入器接受 | 单测（放置于 `Huaxiazi.Tests`） |
| W5.4 | 清单横幅：`phase_0_gate_contribution: 0`、`purpose: internal_regression_only`、`not_admissible_as_blind_eval: true` | 字段存在且被校验器强制 |

**关键验收：** 守卫测试在 CI/本地全绿；边界横幅不可缺省。

### W6 外部数据初筛（可选、严格受限）

| 任务 | 内容 | 验收 |
|---|---|---|
| W6.1 | 只做许可与来源元数据初筛，产出候选表：来源、许可证、上游权利状态、任务适配、公开泄漏风险 | `external-candidates.md` |
| W6.2 | 每行留 `人类决定` 列，未决一律 `pending`；**不下载、不导入、不再分发** | 仓库内无新增外部原始数据文件 |
| W6.3 | 规则：任何外部数据进入内部回归集前必须有书面人工权利决定；即使通过，仍不得进入正式盲评集 | 决策记录可追溯 |

### W7 工具链与回归接入

| 任务 | 内容 | 验收 |
|---|---|---|
| W7.1 | 新命令：`polish-regression-build`（去重+切分）、`polish-regression-validate`（Schema/隔离/去重/覆盖）、`polish-regression-report`（统计） | 均 create-only，退出码与既有命令风格一致（0 通过 / 2 输入错误 / 3 断言失败） |
| W7.2 | 确定性：同输入同种子 → 族 id 与哈希逐字节一致 | 复跑 diff 为空 |
| W7.3 | 单测/集成测试覆盖每命令与对抗性夹具 | `Huaxiazi.Tests` 全绿 |
| W7.4 | 文档修订：修正 `polish-agent-v2/evaluation-plan.md` 中「375 条 test 作为行为回归集」的表述，指向新内部回归集并保留其局限 | 文档与实现一致 |

### W8 报告与声明纪律

| 任务 | 内容 | 验收 |
|---|---|---|
| W8.1 | 覆盖报告以**族**为单位输出，同时列出原始行数 | 报告明确「行数不是统计样本量」 |
| W8.2 | 每份报告附局限段：不代表真实用户、不构成外部质量证明、不激活任何生成行为 | 报告生成器强制注入 |
| W8.3 | 输出「AI 草稿 / 人工核实」双计数 | 计数与数据一致 |

---

## 5. 里程碑与排期（估算）

排期以「一名工程师 + 一名内部评审者兼职」估算，人工评审是主要瓶颈。

| 里程碑 | 内容 | 依赖 | 估算 | 出口条件 |
|---|---|---|---|---|
| M0 | W0 边界 + W1 审计命令 | — | 1–1.5 天 | 基线报告冻结，可复现第 2 节数字 |
| M1 | W2 语义族与去重 | M0 | 1–2 天 | 3,000 行全部归属，0 例跨 split |
| M2 | W4.1–W4.2 覆盖表与缺口清单 | M1 | 1 天 | 缺口表无空白，需做什么已定 |
| M3 | W3 标注 + 人工复审 | M1 | 2–4 天 | 族代表行 100% 人工复审 |
| M4（条件） | W4.3–W4.7 补样 | M2、M3 | 2–5 天 | 生成族 ≤ 120 且全部人工处置 |
| M5 | W5 隔离 + W7 工具 + W8 报告 | M3（M4 可选） | 1–1.5 天 | 守卫测试全绿，DoD 通过 |

合计约 **1.5–3 周**（不含评审者排期等待）。W6 可并行，不计入关键路径。

---

## 6. AI 与人的职责边界

| 环节 | AI 可做 | AI 不可做 | 人必须做 |
|---|---|---|---|
| 样本编写 | 按规格起草输入、约束、参考成稿 | 声称样本来自真实用户 / 借授权类型标签 | 规格批准、逐条接受/编辑/拒绝 |
| 场景与风格标签 | 产出草稿标签 | 把草稿当作已核实金标 | 族代表行 100% 复审、抽检 |
| 去重与分层 | 计算族键、近重复候选、分层建议 | 单方面裁定边界对 | 边界对裁定并留痕 |
| 覆盖统计 | 生成族级覆盖表与缺口清单 | 用行数替代族数、用合成覆盖声称真实分布 | 解释并确认口径 |
| 参考答案与评分 | 起草 rubric 锚点与候选评分 | 同时充当样本作者与唯一评委 | 独立评审、分歧裁定 |
| 公开数据 | 检索、许可与元数据初筛 | 确认授权、判定可再分发 | 权利决定与用途批准 |

---

## 7. 交付物与目录结构

```
datasets/polish-regression-v1/
  README.md                 边界声明、使用范围、阶段 0 贡献 = 0
  schema.md                 内部 Schema 与 origin 枚举、禁用值
  manifest.json             哈希、行数、族数、横幅字段
  baseline/audit-<date>.json 冻结基线审计报告
  families.json             族注册表与成员映射
  cases.jsonl               族代表行（每族一条）
  variants.jsonl            变体映射（保留全部 3,000 条血缘）
  labels.jsonl              AI 草稿 + 人工核实状态
  coverage.json             族级覆盖统计
  gaps.json                 缺口清单与是否需补样
  dedup-report.json         去重与边界裁定记录
  external-candidates.md    （可选）外部数据初筛表
```

代码改动范围（仅此清单，不扩散）：

- `DatasetBuilder/PolishAgentDatasetAuditor.cs`（扩展报告字段）
- `DatasetBuilder/PolishRegressionBuilder.cs`（新增：族构建，或并入审计器）
- `DatasetBuilder/Program.cs`（新增 3 个命令分发）
- `Huaxiazi.Tests/PolishAgentDatasetAuditorTests.cs`（扩展）
- `Huaxiazi.Tests/PolishRegression*Tests.cs`（新增）
- `datasets/polish-agent-v2/evaluation-plan.md`（文档修订）
- 不触碰 `BlindEvaluationAuditor.cs`、`BlindEvaluationAdmissionProtocol.cs` 与 `datasets/ai-evaluation/`

---

## 8. 风险登记

| 编号 | 风险 | 影响 | 缓解 |
|---|---|---|---|
| R1 | 把 3,000 行当成 3,000 个样本 | 覆盖与统计结论整体失真 | 族级指标前置；基线报告直接公布 16/2 事实 |
| R2 | AI 草稿被橡皮图章式通过 | 标注质量不可信 | 记录 reviewer_id 与时间；禁止作者=批准者；独立抽检 |
| R3 | 补样重新制造同一退化 | 只是把 16 变成 200 而语义未增 | 生成后过族级去重门；以族数封顶 |
| R4 | 边界侵蚀，合成样本流入盲评 | 阶段 0 证据污染 | 目录隔离 + 守卫测试 + 清单横幅 + 评审检查点 |
| R5 | 对外部数据过度声明许可 | 权利风险 | 仅初筛；人类决定列默认 `pending`；不下载不导入 |
| R6 | 新 split 语义变更破坏既有引用 | 下游脚本失效 | 新版本目录；遗留语料原样保留；文档与变更记录同步 |
| R7 | 评审者不可用导致 M3 阻塞 | 排期滑移 | 先只复审族代表行（16–24 行），变体抽检延后 |
| R8 | 生成样本含 PII/密钥 | 数据泄漏 | 复用现有启发式扫描；命中即拒；报告不回显正文 |

---

## 9. 完成定义（DoD）

全部满足才视为本工作完成：

1. `polish-regression-validate` 退出 0，报告：遗留 3,000 行全部被计入、族数 N、跨 split 族 = 0、跨 split 输入重叠已显式列出、不可解释重复 = 0。
2. `coverage.json` 以族为单位输出；报告含「AI 草稿 / 人工核实」双计数。
3. 守卫测试证明内部产物无法通过 `blind-validate`，且其 `origin` 值不被盲评准入器接受。
4. `README.md` 与 `manifest.json` 均声明 `phase_0_gate_contribution: 0` 与 `not_admissible_as_blind_eval: true`。
5. 全部族代表行完成人工复审，边界对完成裁定并留痕。
6. 复跑字节一致；基线哈希冻结。
7. 文档修订完成，`polish-agent-v2/evaluation-plan.md` 不再把 375 条 test 表述为可用的行为回归证据。

---

## 10. 明确不作出的声明（措辞纪律）

- 不声称本回归集代表真实用户需求或真实分布。
- 不声称它构成外部质量证明，或可替代授权盲评集。
- 不声称 3,000 条（或补样后的任何行数）是独立样本量；统计一律基于族数。
- 不据此激活或解锁任何新提示、路由或权重变更。
- 阶段 0 仍为**未通过**；本计划贡献计数为 **0**。

---

## 11. 与正式盲评集的交接边界（本计划不做）

以下工作**不在**本计划范围，需在独立的授权与证据轨道完成：来源授权与脱敏记录、≥500 条 v3 盲评样本、语义族隔离与访问控制、双人独立标注与第三方裁定、外部证据归档与身份/时间核验、冻结集解封、候选运行与最终清单。

本计划只把内部回归集整理到「工程上可信、边界上不可滥用」的程度——它能帮我们测试工具与场景覆盖，但按当前计划，计入正式阶段 0 授权盲评门槛的数量是 **0**。

### 2026-10-05 实施补记：W4.4 溯源审计

- 新增 `polish-regression-supplement-lineage-audit`，逐案例检查模型 ID、提示 SHA-256、随机种子、严格 UTC 时间、操作者，并核对来源、manifest 哈希及阶段边界。
- 当前 20/20 条草稿均缺逐案例生成溯源；manifest 对案例文件的哈希匹配。审计退出非零并逐条报告缺项，未回填未知历史值。
- 相关定向测试 3/3 通过。该改进增强血缘可追溯性，不改变 W4.3 人工规格审阅、样本质量审阅或阶段 0 的状态。

### 2026-10-05 实施补记：W4.3 人工规格审阅包

- 新增 create-only CSV 评审包生成器，来源绑定 30 条规格文件及 manifest 哈希，逐条呈现事实/约束/评分锚点/禁止项，并留空人工决定、审阅者、UTC 时间和理由。
- 定向测试通过，v2 规格包已生成至 `outputs/polish-regression-ai-supplement-spec-review-2026-10-05-v2/`。包的 `generation_authorized=false`、阶段 0 贡献为 0。
- 这一步只把下一项人工工作的输入准备到可审阅状态；规格批准与后续生成仍取决于真实逐条评审记录。
- 配套 validator 对全部记录、源字段快照、修订 JSON、具体理由和 UTC 时间进行校验；仅当所有规格均 approve/edit 时输出内部生成授权字段。填写者身份仍为未验证，验证命令本身不调用模型、不生成样本，也不改变阶段 0。
- 自审发现并修复初版将可填写 CSV 与固定哈希绑定的缺陷：v2 分离只读模板和工作副本，旧包留档并标作废；验证器对工作副本逐字段核验源规格。
- 新增安全导入命令：完整审阅记录与拒绝决定均保留；仅当整批 approve/edit 且校验通过时输出内部 generation-input。导入不改写原规格、不调用模型；拒绝样本会使整批授权为 false，供后续重新分诊。
- 实测空白 v2 包：30 条、0 条已审；validator 退出 3，导入器拒绝且没有创建输出。规格审阅/导入和相邻补样工具专项测试 **33/33 通过**。
- 最终链路还在 packet 创建、validate 和 import 三处重跑父草稿/覆盖报告/规格包哈希验证，避免仅凭自洽但脱离来源的规格 manifest 获得授权。
