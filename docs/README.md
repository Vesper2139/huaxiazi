# 文档导航与维护规则

更新时间：2026-10-07

本目录按“当前产品事实、当前执行路线、历史证据”维护。避免多个互相冲突的计划同时指导开发。

## 当前事实与唯一执行入口

- [当前状态](STATUS.md)：最新版本、交付状态、已知缺口。
- [开发路线图](ROADMAP.md)：当前按顺序执行的工作和阶段门。
- [项目结构地图](PROJECT_MAP.md)：目录所有权、开发入口和保留边界。
- [产品架构](AGENT_ARCHITECTURE.md)：运行时职责边界、模块图和请求流。
- [开发与发布规范](development-standards.md)：构建、测试、发布和数据保护约定。
- [成熟开源模型直接采用路线](OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md)：复用预训练/指令微调完成的开源权重，不训练自有模型。

README 与本导航负责发现文档；若内容冲突，以代码、配置/Schema、`STATUS.md` 和 `ROADMAP.md` 为准，并在修正时更新冲突文档的状态说明。

## 产品与运维

- [安装与部署](安装与部署.md)、[首次使用与备份](首次使用与备份.md)、[发布检查清单](发布检查清单.md)
- [隐私与数据](隐私与数据.md)、[智能编排与质量保障](智能编排与质量保障.md)
- [表达模块边界与 Skills](FUNCTIONAL_BOUNDARIES_AND_SKILLS.md)、[专业表达引擎](PROFESSIONAL_EXPRESSION_ENGINE.md)、[皮肤系统](skin-system.md)

## AI 模型与 Provider

- [开源模型采用路线](OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md) 是当前产品方向；[本地模型研究](local-model-research-2026-09-27.md) 与 [本地运行时版本对照](local-runtime-b11429-comparison-2026-10-07.md) 是具体候选的背景/实测材料。
- [Provider 能力盘点](provider-capability-inventory-2026-10-02.md) 与 [凭据格式审计](provider-credential-audit-2026-10-04.md) 是有日期的证据记录，型号、参数和官方行为需在下一次改动时复核。
- [本地运行时制品清单](local-runtime-package-inventory-2026-10-05.md)、[安装烟测](local-runtime-package-install-smoke-2026-10-06.md) 和 [阶段二续作记录](managed-local-phase2-continuation-2026-10-07.md) 属于工程证据；用户侧结论以当前状态页和实际验收为准。

## 数据、评测与安全

- [盲评证据接收规程](blind-evaluation-evidence-intake.md) 和 `datasets/ai-evaluation/` 定义正式评测边界。
- [阶段 0 样本设计提案](phase0-evaluation-sample-plan.md) 是提案，不代表样本到位或阶段门已通过。
- 合成数据构建计划、审阅包和审计 JSON 均为数据血缘材料；逐目录 `README.md` / `manifest.json` 对具体数据版本具有权威性。不得把内部合成集计入正式盲评。
- [威胁模型](PromptFloat-threat-model.md)、[发布安全审查](PRODUCTION_RELEASE_SECURITY_REVIEW_v2.0.0.md)、[隐私审计](privacy-audit-2026-09-06.md) 和整改记录是有日期的安全证据，不是当前发布状态摘要。

## 历史计划与执行日志

以下材料保留作可追溯记录，不再产生新的当前待办；更新状态时以 `STATUS.md` / `ROADMAP.md` 为准：

- [归档目录](archive/README.md)：被替代的计划、历史实施日志和早期交付提案。
- `engineering-foundation-followup-2026-10-07.md`、`test-baseline-classification-2026-10-02.md`：工程与测试基线历史。
- `release-v2.0.0-public.md` 至 `release-v2.0.3-public.md`：各历史版本发布摘要。
- `AGENT_ARCHITECTURE_PROJECT_REPORT.md`、`AGENT_ARCHITECTURE_UPGRADE.md`、`AGENT_ARCHITECTURE_COVERAGE_MATRIX.md`、`AGENT_RESEARCH_NOTES.md`：架构盘点的过程材料；架构事实以 `AGENT_ARCHITECTURE.md` 和代码为准。

## 归档与删改

- 研究、审计、评测和人工审阅记录如仍是数据血缘或安全依据，保留并标注日期/状态，不因“旧”而直接删除。
- 已被新架构或当前交付契约取代的计划，应加“历史/已替代”说明、修复引用，再移出当前执行入口；只有确认没有数据血缘、发布、审计或外部引用后才删除。
- 新建计划前先更新 `ROADMAP.md`，不再追加第二个并行的“当前路线”。
- `outputs/` 与 `training/runs/` 是本地生成的评测/诊断产物，可能含人工审阅工作簿或逐样本输出；不进入公开源码仓库。可复现的工具、脱敏数据、审计结果和正式说明分别保存在 `tools/`、`DatasetBuilder/`、`datasets/` 与本文档体系中。
