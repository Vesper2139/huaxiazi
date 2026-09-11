# 训练与调优实验记录

## 基线

- 模型：Ollama `qwen3:4b`
- 硬件：RTX 4060 8GB（当前约 4GB 被桌面进程占用）
- 数据：`hxz-synthetic-v1`，冻结 test 1,000 条
- 评测门禁：安全通过率 100%、直接可用率至少 85%、澄清召回率至少 80%

## 实验 A：请求级严格指令

增加“只输出最终答案、不要分析、不要前言”的指令。模型仍产生元分析，smoke 的直接可用率为 0；因此该方案不晋级。

## 实验 B：Ollama Modelfile 系统提示适配

创建 `huaxiazi-qwen3-strict`，将同样约束固化到 SYSTEM，并设置 temperature 0。模型仍产生元分析，未证明相对基线有改善；不晋级。

## 结论

当前证据支持“仅靠 prompt/system prompt 不足以满足输出协议”。下一轮应使用人工审核的高质量最终答案做 SFT/LoRA，或在产品层采用支持 reasoning/content 分离的模型 API。任何训练收益必须在 dev 切片上相对基线提升后，才允许读取冻结 test。

## 数据质量修正

审计发现旧版 polish gold 回显了输入中的控制指令（如“语气自然一点”和样本编号），会把元指令教给模型。已改为只保留事实和目标表达，并重新生成 `datasets/v1`；canonical 校验通过，后续实验不得混用旧哈希数据。

## 实验 D：gold 修正后的回归

绑定新数据哈希 `725f0a883d6d86055d4f877ef2a861cbc97d3eae983c1b6f7f9e875a0e150d3e`，对 qwen3:4b 运行 20 条 dev 迭代：`direct_usability_rate=0.65`、`safety_pass_rate=1`、`clarification_recall=1`，门禁失败，挖掘 7 条 DPO 失败样本。相较此前 20 条 smoke 的 0.70，没有证据表明 gold 修正单独提升了基线；下一轮应使用这些失败对进行训练，而不是继续修改评测口径。

## 实验 C：Ollama JSON mode + 结果字段抽取

使用 `/api/chat` 的 `format=json`，并从 `answer`/`required_output` 字段抽取用户可见答案。5 条 dev smoke 的 `missing_predictions=0`、`direct_usability_rate=1`、`safety_pass_rate=1`，门禁通过；exact match 为 0（生成式改写不应以字符串完全相等作为唯一质量指标）。该方案晋级为当前推理基线，但仍需完整 dev 和人工质量评分验证。

扩大到 20 条 dev（覆盖澄清样本）后，结构化抽取的 `missing_predictions=0`、`safety_pass_rate=1`、`clarification_recall=1`，但 `direct_usability_rate=0.7`，门禁失败。说明 JSON mode 解决了输出通道和澄清识别问题，但不能替代模型质量；应以失败样本为下一轮 SFT 采样重点。

## 实验 E：失败对 few-shot 适配

从失败 DPO 对中提取两个 polish 示例，构建 `huaxiazi-qwen3-fewshot`。5 条 dev smoke 在 JSON mode 下为 `direct_usability_rate=1`、`safety_pass_rate=1`、`clarification_recall=1`，门禁通过；但原 qwen JSON smoke 同样为 1，因此当前证据只能说明适配未退化，不能宣称有增益。需要扩大到完整 dev 并进行人工评分后再晋级。

## 实验 F：同切片配对回归

在同一数据哈希、同一前 50 条 polish dev 切片、同一 JSON 推理协议下，原 `qwen3:4b` 的 `direct_usability_rate=0.60`（20 条失败），`huaxiazi-qwen3-fewshot` 为 `1.00`（0 条失败）。这是提示适配的初步正向证据（+40 个百分点），但尚不能代表 prompt_optimize 或完整 dev；晋级前必须复测其余切片并做人工事实/自然度评分。

## 实验 G：prompt_optimize 配对回归

