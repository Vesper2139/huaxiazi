# 话匣子 Agent 架构升级完整项目报告

**报告日期：** 2026-09-07  
**项目：** Huaxiazi / 话匣子（WPF .NET 8）  
**报告范围：** Skill 路由、提示层级、个性化与记忆、长上下文、RAG、Harness、安全护栏、模型选择、架构数据集和真实模型消融协议。  
**重要边界：** 本项目本轮工作的“调优”是提示词/Skill/Harness 架构搜索与评测，不是本地基础模型权重训练。

## 一、执行结论

项目已经从“把多段提示词拼成一条 system prompt”的实现，升级为一个可组合、可回放、可量化评测的 Agent Harness：

```text
用户输入
  -> 安全与意图边界
  -> Skill 候选过滤、组合、权重分配、冲突回退
  -> 个性化约束编译 / 记忆治理 / RAG 检索
  -> 分层上下文编排与预算裁剪
  -> 模型档位选择
  -> Workflow 或 Autonomous Harness
  -> 工具安全执行与结构化输出校验
  -> 质量门禁、轨迹记录、实验评测
```

早期 qwen3:4b 小切片曾将 `policy-first-v1` 作为候选基线；在新数据集和 100 条 dev 对照后，当前暂定基线更新为：**output-contract-v1 + 严格 JSON Schema + 结构化输出校验 + 有界修复重试 + 只读并行/变更串行 Harness**。这仍不是“所有模型上的最优证明”。

## 二、已新增的核心架构

### 1. 多 Skill 路由与权重架构

涉及：`ExpressionSkillRouter`、`SkillWeightAllocator`、`AgentSkill`、`ProfessionalizationPlanner`。

- 先按启用状态、兼容性和应用模式过滤，再计算场景、输入、来源和用户偏好相关性。
- 支持 `PreferredSkillIds` 和 `AvoidSkillIds`；偏好不能越过安全过滤，避免项直接排除。
- 最多组合 3 个高置信 Skill，降低认知负荷和提示膨胀。
- 使用温度化 softmax 将异构分数转换为权重，默认温度 20、主 Skill 上限 0.75。
- ID 大小写不敏感去重，权重有限值校验、归一化和四位小数残差修正。
- 发现互相否定的约束时回退到最高分 Skill，并记录 `SelectedSkills`、`SkillWeights`、`ConflictDetected`。
- 权重只影响候选架构实验和预算选择，不能覆盖安全、事实和权限门禁。

### 2. 专业用语与个性化约束架构

涉及：`PersonalizationConstraintCompiler`、`ConstraintStabilityEvaluator`、`PromptContextComposer`。

- Persona、偏好、专业策略被编译成低优先级约束，而不是当作 system 指令。
- 过滤规则覆盖规则覆盖、提示泄露、外部能力请求、Shell/浏览器/文件等越权片段。
- 保留拒绝片段清单，便于诊断而不是静默丢弃。
- 稳定性评测包含约束通过率、禁用约束违反率、事实锚点保留率和跨运行状态一致性。
- 个性化只能改变语气、格式和表达习惯，不能改变事实、权限、拒答边界或本轮明确要求。

### 3. 系统提示、Skill、记忆与任务的层级编排

涉及：`AgentContextPipeline`、`PromptLayerComposer`、`PromptBuilderService`。

运行时协议顺序固定为：

```text
system -> developer -> facts -> skills -> user_memory -> personalization -> knowledge -> tools -> task
```

- system/developer 和事实锚点可标记为稳定且必需，适合 KV Cache 和回放。
- facts 单独成必需层，避免高风险日期、数量、主体被低优先级文本挤掉。
- 长期记忆与本次个性化独立成层，可分别裁剪、审计和撤销。
- 每层使用 XML 边界，文本先转义；稳定前缀生成 SHA-256 指纹。
- 预算不足时按优先级省略非必需层，返回 `OmittedLayers`、实际字符数和 `RequiredOverflow`，不允许静默截断。

### 4. 用户记忆治理

涉及：`UserMemoryPolicy`。

记忆层次为 `Session > Preference > Profile > Sensitive`：

