# 话匣子隐私内容与数据最小化审计

审计日期：2026-09-06  
范围：源码、测试、配置、文档、CI、构建物、Git 引用与本机运行数据目录  
审计方式：静态扫描、Git 历史扫描、发布包清单检查、关键安全回归测试

## 1. Executive Summary

| 等级 | 数量 | 结论 |
| --- | ---: | --- |
| P0 | 1 | 用户报告的 API Key 已按真实泄露处理，必须撤销并轮换 |
| P1 | 1 | 本地安全加固修改尚未全部进入 GitHub，旧 Release 不代表当前工作区 |
| P2 | 3 | Git 提交者个人邮箱、内部审计绝对路径、运行数据持久化边界需要治理 |
| P3 | 1 | 部分测试与开源归属信息仍可进一步最小化 |

最终判定：**NOT PRIVACY CLEAN**。

原因不是在源码中发现了新的明文密钥，而是：已有密钥曾被暴露、Git 历史含个人联系方式、当前工作区与公开仓库不同步，且旧发布物没有包含最新安全修复。

## 2. Privacy Inventory

| 数据类别 | 当前载体 | 是否应存在 | 处置 |
| --- | --- | --- | --- |
| API Key | 用户本机 DPAPI 密钥文件 | 功能必需 | 保留在本机，不进入仓库/备份；已暴露值必须轮换 |
| 用户原文、成稿与会话 | 本机 SQLite、草稿文件、剪贴板 | 产品功能需要 | 不提交仓库；提供本地删除能力 |
| 非敏感设置 | 本机 `config.json` | 功能需要 | 忽略、不打包、不上传 |
| 错误日志 | 本机 `errors.log*` | 诊断需要 | 忽略；代码已增加敏感字段脱敏与大小限制 |
| 外部 Skill | 源码内开放许可快照、本机自定义副本 | 功能需要 | 只保存策略文本，不执行工具和脚本 |
| 测试数据 | 测试文件中的合成字符串与 localhost 端点 | 测试需要 | 保留；未发现生产数据库或真实用户记录 |
| 发布物 | GitHub Release 附件 | 分发需要 | 只放无配置的二进制；发布物不应包含 PDB、数据库和密钥 |
| 开发者/第三方归属信息 | Git 作者元数据、第三方翻译注释 | 部分为开源归属 | 新提交使用 GitHub noreply；历史邮箱需单独治理 |

## 3. Findings

| ID | 等级 | 位置 | 类型 | 问题 | 动作 |
| --- | --- | --- | --- | --- | --- |
| PRIV-001 | P0 | 用户报告的已暴露 API Key | 凭据 | 该 Key 已经出现在对话/外部可见内容中，不能再视为安全 | **REVOKE + ROTATE**；由密钥所属平台账户执行 |
| PRIV-002 | P1 | 本地工作区与 GitHub `main` | 发布不同步 | 本地存在 30 个已修改文件和 6 个未跟踪文件，含 DPAPI、日志、更新校验和测试加固；公开 `main` 尚未包含这些修改 | 审计通过后才提交；重新构建并替换 Release |
| PRIV-003 | P2 | Git 提交者元数据 | 个人信息 | 历史提交者邮箱含个人联系方式；删除当前文件不能消除 Git 历史副本 | 新提交使用 noreply；历史重写需所有者明确批准并评估 fork/clone 副本风险 |
| PRIV-004 | P2 | 本机运行数据目录 | 用户内容持久化 | `config.json`、DPAPI secrets、SQLite 数据库、drafts 和日志会在卸载/重新下载后继续存在，因此同一 Windows 用户重新运行程序仍可看到旧配置 | 在产品中明确“卸载不删除用户数据”；增加数据目录清理与密钥撤销指引 |
| PRIV-005 | P2 | `docs/testing-expert-data-protection-audit.md` 与验收 HTML | 本机环境信息 | 内部审计材料含本机绝对路径和详细工程上下文，不适合公开 | 已加入 `.gitignore`，不进入 GitHub |
| PRIV-006 | P3 | Inno Setup 中文翻译文件 | 第三方归属 | 文件含上游翻译维护者邮箱；这是许可证/归属信息，不是用户数据 | 当前保留以满足 MIT 归属；若要删除必须先确认许可证要求 |

