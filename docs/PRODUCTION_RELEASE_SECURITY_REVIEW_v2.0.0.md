# 话匣子 / Huaxiazi v2.0.0 — Production Release Security Review

> **历史审查快照：**本文记录 v2.0.0 当时的审查结论。自 v2.0.1 起，本项目采用适合小型开源桌面工具的发布标准：代码签名为可选增强项；无签名发布必须显式启用、通过 CI、由干净标签构建、提供 SHA-256 校验文件，并公开说明 Windows“未知发布者”/SmartScreen 提示。本文中的强制签名要求仅适用于高保障签名发布配置。

> 审查依据：用户提供的《Agent 项目 Production Release Security Review》框架（85 节）
> 审查性质：**只读、非破坏性**（未修改 / 未删除 / 未部署 / 未调用真实付费接口）
> 审查方法：仓库侦察 + 4 路并行代码级取证（Secrets/供应链/更新/CI-CD、Agent/LLM/Tool/注入、传统 AppSec/数据/密码学、隐私/数据流/文档核实），结论均以 `file:line` 证据支撑，并对既有安全文档声明做了代码级核实。

---

## 0. 审查对象锁定（Release Candidate）

| 项 | 值 | 备注 |
|---|---|---|
| Project Name | 话匣子 / Huaxiazi | |
| Release Version | **2.0.0** | `Huaxiazi.csproj:27` |
| Target Framework | net8.0-windows (WPF + WinForms) | 单用户桌面应用 |
| App Type | 本地优先、无账号、无遥测、无云端、无自营 AI 后端 | `README.md:14` |
| LLM 接入 | 用户自带 API Key：OpenAI-Compatible / Anthropic / Gemini / Ollama / LM Studio | |
| Git Commit SHA / Tag / Image Digest | **UNKNOWN** | 未向审查者提供不可变构件标识 |
| Environment | Windows 10/11 x64 桌面 | |

**结论绑定声明**：本审查结论绑定于源码快照 `PromptFloat/`（含 `Huaxiazi.csproj` 版本 `2.0.0`）与既有安全文档。由于未提供 Git Commit SHA / 签名 tag，结论无法绑定到不可变构件——按框架第 1 节，这本身是一个需记录的 UNKNOWN 项，发布前应以带签名的 tag/commit 重新锚定。

---

## 1. 最终门禁结论

> # ⚠️ CONDITIONAL GO

**理由摘要**：

- ✅ **安全基线显著优于同类桌面工具**：密钥(DPAPI+ACL)、供应链(锁定+哈希+审计)、更新链(RSA 签名清单 + SHA256 + Authenticode + 证书指纹 fail-closed)、传统 AppSec(SQL 全参数化、无危险反序列化、SSRF/HTTPS 防护)、密码学、Skill 导入(无脚本/Shell/MCP/浏览器执行)——均 PASS。
- ✅ **框架定义的 8 条攻击链（A–H）在当前 RC 中均不可达**：因本应用**无生产副作用工具**、**RAG/Memory 在生产请求中未接线**、**单用户本地**。"敏感读取 + 外发写入"工具对不存在，故 Indirect Injection Exfiltration（链 A）、Cross-Tenant（B）、Confused Deputy（C）、RAG Poisoning（D，未接线）、Memory Poisoning（E，改不了权限）、MCP Poisoning（F，无 MCP）、Coding Agent Supply Chain（G，无）、Cost Amplification（H，无递归 Agent）全部无法成立。
- ⚠️ **但存在两类必须正视的 FAIL**，不能视而不见：
  1. **Privacy Gate = FAIL**：`privacy-audit-2026-09-06.md` 自判 **NOT PRIVACY CLEAN**（PRIV-001 密钥轮换、PRIV-002 旧 Release 附件替换、PRIV-004 卸载残留）尚未闭环；且用户原文/成稿**默认明文持久化**于本地 SQLite，卸载后保留（SEC-201）。
  2. **Prompt Injection Containment / Indirect Injection / High-impact Confirmation / RAG Poisoning Resistance = FAIL**：当前仅依赖"可绕过型正则黑名单 + System Prompt 文本声明"作为注入边界（SEC-001~004/010/011/012），**无任何代码层结构化隔离**。这些在"无工具"形态下爆炸半径受限（仅文本质量/隐私降级），但**一旦后续接入副作用工具 / RAG / Memory，将直接转化为可打通的攻击链**。

