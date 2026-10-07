# W6.1 外部语料来源初筛

- 版本：`hxz-polish-regression-w6-intake-v1`
- 初筛日期：2026-10-05
- 官方页面复核日期：2026-10-05
- 状态：`metadata_screen_only`
- 阶段 0 贡献：**0**
- 权利/用途核验：所有候选均为 `pending`
- 数据动作：未下载、未复制、未导入、未做样本内容处理

## 初筛原则

本清单只依据公开项目页、数据卡与论文摘要做来源和任务元数据初筛，不构成法律意见、授权证明或样本质量验收。必须区分代码仓库许可、数据集标注许可与上游原始内容权利。任何候选进入项目数据目录前，均需人工确认实际文件版本、逐来源权利、再分发/训练/评测用途、PII与撤回处理、公开泄漏风险，并保存可审计凭据。

公开评测集即使许可字段完整，也可能因提示、参考答案或模型输出已公开而不适合作为冻结盲评集。阶段 0 的盲评集应从独立授权样本轨道构建；本表所有条目的阶段 0 计数始终为 0。

## 候选清单

| 候选 | 来源页声明与内容 | 与本项目的适配 | 主要风险/缺项 | AI 初筛建议 | 人类决定 | 人类决定凭据 |
|---|---|---|---|---|---|---|
| RewritingBench / `heihei/llm-rewrite` | Hugging Face 页面当前列 CC BY 4.0，并称页面对应 EMNLP 2026 论文；列出 730 条人评对、600 条 train、129 条 eval 和 1,008 条类别平衡 train。数据卡报告每条 3 位评分者。 | **高**：中文改写任务直接相关，可参考维度、标注方案及评分方式。 | 公开数据及答案有污染风险；129 条 eval 小于项目 500 条目标；数据查看器因 `messages` 与 `input/output/score` 字段不一致而失败（页面错误指向数据快照 `e4de696e6e9c0ed4b8d115d3193d4190da17c82b`）；上游原文权利和本项目拟用目的仍需逐项核验。 | 仅推荐作为评测设计参考；未来如人工核权并完成重叠检查，可另议开发集用途；不推荐进入冻结测试集。 | `pending` | `not_recorded` |
| ToxiRewriteCN | Hugging Face 页面称核心标注集含 1,556 条中文改写三元组，并列出 1,000/556 的 train/test rewrite split；该页表述 Apache-2.0 指向 source repository。 | **中（限安全子任务）**：适合研究意图/情感保留与安全语气转化。 | 页面明确提示样本可能含冒犯、歧视或有害表达；不是通用润色分布；仓库许可不证明每条上游内容权利或商业/再分发适用性；公开 test 有泄漏风险。 | 只可考虑为隔离的安全鲁棒性诊断参考，不进入通用润色金标或冻结盲评；需安全、权利和用途负责人复核。 | `pending` | `not_recorded` |
| WritingBench | 官方仓库声明 Apache-2.0；README 概览称 1,000 条写作查询、6 个主领域、100 个子领域、每条 5 个实例化 criteria；但同一 README 的首版发布记录称 1,239 条查询。 | **中（评测方法参考）**：可借鉴场景/约束覆盖、逐题评分标准和评审组织方式。 | 主要是长文写作查询，不是润色输入-输出对；1,000/1,239 数量口径不一致，未来使用前须固定具体版本；仓库许可不能自动解决被引用材料权利；公开基准及标准不适合作为未公开冻结测试样本。 | 推荐仅参考评测设计；不得直接挪用公开查询/criteria 充当冻结集。 | `pending` | `not_recorded` |
| COIG-CQIA | 论文与数据卡称其为经过人工核验的中文指令数据集，来源包含中文互联网问答和文章；数据卡许可明确写为“More Information Needed”。 | **低（通用指令背景）**：覆盖广泛中文指令与领域，可辅助了解表达类型，不是专门润色基准。 | 许可未明确；来源包含知乎、豆瓣、小红书、百科、医疗、金融等第三方内容；“human_verified”不等于权利已获授权；与润色契约的匹配较弱。 | 在许可及来源权利未明确前不考虑导入、训练或计分。 | `pending` | `not_recorded` |
| MCTS 多参考中文文本简化集 | 官方仓库说明包含 723 个新闻复杂句、每例多个手工简化参考；仓库 `LICENSE` 在 GitHub 上显示约 35 KB 且由 Git LFS 管理，网页读取到的是 LFS 指针，未核实具体许可正文。 | **低（相邻改写诊断）**：可参考“多种可接受改写”及简化质量维度；简化目标不等同于本项目的润色、事实锚定或 prompt optimization。 | 样本规模不足以独自覆盖 ≥500 通用盲评要求；公开 test/参考答案存在污染风险；具体 license 条款与新闻句子上游权利均未核实；论文中的人工评估不能替代本项目双评/裁定证据。 | 仅推荐作为评测方法参考；取得并审查确切许可文件、上游来源与用途前，不下载样本、不进入任何内部数据或评测集。 | `pending` | `not_recorded` |

