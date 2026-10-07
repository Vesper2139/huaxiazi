# AI 定制化评测集与阶段 0 门禁

本目录用于保存 AI 定制化能力的评测协议、数据来源记录和冻结运行清单。它不承载自动收集的用户内容。当前 `../polish-agent-v2` 是项目自有的合成行为数据，只能用于规则回归与流程冒烟检查，不得充当外部盲评或真实质量证明。

公开中文改写数据源的近期调查和逐项采纳边界见 [外部评测数据来源调查](../../docs/external-evaluation-data-research-2026-10-02.md)。公开数据即使声明开放许可，也不能仅凭许可标签作为本项目冻结盲测；须独立审核上游权利、任务适配、公开泄漏、语义族和人工标注协议。后续将 RewritingBench eval 文件临时下载到 ignored 审计目录核对哈希、结构和隐私启发式，没有将其样本导入或再分发。

## 数据准入

每条盲评记录都必须能追溯到项目自有、明确许可的数据，或用户主动脱敏且明确授权的样本。记录 `source_kind`、`license_or_consent_ref`、去标识化方式和语义族标识；不得写入姓名、联系方式、账号、企业机密等直接身份信息。无授权证明、未脱敏或无法确认权利边界的数据不得进入评测集。

盲评集至少 500 条，覆盖润色和提示词优化；至少覆盖事实/约束保留、语气匹配、需澄清判断、格式/Schema、高风险和常规任务。按语义族整体切分，禁止同一语义族跨开发集与冻结集。冻结测试集必须在候选模型、提示词与采样参数锁定后才解封评分。

每条记录还需双人标注输入的主导表达风格，使用 `colloquial`（完整口语）、`fragmentary`（碎片/省略表达）、`formal`（正式书面）、`speech_transcription`（保留语音转写特征）或 `mixed_language`（有实质语义的跨语言混用）。多种特征并存时按主要承担语义的表达方式择一；不能仅因产品名、缩写或借词标为 mixed-language。每个 split 至少包含三种受支持风格，报告输出各风格计数以便分组分析。此最低门槛只防止测试集退化为单一表达方式，不表示真实用户分布，也不能代替每种风格的充分统计样本量。加入该字段使样本格式升级为 schema v3；旧样本不得自动填默认标签后视为有效，须依据原文标注、复核并重新冻结。

schema v3 的 `human_review.independent_annotations` 保存两名评审各自提交的完整金标标签（任务、风格、约束、风险、决策、参考成稿和标注）；两份一致时 `review_status=accepted` 且最终 gold 必须匹配，存在分歧时必须 `review_status=adjudicated`，记录不同第三人的 `adjudication` 标签，并要求最终 gold 与裁定完全一致。仅有评审 ID 或 accepted/adjudicated 状态不足以通过审计。产品提示快照和匿名 pairwise 包不得带出这些金标评审证据。

Schema/准入协议 **v3.1** 在保留单轮记录 wire format 的基础上，增加可选配对字段 `conversation_id` 与 `turns`。多轮仅用于 `polish`，至少两轮；各轮 `turn_index` 从 1 连续递增，顶层 `input` 必须等于最后一轮 `user_input`。JSON Schema 声明局部字段/成对依赖/任务限制，准入审计器执行跨轮连续性、末轮匹配、PII 扫描与每轮训练/开发重叠检查。其余 500 条、覆盖比例与双评/裁定规则不变；协议 SHA-256 明确绑定多轮语义。

该 JSON 结构只能核验已提交标签之间的一致性及其与最终 gold 的关系，不能凭自身证明两位评审实际独立作答、身份真实或标签未在提交前被改写。正式准入还需由数据管理员保留独立提交的原始评审表/裁定记录、提交时间及可追溯身份确认，并将其纳入受控、不可覆盖的证据归档；没有这些外部证据时，不得把字段完整视为人工双评已核验。

为防止“有该标签但只有一条样本”的名义覆盖，当前准入审计器采用以下可复现比例下限（每个维度向上取整，且最少 1 条）：全量 `polish` 与 `prompt_optimize` 各 ≥20%；`development` 与 `frozen_test` 各 ≥20%；全量高风险 ≥10%、低/中风险常规样本 ≥50%、明确需澄清 ≥5%、有明确格式要求 ≥5%、包含事实或约束锚点 ≥50%。同一组任务、风险、常规、澄清、格式和事实/约束锚点配额还必须分别在 `development`、`frozen_test` 内独立满足，避免总体覆盖由开发集贡献而冻结集缺失关键场景。以 500 条总集为例，全量至少为任务 100/100、两个 split 各 100、高风险 50、常规 250、澄清 25、格式 25、锚点 250；若 split 为 development 400 条、frozen_test 100 条，则各 split 内任务至少 80/80 和 20/20，高风险至少 40 和 10，常规至少 200 和 50，澄清及格式至少 20 和 5，锚点至少 200 和 50。各维度可以重叠。这些是阶段 0 的项目预注册评测协议默认值，不声称是由当前用户生产流量估计出的真实分布；数据准入前应预注册比例。若后续要调整，应同步更新协议与审计实现、记录规则版本并重新冻结数据，不能在看到模型结果后改配额。`blind-validate` 的 JSON 报告同时输出总量和两个 split 的实际计数，便于复核配额是否满足。