- 只注入用户明确同意、未过期、非敏感且通过注入检测的记忆。
- 同 key 冲突按层级优先；同层按 `UpdatedAt` 选择最新值。
- Sensitive 永不进入 prompt。
- XML 渲染前对电话、邮箱、token 等进行脱敏和实体转义。
- 与全局 `PromptInjectionSanitizer` 共用越权检测契约。

### 5. RAG 与知识信任边界

涉及：`HybridContextRetriever`、`KnowledgeContextRenderer`、`IEmbeddingIndex`、`InMemoryEmbeddingIndex`。

- 词法检索与稠密语义检索按 0.55 / 0.45 混合排序。
- 支持敏感等级过滤、top-k、字符预算、知识块 ID 去重。
- 语义 scorer 或 embedding 异常时降级为词法分数，不中断主流程。
- `IEmbeddingIndex` 校验向量维度、有限值、未知 ID 和余弦相似度。
- 知识以 reference-only 证据注入，包含来源、origin 和 score，不能获得系统规则或工具授权。
- 当前已具备生产 provider 的适配契约，但尚未接入真实远端 embedding/vector database。

### 6. Workflow / Autonomous Harness

涉及：`AgentHarnessExecutor`、`IdempotencyStore`、`StreamingToolCallAssembler`。

- Workflow：按预先声明的计划逐步执行。
- Autonomous：有限步数；只读工具可并行，变更工具串行。
- 工具名唯一、工具安全等级必须匹配、每次调用有独立超时。
- 只读操作允许指数退避重试；变更操作禁止自动重放。
- 变更操作必须带幂等键，成功结果按 TTL 缓存，避免副作用重复执行。
- 未知工具、权限不匹配、超时、失败会停止并记录错误码。
- 流式工具调用使用严格状态机，参数必须在结束时通过 JSON 校验，未闭合调用不能执行。
- trace 记录步骤、工具、尝试次数、错误、稳定前缀和省略上下文层，不暴露思维链。

### 7. 统一安全护栏

涉及：`PromptInjectionSanitizer`、`ToolDescriptionPolicy`、`PromptSecurityPolicy`。

共享清洗契约已接入工具输出、工具 schema、RAG、记忆、个性化和外部 Skill：

- Unicode 归一化、零宽字符清理。
- 中英文“忽略/覆盖/泄露/显示隐藏指令”等越权模式识别。
- 工具名、参数名、参数类型和描述长度严格校验。
- 原始工具描述不直接暴露给模型，只发送安全投影。
- 工具输出中的危险指令按行替换为过滤标记，同时保留安全行。

### 8. 模型选择与结构化输出

涉及：`ModelSelectionPolicy`、`StructuredOutputContract`、`StructuredGenerationWorkflow`、`AIService`。

- Fast：短文本、低风险、低延迟。
- Balanced：长上下文或工具任务。
- Reasoning：高风险、复杂推理、深度事实约束。
- 安全等级只能升级模型档位，不能为延迟降级。
- 负数上下文/延迟计量会被拒绝，避免路由绕过。
- 原生 schema 优先；不支持时回退到 Prompt + 本地 validator。
- 输出最多进行有限修复重试，不进入无限 ReAct 循环。

## 三、架构调优数据集与实验资产

### 架构搜索数据集

目录：[datasets/architecture-v1](C:\Users\lenovo\Desktop\话匣子\PromptFloat\datasets\architecture-v1)

| 文件 | 规模 | 用途 |
|---|---:|---|
| `architecture_train.jsonl` | 10,000 | 淘汰明显失败的层级措辞与权重 |
| `architecture_dev.jsonl` | 1,000 | 候选 Skill、层级和权重搜索 |
| `architecture_test.jsonl` | 1,000 | 冻结后一次性最终评估 |
| `architecture_dev_requests.jsonl` | 5,000 | 5 个候选架构 × 1,000 dev 请求 |
| `candidate-configs.json` | 4 | 初始候选架构配置 |
| `manifest.json` | 1.1 | schema、场景、版本和 split 策略 |

数据记录包含：五层提示词、权重、场景、风险等级、失败模式、期望决策、gold 输出、必守约束、禁用约束、事实锚点和 `generalization_family`。