## 4. Secret Rotation List

| 凭据 | 服务 | 状态 | 是否需要轮换 | 是否需要撤销 |
| --- | --- | --- | --- | --- |
| 用户 API Key（仅保留 `sk-…` 形式摘要） | 对应模型供应商 | 已按泄露处理 | 是 | 是 |
| GitHub CLI 会话令牌 | GitHub | 未发现进入仓库；仅用于本次运维 | 按平台策略检查 | 如怀疑终端日志泄露则撤销 |
| 发布签名证书/PFX | GitHub Actions Secret | 未发现进入工作区 | 不需要 | 不需要；仅检查仓库 Secret 配置 |

审计报告不记录任何完整密钥、Token、Cookie 或密码。

## 5. Test Data / API / Artifact Review

- 未发现 `.env`、数据库 dump、SQL 导出、真实用户聊天记录或生产配置进入当前源码树。
- 测试中的 `localhost` 地址只指向本机 Ollama/LM Studio 兼容端点，属于合成测试配置；未发现 `dev/staging/preprod/internal` 服务地址。
- 发布 ZIP 只包含应用、默认配置、Skill 快照和资源；不包含 `config.json`、`secrets`、用户数据库或 PDB。
- 对 EXE、ZIP、安装包执行可读凭据模式扫描，未发现 `sk-`、Google API Key、AWS Access Key、GitHub Token 或私钥头。
- 发布包仍可能读取当前用户已有的 `%LocalAppData%\Huaxiazi` 数据，这是运行时数据持久化行为，不是把密钥编译进 EXE；已暴露 Key 仍必须撤销。

## 6. Git History Findings

- 已扫描当前分支、标签和本地引用中的源码内容，未发现常见 API Key、JWT、私钥或 Bearer Token 模式。
- Git 作者元数据仍包含个人邮箱；公开仓库的历史对象和已有 clone/fork 可能继续保留它。
- 历史重写不是本轮自动执行项：它会改变提交 ID、破坏现有 clone，并不能替代凭据轮换。若所有者批准，应使用 `git filter-repo` 生成新历史、强制推送并通知所有协作者。

## 7. Data Flow Summary

```text
用户输入 → 内存工作流 →（用户点击生成）→ 所选模型供应商
       ├→ 本地草稿/SQLite（可配置保留与清理）
       ├→ DPAPI SecretStore（仅 API Key）
       └→ 脱敏错误日志/诊断摘要
```

API Key 不应进入提示词、请求日志、配置 JSON、备份 ZIP、Skill 内容或诊断复制文本。用户原文只在用户主动生成时发送给所选供应商。

## 8. Preventive Controls

1. 提交前运行 secret、PII、绝对路径和二进制字符串扫描。
2. CI 禁止打印环境变量、Authorization、请求体和响应体；只允许使用 GitHub Actions Secret。
3. 测试数据政策固定为合成数据和保留域名，不复制生产数据库。
4. 发布前从干净工作树构建，检查 ZIP 条目无 `config.json`、`secrets`、数据库、日志和 PDB。
5. 新提交统一使用 GitHub noreply 身份；历史邮箱治理单独审批。
6. 每次公开 Release 都记录 SHA-256，并在替换资产后重新核验。

## 9. Final Privacy Verdict

**NOT PRIVACY CLEAN**

放行条件：撤销并轮换已暴露 API Key；审核并提交当前安全加固修改；重新构建发布物；决定是否批准 Git 历史邮箱清理。完成后重新执行本报告中的二次扫描，才能改判为 `PRIVACY CLEAN`。