## 建议结论

1. **不从公开基准拼凑阶段 0 冻结测试集。** RewritingBench 与 WritingBench 对确定任务维度和标注手册有参考价值，但其公开状态使模型/提示污染难以排除。
2. **继续以独立授权、脱敏、人工审核样本达到 ≥500 条为正式盲评目标。** 优先由项目方原创或明确授权来源提供，并按语义族隔离；来源与授权记录独立保存，未经许可的公开抓取不作为替代方案。
3. **将安全润色单独建切片。** ToxiRewriteCN 仅可能提供安全场景设计参考；若实际使用，先单独确认上游权利、内容访问边界及是否超出项目用途。
4. **W6.1 仅完成候选元数据初筛。** 进入样本导入前必须有人工权利审查结果与负责人批准；即使批准，也不能自动改变 `phase_0_gate_contribution: 0`。
5. **MCTS 元数据复核补充（2026-10-05）：** 官方页面确认任务规模和多参考结构，但 LICENSE 由 Git LFS 管理，浏览页无法读取许可正文。本轮不获取数据或 LFS 对象；许可条款和上游新闻文本权利保持 pending。

## 人类权利/用途决定（W6.2–W6.3）

- `AI 初筛建议` 只用于整理研究线索，不是授权判断、数据准入决定或法律结论。
- `人类决定` 是逐候选的独立控制字段；当前全部为 `pending`。`人类决定凭据=not_recorded` 表示尚无书面决定证据，不能把它解释为默认同意。
- 任何候选进入内部数据处理前，必须记录实际数据版本、逐来源权利核查、明确用途、决定人、日期和可审计的书面凭据。未完成前不下载、不复制、不导入、不训练、不计分。
- 即使未来有人类书面决定批准某候选用于内部工作，也不能把该公开数据并入正式阶段 0 盲评集；阶段 0 仍须使用独立授权轨道样本。

## 来源记录

- RewritingBench（页面复核于 2026-10-05）：[Hugging Face 数据集页](https://huggingface.co/datasets/heihei/llm-rewrite)；页面错误所指文件快照：[e4de696e6e9c0ed4b8d115d3193d4190da17c82b](https://huggingface.co/datasets/heihei/llm-rewrite/tree/e4de696e6e9c0ed4b8d115d3193d4190da17c82b)
- ToxiRewriteCN（页面复核于 2026-10-05）：[Hugging Face 数据集页](https://huggingface.co/datasets/shanewang/ToxiRewriteCN)；论文：[ACL Anthology](https://aclanthology.org/2025.emnlp-main.1808/)
- WritingBench（README 与 LICENSE 复核于 2026-10-05）：[官方 GitHub README](https://github.com/X-PLUG/WritingBench)；[仓库 LICENSE](https://github.com/X-PLUG/WritingBench/blob/main/LICENSE)
- COIG-CQIA（页面复核于 2026-10-05）：[官方数据卡](https://huggingface.co/datasets/m-a-p/COIG-CQIA)；[NAACL 2025 论文页](https://aclanthology.org/2025.findings-naacl.457/)
- MCTS（官方 README 与 LICENSE 页面复核于 2026-10-05）：[官方仓库](https://github.com/blcuicall/mcts)；[Git LFS 管理的 LICENSE 页面](https://github.com/blcuicall/mcts/blob/main/LICENSE)

## 变更控制

本文件是外部来源元数据清单，不属于冻结的 `polish-regression-v1`，不修改其 manifest 或哈希。清单不含样本正文、预测、用户数据或授权原件。AI 初筛建议与人类决定分列；权利状态未经独立人工复核前逐项保持 `pending`，书面凭据逐项为 `not_recorded`；清单本身不产生引入或发布许可。