在同一数据哈希、同一 50 条 `prompt_optimize` dev 切片和同一 JSON 协议下，原 `qwen3:4b` 的 `direct_usability_rate=0.92`、`clarification_recall=0`、门禁失败；`huaxiazi-qwen3-fewshot` 为 `1.00`、`clarification_recall=1`、门禁通过。说明 few-shot 适配不仅未损害该模式，还改善了澄清切片；仍需完整 dev 和人工事实/可验收性评分确认。

## 实验 H：100 条分层 dev 回归

将 50 条 polish 与 50 条 `prompt_optimize` 合并为分层切片，在同一新数据哈希上评测 `huaxiazi-qwen3-fewshot`：`missing_predictions=0`、两种模式直接可用率均为 `1.00`、`safety_pass_rate=1`、`clarification_recall=1`，门禁通过。该结果支持进入完整 dev 回归，但仍不等同于人工质量评分或 SFT 训练结果。

## 审核门禁修正

已将审核深度纳入 validator：所有 dev/test 与高风险样本必须至少双人复核。重新生成 v1 后分布为 `dev-2=1000`、`test-2=1000`、`train-2=2000`（高风险）、`train-1=8000`，canonical 校验通过。

## Claim 保真门禁

评测器新增 claim preservation rate（主体、数量、时间必须保留，门槛 90%）。审计同时发现 prompt_optimize 样本错误挂载了 polish 事实 claim，已修正为仅在实际输入含事实时挂载；重新生成后 schema、审核和泄漏门禁均通过。

## 数据泄漏修正

首次运行 `leakage-check` 发现旧生成器在 split 间重用了模板文本，报告 2,000 条跨 split 输入重复。已加入全局记录扰动并重新生成 v1；当前 `leakage-check` 为 `clean=true, issue_count=0`。旧哈希上的模型实验全部降级为不可直接比较，必须在新哈希上重跑。

## 实验 J：无泄漏哈希配对复核

在新哈希 `e18c2e3f8a3b4a3191e21a55e7b85f78eaeaf281839e6cc84dfd63d3e4909f87` 上，对 10 条 polish + 10 条 prompt_optimize 做配对复核。原模型与 few-shot 候选在该小切片均达到 `direct_usability_rate=1`、`safety_pass_rate=1`、`clarification_recall=1`。该结果说明全局唯一扰动未破坏推理，但该样本量不足以证明候选增益，保留此前 100 条分层结果作为更强证据。

## 实验 I：审核规则变更后的分层回归

在新审核哈希 `b0c4647eb5447298fc9a0960f8984e2ca9499daf304b4d85f28f7a1564a81484` 上重新评测 100 条分层 dev，`huaxiazi-qwen3-fewshot` 得到 `missing_predictions=0`、两种模式直接可用率均为 `1.00`、安全通过率 `1.00`、澄清召回 `1.00`，门禁通过。该结果确认审核元数据变化未破坏候选推理基线。

## 实验 K：五层 Prompt 架构盲测（qwen3:4b）

固定 `qwen3:4b`、temperature=0、think=false、相同请求协议，对四个候选架构各运行 24 条 `architecture_dev` 样本（共 96 条）。安全率和格式率均为 1.00；`minimal-v1` 与 `policy-first-v1` 的决策率均为 0.9583，优于 `explicit-v1` 与 `output-contract-v1` 的 0.875。修正简洁度为连续预算利用率后，综合效用分别为：`minimal-v1=0.9505`、`policy-first-v1=0.9483`、`output-contract-v1=0.9251`、`explicit-v1=0.9219`。该结果只支持在当前模型和小型 dev 切片上优先复测 minimal/policy-first，不能外推为全量或全模型最优。

## 实验 K-3：Provider 结构化输出门禁

在相同 `policy-first-v1`、相同 24 条 dev 切片上启用 Ollama `format=json`，并在 user message 中明确只允许 `answer` 字段。结果：安全率 1.00、决策率 1.00、格式率 1.00、综合效用 0.9842。该结果证明输出契约必须同时由 Prompt 和 Provider schema 双重约束；JSON mode 是 Harness 门禁，不是模型训练。仍需在 120 条独立切片上复测后才能晋级。

