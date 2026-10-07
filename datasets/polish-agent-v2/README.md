# 润色 Agent 行为训练集 v2

本数据集从项目已有 `hxz-synthetic-v1` 的 6,000 条 `polish` 样本中抽取并重新组织为 3,000 条 Agent 行为训练数据，规模处于 1,500～5,000 的目标区间。

## 目标行为

- 识别用户是否要求润色，而不是擅自回答事实问题。
- 保留原文事实、立场、情绪强度、关系和承诺边界。
- 根据场景、对象、渠道和正式程度调整表达。
- 只在关键事实缺失或互相冲突时提出澄清。
- 默认输出可直接使用的最终文本，不附加不必要的分析。

## 文件

- `canonical.jsonl`：可审计记录，包含 context、claims、expected_decision、rubric、review 和 provenance。
- `sft.jsonl`：转换为 user/assistant messages 的 SFT 视图。
- `manifest.json`：来源哈希、切分数量和限制说明。

使用专用审计命令核对 v2 canonical、SFT、manifest 和原始 v1 来源文件：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-agent-validate --input .\datasets\polish-agent-v2\canonical.jsonl --sft .\datasets\polish-agent-v2\sft.jsonl --manifest .\datasets\polish-agent-v2\manifest.json --source .\datasets\v1\canonical.jsonl
```

该命令检查文件哈希、来源 ID/内容映射、模板族切分与 SFT 目标对齐。它验证合成资产的结构和血缘，不验证人类质量，也不产生外部盲评证据。

## 切分策略

每个旧模板族抽取 125 条，共 24 个旧模板族。18 个模板族进入 train（2,250 条），3 个进入 dev（375 条），3 个进入 test（375 条）。这只是按生成器模板标签切分，不能据此声称测试输入与训练输入语义隔离或证明 Agent 学会了可泛化的行为规则：逐行审计发现 test 的 5 种不同输入全部与 train 重叠，且参考成稿高度重复。详见 [evaluation-plan.md](evaluation-plan.md) 与内部回归集计划中的基线审计。该 split 仅可用于数据流程核对，不作为行为泛化或模型质量证据。

## 使用边界

这是项目自有合成数据的行为训练集，不是经过人工专家逐条审核的真实语言语料。正式 SFT 前应至少完成：人工抽检、自然度评分、事实保真检查、场景覆盖统计、近重复检测，以及用真实脱敏样本建立独立评测集。

本地模型选型与评测方案见：

- [local-model-research-2026-09-27.md](../../docs/local-model-research-2026-09-27.md)
- [model-candidates.json](model-candidates.json)
- [evaluation-plan.md](evaluation-plan.md)

