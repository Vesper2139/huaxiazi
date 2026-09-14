# Agent 架构升级基线

本文定义运行时必须保持的不变量；当前架构总览见 [Agent 架构与工程编排](AGENT_ARCHITECTURE.md)。

## 优先级

安全与权限 > 用户本轮要求 > 事实锚点 > 输出协议 > 任务 Skill > 长期偏好 > 推断上下文。任何候选架构、模型或工具都不得改变该顺序。

## 编排规则

- `ExpressionSkillRouter` 先过滤模式、启用状态和兼容性，再选择最多三个高置信 Skill；冲突时回退主 Skill。
- `AgentContextPipeline` 固定组装 `system → developer → facts → skills → user_memory → personalization → knowledge → tools → task`。
- 必需层溢出必须显式返回 `RequiredOverflow`；低优先级层可省略但必须写入 trace。
- Workflow 串行执行声明计划；Autonomous 仅并行只读工具，变更工具串行且必须带幂等键。
- 只读工具允许有限退避重试；变更工具禁止自动重放。
- Provider、Skill、记忆、知识和工具均通过安全投影进入上下文，不能获得更高信任级别。

## 验收要求

每次架构变更必须补充边界测试、事实保真测试、上下文预算测试、工具副作用测试和回放 trace。实验数据与离线评测不得自动宣称为生产能力。
