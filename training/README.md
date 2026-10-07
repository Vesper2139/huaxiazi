# 历史模型实验记录

本目录保存过去的离线提示/输出协议实验、预测和少量训练原型代码，不属于话匣子桌面产品运行时。后续产品方向是直接采用已训练完成的开源 Instruct 模型，不在项目内训练基础模型或 LoRA/SFT。

## 入口

- 后续模型选择、运行时兼容和交付路线：[`docs/OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md`](../docs/OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md)
- 当前产品和数据目录地图：[`docs/PROJECT_MAP.md`](../docs/PROJECT_MAP.md)
- 现有合成集限制：[`datasets/polish-agent-v2/README.md`](../datasets/polish-agent-v2/README.md)
- 内部回归隔离约束：[`datasets/polish-regression-v1/README.md`](../datasets/polish-regression-v1/README.md)

## 工件边界

- `train_sft.py` 与 `requirements-sft.txt` 是旧的训练原型入口，不是当前开发目标；不要为产品接入新建训练依赖。
- `runs/<run-id>/` 保存可追溯的输入配置、数据哈希和预测/训练日志。现有 run 多为提示与输出格式诊断，Ollama 记录只代表历史对照实验。
- 这里的旧实验产物只作历史记录。不要将预测 JSONL 或 Ollama Modelfile 当作产品权重；不得把 Ollama 作为最终本地推理依赖。
- 不存放 API 密钥、用户原始历史、未脱敏个人信息或未授权语料。训练过程不自动上传用户内容。

## 数据使用边界

`datasets/polish-agent-v2/` 的 3,000 行是模板合成流程资产，已知输入和参考答案重复严重且 train/test 输入重叠。它可用于验证解析/数据流水线，不可作为模型优劣或泛化依据。后续模型对比须区分内部冒烟和正式质量评测。

未来如果项目负责人重新决定开展微调，应另立计划并明确授权；不从本历史目录默认启动训练。
