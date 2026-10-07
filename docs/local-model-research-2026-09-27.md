# 本地模型研究：润色与提示词工程

更新时间：2026-09-27

## 结论先行

在不使用外部 API、以中文润色和提示词工程为主的前提下，建议采用分层候选：

1. **主线基线：Qwen3-8B-Instruct / Qwen3-14B**。中文、语体调整、指令遵循、结构化输出和本地生态之间平衡较好；Qwen3 支持 thinking/non-thinking 切换，并支持 Ollama、llama.cpp、Transformers、vLLM、SGLang 等本地路径。
2. **低显存版本：Qwen3-4B-Instruct**。适合桌面端润色、短上下文和高响应速度；复杂提示词优化、长文本拆解和多轮规划需单独评估。
3. **Agent 备选：GLM-4.7-Flash**。30B-A3B 稀疏 MoE，适合工具使用、推理和长上下文；但本地框架兼容性和量化支持更敏感。
4. **结构化推理备选：gpt-oss-20b**。官方定位为约 16GB 内存级别可运行的开放权重模型，具备可调 reasoning effort、工具调用和 Structured Outputs；Harmony 格式、中文自然度和运行时适配必须单独验证。
5. **高端对照：DeepSeek-V4-Flash**。适合作为高端 Agent 和长上下文参照，但 284B 总参数、约 13B 激活参数，不是普通单卡桌面模型首选。
6. **外部对照：Mistral Small、Gemma 3、Llama 4**。适合测试跨语言、英文提示词和许可证差异；对中文保真润色不能先验视为优于 Qwen/GLM。

## 多维比较

评分是本项目初筛，不是公开 benchmark 的复述；最终以本地 `polish-agent-v2` 评测为准。

| 模型 | 定位 | 中文润色 | Prompt/Agent | 本地门槛 | 开放性/许可 | 判断 |
|---|---|---:|---:|---|---|---|
| Qwen3-4B-Instruct | 低延迟桌面润色 | 5 | 3 | 低 | Apache-2.0，开放权重 | 低显存首基线 |
| Qwen3-8B-Instruct | 通用本地主力 | 5 | 4 | 中低 | Apache-2.0，开放权重 | 默认首选 |
| Qwen3-14B | 高质量润色 | 5 | 4 | 中 | Apache-2.0，开放权重 | 24GB 级优先 |
| Qwen3-30B-A3B | MoE Agent/推理 | 5 | 5 | 中高 | Apache-2.0，开放权重 | 有余量时测试 |
| GLM-4.7-Flash | 中文 Agent/长上下文 | 5 | 5 | 中高 | 以具体权重版本为准 | Agent 对照组 |
| gpt-oss-20b | 推理/工具/结构化输出 | 3-4 | 5 | 中 | Apache-2.0，开放权重 | 结构化推理对照 |
| DeepSeek-V4-Flash | 高端 Agent/长上下文 | 4-5 | 5 | 极高，多 GPU | MIT，开放权重版本 | 服务器级参照 |
| Mistral Small 3.2/4 | 多语言、低延迟 | 3 | 4 | 中 | 需核对具体版本 | 跨语言对照 |
| Gemma 3 12B/27B | 多语言/多模态 | 3-4 | 3-4 | 中 | Gemma 条款 | 多语言对照 |
| Llama 4 | 英文/多模态 Agent | 2-3 | 4 | 高 | Community License | 不作中文主力 |
| InternLM3-8B | 中文基线 | 4 | 3-4 | 低中 | 以模型卡为准 | 回归对照 |

## 纵向比较：按硬件与任务

### 桌面低显存

候选：Qwen3-4B-Instruct、InternLM3-8B 量化版、Gemma 小尺寸。重点是短文本润色、语气调整、拒绝/道歉/说明和简单提示词改写。风险是长文本、事实锚点和多约束任务下降。

### 单卡中等显存或 CPU+GPU 混合

候选：Qwen3-8B/14B、gpt-oss-20b、Mistral Small、GLM-4.7-Flash 量化版。适合长段落改写、结构化输出、澄清决策、Agent skill 执行和提示词优化。普通润色建议关闭 thinking，复杂“抽取—判断—改写”任务再提高推理预算。

### 多卡/服务器

候选：Qwen3-30B-A3B、Qwen3-235B-A22B、GLM-4.7、DeepSeek-V4-Flash。适合复杂提示词程序、跨文档风格迁移、工具编排和长上下文，但对普通润色未必有投入产出优势。

## 内外模型差异

- 国内模型更适合作为中文主线：中文语体、职场/政策表达和本地生态更贴近本项目。
- 海外开放权重模型适合作为英文指令、跨语言、结构化输出和 Agent 能力对照；中文细微语气必须实测。
- “开放权重”不等于“完全开源”：要分别记录权重、代码、数据许可和商用/再分发限制。
- 推理能力强不等于润色质量高。润色核心指标是事实保真、语体匹配、情绪不越界、可直接使用和不擅自补信息。

## 推荐决策

当前先测 **Qwen3-4B、Qwen3-8B、InternLM3-8B**；若显存和内存允许，再加入 **Qwen3-14B、GLM-4.7-Flash**。如果重点转向提示词工程/Agent，保留 **gpt-oss-20b** 作为结构化推理对照；DeepSeek-V4-Flash 只作为高端服务器参照。

## 来源

