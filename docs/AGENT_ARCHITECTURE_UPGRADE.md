# Agent 架构升级基线

## 目标

将提示词、Skill、用户个性化约束、记忆和工具编排视为一个可评测的 Harness，而不是把所有规则拼接成一段超长 system prompt。模型只负责在受控上下文中完成当前决策。

## 优先级与权重

运行时采用固定优先级：安全/权限 > 用户本轮明确要求 > 事实锚点 > 输出契约 > 任务 Skill > 用户长期偏好 > 推断上下文。多 Skill 路由先做相关性筛选，再由 `SkillWeightAllocator` 以温度化 softmax 归一化并限制主 Skill 上限；`PromptArchitectureDataset` 中的五层权重用于候选架构搜索，不允许运行时权重覆盖安全门禁。

Skill 路由先按模式和安全状态过滤，再按场景、输入标签、来源和用户偏好计算相关性；最多组合三个高置信 Skill。组合前做冲突检测，发现互相否定的约束时回退到最高分 Skill。路由结果记录 `SelectedSkills`、`SkillWeights` 和 `ConflictDetected`，便于回放和回归测试。

路由上下文支持 `PreferredSkillIds` 与 `AvoidSkillIds`：偏好只在安全和模式过滤之后增加相关性分数，禁用项直接排除；它们不能启用不兼容 Skill，也不能覆盖安全门禁。

## 长上下文策略

所有层级先结构化，再在发送前经过 `PromptContextBudget`。个性化输入先由 `PersonalizationConstraintCompiler` 编译，过滤越权/外部能力片段并返回拒绝清单；预算不足时保留 system 前缀、事实锚点、用户本轮请求和 trust-boundary 尾部，压缩低优先级的记忆、示例和 Skill 说明；压缩动作必须写入轨迹，不能静默丢弃。

## 个性化稳定性

个性化只调整语气、格式和表达习惯，不得改变事实、权限、拒答边界或本轮明确要求。稳定性通过同一输入多次运行的一致性、事实锚点保留率、约束遵循率和安全通过率评估；任何个性化导致安全或事实指标下降，都自动降级为默认表达策略。

用户记忆必须经过 `UserMemoryPolicy`：只有用户明确同意、未过期、非敏感且不含提示注入片段的记忆才允许进入 prompt；Session/Preference 优先于 Profile；Sensitive 永不注入。日志或导出前统一调用脱敏规则处理电话、邮箱和密钥。

`RenderPromptContext` 以 XML 结构输出已批准记忆，并对 key/value 做实体转义；上层不应直接拼接原始记忆文本。

知识获取使用 `HybridContextRetriever`：词法匹配负责精确术语，调用方可注入 embedding 评分实现稠密检索；结果按 0.55/0.45 混合得分排序，受敏感等级、top-k 和字符预算约束，并在进入上下文前清除注入行。这样既能兼容当前离线应用，也能在未来替换为向量数据库或远端 embedding 服务。

工具执行通过 `AgentHarnessExecutor` 统一收口：Workflow 模式逐步执行预先声明的计划；Autonomous 模式只允许有限步数，并行执行只读工具、串行执行变更工具。未知工具、安全等级不匹配、工具异常会立即停止并写入 trace；工具输出再次经过注入清洗。思维链不进入用户可见结果，trace 只保留步骤、工具名和停止原因。

每个工具请求带独立超时；变更工具必须携带幂等键，否则在执行前拒绝。超时、取消、未知工具和安全等级不匹配均产生明确错误码，便于重试策略只对安全的读操作生效。

重试策略仅对只读工具启用，默认最多 2 次重试并使用 100ms、200ms、400ms 指数退避；变更工具即使遇到瞬时错误也不自动重放，避免副作用重复执行。

同一 Harness 实例会按 `toolName + idempotencyKey` 缓存成功的变更结果，重复请求直接返回缓存，不再次触发副作用；生产部署时应将该缓存替换为具备 TTL 和持久化能力的幂等存储。

当前实现已加入可配置 TTL（默认 10 分钟）；条目过期后才允许同一幂等键再次执行，避免无限内存增长和永久阻塞合法的新操作。

幂等逻辑通过 `IIdempotencyStore` 解耦，默认实现为 `InMemoryIdempotencyStore`；生产环境可注入 SQLite、Redis 或其他持久化实现，而无需修改工具编排器。

流式响应由 `StreamingToolCallAssembler` 管理状态：文本增量、工具开始、参数增量、工具结束、完成事件严格按状态迁移；工具参数必须在 `ToolEnd` 时通过 JSON 校验，未闭合调用不能触发执行或结束会话。

Harness trace 额外记录每个工具的实际尝试次数和最终错误码，可区分“只读重试后成功”“超时”“幂等键缺失”“权限不匹配”等故障类型，为架构候选评测提供可归因信号。

工具注册前经过 `ToolDescriptionPolicy`：校验工具名和参数类型、限制描述长度、通过共享 `PromptInjectionSanitizer` 清洗描述中的注入语句，并保留只读/变更安全等级。工具输出、RAG、个性化和 Skill 投影也复用同一判定契约，避免各边界的正则规则漂移。模型看到的工具 schema 必须是安全投影，原始描述只保存在本地注册表中。

`KnowledgeContextRenderer` 将检索结果封装为带 `source`、`origin` 和 score 的引用证据，并明确 reference-only 信任边界；知识片段不能获得系统规则、工具授权或覆盖用户当前请求的权限。

提示层由 `PromptLayerComposer` 结构化编排：稳定且必需的 system/developer 层固定在前，事实锚点作为必需 `facts` 层紧随其后，随后按显式协议序号排列 `skills -> user_memory -> personalization -> knowledge -> tools -> task`；自动路由 Skill 与专业化执行策略在 skills 边界内安全合并。长期记忆与本次个性化约束独立成层，可分别裁剪和审计。优先级权重只用于预算省略和候选架构实验，不会改变协议顺序。每层用 XML 边界包裹，生成稳定前缀哈希用于 KV Cache 命中和回放比对。预算不足时按优先级省略非必需层，并返回 omitted、实际用量和 required overflow 指标，不允许静默截断。

模型路由由 `ModelSelectionPolicy` 决定：短文本且低延迟任务使用 Fast；长上下文或工具任务至少使用 Balanced；高风险或复杂推理任务升级到 Reasoning。安全等级只能升级模型档位，不能为了延迟降级；工具和高风险请求会标记需要 fallback/重试门禁。

个性化回归由 `ConstraintStabilityEvaluator` 门禁：多次采样分别检查必守约束、禁用约束、事实锚点保留率和跨次一致性；任何一项低于阈值都判定为不稳定，阻止将该个性化策略提升为默认配置。

## 实验协议

固定模型、工具清单、采样参数和数据版本；train 用于淘汰候选措辞，dev 用于选择层组合和权重，冻结 test 只运行一次。个性化稳定性使用约束/事实锚点状态一致性，不要求多次输出逐字相同。每个失败样本必须归因到层级（system/developer/skill/harness/output_contract）、上下文压缩、工具协议或模型能力，禁止只记录“模型答错”。

架构评测同时输出 Wilson 95% 下置信界和 `PromotionEligible`。默认晋级门要求至少 100 条样本、安全与格式点估计不低于 99%、两者下置信界不低于 90%，且决策下置信界不低于 60%；不满足时只能作为实验候选，不能冻结 test 或替换生产默认值。

数据集入口：

```powershell
dotnet run --project .\DatasetBuilder\Huaxiazi.DatasetBuilder.csproj -- architecture-dataset --output .\datasets\architecture-v1
```
