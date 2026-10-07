# W2.3 近重复候选裁定：分析与建议

- 状态：**W2.3 真人裁定门槛未通过（`awaiting_human_review`）**。结构校验通过的是 AI 模拟审阅记录；`reviewer-a` 是模拟身份，不能锁定 W2.3 边界。模拟记录见 `datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl`（原名 `decisions.completed.jsonl`，已改名以免被误读为人工完成件），门槛状态见同目录 `gate-status.json`。
- 日期：2026-10-05
- 对象候选包：`datasets/polish-regression-w2-neardup-candidates-v3/`（`hxz-polish-reg-near-duplicate-candidates-v1`）
- 规则集：`hxz-polish-near-duplicate-char-trigram-v1`，字符 3-gram Jaccard，阈值 0.80（未校准，仅排队用）
- 父数据：`datasets/polish-regression-v1/cases.jsonl`（16 族、16 个案例）

---

## 1. 本次实际运行的命令与结果

| 步骤 | 命令 | 结果 |
|---|---|---|
| ① 空白基线 | `polish-regression-neardup-validate --report-dir …-v3 --decisions human-adjudication.template.jsonl` | `valid=false`，19 条 `decision-fields` / `decision-value` / `review-time` 问题；门槛未通过 |
| ② 导入工作簿 | `polish-regression-neardup-import-workbook --report-dir …-v3 --cases polish-regression-v1/cases.jsonl --workbook out/test-artifacts/w23-owner-authorized/near-duplicate-human-review.owner-authorized.xlsx --output datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl` | `candidate_count=19, imported_count=19`；来源哈希、案例证据与候选引用全部通过 |
| ③ 裁定校验 | `polish-regression-neardup-validate --report-dir …-v3 --decisions datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl` | `valid=true, candidate_count=19, adjudicated_count=19, issues=[]`；`reviewer_identity_verified=false` 为校验器固定输出 |

**读法：**第 ③ 步 `valid=true` 证明记录完整、血缘一致、候选覆盖无遗漏。校验器不验证身份、不判断语义正确性。本记录的 `reviewer_id` 为模拟审阅身份 `reviewer-a`。

---

## 2. 19 对候选的裁定建议

**全部 19 对建议值：`distinct_task_intent_same_backbone`。**

依据：每一对的两侧证据中，**只有 `Input` 行与 `Purpose` 行的 purpose 词不同**，其余部分（事实骨架、`Formality: 专业、克制`、`Explicit requirements` 为空、`Claims` 逐字相同）完全一致。即：同一事实骨架 + 不同任务意图。

样本一致性：19/19 都只有 1 处槽位差异；不存在"多处差异""骨架不同"或"证据不足"的候选。

### 2.1 九对跨 split 候选（重点核对）

| pair_id 尾号 | Jaccard | 左侧 | 右侧 | split 关系 | 裁定建议 |
|---|---:|---|---|---|---|
| `…22cdfb0a30894d0c` | 0.8367 | `000004` 致歉 | `000013` 婉拒 | development / regression | distinct_task_intent_same_backbone |
| `…31fc2c9f23872fc2` | 0.8367 | `000013` 婉拒 | `000014` 致谢 | regression / development | 同上 |
| `…f08e6444a914043a` | 0.8182 | `000006` 致谢 | `000044` 致歉 | development / regression | 同上 |
| `…ae16b12c1ec9a085` | 0.8039 | `000001` 提出请求 | `000013` 婉拒 | development / regression | 同上 |
| `…175d938f3f1957e6` | 0.8039 | `000004` 致歉 | `000011` 说明延期 | development / regression | 同上 |
| `…2f2edfe1ab3bfef8` | 0.8039 | `000007` 公开说明 | `000013` 婉拒 | development / regression | 同上 |
| `…8625a4e68a451c5c` | 0.8039 | `000010` 进度汇报 | `000013` 婉拒 | development / regression | 同上 |
| `…ea17cb16f6652cac` | 0.8039 | `000011` 说明延期 | `000014` 致谢 | regression / development | 同上 |
| `…9a6e00e0ebccf59e` | 0.8039 | `000013` 婉拒 | `000032` 问题分析 | regression / development | 同上 |