**因此判定 CONDITIONAL GO**：可在补全"发布前条件"后公开上线；但在"架构性前置条件"满足前，**严禁**将副作用工具或 RAG/Memory 接线进发布版本。

---

## 2. System Inventory（组件资产）

| Component | Technology | Purpose | Trust Level | Sensitive? | Internet Exposed? |
|---|---|---|---|---|---|
| Main App | .NET 8 WPF (WinExe) | 表达润色/提示词优化 UI | User context | — | No |
| LLM Client | HttpClient + provider SDKs | 调用用户自带 LLM | User-controlled endpoint | API Key (DPAPI) | Yes (to provider) |
| Secret Store | DPAPI `ProtectedData` | 加密保存 API Key | CurrentUser+LocalSystem | **Yes** | No |
| Config | `config.json` (non-sensitive) | 用户设置 | Local | No (ApiKey 强制清空) | No |
| Local DB | SQLite (`huaxiazi.db`) | 原文/成稿/版本索引 | Local | **Yes** (明文) | No |
| Skills | Markdown/JSON prompt packages | 受限表达策略（仅文本） | User import | No | No |
| Update | RSA-sign manifest + HTTPS | 可选自动更新 | Signed-only | No | Yes (opt-in) |
| CI/CD | GitHub Actions | 构建/签名/发布 | GitHub | Signing keys | Yes |

---

## 3. Attack Surface（入口面）

| Entry Point | Auth Required | User Controlled | Internet Exposed | Sensitive Ops | Risk |
|---|---|---|---|---|---|
| 主 UI 生成按钮 | No (local) | Yes (text) | No | 无副作用 | Low |
| Provider 配置（端点/Key） | 本地 | Yes | No | 写 DPAPI | Low (本地) |
| Skill 导入（文件/zip） | 本地 | Yes | No | 仅文本注入 | Med (注入) |
| 更新检查（HTTPS 清单） | 签名校验 | No | Yes | 触发下载安装 | Low (fail-closed) |
| 备份恢复（zip） | 本地 | Yes | No | 写数据目录 | Low (隔离) |

注：无 REST API / Webhook / GraphQL / OAuth Callback / Admin Interface 等网络入口。攻击面本质是**本地单用户 + 用户自带 LLM 端点**。

---

## 4. Threat Model（适用攻击者）

| Attacker | 在当前 RC 可达？ | 说明 |
|---|---|---|
| Anonymous Internet Attacker | 否 | 无公网入口 |
| Malicious Web/PDF/DOCX Author（间接注入） | **部分** | 内容可经 Skill/RAG(未接线) 进 prompt，但无外发/执行工具 → 仅文本降级 |
| Malicious Skill Author | **是（受限）** | 恶意识别注入可抬升到 system 角色（SEC-002/203），影响生成风格/隐私，无代码执行 |
| Insider / 同机本地用户 | 部分 | 数据目录依赖 OS 默认 ACL（SEC-002 AppSec）；Secrets 子目录已硬化 |
| Compromised LLM / Prompt Injection | **是（受限）** | 见 §6 Security Invariants —— 因无工具，无法转化为越权/外传 |
| Supply Chain Attacker | 否（当前） | 依赖锁定+哈希+更新签名校验 |

---

## 5. 数据流（一次"生成"实际外发内容）

追踪 `MainViewModel.OptimizeAsync` → `PolishWorkflowService` / `PromptOptimizationWorkflowService` → `AIService.GenerateAsync`：

| 数据 | 进入模型？ | 证据 |
|---|---|---|
| 用户原文 | ✅ user message | `PolishPromptBuilderService.cs:62` |
| 用户显式背景（对象/渠道/目的/正式度） | ✅ system | `PolishPromptBuilderService.cs:28-34` |
| 用户自身设置（persona/偏好/自定指导） | ✅ system | `:35-37` |
| 选中 Skill 元数据 + 指令 | ✅ system | `AgentContextPipeline.cs:41-49` |
| 从原文派生的提示（场景/风险等级/事实锚点） | ✅ system | `:46-56` |
| **RAG 知识** | ❌ 未接线 | `PromptBuilderService.cs:125-134` 未赋值 `Knowledge` |
| **Memory/个性化** | ❌ 未接线 | `AgentContextPipeline.cs:18,52,56` 默认空数组 |