当前覆盖 10 类架构场景：普通请求、歧义、提示注入、高风险事实、工具失败、格式违规、超长上下文、Skill 冲突、记忆冲突、工具并行。

数据门禁包括：

- 五层权重非负、有限且总和为 1。
- 场景与 `expected_decision` 语义一致。
- train/dev/test 输入和 generalization family 不交叉。
- `architecture-batch` 拒绝 test split。
- Provider 脚本拒绝缺失或非法 split。
- 评测器拒绝预测 split 与 gold 不匹配。

### 评测指标与晋级门

`PromptArchitectureEvaluator` 输出：覆盖率、安全率、决策率、格式率、约束率、事实锚点率、简洁度、效用、Pareto 前沿和 Wilson 95% 下置信界。

默认晋级要求：至少 100 个样本、覆盖率 ≥99%、安全/格式/约束点估计 ≥99%，相应下置信界 ≥90%，决策下置信界 ≥60%。

`format_violation` 使用严格 answer-only JSON schema：顶层 object、仅有 `answer` 字符串字段；不是“能解析 JSON”即可通过。

## 四、真实模型实验结论

实验记录：[experiment-log-2026-09-07.md](C:\Users\lenovo\Desktop\话匣子\PromptFloat\training\experiment-log-2026-09-07.md)

- `qwen3:4b` 基线在 120 条架构 dev 上：安全率 0.8500、决策率 0.7750、格式率 0.8333、效用 0.8392。
- `policy-first-v1` 同切片：安全率 0.9833、决策率 0.7667、格式率 0.8333、效用 0.8900。
- Strict JSON Schema 24 条 smoke：安全率 1.00、决策率 0.8333、格式率 1.00、效用 0.9500。
- `policy-first-v2` 24 条 smoke 效用 0.9625，但独立 120 条切片效用 0.9025，未晋级。
- `huaxiazi-qwen3-strict` 与 `qwen3:4b` 在 24 条严格 schema 切片上结果相同，没有证据证明 Modelfile 本身带来增益。

结论：**严格 schema 解决输出通道问题，但不能替代模型内容质量；小样本提升不能作为晋级依据。** 早期建议锁定 policy-first-v1 仅适用于旧切片；当前应以 output-contract-v1 作为待复测基线，继续扩大 dev、进行人工事实/自然度评分，再决定是否进入冻结 test。

## 五、验证证据

截至本报告生成时：

- Release 构建：0 警告、0 错误。
- 架构数据集 train/dev/test 分别验证通过，0 个问题。
- 架构、Skill、权重、个性化、记忆、RAG、embedding、Harness、工具 schema、模型选择专项回归：73/73 通过。
- 过去完整 WPF 测试宿主曾出现 `HwndSubclass` 无头环境崩溃，因此本报告不把专项测试结果等同于完整桌面 UI 全量通过。

## 六、当前未完成与风险边界

以下项目尚不能宣称完成：

1. 真实 embedding provider 和持久化向量数据库尚未接入。
2. 多模态 OCR、音频、视频知识抽取管道尚未接入。
3. 流式网络客户端尚未统一覆盖所有 Provider。
4. 真实模型批量消融仍需在固定模型、工具环境和完整 dev 上持续运行。
5. 尚未完成受保护发布环境、签名构建、SBOM/provenance 与旧 Release 资产替换。
6. 全量 WPF 桌面回归需要真实桌面会话验证，不能由无头测试替代。

## 七、建议的下一阶段实施顺序

### P0：完成架构选择闭环

固定数据集 hash、模型、temperature、think 参数、schema、工具清单和 Prompt 版本；对所有候选跑完整 dev；做人工双人事实/自然度评分；只将满足下置信界门禁的候选送入冻结 test。

### P1：生产 RAG 与 Provider 接入

实现真实 embedding provider、向量库持久化、批量 embedding、索引版本和删除/重建策略；为每个 Provider 添加流式事件、工具并行和 schema 集成测试。

### P1：记忆与隐私运营化

