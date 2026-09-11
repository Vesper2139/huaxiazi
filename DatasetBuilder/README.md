# 话匣子模型调优数据流水线

## 提示架构调优（当前目标）

本项目的“调优”首先针对 agent 架构，而不是本地模型权重。`architecture-dataset` 生成用于提示层消融、权重搜索和 harness 门禁评估的数据：每条记录都显式包含 `system`、`developer`、`skill`、`harness`、`output_contract` 五层的具体措辞、权重向量、场景、风险、失败模式、期望决策和 gold 输出。

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-dataset --output .\datasets\architecture-v1
```

输出 `architecture_train.jsonl`（候选架构拟合集）、`architecture_dev.jsonl`（选择变体/权重的开发集）、`architecture_test.jsonl`（冻结的最终架构测试集）和 `manifest.json`。默认规模仍为 10,000/1,000/1,000；场景覆盖普通请求、歧义、提示注入、高风险事实、工具失败、格式违规、超长上下文、Skill 冲突、记忆冲突和工具并行。生成器会校验五层权重总和为 1、场景风险标注、场景与 `expected_decision` 的语义一致性、跨 split 输入去重，以及 `generalization_family` 词族隔离（dev/test 使用训练集未出现的措辞族）。`seed` 会确定性地参与场景、变体和措辞族抽样：同 seed 完全复现，不同 seed 产生不同样本，可用于多种子稳定性实验。记录还包含 `required_constraints`、`forbidden_constraints`、`fidelity_anchors`，manifest 会声明字段、场景和 split 隔离策略。该数据集不是 SFT 训练文件，也不会触发本地模型权重训练。

`datasets/architecture-v1/candidate-configs.json` 提供四个可直接用于实验的候选架构（完整五层措辞与权重），可作为 dev 阶段的起始搜索空间。
批量展开前会严格校验候选 ID 唯一、五层措辞非空、权重有限且非负并总和为 1；不合格配置直接拒绝，避免生成不可比较的实验批次。

将候选架构展开为供真实 Provider 批量推理的请求（不包含 gold，避免评测泄漏）：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-batch --input .\datasets\architecture-v1\architecture_dev.jsonl --candidates .\datasets\architecture-v1\candidate-configs.json --output .\datasets\architecture-v1\architecture_dev_requests.jsonl
```

`architecture-batch` 明确拒绝 `test` split；候选搜索只能使用 train/dev，冻结 test 只能在架构锁定后一次性评估，防止测试集反馈造成选择偏差。

Provider 返回后，将每行整理为 `id/architecture_id/split/output`，再使用 `architecture-evaluate` 计算候选架构得分。`split` 用于审计请求来源，Provider 脚本拒绝 test 或缺失 split，评测器拒绝与 gold 不匹配的 split。`format_violation` 场景按严格 answer-only JSON schema 评分，而不是仅检查 JSON 是否可解析。评测同时报告 `constraint_rate`/`anchor_rate` 及 Wilson 下界，个性化约束或事实锚点不稳定的候选不能晋级；Pareto 前沿也将这两项纳入支配关系。
评测器会按 `(architecture_id, id)` 去重并报告 `coverage_rate`；缺失或重复预测不会抬高分数，覆盖率低于 99% 的候选自动禁止晋级。
使用 `architecture-compare` 可对两个候选在相同样本 ID 上做成对比较，输出胜负、平局、胜率和 Wilson 下界；只有至少 20 个非平局样本且左侧胜率下界高于 50% 才标记为显著，避免仅凭独立均值选择架构。

如果本机使用 Ollama，可直接运行 `training/invoke-ollama-architecture-eval.ps1`；它只做推理，不训练或修改模型权重：

```powershell
powershell -ExecutionPolicy Bypass -File .\training\invoke-ollama-architecture-eval.ps1 -Model qwen3:4b -Limit 24
```

推荐实验顺序：固定基础模型和工具环境，先在 train 上筛掉明显失败的层级措辞，再只用 dev 搜索层权重/组合，锁定候选架构后一次性在 test 上评估；任何 test 结果都不得反馈回候选集。