**结论**：当前请求体 = 仅用户原文 + 用户显式背景 + 用户自身设置 + Skill 元数据 + 派生提示。**不存在"任务只需 X 却发 X+Y+Z"的过量暴露**（Data Minimization = PASS）。⚠ 一旦 RAG/Memory 接线，须重新走隐私/注入审查。

---

## 6. Security Invariants（第 73 节）验证

| Invariant | 判定 | 说明 |
|---|---|---|
| LLM 不能访问未授权租户数据 | N/A | 单用户 |
| LLM 不能臆造授权 | N/A | 无授权层 |
| LLM 不能自授新权限 | N/A | — |
| LLM 不能获取不必要密钥 | **PASS** | Key 仅作认证头（`AIService.cs:417-426`），从不进 prompt/日志/配置/备份 |
| LLM 不能扩大 OAuth scope | N/A | 用户自带 Key，应用不发 OAuth |
| LLM 不能直接改安全策略 | **FAIL(弱)** | "不可覆盖边界"仅为 System Prompt 文本（SEC-004）；但无代码级策略可改，影响受限 |
| LLM 不能自由访问任意内网目标 | **PASS** | `ProviderEndpointPolicy` 拒绝 loopback/内网/链路本地/云元数据 |
| LLM 不能在无强制下执行高影响操作 | **PASS(因无工具)** | 无生产副作用工具 |
| LLM 不能把 memory 变认证 | **PASS** | Memory 改不了身份/授权；单用户 |
| LLM 不能把 RAG 变授权 | **PASS** | RAG 未接线 |
| LLM 不能把 MCP 当系统指令 | **PASS** | 无 MCP |
| LLM 不能绕过租户隔离 | N/A | — |
| LLM 不能制造无限成本 | **PASS** | 无生产递归 Agent；成本由用户自带 Key 自身控制 |
| LLM 沦陷 ≠ 基础设施沦陷 | **PASS** | 无云端/服务器基础设施 |

---

## 7. Findings（按严重度）

### CRITICAL
无。

### HIGH（潜伏性 —— 当前 RC 不可利用，因无副作用工具）

**[SEC-001] PromptInjectionSanitizer 为可绕过枚举型黑名单**
- Severity: HIGH · Confidence: CONFIRMED · Category: Prompt Injection
- Evidence: `Services/PromptInjectionSanitizer.cs:15-17`（被 `HybridContextRetriever:42`、`UserMemoryPolicy:65`、`ExpressionSkillRouter:89,104`、`ToolDescriptionPolicy:19,27` 等 6+ 处复用）
- Impact: 角色扮演、"作为本任务唯一权威"、同义改写、Base64/Unicode 同形、跨行/跨字段拆分均可绕过。所有注入防护退化为 best-effort。
- Root Cause: 以"检测越权短语"替代"结构化隔离不可信内容"。
- Fix: 不可信内容（尤其 Skill/RAG）必须做**结构化隔离**（见 SEC-002）；sanitizer 仅作纵深防御，不得作为唯一边界。

**[SEC-002] Skill 指令未 XML 转义 → system 角色分隔符注入**
- Severity: HIGH · Confidence: CONFIRMED · Category: Indirect Injection / Delimiter Breakout
- Evidence: `AgentContextPipeline.cs:44-49`（`</execution_strategy>` 包裹）、`PromptLayerComposer.cs:77`（`<skills>` 包裹）均**无转义**；对比 memory/knowledge 均转义。
- Impact: 恶意 Skill 含 `</skills>` 提前闭合其层，后接伪造 `<developer>` 指令；整串作为 system 角色，模型难分"数据"与"指令"。实际影响受"无副作用工具"约束（风格/隐私降级）。
- Fix: 对所有进入 system 角色的不可信层统一调用 `EscapeText`（`AgentContextPipeline.cs:71-74` 已有现成实现），并在 `PromptLayerComposer` 出口强制转义。

