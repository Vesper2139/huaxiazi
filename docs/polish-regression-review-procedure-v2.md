# W3.3 人工审阅结果回收与验收

## 本步骤边界

输入是 `datasets/polish-regression-review-packet-v2/` 中的冻结审查包、`polish-regression-v2-draft/` 草稿标签和工作簿副本。目标是核验 16 个族代表是否完成了独立首轮审阅及草稿对照裁定。此验证器只检查覆盖、来源未被改动、字段完整性、理由、时间戳和审阅者独立性；它不代替判断标签是否正确，不写入已验证标签，也不解锁模型质量门。

## 人工结果文件

保留审查包原件不变，在新的受控目录复制两份 JSONL：

- `source-first-pass.completed.jsonl`：从 `source-first-pass.jsonl` 复制，只填写 `independent_first_pass` 中的审阅者、UTC 时间、14 个独立字段判断、理由和不确定项。不要改案例、来源、输入、claims、决策或参考成稿等来源字段。
- `human-decisions.completed.jsonl`：从 `human-decisions.template.jsonl` 复制，填写每族裁定。每个族必须有 `reviewer_id`、UTC `reviewed_at_utc`、总体结论与理由、骨架结论/最终 ID/理由、参考复用结论/理由，以及除 `reference_output_reuse` 外的全部字段结论/最终值/理由；参考复用使用独立的 `reference_reuse_decision`。

只允许 `accept`、`revise`、`reject`、`uncertain` 等 Schema 列出的结论。`accept` 的最终值必须和草稿值一致；修订值须明确记录。时间戳必须为 `Z` 或 `+00:00` 的 UTC 时间。AI 草稿作者不得填写审阅者 ID；无法判断的项目保持 `uncertain` / `unresolved`，不得用默认值补齐。

Excel 工作簿用于阅读和填写辅助记录，不是机器验收的权威数据；将填写内容按上述 JSONL 结构转录并保留每项的理由。首轮判断记录完成后再打开草稿对照页。

## 验收命令

从仓库根目录执行：

```powershell
dotnet run --project DatasetBuilder -- polish-regression-review-validate `
  --packet-dir datasets/polish-regression-review-packet-v2 `
  --first-pass path/to/source-first-pass.completed.jsonl `
  --decisions path/to/human-decisions.completed.jsonl `
  --labels datasets/polish-regression-v2-draft/labels.jsonl `
  --draft-manifest datasets/polish-regression-v2-draft/manifest.json
```

只有 `valid=true` 且 `independent_first_pass_count=16`、`completed_decision_count=16` 才证明审阅覆盖完整、流程约束和血缘校验通过。`approval_candidate_count` 只表示满足“所有字段接受、骨架确认、参考复用可接受”条件的候选族数，不等同于已发布金标；任何 `revise`、`reject`、`uncertain`、`unresolved` 均需要后续处理或保留为未决。

审阅验证报告的 `phase_0_gate_contribution` 永远为 0。后续若要把接受结果物化为新的内部标注版本，必须单独创建版本化最终化步骤并检查 W3.4 分歧仲裁状态；不得覆盖原始 v1、AI 草稿 v2 或审查包。