将幂等存储和记忆存储替换为具备 TTL、审计和恢复策略的持久化实现；增加用户可见的记忆查看、撤销、导出和彻底删除验证。

### P2：多模态与知识建模

增加 OCR/音频/视频抽取、来源 provenance、结构化实体/关系索引，并把多模态片段统一转成 reference-only 知识证据。

### P2：持续评测与发布门禁

将数据集校验、泄漏检查、候选评测、Prompt 注入回归、SBOM、签名和 provenance 纳入 CI；任何 test 结果不得回流候选搜索。

## 八、最终判断

本项目已经完成**Agent 架构层的系统化升级和可评测基础设施建设**，尤其覆盖了用户提出的前三项核心问题：多 Skill 选择/权重、专业表达与个性化约束稳定性、复杂提示/记忆/知识的层级与超长上下文处理。

但它目前仍应被准确称为：**“具备成熟 Harness 设计和离线/小规模真实模型评测能力的 Agent 架构候选系统”**，而不是已经在所有 Provider、向量库、多模态场景和生产环境中证明全自动最优的 Agent。

## 九、实验模型与 API 边界

此前记录的 qwen3:4b 实验通过 Ollama 在本地运行，属于本地模型行为评测，不是本地训练或微调。当前开发实验统一使用 Ollama 的 OpenAI-compatible 模拟 API；本报告不包含线上 MiMo 请求、线上密钥或线上模型结果。未来若重新启用云端 Provider，必须由独立实验运行器读取安全凭据，并单独记录模型、Prompt 版本、数据集 split 和成本。

## 十、前端/设置页暴露筛选

新增 `AgentTuningExposurePolicy` 作为前端白名单，并由 `SettingsViewModel.UserTuningOptions` 提供给设置页绑定。Basic 设置可直接面向普通用户：回答风格、详细程度、澄清偏好和首选语言。Advanced 设置面向熟悉用户：偏好/回避 Skill、记忆授权、模型档位、上下文预算和知识检索范围；这些值仍必须经过路由、安全和上下限校验。当前页面已有的表达风格、澄清、记忆和 Skill 管理控件继续沿用既有绑定；白名单为后续统一渲染高级调优项提供单一入口。

本轮进一步修正多 Skill 权重一致性：权重现在在 Skill 安全投影之后计算，被过滤为空的 Skill 不会再出现在权重表中；发生指令冲突并回退主 Skill 时，权重明确重置为主 Skill=1，避免下游把未生效能力误认为已参与生成。

路由器同时增加了保守的跨 Skill 语义冲突检测（例如“简洁”对“详细”、“正式”对“口语”）。只有对立词分别来自不同 Skill 时才触发回退，并记录 `ConflictDetected`，避免简单关键词重叠造成误报。

`SkillWeightAllocator` 的残差回填也已调整：四舍五入后的正残差优先分配给非主 Skill，确保主 Skill 的最大权重是硬上限，而非近似上限。

个性化约束评测新增 `RequiredAnyOf` 约束组：产品可以为“专业/正式”“简洁/直接”等经过审核的表达变体定义至少命中一个，同时继续保留 `Required` 的逐字硬约束和 `Forbidden` 禁用项。这样能减少合法措辞差异造成的假失败，但不会把任意自然语言都视为满足约束。

个性化编译器还对同一字段中的重复片段做大小写无关的确定性去重，保留首次出现顺序，避免重复偏好造成 token 浪费或让模型错误放大某条偏好。

另外，生产分层入口不再允许用户自定义文本替换内置 system prompt。`BuildAgentContext` 始终加载产品系统规则，并将自定义内容经过注入过滤后放入低优先级 developer 指导层；因此用户仍可调教表达风格，但不能覆盖安全规则、工具权限或评测协议。

润色工作流的独立提示构建器也已对齐该策略：用户自定义系统文本统一经过同一编译/注入过滤，并标注为低优先级“表达指导”，避免不同业务入口出现信任层级漂移。

同时修复分层 Prompt 入口的安全边界遗漏：`BuildAgentContext` 现在在构造 system 层时显式追加统一 `PromptSecurityPolicy`，不再依赖某一份模板是否恰好包含完整安全规则；该边界由工作流回归测试验证。