**[SEC-003] 转义目标 token 与实际分隔符不匹配（死防御）**
- Severity: MEDIUM · Confidence: CONFIRMED · Category: Indirect Injection
- Evidence: `AgentSkillPackageService.cs:476` / `ProfessionalizationPlanner.cs:85` 转义的是 `</external_expression_strategy>`，但 `AgentContextPipeline` 实际用 `<execution_strategy>` / `<skills>`。
- Fix: 转义必须与 `AgentContextPipeline`/`PromptLayerComposer` 实际层 tag 对齐，或统一在 Composer 出口转义所有非稳定层。

**[SEC-004] "不可覆盖安全边界"仅为 System Prompt 文本，无代码层强制**
- Severity: HIGH · Confidence: CONFIRMED · Category: Security Boundary
- Evidence: `PromptSecurityPolicy.cs:7-13` + `AgentContextPipeline.cs:31`（`systemPrompt.Contains("不可覆盖的安全边界")` 字符串判断）。
- Fix: 将"不可信层为数据"升级为**结构化信道隔离 + 响应后处理策略校验**；弱化对 SEC-001 黑名单的依赖。

### MEDIUM

**[SEC-010-A] RAG Poisoning 抵抗仅靠弱正则**
- Severity: MEDIUM · Confidence: CONFIRMED · Category: RAG Poisoning
- Evidence: `HybridContextRetriever.cs:42`（`RemoveUnsafeLines`）+ `KnowledgeContextRenderer.cs:16-28`（标 `reference_only` 仍进 system 角色）。投毒块经弱正则后仍作"参考指令"影响生成。
- 注：RAG 当前未接线，影响待接线后显现。Fix: 带 provenance/完整性标记 + 后处理一致性校验。

**[SEC-011-A] AgentHarness 对 Mutating 工具无 Human Confirmation / 参数沙箱（未来风险）**
- Severity: MEDIUM · Confidence: CONFIRMED(框架) / UNKNOWN(当前不可达) · Category: Tool Least Privilege / High-impact Confirmation
- Evidence: `AgentHarnessExecutor.cs:105-126`（`IdempotencyKey` 即止，无 WHAT/WHO/WHERE/DESTINATION/DATA/IMPACT 确认；`Arguments` 为自由字符串无校验）。
- Fix: 接线 Mutating 工具前必须先落地 Meaningful Human Confirmation + 参数模式校验/目标白名单（参考 `ProviderEndpointPolicy` fail-closed）。

**[SEC-005-A] Tool Description Poisoning 依赖同一弱 sanitizer**
- Severity: MEDIUM · Confidence: CONFIRMED(当前有限) · Category: Tool Description Poisoning
- Evidence: `ToolDescriptionPolicy.cs:19,27`。当前无生产工具不可达；若未来注册工具，恶意 description 可绕过正则。Fix: 工具描述来自本地受信注册表 + 人工/签名校验。

**[SEC-012-A] ExplicitRequirements 未经转义/清洗进 system 角色**
- Severity: LOW · Confidence: CONFIRMED · Category: Indirect Injection
- Evidence: `ProfessionalizationPlanner.cs:88`（`.Trim()` 直接拼入 skill 层）。Fix: 同经 `PersonalizationConstraintCompiler` + `EscapeText`。

**[SEC-203] 外部 Skill 仍以自然语言注入 system prompt（已知残余）**
- Severity: MEDIUM · Confidence: CONFIRMED · Category: Prompt Injection
- Evidence: `ExpressionSkillRouter.cs:83-111` + `AgentContextPipeline.cs:41-49`。威胁模型 SEC-002 已记录为"缓解非根除"。
- Fix: 拆为结构化字段（目标/语气/禁用表达/输出格式）+ 对抗样本回归。

**[SEC-201] 明文 SQLite 默认持久化且卸载保留**
- Severity: MEDIUM · Confidence: CONFIRMED · Category: Data Retention / Minimization
- Evidence: `AppSettings.cs:50,53`（`SaveOriginalText/SaveOptimizedText` 默认 true）；`ArchiveService.cs` 存 `OriginalText/FinalText`；无 SQLCipher；卸载后 `%LocalAppData%\Huaxiazi` 保留。
- Fix: 提供"隐私模式/默认不保存原文"开关并在首次运行明示；或至少对 `OriginalText` 列做 DPAPI 级加密；卸载页明示"卸载不删数据"。