RAG 的稠密检索可以通过 `IEmbeddingIndex` 注入。内置 `InMemoryEmbeddingIndex` 用于离线验证向量维度、有限值和余弦相似度；生产环境只需替换查询编码器/索引实现，不改变 `HybridContextRetriever` 的词法+稠密混合排序契约。

架构预测文件每行格式为 `{"id":"hxa-v1-test-...","architecture_id":"policy-first-v2","output":"模型最终输出"}`。可用以下命令计算安全、决策、格式、简洁度和综合效用，并标记 Pareto 最优候选：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-evaluate --gold .\datasets\architecture-v1\architecture_dev.jsonl --predictions .\architecture-predictions.jsonl --output .\architecture-report.json
```

该项目先固定“标准数据层”，再由训练侧绑定具体基础模型或 LoRA 适配器。默认生成：

- `train` 10,000 条、`dev` 1,000 条、冻结 `test` 1,000 条
- `polish` 与 `prompt_optimize` 各 50%
- `sft.jsonl`、`preference.jsonl` 和可审计的 `canonical.jsonl`

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- generate --output .\datasets\v1
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- validate --input .\datasets\v1\canonical.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- leakage-check --input .\datasets\v1\canonical.jsonl
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- report --input .\datasets\v1\canonical.jsonl --output .\datasets\v1\report.json
# predictions.jsonl 每行格式：{"id":"hxz-v1-...","output":"模型输出"}
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- evaluate --gold .\datasets\v1\canonical.jsonl --predictions .\predictions.jsonl --output .\evaluation.json
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- mine-failures --gold .\datasets\v1\canonical_dev.jsonl --predictions .\predictions.jsonl --output .\training\dpo-failures.jsonl
```

生成使用固定 seed，并在 `manifest.json` 写入完整数据集哈希。任何模板、比例或校验规则变更都应提升数据集版本并重新冻结测试集。当前样本为项目自有合成数据；在接入真实用户数据或教师模型前，必须经过脱敏、授权、双人抽检与独立测试集去重。

该流水线完成数据构造、校验和导出，不宣称已经完成某个具体基础模型的训练。实际 SFT/LoRA 训练需要另行提供基础模型、训练运行时、显存预算和超参数配置，并以 `dev` 指标门禁后才允许评测 `test`。

评测器会报告 exact match、直接可用率、安全通过率和澄清召回率；安全未满分、可用率低于 85% 或澄清召回率低于 80% 时返回非零退出码，适合作为训练流水线门禁。

`mine-failures` 会把可识别的低质量预测转成下一轮 DPO 偏好对：gold 为 `chosen`，失败预测为 `rejected`，并记录 `unsafe` 或 `not-directly-usable` 原因。它只处理实际存在的预测，不会把缺失结果伪造成负例。

可用 `training/iterate.ps1` 运行一轮可追溯迭代（默认 20 条 dev smoke）：

```powershell
powershell -ExecutionPolicy Bypass -File .\training\iterate.ps1 -Model qwen3:4b -Limit 20
```

每轮会保存 validation、gold、predictions、evaluation 和 dpo-failures，作为下一轮训练/调参的证据链。

真实 SFT 入口位于 `training/train_sft.py`。它只接受 HuggingFace 模型目录/Hub ID，遇到 `qwen3:4b` 这类 Ollama 名称会拒绝运行；先安装 `training/requirements-sft.txt`，再执行：

```powershell
python .\training\train_sft.py --model <hf-model-dir> --train .\datasets\v1\sft_train.jsonl --output .\training\artifacts\lora-v1
```

训练前可运行 `powershell -File .\training\preflight.ps1 -Model <hf-model-dir>`，检查依赖、CUDA、GPU 显存、磁盘和训练文件是否就绪。

若本机已安装 Ollama，可用 `training/invoke-ollama-eval.ps1` 通过 `/api/chat` 生成真实模型预测（显式设置 `think=false`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\training\invoke-ollama-eval.ps1 -Model qwen3:4b -Limit 20
```

脚本支持完整 test 集，但应先以小样本 smoke、再跑 dev、最后跑冻结 test。评测器会把缺失预测按失败处理；本次 1 条 qwen3:4b smoke 预测在完整 test 门禁上得到 `passed=false`，这是预期的保守行为，不代表模型完整测试分数。