## 实验 K-4：严格 JSON Schema 输出

发现 `format=json` 仍允许模型回显完整架构，因此将 Ollama `format` 升级为 `{answer: string}` 且 `additionalProperties=false` 的 JSON Schema，并在适配器侧只保留 `answer`。同一 policy-first 24 条切片结果：安全率 1.00、格式率 1.00、简洁度 1.00、决策率 0.8333、综合效用 0.9500。说明语法约束已稳定，但模型仍可能把指令复述为 answer；生产端还需增加 answer 内容质量门禁和失败重试，不能把 schema 合法等同于任务完成。

## 实验 K-5：policy-first-v2 单变量输出契约

保持 system/developer/skill/harness 和权重完全不变，只将 output_contract 改为“answer 只能填写面向用户的业务结果，禁止复述协议/架构字段/分析过程/格式要求”。同一 24 条 dev、严格 JSON Schema 下：安全率 1.00、决策率 0.875、格式率 1.00、简洁度 1.00、综合效用 0.9625，较 policy-first-v1 的 0.9500 提升 1.25 个百分点。该候选进入下一阶段 120 条独立切片复测。

## 实验 K-6：policy-first-v2 独立切片复测

在独立 120 条 dev 切片、相同严格 JSON Schema 和 `qwen3:4b` 参数下，v2 的安全率 1.00、决策率 0.6750、格式率 1.00、综合效用 0.9025；低于 v1 同规模切片的决策率 0.7667、综合效用 0.9078。24 条 smoke 的提升未能复现，v2 不晋级；该结果确认架构选择必须以独立切片稳定性为准，不能依据小样本调优。

## 实验 L：模型选择配对 smoke

固定 `policy-first-v1`、Strict JSON Schema、24 条 dev 切片和 `num_predict=128`，比较 `qwen3:4b` 与 `huaxiazi-qwen3-strict:latest`。两者均达到安全率 1.00、格式率 1.00、决策率 0.8333、综合效用 0.9500；当前没有证据支持 strict Modelfile 相对基础模型有增益。模型路由仍应依据任务风险和上下文策略，而不是仅凭模型名称晋级。

## 实验 K-2：扩大 dev 与评测规则校准

对 `minimal-v1` 与 `policy-first-v1` 各运行 120 条 dev（`num_predict=128`）。同时修正评测器：拒绝泄露时提及“系统提示”不再误判为泄露，仅披露形态或凭证样式才计入安全失败；澄清决策增加“确认/明确/具体”等等价表达。校准后 `minimal-v1`：安全率 0.8500、决策率 0.7750、格式率 0.8333、效用 0.8392；`policy-first-v1`：安全率 0.9833、决策率 0.7667、格式率 0.8333、效用 0.8900。当前证据支持 `policy-first-v1` 进入下一阶段，但格式门禁仍只有 83.33%，必须先优化 JSON 输出契约并在独立切片复测，不能直接冻结 test。

## Ollama 本地 API 冒烟（追加）

- 时间：2026-09-07
- Endpoint：`http://127.0.0.1:11434/api/chat`
- 基线模型：`qwen3:4b`
- 严格模型：`huaxiazi-qwen3-strict:latest`
- 请求：将“会议将在明天下午三点开始。”改得更自然。
- 基线观察：即使设置 `think=false` 与“只输出最终结果”，仍产生分析性前缀，说明自然语言提示不足以保证输出通道约束。
- 严格观察：使用 JSON schema（仅 `answer` 字段）后返回 `{"answer":"会议明天下午三点开始。"}`，通过结构化输出边界。

结论：本地模拟 API 验证支持当前 `policy-first-v1 + strict schema + validator` 组合；后续实验应把“是否泄露分析/是否满足 schema”作为独立质量门，而不能只看语义改写质量。

## Ollama 架构 dev smoke（20 条）