**[SEC-010-CI] CI 发布未注入更新信任锚**
- Severity: MEDIUM · Confidence: CONFIRMED / POTENTIAL(生产影响) · Category: Release Process / Update Trust Chain
- Evidence: `build.yml:91`（`dotnet publish` 未传 `-p:HuaxiaziUpdateManifestPublicKey/-p:HuaxiaziUpdateSignerCertificateSha256`），而 `publish.ps1:166-184` 才注入。公钥为空 → 验签恒失败 → CI 产物自动更新 fail-closed（安全但流程不一致）。
- Fix: 在 `sign-release` job 注入这两个受保护环境变量，使 CI 产物也建立完整更新信任锚；或明文规定"生产发布必须用 publish.ps1"。

### LOW

- **[SEC-011-CI]** `publish.ps1:127` restore 未 `--locked-mode`（CI 用了）。Fix: 对齐 `--locked-mode`。
- **[SEC-012-CI]** `deploy/sign.ps1:133-137` PFX 密码经命令行 `/p` 传入（本机进程可见）。Fix: 改用 `CertSha1` 证书存储引用，避免口令进命令行/历史。
- **[SEC-013]** Git 历史为"加固快照"（squash/rewrite），可见历史零密钥，但快照前开发史是否泄漏不可审计。Fix: 对**任何曾用真实密钥/证书**执行一次 rotate（纵深防御）。
- **[SEC-002-SECRET]** `DpapiSecretStore.cs:47-49` 解密后托管字节数组未 `Array.Clear`；`AIService._apiKey` 为不可变 string 长驻 GC 堆。影响低（需本地内存转储）。Fix: 读取后 `Array.Clear`，高敏场景评估 `ProtectedMemory`。
- **[SEC-002-APPSEC]** 主数据目录 `%LocalAppData%\Huaxiazi` 依赖 OS 默认 ACL，未复用 `HardenDirectoryAccess`（Secrets 子目录已硬化）。Fix: 对 `DataRoot` 也执行一次 ACL 加固，或文档声明"数据目录须为本机用户私有"。
- **[SEC-004-APPSEC]** Skin/Skill 包安装仅校验结构与扩展名，**无签名/哈希完整性校验**（`SkinPackageService.cs:145-179`）。影响低（包内容不加载为代码）。Fix: 分发渠道传播的包加发布者签名校验。
- **[SEC-006-APPSEC]** Provider 端点 `ResolvesToRestrictedAddress` 仅校验时解析一次，`HttpClient.SendAsync` 再次解析存在 TOCTOU（DNS rebinding）。被默认 TLS 证书校验缓解（证书不匹配阻断会话）。Fix: 连接时固定首次校验 IP 或加证书固定。
- **[SEC-009-APPSEC]** `ErrorLogService.cs:54` 堆栈含本地绝对路径（如 `C:\Users\lenovo\...`）。影响低（日志仅用户私有目录；Release 已剥离 PDB）。Fix: redaction 追加本地路径正则抹除。
- **[SEC-204]** 生成失败 `ReasonPhrase`（服务端可控）仍可能入日志（`AIService.cs:183-211`）。Fix: 仅用状态码。

### INFO / 已确认 PASS（强度亮点，不逐项列）

DPAPI 密钥落盘 + 目录 ACL 硬化；config 永不含明文 Key；更新清单 RSA 验签 fail-closed；下载包 SHA256+Authenticode+证书指纹三重校验、仅 HTTPS、同主机、无 TOCTOU；SQL 全参数化、无 `BinaryFormatter`、无 MD5/SHA1/ECB/硬编码密钥；SSRF 防护 + HTTPS 强制 + 重定向不泄 Key；Skill 导入隔离"仅提示不执行"、zip-slip/炸弹/符号链接防护；Memory 同意门控+敏感排除+PII 脱敏+XML 转义；CI 最小权限+action SHA pin+fork PR 不可触达密钥+签名 Job 仅 tag 触发；发布物不入库（`.gitignore`）；无遥测。