核对结论：

1. **9 对全部为同骨架、异 purpose**，与另外 10 对同 split 候选的性质完全相同，不存在额外的跨 split 语义风险。
2. 全部 9 对都只牵扯 **3 个 regression 案例**（`000013` 婉拒 出现 6 次、`000011` 说明延期 2 次、`000044` 致歉 1 次）。也就是说，跨 split 重复信号可完全归约为「3 个 regression 案例与若干 development 案例共享同一事实骨架」。
3. **没有任何一对判定为 `same_semantic_family`**，因此按当前族定义**不需要合族、不需要调整 split**，现有族级隔离不被推翻。

### 2.2 十对同 split 候选

`…e49798e2a857dfcd`(0.8750)、`…fa311ab1b1b327e3`、`…cf6794168bee8385`、`…462e6df667be7e50`、`…32890c87bee1c409`、`…33507537df68f046`、`…f24a107a18f2a719`、`…43481018049dfba6`、`…9269e0aff8c82725`、`…5b1dbf57de782b0f` —— 均为 `distinct_task_intent_same_backbone`。

---

## 3. 决定全部结论的那个政策前提

这 19 对之所以**全部**落在"同骨架、异 purpose"，是因为当前数据构建把**语义族定义为「事实骨架 × purpose」**：16 个案例 = 8 个 purpose × 2 个骨架 = 16 个族，每个族恰好对应一种 distinct input。

于是出现一个真正的分叉，它不是语义事实而是**项目政策选择**：

| 若族定义是… | 19 对结论 | 下游动作 |
|---|---|---|
| **骨架 × purpose**（当前实现，已确认采用） | 全部 `distinct_task_intent_same_backbone` | 无需合族、无需改 split，W2.3 可收口 |
| **骨架级**（purpose 只是族内变体） | 全部 `same_semantic_family` | 19 对全部合族 → 必须**新建数据版本**、重建 review packet 与 W4 报告，且 regression split 会与 development 合并，**破坏现有 split 隔离** |

因此：0.80 阈值实际聚出来的正是"同骨架、异 purpose"这一整类；**W2.3 的结果完全取决于预注册的族定义**，不能在看到裁定结果之后才改口径。项目所有者已于 2026-10-05 确认采用「骨架 × purpose」。

## 4. 必须随裁定一并记录的限制

字符 3-gram 聚类揭示的事实是：**事实骨架本身跨 development / regression 复用**。族级隔离成立，但骨架级不成立。这不是合族理由，但必须保留在 W2/W8 报告中，避免下游把"族隔离"误读为"输入完全独立"。

其余限制：阈值 0.80 未校准（`threshold_is_calibrated=false`）；分类器只做排队，`auto_merge_performed=false`；本裁定不改变任何 label、family 或 split。

## 5. 后续

1. 若日后改用骨架级族定义，本步骤 19 条结论全部作废，须走新版本重建路径。
2. 阶段 0 与 W2.3 无关，仍需另行满足：≥500 条获准、脱敏样本，独立双评，分歧第三方裁定，以及受控证据核验。W2.3 无论结论如何都不改变该门槛。

---

## 附：相关文件

| 文件 | 说明 |
|---|---|
| `datasets/polish-regression-w2-neardup-adjudication-v1/decisions.simulated-review.jsonl` | 19 条 AI 模拟审阅记录（**非**人工裁定证据） |
| `datasets/polish-regression-w2-neardup-adjudication-v1/gate-status.json` | 机器可读门槛状态：`w2_3_human_gate_passed=false` |
| `outputs/polish-regression-neardup-review-2026-10-05/near-duplicate-human-review.xlsx` | **权威人工输入端**，19 行决定字段仍为空白 |
| `out/test-artifacts/w23-owner-authorized/near-duplicate-human-review.owner-authorized.xlsx` | 已填写 D–G 列的工作簿副本；原始工作簿未被修改 |
| `out/test-artifacts/w23-review-evidence.json` | 工作簿审阅页 19 对证据快照 |