- 模型：`huaxiazi-qwen3-strict:latest`
- 架构：`explicit-v1`
- 样本：`hxa-v1-dev-010001` 至 `010020`，仅 dev split
- 安全率：1.00；格式率：1.00；事实锚点率：1.00；决策率：0.45；约束率：0.05；综合效用：0.8350
- 结论：该架构未达到晋级门禁。结构化输出和安全边界有效，但用户约束遵循明显不足；下一轮应优先比较 `policy-first-v1` 与 `minimal-v1`，并针对约束失败样本做定向修正。

对照 `policy-first-v1` 的同位置 20 条 dev smoke：安全率 1.00、格式率 1.00、事实锚点率 1.00、决策率 0.20、约束率 0.05、综合效用 0.7600，同样未达到晋级门禁。该结果表明当前瓶颈更可能是本地模型对数据集任务标签/约束语义的理解，而不是单一架构层措辞；应先审查样本标签与请求模板，再进行大规模权重搜索。

## 数据集质量升级

架构数据生成器已将各场景从单一占位语句升级为 4 组真实业务语境模板，并保留跨 split 唯一案例编号；重新生成 train/dev/test（10,000/1,000/1,000），三份校验均为 0 问题。此前基于旧输入文本的 smoke 评测结果标记为历史结果，不得与新数据集直接比较；后续必须重新跑 dev。

## 新数据集五架构对照 smoke（各 20 条 dev）

使用 `huaxiazi-qwen3-strict:latest`、temperature 0、JSON Schema，初始结果如下：`explicit-v1` 效用 0.7900 / 决策 0.30；`policy-first-v2` 0.7600 / 0.20；`policy-first-v1` 0.7600 / 0.20；`output-contract-v1` 0.8350 / 0.45；`minimal-v1` 0.7750 / 0.25。五个候选安全率、格式率、事实锚点率均为 1.00，但约束率均为 0.10，均未达到晋级门禁。

当前最强 smoke 候选是 `output-contract-v1`，但样本量不足且决策/约束下置信界仍低，不能冻结 test。约束率在所有候选上同步偏低，下一步优先修订约束标签与评测映射，并增加人工审核样本，而不是继续微调层权重。

评测器随后修正了“事实/直接”约束的语义判定：对直接回答样本不再要求输出必须逐字包含这些词，而是检查非空、无元指令回显和禁用内容。修正后约束率为 explicit 0.30、policy-v2 0.25、policy-v1 0.25、output-contract 0.45、minimal 0.30；该修正只改变测量有效性，不改变模型输出或晋级门禁。

## output-contract-v1 扩展 dev（100 条）

在新数据集上扩大到 100 条 dev：安全率 1.00、格式率 1.00、决策率 0.54、约束率 0.47、事实锚点率 0.94、综合效用 0.8620。安全/格式下置信界达到约 0.963，但决策下置信界 0.443、约束下置信界 0.375、锚点下置信界 0.875，仍未达到晋级门禁。该结果支持其作为当前候选基线，但不支持冻结 test。

同等条件下 `explicit-v1` 扩展 100 条 dev：安全率 1.00、格式率 1.00、决策率 0.42、约束率 0.36、事实锚点率 0.94、综合效用 0.8260。其决策下置信界 0.328、约束下置信界 0.273，低于 output-contract-v1；暂时支持 output-contract-v1 的相对优势，但仍不足以晋级。

同等条件下 `policy-first-v1` 扩展 100 条 dev：安全率 1.00、格式率 1.00、决策率 0.35、约束率 0.33、事实锚点率 0.94、综合效用 0.8050。其表现低于 output-contract-v1 和 explicit-v1，说明“先讲策略”并不自动提升本地模型的任务决策能力；策略层仍需配合更清晰的任务契约和经过审核的示例。

## 本轮隔离 smoke（每个架构 1 条 dev）

使用 `huaxiazi-qwen3-strict:latest` 和 JSON 模式，通过 `run-ollama-architecture-matrix.ps1` 对五个候选各运行 1 条 dev。五个架构均成功生成预测，输出只写入临时实验目录；未读取或修改生产配置，未执行线上 Provider 请求。
