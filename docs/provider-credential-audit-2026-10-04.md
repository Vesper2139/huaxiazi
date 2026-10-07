# Provider 凭据格式审计（2026-10-04）

## 用途与范围

本审计只回答阶段 0 离线评测数据准入和候选输出评分会不会漏掉项目所接入 Provider 的凭据。它不证明授权、身份或 key 是否仍有效，也不修改模型请求。项目 Provider 来源为 `Services/ProviderPlatformCatalog.cs`。官方文档若只公开授权头或短前缀而未公开完整 token 语法，本文只记录其证据边界，不推断格式。

规则共有两条扫描边界：`BlindEvaluationAuditor.Validate` 检查准入记录；`BlindEvaluationScorer.Evaluate` 检查候选输出。二者复用 `BlindEvaluationAuditor.ContainsPotentialSensitiveData`。因此某个规则存在并不等于它覆盖未知 token；表内逐项列出匹配前提。

## 云端 Provider 逐家台账

| Provider / 证据 | 官方资料明确内容 | 当前扫描器实际匹配 | 状态与缺口 |
|---|---|---|---|
| OpenAI — [Cookbook 示例](https://developers.openai.com/cookbook/examples/agents_sdk/session_memory) | API key 示例有 `sk-proj-…` 分段前缀 | 专用 `sk-proj-` 加至少一个 ASCII 字母、数字、下划线或连字符；其它连续式 `sk-` 仍由通用规则处理 | 已有准入和候选正负例。官方示例不是完整 key 语法；其它格式未证明覆盖。 |
| Anthropic — [Claude for Sheets 文档](https://docs.anthropic.com/en/docs/agents-and-tools/claude-for-sheets) | 示例包含 `sk-ant-api03-…` | 专用已知前缀后接非空 ASCII 字母、数字、下划线或连字符 | 有两端回归；只对已展示前缀作覆盖声明。 |
| Gemini — [API key 文档](https://ai.google.dev/gemini-api/docs/api-key) | 传统 key 示例使用 `AIza`；REST 请求可将 key 放在 `x-goog-api-key` header | 检测固定 `AIza` 形状；未知新格式若出现在明确 header/key 标签中可按标签筛查，孤立裸值不保证命中 | 新型 opaque key 的独立 token 形状仍未核实。 |
| DeepSeek — [官方接入文档](https://api-docs.deepseek.com/quick_start/agent_integrations/codex/) | 文档称 API key 以 `sk-` 开头 | 通用模式只匹配 `sk-` 后连续至少 12 个小写 ASCII 字母或数字 | 常见形状与当前模式相符，但文档未给字符集/长度完整规范；大写、分段或较短 key 的覆盖未知。 |
| 阿里云百炼 / Qwen — [获取 API key](https://help.aliyun.com/en/model-studio/get-api-key) · [Coding Plan FAQ](https://help.aliyun.com/zh/model-studio/coding-plan-faq) | 通用 key 以 `sk-` 开头；Coding/Token Plan key 以 `sk-sp-` 开头 | 通用连续式 `sk-` 规则；新增 `sk-sp-` 专用规则，非空 ASCII 字母、数字、下划线或连字符 | 两种均有回归。计划 key 检测用于离线防泄漏，不代表话匣子支持该套餐；Coding Plan 使用受厂商场景限制。 |
| 火山方舟 / Doubao — [Ark quick start](https://docs.volcengine.com/docs/ark/quick-start-beginner?lang=zh) | 官方脚本兼容“历史 UUID 格式”及新格式 `ark-<uuid>-<suffix>`，但页面没有说明旧格式在语料中的边界符或额外固定标记 | 新格式由专用规则检查；旧 UUID 若保留在 `API key: <uuid>` / `api_key=<uuid>` 字段或完整 Bearer Authorization header，可被通用标签规则检查；裸 UUID 没有专属匹配 | 通用字段规则可扫到有标签上下文的 UUID；不增裸 UUID 专用检测，因为 UUID 与任务/记录/请求 ID 结构相同，单凭词法无法区分凭据和普通标识符。当前没有真实样本测量此类误报/漏报。 |
| Xiaomi MiMo — [官方 API 集成 FAQ](https://mimo.mi.com/docs/zh-CN/quick-start/faq/api-integration) | 按量 API key 形状写作 `sk-xxxxx`；Token Plan key 写作 `tp-xxxxx`，并支持 `api-key` 或 Bearer header | `tp-` 专用规则检查非空 ASCII 字母、数字、下划线或连字符；按量 key 仍走通用 `sk-[a-z0-9]{12,}` 子集 | Token Plan 专用前缀两端有回归。官方写 `xxxxx` 为格式占位符，未规定其完整长度/字符集；按量 key 的通用模式边界同 DeepSeek/SiliconFlow。 |
| Kimi（月之暗面）— [官方快速开始指南](https://platform.kimi.com/blog/posts/kimi-api-quick-start-guide) · [Thinking API 指南](https://platform.kimi.com/blog/posts/kimi-thinking) | 官方请求示例用 `Authorization: Bearer` 传 API key；所查页面没有公开可识别的裸 key 前缀或完整 token 语法 | 保留完整 `Authorization: Bearer` 标头且值符合通用长度/字符范围时命中 | 标头路径可查；裸 Kimi key 的稳定词法格式仍未知，不据协议兼容推断 token 前缀。 |
| SiliconFlow — [官方接入示例](https://docs.siliconflow.cn/docs/usercases/use-siliconcloud-in-OpenClaw) | 示例称 key 以 `sk-` 开头 | 同 DeepSeek 通用模式，仅连续至少 12 个小写 ASCII 字母/数字 | 文档没有完整 token 字符集/长度规范；通用模式只在所列形状范围内命中。 |
| MiniMax — [Token Plan 页面](https://platform.minimaxi.com/subscribe/coding-plan) | 页面称可获取 “sk-cp Key” 供 OpenAI-compatible 工具使用，但没有展示完整 key 文本，也未说明该 Token Plan key 的认证 header；开放平台其它 API 的 Bearer 示例不能据此自动套用给此套餐 key | 通用 `sk-` 模式不匹配带额外分段的 `sk-cp` 形状；若文本保留完整、适用于该 key 的 Authorization Bearer 标头才会走通用标头检查 | 官方页面未展示 `sk-cp` 后的确切分隔符、token 语法或认证头；不添加猜测性前缀正则，裸 Token Plan key 是已知待核缺口。 |
| OpenRouter — [Quickstart](https://openrouter.ai/docs/quickstart) | 示例 `sk-or-v1-…` | 专用 `sk-or-v1-` 加非空 ASCII 载荷 | gold/candidate 均有回归；不是完整长度规范。 |
| xAI / Grok — [Management auth](https://docs.x.ai/developers/rest-api-reference/management/auth) | 创建示例展示 `xai-…`，推理使用 Bearer | 专用 `xai-` 加非空 ASCII 载荷 | gold/candidate 均有回归；字符集/长度只是扫描启发式。 |
| Groq — [Security onboarding](https://console.groq.com/docs/production-readiness/security-onboarding) | 官方示例显示 `gsk_your_secret_key_here` | 专用 `gsk_` 规则要求 20–255 个 ASCII 字母、数字、下划线或连字符 | 已有通用 Groq 格式回归；文档示例中的后缀长度落在实现范围内，但官方示例不定义全部 key 语法。 |
| Mistral — [Quickstart](https://docs.mistral.ai/getting-started/quickstarts/studio/activate-and-generate-api-key) | API 请求用 `Authorization: Bearer $MISTRAL_API_KEY`；没有在该资料中给裸 key 前缀 | 当文本保留完整 `Authorization: Bearer …` 标头且值符合通用长度/字符范围时命中 | 复制出来的裸 Mistral key 若无标签，格式未核验，不能声称覆盖。 |
| 智谱 GLM — [推理接口](https://docs.bigmodel.cn/api-reference/%E6%99%BA%E8%83%BD%E4%BD%93-api%EF%BC%88%E6%97%A7%EF%BC%89/%E6%8E%A8%E7%90%86%E6%8E%A5%E5%8F%A3) | API 使用 Bearer Authorization；该页面未提供 key 的裸 token 语法 | 有完整 Authorization Bearer 标签时按标签模式筛查 | 裸 key 外形待核；该 API 页面不是通用 Chat completions 的专属格式规范。 |
| StepFun — [官方 API 示例](https://platform.stepfun.com/docs/api-reference/vector-stores/delete_file) | 请求以 `Authorization: Bearer $STEP_API_KEY` 认证；没有给裸 key 前缀 | 保留完整 Authorization Bearer 标头时按标签模式筛查 | 裸 key 外形待核。 |
| 百川 — [官方对话 API](https://platform.baichuan-ai.com/docs/api?activity=true) | `Authorization` API key 以 Bearer 方式传递；页面未定义裸 token 形状 | 保留完整 Authorization Bearer 标头时按标签模式筛查 | 裸 key 外形待核。 |
| 讯飞星火 — [官方 HTTP 调用文档](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html) | 文档说明控制台获取的 `APIPassword` 用于 `Authorization: Bearer`；示例值 `123456` 是说明占位，未公布 token 前缀/长度规范 | 完整 Bearer 标头可由通用 Authorization 规则检查；另新增 `APIPassword:` / `APIPassword=` 标签形式检测，用于导出文本或脱敏记录 | 标签和值同在的短/长合成值在 gold 与 candidate 两侧均有回归；只讨论 `APIPassword` 术语不命中。无标签的裸 Spark key 仍无法按未知格式保证识别。 |
| Together — [Quickstart](https://docs.together.ai/docs/quickstart) | key 通过 Authorization Bearer 发送；页面以 `your_api_key` 占位，没有公开可识别裸前缀 | 保留完整 Authorization Bearer 标头时按标签模式筛查 | 裸 key 外形待核。 |
| 零一万物 Yi — [官方接口文档](https://platform.lingyiwanwu.com/docs/api-reference) · [API Key 管理](https://platform.lingyiwanwu.com/apikeys) | SSR 文档正文明确 API 通过 `Authorization: Bearer YOUR_API_KEY` 验证，并将 `Authorization` 列为必填 header；未展示裸 key 前缀或完整 token 语法 | 完整 Authorization Bearer header 会走通用规则；当前仅在 header 保留且值满足至少 20 个允许字符时命中，官方短占位符 `YOUR_API_KEY` 本身不满足长度门槛 | 已有通用 Bearer 路径，无需增加猜测性规则。仅确认传输方式，裸 Yi key 的格式/长度仍未知。 |
| 用户自定义 OpenAI-compatible endpoint | 用户可填写任意 base URL 和凭据；不存在统一上游认证规范 | 只有已知格式专属规则或被保留的 Authorization 标签能命中 | 必须按用户实际 endpoint 的一手认证资料单独登记；不能借用“OpenAI-compatible”推断 key 格式。 |

## 仍需核验

Provider catalog 有 20 个预置云端厂商；本台账已逐家列出全部 20 个，且现在 20/20 均有官方资料可说明至少一种认证传输方式。Yi 官方 SSR 接口正文明确 `Authorization: Bearer`，补全了上一轮遗漏；不过它未给裸 key 前缀或完整 token 语法，通用扫描器只有在完整 header 保留且值达到当前规则长度时才会命中。Kimi 也已核到 Bearer 用法但不能证明裸 token 格式；讯飞星火另支持带值的 `APIPassword:` / `APIPassword=` 标签形式。逐家有认证传输证据，不等于逐家已知裸 key 格式。另有一个用户自定义 OpenAI-compatible endpoint，其认证格式必须按用户填入的服务逐实例核验，不能像预置厂商一样计作一种固定格式。OpenAI-compatible 只是请求协议形状，不代表这些服务共享同一密钥语法。外部 Ollama/LM Studio 与 managed local 属本地连接，不应自动假定有远端 provider key。

下一步继续寻找 MiniMax `sk-cp` 对应的专属认证契约，并按具体配置盘点自定义 endpoint 的认证格式；Ark 旧 UUID 当前以带字段/header 时可检查、裸 UUID 不做猜测规则收口。严禁用未获授权的真实 key 填测试夹具。

## 判定原则与影响

- 通用 `sk-` 规则实现为 `sk-[a-z0-9]{12,}`；它不等于“匹配所有以 sk- 起始的 key”。
- 带厂商专属分段的 key 必须按有证据的前缀分别处理；对不明 token 使用宽泛高熵扫描会把普通正文误判成密钥，因此本轮不采取该方案。
- 此审计只加强数据泄漏筛查，不影响 API 参数、路由、提示词、偏好或生成质量。在线命中率仍需在获得授权数据后估计；不能把合成回归覆盖率当线上召回率。
- 阶段 0 的数据来源、真实授权、脱敏复核、独立双评和冻结集门槛未因此改变。