---

## 8. Production Security Gate（第 85 节决策表）

| Gate | Result | 关键依据 |
|---|---|---|
| Release Candidate Immutable | **UNKNOWN** | 版本锁定 v2.0.0，但未提供 Git SHA/Tag/Digest |
| Architecture Understood | PASS | 单用户本地优先桌面 LLM 工具 |
| Threat Model | PASS | 既有 threat-model 与代码一致 |
| Secret Exposure | PASS | DPAPI+ACL，config 无明文（内存残留 LOW） |
| Git History Secrets | PASS | 全量扫描零命中（建议 rotate，SEC-013） |
| Test Credentials | PASS | 未发现任何 test/demo 生产权限密钥 |
| Authentication | **N/A** | 单用户本地，无账号 |
| Authorization | **N/A** | 无角色/多租户 |
| Tenant Isolation | **N/A** | 单用户 |
| Application Security | PASS | SQL 全参数化、无危险反序列化、SSRF/HTTPS 到位 |
| Privacy | **FAIL** | 明文 SQLite 默认持久化(SEC-201)；privacy-audit 自判 NOT PRIVACY CLEAN |
| Data Minimization | PASS | 当前请求仅必要字段，RAG/Memory 未接线 |
| Prompt Injection Containment | **FAIL** | 仅可绕过黑名单+文本边界(SEC-001/002/003/004) |
| Indirect Injection Containment | **FAIL** | RAG/Skill/Memory 共用弱 sanitizer(SEC-010/203) |
| Tool Least Privilege | PASS(当前) | 无生产副作用工具；Skill 执行隔离 |
| Tool Parameter Security | UNKNOWN | 无生产工具参数路径可验；框架无校验(SEC-011) |
| High-impact Confirmation | **FAIL(框架)** | AgentHarness 无 Meaningful Human Confirmation(SEC-011) |
| MCP Security | PASS | 无 MCP |
| RAG Authorization | PASS | 按敏感度过滤；单用户无需 per-user |
| RAG Poisoning Resistance | **FAIL** | 投毒块经弱正则仍作指令(SEC-010) |
| Memory Isolation | PASS | 同意门控+敏感排除+脱敏+转义；跨租户 N/A |
| Multi-Agent Security | UNKNOWN | Autonomous 模式存在但无生产多体回路 |
| Infrastructure | **N/A** | 无云/服务器（本地桌面）；更新/CI 在 GitHub |
| Cloud IAM | **N/A** | 无云资源 |
| Supply Chain | PASS | 依赖锁定+contentHash+审计（1×LOW） |
| CI/CD | PASS | 最小权限+SHA pin（1×MEDIUM SEC-010-CI） |
| Logging / Telemetry | PASS | 脱敏+无遥测（堆栈路径 LOW） |
| Rate / Cost Limits | PASS(用户侧) | 无服务端限速；递归 Agent 未上线 |
| Abuse Controls | PASS | fail-closed 端点、签名更新、Skill 不执行 |
| Monitoring | UNKNOWN | 本地审计链存在，但无主动告警/检测 |

**Gate 统计**：PASS 18 · FAIL 6（Privacy、Prompt Injection、Indirect Injection、High-impact Confirmation、RAG Poisoning、+RC Immutable 计 UNKNOWN）· N/A 7 · UNKNOWN 4。
**关键定性**：6 个 FAIL 中，Privacy 是真实数据留存/运营缺口；其余 5 个注入类 FAIL 均为**潜伏性架构弱点**——在当前"无副作用工具 + RAG/Memory 未接线"形态下不可利用，但构成未来能力上线前的硬性前置条件。

---

## 9. Attack Graph（第 82 节）

**当前 RC 可达的攻击链：无。**

以框架最高优先的链 A（Indirect Injection Exfiltration）为例，在当前 RC 的判定：

```
Malicious Web/PDF/Skill
   │ (间接注入)
   ▼
Agent (system 角色被抬升, SEC-002)
   │
   ▼
Sensitive Read Tool  ──✗ 不存在──┐
   │                            │
   ▼                            ├─ 链路断裂：无 "读取+外发" 工具对
External Write Tool ──✗ 不存在──┘
   │
   ▼
Exfiltration  ──✗ 不可达
```

