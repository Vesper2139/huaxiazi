# Agent 架构覆盖矩阵

| 主题 | 当前实现 | 验证证据 | 状态 |
|---|---|---|---|
| 多 Skill 选择与权重 | `ExpressionSkillRouter`，最多组合 3 个 Skill；`SkillWeightAllocator` 使用温度 softmax、主 Skill 硬上限、重复 ID 合并、稳定排序和安全残差回填；权重仅分配给最终通过安全投影的 Skill；检测保守语义对立并回退主 Skill=1 | `ExpressionSkillRouterTests`、`SkillWeightAllocatorTests` | 已实现 |
| Skill 路由证据贯穿业务链路 | `ProfessionalizationRequest/Plan` 保存所选 Skill、权重和冲突状态；Prompt 构建时显式注入 | 工作流、Prompt 构建测试 | 已实现 |
| Skill 路由审计归档 | Polish revision `ContextJson` 保存 selectedSkillIds/skillWeights/conflict 状态 | `PolishWorkflowServiceTests` | 已实现 |
| Skill 安全边界 | `AgentSkillPackageService` + `ToolDescriptionPolicy`，仅提示投影，不执行包内代码 | Skill 安全测试、工具 schema 测试 | 已实现 |
| 系统/任务/Skill/记忆层级 | `PromptBuilderService.BuildAgentContext`、`AgentContextPipeline`、`PromptLayerComposer`、`PromptContextComposer`、`PersonalizationConstraintCompiler`、`SkillWeightAllocator` | 真实业务入口分层、内置 system 不可被用户文本替换、用户自定义指导降级为 developer、事实锚点必需层、长期记忆/本次个性化独立层、XML 边界转义、层级排序、稳定哈希、优先级裁剪、越权过滤、Skill 权重测试 | 已实现 |
| 超长上下文 | `PromptContextBudget`；`PromptLayerComposer` 按优先级选择，高优先级可局部压缩并记录 `CompressedLayers`，低优先级才整体省略；压缩切片保护 UTF-16 代理项；`ComposedPrompt` 同时提供确定性 token 估算用于路由/遥测 | 上下文预算、压缩边界和 token 估算测试 | 已实现 |
| KV Cache 友好 | 稳定层前置、显式 DeveloperStable、稳定前缀哈希 | `PromptLayerComposerTests`、动态层哈希测试 | 已实现 |
| 个性化约束稳定性 | `ConstraintStabilityEvaluator`、`PersonalizationConstraintCompiler`，支持严格 Required 与经审核的 RequiredAnyOf 表达变体、偏好规范化去重、禁用项、事实锚点和跨次状态一致性 | 稳定性门禁、表达变体、去重和越权过滤测试 | 已实现 |
| 用户记忆 | `UserMemoryPolicy`，同意、过期、敏感等级、脱敏、同 key 分层冲突消解、同层按 UpdatedAt 选最新、按实际 XML 渲染成本预算；与 `PromptInjectionSanitizer` 共用越权检测 | 记忆策略、冲突、注入拒绝、更新语义和 XML 预算测试 | 已实现 |
| RAG 混合检索 | `HybridContextRetriever`，词法 + 可注入语义评分；索引去重、语义故障降级、Unicode 安全截断 | 混合检索、故障降级和截断边界测试 | 已实现（离线适配器） |
| RAG 信任边界 | `KnowledgeContextRenderer`，reference-only、来源和得分；按真实 XML 结束标签预留预算 | 知识渲染与预算测试 | 已实现 |
| 统一上下文入口 | `PromptBuilderService.BuildAgentContext` 接入 `AgentContextPipeline`，显式追加统一不可覆盖安全边界，并由 Prompt 优化工作流使用 | 工作流、上下文管线和安全边界测试 | 已实现（Provider 仍需端到端验证） |
| Workflow / Autonomous | `AgentHarnessExecutor`，有限步数、总工具调用预算、读并行、写串行；轨迹记录稳定前缀哈希、上下文省略层和用量，并区分步数/调用预算耗尽 | Harness 测试 | 已实现 |
| 工具安全 | 权限等级、唯一工具注册、参数 schema 严格校验、幂等键、超时、只读重试、注入清洗 | Harness 与工具 schema 测试 | 已实现 |
| 跨层提示注入契约 | `PromptInjectionSanitizer` 统一处理中英文/Unicode 归一化后的越权、泄露和规则覆盖短语；`AgentContextPipeline` 幂等追加不可覆盖安全边界；工具输出、工具 schema、RAG、个性化与 Skill 投影共用 | Harness、上下文管线、工具 schema、RAG、个性化、Skill 路由回归测试 | 已实现 |
| 流式工具调用 | `StreamingToolCallAssembler`，严格状态迁移和 JSON 校验 | 流式状态机测试 | 已实现 |
| 结构化输出质量门 | `StructuredOutputValidator` + `StructuredGenerationWorkflow`，Schema/内容校验、有限修复重试；自动探测 `IStructuredTextGenerationClient` 使用原生 schema，否则回退 Prompt 校验 | DatasetBuilder、AIService 结构化请求测试 | 已实现 |
| 模型选择 | `ModelSelectionPolicy`，Fast/Balanced/Reasoning 升级策略；拒绝负数上下文/延迟计量，避免错误降级 | 模型路由与边界参数测试 | 已实现（策略层） |
| 架构候选训练/测试集 | `architecture_train/dev/test.jsonl`，五层措辞、权重、风险、失败模式和个性化约束；验证器检查场景—决策语义一致性 | 数据集校验、语义错标回归与无泄漏测试 | 已实现 |
| 架构候选评测 | `PromptArchitectureEvaluator`，效用分和 Pareto 前沿；严格 answer-only JSON schema；批量展开器拒绝冻结 test split，防止测试集反馈 | 架构评测、格式 schema 错误、test split 隔离测试 | 已实现（需接真实模型预测） |
| embedding / 向量库适配 | `IEmbeddingIndex` + `InMemoryEmbeddingIndex` 校验维度、有限值和余弦相似度；`HybridContextRetriever` 可直接注入 | 向量数学、异常查询、混合检索集成测试 | 已实现（生产 embedding provider 待接入） |
| 真实模型批量消融 | 候选批量请求显式携带 split；Provider 脚本拒绝 test/缺失 split，评测器拒绝预测 split 错配 | 架构批量构建测试、dev 请求样例、预测 split 隔离与脚本门禁 | 部分实现（仍需持续运行多模型矩阵） |
| 云端 Provider 实验 | 当前不执行线上请求；实验统一使用 Ollama OpenAI-compatible 模拟 API | 本地模拟请求测试 | 已实现（本地模拟） |
| 前端调优暴露白名单 | `AgentTuningExposurePolicy`：Basic/Advanced/Internal 分级，未知项默认拒绝 | `AgentTuningExposurePolicyTests` | 已实现 |
| 实验/运行时隔离 | 主工程排除测试与 DatasetBuilder；实验数据和日志不进入发布运行链路 | `Huaxiazi.csproj` 编译排除与 Release 构建 | 已实现 |
| 多模态知识抽取 | 当前无生产级 OCR/音频/视频管道 | 缺少连接器与数据 | 待接入 |
| 流式网络客户端 | 已有解析/状态组件，尚未统一接入所有 Provider | 需 Provider 集成测试 | 待接入 |

## 发布门禁

在“待接入”项完成前，系统不应宣称已完成全自动 Agent 优化。每次架构变更至少需要：专项单元测试、架构数据集无泄漏检查、dev 候选评测和冻结 test 一次性评测；高风险工具必须保留幂等键和人工审计记录。