- [Qwen3 官方 GitHub](https://github.com/QwenLM/Qwen3)
- [GLM 官方 GitHub](https://github.com/zai-org/GLM-4.5)
- [OpenAI gpt-oss 官方 GitHub](https://github.com/openai/gpt-oss)
- [DeepSeek-V4-Flash 官方 Hugging Face](https://huggingface.co/deepseek-ai/DeepSeek-V4-Flash-0731)
- [Mistral 模型文档](https://docs.mistral.ai/models)
- [Gemma 3 官方页面](https://deepmind.google/models/gemma/gemma-3/)
- [Llama 4 官方模型卡](https://github.com/meta-llama/llama-models/blob/main/models/llama4/MODEL_CARD.md)
- [InternLM 官方 GitHub](https://github.com/InternLM/InternLM/)

## 2026-10-02 官方资料复核与决策修订

本节只更新可核实的候选身份和集成风险，不把模型厂商 benchmark 当作本产品结论。盲评数据、Windows 目标机测试和项目中文任务上的盲评仍是准入依据。

### 本地候选

- **Qwen3.5-4B 与 Qwen3.5-9B 可作为本地候选，不预先宣布质量优于现有 Qwen3-4B。** Qwen 官方 Hugging Face 卡标记 Apache-2.0，并发布 Transformers 格式权重；卡片列出 262,144 原生上下文、扩展到 1,010,000 的说法。该模型卡的多项 benchmark 是模型方报告值，和本项目中文保真润色/提示词优化任务不等价。4B 仓库权重约 19.3 GB，主安装包不能直接承载；本计划应记录实际 GGUF 量化文件哈希/大小、转换来源、模板版本、量化损失和 Windows 运行时兼容性。
- **Qwen3.8-27B 应列为高资源本地候选，同时也有云端服务路径。** 官方 Hugging Face 卡提供 Transformers 格式模型权重，标记 Apache-2.0，并给出 vLLM、SGLang 等部署指引；阿里云 Model Studio 另提供托管推理。因此此前“仅云端”的推断不成立。官方权重和许可足以列入候选研究，但还不能证明本项目的 GGUF 转换来源、llama.cpp 模板/算子兼容、量化质量、Windows 性能和硬件可达性。它不进入首轮低资源包；仅在目标硬件矩阵及盲评证明收益后评估独立高资源下载档。
- 候选矩阵仍按任务与硬件分层：以已存在的 Qwen3-4B 为本地回归锚点，新增 Qwen3.5-4B 做代际对照；Qwen3.5-9B 只在目标机内存/显存预算允许时进入扩展档，Qwen3.8-27B 则列为高资源档研究对象。比较必须保持产品实际提示、Schema、采样参数、同一冻结样本和同一硬件条件；大参数或长上下文数字不直接形成购买/发布理由。

### 结构化输出与本地服务风险

- OpenAI Structured Outputs 仅接受 JSON Schema 子集；Object 需显式 `additionalProperties: false`，不支持的组合可能直接拒绝；拒答与输出截断仍需应用处理。Responses API 将结构定义放入 `text.format`，Chat Completions 使用 `response_format`，两者的请求、输出和流事件不能共用未经适配的解析逻辑。
- llama.cpp 文档提供 Windows `llama-server.exe`、默认 loopback 服务、健康检查和 OpenAI-compatible 路径，但仓库 `master` 是移动目标，不能作为交付版本号。其官方仓库 issue 曾报告 `$ref/$defs` 复杂 Schema 在特定构建上解析失败并回退为不受约束生成；该报告不证明当前构建仍有同一缺陷，但足以说明本地结构化输出必须针对锁定提交、模型模板和真实润色 Schema 做正反用例验证，并始终保留应用侧 JSON 解析、Schema 校验与质量门禁。
- 所以阶段 1 Schema 宜尽量保持扁平、限制条件收敛到各 Provider 的共同子集，并为 OpenAI Responses、Chat Completions、Anthropic、Gemini、llama.cpp 分别记录能力映射和降级策略。不得把“HTTP 200”或“输出可解析 JSON”视为符合 Schema。

### 更新后的来源

- Qwen 官方权重卡：[Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B)（Apache-2.0、Transformers 权重、上下文和模型方基准说明）、[Qwen3.5-9B](https://huggingface.co/Qwen/Qwen3.5-9B)。
- Qwen 官方 [Qwen3.8-27B 权重卡](https://huggingface.co/Qwen/Qwen3.8-27B)（权重、Apache-2.0、vLLM/SGLang 部署说明）；阿里云 Model Studio [Qwen3.8-27B](https://help.aliyun.com/en/model-studio/qwen3-8-27b)（托管推理服务、模态/上下文与能力说明）。
- OpenAI 官方文档：[Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs)、[迁移到 Responses API](https://developers.openai.com/api/docs/guides/migrate-to-responses)。
- llama.cpp 官方资料：[Server 文档](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)、[Schema `$ref/$defs` issue #21228](https://github.com/ggml-org/llama.cpp/issues/21228)（特定构建的缺陷报告，作为回归风险输入，不当作当前所有版本的定论）。

本轮结论：模型选择保持“待盲评”，本地主线候选更新为 Qwen3-4B vs Qwen3.5-4B，并以 Qwen3.5-9B 做资源允许时的升级档；Qwen3.8-27B 保留为高资源本地候选，也可通过官方云服务接入。正式纳入任何应用包前仍需核验对应权重修订、许可、转换/量化和运行时兼容。阶段 0 的样本授权与真实基线仍未满足，阶段 1 行为改动继续锁定。

