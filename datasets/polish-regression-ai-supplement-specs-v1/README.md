# AI 补样规格草稿 v1

状态：AI 撰写、待人工审阅。这里只是候选规格，不是可运行样本、金标或训练数据。

## 生成依据

- 来源是冻结 v1 的 W4 覆盖报告以及 20 条未审阅 AI 草稿的覆盖统计。
- 计划建议每个被纳入的内部回归行为至少覆盖 5 个独立族；该数值是规划目标，不是统计功效证明。
- 当前 10 个目标类别共缺 30 个规格。若每个规格各生成一个新族，草稿集将从 20 增至 50，仍低于 120 族的上限。
- 所有规格都列出事实锚点、约束、期望决策、评分锚点和禁止内容。状态保持 `ai_spec_draft_pending_human_review`。
- 三条多轮规格目前把对话序列编码在 `input` 文本中。这只是数据设计草案；要成为真实多轮评测，须先扩展 `turns` Schema 并实现逐轮回放，不能把单字段文本称为多轮运行验证。

## 规格分类数

| 行为 | 当前 AI 草稿族数 | 新规格数 | 目标族数 |
|---|---:|---:|---:|
| clarification_positive_examples | 1 | 4 | 5 |
| needs_clarification_outputs | 1 | 4 | 5 |
| format_and_schema_requirements | 2 | 3 | 5 |
| high_risk_fact_reversal | 2 | 3 | 5 |
| tone_and_scenario_diversity | 2 | 3 | 5 |
| non_workplace_scenarios | 3 | 2 | 5 |
| multi_turn_revision_and_user_negation | 2 | 3 | 5 |
| negation_quantity_time_condition_boundaries | 3 | 2 | 5 |
| prompt_injection_resistance | 2 | 3 | 5 |
| cross_language_mixing | 2 | 3 | 5 |

## 使用边界

人工审阅应检查事实是否自洽、是否和已有案例同义重复、期望决策是否必要、rubric 是否可观察，以及是否应当扩展 Schema。只有人工评审规格后，才把通过的规格转成新的 AI 案例草稿；仍须单独人工审阅案例标签与参考输出。

本包 `phase_0_gate_contribution=0`，不可用于盲评，不授权生成，不可用于模型晋级。源哈希和计数见 `manifest.json`。
