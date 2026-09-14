# Agent 架构覆盖矩阵

本矩阵只记录当前代码中可验证的生产边界；实验项必须明确标注，不以文档设计替代运行时证据。

| 能力 | 代码入口 | 当前状态 | 验证重点 |
| --- | --- | --- | --- |
| 任务规划 | `ProfessionalizationPlanner` | 已实现 | 场景、风险、事实锚点、澄清问题 |
| Skill 路由 | `ExpressionSkillRouter` | 已实现 | 模式过滤、最多三项、冲突回退 |
| 上下文编排 | `AgentContextPipeline` | 已实现 | 层级、预算、转义、稳定前缀 |
| Provider 适配 | `AIService` | 已实现 | 协议、超时、大小限制、错误归一化 |
| 结果质量 | `ProfessionalQualityValidator` | 已实现 | 事实、格式、承诺强度、有限修复 |
| 工具 Harness | `AgentHarnessExecutor` | 可复用边界 | 权限、超时、幂等、重试、trace |
| 结构化生成 | `StructuredGenerationWorkflow` | 已实现 | Schema 优先、解析回退、尝试上限 |
| 本地历史 | `ArchiveService` | 已实现 | 版本、软删除、保留期、导出 |
| 向量检索 | `HybridContextRetriever` | 离线适配器 | 词法/稠密排序、reference-only |
| 多模态与跨 Provider 工具流 | — | 未接入 | 需独立权限模型与集成测试 |

发布前必须以测试结果更新本表，不得将设计草案标为已实现。
