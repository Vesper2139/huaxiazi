# 表达模块边界与外部 Skill 架构

## 第一性原则

Vesper 的核心价值不是暴露更多开关，而是用最少操作把用户原始表达转换为可直接使用的结果。系统必须先保证事实、立场、隐私和输出协议，再允许个性化或 Skill 改变表达方法。

## 本轮收敛结果

| 能力 | 合并后的唯一边界 | 不负责的内容 | 用户体验影响 |
|---|---|---|---|
| 个性化上下文 | `PromptContextComposer` | 不学习、不存储正文、不决定事实 | 身份与学习偏好只注入一次，避免相互冲突 |
| Skill 运行选择 | `ExpressionSkillRouter` | 不执行脚本、不读取密钥、不决定安全规则 | 启停就是唯一运行状态，每次只自动选择一个匹配能力 |
| Skill 包生命周期 | `AgentSkillPackageService` | 不联网下载、不运行包内工具 | 支持默认导入、启停、导出、派生编辑和删除自定义项 |
| 专业化计划 | `ProfessionalizationPlanner` | 不调用模型、不渲染界面 | 只在关键歧义时追问，普通缺失信息保守推断 |
| 质量门禁 | `ProfessionalQualityValidator` | 不改变表达风格 | Skill 无法覆盖事实保真和承诺强度 |

## 保留而不强行合并的边界

`PolishWorkflowService` 与 `PromptOptimizationWorkflowService` 保持分离。前者处理澄清协议、JSON 成稿解析和归档；后者输出可直接交给模型的提示词。它们的输出契约不同，机械合并只会产生模式分支和错误耦合。

`PromptBuilderService` 与 `PolishPromptBuilderService` 暂时保留为两个模式适配器，但共同使用 `PromptContextComposer`、`PromptSecurityPolicy` 和外部 Skill 解析边界。后续若建立 `PromptComposer`，应只合并公共组装阶段，不合并两个输出协议。

审查文档提到的 `ProfessionalExpressionEngine` 在当前生产代码中并不存在；现有同名仅为测试类，因此无需删除一个不存在的运行模块。专业表达的真实执行链是 Planner → 模式工作流 → Validator。

`LegacyKnowledgeDataService` 不是知识库能力，而是旧数据的导出/明确删除兼容适配器。它不参与模型请求。待旧版本迁移窗口结束后可并入 `DataManagementService`，当前直接删除会违背“不静默删除旧数据”的升级承诺。

## 外部默认 Skill 生命周期

```text
安装包 Presets/Skills（外部只读包）
        ↓ 首次启动导入快照，不覆盖用户状态
用户目录 agent-skills（统一目录）
        ↓ 启用且模式匹配
ExpressionSkillRouter（模式 → 场景 → 专用关键词 → 通用候选）
        ↓ 仅作为低优先级表达策略
Planner / Prompt Builder / Quality Validator
```

- 默认包不是 C# 原生策略，也不绕过导入器；它们以标准 `SKILL.md` 随框架分发并在启动时导入。
- 默认包不可直接编辑或删除；“编辑”会创建用户副本，确保升级基线可恢复。
- 用户副本支持编辑、启停、导出和删除。
- 默认包固定到上游提交并记录来源、许可证和 SHA-256；更新包时保留本机启停状态。
- 运行投影会排除许可证正文、工具读取、多阶段审批和与 Vesper 输出协议冲突的章节，导出仍保留完整快照。
- 所有包只读取允许的文本参考文件；脚本、Shell、Python、MCP、浏览器和任意文件访问仍然禁止。

## 普通用户与深度用户

普通用户无需管理 Skill；三个网络预置默认启用并自动匹配。深度用户可在同一“表达能力”页面启停、导入、导出或从只读预置派生可编辑副本。不存在全局开关，也不暴露策略 ID、模型采样参数或多 Skill 组合。
