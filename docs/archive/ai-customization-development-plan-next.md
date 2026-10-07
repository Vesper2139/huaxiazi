# 话匣子 AI 定制化：历史阶段计划与执行日志

> **历史材料，不再是当前开发计划。** 当前状态与待办以 [STATUS.md](../STATUS.md) 和 [ROADMAP.md](../ROADMAP.md) 为准；成熟开源模型的当前采用路线见 [OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md](../OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md)。本文保留逐日决策与验证证据，不应据旧日期条目推断当前状态。

制定日期：2026-10-02；进度更新：2026-10-07
依据：当前源码评估、阶段状态记录、盲评工具链现状和外部数据源调查。
计划性质：按依赖分步交付；盲评缺失不阻断无数据依赖的软件开发，只限制质量收益结论和模型权重发布。阶段状态用于记录验证范围，不作为所有后续工程工作的停工门槛。

**2026-10-07 DeepSeek Chat 契约与型号生命周期更新：** 直连官方 Chat 的当前 `deepseek-flash`、`deepseek-v4-pro` 及文档明确的两个临时兼容别名，现显式映射 thinking、JSON Mode 和请求终态；不再让 `deepseek-v4*` 任意后缀继承。产品 High 从原先错误映射的 `max` 更正为官方 `high`；Medium 映射 `high`，Low 映射 `low`。型号选择器显示 V4.1 Flash 和 V4 Pro 的当前别名路由，保留 model ID 与新建默认。Provider/AIService/DeepSeek 设置页联动筛选 **407/407**，WPF 主项目 build **0 warning / 0 error**；无真实 API 请求或质量/成本/时延测量。下一步继续补核其他尚未按精确型号映射的 Provider。详见[实施状态记录](ai-customization-implementation-status.md#阶段-1deepseek-chat-型号能力与响应终态收敛2026-10-07)与[能力盘点](../provider-capability-inventory-2026-10-02.md)。

**2026-10-07 Spark 六型号与请求边界映射：** 新建 profile 默认改为显式 `4.0Ultra`；目录列出官方六个 HTTP 型号，按型号限制 max_tokens（32,768/8,192/4,096），top_p 限制到官方 `(0,1]`（设置值 0 映射至最小正 double），保留存量 profile 与本地 Schema 校验。Spark 专项 25/25，Provider/AIService/设置摘要联合 398/398 通过；主项目 build 0 warning/0 error。无真实 API 请求，质量、费用、时延不变更声称。下一步继续核验剩余 Provider 精确能力并保持盲评轨道独立。详见[阶段记录](ai-customization-implementation-status.md#阶段-1spark-当前型号采样边界与输出预算映射2026-10-07)与[能力盘点](../provider-capability-inventory-2026-10-02.md)。

**2026-10-07 Groq 退役型号与推理响应已对齐：** Groq 新建 profile 默认改用官方建议替换 Llama 3.3 的 GPT-OSS 120B；Llama 3.3/3.1 仍保留但标注 2026-08-16 退役及企业例外，既有用户配置不重写。GPT-OSS 请求关闭 reasoning 字段返回，Qwen 3.8 设 reasoning_format=hidden，未知型号后缀不继承这三款精确型号的推理能力。Provider/AIService 联合回归 378/378，build 0 warning/0 error；未发真实 API 请求或测质量/费用/时延。下一步继续按精确型号核验剩余 Provider 参数，并保持盲评轨道独立。详见[阶段记录](ai-customization-implementation-status.md#阶段-1groq-退役型号新建默认及推理响应隔离2026-10-07)与[能力盘点](../provider-capability-inventory-2026-10-02.md)。

**2026-10-07 路由诊断可观测性已补齐：** 主生成流程的本机 opt-in 诊断现在记录策略、白名单原因及每次请求的主/显式备用角色，汇总 JSON 给出尝试计数；不记录 profile ID/名称和生成内容，兼容没有这些字段的旧记录。诊断/集成联合回归 43/43 通过，主项目构建 0 warning/0 error。下一步继续收敛阶段 1 的精确 Provider 能力边界和阶段 2 路由覆盖；质量排序尚无盲评与成本/时延依据。详见[实施状态记录](ai-customization-implementation-status.md#阶段-2本机诊断记录路由策略与显式备用尝试2026-10-07)。

**2026-10-07 OpenRouter 型号发现已落地：** Provider 设置页现在可由用户显式刷新 OpenRouter 官方文本输入/输出型号目录；固定官方 HTTPS 地址、Bearer Key、关闭重定向、限制响应大小，刷新失败保留原目录及当前模型。目录不用于推断 Schema 或 reasoning 能力，后者仍以 endpoint 证据为准。Provider 专项筛选 134/134；加入设置 UI 契约与 WPF view smoke 后联合筛选 193/193；主项目 build 0 warning/0 error；未发真实 OpenRouter 请求。下一子任务继续按精确型号和协议补充尚未映射的 Provider。实施细节见[状态记录](ai-customization-implementation-status.md#阶段-1openrouter-官方文本型号目录按需刷新2026-10-07)。

**2026-10-07 MiMo 型号与请求契约落地：** 核验发现 preset 默认 V2.5 Pro 与官方型号生命周期不一致；新 profile 现在默认 V2.6 Pro，Flash/UltraSpeed 加入目录，V2.5 仍可供存量配置选用并标注 2026-10-21 退役。能力映射按官方契约在 thinking 关闭时才发送 temperature/top_p（范围 `0–1.5` / `0.01–1.0`），thinking 开启时省略；输出改用 `max_completion_tokens`；结构化请求用 JSON Mode，Schema 保持应用本地校验。MiMo 专项回归 14/14、`AIServiceTests|ProviderPlatformTests` 联合 373/373 通过，WPF build 0 warning/0 error；没有真实 API 请求或质量收益数据。自审确认老 V2.5 ID 仍留在目录，不会把配置归一化到新默认。下一步继续按官方证据补核剩余 Provider，之后转回工作流级路由与个性化链路。详见[状态记录](ai-customization-implementation-status.md#阶段-1mimo-v2.6-型号生命周期chat-参数与-json-mode-映射2026-10-07)与[能力盘点](../provider-capability-inventory-2026-10-02.md)。

**2026-10-07 推理档位语义纠偏：** 原设置页把 Low/High 描述为“更快/更充分推理”，但 presets 只固定写入 temperature/top_p/max_tokens/timeout，各 Provider 可能忽略采样、映射 thinking 或改用 reasoning_effort。现在共享 preset 数据驱动实际配置与 UI 说明，逐档展示写入值，并将服务端真实生效行为交给当前型号 capability 摘要说明；没有更改任何请求值。预设/Custom、MiMo thinking 开关、GPT-6 Astra 默认采样/effort 和 XAML 设置契约联合回归 **6/6 通过**；WPF build 0 warning/0 error，涉及文件 diff-check 无空白错误。没有真实请求或模型质量/速度收益测量；这一步提升的是参数语义透明度。详见[状态记录](ai-customization-implementation-status.md#阶段-2推理预设的实际参数说明与-provider-能力边界2026-10-07)。

**2026-10-07 本地后端识别纠偏与驱动诊断：** 锁定 b11424 的反例证明 `offloaded N/M layers to GPU` 可能只是请求层数，Vulkan 驱动初始化失败时也会出现 `37/37`，所以现已弃用该信号。Vulkan 仅在日志出现正数 `VulkanN model buffer size` 时确认；loader 报告无 driver 且模型成功加载时确认 CPU；矛盾或不完整证据保持未知。Vulkan 子进程加入 `VK_LOADER_DEBUG=error`（保留现有级别），原始日志不存盘。定向测试 14/14；正常 Vulkan、CPU-only、隔离禁用所有 Vulkan driver 三种本地生成 smoke 均通过，第三种显示“CPU（Vulkan 驱动不可用）”，均无云请求且不保存生成正文。此前以 offload 层数确认 Vulkan 的 smoke 记录已由这轮实测纠正，详见[实施状态记录](ai-customization-implementation-status.md#阶段-4vulkan-driver-初始化失败诊断与后端证据修正2026-10-07)。仍需验证 Windows 干净环境/硬件矩阵；内部 smoke 对 phase 0 贡献为 0，不代表模型质量收益，未进入 LoRA/SFT。

**2026-10-07 Yi 能力证据补齐：** 当前零一万物官方接口页已可读取，明确 `/v1/chat/completions`、`yi-lightning`、`temperature` 0–2、`top_p` 0–1 及 `max_tokens`。Provider resolver 仅为直连 Yi 的精确 `yi-lightning` 声明这些能力；JSON Schema、推理等级、默认 `yi-large`、代理均保持 Unknown。fake-handler 与精确能力筛选 **3/3** 通过；没有真实 API 请求或质量/成本数据，输出契约仍走提示+本地校验。详见[能力盘点](../provider-capability-inventory-2026-10-02.md)。

**2026-10-07 百川请求范围纠正：** 现行官方 API v2 文档列出六个模型和边界：temperature `0–1`、top_p `[0,1)`、`max_tokens ≤2048`；其中五个型号列为支持 JSON Mode，严格 Schema 未列。修复前请求会将 `1.5 / 1.0 / 4096` 直接发给默认 Baichuan4-Turbo；现在分别约束为 `1.0 / 0.9999999999999999 / 2048`，并仅对列明型号发送 `response_format=json_object`。Provider/AIService 联合回归 **299/299** 通过，构建 **0 warning/0 error**。这降低范围错误导致的拒绝风险，不证明生成质量提升；未发真实 API 请求。详见[能力盘点](../provider-capability-inventory-2026-10-02.md)与[实施状态](ai-customization-implementation-status.md#阶段-1百川模型能力范围与请求边界纠正2026-10-07)。

**2026-10-06 阶段 3 可确认语气候选：** 按已接受结果关联的无正文风格统计，增加“正式/专业→professional、亲切→warm”语气建议。单一风格至少 3 次、无拒绝/撤销风格信号、当前范围无已确认规则且未忽略时才显示；载入后仍只是本地草稿，保存设置才进入生成上下文。服务/设置回归和 WPF 视图烟测 38/38 通过，主项目构建 0 warning/0 error。阈值未经盲评校准；不证明生成质量改善。下一步对人工审阅的本地候选做失败类型复盘；若人审尚未交回，继续阶段 4 的清洁环境依赖核查。详见[实现状态记录](ai-customization-implementation-status.md#2026-10-06按已接受输出风格提供语气偏好候选)。

**2026-10-06 输出契约锁定：** 已加 Provider 无关结构化契约 SHA-256，按必需/允许字段、字段类型、枚举、数值范围和本地答案长度校验规则规范化；描述性契约名称不参与哈希。逐样本和逐多轮样本快照记录指纹，prompt bundle hash 纳入契约；输出契约单独摘要贯通 snapshot manifest v2、candidate map v2、run-info v2、final run manifest v4。单候选 finalizer 交叉核验 run/map，cohort finalizer 要求候选输出契约束一致，manifest validator 将摘要设为必需。入口一致性、指纹/快照/候选运行/单候选与 cohort 收口/清单校验联合筛选 **95/95 通过**，主 WPF build 0 warning/0 error。下一步转回阶段 1 的 Provider 能力映射扩展，并保留“契约之外的 adapter 请求约束不得静默变化”的检查项。详见[实现状态记录](ai-customization-implementation-status.md#2026-10-06盲评快照纳入结构化输出契约指纹)。

**2026-10-06 入口与盲评提示核对：** 提示词优化缺项（澄清开/关）、足量信息正向生成、直接服务与盲评执行器回归，以及既有润色正反路径筛选 **11/11 通过**；主 WPF build 0 warning/0 error。已查明后续 Polish 快照断言差异是测试客户端只实现文本接口，强制走兼容回退；产品盲评候选的 AIService 和本地客户端均支持结构化输出。改用实际结构化客户端契约的盲评执行器/快照/兼容回退筛选 **14/14 通过**。没有改生产提示或输出行为。自审发现当前 prompt bundle hash 未包括原生请求中单独传输的 JSON Schema；下一子任务应把输出 Schema 纳入 provider-neutral 快照指纹并让 cohort finalizer 锁定，避免不同输出约束的候选被视为同一提示实验。详见[实现状态记录](ai-customization-implementation-status.md#2026-10-06盲评执行器提示快照测试改用产品实际结构化路径)。

**2026-10-06 直接推进更新：** 带单位数字事实锚点已改为数值+单位比较，接受“两天”对应 `2天`，仍拒绝 `12天`。同一 Qwen3-4B、固定参数下，两个 CPU 失败案例在修复后重跑均 Final 且无修复；同两例 Vulkan 也均 Final。随后 11 条 development 记录在 Vulkan 跑通，出现一次结构/质量修复； smoke 覆盖的是少量重复输入骨架，不是质量评估。扩展过程中发现模型会为“说明延期”补写无依据原因，并弱化“问题分析”任务；单纯强化提示后问题仍在。因此已在 `ProfessionalizationPlanner` 增加受明确用户意图约束的前置澄清，并将密钥读取和生成客户端工厂调用移到澄清判断之后。新增工厂调用计数断言先复现两条澄清路径仍创建客户端，再经重排通过；往返测试确认用户回答会进入下一次生成且不重复追问。相关定向测试 33/33 通过，主 WPF build 0 warning/0 error。最新结果、延迟、token、工作集及 artifact 哈希见[实现状态记录](ai-customization-implementation-status.md#阶段-4-qwen3-4b-内部开发切片与事实校验器误拒修复2026-10-06)。这仍是内部合成 smoke，人工核验未完成，phase 0 贡献为 0；不据此宣称本地模型质量提升或发布性能基准。

**2026-10-06 工作流层澄清保护：** 复核发现上述本地 smoke 直接调用 `PolishWorkflowService`，绕过主界面的澄清前置检查；服务自身收到缺关键事实的“问题分析”请求仍会生成。已将澄清检查下沉到工作流入口；未提供预制计划时，从请求正文、目的、收件人、场景和正式度构建计划。澄清在归档依赖检查及模型生成前返回，使主界面、直接工作流调用和本地候选 smoke 共用保护。新增测试先证明旧实现返回 Final；修复后提示构造、润色工作流和专业化规则定向组合 44/44 通过，缺证据时返回具体问题且生成请求数为 0。该改动不依赖盲评数据、不证明输出质量提升；对触发规则的缺信息请求，生成请求由 1 降为 0，从而消除该次模型调用的推理延迟和 token 消耗。阶段 0 贡献仍为 0。

**2026-10-06 真实本地服务层烟测与脱题拦截：** 相同 Qwen3-4B/Vulkan 固定参数下，`000003`/`000008` 缺关键信息时均在生成前澄清、生成请求 0 次；充分事实正控 `000007` 正常 Final，保留日期、条件和 2 天。澄清关闭时模型仍将 `问题分析` 改成进度汇报并输出占位；增加显式任务信号和占位校验后，修复仍失败的结果转为 `Invalid` 并不展示。定向回归 50/50 通过。前向任务约 1,102 tokens/7.02 秒；脱题案例修复尝试合计 2,130 tokens/12.90 秒后阻断。此为内部合成行为烟测，不能当质量评分；当前信号检查只覆盖明确“问题分析/说明延期”，关闭澄清时的失败请求成本和 UX 是下一步优化对象。机器报告见[实施状态记录](ai-customization-implementation-status.md#2026-10-06润色工作流统一执行缺事实前置澄清)。Phase 0 贡献仍为 0。

**2026-10-06 澄清关闭路径继续推进：** 针对澄清关闭时“问题分析”无证据导致本地模型两次生成后仍被拦截的问题，MainViewModel 和 `PolishWorkflowService` 现在在生成前返回明确缺项，不创建客户端、不发请求、不弹澄清问题；信息充分的延期说明保持一请求完成。定向组合 **80/80 通过**，WPF build 0 warning/0 error。同一固定 Qwen3-4B/Vulkan 参数下，000007 正常 Final（693+409 tokens，约 6.83 秒）；000008 缺问题信息时零生成请求、0.87 ms 工作流返回。相较上一轮失败路径节省该案例约 2,130 tokens/12.90 秒生成时间；smoke harness 预先启动 runtime，该数字不含约 3.24 秒启动。完整记录和 hash 见[实施状态记录](ai-customization-implementation-status.md#2026-10-06澄清关闭时对已知缺事实任务做零请求预检)。本地样本未人工审核，Phase 0 贡献 0，不代表整体质量收益。

## 1. 当前判断

项目已有较完整的离线评测工具基础：盲评数据准入、双人标签/争议裁定结构、PII 启发式检测、逐边界记录完整性复核、cohort 收口和清单校验均已实现。cohort 同条件门禁会拒绝提示束或采样参数不一致的候选。当前 split 隔离修改后，`FullyQualifiedName~BlindEvaluation` 相关筛选 146/146、扩大到 `FullyQualifiedName~Blind` 的候选执行/盲评组合筛选 182/182 通过，包含 report/manifest split 一致性、候选运行 finalizer 端到端路径和 split 外 comparison 拒绝。这证明所选工具链路径的回归行为，不代表模型质量，也不证明评审者身份、独立性或数据授权真实。

阶段 0 **仍未通过**。`datasets/ai-evaluation/blind-eval.jsonl` 不存在；尚缺至少 500 条授权/许可、脱敏、语义族隔离的盲评样本，受控存档的独立原始评审提交与身份/时间核验，以及冻结提示和参数下的云端、本地产品基线。此前合成样本、公开数据调研和小规模本地实验均不能代替这些证据。公开中文数据调查未发现可直接满足通用润色评测、权利和污染隔离条件的数据；因此优先构建授权自有评测集，不从许可或适用范围不清的公开集拼凑门槛。

当前开发继续沿统一契约、显式路由、可控个性化、本地运行时交付推进，评测证据链并行补齐。缺少盲评时仍可实现和验证软件行为，但不得把单测、合成集或工程完成度说成质量收益；LoRA/SFT 与模型权重发布继续依赖授权数据和独立评测。

**2026-10-05 直接推进更新：** 结构化 Provider 拒绝的本地校验回退现覆盖 HTTP 400/422 的 `RequestRejected`，WPF smoke 的 SQLite 数据根已隔离到测试输出目录；DatasetBuilder、AIService、本地运行时、润色工作流与 WPF smoke 联合回归 155/155 通过。真实 Provider 请求数仍为 0，本地盲评状态未变；这些条件不阻断其它无依赖工程实现。

**2026-10-05 本地运行时继续推进：** `LocalRuntimeManager` 已按 GPU 设置排序候选：Auto 可先试 Vulkan、失败转 CPU；Off 只使用 CPU；用户取消不回退；最终 CPU 异常原样保留。默认 runtime 根位于 `%LOCALAPPDATA%/Huaxiazi/runtimes/local`，兼容已有便携目录；本地运行时、健康检查、候选工厂组合筛选 26/26。b11424 CPU/Vulkan prerelease ZIP 已实际下载、比对 SHA-256、通过限定 signer/repo/source-ref 的 attestation 验证，并检查归档成员和 `llama-server.exe --version`；两包均缺 llama.cpp 主 MIT 文件但带 OpenMP notice。关键自审发现 launcher 报告 commit `6c59…`，attestation 记录的 publish-workflow source 为 `c06f…`；因发布 workflow 先下载前序构建 artifacts 再签 attest，当前不能把 publisher source 当作 compiler source。详见[制品核验记录](../local-runtime-package-inventory-2026-10-05.md)。下一步在不误报来源的前提下实现只接受应用 pinned digest 的独立下载/安全解压/原子安装，并将候选标为 prerelease/source 未映射；随后补完整许可证清单及真实 CPU/Vulkan 启动/Schema 检查。当前没有模型加载、HTTP health 或硬件性能数据。

本轮 P2 补充了模型级采样参数风险：现有 `AIService` 向多个 Provider 协议统一发送 temperature/top_p，而当前 OpenAI GPT-6 Astra 与 Anthropic Opus 4.6 后模型对这些字段有明确限制，通用数值 clamp 不能证明请求有效；OpenAI Chat 对推理模型还要求按能力使用 `max_completion_tokens`，旧 `max_tokens` 与 o-series 不兼容，且新上限包含隐藏 reasoning token；Low/Medium/High 档当前多数后端仅改采样、输出上限和超时，并未调整 reasoning budget。**P2-A 已完成**：Provider 能力盘点新增云端字段矩阵、llama-server 启动参数/本地请求映射、模型级 unsupported/unknown 风险与 UI 档位语义依据，代码及官方资料逐项可追溯。**P2-B 输入与验收设计已完成**：能力查找必须绑定 provider/platform + protocol/API version + 映射后的 exact model ID；OpenRouter/custom/Ollama/LM Studio 同名 ID 默认为 unknown；ManagedLocal 绑定 runtime build/hash + model/template。盘点文档包含 9 类 fake-handler/本地运行时场景及静态表、在线探测、用户覆写三种方案的取舍，建议“有来源和日期的静态能力事实 + unknown 默认 + 可见高级覆写”。量化验收为 unsupported 请求发送禁用字段 0 个、supported 值与用户确认值一致、unknown 不静默标为已生效、reasoning token 上限导致可见截断可识别、所有断言用 fake transport 且真实 Provider 请求数为 0。阶段 0 未通过前不增加这些测试/生产适配。P2-C 的 v21 固定磁盘读取基线已补（见下），未来字段的默认/Clone/Normalize/迁移测试仍须随字段实现。触发条件：授权盲评和基线门槛通过后，才把输入矩阵落为阶段 1 请求契约测试和 adapter 实现；若兼容 Provider 无官方或运行时证据，保留 `unknown`，不猜测支持。

**2026-10-03 官方能力复核：** 复查 OpenAI、Anthropic、Gemini 与 llama.cpp 当前官方页面后，已在[Provider 能力盘点](../provider-capability-inventory-2026-10-02.md)追加有来源的修订：GPT-6 Astra 自定义采样参数应省略且不依赖 reasoning 档位；Anthropic 测试族更新覆盖当前列出的 Opus 4.7/4.8，并将 top_p 兼容值与真正生效区分；Gemini REST structured output 采用 `generationConfig.responseFormat.text` 新形状并标记旧 Schema 字段 deprecated；llama.cpp 的 `reasoning_effort` 仅传给模板，不能推断通用推理控制。此前表述若与此段冲突，以本次复核为准。此更新不改生产请求，阶段 1 capability 测试保持冻结至阶段 0 通过。

WPF 宿主诊断的最新结论：Run L 在同一实际 WPF Application/STA/Dispatcher 上用 CompanionFace 资源与反馈计时器完成 100 轮窗口创建/关闭，未复现既有 FailFast。Run M/N 将反射重置 WPF Application 单例后跨 STA 重建的失败稳定复现于 `Application.GetResourcePackage(Uri)`，但该序列违反每 AppDomain 仅一个 Application 的 WPF 生命周期契约；子进程试验已完成，相关测试已从默认测试源码移除，不能作为产品生命周期回归或原 `HwndSubclass` 崩溃的根因。全量 `HwndSubclass` 根因仍未证实；Run P/Q 的最新全量记录为 1,118 通过、4 个产品契约失败、11 跳过、0 中止，本轮未复现宿主崩溃。若后续继续处理，先为合法的单 Application 长期 STA 生命周期定义回归并估算迁移覆盖，再决定是否改测试基架；不要重复 Run N 或混淆两个崩溃栈。详见[测试基线分类报告运行 M/N/P/Q](../test-baseline-classification-2026-10-02.md#运行-n-testhost-中三次重建-application-的子进程复核)。

**P2-C 配置兼容复核：** `ConfigService` 当前仅显式接纳 v20→v21 迁移；v21 Save→Load 往返有测试，但测试集中没有固定的 v21 JSON 文件直接加载用例。新增可选且有安全默认的模型能力/路由字段优先保持 configVersion 21；字段缺失时分别为 `unknown`/`Manual`，并覆盖 Clone/Normalize/Profile 副本。只有改变旧字段语义的非兼容改动才走显式 v21→v22 迁移。新 fixture 验收会锁定多 Provider、活动 profile、model mapping、采样参数和有效 SecretId 往返不变，并证明不生成 v21 reset backup。该本轮是源码与测试库存核查及验收设计，未发现当前行为红测，不新增空洞测试；待阶段 1/2 真正添加字段时，将此 fixture 与实现按 TDD 同提交。旧 v0–v6 有意重置行为仍保持原约定。

**P2-C 进展更新（2026-10-03）：** 已将上述 v21 磁盘 fixture 验收提前作为兼容性基线补齐，不等待未来路由/capability 字段。固定 JSON 含 OpenAI 与 Anthropic 两个 profile、非首个活动 profile、不同协议/API Base、模型映射、采样/超时参数和有效 `provider-{id}` SecretId。直接 Load 及 Load→Save→Load 后均核对关键字段不变，且不创建 `.v21.ignored`。首次测试因嵌套 ProviderProfile 字段误写 camelCase 而失败；源码 `ProviderProfile` 没有 camelCase 命名策略，真实 v21 wire format 使用 PascalCase。修正 fixture 后定向 **1/1**、完整 `ConfigServiceTests` **14 通过、11 个既有历史契约跳过**。该错误在测试数据中而非产品代码；未改 ConfigService、配置版本或运行时行为。未来新增字段仍须按约定覆盖缺省安全值、Clone/Normalize 和迁移/回滚。

### 最新进度补充

16. **frozen_test 确认状态进入最终证据链：** 运行服务此前执行前检查确认，但 run-info、sealed map 和最终清单没有保存该确认。现将 run-manifest 升为 v3；候选执行写入运行记录和身份封存映射，单候选/cohort finalizer 校验两者一致，含 frozen_test 时须为 true，并将值写进最终清单；独立 validator 同样拒绝未确认冻结集。cohort 成功/拒绝路径测试 9/9、单候选 finalizer/validator 与 cohort 联合筛选 43/43 通过。阶段 0 外部证据门槛仍未通过。
17. **修复候选工作流对系统临时目录的隐式依赖：** 之前候选润色流程即使 `autoArchive=false` 也会创建系统临时目录并初始化 SQLite 归档库；系统临时目录不可写时，异常被记录成 `provider_error`，且 Provider 未收到请求。`PolishWorkflowService` 现只在自动归档实际启用时要求归档服务，且自动归档缺少服务时会在 Provider 调用前拒绝；盲评执行器移除不会被使用的临时归档库。候选运行服务回归 12/12 通过，包含原失败冻结集成功路径；这使受限环境的盲评执行更可靠，不改变正常产品归档路径。
18. **润色工作流测试隔离目录改为项目内临时工件区：** `PolishWorkflowServiceTests` 也将 SQLite fixture 根目录创建在不可写的系统 Temp，导致 8 项归档/非归档用例在到达工作流断言前就失败。fixture 改用 `out/test-artifacts`，`Dispose` 仍负责删除每个独立目录；候选执行、润色工作流、单候选/cohort finalizer 和 manifest validator 联合筛选 65/65 通过；加入快照编译、准入/评分/来源登记与盲评工作流后为 146/146 通过。
19. **区分 runner 错误与 Provider 错误：** 系统临时目录故障曾被归为 `provider_error`，会污染可靠性对比。新增规范化类别 `runner_error`，用于 UnauthorizedAccess、IO 和参数类执行异常，并加入 prediction schema/scorer 白名单及端到端计数回归；候选运行、评分、单候选/cohort 收口和清单校验联合筛选 65/65 通过，扩展盲评完整相关筛选为 146/146。评测报告可分别统计工具链故障与模型服务故障。
19. **区分 runner 错误与 Provider 错误：** 系统临时目录故障曾被归为 `provider_error`，会污染可靠性对比。新增规范化类别 `runner_error`，用于 UnauthorizedAccess、IO 和参数类执行异常，并加入 prediction schema/scorer 白名单及端到端计数回归；候选运行、评分、单候选/cohort 收口和清单校验联合筛选 65/65 通过，扩展盲评完整相关筛选为 147/147。评测报告可分别统计工具链故障与模型服务故障。
20. **校验独立评审与第三方裁定的时间顺序：** 证据索引此前绑定提交时间和同一盲包，但允许 adjudication 时间早于任一评审提交。现在在两份时间戳都符合严格 UTC 格式时，拒绝早于较晚评审提交的裁定时间，并提示可信时间仍需外部审计；反例回归新增后证据清单校验器 18/18、完整盲评相关筛选 147/147 通过。该检查只排除内部矛盾，不证明时间真实。
20. **校验独立评审与第三方裁定的时间顺序：** 证据索引此前绑定提交时间和同一盲包，但允许 adjudication 时间早于任一评审提交。现在在两份时间戳都符合严格 UTC 格式时，拒绝早于较晚评审提交的裁定时间，并提示可信时间仍需外部审计；反例回归新增后证据清单校验器 18/18、完整盲评相关筛选 149/149 通过。该检查只排除内部矛盾，不证明时间真实。
21. **阻止未结例外清单进入 `reviewed` 状态：** 清单校验器此前允许授权/独立性/准入/封存都填为完成，同时仍保留 `open` 或 `rejected` 例外。新回归先复现该状态矛盾，现由运行时 validator 与 JSON Schema 同步要求 `reviewed` 清单无这两类阻断例外；已解决及历史 `withdrawn` 条目仍需按数据快照/权利复核流程解释。证据清单校验器 20/20、完整盲评相关筛选 149/149 通过。清单的人工声明和签名真实性仍不由本地工具证明。
22. **split 隔离贯通到独立校验器与候选运行集成：** 新 pairwise 包和 `blind-evaluate` 明确绑定单一 split；单候选/cohort finalizer 按该 split 重算。后续审计发现独立 manifest validator 原先只验证 report admission 和哈希，未交叉核对 split；新增报告 split、清单 role/样本数与 comparisons gold split 检查。更宽 `~Blind` 筛选又发现候选执行 finalizer 集成夹具缺少 `evaluation_split`，补充显式 development 诊断报告后恢复成功路径。红测分别复现 split mismatch 两项及 runner finalizer 集成失败；最终 `~BlindEvaluation` 146/146、`~Blind` 182/182 通过。合成 400/100 切片下 frozen 输出仅 100 条；这是隔离正确性，不是 AI 质量或真实费用改进。旧 manifest 缺 split 扩展字段仍按历史兼容路线校验；阶段 0 外部证据门槛保持未通过。
23. **统一人工输出评分的跨字段语义：** 手册将直接可用布尔值定义为“不需实质重写”，但旧 rubric 只建议其与 1–5 评分相符，且评分 Schema 没有可绑定的例外备注字段；旧 scorer 也接受 `directly_usable=true` 搭配 3 分。现将 4 分设为通过阈值（4–5=true、1–3=false），并强制高风险关键事实反转对应保真 1 分；prediction contract 升至 v2，Schema/scorer/手册/README 同步。三条新回归先按预期失败，`FullyQualifiedName~Blind` 经修复测试夹具后 **185/185 通过**；六个有效/冲突实例通过 PowerShell Schema validator 检查。该项只提高未来人工评测结果一致性，无真实数据质量结论，不解除阶段 0 门槛。详情见[实现状态](ai-customization-implementation-status.md#阶段-0-候选评分跨字段一致性2026-10-04)。
24. **补全阶段 0 已知凭据格式拦截：** 审计发现“疑似密钥”正则只识别 `sk-` 和 `api_key`，漏过 AWS `AKIA`/`ASIA` ID、Slack `xox*` token、HTTP Basic/Bearer Authorization 和 PEM 私钥头。按 TDD 增加 6 个正向格式用例，旧实现 6/6 红；实现后与 6 个术语/不完整/错误格式负例一并通过，审计器测试 **43/43**、扩大后的盲评链路 `FullyQualifiedName~Blind` **197/197**。README 同步说明范围及“不完整、不能判定有效性”的限制。此为准入数据与模型预测产物隐私门禁，不报告质量提升、不解锁阶段 0；来源与验证详见[实现状态](ai-customization-implementation-status.md#a0-隐私准入审计扩展常见凭据格式2026-10-04)。
25. **覆盖 Gemini/Groq 的已核对裸 key 前缀：** 复核 Provider 目录发现应用直接支持 Gemini 和 Groq，而先前扫描不能识别无标签的 Google `AIza…` standard key 与 Groq `gsk_…` key。新增正向回归后旧实现 2/2 漏检；补上 Google standard key 固定 39 字符模式与 Groq `gsk_` 20–255 字符 heuristic，并覆盖短格式负例。`BlindEvaluationAuditTests` **48/48**、扩大后的 `FullyQualifiedName~Blind` **202/202**。Google 官方文档说明 2026-05-28 起新建 AI Studio key 默认转为 authorization key，但未在查阅资料中给出稳定前缀，故 README 明确其未带标签/header 时不保证检测。未报告实际隐私拦截率或模型质量提升；详情见[实现状态](ai-customization-implementation-status.md#a0-继续覆盖项目-provider-的密钥格式2026-10-04)。
26. **确认 Provider key 检测贯穿候选输出隔离：** 审查证实 `BlindEvaluationScorer` 复用 `ContainsPotentialSensitiveData`，但原有 candidate-output 回归只覆盖邮箱/手机/微信。新增 Google `AIza` 与 Groq `gsk_` 输出案例，验证产生 `pii-output`、评分拒绝且问题内容不回显。`BlindEvaluationScorerTests` **20/20**，扩大后的 `FullyQualifiedName~Blind` **204/204**。这是输出评分隐私边界的回归覆盖，不代表线上拦截率；未知 Gemini authorization key 格式和未核验 Provider key 仍有检测边界，阶段 0/1 状态不变。详情见[实现状态](ai-customization-implementation-status.md#a0-2-候选输出也扣留新识别的-provider-key-2026-10-04)。

### 本轮计划调整

运行 J 的两份 dump 显示 STA/WPF `HwndSubclass.SubclassWndProc` → 资源本地化递归 → `Environment.FailFast`；Settings 保存线程当时在另一条 xUnit 工作线程等待，不能据此认定其为原因。运行 K/L 未在合法的单 Application 生命周期探针中复现该栈。运行 M/N 复现的是测试 helper 反射重置 Application 后跨 STA 重建时的另一类 `GetResourcePackage(Uri)` FailFast；WPF 契约复核确认该测试序列不合法，N 子进程回归已完成并撤下，不再作为待办。Run P/Q 最新普通全量与逐类隔离均为 1,118 通过、4 个产品契约失败、11 跳过、0 中止；这次未复现 WPF FailFast，但不构成根因修复证明。不得再把两类崩溃当作同一根因；只有出现符合 WPF 单 Application 生命周期的可重现问题并完成覆盖迁移评估后，才调整测试基架。阶段 A 的外部盲评、权利、独立评审证据及云/本地基线仍未齐备。

运行 I 已按 103 个测试类隔离进程覆盖 1126 项：1115 执行，1107 通过、8 项已知契约失败、11 项 `NotExecuted`，0 宿主中止。这验证了进程隔离可避开本次 WPF 宿主崩溃，但普通单进程全量仍未通过，不能将类隔离结果作为发布基线或 WPF 根因修复。

1. **已完成：** cohort finalizer 对所有候选逐项比对组合提示、system/user 提示束和 temperature、top_p、max_tokens、inference_level；不一致即拒绝生成 cohort 清单。提示/采样探索须建立独立 cohort。定向用例 6/6、盲评筛选 54/54 通过。
2. **当前首要工作仍是阶段 A：** 取得并受控审查授权样本及独立评审原始凭据，建立可追溯的冻结集；在证据可用后再跑云端/本地产品基线。工具通过率不再作为阶段推进代理指标。
3. **并行的工程治理工作：** 子进程隔离当前有成熟的按类回归工具；Run M/N 证明的跨 STA 反射重建失败违反 WPF Application 单例契约，其子进程测试已撤下，不能用它驱动产品或测试基架重构。Run P/Q 的最新普通全量和按类隔离均为 1,118 通过、4 项发布/安装契约失败、11 跳过、0 aborted；当次没有复现普通宿主 FailFast，根因仍未证实。4 项失败是发布/安装实际交付契约待裁决，不得为消红单方面改实现或断言。此前悬浮球窗口尺寸和猫蝶 Curious 资产映射的 4 项已按现有产品定义修复并定向通过。若新证据要求继续调查 WPF，先设计合法 Application 生命周期的可复现回归并计算迁移覆盖/成本；阶段 0 外部数据门槛不变。
4. **P2 接口与兼容盘点已完成：** 新增[Provider 能力盘点](../provider-capability-inventory-2026-10-02.md)，源码确认两条产品工作流都尚未接入已有结构化生成入口；Anthropic/Gemini 当前 Schema 参数未映射；通用 OpenAI-compatible 路径也未按模型能力判别；OpenAI Responses 目前只有部分响应解析，没有对应请求 adapter。另记录了配置版本 21 的严格加载策略、20→21 唯一迁移、profile 手动 Clone 和旧配置兼容要求。该盘点还给出阶段 1 分步方案及官方协议差异。它是实现输入，不解锁阶段 1 生成行为改动。
5. **阶段 A 证据收口工具已增强：** 新增 `blind-evidence-validate` 与 `evidence-manifest.schema.json`。它校验清单绑定的 dataset 字节哈希、协议/样本数、每条授权来源引用、盲包哈希、两份评审提交及第三方裁定引用，并与训练/开发集复跑当前记录完整性和泄漏检查；报告分离记录完整性与外部核验声明，固定说明工具未认证保管库、身份、授权或可信时间，`phase_0_gate_passed` 固定为 false。清单声明为 verified 不会被自动提升为事实。
6. **工具链进展：** BlindEvaluationRunner 支持 ManagedLocal 候选并复用产品运行时管理器和本地生成客户端；需显式指定 runtime root，模型目录可用产品默认路径或覆盖。`LocalTextGenerationClient` 可选记录本地响应 usage 与不含内容的 HTTP 调用时延，计时在运行时启动后开始；`LocalRuntimeManager` 提供启动到健康检查 ready 的时长与 llama-server 进程峰值 working set，并由 runner 写入现有评测字段。ManagedLocal 候选现流式核验模型/LoRA/运行时 SHA-256，并将 provenance 贯通封存映射、单候选和 cohort 最终清单。前者是启动/加载代理，峰值 working set 不含 Vulkan 显存；哈希验证用合成文件通过，实际模型和固定运行时/目标硬件仍未测。
7. **暂缓：** Provider 生成契约重构、自动路由、偏好学习和 LoRA/SFT。只允许不改变生成行为的可逆准备；阶段 A 门槛完整通过后，按 B→C→D→E 顺序逐段验收。
8. **阶段 A 文档准入件已成稿：** 新增 v1.0 标注手册、授权来源登记 JSON Schema/模板和去重/冻结规程，定义 gold 决策、评分尺度、成对评审、family 级切分及受控引用/留痕字段；已在接收规程和数据集 README 中关联。手册仍需评审校准，Schema/模板不构成真实数据、授权或独立评审证据，阶段 0 不因此解锁。
9. **来源登记校验入口已实现：** `source-register-validate` 交叉核对登记字段、dataset `source_ref` 覆盖、来源类型、候选推理/人工评审用途声明、日期窗口、可选来源记录 hash 和声明为 `verified` 时的复核字段；报告将结构完整性与人工复核声明分开，工具认证和阶段 0 通过字段固定为 false。盲评链路定向回归 78/78 通过；尚无真实 `blind-eval.jsonl`，不代表真实授权已通过。
10. **费用度量进一步校准：** 单候选/cohort 收口会将 API 成本初始化为云端 `not_reported` 或本地 `not_applicable`；validator 支持仅在具备费率卡/账单记录受控引用及 SHA-256 时接收 `estimated` / `provider_billed`，并拒绝费用状态与 Cloud/Local Provider 不一致的清单。复核官方 usage 字段后，现将缓存读取/写入 token 从 Provider 响应贯通至请求遥测、盲评汇总和最终清单；CLI 白名单接受新遥测字段，prediction schema 与可空运行时遥测一致，负数被评分器拒绝，缺失细分仍记为未知，遇到样本不完整不报部分合计。500 条合成 CLI 回归验证字段贯通；另有跨样本缺失缓存细分时保持 null 的回归。token 数不被伪装成费用。未发起云请求或改变生成行为。
11. **评测金标数据最小化：** 候选工作流此前虽只把白名单输入字段编译为 Provider 提示，但执行请求对象仍保留完整 gold/评审记录。现仅将匿名记录 ID、任务与 split 传入执行器，并以 gold/评审/context 哨兵验证提示隔离；快照与工作流/候选执行关联回归 118/118 通过。该边界加固不解锁阶段 0，也不改变生产生成行为。
12. **隐私启发式补齐微信联系标识：** 盲评 gold 和来源登记此前检查大陆手机号、身份证号、邮箱、QQ 联系标识及疑似密钥，但没有显式微信号规则；候选输出评分也复用该缺口。新增明确微信号/微信账号/WeChat ID 标签与 `wxid_` 前缀检测并同步评分诊断文案；新红测 3/3 复现旧缺陷，关联盲评/来源登记/候选评分回归 125/125 通过。启发式仅触发人工核验，不能替代脱敏审查或完整识别个人信息。
13. **扩展 PII 审计到标识元数据：** 准入审计器原先不扫描样本 ID、语义族 ID和来源引用，且逐记录诊断会原样使用样本 ID。当前将这些字段加入与输入/gold相同的敏感数据启发式；检测到敏感 ID 时准入/评分逐样本问题报告均改用固定占位符。邮箱样本 ID、手机号语义族 ID和邮箱来源引用三字段红测复现后，准入审计器分支验证通过；评分器独立 PII 输出报告的 ID 泄漏也经红测复现并修复。Schema 仍负责结构检查，启发式不能替代人工脱敏。
14. **评分器诊断 ID 脱敏完成：** 对 `BlindEvaluationScorer` 的逐记录、成对比较和重复样本问题统一调用安全诊断 ID 映射，且重复预测问题不再把 candidate ID 拼入错误文案。敏感预测 ID 泄漏红测先失败后通过；盲评、评分、来源登记、候选执行与收口联合筛选 127/127 通过。未运行全量应用测试，阶段 0 仍缺外部准入证据。
15. **frozen_test 确认下沉到运行服务：** 之前 CLI 会要求解封确认，但公开 `BlindCandidateRunService` 本身未要求确认，也未核对被运行记录和 `split` 一致。现在由 CLI 传递显式确认并由服务在副作用前再次强制；split 非 `all` 时逐条核对。未经确认（直接 `frozen_test` 与含冻结样本的 `all`）、错配拒绝、确认后允许执行及快照选择器相关筛选 18/18 通过；盲评/候选执行/收口关联筛选 131/131 通过。该门禁防止意外解封/错误封存，不代替冻结测试集真实受控保管。

## 2. 目标与边界

目标是建立可复跑、可比较、用户可控的定制链路，使润色和提示词优化在事实保真、输出可用性、风格遵循、隐私和成本之间可度量地改进。

持续约束：

- 手动选择 Provider/profile 继续作为默认行为，旧配置、历史、偏好和密钥兼容。
- 云端与本地并行；仅本地策略任何失败都必须 fail closed，禁止静默云端回退。
- 默认不上传用户内容、不启用内容遥测、不从设备汇集训练数据。
- 本地运行时和权重作为独立、可校验、可卸载下载包，不扩大主安装包。
- 每阶段先验收再进入依赖阶段；未过阶段 0 前不改变生产生成行为、不上线自动路由或学习偏好。
- 下列评测数值是目标线，不是当前已达到的结果。真实用户数据需经许可、脱敏和明确用途授权。

## 3. 分阶段开发与准入

### 阶段 A：补齐评测准入与基线证据（阶段 0 的收尾）

**可立即开展的工程工作**

1. 定义受控评审提交包：每名评审单独提交原始标签、提交时间、评审者受控身份引用和盲包哈希；身份映射与数据分开存放，只由评测保管角色持有。保留原始提交只读副本和校验摘要。
2. 在现有 v3 双评/裁定格式上补充“证据索引”而非在普通 JSON 中声称身份已验证；准入报告需区分 `record_integrity_valid`、`rights_reviewed`、`reviewer_independence_verified` 三项状态。缺一项都不标记阶段 0 通过。
3. 将标注手册、授权来源登记、去重/语义族规则、冻结集保管流程和变更记录版本化。评审包不得暴露候选身份、gold、参考答案或评审证据。
4. 完成一份可复跑的基线命令/配置模板：固化任务、系统提示版本、provider/model 精确 ID、采样和最大输出参数、时间、运行时版本、硬件、输入/输出哈希与指标。禁止把正文、成稿和个人偏好放进遥测或普通日志。
5. 已开始分类历史全量测试失败：先前 966 项的汇总无原始 TRX，不能逐例复核。本轮已记录三次新运行，并修复空配置时默认数据根未经可写探测的问题；完整结果从 1045 项中的 98 失败降为 14 失败（1020 通过、11 跳过）。剩余失败已按环境、发布契约、窗口几何和素材映射分类，见[测试基线失败分类报告](../test-baseline-classification-2026-10-02.md)。发布与 UI 冲突仍待需求确认，不在本 AI 定制阶段改写其行为。

**数据与评测执行**

1. 获取不少于 500 条有权用于本评估的样本；以语义族为单位切分 development/frozen_test，禁止近重复、同源变体或输入泄漏跨 split。按既有准入协议覆盖两类任务、风险、常规表达、澄清、格式要求、事实/约束锚点和多种输入风格。
2. 对每条样本执行两名评审独立标注；标签一致时保存双方原始标签，分歧由第三名评审裁定。核查实际原始提交及时间/身份隔离，不能仅依赖 JSON 中的 reviewer ID。
3. 冻结提示词、参数和产品输入组装后，对当前润色/提示词优化生产路径及代表云/本地候选做同条件基线。固定 model/version、运行时、硬件、温度、上下文与采样参数；候选身份在盲评完成前封存。冻结测试集只在比较设置锁定后使用。
4. 同时报出整体与任务/风险/格式切片结果、失败数、评审一致率、成对偏好及置信区间、Schema 合法率、直接可用率、事实/约束保留率；API 记录延迟/用量/费用字段，本地按硬件报告加载时间、p50/p95、tokens/s、峰值内存。

**准入门槛**

- 评测来源权利审查完成，脱敏审查有记录；盲评样本数量、配额、语义族隔离和 train/dev 泄漏验证通过。
- 两名评审的独立原始提交、时间和受控身份引用可审计，争议裁定完整；盲测解封清单与数据/提示/预测/比较材料哈希可重验。
- 当前生产流程及候选模型完成同条件基线，配置、缺失数据和限制均可复跑、可追溯。

**停止条件**：授权样本或独立评审来源暂不可得时，暂停盲评基线、质量收益声明和模型权重发布；继续实施不依赖这些数据、且可由软件契约验证的生成与服务改进。不得用合成数据宣称阶段完成，也不得将工程行为测试解释为模型质量证据。

### 阶段 B：统一生成契约与输出质量门禁（阶段 1）

阶段 A 通过后，先为润色与提示词优化定义 Provider 无关的请求/响应及完成状态，包括内容、结构化结果、停止/截断/拒绝状态、规范化错误、用量、耗时、请求标识和实际模型。合并两条工作流的上下文层级：稳定 system/developer 指令、事实/约束锚点、Skill/任务说明、用户本轮要求、经用户确认的个性化、输出 Schema；保留现有各业务所需字段和兼容路径。

为 `PolishResponse` 定义明确 Schema，并在应用侧无条件做解析和语义质量校验。能力声明逐 Provider 实现；原生 Schema 能力可用时使用，不可用则做本地严格解析、校验和最多一次受控修复，失败时呈现可理解错误，不将部分/拒绝输出当成成功文本。

- OpenAI 保留现有 Chat Completions 适配器，并将 Responses API 作为独立可选适配器逐工作流对比后再迁移；Responses 的结构化格式使用 `text.format`，Chat Completions 使用各自的 `response_format`。不要把 Responses 当成所有 Provider 的通用接口。
- Anthropic、Gemini、OpenAI-compatible 和 llama.cpp 各自声明支持的 Schema 子集与采样/推理参数映射；不能只根据“OpenAI 兼容”标签假设语义完全一致。
- 错误区分拒绝、截断/长度上限、Schema 不支持、解析失败、网络/鉴权错误和服务端错误；无效 Schema 在发请求前发现。
- prompt caching 只作为稳定前缀和成本/延迟优化候选；先测 cache hit、cached tokens 与总成本，不将缓存视作质量改进或默认数据治理策略。

**退出门槛**：两条工作流共用契约和校验入口；各 Provider 映射有契约测试；原生结构化输出和降级解析均能正确处理拒绝、截断、无效 JSON 与 Schema 不匹配；旧 profile 与历史配置可读写迁移；无评测退化。

### 阶段 C：落地用户控制的模型路由（阶段 2）

默认保持 Manual。设置按任务绑定既有 Provider profile，另提供 Manual、仅本地、优先本地、优先云端策略；本地策略明确是否允许备用项，且本地-only 绝不允许备用到云端。只有用户启用自动路由时，才由可解释规则按任务风险、复杂度、预算/延迟目标、能力和隐私策略筛选候选。风险高或上下文不足时可要求用户确认，禁止隐藏切换。

路由决策需是独立纯逻辑模块：输入任务特征、用户策略、能力描述和已配置候选；输出选中 profile、可解释原因、未选原因及是否允许回退。执行结果持续显示实际 Provider/profile/model 与回退原因。日志只保存不含内容的元数据，且本地-only 测试通过禁止网络请求的 fake transport 验证边界。

**退出门槛**：Manual 旧行为不变；策略优先级、风险、预算、能力映射和回退有自动化覆盖；本地-only 故障不产生云请求；UI 的实际模型标识和错误状态与执行事实一致。

### 阶段 D：建设可查看、确认与清除的个性化（阶段 3）

把偏好转为按任务/场景隔离的结构化值（如篇幅、正式度、保留原话、常用场景、禁用表达），含来源、更新时间、置信度、用户确认状态。每轮明确指令高于长期偏好。学习信号综合显式风格选择、接受、编辑、重试、撤销，区分一次性编辑和稳定偏好，不根据单一长度变化自动写入偏好。

提供查看、编辑、确认、重置、全部清除和无痕入口；未知偏好以“建议”呈现，用户确认后才提升为长期规则。所有推断默认为设备本地存储，默认不向 API 或遥测附带未经允许的学习数据；用户可以选择使用本地保存偏好参与云端请求，并清楚看到发送内容范围。

**退出门槛**：与无偏好基线做冻结集 A/B 和人工成对盲评，偏好遵循率/成稿可用性有可信提升；事实保真、安全、高风险切片和格式质量不下降；偏好可完整导出/清除，清除后缓存、索引和备份策略明确。

### 阶段 E：将本地模型路径做成可交付产品（阶段 4）

本地服务和模型清单独立发布、精确版本化。选择 llama.cpp 或既有外部 Ollama/LM Studio 接入的产品范围需先从源码现状和目标用户场景验证；先修复现有集成的有效参数映射，再增加分发链。对 llama.cpp 固定 release/commit、构建来源、依赖与许可；下载清单绑定运行时兼容版本、模型来源/许可/SHA-256/上下文/硬件档位及 LoRA 基座哈希。签名清单采用离线/受控发布密钥验证，不能把哈希当签名。

下载器需在开始前校验目标盘空间，支持取消、续传、分块/完成哈希验证、校验失败清理和重新拉取；安装 UI 提示来源、体积、许可和需要的磁盘空间。首次运行做硬件预检、健康检查、CPU/Vulkan 能力探测、内存/上下文约束说明、启动超时和崩溃恢复。设置面板只显示确实进入启动参数或请求的参数；对无效 `Seed`、`RepeatPenalty`、`KeepLoaded` 等要么映射并验证，要么暂时隐藏/禁用并解释。

模型候选按阶段 A 的中文盲评和目标硬件实测决定，不预设某个 4B/9B 型号为默认。模型许可和派生/再分发条件逐个核查；运行时、权重和适配器独立下载、升级、回退、卸载。

**退出门槛**：受支持 Windows 硬件矩阵验证下载安装、校验失败、取消/续传、启动、生成、取消生成、断网、磁盘不足、异常恢复、更新回滚和卸载；本地模式服务端点固定在本机且不依赖外部服务；参数可从实际请求/进程参数验证；生成质量和硬件指标均达产品目标。

### 阶段 F：根据残余差距决定是否启动 LoRA/SFT（阶段 5）

只有阶段 B–E 的提示、Schema、路由和确认偏好均通过评测，而风格盲评仍出现稳定、可归纳的差距时才立项。数据必须明确授权，去重、PII 审查、来源追溯、双人抽检；训练/开发/冻结测试按来源和语义族隔离。至少两轮独立切片比较基座、应用层方案与适配器；模型、提示和参数锁定前不得查看冻结测试标签。

适配器作为独立可选包，绑定基座精确哈希、适配器哈希、训练数据版本/许可、训练参数与评测报告，并允许立即回退基座。若收益只在单一切片、置信区间含零，或事实保真/安全/直接可用性下降，则不发布，保留基座加用户偏好上下文方案。

## 4. 全程发布质量线

候选版本扩大交付前，以冻结测试集报告并满足原计划质量门槛：

- Schema 合法率 ≥99%。
- 总体事实/约束保留率 ≥98%，高风险关键事实反转为 0。
- 直接可用率 ≥90%。
- 个性化成对偏好优于基线且区间支持收益；事实保真与安全指标不下降。
- API 报告请求延迟、输入/输出/缓存 token 与成本字段；本地报告分硬件档给出 p50/p95、tokens/s、峰值内存和模型加载时间。

这些汇总线不能遮盖切片失败；任何高风险关键事实反转、隐私边界越权或仅本地跨云请求都触发停止发布与回归调查。

## 5. 执行顺序与工作包

| 顺序 | 工作包 | 依赖 | 完成凭证 |
|---|---|---|---|
| P0 | 评测来源授权、受控盲评存档与准入报告 | 评审协调/数据权限 | ≥500 样本合规准入报告；独立原始提交与冻结哈希 |
| P1 | 统一云/本地基线跑批与可信指标 | P0 数据冻结 | 可复跑配置、分切片报告、模型/运行时/硬件记录 |
| P2 | 兼容性盘点、迁移设计、测试可靠性分类（含精确模型参数能力） | 可与 P0 并行，不改生成行为 | 能力映射矩阵、未知能力策略、旧配置迁移方案、环境/产品测试分类与阶段 1 负例测试输入 |
| P3 | Provider 无关契约与 Schema 闭环 | P0/P1 通过 | 双工作流契约验收、Provider 映射/失败模式验证 |
| P4 | 手动优先的用户控制路由 | P3 通过 | 路由优先级验证、本地-only fail-closed 证据 |
| P5 | 偏好确认/管理/删除闭环 | P3/P4 通过 | A/B 盲评收益及不退化报告、清除验证 |
| P6 | 独立本地运行时/模型分发 | P1/P3 通过 | Windows 硬件矩阵验收、签名/许可/哈希清单 |
| P7 | 条件性 LoRA/SFT | P0–P6 通过且仍有残差 | 训练数据审查、独立切片收益、基座回退证明 |

工作按交付物流转，不设未经验证的时间承诺。P2 可与数据准入并行；P3 及以后涉及真实生成行为的实现必须等待 P0/P1 门槛。每个工作包在开发 PR/提交前先确定验收场景与负向测试，再实现并复验。

## 6. 阶段测试与故障注入清单

- 契约：各 Provider 参数映射、严格 schema、拒绝、截断、无效 JSON、一次修复失败、超时/鉴权/速率限制与取消。
- 兼容：旧 Provider profile、密钥引用、偏好字段、归档读写与配置迁移。
- 路由：Manual 不变化、风险与预算优先级、能力缺失、备用配置、实际模型可见性、仅本地网络隔离。
- 偏好：单次指令优先、场景隔离、未确认建议不会静默应用、无痕和完整清除。
- 下载/本地：SHA-256 或签名失败、空间不足、网络中断续传、用户取消、运行时不兼容、启动失败、模型哈希不匹配、卸载残留。
- 训练包：LoRA 基座不匹配拒绝、适配器损坏拒绝、版本回退到基座。
- 最终发布：真实 Windows 打包、升级/降级、干净安装、端到端和硬件矩阵验收。

## 7. 风险与决策记录

1. **最主要阻塞是评测证据，而非缺少另一个模型。** 先引入模型或训练更难判断收益，并可能固化偏差。
2. **验证身份不能靠声明字段。** 现有校验可以证明提交记录内部一致，不能证明授权、真实身份或评审独立；需受控流程和独立保管人。
3. **跨 Provider 的“结构化输出”不是统一能力。** OpenAI Structured Outputs 和 Responses 使用其接口特定的 schema 字段；Gemini 只支持 JSON Schema 子集；降级路径仍必须由本地严格校验兜底。
4. **OpenAI Responses API 迁移应增量对比。** 官方目前建议新流程使用 Responses，同时 Chat Completions 仍受支持；应先单独适配一条文本工作流，比较输出解析、延迟、用量与错误后再决定扩展。
5. **Prompt caching 是成本/首 token 延迟优化。** 缓存命中需要稳定前缀，变动提示会影响复用；应通过实际 cached-token/成本指标决定是否整理上下文顺序。
6. **llama.cpp 主分支文档会变。** 运行时发布需固定具体版本/提交并对目标模型模板、JSON Schema、取消和 LoRA 做冒烟与回归，不直接跟随 master。

## 8. 依据资料

- OpenAI Structured Outputs：支持结构化最终响应的 Schema 输出，但遵守 JSON Schema 子集；拒绝、截断仍需应用处理。<https://developers.openai.com/api/docs/guides/structured-outputs>
- OpenAI Responses API 迁移：结构化格式由 Chat Completions 的 `response_format` 映射至 Responses 的 `text.format`；建议按文本流等工作流逐步迁移并比较行为、延迟、用量与错误。<https://developers.openai.com/api/docs/guides/migrate-to-responses>
- Gemini Structured Outputs：仅支持 JSON Schema 子集，需独立能力映射和验证。<https://ai.google.dev/gemini-api/docs/structured-output>
- OpenAI Prompt Caching：前缀匹配与稳定提示结构影响复用，需观察 cache usage，不可把缓存当作质量机制。<https://developers.openai.com/api/docs/guides/prompt-caching>
- llama.cpp Server：当前提供兼容 API、Schema-constrained JSON 和 LoRA 等能力；本项目仍需固定版本并验证具体模型与模板。<https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md>


## 2026-10-03 计划纠偏：WPF 诊断序列不作为产品回归

子 testhost 中三轮重建 `Application` 已按预期复现资源包 FailFast；复核微软官方契约后确认该试验故意绕过 WPF 每 AppDomain 单例约束，不能作为产品正常工作场景的验收。相关三周期测试已移出默认测试源码，避免保留错误回归目标。当前 Dispatcher 显式 teardown 定向用例在绝对临时目录下 1/1 通过。后续若继续修复测试宿主，优先比较“进程内一个长期 STA/Dispatcher”与“按进程隔离 WPF 用例”，先统计迁移范围再动 helper；不得把这个独立夹具缺陷宣称为旧 `HwndSubclass` crash 的根因。阶段 0 仍等待获授权盲评材料及独立评审证据；这限制盲评质量结论和模型权重发布，不阻断有明确证据且可做软件契约验证的定向生成/服务修复。

## 2026-10-03 计划推进：P2 逐类测试基线可复跑化

### 本步卡片

| 项目 | 内容 |
|---|---|
| 输入 | 当前 `Huaxiazi.Tests` 项目、此前 103 类隔离记录、实际 `dotnet test --list-tests` 输出和每类 TRX；不调用任何在线 Provider，不使用用户内容 |
| 产出 | `tools/test-isolation/TestIsolation.psm1`、`run-isolated-tests.ps1`、Pester 回归及 103 类新隔离运行证据 |
| 验收 | 类名唯一抽取、过滤器精确到类边界、TRX 成功/失败/跳过/中止计数可解析；缺计数拒绝；4 类 smoke 与整套类隔离真实运行；任一失败以非零码退出 |
| 结果 | helper 5/5；整套 103 类启动成功，1,132 项中 1,113 通过、8 失败、11 跳过、0 aborted；发现并修复 TRX `notExecuted=0` 与 11 条 `NotExecuted` 结果不一致的报告 bug |
| 上下游 | 降低 P2 后续裁决与阶段 1 负例验证的宿主噪声；不解锁 P3，也不代表 AI 输出质量证据。先裁决失败契约与普通宿主 crash，再由阶段 0 授权盲评/A0.2 基线决定是否进入生成链路改造 |

### 结论与下一步触发条件

全类别独立进程隔离已有可复跑实现，但运行约 2.5 分钟，只有在需要完整分类/回归时使用；日常局部改动指定 `-ClassName`。Run P 的 103 类隔离与 Run Q 的普通单 testhost 全量回归均为 1,118 通过、4 失败、11 跳过、0 aborted；此前 WPF FailFast 本轮未复现。剩余 4 项均涉及发布/安装实际交付契约，详见[测试基线 Run P/Q](../test-baseline-classification-2026-10-02.md#运行-pwpf-sta-teardown-修复后的全量隔离基线-2026-10-03)；发布交付要求应由产品所有者确认后再统一实现和断言。阶段 0 的盲评集、权利和独立评审证据仍缺，生成行为/API 参数保持冻结。

## 阶段 0 当前执行卡（2026-10-03 实盘核对）

| 子任务 | 输入 | 产出 | 验收门槛 | 上下游关系 |
| --- | --- | --- | --- | --- |
| A0.1 来源与样本准入 | ≥500 条项目自有或明确授权、已脱敏样本；来源权利受控引用；来源负责人、两位独立评审、一位分歧裁定人和独立复核角色 | v3 `blind-eval.jsonl`、source register、双评原始引用、分歧裁定、family/split 记录、evidence manifest 与准入报告 | 逐样本权利/用途覆盖、人工隐私复核、双评与裁定可重建、family 不跨 split、工具审计通过；受控核验状态真实可追溯 | 前置；未过不能解封冻结测试或修改生成行为 |
| A0.2 同条件产品基线 | A0.1 冻结 hash；固定候选模型/版本、system/user prompt bundle、采样和推理参数、硬件/运行时；云端调用另有预算批准 | 每候选原始预测/评审包、非内容 run manifest、人工评分/成对比较、API usage/cost 或本地 p50/p95、tokens/s、内存与加载报告 | 参数一致或明确分 cohort；冻结候选后才解盲；Schema/保真/安全/可用性及性能指标可复跑且对样本覆盖完整 | A0.1 通过后开始；结果支撑阶段 1 及后续优先级决策 |

500 条准入集的建议 split、task × split × 场景配额及统计精度说明见[阶段 0 样本设计提案](../phase0-evaluation-sample-plan.md)。它是待产品经理/评测负责人批准的工作设计，不变更协议或 validator；不得把该矩阵描述成当前已通过的实际数据配额。

实盘盘点确认仓库有 12,000 行 `datasets/v1` 合成集、3,000 行 `polish-agent-v2` 衍生合成集及 12,000 行 `architecture-v1` prompt architecture 搜索集；它们没有替代授权真实 blind eval 的权利/独立双评证据。`datasets/ai-evaluation` 目前仅有规程、Schema、模板，实际 `blind-eval.jsonl`、来源登记、证据清单和评审记录缺失。下一步的实质输入是受控样本与评审角色；在获得前，合成集仅可做内部流程回归，不能据此报告模型质量提升。当前阶段通过状态：**未通过**。

数据负责人交付项、责任分离和停止点已汇总到[阶段 0 项目经理交接卡](../blind-evaluation-evidence-intake.md#2026-10-03-项目经理交接卡)。本次文件清单核查确认这些输入当前仍缺；云端 A0.2 默认不出站，需在 A0.1 通过后另行批准预算和数据发送范围。

## 阶段 0 评测方法更新（2026-10-03）

评分器审计发现按单条样本算 Wilson 区间与语义族隔离设计不一致：旧代码会把同一 `semantic_family_id` 的 20 条全胜变体误计为 20 个独立偏好观测。红测已复现。现行作法把样本行胜率保留为描述指标，另将每个语义族压成左胜/右胜/族内平局一个等权结果，并以族级 Wilson 区间为偏好推断依据；至少 30 个决定性族且下界 >0.5 才标为显著。行级 Wilson 不再作为增益置信区间，manifest 不再按 item rows 单独重算。统计推断的独立单位和小簇风险依据 [NBER clustered errors research](https://www.nber.org/papers/t0344)。

此选择的前提是评测目标为“跨语义任务族的一般偏好”，不是按真实流量分布加权；多数票会丢弃族内偏好幅度，30 族也只是最低运行门槛、不是功效证明。新标注规则在 `annotation-handbook-v1.1.md` 固化，v1.0 保留历史。盲评/manifest/finalizer 关联回归 117/117 通过。数据未就绪前不运行候选，不解锁阶段 1。

## 2026-10-04 执行记录：A0.1 公开来源候选再筛

| 项目 | 内容 |
|---|---|
| 输入 | 当前 `datasets/ai-evaluation/` 文件清单；官方/上游数据卡、仓库说明、Creative Commons 许可页。没有下载/导入数据、模型输出或用户内容。 |
| 产出 | `docs/external-evaluation-data-research-2026-10-02.md` 的新版本复核与来源处置矩阵，覆盖 Zhiyin、WritingBench、RewritingBench 和 ToxiRewriteCN。 |
| 验收 | 区分案例数与多模型输出行；记录公开污染、许可声明范围、任务匹配和人工双评证据缺口；给出正式集与诊断集分轨决策。 |
| 结果 | 未发现能直接满足 A0.1 的公开集。Zhiyin 280 案例且 CC-BY-NC-ND、繁体写作/LLM judge；WritingBench 1,000 query 适合做任务 taxonomy 参考但逐项权利和上游材料需复核；RewritingBench 129 条公开 eval；ToxiRewriteCN 1,556 条只可能成为隔离的安全压力切片。正式冻结合格数仍为 0。 |
| 下一步触发 | 数据负责人提供 ≥500 条项目自有/明确授权的通用样本，并安排双人独立评审、分歧裁定与受控证据复核；随后执行现有准入工具。A0.1 未过之前不进入候选模型基线或阶段 1 生成行为改造。 |

**偏差自审：** 公开网页声称的许可不等于逐项权利链；未下载样本、未形成 source register，也没有现实评审证据。因此本步是来源筛选记录，不应写成 A0.1 数据准入已完成或质量评测开始。

## 2026-10-04 执行卡 #27：Gemini authorization key 标头检测回归

| 项目 | 内容 |
|---|---|
| 计划节点 | A0 脱敏与评分安全门；不改变 A0.1/A0.2 放行条件 |
| 输入 | 现有 `BlindEvaluationAuditor` 的 `api-key` 标签扫描；Google Gemini API key 类型与 REST header 官方文档；合成 opaque-key 测试值 |
| 产出 | 准入上下文 `x-goog-api-key` 标头测试、候选输出标头测试、README 覆盖边界说明、状态记录 |
| 验收 | 准入端命中并返回不回显的 `pii`；候选端命中并返回不回显的 `pii-output` 且候选不过关；定向测试和盲评相关测试全绿 |
| 结果 | 定向 **70/70**，`FullyQualifiedName~Blind` **206/206** 通过；未增加密钥正则，不触碰生成逻辑 |
| 量化收益 | 新增 2 个跨准入/评分边界的 header 标签回归；不能推导真实拦截率或 AI 质量收益 |
| 上下游 | 维持 A0.1 等待 ≥500 条明确授权脱敏样本、独立双评/裁定和受控核验证据；这些证据齐全后才能运行 `blind-validate` 并决定 A0.2 基线安排。 |

**自审与方案取舍：** Google 文档给出 REST header 标签，但没有给新 authorization key 一个本地可维护的固定字面模式。沿用可审查的标签匹配避免高熵泛匹配的误报；无标签且外形未知的值仍有漏检风险，应由人工脱敏与准入流程承担，而不是宣称正则已覆盖所有 secret。

## 2026-10-04 执行卡 #28：OpenRouter key 格式双路径审计

| 项目 | 内容 |
|---|---|
| 计划节点 | A0 脱敏与预测输出安全审计 |
| 输入 | 项目 Provider catalog；`BlindEvaluationAuditor` 当前凭据规则；OpenRouter 官方 quickstart 前缀示例；仅含合成 token 的回归夹具 |
| 产出 | 准入/预测评分回归、OpenRouter 特定扫描规则、README 与实施状态记录 |
| 验收 | 旧代码下 gold 与 candidate 两条路径各有 1 个真实漏检；修复后两侧拒绝且无凭据回显；短词汇负例通过；盲评相关测试通过 |
| 结果 | RED 阶段 **2/2 漏检**；修复后定向 **22/22**，`FullyQualifiedName~Blind` **209/209** |
| 参数/理由 | 匹配 `sk-or-v1-` 后一个或多个 ASCII 字母、数字、下划线或连字符，裸前缀不命中。上游未发布完整长度规范，因此不臆设最小/最大长度；标签示例误报仍需人工脱敏/审核。 |
| 收益 | 已文档化 OpenRouter key 家族的准入/评分测试路径从 **0/2** 提高到 **2/2**；无真实命中率、AI 输出质量或性能数据。 |
| 上下游 | 只提高阶段 0 样本/预测的隐私门禁，不解锁 A0.1/A0.2 或阶段 1。下一步按 Provider 官方资料盘点其他 key 格式；阶段门推进仍等待 ≥500 条授权脱敏样本及完整独立评审证据。 |

**纠偏复核：** 初版 16 字符下限没有上游长度证据；新增 `sk-or-v1-a` 两侧回归后，确认初版仍会漏检（2/2 RED），故立即撤销这个臆设下限。最终规则以已核实的专属前缀和非空载荷区分裸前缀；未触及生成链路或模型参数，测试数据全为占位值。

## 2026-10-04 执行卡 #29：Anthropic key 示例前缀双路径审计

| 项目 | 内容 |
|---|---|
| 计划节点 | A0 脱敏与预测输出安全审计 |
| 输入 | Provider catalog 中 Anthropic profile；当前凭据正则；Anthropic 官方 Claude for Sheets API key 示例；合成 key 占位值 |
| 产出 | Gold 准入和 candidate 评分测试、独立 Anthropic 示例前缀检测、README 与状态记录 |
| 验收 | `sk-ant-api03-` 长/短载荷在旧实现下均复现准入及评分漏检；修复后拒绝且不回显；裸前缀词汇负例通过 |
| 结果 | 旧实现 **4/4 漏检**；修复后凭据回归组 **29/29**、`FullyQualifiedName~Blind` **216/216** 通过 |
| 参数/依据 | 使用官方示例展示的精确 `sk-ant-api03-` 前缀，后接至少 1 个 ASCII 字母、数字、下划线或连字符。文档未给完整格式或长度规范，故不设长度界限；后缀规则是扫描器可观察边界，不宣称厂商完整语法。 |
| 收益 | 已展示 Anthropic key 示例家族准入/评分路径从 **0/4** 提高到 **4/4** 回归覆盖；没有真实检测率或生成质量、成本、延迟变化数据。 |
| 上下游 | 只提升离线数据/预测隐私门禁。后续需继续按官方一手材料核对其他 Provider；实际 A0.1/A0.2 和阶段 1 仍等待授权样本与独立评审证据。 |

**自审：** 官方文档给的是局部示例 `sk-ant-api03-j1W...`，不是正式 token 规范；本项仅覆盖它明确展示的前缀家族，对其它 Anthropic key 变体不作覆盖承诺。

## 2026-10-04 执行卡 #30：xAI API key 前缀双路径审计

| 项目 | 内容 |
|---|---|
| 计划节点 | A0 脱敏与候选输出安全审计 |
| 输入 | `ProviderPlatform.Grok` 配置；当前已知凭据规则；xAI 管理 API 的官方 key 创建/认证示例；合成 token |
| 产出 | 准入与评分正例、`xai-` 前缀专属规则、短词汇负例及文档记录 |
| 验收 | 长/短载荷在旧实现下均能重现 gold 与 candidate 漏检；修复后两侧都隔离且诊断不回显；裸前缀不作为 key 命中 |
| 结果 | RED **4/4 漏检**；定向 **34/34** 与 `FullyQualifiedName~Blind` **221/221** 通过 |
| 参数/依据 | 只匹配文档展示的 `xai-` 前缀和至少一个 ASCII 字母/数字/下划线/连字符载荷，不猜长度，不把 Bearer 凭据里的任意值都视作 xAI key。 |
| 收益 | xAI key 示例家族两个字段边界、两种载荷长度共 **4/4** 受回归保护；这不是线上检测率或模型输出质量增益。 |
| 上下游 | 强化 A0 数据与候选产物的隐私筛查；不解锁 A0.1/A0.2。继续核验剩余直连云 Provider 的一手 key 格式；阶段门仍需实际授权集、独立评审和证据核验。 |

**自审：** xAI 官方文档示例足以支持 `xai-` 前缀，却不是完整 token 规范。按最窄有来源前缀实现；若将来文档格式改变，需新增版本化检测与两侧回归，而非放宽为任意高熵字符串。

## 2026-10-04 执行卡 #31：OpenAI project key 分段前缀审计

| 项目 | 内容 |
|---|---|
| 计划节点 | A0 数据准入与候选输出的 Provider credential 隐私扫描 |
| 输入 | 项目 OpenAI Provider；当前 `sk-` 规则；OpenAI 官方 Cookbook 的 `sk-proj-…` key 示例；合成 token |
| 产出 | 2 种载荷长度 × 2 条扫描路径的回归、`sk-proj-` 规则、README/状态/计划说明 |
| 验收 | 旧实现四个正例全漏；修复后 gold 与 candidate 输出均拦截、问题不回显 key；裸前缀文案不误命中 |
| 结果 | RED **4/4 漏检**；定向 **39/39**、`FullyQualifiedName~Blind` **226/226** 通过 |
| 参数/理由 | 精确匹配 `sk-proj-` 后非空 ASCII 字母、数字、下划线或连字符；不设长度界限，因为官方示例未给出规范长度。通用 `sk-` 规则保留以兼容其它连续式 key。 |
| 收益 | `sk-proj-` 示例格式在 gold 和候选输出两个边界由 **0/4** 提升为 **4/4** 测试保护；没有实测隐私拦截率或模型质量/性能收益。 |
| 方案审查 | MiniMax 官方页面只称 Token Plan key 为 `sk-cp`，未提供精确完整 token 字符串；为遵守一手证据要求，本轮只记录待核验，不基于第三方示例编写规则。 |
| 上下游 | 继续逐个盘点其它直连云 Provider 的正式格式；增强只影响离线准入/候选评分，不改生成路径。阶段门仍待授权评测集与独立评审证据。 |

**自审：** OpenAI Cookbook 给出的是开发示例而非完整 key 语法；`sk-proj-` 检测只针对该展示格式，不能推导对所有 OpenAI key 类型完整覆盖。

## 2026-10-04 执行卡 #32：Ark 与阿里云计划 API key 隐私扫描

| 项目 | 内容 |
|---|---|
| 计划节点 | 阶段 0 数据准入与候选输出隐私扫描；不改变盲评放行门槛 |
| 输入 | 项目 Provider catalog 的 Ark/DashScope 集成；火山方舟 quick start 的 `ark-<uuid>-<suffix>` 形状；阿里云 Model Studio 的 `sk-sp-` 官方说明；合成占位 token |
| 产出 | 金标准入与候选评分正例、词汇负例、两种凭据规则、README/状态记录 |
| 验收 | 旧实现两个格式在两个边界全漏；修复后两边拒绝且不回显凭据，词汇负例接受 |
| 结果 | RED **8/8**（Ark、`sk-sp-` 各 2 种长度 × gold/candidate）；规则落地后目标组 **49/49**、完整 `FullyQualifiedName~Blind` **236/236** 通过 |
| 全量回归 | `Huaxiazi.Tests`：1202 通过、11 跳过、4 失败；失败限于当前工作树发布/交付脚本断言（AcceptanceDefect/ReleaseSecurity），与扫描规则无关。设置仓库内隔离 `TEMP` 后运行完成。 |
| 参数/依据 | Ark 要求标准 8-4-4-4-12 十六进制 UUID 分组和至少一个后缀字符；阿里云按确切 `sk-sp-` 前缀和非空 ASCII 载荷检测。没有证据时不约束长度。计划 key 的检测不表示本产品支持或鼓励使用受限套餐。 |
| 收益 | 两类凭据跨准入和候选输出新增 **8 个合成回归保护**；不估计实际检测率或模型质量、成本、延迟收益。 |
| 局限/下一步 | Ark 后缀字符集是本地启发式，不是厂商正式语法；历史无专属前缀 UUID key 未被这条规则覆盖。继续盘点剩余 Provider；阶段 0 仍等待授权外部集及双评/裁定证据。 |

## 2026-10-04 执行卡 #33：Provider 凭据格式证据矩阵

| 项目 | 内容 |
|---|---|
| 计划节点 | 阶段 0 隐私准入门；盘点可验证覆盖边界，不推进 A0.1 数据准入 |
| 输入 | Provider catalog、17 家预置 Provider 官方认证/快速开始资料、当前敏感字符串检测实现 |
| 产出 | [`Provider 凭据审计台账`](../provider-credential-audit-2026-10-04.md)，逐家记录格式证据、扫描边界、未核假设及剩余 3 个预置厂商；自定义 endpoint 按实例处理 |
| 判定 | `sk-` 开头资料仅支持其已知前缀，不足以证明 token 的长度和字符集；Bearer 标记支持的是保留 Authorization header 的记录，不是未标注的裸 secret。OpenAI-compatible 是协议兼容，不是凭据格式统一。 |
| 方案选择 | 对有精确格式依据者分别建规则；对未知 token 不扩大成泛高熵匹配，以避免普通文本误报；对 MiniMax `sk-cp` 和 Ark 历史 UUID 保留明确缺口，等完整官方形状/授权脱敏证据再改代码。 |
| 验收 | 20 个预置云厂商中 17 个的证据和扫描前提可逐条追溯、其余 3 个显式标记未核；另将用户自定义 endpoint 独立标为逐实例核验。文档范围审查通过，未改模型请求。 |
| 下一步 | 逐一复核剩余 Kimi、讯飞星火、Yi；真实 A0.1 仍需授权冻结集和独立评审证据。 |

## 2026-10-04 执行卡 #34：MiMo Token Plan `tp-` 凭据扫描

| 项目 | 内容 |
|---|---|
| 计划节点 | 阶段 0 数据准入与候选输出凭据隐私扫描 |
| 输入 | Xiaomi MiMo 官方 API 集成 FAQ，明确 `tp-xxxxx` Token Plan key 与 `sk-xxxxx` 按量 key；合成占位值；当前共用扫描函数 |
| 产出 | 准入/候选长短两类正例、前缀纯文字负例、`tp-` 专用规则及矩阵/状态更新 |
| 验收 | 旧逻辑 gold 与 candidate 路径合计 4/4 漏检；修复后全部拦截且错误不回显 key，词汇负例通过 |
| 结果 | RED **4/4**；目标凭据组 **54/54**、完整 `FullyQualifiedName~Blind` **241/241** 通过 |
| 参数/依据 | `tp-` 后要求至少一个 ASCII 字母、数字、下划线或连字符；无长度界限。官方 `xxxxx` 是格式占位符，无法支持更严格长度断言。 |
| 收益 | MiMo Token Plan 明确前缀两种载荷长度、两条扫描边界共 **4 个回归点** 从漏检变为受测；不代表生产检测率或模型质量、成本、时延收益。 |
| 局限/上下游 | 不表示话匣子集成 MiMo Token Plan；MiMo 按量 `sk-` 仍只有通用子集检测。继续核验 Kimi、讯飞星火和 Yi，且阶段 0 外部授权数据/双评门槛仍未通过。 |

## 2026-10-04 执行卡 #35：Kimi / 讯飞星火认证资料与 APIPassword 标签扫描

| 项目 | 内容 |
|---|---|
| 当前节点 | 阶段 0 隐私准入门的 Provider 覆盖盘点；A0.1 授权数据准入与真实基线仍未通过 |
| 输入 | Provider catalog 20 个预置云 profile；[Kimi 官方 API 指南](https://platform.kimi.com/blog/posts/kimi-api-quick-start-guide)；[讯飞星火 HTTP 文档](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)；合成凭据占位与词汇负例 |
| 问题与方案 | Kimi 文档可证 Bearer header 但未给裸 token 格式，继续仅依据完整 Authorization header 扫描。星火文档使用 `APIPassword` 标签，但通用规则不识别此标签；新增 `api_password` 字段扫描，必须跟随 `:` 或 `=`，避免纯文档词汇误报。 |
| 产出 | 准入/候选输出正例、标签词汇负例；`BlindEvaluationAuditor` 最小规则扩展；Provider 台账、AI 评测 README 和实现状态更新。 |
| TDD / 验收 | 旧实现标签化 Spark 凭据两条路径 **2/2 漏检**；自审添加空字段负例后旧实现 **1/1 误报**。修复后目标筛选 **25/25**、`FullyQualifiedName~Blind` **245/245** 通过，candidate 问题不回显凭据，字段名词汇和空字段负例均通过。 |
| 覆盖状态 | 20 个预置云 Provider 中 **19/20** 有可用于扫描边界的官方认证资料，剩余 Yi 1 家；当前 Yi 官方门户只显示 API/Key 管理入口，未能读取认证正文。自定义 endpoint 仍按实际实例核验。 |
| 参数与依据 | 只识别 `api[_ -]?password` 后跟冒号/等号和至少一个非空字符，不限制短/长值，因为星火官方未定义 token 长度和字符集；Kimi 不新增裸 token 猜测规则。 |
| 收益与限制 | 新增 2 个跨准入/候选正例和 2 个误报负例；该格式由 **0/2** 变为 **2/2** 受回归保护。合成回归数不等于线上命中率，也不代表 AI 质量、成本或时延收益；裸 Spark/Kimi key 仍可能漏检。 |
| 上下游 / 触发条件 | 下一项需取得可读取的 Yi 官方认证正文，再完成该 provider 格式核验。阶段 0 仍等待 ≥500 条授权脱敏评测样本、双人独立评审及可追溯冻结证据；条件满足并通过基线门槛后才触发阶段 1 生成契约开发。 |

## 2026-10-04 执行卡 #36：补齐 Yi 官方 Bearer 认证传输证据

| 项目 | 内容 |
|---|---|
| 计划节点 | 阶段 0 Provider 凭据证据矩阵收尾，不进入真实数据准入或产品生成行为变更 |
| 输入 | [Yi 官方接口文档](https://platform.lingyiwanwu.com/docs/api-reference)；第一次页面提取结果只有平台导航，故进一步检查同一官方页面的 SSR HTML 正文 |
| 发现 | 正文明确 Yi API 使用 `Authorization: Bearer YOUR_API_KEY`，请求表格将 `Authorization` 列为必需 header；占位符没有揭示裸 token 前缀、长度或字符集 |
| 方案/产出 | 更新凭据台账与实现状态。重用当前通用 Authorization 检查，不加 Yi 专属裸 key 正则，也不为短的文档占位值修改既有 ≥20 字符 Bearer 启发式 |
| 验收 | Provider catalog 20 个预置云服务的认证传输资料达到 **20/20**；明确此指标不等于 20 种裸凭据格式完整已知。已有通用 Bearer 正负例为代码路径回归，本子任务仅更新证据文档，未改源码或发真实请求 |
| 量化影响 | 认证传输方式资料覆盖 **19/20 → 20/20**；可确定的裸凭据规则覆盖率无提升。无模型质量、成本、延迟或生产漏检改善数据 |
| 下一步 / 阶段门 | 继续核查 MiniMax `sk-cp`、Ark 历史 UUID 等资料边界与自定义 endpoint 实例规范。阶段 0 的主要阻断仍是缺少 ≥500 条授权脱敏样本、双评/裁定的受控证据与冻结数据包；这些输入到齐且通过基线门槛后才触发阶段 1 |

## 2026-10-04 执行卡 #37：MiniMax `sk-cp` 证据边界复核

| 项目 | 内容 |
|---|---|
| 计划节点 | 阶段 0 Provider 凭据格式审计；不变更产品 Provider 支持或模型行为 |
| 输入 | [MiniMax 官方 Token Plan 页面](https://platform.minimaxi.com/subscribe/coding-plan)；当前 `sk-` / Bearer 扫描规则 |
| 发现 | 官方称可获取 “sk-cp Key” 用于 OpenAI-compatible 工具，但没有在该套餐页面给完整 token 示例。该平台另处的 Bearer API 例子没有明确将同一凭据绑定到 `sk-cp`，故不能拼接两处资料推断 `sk-cp` 认证 header 或后缀语法 |
| 方案与产出 | 台账将“套餐 key 命名”“通用 API 的 Bearer 示例”分开陈述；不增加 `sk-cp-` 假设正则，不将任意带 `sk-cp` 的普通文本认作凭据 |
| 验收 | 仅一手资料更新，无代码改动、模型调用或新测试。裸 `sk-cp` 仍在已知扫描缺口表中 |
| 取舍与风险 | 继续漏掉部分无标签计划 key 的风险，换取避免错误假设和普通正文误报；如取得明确 Token Plan key 样本形式或认证契约，再通过准入/candidate 两路径 TDD 处理 |
| 下一步 | 核对 Ark 旧 UUID 格式的官方说明及可操作的低误报检测策略；真正解锁阶段 0/1 的依然是受控外部评测数据包和独立评审证据 |

## 2026-10-04 执行卡 #38：Ark 历史 UUID 检测边界裁决

| 项目 | 内容 |
|---|---|
| 计划节点 | 阶段 0 凭据扫描精度/误报边界审查 |
| 输入 | [火山方舟官方 quick start](https://docs.volcengine.com/docs/ark/quick-start-beginner?lang=zh)；现行 `api_key` 标签扫描、通用 Bearer 检测与 `ark-<uuid>-<suffix>` 专用规则 |
| 官方事实 | 官方脚本兼容历史 UUID key 和新 `ark-<uuid>-<suffix>` 格式；未在该说明中界定历史裸 UUID 的额外字段或边界标记 |
| 核心冲突 | 裸 UUID 容易与样本 ID、任务 ID、请求 ID 混淆。无来源上下文时，无谓误报会使评测集大量拒收；不做裸 UUID 检测则可能漏掉无标签旧 key。当前没有受授权文本可估算两种代价 |
| 裁决/产出 | 不添加裸 UUID 通用规则；带 `api_key`/`API Key:` 标签值或完整 Bearer header 继续由共用扫描入口处理。台账和实现状态明确这只支持带上下文检测，不声称裸值覆盖 |
| 验收/量化 | 官方资料已链接，代码现状与扫描边界逐项核对；无源码和测试改动。该类凭据的裸值检出率、误报率均未知，不能给出虚假改善百分比 |
| 上下游/重开条件 | 收到有授权且含明确来源标注的真实数据，能区分 API key 与业务 UUID 后，先在隔离盲评片段测误报/漏报，再决定是否加上下文类型识别；不以 `uuid` 词法本身扩大匹配。阶段 0 的授权数据/独立评审主门槛仍待交付 |

## 2026-10-07 阶段 1 执行卡：StepFun 当前型号能力映射

| 项目 | 内容 |
|---|---|
| 输入 | StepFun 官方 Chat Completions、Step 5 Preview、Step 3.7 Flash、Step 3.5 Flash 型号文档；当前 `ProviderPlatformCatalog`、capability resolver 与 AIService 请求体 |
| 输出 | catalog 增加 4 个当前型号并保留旧项和旧默认；StepFun 精确型号能力解析；Step 5 的原生 JSON Schema 请求与推理级别回归 |
| 参数决策 | 所列型号 temperature 0–2；top_p 字段可用但官方未公布范围，因此不裁切。Step5/3.7 映射 low/medium/high；3.5-flash-2603 映射 low/high；普通 3.5 不推断 reasoning_effort。只给 Step5 映射 JSON Schema 与 65536 服务上限，其他结构化输出保持 Unknown。应用 max token 上限仍为 32768，不在本项擅自提高 |
| 验收 | 目标测试 5/5；`AIServiceTests|ProviderPlatformTests` 306/306；`dotnet build Huaxiazi.csproj --no-restore` 0 warning / 0 error。红测阶段确认 catalog、capability 与原生 Schema 请求均缺失 |
| 收益边界 | 目录可选模型 2→6，其中 2 个明确旧型号；精确映射 4 个型号的采样、3 个型号的推理档、1 个型号的严格结构化输出。没有真实服务调用、质量盲评、成本或时延收益数据 |
| 下一步 | 继续官方证据支持的 Provider request-capability 盘点。确定模型推荐、默认项或采样质量调优前，需要真实受控兼容性与盲评/成本数据；阶段 0 数据轨道不受本次软件实现替代 |

## 2026-10-07 阶段 1 执行卡：MiniMax 当前模型与推理输出隔离

| 项目 | 内容 |
|---|---|
| 输入 | MiniMax 官方模型调用、OpenAI-compatible API 和 Quickstart 准备文档；MiniMax 目录、请求能力解析、API endpoint 安全校验与响应内容解析 |
| 产出 | 新 preset 指向 `https://api.minimax.cn/v1`，列 M3/M2.7/M2.7-highspeed；保留历史型号并将旧 `api.minimax.chat/v1` 精确保留在安全 allowlist；精确型号参数/响应映射 |
| 参数决策 | temperature 0–2，top_p 0–1；M3 用 `max_completion_tokens` 并设置 `reasoning_split=true`；M2.7 系列用 `max_tokens` 并在响应交付前移除 `<think>…</think>`。未核实原生 JSON Schema 与可调 effort 均不发送。M3.1-Flash-Preview 暂不供普通按量 key 选择 |
| 兼容边界 | 默认 catalog 更新为当前公开型号 MiniMax-M3，仅影响用户新建或主动应用 preset；已保存 profile 不自动迁移。M3 旗舰成本未与 M2.7 比较，默认仅代表当前模型目录首选，不代表质量/性价比结论 |
| 验收 | RED 确认旧目录/capability/请求地址/think 内容缺陷；专项 10/10；`AIServiceTests|ProviderPlatformTests|SecurityHardeningTests` 隔离临时目录后 333/333；主项目构建 0 warning / 0 error |
| 下一步 | 继续核查目录中存在且当前能力解析尚未覆盖的 Provider。质量推荐、价格或推理档位调参需后续授权数据与基线对照；本步骤无真实请求或质量/延迟/成本实测 |

## 2026-10-07 阶段 1 执行卡：xAI Grok 当前型号与 Responses 主接口

| 项目 | 内容 |
|---|---|
| 输入 | xAI Grok 4.3/4.7 型号页、Responses API、Chat Completions API 与 Structured Outputs 文档；既有 Grok catalog、serializer/parser、endpoint policy 和 capability resolver |
| 产出 | catalog 提供 4.3/4.7 并保留历史 IDs；新 profile 选用官方当前 Responses API；现存 Chat profile 保持；两协议的精确型号 capability、Schema/推理/输出预算请求回归 |
| 默认项与取舍 | 新目录默认 4.3，仅因公开常规价目低于 4.7（输入/输出 `$1.25/$2.50` 对 `$2/$6` 每百万 token）；不是质量推荐。存量 profile 不迁移。官方将 Responses 定为主接口，故新 preset 选 Responses；当前 endpoint policy 精确允许 Grok 的 Chat 与 Responses 两协议 |
| 参数与兼容性 | temperature 上限 2；top_p 不猜范围；Chat 使用 `reasoning_effort` / `max_completion_tokens`，Responses 使用 `reasoning.effort` / `max_output_tokens`。4.7 映射 low/medium/high/xhigh，4.3 官方页面 xhigh 有矛盾，采用 none/low/medium/high；原生 Schema 后仍本地校验；代理/未知型号不继承 |
| 验收 | RED 旧实现 Grok 筛选 4 项失败、3 项负例通过；Responses 主接口转换时又发现旧协议兼容和摘要字段两项缺口并补上。最终 Grok 定向 16/16，`AIServiceTests|ProviderPlatformTests|SecurityHardeningTests` 联合 349/349；主项目 build 0 warning / 0 error；diff check 无空白错误（保留仓库既存行尾提示） |
| 收益边界 | 未发真实 xAI 请求；未测润色质量、实付、延迟或吞吐。不报告模型质量提升；公开标价仅解释新目录默认取舍 |
| 下一步 | 重新执行 Grok、Provider、AIService 和 endpoint policy 相关回归，构建成功后再继续下一 Provider 的能力映射；真实盲评与受控账单数据仍决定质量/成本判断 |



## 2026-10-07 阶段 1 执行卡：Anthropic 型号生命周期校准

| 项目 | 内容 |
|---|---|
| 输入 | Anthropic 官方 Models overview、Model deprecations、Sonnet 5.5 与 Effort 文档；当前 catalog、capability resolver、AIService 请求和配置归一化行为 |
| 产出 | 新 preset 默认 `claude-sonnet-5-5`；加入当前 Opus/Sonnet/Haiku 型号；保留并标记 2026-11-30 退役的 Sonnet 4.5；新型号列表移除已退役 Opus 4.1；不静默迁移已保存 profile |
| 请求映射 | Sonnet 5.5 结构化 Messages 请求传输 `output_config.effort` 和 JSON Schema，省略官方不支持的自定义 temperature/top_p；token 输出上限沿用应用 profile |
| 验收 | 旧状态 RED 覆盖目录/default；配置兼容、Sonnet 5.5 请求体测试通过。`AIServiceTests|ProviderPlatformTests` 331/331；`dotnet build Huaxiazi.csproj --no-restore` 0 warning / 0 error；`git diff --check` 无空白错误 |
| 结论边界 | 规避新 profile 默认选中弃用型号；不代表成稿质量改善。官方标准标价 Sonnet 5.5 相对 Sonnet 4.5 输入/输出名义单价约低 33.3%，实际费用、质量、延迟未测 |
| 下一步 | 继续核对其他 Provider 当前型号与请求能力，优先修复有官方生命周期/兼容证据的目录问题 |


## 2026-10-07 阶段 1 执行卡：Together 当前 Serverless 型号与结构化输出

| 项目 | 内容 |
|---|---|
| 输入 | Together Available serverless models、Chat Completions API、Structured outputs 文档；当前 catalog、resolver、请求序列化和 endpoint policy |
| 问题 | preset 仅列 Llama 3.3 与未列入当前 serverless 目录的 Qwen2.5；使用历史 `.xyz` host；Together structured workflow 之前没有模型级 native Schema 映射 |
| 产出 | 默认 host 改用官方当前 docs `api.together.ai`，仍兼容存量精确 `.xyz` endpoint；保留当前仍可用的 Llama 默认，不自动迁移 profile；Qwen2.5 显示为历史项；增加其余 11 个当前 schema-capable 型号，共 12 个当前模型精确映射 |
| 参数取值 | temperature 最大 1.0，top_p 支持但不擅自裁切，输出字段仍 `max_tokens`；12 个已核验 model IDs 用 strict JSON Schema；不推断推理强度支持，不改应用 token ceiling |
| 安全与兼容 | 新旧正式域名精确放行，`api.together.ai.attacker.example` 与 `.xyz.attacker.example` 拒绝；既存 endpoint/model/SecretId/活动 profile 归一化不变 |
| 验收 | 目录/能力/endpoint/request 用例 RED 11 失败复现缺口；能力说明另有 1 个预期失败。Together 专项 46/46；`AIServiceTests|ProviderPlatformTests|SecurityHardeningTests` 377/377；WPF 主项目 build 0 warning / 0 error；diff check 无空白错误 |
| 收益与限制 | 模型选择从 2 项到 13 项（12 current + 1 history）；应用原生 strict-schema 精确覆盖 12 个型号。Qwen 3.5 9B 标价 `$0.17/$0.25` 每百万 token，Llama 3.3 `$1.04/$1.04`，只说明标价档位，无实际账单/质量/延迟对照 |
| 下一步 | 基于官方证据继续 provider 能力映射；再回到统一生成流程、用户偏好和可执行路由；盲评/本地硬件评测继续另行形成真实效果证据 |



