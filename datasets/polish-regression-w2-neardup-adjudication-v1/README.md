# W2.3 近重复候选裁定记录 v1

> **W2.3 真人裁定门槛：未通过（`awaiting_human_review`）。**
> 本目录的 JSONL 是 **AI 角色扮演产出的模拟审阅记录**，`reviewer_id` = `reviewer-a` 为模拟身份。
> 它**不能**用于锁定 W2.3 边界，**不能**计为人工裁定证据；W3.3 与 W4 补样因此**未解锁**。
> 权威的人工输入端是仍为空白的工作簿：`outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review.xlsx`（19 行决定字段均未填写）。
> 机器可读状态见 `gate-status.json`。

2026-10-07 检查到同目录另有一份填写副本 `near-duplicate-human-review - 副本.xlsx`。它包含 19 行决定，但 19 条理由均为“无非空理由”占位语；`polish-regression-neardup-import-workbook` 因 `decision-incomplete` 拒绝导入，未生成裁定文件。该副本不改变上面的权威原件状态，也未被认定为有效人工裁定；需要审阅者补充每对的具体理由后再导入。

- 日期：2026-10-05
- 上游候选包：`../polish-regression-w2-neardup-candidates-v3/`（19 对候选，含 9 对跨 split）
- 阶段 0 贡献：**0**；不进入盲评集。

## 文件

| 文件 | 性质 |
|---|---|
| `decisions.simulated-review.jsonl` | AI 模拟审阅记录（原名 `decisions.completed.jsonl`；改名以避免被误读为已完成的人工裁定） |
| `gate-status.json` | 机器可读门槛状态 |
| `README.md` | 本文件 |

## 模拟记录的结论（仅作分析参考）

| 项 | 值 |
|---|---|
| 裁定对数 | 19（跨 split 9） |
| 决定 | 全部 `distinct_task_intent_same_backbone` |
| 合族 / split 调整 | 无 / 无 |
| `reviewer_id` | `reviewer-a`（模拟身份） |
| 结构校验 | `valid=true`、`candidate_count=19`、`adjudicated_count=19`、`issues=[]`；`reviewer_identity_verified=false` |

19 对候选两侧证据仅 `purpose` 槽位不同：事实骨架、`formality`「专业、克制」、`explicit requirements`（空）与 `claims` 逐字一致。按族定义「事实骨架 × purpose」，它们属于同一骨架下的不同任务意图，保留为不同语义族。9 对跨 split 候选逐对核对均为字符 3-gram 启发式的误报（仅牵扯 3 个 regression 案例：`000013` 婉拒 6 次、`000011` 说明延期 2 次、`000044` 致歉 1 次），无需合族、无需调整 split。

**上述结论未经真人确认，因此不构成 W2.3 边界锁定。**

## 校验含义

`polish-regression-neardup-validate` 的 `valid=true` 只表示记录格式、候选引用与覆盖完整；该校验器不验证身份（`reviewer_identity_verified` 固定为 `false`），也不判断语义正确性。

## 复现

```powershell
# 工作簿副本（D–G 列已填写，模拟身份）
out/test-artifacts/w23-owner-authorized/near-duplicate-human-review.owner-authorized.xlsx

dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-neardup-import-workbook `
  --report-dir .\datasets\polish-regression-w2-neardup-candidates-v3 `
  --cases .\datasets\polish-regression-v1\cases.jsonl `
  --workbook .\out\test-artifacts\w23-owner-authorized\near-duplicate-human-review.owner-authorized.xlsx `
  --output .\datasets\polish-regression-w2-neardup-adjudication-v1\decisions.simulated-review.jsonl

dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- polish-regression-neardup-validate `
  --report-dir .\datasets\polish-regression-w2-neardup-candidates-v3 `
  --decisions .\datasets\polish-regression-w2-neardup-adjudication-v1\decisions.simulated-review.jsonl
```

导入器与校验器均为 create-only / 只读上游：原始工作簿、候选包与冻结 `cases.jsonl` 均未被修改。

## 通过 W2.3 所需的人工步骤

1. 真人审阅者在**原始工作簿**的 19 行上独立填写 `decision`、`reviewer_id`、UTC 时间与 `rationale`（优先复核 9 对跨 split）。
2. 用安全导入器生成**新**输出文件（不得覆盖本目录的模拟记录）。
3. 运行 `polish-regression-neardup-validate` 并通过。
4. 若任何裁定要求合族或调整 split，必须新建数据版本并重建下游 review packet 与 W4 报告。

## 与阶段 0 的关系

本步骤与阶段 0 授权盲评门槛无关。阶段 0 另需 ≥500 条获准、脱敏样本，独立双评，分歧第三方裁定与受控证据核验；W2.3 无论结论如何都不改变该门槛。