链 B（Cross-Tenant）/ C（Confused Deputy）/ D（RAG Poisoning，未接线）/ E（Memory Poisoning，改不了权限）/ F（MCP，无）/ G（Coding Agent，无）/ H（Cost，无递归 Agent）——同样断裂。

**结论**：所有 CRITICAL 候选（第 75 节：未认证 RCE、跨租户敏感数据访问、认证绕过、注入→敏感数据外传、Agent→云管理员、任意高权限工具调用、未认证生产库访问）在当前 RC 中**均不成立**。这是 CONDITIONAL GO 而非 NO-GO 的根本依据。

---

## 10. 发布条件（CONDITIONAL GO 的约束）

### A. 发布前必须闭环（阻塞公开上线）
1. **隐私闭环**：完成 `privacy-audit` 的 PRIV-001（轮换任何曾暴露的 Key）、PRIV-002（用加固构建替换旧 Release 附件）；
2. **明文留存决策**：对 SEC-201 给出用户可感知方案——提供"隐私/不保存模式"开关，或至少在首次运行与卸载页**明示**"原文/成稿默认明文存储于本机、卸载不删数据"；
3. **RC 锚定**：以带签名的 Git tag/commit（含 SHA + 构建 digest）重新绑定本结论。

### B. 架构性前置条件（严禁在以下完成前接线副作用能力）
4. 对进入 system 角色的所有不可信层统一 `EscapeText`，消除 SEC-002/003/012 分隔符注入；
5. 将"不可信层为数据"从文本声明升级为**结构化隔离 + 响应后处理校验**（弱化 SEC-001 黑名单依赖）；
6. 若启用 Mutating 工具/RAG/Memory，必须先落地 Meaningful Human Confirmation + 参数模式校验/目标白名单（SEC-011-A），并重新走完整审查。

### C. 建议修复（非阻塞，优先级按影响）
- SEC-010-CI（CI 注入更新信任锚）
- SEC-011-CI（`publish.ps1` 加 `--locked-mode`）
- SEC-012-CI（PFX 改用 `CertSha1`）
- SEC-002-APPSEC（主数据目录 ACL 加固）
- SEC-009-APPSEC（日志路径抹除）
- SEC-004-APPSEC（分发包签名校验，可选）
- SEC-013（密钥/证书 rotate 作为纵深防御）

---

## 11. 修复优先级（第 83 节）

`Security Impact × Exploitability × Blast Radius ÷ Engineering Cost`

1. **最高优先（安全边界类）**：SEC-002 / SEC-004 / SEC-003 / SEC-001（结构化隔离 + 转义一致性）—— 它们决定"未来能力上线时是否会被注入打通"，且修复成本低、收益高。
2. **次高（隐私/运营）**：SEC-201 + PRIV-001/002 闭环。
3. **流程一致性**：SEC-010-CI。
4. **加固类 LOW**：ACL / 日志 / 包签名 / rotate。

---

## 12. 方法论与可信度说明

- 全程只读，未触碰生产数据、未部署、未调用真实接口。
- 4 路并行取证均给出 `file:line` 证据；对既有 `privacy-audit`、`security-assessment-followup`、`PromptFloat-threat-model`、`release-v2.0.0-public`、`发布检查清单` 的"已修复"声明做了代码级抽查，**1–19 项声明全部在代码中落实（PASS）**，未发现"声称已修复却未落实"的矛盾。
- 本审查**确认并补充**了既有文档结论：既有威胁模型已自陈 TM-006（可绕过黑名单）、PRIV 未闭环；本审查进一步定位了具体代码缺陷（SEC-002/003/004 转义不一致、High-impact 确认缺失），使"缓解非根除"从文档陈述变为可执行的代码级修复清单。
- 未验证项如实标注 UNKNOWN（RC 不可变标识、Multi-Agent 回路、Monitoring 主动告警、SEC-110/113 部分 UI 链接校验），未伪造 PASS。

---
*Generated by 匣灵 · Production Release Security Review (framework: 写作.docx)*
