# Provider 能力盘点与阶段 1 接口设计输入

盘点日期：2026-10-02；官方文档补核：2026-10-07

用途：作为 AI 定制计划 P2 兼容性盘点和阶段 1 结构化输出实现的依据。本文记录代码现状与官方协议差异，不改变产品生成行为，也不代表已完成端到端 Provider 验证。

### 2026-10-06 实施状态补记

OpenAI 直连 Provider 另已为存量 o 系列 profile 补齐 exact-match 能力：`o3`、`o3-2025-04-16`、`o4-mini`、`o4-mini-2025-04-16` 仅在官方 Chat/Responses 协议启用。Chat 使用 `developer` 消息、`max_completion_tokens` 和 low/medium/high `reasoning_effort`；Responses 使用 `max_output_tokens` 和 `reasoning.effort`。Chat Completions 文档未明确声明这些旧型号对 temperature/top_p 的逐型号支持状态；能力仍标为 unknown，应用在 o 系列推理路径中按保守策略省略两个可选字段，UI 告知所存设置值不生效，而不宣称服务端必然拒绝。映射支持 Structured Outputs，但仍由本地工作流校验结果。代理和相似 ID 不继承能力。上述模型均已弃用，不加入新建模型目录；o4-mini 计划于 2026-10-23 下线，o3 于 2026-12-11 下线。策略修正后 o 系列专项能力/请求测试 **15/15**、设置页解析后型号摘要 **1/1**、AIService + ProviderPlatform 回归 **254/254**、主项目 build **0 warning / 0 error**；均为本地代码/序列化验证，未发真实 API 请求。来源：[Chat Completions API](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[旧 o3 effort 指南](https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.2)、[o3](https://developers.openai.com/api/docs/models/o3)、[o4-mini](https://developers.openai.com/api/docs/models/o4-mini)、[OpenAI 弃用清单](https://developers.openai.com/api/docs/deprecations)。

OpenAI 保留默认 `gpt-4o-mini`，目录新增 GPT-6 Astra、GPT-6.1 Sol、GPT-6 Sol、GPT-6 Luna。官方 GPT-6 参数仅对 OpenAI 直连且精确命中的这四个型号启用：Chat/Responses 都支持应用 Low/Medium/High 对应的推理 effort；GPT-6 Astra / 6.1 Sol 不接受 `none`，Sol / Luna 接受。GPT-6 推理模式下省略 temperature/top_p；Chat 用 `max_completion_tokens`，Responses 用 `max_output_tokens`。能力未传播至代理、自定义端点和相似 ID。定向测试 **20/20**，AIService + ProviderPlatform 回归 **237/237**；未发真实 API 请求。依据：[GPT-6 参数与迁移指南](https://developers.openai.com/api/docs/guides/latest-model)、[Chat Completions API](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。

硅基流动目录保留 `deepseek-ai/DeepSeek-V3` 默认并新增官方 API 文档列出的 `Pro/deepseek-ai/DeepSeek-V4`、`deepseek-ai/DeepSeek-V4-Flash`、`Pro/zai-org/GLM-5.2`。V4/GLM 推理能力只对这三个完整 ID 生效：请求开启 `enable_thinking`，应用 High 档发送 `reasoning_effort=high`；API 只接受 high/max，low/medium 实际折算为 high，故 UI 低/中档省略 effort 而不声称降低推理。当前官方 JSON Mode 指南表示平台语言模型支持 JSON Mode；严格 JSON Schema 的完整支持型号没有列出。V4/GLM 使用 JSON Mode；默认 DeepSeek-V3 也精确使用 JSON Mode，并在 system 指令放入 Schema、由应用本地校验/修复，不假设它支持严格 JSON Schema。`max_tokens` 继续表示最终答复上限，不猜测 `thinking_budget`。SiliconFlow 专项 **17/17**，AIService + ProviderPlatform 回归 **239/239**，主项目构建 **0 warning / 0 error**。未发真实请求或测质量、费用、延迟。来源：[Chat Completions API](https://docs.siliconflow.cn/docs/api/chat-completions-post)、[推理指南](https://docs.siliconflow.cn/docs/userguide/capabilities/reasoning)、[当前 JSON Mode 指南](https://docs.siliconflow.cn/docs/userguide/guides/json-mode)。

Mistral 目录保留 `mistral-small-latest` 默认和既有项，新增官方当前模型 ID `mistral-small-2603`（Small 4）与 `mistral-medium-3-5`。官方 Chat API 接受 `reasoning_effort` 并提供 `json_schema` 模式；Reasoning 文档明确仅把 `mistral-small-latest` 与 `mistral-medium-3-5` 列为可调推理型号，故应用 Low/Medium/High 映射为 `low`/`medium`/`high`，Custom 省略字段；Small 4 只使用其型号页明确列出的结构化输出能力，不推断可调 effort。High 模式会额外返回 reasoning chunk，现有非流式解析器提取 `type=text` 最终答案并忽略 `type=thinking`。保留用户设置的 temperature/top_p（官方建议二选一）及 `max_tokens`，未凭建议改写用户采样/输出预算。目录、能力和响应形状回归 **6/6**，AIService + ProviderPlatform 联合筛选 **202/202** 通过，主工程 build **0 warning / 0 error**；没有真实 API、质量、时延或费用数据。来源：[Mistral Chat API](https://docs.mistral.ai/api/endpoint/chat)、[Reasoning 与响应 chunk 形状](https://docs.mistral.ai/studio/conversations/reasoning)、[Small 4 型号页](https://docs.mistral.ai/models/mistral-small-4-0-26-03)、[当前模型目录](https://docs.mistral.ai/getting-started/models/compare?models=mistral-small-4-0-26-03)。

Groq 官方模型目录当前列出 GPT-OSS 20B/120B 为生产型号、Qwen 3.8 27B 为预览型号；官方弃用页已将 Llama 3.1 8B 与 Llama 3.3 70B 标为 2026-08-16 退役，普通开发者账号不可继续使用，部分已有企业合同例外。故新 Groq profile 默认更新为官方建议替换 Llama 3.3 的 `openai/gpt-oss-120b`，仍保留 Llama 型号但在选择器明确标注退役及企业例外，不迁移已保存 profile。推理能力按精确型号区分：GPT-OSS 20B/120B 与 Qwen 3.8 支持 `reasoning_effort` low/medium/high 和 strict JSON Schema；Groq 默认会在 GPT-OSS 返回 `reasoning` 字段，应用现对其发送 `include_reasoning=false`；Qwen 3.8 改发 `reasoning_format=hidden`，避免 `<think>` 推理文本混入模型答复，并满足 JSON 模式对 reasoning format 的当前约束。仅三个被核实的精确模型 ID 获得上述推理能力；带未知后缀的相似 ID 不继承。其他模型使用 JSON Mode；Groq Chat API 将 `max_tokens` 标为 deprecated，直连 Groq profile 使用 `max_completion_tokens`。能力只绑定 Groq，不套用相同模型 ID 的 OpenRouter 或其他代理。Provider/AIService **378/378** 通过，主项目 build **0 warning / 0 error**；未发真实 API 请求。依据：[Chat API](https://console.groq.com/docs/api-reference)、[Structured Outputs](https://console.groq.com/docs/structured-outputs)、[Reasoning](https://console.groq.com/docs/reasoning)、[模型目录](https://console.groq.com/docs/models)、[退役清单](https://console.groq.com/docs/deprecations)。

**2026-10-07 Yi 当前 API 补核：** 当前官方接口正文现可读取，确认请求地址为 `https://api.lingyiwanwu.com/v1/chat/completions`，示例 model 为 `yi-lightning`；请求字段列出 `max_tokens`、`top_p`（0–1，默认 0.9）、`temperature`（0–2，默认 0.3）及常见 Chat Completions `messages`。应用能力映射仅对 `ProviderPlatform.Yi + OpenAICompatible + 精确 yi-lightning` 标记 temperature/top_p 支持和 `max_tokens`；`yi-large`、快照/近似 ID、OpenRouter/自定义代理仍不继承。接口正文未列 `response_format` / 严格 JSON Schema 或推理等级，因此这些仍是 Unknown，结构化生成继续在 system 指令中附 Schema 并在本地校验。存量 catalog 默认仍是 `yi-large`，不因单一示例 ID 改默认。官方平台服务条款与隐私政策称该服务存在平台智能路由和集成第三方模型，云端请求的数据流边界须继续按平台/第三方处理理解；本地模式不受该条款路径影响。来源：[当前官方 Yi 接口文档](https://platform.lingyiwanwu.com/docs/api-reference)、[当前平台服务条款](https://platform.lingyiwanwu.com/useragreement)、[当前平台隐私政策](https://platform.lingyiwanwu.com/privacypolicy)。本次只补齐能力描述与证据，不发真实 Provider 请求。

**2026-10-07 百川 Chat Completions 能力与数值边界：** 百川当前 [API v2 文档](https://platform.baichuan-ai.com/docs-v2/api)列出 `Baichuan4-Turbo`、`Baichuan4-Air`、`Baichuan4`、`Baichuan3-Turbo`、`Baichuan3-Turbo-128k`、`Baichuan2-Turbo`；temperature 范围 0–1，top_p 范围 `[0,1)`，`max_tokens` 范围 1–2048。文档只把前五个型号列为支持 `response_format={type:json_object}`；Baichuan2 的 JSON mode 和所有型号的严格 JSON Schema 未被证实。Catalog 增列上述官方型号但保留 `Baichuan4-Turbo` 默认；resolver 限定平台、协议、型号精确匹配，并存每项数值上限。序列化会夹取 temperature、top_p、输出 token；top_p 上界使用 `double.BitDecrement(1.0)`，确保 JSON 数值仍严格小于 1。已列型号用 JSON Mode，Schema 仍进入提示并由应用校验；Baichuan2 走本地 Schema 路径。该项没有真实 API 或质量/成本实验，仅修复请求范围和 UI 能力可解释性。

智谱当前推荐目录列出 GLM-5.3/GLM-5.2。二者直连 OpenAI-compatible profile 现按精确 model ID 适配 `reasoning_effort`：GLM-5.3 Low/Medium/High→low/high/max；GLM-5.2→none/low/max（官方说明 low 与 medium 入参均映射为 high、none 会放弃思考）。智谱 Chat API 的 `response_format` 仅列 `text`、`json_object`，未列 `json_schema`；结构化请求现附带 JSON Schema 的 system 指令并使用 JSON Mode，由应用侧契约校验，而不是假设服务端严格执行 Schema。catalog 增加两个当前可选模型但保留原默认，以免无质量/成本基线时更改默认档。智谱同名/近名 OpenRouter 不继承。定向 GLM/provider/settings 测试 **11/11** 通过，未发真实 API 请求。依据：[模型概览](https://docs.bigmodel.cn/cn/guide/start/model-overview)、[GLM-5.3](https://docs.bigmodel.cn/cn/guide/models/text/glm-5.3)、[GLM-5.2](https://docs.bigmodel.cn/cn/guide/models/text/glm-5.2)、[核心参数](https://docs.bigmodel.cn/cn/guide/start/concept-param)、[对话补全 API](https://docs.bigmodel.cn/api-reference/%E6%A8%A1%E5%9E%8B-api/%E5%AF%B9%E8%AF%9D%E8%A1%A5%E5%85%A8)、[结构化输出](https://docs.bigmodel.cn/cn/guide/capabilities/struct-output)。

讯飞星火当前官方 HTTP Chat Completions 文档列出 `4.0Ultra`、`max-32k`、`generalv3.5`、`generalv3`、`pro-128k`、`lite` 六个 `/v1/chat/completions` 型号；`generalv3.5` 对应 Max，官方公告称 Max 服务 2026-03-10 起升级到 Ultra，授权用量与 Ultra 合并。新建 Spark profile 现在选择明确的 `4.0Ultra`；保留所有旧型号 ID 与已存 profile，不进行自动重写。文档对该接口支持的 sampling 参数给出 temperature `[0,2]`、top_p `(0,1]`；保存值为 0 时仅将传出值提升为最小正 IEEE-754 double，不捏造官方未公布的十进制下界。按型号映射 max_tokens 上限：Ultra/Max-32K/Pro-128K 为 32768，Max/Pro 为 8192，Lite 为 4096。六个型号使用文档列出的 JSON Object Mode，Schema 仍由应用本地校验；文档未列 reasoning_effort，故不映射。未知型号后缀不继承这些版本能力，OpenRouter 不继承 Spark 能力。专项 Provider/AIService/设置摘要测试 **25/25 通过**（其中 API 契约、目录、UI 摘要与未知后缀覆盖）；无真实 API 请求。依据：[讯飞星火 HTTP Chat Completions 官方文档](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)。

Kimi 官方现行目录确认 `kimi-k2-0905-preview` 与 `moonshot-v1-*` 已下线，当前候选为 K2.6、K3、K2.7 Code/高速版。目录默认选通用型 `kimi-k2.6`，不自动改写存量 profile；K3 为旗舰可选项。直连 Kimi 的四个当前型号均省略 temperature/top_p（固定为 1.0/0.95）；仅 K3 支持 `reasoning_effort`，Low/Medium/High 映射为 low/high/max，Custom 使用默认 max。K2.6 默认启用思考，K2.7 Code 始终思考；应用没有专属思考开关，故不发送 `thinking`。K2 型号的推理档位还会改变应用设置的 `max_tokens`/超时，但没有 direct effort 控制。能力不外推给 OpenRouter、自定义 endpoint 或同名模型。官方指出 K2.6 对复杂 JSON Schema 支持有已知限制，业务端仍须本地验证。fake-handler、型号目录和设置摘要联合测试 **31/31** 通过；无真实 API 请求或质量/成本结果。来源：[Kimi 当前模型列表](https://platform.kimi.com/docs/models)、[模型参数参考](https://platform.kimi.com/docs/api/models-overview)、[推理强度](https://platform.kimi.com/docs/guide/use-reasoning-effort)、[Structured Output](https://platform.kimi.com/docs/guide/response_format)。

火山方舟豆包预设默认版本从即将下线的 `doubao-seed-2-0-lite-260215` 更新到官方推荐 `doubao-seed-2-0-lite-260428`。官方 Chat API 对旧版 Lite/Pro 明确 temperature/top_p 固定为 1.0/0.95，所以旧 profile 的这两个字段现在省略并在能力说明中标注；新版 Lite 支持 `reasoning_effort`，按应用 Low/Medium/High 发送 low/medium/high，Custom 使用服务默认值。仅应用于直连 Doubao 平台；存量 profile 不迁移，其他兼容服务不继承。新版 JSON Schema 输出支持仍标 beta，本地 schema 校验继续作为质量门禁。来源：[Chat API 参数](https://docs.volcengine.com/docs/ark/chat-api?lang=zh&redirect=1)、[参数支持列表](https://docs.volcengine.com/docs/ark/model-parameter-support?lang=zh)、[推荐型号目录](https://docs.volcengine.com/docs/ark/model-release-announcement?lang=zh)、[结构化输出](https://docs.volcengine.com/docs/ark/structured-output-beta)。Doubao 定向测试 **26/26**，无真实 Provider 调用。

DeepSeek 当前官方 OpenAI-compatible 型号为 `deepseek-flash` 与 `deepseek-v4-pro`；`deepseek-v4-flash` 仍被接受但已退役，并路由到 V4.1 Flash。thinking mode 禁止 temperature 生效，top_p 仅在 thinking mode 生效且有效区间为 `[0.95,1.0]`，服务会把低于 0.95 的值视作 0.95。代码现对直连 DeepSeek 当前/旧 V4 型号识别 thinking controls：low/medium/high 映射为 low/high/max，Custom 省略 effort；thinking 时省略 temperature 并发送受限 top_p，关闭思考的空答重试则只发送 temperature。新建 profile 使用 `deepseek-flash`；存量配置不迁移。来源：[DeepSeek Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode)、[当前型号与旧别名](https://api-docs.deepseek.com/quick_start/agent_integrations/codex)。`AIServiceTests`、目录/UI 专项共 178 项通过；无真实 API 请求。

阿里云百炼 Chat Completions 的 Qwen 3.8 Max/Flash/2.4T A95B/27B 支持 `reasoning_effort`，其中 `low`、`medium`、`xhigh` 是本项目应使用的离散值；项目 Low/Medium/High 映射为这三档，Custom 省略字段，不设置 `thinking_budget`。该映射只适用于 `ProviderPlatform.Qwen` 的直连 profile。目录增加 Max/Flash/27B，但默认仍为 `qwen-plus`；其他型号和同名代理不继承映射。官方同时建议 temperature 与 top_p 只设置一个；当前保留用户两项 profile 值，采样字段取舍等待基线评测。来源：[百炼 OpenAI 兼容 Chat 文档](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions)。新 request/catalog/UI tests **6/6** 通过，尚无真实 API 调用。

官方 OpenAI profile 已增加可选 `OpenAIResponses` 协议并保留默认 `OpenAICompatible`。Responses 请求现在使用 `/responses`、`text.format`、`max_output_tokens` 和 `store=false`；响应处理拒绝、截断并解析 Responses usage。只对精确核验的 `gpt-4.1-mini` 发送采样参数，对 `gpt-6-astra` 按官方能力省略采样并发送 Responses `reasoning.effort`，未识别模型省略这些可选参数。此补记不改变下方 2026-10-02 历史盘点表；定向 fake-handler 测试和构建通过，但未发真实 Provider 请求，因此线上行为仍待用户选择后验证。

另已为 Chat Completions 归一化 `finish_reason=length` 为 `Incomplete`、`content_filter` 与 `message.refusal` 为 `Refused`。安全拒绝不会触发显式 fallback；避免把拒绝信号当作 Schema 不兼容或服务故障后将同一内容重发到另一 Provider。该判断只依赖明确响应字段；未知兼容网关的停止语义不做猜测。

截至 2026-10-06 官方 Anthropic Messages API 当前模型目录还列出 Opus 5/5.5、Sonnet 4.6/5/5.5。`ProviderCapabilityResolver` 已将这 5 个型号及此前核验的 Opus 4.7/4.8 纳入直连 Anthropic 的精确映射；这组模型的请求不发送自定义 `temperature`/`top_p`，并使用已记录的 effort 支持值。Sonnet 4.6 不列入 xhigh。该能力仅绑定 Anthropic Messages 协议，不套用到 OpenRouter、兼容代理或同名本地模型；字段映射经 fake-handler 测试，未执行真实 API 请求。依据：[Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)、[Anthropic effort levels](https://platform.claude.com/docs/en/build-with-claude/effort)。

Google 当前 Gemini 3.x 文档要求去掉 `temperature`、`top_p`、`top_k`，并通过 `thinkingLevel` 指定 `minimal/low/medium/high` 中该型号实际支持的值；GenerateContent REST 对应 `generationConfig.thinkingConfig.thinkingLevel`。Gemini 3.5 Flash、3.8/3.7/3.6 Flash、3.5 Flash-Lite、3.1 Flash-Lite、3.1 Pro Preview 和 3 Flash Preview 的定向映射现已生效：采样参数不发送，当前 Low/Medium/High 可选档映射为 thinkingLevel。其他未知 Gemini 模型继续走旧采样规则并由 UI 标为未核验。官方当前 model guide 将 3.8 Flash 列为稳定、3.1 Pro 列为预览；本项目 Gemini preset 已删除无官方 model ID 的 `gemini-3.5-pro`，加入上述两个当前候选但保持现有默认模型不变。依据：[Gemini 3.x 更新指南](https://ai.google.dev/gemini-api/docs/generate-content/whats-new-gemini-3.5)、[thinking 参数](https://ai.google.dev/gemini-api/docs/generate-content/thinking)、[官方型号目录](https://ai.google.dev/gemini-api/docs/models)。协议/界面/目录由 fake-handler 与单测验证，未发真实 API 请求。

## 一、当前实现事实

Provider 路由主要由 `ProviderProfile.Protocol` 决定。`ProviderPlatformCatalog` 中许多供应商使用同一 `OpenAICompatible` 协议，但目录中的兼容标记只决定请求形状，不包含某一账号、模型或网关所支持的参数和 Schema 子集。

| 当前路径 | 实际请求 | 结构化 Schema 的处理 | 已有响应处理 | 判断 |
|---|---|---|---|---|
| `OpenAICompatible`（OpenAI、各兼容云端服务及 Ollama/LM Studio 等 profile） | 固定请求 `/chat/completions`；发送 `messages`、按精确 Provider/model capability 选择的 token 上限、`stream=false`。采样与推理参数由 ProviderCapabilities 映射。 | 只有 capability 明确为 `JsonSchema` 时发送严格 `response_format.type=json_schema`；`JsonObjectOnly` 发送 JSON Mode；`Unknown` 把 Schema 放入 system 指令，不发送原生格式字段，并继续本地 Schema 校验。OpenAI 官方型号映射精确到已核实 ID，兼容网关不继承同名模型能力。 | Chat 完成状态、拒绝、截断与错误类按已核实字段规范化；只针对 OpenAI 错误体的 allow-listed `error.param` 识别明确的结构化参数拒绝。 | “兼容 OpenAI”仍不证明严格 Schema、推理参数或错误语义兼容；未登记型号走提示约束 + 本地校验。 |
| `OpenAIResponses`（仅官方 OpenAI profile 可选） | `/responses`；发送 `instructions`、`input`、`max_output_tokens`、`store=false`；模型能力决定采样、`reasoning.effort` 与结构化格式。 | 精确白名单支持型号发送 `text.format={type:json_schema,name,strict,schema}`；Unknown 将 Schema 附入 `instructions` 并由本地校验，不发送原生格式。 | 解析 completed 输出；显式归类 refusal、incomplete 与 failed/cancelled；usage 从 Responses `id`/`usage` 读取。 | 保留旧 Chat 默认配置。Fake handler 验证载荷/本地处理；未实测真实 Provider 质量或错误率。 |
| `AnthropicMessages` | 固定请求 `/v1/messages`；发 `system`、`messages`、`max_tokens` 和按能力映射的采样/推理设置；使用 `x-api-key` 和固定 `anthropic-version`。 | 已核实 Claude 型号发送 `output_config.format={type:json_schema,schema}`；未知型号将 Schema 注入 system 指令并由本地校验。官方 Schema 仅支持一部分 JSON Schema 能力。 | 从 `content[]` 提取文本并按 stop reason 规范化截断、拒绝、工具调用等终态。 | 原生能力依赖型号及目标平台；未验证变体不继承。Fake handler 证明请求契约，不证明模型质量。 |
| `GeminiGenerateContent` | 固定请求 `models/{model}:generateContent`；发 `system_instruction`、`contents` 和 `generationConfig`，使用 `x-goog-api-key`。 | 已核实型号发送 `generationConfig.responseFormat.text`（`mimeType=application/json` 和 `schema`）；未知型号仅把 Schema 放进 system 指令并做本地校验。Google 支持 JSON Schema 子集。 | 按 `promptFeedback`、`finishReason`、拒绝/截断与工具状态规范化候选结果。 | 精确型号决定原生格式；fake-handler 验证序列化与状态处理，不代表线上效果或 Schema 全量兼容。 |
| `ManagedLocal` | `LocalTextGenerationClient` 请求本机运行时 `/v1/chat/completions`，发送运行时模型 ID、采样参数及 `stream=false`。 | 有 Schema 时发送 OpenAI 风格 `response_format`；客户端不确认运行时版本实际执行了约束。 | 只读取 Chat Completions 文本；本地 HTTP 错误目前主要转成状态码错误。 | UI 参数映射已有基础，能力真实性取决于独立运行时版本、构建和模型模板；交付前必须固定版本并做反例验证。 |

## 二、两条正式工作流与结构化接口的关系

### 2026-10-03：采样/推理参数的模型级兼容风险复核

当前 [`AIService`](../Services/AIService.cs) 在构造时只按全局范围裁剪 profile 参数：temperature 到 `[0,2]`、top_p 到 `[0,1]`（`AIService.cs:83-86`）；随后 Anthropic Messages、Gemini GenerateContent 和一般 OpenAI-compatible 请求都会显式发送这两个值（`AIService.cs:452-475,547-550`）。DeepSeek V4 推理开启时是一个特例：发送 `thinking`/`reasoning_effort`，不发送 temperature/top_p。能力映射当前没有按精确模型 ID 生效。

这会与当前官方模型约束冲突：

- OpenAI 官方 GPT-6 Astra 文档说明该模型不支持自定义 temperature/top_p，并要求 reasoning effort 非 `none` 时移除采样参数。当前应用默认 profile 温度为 `0.4`，因此用户在兼容 profile 中填写 `gpt-6-astra` 时，现有请求构造会显式发送不受支持的 temperature；这可导致请求被拒绝，而不是获得“较随机”的采样效果。
- OpenAI Chat Completions 当前将 `max_tokens` 标为 deprecated，并说明它与 o-series 模型不兼容，推荐 `max_completion_tokens`。当前 `AIService` 对所有 OpenAI-compatible 模型固定发 `max_tokens`；因此在该协议中选择 o-series 时，输出上限字段本身可能让请求失败。GPT-6 Astra 当前同时支持 Chat Completions 和 Responses；官方 Chat 参数以 `max_completion_tokens` 表达上限。此字段的计数包含可见输出与 reasoning tokens，意味着相同上限下留给可见文本的预算会因内部推理占用而变，必须在冻结任务集上校准，不能按普通聊天模型的可见长度直觉复用。
- Anthropic Messages API 文档说明 Opus 4.6 之后发布的模型不支持设置 temperature；只有兼容旧行为的 `1.0` 会被接受。当前 profile 默认温度 `0.4`，所以该系列模型会收到不兼容值。该 API 将 top_p 标为 deprecated，后续模型仅接受 `>=0.99` 的兼容值；本项目默认 top_p `1.0` 虽满足数值例外，但仍不应把已弃用字段当成有效用户控制项。
- Gemini 官方 API 文档说明生成参数的默认值随模型而异；单一平台协议不能证明具体 model ID 支持同一参数范围或同一有效默认值。应按精确模型/API 版本验证，而不是把全局 clamp 当作 capability 校验。

**问题本质与边界：** 当前 UI 保存的是用户配置值，`AIService` 会把它们转换成 HTTP 参数；仅验证数值处于通用区间不足以证明目标模型接受该参数。由于当前实现没有逐模型能力证据，不能对所有兼容 endpoint 自动推断参数支持。这个缺口影响请求成功率/服务可用性；它本身不证明任何参数会改善中文润色质量。

另一个相关的产品语义偏差是当前“推理强度”档位并不普遍控制模型 reasoning budget：[`ProviderInferencePresets.Apply`](../Models/InferenceLevel.cs) 的 Low/Medium/High 实际只写入 temperature、top_p、max_tokens 和 timeout；`AIService` 仅在 DeepSeek V4 请求中把 Low/High 映射成 `reasoning_effort`，其他 OpenAI-compatible、Anthropic、Gemini 和 ManagedLocal 路径都没有按该 enum 发送 reasoning/thinking 控制。ManagedLocal 的 context size、batch size、GPU layers、CPU threads 与 LoRA scale 是另一组运行时启动参数；`LocalTextGenerationClient` 发送的是 temperature/top_p/max_tokens/可选 Schema。SettingsViewModel 对 High 的当前描述“更充分推理”因此不能作为各 Provider 一致的技术语义。建议阶段 1 把“采样/输出档位”与“推理预算/模式”拆成不同 capability-aware 设置：每个档位列出实际生效字段，provider/model 不支持 reasoning control 时显示“不适用/使用服务默认值”，不要用单一高低标签暗示所有后端都增加了思考深度。

**阶段门槛内的处理：** 不在阶段 0/1 通过前改变生产请求。阶段 1 adapter 设计需把“是否发送某参数”与参数取值分开，按 provider + 精确 model/version + API 协议解析 capability；明确 unsupported 时不得发送，unknown 时不得静默声称用户值已应用，应显示已应用/未应用状态并采用经过验证的默认行为。模型别名或未识别版本不能继承乐观推断。推理 effort 同样按模型支持集合映射；无法证明支持的值应在发送前提示或拒绝。

**阶段 1 验证要求：** 用 fake HTTP handler 对 OpenAI Chat 与 Responses、Anthropic Messages、Gemini GenerateContent、OpenAI-compatible 及 ManagedLocal 捕获序列化请求；覆盖支持/不支持/未知三态及精确模型版本。负例必须证明 GPT-6 Astra 等不支持的 profile 不含 temperature/top_p，Anthropic Opus 4.6 后系列不含非默认 temperature/top_p，且未识别兼容模型不会因“兼容 OpenAI”标签自动套用官方能力。还需验证 UI 状态、实际请求字段和无内容遥测一致。退出阶段 0 前不发真实 Provider 请求，不宣称已修复。

官方核对：[OpenAI Reasoning models](https://developers.openai.com/api/docs/guides/reasoning)、[OpenAI Chat Completions create](https://developers.openai.com/api/reference/cli/resources/chat/subresources/completions/methods/create)、[OpenAI GPT-6 API checklist](https://developers.openai.com/api/docs/guides/deployment-checklist)、[Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)、[Gemini GenerateContent](https://ai.google.dev/api/generate-content)。模型和参数限制会更新，落地前需按当时精确 model ID 复核并保留查询日期。

### 2026-10-03：官方协议文档复核与前述判断收紧

本节是对上述 10-02 初次盘点的更新，以本次官方页面为准；仍仅作实现输入，不更改生产请求。

- **OpenAI GPT-6 Astra：** 当前模型页列出 Chat Completions 与 Responses 两个 endpoint，并列出 Structured Outputs；Chat API 定义 `max_completion_tokens` 覆盖可见输出与 reasoning tokens，`max_tokens` 已弃用且明确不兼容 o-series。OpenAI 变更记录明确 GPT-6 Astra 不支持自定义 `temperature`、`top_p` 与 `logprobs`，`reasoning.effort` 可用值不含 `none`。因此修正此前“非 none 时才去掉采样字段”的过窄表述：能力实现应对该模型省略自定义采样字段，不应把采样字段省略与 reasoning effort 档位绑定；用户选择的采样值在 UI 必须显示未应用/服务默认。用 `max_completion_tokens` 并不能保证可见文本长度，因为预算包含 reasoning token，需通过冻结任务评测校准。来源：[GPT-6 Astra 模型页](https://developers.openai.com/api/docs/models/gpt-6-astra)、[OpenAI API changelog](https://developers.openai.com/api/docs/changelog)、[Chat Completions 参数](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。
- **Anthropic Claude：** 当前 Messages API 页面列出 `claude-opus-4-8`、`claude-opus-4-7`、`claude-opus-4-6`。文档规则是“Opus 4.6 之后发布的模型”：自定义 temperature 不支持（只有 1.0 接受向后兼容）；top_p 已弃用，只有 `>=0.99` 接受兼容，其余值拒绝。因此阶段 1 测试矩阵需覆盖 Opus 4.7/4.8 和一个明确较早且支持的 model ID；不可把接受兼容值误报为用户采样值真正生效。原测试输入仅写 Opus 4.7，现扩为当前文档列出的 4.7 与 4.8。来源：[Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)。
- **Gemini GenerateContent：** 当前 REST 示例将结构输出放在 `generationConfig.responseFormat.text` 下，以 `mimeType: application/json` 和 `schema` 表达；`responseSchema` 与 `_responseJsonSchema` 在 API 参考中标为 deprecated，文档要求使用 `responseFormat`。JSON Schema 仍是受限子集，不支持属性会被忽略，复杂/深层 Schema 可能被拒绝；应用必须进行语义校验。因此旧表中“给 generationConfig 映射 Schema”需明确到新字段形状，并按选定 API/模型版本做序列化契约测试，不能只改名或假设完整 JSON Schema 支持。来源：[Gemini structured output REST 示例](https://ai.google.dev/gemini-api/docs/generate-content/structured-output)、[GenerateContent API 参考](https://ai.google.dev/api/generate-content)。
- **llama.cpp Server：** 当前上游文档继续明确其 OpenAI 风格 API 不承诺完整兼容；`reasoning_effort` 只是提供给 Jinja chat template，是否有效取决于模型模板，不是通用推理预算控制。服务端支持 `response_format` schema JSON 与 temperature/top_p sampler，但这不能证明本项目版本/模型模板实现一致。来源：[llama.cpp Server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)。

能力设计结论不变：精确模型、API 协议/版本与 endpoint 身份共同决定 capability；custom/OpenRouter/Ollama/LM Studio 与本地运行时没有对应证据时保持 `unknown`。本次文档复核没有触发生产代码改动，也没有 API 请求或模型质量测量。

| 执行路径 | temperature / top_p | 输出上限 | InferenceLevel / reasoning | 结构化请求 | 代码与状态 |
|---|---|---|---|---|---|
| OpenAI-compatible（普通模型） | 显式发送两个字段 | `max_tokens` | enum 本身不发；档位通过 presets 改采样/上限/超时 | `GenerateStructuredAsync` 发送 OpenAI Chat `response_format` | `Services/AIService.cs`，可序列化请求由单测覆盖；无 exact-model capability gate |
| OpenAI GPT-6 Astra / 6.1 Sol / 6 Sol / 6 Luna | GPT-6 推理请求省略两个采样字段 | Chat `max_completion_tokens`；Responses `max_output_tokens` | 精确型号能力表决定 reasoning effort；Astra/6.1 Sol 不支持 `none` | 对应协议映射严格 JSON Schema | 两协议映射已实现并由 fake-handler/能力测试覆盖；token 上限同时包含 reasoning 与可见输出，需评测校准 |
| OpenAI Chat 推理模型（o-series 等） | 按精确型号及 effort 能力判断，其他模型保持现有 profile 字段 | 应按模型用 `max_completion_tokens`；o-series 与旧 `max_tokens` 不兼容 | 尚未覆盖的型号维持 unknown，不推断 GPT-6 能力 | Chat `response_format` 支持情况按 exact model 检查 | GPT-6 能力已实现；其他族仍待映射与验证 |
| OpenAI-compatible（DeepSeek V4） | thinking 开启时省略；关闭时发两个字段 | `max_tokens` | Low→`low`、High→`max`、其余→`high` 的 `reasoning_effort` | 使用同一 Chat `response_format` 形状，需按模型确认 Schema 支持 | 同文件；特例映射已存在，不能外推至别的模型 |
| Anthropic Messages | 新版精确型号使用模型默认值；其余按 capability 支持情况 | `max_tokens` | 精确 Claude 型号映射 effort | `output_config.format` JSON Schema | 已接通型号映射、本地校验与请求契约测试；未做真实 API 请求 |
| Gemini GenerateContent | Gemini 3.x 精确型号使用模型默认值；旧/未知型号按 capability 处理 | `maxOutputTokens` | Gemini 3.x 映射 thinkingLevel | `generationConfig.responseFormat.text` JSON Schema | 新字段形状及候选状态处理已接通并测试；Gemini Schema 仍是受限子集 |
| ManagedLocal / llama-server | LocalTextGenerationClient 总是发两个字段 | `max_tokens` | InferenceLevel 经 presets 间接改变采样/上限；无 reasoning effort 请求字段 | Structured 请求发 OpenAI Chat `response_format`，实际约束力依赖运行时版本/模板 | `Services/LocalRuntime.cs`；argv 映射 ctx-size/batch-size/gpu-layers/threads/LoRA scale 已实现，但生产运行时尚未固定/提供 |

UI 端 `InferenceLevel` 已对部分 OpenAI GPT-6、Anthropic Claude、Gemini 3.x 及其他核实过的型号映射参数；普通模型、未知型号与未匹配的代理仍可能使用服务默认值或旧生成档位。字段映射测试只能证明请求序列化正确，不能证明服务端接受或遵守字段。持续补齐型号时应核对设置选项→profile 值→最终请求→完成状态/用量，并在能力摘要中明确未映射项。

**P2-B 模型能力测试输入与识别边界：** 模型选择器为可编辑 ComboBox，允许手动输入任意 model ID；`AIService` 在请求前还会将 profile.Model 按 `ModelMapping` 映射为 `_resolvedModel`。因此后续 capability lookup 必须使用最终请求的 `(Platform, Protocol, API version, resolvedModel)`，不能只看 UI 别名、显示名称或 ProviderPlatform。OpenRouter、Ollama、LM Studio 和自定义 OpenAI-compatible endpoint 即使模型 ID 带有厂商前缀，也不能自动继承直连厂商的参数保证；ManagedLocal 的能力还要绑定运行时 build/hash 和模型/模板版本。现有 Provider 预设的 `VerifiedOn` 默认为 2026-08-20，且预设模型列表只供建议、可自由改写；该日期不是用户实际模型 ID 或能力的验证凭据。

| 测试输入场景 | 预期选择/发送行为 | 关键验收 |
|---|---|---|
| OpenAI 官方 Platform + 精确 `gpt-6-astra` | reasoning effort 非 `none` 时不序列化 temperature/top_p；输出上限使用 `max_completion_tokens` | fake handler 捕获请求中两字段均缺失、旧 `max_tokens` 缺失且新上限值正确；不得显示采样用户值已生效 |
| OpenAI 官方 Platform + 官方 o-series model ID | 发送受支持的 `max_completion_tokens`，不发送旧 `max_tokens`；reasoning effort 使用该模型支持集合 | `max_tokens` 不兼容负例不得穿透；完成/截断分类可区分 reasoning+可见输出预算耗尽 |
| Anthropic 官方 Platform + 官方列出的 Opus 4.7/4.8（高于 4.6） | 不发送自定义 temperature；top_p 仅按精确模型的兼容限制处理，优先省略弃用字段 | `temperature=0.4`、`top_p=0.95` 不出现在 body；若发送兼容值须显示其非用户控制语义；能力来源包含官方文档版本/核对日期 |
| Anthropic 老版本已明确支持的 model ID | 仅按已验证参数支持集序列化 | 用户显式字段在支持时原值保留；不因为协议统一而丢失支持能力 |
| 官方 Platform 的推荐/旧 model ID 列表 | 不把推荐列表成员关系当成 capability 证据 | 能力决策来源是精确模型/API 文档，而非 preset catalog |
| `Model=alias`、`ModelMapping[alias]=gpt-6-astra` | 对映射后的真实 ID 解析 capability | 若先查 alias 必须在红测中暴露错误；最终请求不得泄漏 unsupported 参数 |
| OpenRouter `openai/gpt-6-astra`、custom gateway 的相同/相似 ID | 默认 unknown，除非该 profile/gateway 有单独验证证据 | 不继承 OpenAI 官方平台能力；unknown 在 UI 中显式显示，不静默声称已生效 |
| Ollama/LM Studio 同名模型或 alias/tag 变化 | unknown，或绑定可证明的服务/模型版本 capability | 不能仅凭 OpenAI-compatible API 形状推断 sampler 或 Schema 语义 |
| ManagedLocal 相同 GGUF 文件、不同 llama.cpp build/template | capability 随 runtime hash 与 model/template 绑定 | runtime 未固定时不得声称 native strict Schema；仅验证 localhost 请求不代表模型遵守 Schema |

capability 存储方案可选：（1）按 provider/model/API 的内置静态表，离线可预测、维护负担随目录增长；（2）连接时向 Provider 探测，覆盖可变 endpoint 但会引入网络/凭据依赖，且多数服务没有完整参数能力 discovery 接口；（3）完全依赖用户输入，适配自定义网关但容易误配。建议以**带来源与核对日期的静态事实表 + unknown 默认 + 可见的高级显式覆盖**为主；探测仅补充可验证证据，不把探测失败推断为 unsupported，也不将厂商兼容性推断成模型能力。显式覆盖应提示风险、记录不含内容的 capability 版本与应用字段，并允许一键恢复服务默认值。

- `PolishWorkflowService` 调用 `ITextGenerationClient.GenerateAsync`，接着用 `PolishResponseParser` 解析 `final` / `needs_clarification`，再执行专业质量校验和至多一次修复。它没有调用已存在的 `IStructuredTextGenerationClient`。
- `PromptOptimizationWorkflowService` 也使用普通文本生成，再做专业质量校验和至多一次修复。
- `StructuredGenerationWorkflow` 可以优先使用 `IStructuredTextGenerationClient`，并对结果做本地校验；当前仓库的引用只在其自身和 `DatasetBuilderTests`，并未接入上述两个正式产品工作流。
- 该通用工作流当前生成的 Schema 固定为仅含 `answer:string`，但其校验器允许调用者传入不同的 `RequiredFields` / `AllowedFields`。因此 API 暂时只对 answer 单字段契约自洽，接入更丰富的润色联合结果前需先统一契约定义和 Schema 生成规则。
- 现有 `PolishResponseParser` 在无效 JSON/未知类型时会返回 `Invalid`，但 `AIService` 只返回提取到的文本，没有提供通用的拒绝、截断、完成状态、实际请求模型和结构化失败状态对象给工作流。

## 三、官方协议核对与设计约束

| 供应商/运行时 | 官方文档所述能力 | 对项目的设计约束 |
|---|---|---|
| OpenAI | Structured Outputs 通过 JSON Schema 约束最终响应；Chat Completions 使用 `response_format`，Responses 使用 `text.format`。JSON mode 仅保证 JSON 语法，不保证 Schema；拒绝、截断和不支持 Schema 仍须应用处理。 | 现有 Chat Completions 路径继续兼容；Responses 应做成独立适配器，逐工作流比较完成状态、解析、用量、延迟和错误，再决定是否扩大使用。不能把 Responses 请求字段套给其他供应商。 |
| Anthropic | Messages 原生 JSON 输出使用 `output_config.format`；Schema 支持受限。官方还说明首次 Schema 可能有语法编译延迟，Schema 会短期缓存，格式变化可能影响 prompt cache。 | 在 Anthropic adapter 中映射其字段；保存 model/profile 能力声明；对首次与命中缓存请求分开统计时延/用量。依然要做本地 Schema 和语义校验。 |
| Gemini | GenerateContent 提供 JSON MIME 类型和响应 Schema 配置；只支持 JSON Schema 子集，接口参考还出现新的 response-format 形状及旧字段迁移信息。 | adapter 明确固定使用的 API 版本和请求字段，先验证当前 `generationConfig` 映射；将不支持的 Schema 预先拒绝或降级到本地解析，不静默丢弃 Schema。 |
| llama.cpp Server | 当前上游 Server 文档列有 OpenAI 兼容 Chat/Responses 路径及 Schema 约束 JSON；其文档也说明实现是“OpenAI-inspired”，并非完整兼容保证。 | 以项目实际打包的固定 commit/build 和目标模型模板做契约验证。上游 master 文档只能作候选能力，不能证明项目所下载运行时版本具备该能力。 |

另有一项 2026 年 7 月的初步研究，在 44 个模型的单词选择实验中观察到，仅要求 JSON 格式也会改变部分模型的答案分布；该实验不覆盖中文润色，不能直接推导本项目效果。它支持一条评测原则：结构约束负责机器可读性，风格和语义质量要在本项目任务上单独盲评，不能把 Schema 合法率当成定制质量指标。

## 四、阶段 1 的建议接口与实施顺序

1. **保持阶段门禁：** 本阶段只形成接口设计输入。真实业务工作流的生成行为改动仍须等阶段 0 的授权盲评数据、独立评审凭据和同条件云/本地基线通过。
2. **定义 Provider 无关结果：** 最少包括文本/结构化载荷、完成状态（完成、拒绝、长度截断、取消、失败）、规范化错误、request ID、实际平台/profile/model、耗时和可用 token 用量。业务正文不进入遥测。
3. **明确业务 Schema：** 润色需要显式表达 `final` 与 `needs_clarification` 两个合法分支；提示词优化定义自己的输出契约。每个字段的类型、必需性、字段白名单和语义约束必须由同一个契约来源生成 Schema 与本地校验规则。
4. **按协议适配而不是按名称猜测：** OpenAI Chat、OpenAI Responses、Anthropic Messages、Gemini GenerateContent、OpenAI-compatible gateway、ManagedLocal 分别映射端点、Schema、推理参数、采样参数和响应状态。能力最少分为 `native_strict_schema`、`json_only`、`local_validation_only`、`unsupported`，并可由 profile/model 覆盖供应商默认值。
5. **对每个 adapter 做请求/响应契约测试：** Fake HTTP handler 捕获真实序列化请求；验证正确字段位置、未支持参数不发送、Schema 子集拒绝、拒绝/截断识别、错误分类和 usage 解析。需要至少一个协议负例来证明不支持参数没有被静默吞掉。
6. **工作流最后接线：** 统一入口被测试覆盖后，再让润色与提示词优化共同使用上下文组装、结构化校验和最多一次受控修复；旧 `GenerateAsync` 保留给文本任务和兼容调用。

**阶段 1 通过条件：** 生产路径真实使用共同契约；每个支持的 Provider 映射都有请求级测试；所有结果在应用端完成结构与业务语义校验；拒绝、截断和错误不会被当作成功成稿；旧 profile 可读取；冻结评测上未出现质量退化。

## 五、配置兼容与迁移约束

源码核对结果：

- `ConfigService.CurrentConfigVersion` 当前为 21；Load 只显式迁移版本 20，其他不匹配版本会保留旧文件副本后以默认配置启动。测试明确跳过 v0–v6 迁移用例，体现 2.0.0 对这些历史 schema 的有意重置策略；AI 定制改造应保留该既有产品约定。新增字段不能通过随意提高版本号来“顺手迁移”，否则当前 v21 用户配置会走重置路径。
- `ProviderProfile` 的缺省值包括 `Platform=OpenAI`、`Protocol=OpenAICompatible`、`InferenceLevel=Medium`；`AppSettings.NormalizeProviderProfiles()` 会清理重复 ID、填补必要字段并约束采样参数范围。
- `ProviderProfile.Clone()` 是逐字段手动复制；添加 profile 字段时若忘记同步克隆，设置页、路由候选或工作流中的副本可能丢失新值。
- `ProviderProfile` 保存 `SecretId`，密钥正文单独存储；能力发现/路由设计不得把原始密钥写入新设置、诊断或评估清单。
- `ProviderPlatformCatalog.InferLegacyPlatform()` 当前只被测试直接调用，ConfigService.Load 未调用它。不能假设读取任何旧配置都会按 ApiBase 自动推断平台；也不能对用户已明确配置的 profile 按 endpoint 主机名反复覆盖 Platform/Protocol。

### 2026-10-03：v21 配置兼容检查与后续字段策略

源码确认 `CurrentConfigVersion=21`；加载器仅把 v20 作为受支持的显式迁移源，任意其他不匹配版本会备份（脱敏）并以默认配置启动。现有测试有 v20→v21 迁移用例，断言一个 profile、SecretId 引用和活动 profile 保留；`ConfigService` 的 Save→Load 往返测试从对象写出 v21 后再读，不等同于从手工、固定的真实 v21 JSON 磁盘 fixture 开始。测试目录没有发现字面 `configVersion: 21` 的加载 fixture。这是未来配置升级前需要补的证据缺口，不是当前加载器已被证明会丢弃 v21 配置。

**输入/产出/验收：** 输入为仓库当前 v21 用户配置合同与 `ProviderProfile` 克隆/归一化逻辑；本轮产出为迁移决策和阶段 1 负向验收要求。纯新增且向后兼容的可选设置（例如缺失时等于 `Manual`、capability override 缺失时等于 `unknown`）优先保持 `ConfigVersion=21`，确保老 JSON 反序列化得到显式安全默认，避免触发全量重置；新字段必须同步序列化、`Clone()`、`NormalizeProviderProfiles()` 和 UI/profile 副本路径。若新字段会改变旧数据含义、删除信息或要求不可逆重写，才增加 v21→v22 显式迁移、原子保留脱敏 v21 备份与回滚路径；严禁把 2.0.0 有意重置的 v0–v6 配置重新解释成新 schema。

**待门槛后执行的 v21 fixture 回归：** 手工写入两个具有不同 Platform/Protocol/model、精确 ApiBase、ModelMapping、Temperature/TopP/MaxTokens、有效 `provider-<id>` SecretId 和非首个 `ActiveProviderProfileId` 的 v21 JSON；Load→Save→Load 后逐字段比较，确认 profile 数量/顺序/身份、活动选择、密钥槽引用、映射和采样值不变，版本仍为 21，且不出现 `.v21.ignored` 备份。fixture 再省略未来新增的 optional routing/capability 字段，断言路由是 Manual 且能力为 unknown。此后才可用真实 v21 文件启动阶段 1/2 的字段迁移验证。当前不新增测试代码，因为该现存行为尚无预期红测失败；它是后续字段实现的验收用例，而不是可单独修复的现存缺陷。

| 新增内容 | 兼容策略 | 必需验证 |
|---|---|---|
| 纯派生 Provider capability（由 Platform/Protocol/model/runtime version 解析） | 不持久化能力结果，避免旧 profile 和缓存能力漂移。无法证明的能力按 unknown 处理，业务侧使用本地严格解析与校验。 | 同一 profile clone/重启前后路由结果一致；未知 capability 不会发送未经支持的参数。 |
| 可选的每 profile 能力覆写 | 仅在用户明确指定某能力时持久化；覆写模型/协议身份变化后标记待复核或清除。字段缺失必须等价于 unknown，不要默认 native strict。实现时同步 `Clone()`、`NormalizeProviderProfiles()` 和验证逻辑。 | 老 profile 缺字段可载入；新增值可保存/重载/克隆；不合法枚举被安全归一化，且不覆写 ApiBase、Platform、Protocol、Model、SecretId。 |
| 自动路由设置 | 首选新增可选配置并将缺省值定义为 `Manual`，保留用户当前 ActiveProviderProfileId；只有在现有版本规则允许安全加字段、且测试证明旧 v21 可读写时使用该做法。否则明确增加 v21→v22 迁移、原子保留旧配置和新字段缺省。不得新增后直接让自动路由默认生效。 | 以真实 v21 JSON fixture 测试 load-save-load，断言所有 profile、profile ID、ApiBase、model mapping、采样参数、SecretId 与 ActiveProviderProfileId 不变；路由默认 Manual。 |

实现阶段应先补兼容性回归和迁移测试，再接入能力解析；不能用 `NormalizeProviderProfiles()` 的自动纠正静默改写用户显式 endpoint/profile 选择。

## 六、核对来源

- OpenAI [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs) 与 [迁移到 Responses API](https://developers.openai.com/api/docs/guides/migrate-to-responses)
- Anthropic [Structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)
- Google [Gemini structured output](https://ai.google.dev/gemini-api/docs/structured-output) 与 [GenerateContent API reference](https://ai.google.dev/api/generate-content)
- llama.cpp [Server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- Research preprint: [Structured Output Collapses Answer Diversity Across 44 Language Models](https://arxiv.org/abs/2607.18476), July 2026. Scope is exploratory answer-choice diversity, not Chinese editing quality.

### 2026-10-07：StepFun Chat Completions 当前型号补核

依据官方[Chat Completions API](https://platform.stepfun.com/docs/zh/api-reference/chat/chat-completion-create)、[Step 5 Preview 型号页](https://platform.stepfun.com/docs/zh/guides/models/step-5-preview)、[Step 3.7 Flash 型号页](https://platform.stepfun.com/docs/zh/guides/models/step-3.7-flash)、[Step 3.5 Flash 型号页](https://platform.stepfun.com/docs/zh/guides/models/step-3.5-flash)：当前目录增加 `step-5-preview`、`step-3.7-flash`、`step-3.5-flash` 与 `step-3.5-flash-2603`，旧 profile 及默认值保留。四型号只对阶跃直连 OpenAI-compatible profile 映射 temperature 0–2 与 top_p 字段；top_p 取值范围没有公布，能力显示为可发送但不设边界。`reasoning_effort`：Step 5 / Step 3.7 low、medium、high；Step 3.5 Flash 2603 low、high；普通 Step 3.5 不映射。Step 5 单型号明确支持严格 JSON Schema，型号页的服务输出上限为 64K；当前应用自身 MaxTokens 上限仍为 32K。其他型号 JSON Schema 逐型号状态未知，继续 prompt schema + 本地校验。请求回归验证 Step 5 的 `response_format.json_schema`、strict 标记和 high effort。无真实服务请求或质量/价格/延迟比较。

### 2026-10-07：MiniMax 当前模型与 OpenAI-compatible 参数/思考内容

依据官方[模型调用指南](https://platform.minimaxi.com/docs/guides/text-generation)、[OpenAI-compatible API](https://platform.minimaxi.com/docs/api-reference/text-openai-api)和[Quickstart 准备指南](https://platform.minimaxi.com/docs/guides/quickstart-preparation)：当前公开服务列有 `MiniMax-M3`、`MiniMax-M2.7`、`MiniMax-M2.7-highspeed`；MiniMax-M3.1-Flash-Preview 暂只经 M Plan/MiniMax Code 提供，故未加入普通按量 API 型号目录。新 API Base 为 `https://api.minimax.cn/v1`；旧 profile endpoint `https://api.minimax.chat/v1` 保持 exact allowlist 兼容。直连 Chat Completions 三型号 temperature `[0,2]`、top_p `[0,1]`；M3 新接入用 `max_completion_tokens`，M2.7 系列保留 `max_tokens`。M3 用 `reasoning_split=true` 分离推理；M2.7 inline `<think>…</think>` 只在最终成稿交付前剥离；推理 effort 不冒用，仅 M3.1 Preview 明确支持。未找到 JSON Schema 原生输出说明，能力保持 Unknown。无真实请求、质量、成本和延迟测量；当前 app 可发送的 token 上限仍受 AIService 32K限制。

### 2026-10-07：xAI Grok 4.3 / 4.7 Chat Completions 能力补齐

依据 xAI 官方 [Grok 4.3](https://docs.x.ai/developers/models/grok-4.3)、[Grok 4.7](https://docs.x.ai/developers/models/grok-4.7)、[Responses API](https://docs.x.ai/developers/rest-api-reference/inference/responses)、[Chat Completions API](https://docs.x.ai/developers/rest-api-reference/inference/chat-completions) 与 [Structured Outputs](https://docs.x.ai/developers/model-capabilities/text/structured-outputs)：目录增加 `grok-4.3`、`grok-4.7`，保留 `grok-2-latest` / `grok-beta` 供存量配置手动使用，但它们不继承当前型号能力。新 preset 选择官方当前主接口 Responses API；已存 Chat Completions profile 不迁移，仍被精确允许并保留能力映射。两协议仅对直连 xAI + 精确型号映射 temperature/top_p、推理级别和原生 JSON Schema；Responses 使用 `max_output_tokens`，Chat 使用 `max_completion_tokens`，响应 Schema 仍由应用本地校验。temperature 上限为 2；top_p 的数值边界在本记录未核实，因此不裁切。4.7 effort 为 low/medium/high/xhigh；4.3 型号页对 xhigh 自身描述前后冲突，本应用保守映射文档一致列出的 none/low/medium/high，且现有 UI 不暴露 none。Responses 请求按 API privacy guidance 设置 `store=false`，不启用响应持久化检索。新目录默认 `grok-4.3`：官方列出的常规价目为每百万输入/输出 token `$1.25/$2.50`，低于 4.7 的 `$2/$6`，仅作为新目录默认项的公开标价成本依据；高上下文窗口有另行定价，实际区域、账户费用与润色质量都未实测，因此此默认不代表质量优选。未发真实请求、没有质量/延迟/成本实测。

### 2026-10-07：Anthropic 型号生命周期与 Sonnet 5.5 请求能力

依据 Anthropic 官方 [Models overview](https://platform.claude.com/docs/en/models/overview)、[Model deprecations](https://platform.claude.com/docs/en/about-claude/model-deprecations)、[Sonnet 5.5](https://platform.claude.com/docs/en/models/sonnet-5-5/overview) 与 [Effort](https://platform.claude.com/docs/en/build-with-claude/effort)：Sonnet 4.5 已弃用，计划 2026-11-30 退役，建议迁移到 Sonnet 5.5；Opus 4.1 已于 2026-08-05 退役。Sonnet 5.5 非默认 temperature/top_p/top_k 会被拒绝，支持 `output_config.effort`。项目 resolver 已对精确 Sonnet 5.5 映射省略采样值、effort 与严格 JSON Schema。新目录默认切换至 Sonnet 5.5，保留且明确标记旧 Sonnet 4.5；Opus 4.1 不再进入新 profile 型号列表，保存中的用户配置不自动迁移。当前为文档证据和 fake-handler 请求体验证，没有真实服务请求或模型质量/延迟/账单对照。

### 2026-10-07：Together Serverless chat 精确型号能力映射

当前 Together [serverless chat 目录](https://docs.together.ai/docs/serverless/models)包含模型字符串、上下文、定价及逐型号 structured outputs 状态。项目旧 catalog 只有仍在目录中的 Llama 3.3 70B 与当前目录未列出的 Qwen2.5 72B。官方 [Chat Completions API](https://docs.together.ai/reference/chat-completions) 当前示例以 `https://api.together.ai/v1` 调用，并列出 `temperature` (0–1)、`top_p`、`max_tokens`；[structured outputs 指南](https://docs.together.ai/docs/inference/chat/structured-outputs)要求通过 `response_format` 传 Schema，并将用户导向 serverless catalog 查询哪些模型支持。

能力 resolver 只对官方目录中明确标记 structured outputs 的 12 个精确 Together model ID、且 platform/protocol 是 Together + OpenAICompatible 时映射 `temperature/top_p Supported`、temperature 上限 1.0、native JSON Schema；top_p 不猜上界，inference effort 不由 API 存在通用字段推断为任意型号支持。其他 Together 型号、当前 Qwen3.8 两项未标记结构化支持的型号、代理/自定义 endpoint 均不继承 strict schema。模型可用目录随时间变化，`VerifiedOn` 为 2026-10-07；以后应以官方 catalog/API docs 重核，不把当前静态清单视为实时完整市场清单。新 preset 的 API Base 改用当前 docs host `api.together.ai`，旧应用 `.xyz` host 作 exact authority 兼容；endpoint lookalike 仍拒绝。未发真实请求。

### 2026-10-07：OpenRouter 型号目录与 endpoint 能力边界

OpenRouter 型号目录由设置页显式调用 `GET /api/v1/models`。应用白名单读取 `supported_parameters` 中的 `response_format`、`structured_outputs`、`max_tokens`、`temperature`、`top_p`，未知字段会忽略；该模型级集合只是候选信号，不能证明实际所选 endpoint 的能力。官方结构化输出文档指出支持按 endpoint 区分、并随时间变化；endpoint 列表 API 的普通请求示例返回 403（仅 management key 可用）。因此当前实现只在所选 profile 的能力快照与型号精确匹配，且目录同时列有 `response_format`、`structured_outputs`、`max_tokens` 时发送原生 JSON Schema，并设置 `provider.require_parameters=true`，让 OpenRouter 仅选择支持请求体全部参数的 endpoint；本地 Schema 校验继续保留。目录快照缺失或刷新后当前型号已不在目录中则能力保持 Unknown/清除旧快照。temperature/top_p 仅在目录明确列出时发送，不从 provider 名称或模型名称猜测；不推断 reasoning。目录读取不包含用户对话、不改变日志/遥测，也不构成输出质量评测证据。参考：[Models API](https://openrouter.ai/api/v1/models)、[Structured Outputs](https://openrouter.ai/docs/guides/features/structured-outputs)、[Provider Routing](https://openrouter.ai/docs/guides/routing/provider-selection)、[Endpoint list API](https://openrouter.ai/docs/api/api-reference/endpoints/list-all-endpoints-for-a-model)。

### 2026-10-07：MiMo V2.6 与 V2.5 Chat Completions 能力

MiMo 官方 API [型号清单](https://mimo.mi.com/docs/en-US/quick-start/model)列出 `mimo-v2.6-pro`、`mimo-v2.6-flash` 和资格受限的 `mimo-v2.6-pro-ultraspeed` 为当前实时 API 型号；`mimo-v2.5-pro` 与 `mimo-v2.5` 标为即将退役，指定退役时间为 2026-10-21 10:00（北京时间）。新 profile 默认使用 `mimo-v2.6-pro`，目录保留旧 V2.5 ID 并显示退役日期，存量 profile 不自动迁移。

官方 [Model Hyperparameters](https://mimo.mi.com/docs/en-US/api/guidance/model-hyperparameters)列出五个型号的 `temperature` 范围 `[0,1.5]`、`top_p` 范围 `[0.01,1.0]`，并说明 thinking 开启时自定义采样值无效；[Deep Thinking](https://mimo.mi.com/docs/en-US/quick-start/usage-guide/other/deep-thinking)和 [OpenAI Chat API](https://mimo.mi.com/docs/en-US/api/chat)确认 `thinking.type` 可设 `enabled/disabled`，默认启用，推理档位没有 finer effort 控制。本应用将 Low 映射到 disabled 并发送经范围夹取的采样设置；Medium/High/Custom 映射 enabled，省略 temperature/top_p。输出上限用 `max_completion_tokens`，该值同时消耗于思考与最终答案；连接测试使用 8 token 并关闭 thinking。

官方 [Structured Output](https://mimo.mi.com/docs/en-US/quick-start/usage-guide/text-generation/structured-output)明确列出上述 V2.6 与 V2.5 型号支持 `response_format: {"type":"json_object"}`，且说明 JSON Mode 只保证语法、不保证 Schema 结构。本应用发送 JSON Mode，同时将 Schema 放入提示并继续执行本地契约校验；Resolver 映射为 `JsonObjectOnly`，不得标注为 strict JSON Schema。能力只绑定 MiMo 直连 OpenAICompatible 和以上精确 ID；OpenRouter、自定义代理和 alias 不继承能力。官方文档的 Responses API 另有不同的 `reasoning.effort` 契约，本次没有把该协议混用到 Chat。

基于 fake HTTP handler 的 capability、采样档位、范围边界、completion token、JSON Mode 与 Schema 本地校验回归通过；没有真实 API 请求、账号资格检查、中文润色盲评或质量/成本/延迟测量。

### 2026-10-07：DeepSeek 当前 Chat 型号、JSON Output 与终态

DeepSeek 官方 [Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)当前列出 `deepseek-flash` 与 `deepseek-v4-pro`，temperature 范围为 `≤2`，top_p 要求 `(0,1]`；thinking 模式下 temperature 不生效，top_p 有效区间为 `[0.95,1]`，更低的请求值按 0.95 处理。官方 [Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)规定 OpenAI Chat 档位为 low/high/max，requested `high` 映射 actual high，`max` 映射 actual max；本应用 Low/Medium/High 分别发送 low/high/high，Custom 不发 effort 并保持 API 默认。thinking 模式会返回单独的 `reasoning_content`，应用不会把该字段当作最终答案。

官方 [JSON Output](https://api-docs.deepseek.com/guides/json_mode/)要求 `response_format={"type":"json_object"}`，并要求提示包含 JSON 输出指令。本应用对直连 DeepSeek 且 model 精确为 `deepseek-flash`、`deepseek-v4-pro`、`deepseek-v4-flash`、`deepseek-v4-flash-vision-exp` 时使用 JSON Mode，同时将 Schema 附在提示并运行本地校验；该 API Chat 文档没有在此路径承诺 strict JSON Schema。官方 [2026-09-10 更新](https://api-docs.deepseek.com/news/news260910/)说明 `deepseek-flash` 现指向 V4.1 Flash、`deepseek-v4-pro` 当前兼容路由也指向 V4.1 Flash；V4 Flash/vision-exp 的旧名只是临时兼容别名。因此设置页显示现行路由但保留已有 model ID 与默认。能力只绑定直连 DeepSeek `OpenAICompatible`，未知前后缀、OpenRouter、自定义代理不继承。

Chat 的 `finish_reason=insufficient_system_resource` 被归一化为 transient ProviderUnavailable，`aborted` 被归一化为 transient Incomplete；两者不允许部分文本作为完整结果交付。配置了用户显式备用模型时沿用现有显式 fallback 路径。本地 fake-handler 验证请求和失败类别；没有真实 API 请求、拒绝率、质量、成本或时延数据。