## 文件约定

- `blind-eval.schema.json`：单条盲评样本的格式约束。
- `annotation-handbook-v1.0.md`：初版 v3 金标与候选评分口径，保留为历史草案。
- `annotation-handbook-v1.1.md`：历史候选评分/语义族级 pairwise 口径，保留不可覆盖。
- `annotation-handbook-v1.2.md`：历史评分口径，保留不可覆盖。`annotation-handbook-v1.3.md`：当前候选评分口径，增加 v3.1 多轮润色的逐轮评审规则；评分锚点与配额不变。冻结运行必须记录准确版本及 SHA-256。它仍是待真实样本校准的作业标准，不表示已有真实评审数据。
- `dataset-freeze-policy-v1.0.md`：历史规程。`dataset-freeze-policy-v1.1.md`：当前 v3.1 冻结操作规程，定义去重、family 切分、冻结材料、受控保管和变更流程。
- 当前审计器只检查精确输入重复、family/split 字段隔离和训练集交叉泄漏；语义族判断和真实保管控制仍需人工及受控系统完成。
- `source-register.schema.json` 与 `source-register.template.json`：来源授权登记结构与空白模板。模板中的占位项必须替换并经受控人工复核；该 Schema 只约束字段形状，不能证明许可、授权、身份或脱敏审核真实。
- `source-register-validate --register <registry.json> --dataset <blind-eval.jsonl>`：校验来源登记字段、source_ref 与数据集引用/类型对应、评测用途声明和复核字段完整性。退出码 0 仅表示登记记录与样本引用结构一致；报告中的 `rights_reviewed`、`external_evidence_authenticity_verified_by_tool`、`phase_0_gate_passed` 固定为 false。受控人工审核流程见[证据接收规程](../../docs/blind-evaluation-evidence-intake.md)。
- `evidence-manifest.template.json` 与 `evidence-manifest.schema.json`：授权与评审独立性受控核验的清单模板及结构 Schema。模板初始状态为 `template_not_verified`，其中的引用、哈希、状态值需由职责分离的保管/复核人对照受控原始证据填写；单纯填写 `verified` 或提供 SHA-256 不能证明真实性。`blind-evidence-validate` 将清单与冻结数据逐条交叉核对，但不访问/认证 vault、不证明身份或授权，也不会把清单声明升级为已核验事实。受控流程、证据分库存放、撤回与核验边界见 [盲评证据接收规程](../../docs/blind-evaluation-evidence-intake.md)。
- `prediction.schema.json`（v2）、`pairwise.schema.json`、`pairwise-answer.schema.json`：候选预测、解盲后的比较记录，以及不含候选身份的盲评答卷格式。v2 收紧评审分数之间的一致性约束。
- `run-manifest.template.json`：运行环境、模型/提示词/输出契约/参数版本、数据快照哈希、准入协议版本与规则哈希和隐私设置记录模板。当前运行清单为 v4，除 system/user prompt 分束哈希外，必须记录 Provider 无关的 `output_contract_bundle_sha256`；它覆盖 JSON 输出字段、类型、枚举/数值范围与本地校验长度。候选 run-info 为 v2，封存映射为 v2，二者须绑定相同输出契约束哈希；cohort finalizer 拒绝契约不一致的候选。执行字段必须记录 `frozen_test_unlock_confirmed`；数据包含 `frozen_test` 时该值必须为 `true`，候选运行摘要和封存映射中的确认位也必须一致。旧版清单保留作历史记录，但缺少当前准入协议绑定、输出契约指纹及冻结集解封审计字段，不能作为当前准入协议下的可比基线。
- `blind-manifest-validate` 将清单绑定到原始 gold、predictions、评分报告和可选 comparisons 文件的 SHA-256；同时核对样本数、语义族/split 摘要、UTC 时间、必需指标字段和内容遥测关闭状态，并复验逐条 gold 的字段、来源元数据、PII 启发式和 v3.1 双评/裁定标签一致性。语义族摘要由去重后的 `family_id<TAB>split<LF>` 行按 ordinal 顺序拼接后计算 SHA-256。适用性指标可显式设为 null，比例与置信区间会检查取值范围。该命令不验证授权凭据、评审身份、评审者实际独立作答、模型身份或提示词内容本身是否真实；总量覆盖与 train/dev 重叠仍须通过 `blind-validate --training` 并核对 admission 报告。
- 离线工具 `DatasetBuilder` 的 `blind-validate` 命令使用严格字段白名单读取 schema v3.1，并检查样本数、授权/脱敏元数据、PII 启发式、两份完整独立标签及裁定链、语义族/输入跨集泄漏，并与传入的既有训练/开发集比对；额外未知字段会拒绝，不静默忽略。多轮记录还要求成对提供 `conversation_id` 和 `turns`，且只允许 `polish`；审计检查连续轮次、末轮输入一致性，并将 PII/重叠检测作用于所有用户轮次。PII 启发式扫描样本 ID、语义族 ID、来源引用、输入、参考输出、上下文、约束和全部评测标注，尝试发现大陆手机号（含常见空格/连字符及 +86/0086 前缀）、大陆居民身份证号、邮箱、明确标注的 QQ 群号/QQ 号、明确标注的微信号/WeChat ID、`wxid_` 前缀账号，以及带 `sk-` / `api_key` / `APIPassword` 字段标签特征、[OpenRouter `sk-or-v1-` key](https://openrouter.ai/docs/quickstart)、[Anthropic 官方示例中的 `sk-ant-api03-` key](https://docs.anthropic.com/en/docs/agents-and-tools/claude-for-sheets)、[xAI `xai-` key](https://docs.x.ai/developers/rest-api-reference/management/auth)、AWS `AKIA`/`ASIA` access key ID、Gemini 传统 `AIza` key、Groq `gsk_` key、Slack `xox*` token、HTTP Basic/Bearer Authorization 凭据和 PEM private-key header 的疑似密钥/凭据。OpenRouter、Anthropic 与 xAI 已知前缀后，只要跟随非空 ASCII 字母、数字、下划线或连字符就标为疑似凭据；官方示例未给出完整长度规范。`APIPassword` 标签后须有 `:` 或 `=` 与字段值，单独讨论该字段名不会命中。Gemini 新授权 key 若以不透明格式单独出现且没有 `x-goog-api-key` / `api-key` 等标签或 HTTP header，目前不保证命中；若以这些标签/header 形式出现，则会按其标签特征检查，不依赖未知 key 的字面格式。上下文与 `human_review` JSON 均递归检查解码后的字符串值、属性名和数值原文，避免序列化转义中文标识词后漏检。若样本 ID 本身疑似敏感，准入和评分问题报告均使用固定脱敏 ID，不回显原 ID。它只能验证记录和元数据完整性，不能替代人工核验授权、匿名盲评和标注质量，也不能保证发现所有个人信息或识别凭据是否仍有效。
- `blind-validate` 报告中的 `external_evidence` 固定标明 `verified_by_tool=false`，且授权与评审独立性状态为 `not_verified`。这不表示受控人工核验失败，而是该 CLI 没有读取/认证外部证据；不得仅凭顶层 `valid=true` 宣称阶段 0 准入完整。受控接收规程与证据清单模板见 `docs/blind-evaluation-evidence-intake.md` 和 `evidence-manifest.template.json`。
- OpenAI 项目 API key：OpenAI Cookbook 以 `sk-proj-…` 为 key 示例；启发式另检查 `sk-proj-` 后非空的 ASCII 字母、数字、下划线或连字符，因为通用 `sk-` 连续字符模式会在 `proj-` 处分段漏检。[OpenAI Cookbook 示例](https://developers.openai.com/cookbook/examples/agents_sdk/session_memory)。官方示例没有规定长度，检测不设长度界限；该前缀专属匹配不代表覆盖所有未来 OpenAI key 类型。
- 小米 MiMo：官方格式说明列出按量 key `sk-xxxxx` 和 Token Plan key `tp-xxxxx`。扫描器为 `tp-` 检查非空 ASCII 载荷；`sk-` 仍受通用模式的连续小写字母/数字和至少 12 字符限制，不能保证识别所有 key。[MiMo API 集成 FAQ](https://mimo.mi.com/docs/zh-CN/quick-start/faq/api-integration)
- 讯飞星火：官方 HTTP 文档说明 `APIPassword` 通过 Bearer Authorization 传送；扫描器支持保留的完整 Authorization 标头，也识别有 `:` 或 `=` 字段值的 `APIPassword` 标签，但不保证识别无标签且格式未知的裸 key。[星火 HTTP 调用文档](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)
- 火山方舟 API key：官方 quick start 展示 `ark-<uuid>-<suffix>` 新格式；离线检查匹配标准分组十六进制 UUID 和非空后缀。[Ark quick start](https://docs.volcengine.com/docs/ark/quick-start-beginner?lang=zh)。阿里云 Model Studio Coding Plan / Token Plan 官方 key 以 `sk-sp-` 开头，离线扫描器也覆盖该前缀；这仅用于避免评测文本意外包含凭据，不代表话匣子支持套餐，阿里云将 Coding Plan 限于指定交互式编程工具。[阿里云 Coding Plan FAQ](https://help.aliyun.com/zh/model-studio/coding-plan-faq)。
- `blind-evaluate` 同样对每条成功候选输出执行上述 PII 启发式检查；命中时只报告记录 ID 和问题类别，不回显候选内容，并使本次评分失败。发现疑似内容后应隔离相关产物，人工核查来源与脱敏边界后再决定如何处理。
- `../polish-agent-v2/`：现有合成数据；可做回归，不可替代外部盲评。
- 每次实验另建不可变目录，保存脱敏后的样本快照、输出、盲评标注与汇总，不覆盖旧运行。`blind-evaluate --output` 只创建新报告；目标文件已存在时会失败并保留原文件，请为每轮运行使用新目录或文件名。

`blind-evidence-validate` 用于核验受控证据索引与当前 gold 字节、来源引用、逐条盲包哈希、两份评审提交及裁定提交的一致性；裁定提交时间不得早于两份评审中较晚的一份。每条样本/评审的 `reviewer_ref`（以及需要时的 `adjudicator_ref`）必须是整份数据集唯一的一次性任务引用；validator 会跨记录拒绝重复引用，但这不能证明引用背后是不同真实身份。它需要同时传入 `--training`，复跑当前准入与泄漏检查。报告分开输出 `evidence_index_structurally_valid`、`record_integrity_valid`、清单中的授权/独立性状态声明，以及固定为 false 的 `rights_reviewed`、`reviewer_independence_verified`、`external_evidence_authenticity_verified_by_tool` 和 `phase_0_gate_passed`。`validation_passed` 只表示证据索引格式、内部关联和数据记录检查通过。JSON 清单状态是待人工核对的声明；时间顺序检查不能证明可信时间，也不能替代保管库原件、身份/时间核验、评审职责隔离和签名信任链。`--output` 使用 create-only 写入。

## 标注与指标

每个候选输出由两名标注者独立判断，并对分歧进行第三方裁定。候选模型与输出顺序随机化，标注者不显示模型名称。评估员按事实/约束逐条打勾；高风险样本对关键事实反转必须单列并要求为零。其余维度 1–5 分：事实保真、任务完成、自然度、语气/场景匹配、直接可用性与安全性；另记录是否澄清、Schema 是否合法、是否需人工大改。

报告至少给出：事实/约束保留率、直接可用率、语气匹配率、Schema 合法率、高风险关键事实反转数、澄清决策准确率、成对偏好与置信区间；API 请求记录延迟/用量/错误类别，本地记录模型加载时间、tokens/s、p50/p95 和峰值内存，并按硬件分层。评分 JSON 的 `product_slice_reports` 还按候选 × task × split × 固定产品场景/类别输出样本数、覆盖、Schema、事实保留、直接可用、语气、澄清、安全和高风险反转指标，并为延迟、token 用量和本地推理指标提供观测数；失败、缺失与重复/无效预测分开计数。token 总量只在该切片所有尝试均有对应字段时输出，部分数据返回 `null`；延迟和本地性能分位数须与各自观测数一起解释。产品切片只有描述性统计，不单独判断显著性或准入，以免小样本比例被误读为稳定收益；切片结论必须结合计数、冻结集总指标和语义族级成对比较。除非用户显式启用采集，诊断不记录输入、成稿或个性化内容。

人工候选输出评分每条由两名不知道模型身份的评审者完成；每个维度评分 1–5，同时记录事实约束保留、直接可用、语气匹配、澄清决策、安全通过和高风险事实反转。`directly_usable` 必须与 `direct_usability_score` 一致：4–5 分为 true，1–3 分为 false；若 `high_risk_key_fact_reversed=true`，`fidelity_score` 必须为 1。prediction Schema 和离线 scorer 会拒绝违反这两条跨字段规则的原始评审或裁定记录。任一标签/评分有分歧时必须由不同于原评审者的第三人裁定。成对比较按样本 ID 对齐、左右顺序随机化，评审者只选择 left/right/tie。报告同时给出按样本汇总的描述性胜率，以及按 `semantic_family_id` 将每个语义族裁为左胜/右胜/族内平局后的家族级胜率、Wilson 95% 区间和独立决定性语义族数；至少 30 个决定性语义族且家族级区间下界高于 0.5，才标记为显著。家族级估计让每个预定义语义族贡献一个独立结果，避免同一底层任务的变体被重复当成独立证据；其解释依赖语义族划分有效且家族间可视为独立。30 是本项目的最低操作门槛，不保证统计功效；真实冻结数据到位后仍须基于语义族数量/大小与目标最小有意义效果评估功效，家族不足时只报告描述值、不宣称个性化收益。可信区间只支持偏好差异判断，不替代事实保真、安全和直接可用性门槛。

## 解锁阶段 1 的条件

- 来源授权和脱敏记录齐全；
- 至少 500 条盲评样本，标注规则和双人抽检完成；
- 语义族隔离、测试集访问控制及数据快照哈希可追溯；
- 当前润色、提示词优化及代表性云端/本地基线用固定提示词、采样参数、模型和代码版本完成一次可复跑比较；
- 运行报告披露本项目合成数据评测的局限，不据此宣称外部质量。

任一条件未满足时，阶段 0 保持未通过。可以继续做不改变生成行为的审计、协议与数据治理工作；不得启用新的提示/路由/权重来宣称质量提升。

## 运行准入审计

`--training` 可重复传入，每个文件应为项目既有 `DatasetBuilder` canonical JSONL：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-validate --input .\datasets\ai-evaluation\blind-eval.jsonl --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl
```

退出码 `0` 仅表示该样本文件通过本命令能执行的结构与重叠检查；它本身不表示云/本地基线、实验复跑或阶段 0 整体门槛通过。

## 产品提示快照（离线）

在有效盲评样本通过 `blind-validate` 后，可用以下命令从应用实际的 `PolishPromptBuilderService` 和 `PromptBuilderService` 构造首轮请求提示快照：

```powershell
dotnet run --project .\tools\BlindEvaluationRunner\BlindEvaluationRunner.csproj -- snapshot --input .\datasets\ai-evaluation\blind-eval.jsonl --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl --split development --output .\datasets\ai-evaluation\runs\<new-run-id>
```

`--split` 必须显式选择 `development` 或 `frozen_test`，普通流程建议先只导出 development。导出 frozen_test 时还必须附带 `--confirm-frozen-test-locked`，声明模型、提示和采样参数已经锁定；该选项只记录操作者确认，不验证模型配置真实锁定。每条输出只有 `id`、`task`、`split`、`system_prompt` 和 `user_message`；它不会包含参考成稿、评测标注或 gold 评审身份。润色任务从 `context` 读取 `recipient`、`channel`、`purpose`、`formality`、`scenario`、`output_style`、`custom_style_instructions`、`persona`、`custom_system_prompt`、`preference_instructions`、`companion_driver_mode` 和可选 `clarification_enabled`；提示词优化读取 `category`、`depth`、`persona`、`custom_system_prompt`、`preference_instructions` 和 `companion_driver_mode`。两类任务均把 `constraints` 作为本轮明确要求输入产品规划器。

快照文件包含完整提示内容，必须保存在受控本地目录。旁边的 `snapshot-manifest.json` 记录原始 gold 摘要、提示文件和提示束摘要、产品程序集版本，并明确标记 `provider_calls_performed=false`、`content_telemetry_enabled=false`。输出目录必须是新目录，不能覆盖既有运行。快照只复现生产提示构造，不执行 Provider 请求、工作流质量修复或人工评分，不能单独作为模型基线或质量证明。

## 显式候选运行

完成数据授权、脱敏、人工双评和 split 冻结后，可用 runner 对单个候选执行完整产品工作流。候选配置仅保存 `ProviderProfile` 和环境变量名，不写入 API key，例如：

```json
{
  "profile": {
    "id": "eval-openai",
    "name": "API candidate",
    "type": "Cloud",
    "platform": "OpenAI",
    "protocol": "OpenAICompatible",
    "api_base": "https://api.openai.com/v1",
    "model": "<固定模型版本>",
    "temperature": 0.4,
    "top_p": 1.0,
    "max_tokens": 2048
  },
  "api_key_environment_variable": "HXZ_EVAL_API_KEY"
}
```

```powershell
$env:HXZ_EVAL_API_KEY = "<在本机安全注入的 key>"
dotnet run --project .\tools\BlindEvaluationRunner\BlindEvaluationRunner.csproj -- run --input .\datasets\ai-evaluation\blind-eval.jsonl --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl --candidate-config .\candidate.json --output .\datasets\ai-evaluation\runs\<new-reviewer-run> --sealed-manifest .\restricted\<new-candidate-map>.json --split all --hardware-profile <non-identifying-tier-label> --allow-cloud --authorization-ref <approved-ref> --confirm-frozen-test-locked
```

ManagedLocal 候选配置示例（`model` 与 `local_model_installation_id` 必须是产品已安装模型的同一 installation ID）：

```json
{
  "profile": {
    "id": "eval-managed-local",
    "name": "managed local candidate",
    "type": "Local",
    "platform": "ManagedLocal",
    "protocol": "OpenAICompatible",
    "api_base": "http://127.0.0.1:0/v1",
    "model": "<installed-model-id>",
    "local_model_installation_id": "<installed-model-id>",
    "temperature": 0.4,
    "top_p": 1.0,
    "max_tokens": 2048
  },
  "api_key_environment_variable": ""
}
```

ManagedLocal 命令还必须显式指定 `--runtime-root <runtimes/local>`；该目录下应包含 `cpu/llama-server.exe` 和/或 `vulkan/llama-server.exe`。可用 `--model-root <installed-models-root>` 覆盖产品默认模型目录；若不覆盖，使用 `%LOCALAPPDATA%\\Huaxiazi\\models`。运行时进程由 runner 启动，并在候选执行结束时停止。CLI 每次都重新审计完整 gold 与 train/dev 重叠，再选指定 split。`development` 可用于调试；需要完成评分、指标和最终清单时，应对冻结后的同一候选使用 `--split all --confirm-frozen-test-locked` 跑完整数据快照，保证 gold、请求和预测样本数一致；只运行单个 split 的部分结果不能生成完整运行清单。云 profile 除配置外还必须由调用者同时传入 `--allow-cloud --authorization-ref <授权凭据引用>`；只有检查通过后才读取配置指定的环境变量。Cloud profile 必须是非 loopback HTTPS；外部 Local profile 仅允许带端口的 loopback HTTP，以及当前生产 endpoint 安全策略支持的 Ollama 或 LM Studio。外部候选会经过产品当前的 fail-closed endpoint policy；ManagedLocal 由产品本地运行时管理器启动，实际请求端点在本机动态分配。loopback 自定义 OpenAI 兼容端点暂不受应用现有本地 endpoint policy 支持。工具只运行指定候选，不做自动路由或 fallback。候选配置中的 API 地址禁止 userinfo、query 和 fragment，且未知/内嵌 key 字段会被拒绝。

`predictions.jsonl` 是不可变原始候选产物，只带随机 alias、输出、Schema 标志、空评审数组、运行状态和可用汇总指标；候选 Provider、模型、地址和授权引用写入 `--sealed-manifest` 指定的独立文件，该路径不能位于评审输出目录内。所有目标均 create-only。人工评审时将原始预测复制为单独的 `reviewed-predictions.jsonl` 并只在副本上填写评审与裁定字段；评分和最终清单使用此副本，同时保留原始文件用于验证候选内容未被改写。请求观测写入 `request-telemetry.jsonl`，仅有 request ID、HTTP 状态、错误类别、计数和时延，不记录请求正文或响应正文。Provider 返回的 ID 仅接受最长 200 字符的 ASCII 字母、数字、下划线、连字符、点和冒号，避免把回显内容误写为 request ID。API token 计数只在 Provider 响应实际提供且结构合法时填写，未知值保持 null；OpenAI-compatible 两种 usage 命名、Anthropic 输入缓存计数和 Gemini usageMetadata 分别解析。外部 loopback 服务按返回 token 数和请求时延计算端到端 tokens/s；ManagedLocal 现在也按本地响应 usage 记录 token 数和 HTTP 请求时延，后者在运行时 `EnsureStartedAsync` 后开始，因此不混入首次启动耗时。llama-server 未提供合法 usage 时 tokens/s 仍为空。ManagedLocal 的运行时指标记录进程启动到 `/health` ready 的时长（作为启动/加载代理，不等同于纯权重加载时长）及 llama-server 进程 `PeakWorkingSet64`；后者不含 Vulkan 显存。指标接口已用 fake runtime 测试，固定运行时版本与目标设备上的实际测量仍需等运行时包具备后完成。候选输出触发 PII/密钥启发式时会从 reviewer 文件中剔除，只留规范化 `other` 错误状态和 withheld 数量。

`run` 必须在候选开始前提供 `--hardware-profile`；相同值写入封存映射和 `run-info.json`，`finalize`/`finalize-cohort` 会交叉检查它与最终元数据相同，cohort 内所有候选也必须一致。该值须使用不含序列号、主机名或用户名的稳定档位别名；它是操作员声明和一致性约束，不是自动硬件探测或真实性证明。Cloud 候选记录的是执行客户端档位，云服务端推理硬件未知。新清单将 `execution.hardware_profile_source` 标记为 `operator_declared_unverified`；历史 v3 清单可缺省此扩展字段，若提供则必须匹配该值。`run-info.json` 是执行摘要，包含数据/原始预测/telemetry/prompt bundle/输出契约束摘要与候选 alias，不含真实模型身份；封存映射同时记录产品系统提示、用户消息分束和 Provider 无关输出契约的 SHA-256。Schema 原生传输还是文本兼容回退不改变语义指纹；不同本地校验规则会改变指纹。Ctrl+C 会取消当前请求并将当前/剩余样本写成 `cancelled` 状态，然后保留完整不可覆盖的部分运行产物。双人盲评与评分完成后，由解盲管理员生成最终清单；该步骤会核对 sealed candidate alias、gold/原始预测哈希、评审副本与原始预测的逐条内容一致性、候选评测报告、提示及输出契约摘要和运行信息，再计算必需指标并调用清单校验器，最终清单必须保存在评审产物目录之外。v1 旧格式的 `system_prompt_sha256` / `developer_prompt_sha256` 仍可读取；新运行记录真实的 `system_prompt_bundle_sha256` / `user_message_bundle_sha256`，不把当前产品未独立暴露的 developer 文本伪称为独立提示层。评测工具无法证明 CLI 操作者提供的授权引用真实有效，也不能替代人工权限核验。Cloud 执行必须由数据授权和费用预算负责人单独批准；本地测试可以先使用测试专用 loopback 服务与已授权合成/盲评切片。

计数映射依照 Provider 官方响应字段：OpenAI Responses 使用 `input_tokens` / `output_tokens`，Chat Completions 使用 `prompt_tokens` / `completion_tokens`；Anthropic Messages 将 `input_tokens`、`cache_creation_input_tokens`、`cache_read_input_tokens` 汇总为输入量；Gemini 使用 `usageMetadata.promptTokenCount` / `candidatesTokenCount`。字段会随 API 版本演进，适配上线前应复核官方资料：[OpenAI Responses API](https://developers.openai.com/api/reference/cli/resources/responses/methods/create)、[OpenAI Token counting](https://developers.openai.com/api/docs/guides/token-counting)、[Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)、[Gemini GenerateContent](https://ai.google.dev/api/generate-content)。

在总输入 token 之外，评测请求遥测与汇总会分别保留 `cache_read_input_tokens` 和 `cache_creation_input_tokens`（Provider 有提供时）。OpenAI 的缓存 token 位于输入 usage details，Anthropic 提供缓存读取/写入输入 token，Gemini 提供 `cachedContentTokenCount`。缓存细分缺失时记为未知，不从总输入量猜算；只要汇总切片中有缺项，对应细分总量就保持 null，避免把不完整数字冒充完整用量。CLI 输入校验允许这两个字段，评分器拒绝负数；`prediction.schema.json` 的遥测 schema 也与运行时一致地允许指标和错误类别使用 null。具体费率由模型和缓存类型决定，清单不根据 token 单价自动推算账单。字段映射于 2026-10-03 复核：[OpenAI Prompt Caching usage](https://developers.openai.com/api/docs/guides/prompt-caching)、[Anthropic Messages usage](https://platform.claude.com/docs/en/api/typescript/messages)、[Gemini usageMetadata](https://ai.google.dev/api/generate-content)。

ManagedLocal 运行前会流式重算已安装模型/LoRA 与所选 `llama-server.exe` 的 SHA-256，并与模型仓库登记值比对；模型大小、版本、installation ID、适配器基座绑定、CPU/Vulkan 选择及可读取的文件版本会写入评审目录外的封存映射，最终运行清单会复用同一记录。清单校验器要求 ManagedLocal 记录具备完整 provenance，拒绝缺失或格式不合法的摘要；路径不写入封存身份字段。大型 GGUF 文件的完整哈希可能使运行准备多耗时。该能力目前用合成小文件与占位运行时文件验证数据链路，不代表真实 llama.cpp 运行时/模型已验收。

人工评审完成后可计算候选指标和同案例成对偏好：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-evaluate --gold .\datasets\ai-evaluation\blind-eval.jsonl --split frozen_test --predictions .\datasets\ai-evaluation\runs\<reviewer-run>\reviewed-predictions.jsonl --comparisons .\datasets\ai-evaluation\runs\<reviewer-run>\comparisons.jsonl --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl --output .\datasets\ai-evaluation\runs\<reviewer-run>\report.json
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-manifest-validate --manifest .\datasets\ai-evaluation\run-manifest.json --gold .\datasets\ai-evaluation\blind-eval.jsonl --predictions .\datasets\ai-evaluation\predictions.jsonl --report .\datasets\ai-evaluation\report.json --comparisons .\datasets\ai-evaluation\comparisons.jsonl
```

跨候选偏好必须先将同一 gold 上的候选原始预测制成独立评审包。命令会重跑完整数据准入和 train/dev 泄漏审计，并要求显式指定一个 `--split development|frozen_test`；每个包只包含所选 split，开发集与冻结集不得混包或合并评分。选择 `frozen_test` 时还必须确认模型/提示/采样均已锁定。它对每个可比较案例加密随机左右顺序、为评审项生成随机 ID，只导出输入、约束、评分锚点和两份成稿；候选 alias 与 gold ID 的对应关系封存在 reviewer 目录外。评审文件仍含受控输入和模型生成内容，应按授权约定限制访问。评审人员复制答卷模板，填写两名评审者的 `left` / `right` / `tie` 投票；有分歧时补第三人裁定。merge 会核对评审包哈希、答卷覆盖率和评审者身份，再恢复成 scorer 所需的内部 candidate ID，并将解盲比较文件写到评审目录之外：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-pairwise-package --gold .\datasets\ai-evaluation\blind-eval.jsonl --split frozen_test --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl --predictions .\datasets\ai-evaluation\runs\<candidate-a>\predictions.jsonl --predictions .\datasets\ai-evaluation\runs\<candidate-b>\predictions.jsonl --output .\datasets\ai-evaluation\runs\<new-pairwise-review> --sealed-manifest .\restricted\<pairwise-map>.json --confirm-frozen-test-locked
Copy-Item .\datasets\ai-evaluation\runs\<new-pairwise-review>\pairwise-answers.template.jsonl .\datasets\ai-evaluation\runs\<new-pairwise-review>\pairwise-answers.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-pairwise-merge --answers .\datasets\ai-evaluation\runs\<new-pairwise-review>\pairwise-answers.jsonl --review-package .\datasets\ai-evaluation\runs\<new-pairwise-review>\pairwise-review.jsonl --sealed-manifest .\restricted\<pairwise-map>.json --output .\restricted\<comparisons>.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- blind-evaluate --gold .\datasets\ai-evaluation\blind-eval.jsonl --split frozen_test --predictions .\datasets\ai-evaluation\runs\<candidate-a>\reviewed-predictions.jsonl --predictions .\datasets\ai-evaluation\runs\<candidate-b>\reviewed-predictions.jsonl --comparisons .\restricted\<comparisons>.jsonl --training .\datasets\v1\canonical.jsonl --training .\datasets\polish-agent-v2\canonical.jsonl --output .\datasets\ai-evaluation\runs\<new-pairwise-review>\report.json
```

`blind-evaluate` 支持重复 `--predictions`，同一报告仅汇总 `--split` 指定 split 的候选单项质量与配对偏好；完整 gold 仍会重新做准入/泄漏审计，预测按选定 split 过滤，`comparisons` 若含其它 split 样本则拒绝。省略 split 的 `all` 只用于全量诊断，不可用于最终清单。公开给评审者的 pairwise JSONL 不含 candidate ID、gold ID、参考答案或候选映射；`pairwise-answers.template.jsonl` 应复制后填写，不能修改原始评审包。单候选 `finalize` 绑定 `development` 或 `frozen_test` 单 split 报告，并按该 split 重算指标；多候选配对总报告使用独立 `finalize-cohort` 清单，绑定所有候选的运行摘要、模型/提示身份、原始与评审预测摘要、统一评分报告、匿名 pairwise 评审包、原始答卷、封存映射和 comparisons 文件。finalizer 会拒绝评分 split 与比较 ID 不一致；cohort finalizer 还从原始匿名答卷重建 comparisons，并校验映射中的左右候选/样本与预测成稿哈希。`blind-manifest-validate` 对新格式清单交叉校验报告 split、所选样本数和比较样本归属；未包含 split 扩展字段的历史清单仍走旧版兼容校验。清单写入 `evaluation_role=development_diagnostic|locked_final_evaluation` 与冻结集确认状态，不自动宣称阶段门槛或候选晋级已通过。最终清单需放在所有评审目录之外。

示例（重复参数按候选顺序成组出现；候选 ID 必须与 sealed candidate map 一致）：

```powershell
dotnet run --project .\tools\BlindEvaluationRunner\BlindEvaluationRunner.csproj -- finalize-cohort --gold .\datasets\ai-evaluation\blind-eval.jsonl --raw-predictions .\runs\a\predictions.jsonl --predictions .\runs\a\reviewed-predictions.jsonl --run-info .\runs\a\run-info.json --sealed-manifest .\restricted\a-map.json --candidate-id a --raw-predictions .\runs\b\predictions.jsonl --predictions .\runs\b\reviewed-predictions.jsonl --run-info .\runs\b\run-info.json --sealed-manifest .\restricted\b-map.json --candidate-id b --report .\runs\cohort\report.json --comparisons .\restricted\comparisons.jsonl --pairwise-answers .\runs\cohort\pairwise-answers.jsonl --pairwise-review-package .\runs\cohort\pairwise-review.jsonl --pairwise-sealed-manifest .\restricted\pairwise-map.json --output .\restricted\cohort-manifest.json --dataset-id <dataset-id> --authorization-review <review-ref> --hardware-profile <host> --runtime-version <runtime-version> --code-revision <revision> --run-id <run-id>
```

cohort 清单目前绑定候选/报告/匿名 pairwise 输入及比较文件摘要并重新计算 scorer 结果；它仍是本地内容寻址清单，不是数字签名。输出必须存放在所有候选评审目录之外。

候选评分后，由评测管理员在评审目录外执行 `finalize`。授权审查引用、数据集 ID、机器/运行时版本、代码修订和运行 ID 由管理员依据可追溯记录填写：

```powershell
dotnet run --project .\tools\BlindEvaluationRunner\BlindEvaluationRunner.csproj -- finalize --gold .\datasets\ai-evaluation\blind-eval.jsonl --raw-predictions .\datasets\ai-evaluation\runs\<reviewer-run>\predictions.jsonl --predictions .\datasets\ai-evaluation\runs\<reviewer-run>\reviewed-predictions.jsonl --report .\datasets\ai-evaluation\runs\<reviewer-run>\report.json --run-info .\datasets\ai-evaluation\runs\<reviewer-run>\run-info.json --sealed-manifest .\restricted\<candidate-map>.json --output .\restricted\<final-run-manifest>.json --dataset-id <dataset-id> --authorization-review <review-ref> --hardware-profile <host-profile> --runtime-version <runtime-version> --code-revision <revision> --run-id <run-id> [--comparisons .\datasets\ai-evaluation\runs\<reviewer-run>\comparisons.jsonl]
```

`finalize` 是解盲动作：只应由评测管理员在匿名评分已冻结后执行。它拒绝 alias、数据、预测和提示摘要不匹配，拒绝不完整或含敏感输出剔除记录的运行，并以 create-only 方式写清单。报告文件必须是 `blind-evaluate` 的完整 JSON 报告；不能只提供手工摘录出的分数。

费用字段 `metrics.api_cost_accounting` 显式区分 `not_reported`、`not_applicable`、`estimated` 和 `provider_billed`。单候选与 cohort 收口器在没有费用凭证输入时，云 Provider 写 `not_reported`，本地推理写 `not_applicable`；清单校验器会检查费用状态与 Cloud/Local Provider 类型相符，不会从 token 数推算账单金额。人工补录估算必须绑定费率卡引用、SHA-256 和核算时间；实际账单金额必须绑定 Provider 账单记录引用及其摘要。引用只允许受控不透明 ID，不写入 API key、账单正文或用户内容。

硬件分层运行方式：每个单候选运行分别生成自己的清单，记录该次声明档位的本地/客户端性能指标；需要判断模型偏好时，在每个档位内另建同档候选 cohort。该约束让硬件成为受控变量，而不是把硬件差异混入模型偏好。不同档位之间只按质量/延迟/tokens/s/内存/加载指标分层描述，不跨档合并 pairwise 显著性或将其解释为模型增益。云端档位仍只代表执行客户端，不提供 Provider 推理硬件信息。

预测文件只使用随机化 `candidate_id`，模型名称、Provider 和输出左右顺序的映射单独封存，盲评完成后再解盲。评分器不把机器可解析的 JSON 当作语义质量；结构通过率由 Schema 检查记录，其他质量项取双评审共识或第三方裁定。退出码 `0` 表示至少一个候选达到计划中的基本质量门槛且评审记录完整；`4` 表示记录可评分但没有候选达标；`3` 表示数据、评审或成对比较记录无效。

失败或取消的请求必须带 `status` 和规范化 `error_category`，不能附带候选输出或人工评分。若产品工作流质量门禁阻止结果、响应无效或最终结果为空，runner 必须记为失败，错误类别为 `workflow_rejected`；仅 `completed` 或包含澄清问题的 `needs_clarification` 且输出非空，才记为成功。评分器将失败/取消尝试计为缺失覆盖，并按错误类别汇总；Provider 服务失败和本地 runner 输入/权限/文件系统错误分别归为 `provider_error` 与 `runner_error`，避免把评测工具故障误计为模型服务故障。



