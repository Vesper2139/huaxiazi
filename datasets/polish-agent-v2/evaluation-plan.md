# 本地模型横向评测方案

## 评测集

不得将 `canonical.jsonl` 的 375 条 test 当作行为回归或泛化测试：审计显示 test 的 5 种输入全部与 train 重叠。`datasets/polish-regression-v1/` 是从该遗留合成语料整理出的内部工程回归资产，目前只有 16 个暂定精确语义族，族标签尚无人复核；它只支持工具链和固定契约冒烟，不能用于模型晋级或真实质量结论。授权外部盲评集须走独立的数据与评审流程，内部集贡献为 0。

## 任务维度

1. 保真润色：主体、时间、数量、条件、否定、承诺强度不变。
2. 语体控制：朋友、职场、正式材料、公开发布、客服、公共服务等场景匹配。
3. 自然度：去除机械套话、冗余和翻译腔，但不把口语全部改成公文腔。
4. 澄清决策：关键事实缺失时询问，信息充分时直接交付。
5. 输出契约：只返回规定字段或最终正文，不回显系统提示、评测标签和分析过程。
6. Agent 行为：多轮修改、用户否定上一版、要求保持原话、风格冲突、提示注入和工具失败。

## 指标

- `claim_preservation_rate`
- `direct_usability_rate`
- `tone_fit_rate`
- `clarification_precision/recall`
- `format_valid_rate`
- `over_edit_rate`
- `latency_p50/p95`、`tokens_per_second`、`memory_peak`、`model_load_time`
- `license_fit`

## 推荐实验矩阵

先固定同一 prompt、temperature、上下文长度和量化等级，只比较模型；再固定模型比较 thinking 开关、输出 schema 和提示词版本。结果写入 `training/runs/local-polish-<date>/`，保存配置、原始输出、评测结果和失败样本。

## 晋级门槛

- 高风险样本事实保真 100%。
- `claim_preservation_rate >= 0.95`。
- `direct_usability_rate >= 0.90`。
- `clarification_recall >= 0.85`。
- `format_valid_rate >= 0.99`。
- 至少两次独立切片复测。