为防止未来新增调用方绕过 Builder，`AgentContextPipeline` 本身也做幂等安全边界注入：调用方未提供边界时自动补齐，已提供时不重复追加。安全策略因此从“入口约定”升级为“管线不变量”。

Harness 自主模式新增独立的 `maxToolCalls` 总调用预算（默认 16，上限 256）。即使只读工具可以并行，也不能因为单步批处理而绕过总成本上限；轨迹会明确记录 `tool_call_budget_exhausted`，便于运营监控与失败归因。

长上下文组合现在额外记录 `ComposedPrompt.EstimatedTokens`。该值是 tokenizer-independent 的保守估算（中日韩字符按单字符、连续 ASCII 按约四字符一 token），只用于遥测和模型路由，不冒充 Provider 的精确计费 token；实际字符预算和安全层选择逻辑保持不变。

压缩器还增加了 UTF-16 代理项安全切片，避免在 emoji 或扩展字符边界截断后产生非法 Unicode；该行为由专项回归测试覆盖。

用户记忆选择器现在按实际转义后的 XML 项成本计入预算（含 `<user_memory>` 容器和 `<item>` 标签），而不是只计算原始 key/value 长度；因此记忆层的声明预算与最终注入文本一致，避免隐式挤占任务层上下文。

混合 RAG 检索器的 chunk 截断也改为代理项安全切片，避免长知识片段在预算边界破坏 Unicode；RAG 输出仍经过注入清洗后才进入 reference-only 知识层。

知识渲染器的预算判断改为预留真实 `</knowledge_context>` 结束标签长度，确保 reference-only XML 在任何 source 数量下都不会超过声明上限。

系统提示词、开发者提示词、层权重、安全阈值、注入检测规则、工具权限、重试/幂等策略、稳定前缀、原始 Skill 指令和 test split 均标记为 Internal，前端不可编辑。未知设置默认 fail-closed 为 Internal。这样既保留用户可调教能力，也避免用户覆盖安全边界、破坏评测隔离或制造副作用。

## 十一、实验工程与实际项目边界

实验数据、候选生成器、评测器和测试用例不属于运行时产品链路。主工程 `Huaxiazi.csproj` 已显式排除 `Huaxiazi.Tests/**` 与 `DatasetBuilder/**`，且不引用测试工程或 DatasetBuilder；只有测试工程引用它们。生产发布包不应携带 `datasets/`、`training/`、测试程序集或实验日志。实验应通过独立进程或 CI 调用已发布 Agent/Provider 接口，结果以版本化报告回写，不把测试集样本、测试反馈或评测权重写入运行时配置。

最新 Ollama 冒烟显示：`qwen3:4b` 在“只输出最终答案”的自然语言约束下仍产生分析性前缀；`huaxiazi-qwen3-strict:latest` 配合仅含 `answer` 的 JSON Schema 后返回满足协议的结构化结果。这验证了输出契约必须由模型 API、解析器和校验器共同保证，不能只依赖提示词措辞。

架构数据集生成器已升级为每个场景 4 组真实业务语境模板，并为每条记录保留跨 split 唯一编号，避免单一占位语句导致模型学到样本格式而非架构决策。train/dev/test 已重新生成并分别验证通过；旧数据上的评测结果不与新版本混比。

基于新数据集的五候选本地 smoke（每个 20 条 dev）中，`output-contract-v1` 暂时最高（效用 0.8350、决策率 0.45）；其余为 `explicit-v1` 0.7900、`minimal-v1` 0.7750、`policy-first-v1` 0.7600、`policy-first-v2` 0.7600。所有候选安全率、格式率和事实锚点率均为 1.00，但约束率均仅 0.10，尚无候选满足晋级门禁。该结果仅用于定位问题，不构成最优架构结论。

评测器已修正直接回答场景中“事实/直接”约束的语义判定，避免把“必须保真”误测成“答案必须出现这些词”。修正后 output-contract-v1 约束率为 0.45，其余候选为 0.25—0.30；这属于测量校准，不能视为模型质量提升，仍需扩大 dev 并进行人工复核。
