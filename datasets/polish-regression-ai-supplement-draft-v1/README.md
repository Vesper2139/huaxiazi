# AI 补充润色回归样本草稿 v1

状态：AI 合成、未审阅的内部开发材料。

- 样本数：20；语义族：20；行为类别：10。
- 决策分布：clarify=2，polish=18。
- split：development=10，regression=10；按唯一 family_id 分配。
- 主要覆盖：澄清/澄清输出、格式契约、高风险事实保真、语气与非职场场景、多轮修订、否定/数量/时间/条件、中英混写、引用注入文本处理。
- tool_failure 未纳入：当前样本契约和润色流程没有工具调用/失败事件，不能用文本案例伪造工具执行轨迹。
- 每条样本均为 ai_assisted_draft 且 unreviewed；reference_output 不是金标。需要后续人工抽检事实、行为目标、边界和格式。
- 正式盲评贡献为 0；不得用于 blind-validate、blind-evaluate、微调或模型晋级。
- 逐条人工审阅模板见 `outputs/polish-regression-ai-supplement-review-2026-10-05/`，源样本哈希已绑定，人工结论栏保持空白。
- 冻结 v1、现有 W2 候选包及用户工作簿均未修改。
