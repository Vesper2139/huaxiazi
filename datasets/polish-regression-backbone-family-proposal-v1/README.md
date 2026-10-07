# 骨架级语义族合并影响草案 v1

- 状态：计算草案，非正式裁定，非训练/计分集。
- 输入：负责人提供的近重复审阅副本中的 19 个 same_semantic_family 决定。
- 原因列：19 条均为占位语 无非空理由；不把它们当成有效人工理由。
- 结果：16 个旧案例投影为 8 个候选骨架族，其中 2 个候选族横跨 development/regression。
- 为避免同一候选族跨 split 泄漏，本草案将任何含 regression 案例的合并组整体留在 regression；由此 development 从 2,414 条变为 802 条，regression 从 586 条变为 2198 条；1612 条 development 变体被移至 regression。
- 该划分只用于展示影响，不代表评测统计有效性。regression 只剩 2 个候选族，无法替代 500 条授权盲评集。
- 冻结 v1、已有 AI 标签草稿和 review packet 均未改动。阶段 0 贡献为 0。

清单哈希绑定 `case-family-map.jsonl`、`groups.jsonl` 与 `impact.json`。
