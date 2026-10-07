# 话匣子当前状态

更新时间：2026-10-07

## 当前版本

源码版本已统一为 **2.0.4**。Release 候选可从下列路径运行或交付：

- 可直接运行的应用：[Huaxiazi.exe](../out/today-runnable/2.0.4-20261007/portable/Huaxiazi.exe)
- Portable：[Huaxiazi-Portable.zip](../out/today-runnable/2.0.4-20261007/delivery/Huaxiazi-Portable.zip)
- Setup：[Huaxiazi-Setup.exe](../out/today-runnable/2.0.4-20261007/delivery/Huaxiazi-Setup.exe)，并须与同目录的两个 `Huaxiazi-Setup-*.bin` 一起使用。
- 校验清单：[SHA256SUMS.txt](../out/today-runnable/2.0.4-20261007/delivery/SHA256SUMS.txt)

Portable 与 Setup 都由当前源码 self-contained 发布生成。模型和本地推理运行时仍独立下载，不包含在主程序包中。正式 `release/` 未覆盖。

## 开源模型采用状态

- 不计划自行训练基础模型或 LoRA/SFT，直接采用完成训练的开源 Instruct 权重。
- 产品已实现受管 GGUF/llama.cpp 下载与推理路径，内置目录列出 Qwen3 1.7B、4B Instruct 2507、官方 4B 和 8B 候选；继续工作是核对具体权重、兼容性和任务质量，不是增加训练器。
- `training/runs/` 是历史提示/协议诊断记录，不是模型训练成品；实验 Ollama 不属于产品。
- 模型选择与接入路线见[成熟开源模型直接采用路线](OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md)，旧训练脚本说明见[训练实验归档](../training/README.md)。

## 本轮完成

- Release 安全测试改为优先使用 `pwsh.exe`，未安装时回退 Windows PowerShell 5.1。
- Inno Setup 门禁使用注册表 `DisplayVersion`，本机实测 6.7.3 可通过；6.7.2 会拒绝。
- `Directory.Build.props` 成为版本单一来源；README、CHANGELOG 和程序集均已同步到 2.0.4。
- 全量 Release 测试：**2,033 通过、0 失败、11 跳过**。原始记录：[release-v2.0.4-final.trx](../out/reports/20261007-v204-final/release-v2.0.4-final.trx)。测试使用仓库内可写临时目录。
- Portable ZIP 必需文件及 512 个归档条目已检查；校验清单 4 项均与文件 SHA-256 匹配。
- b11429 CPU/Vulkan 官方包已按发布 attestation SHA-256 核验；使用产品本地推理链路与相同 Qwen3-4B 对 b11424/b11429 做了各 5 次性能观察，20/20 请求成功。样本少、运行顺序固定且不评估文本质量，所以 b11424 继续锁定，b11429 保留为候选。详见[对照记录](local-runtime-b11429-comparison-2026-10-07.md)。

## 尚未验收

- 全量测试尚未在用户正常控制台的系统 `%TEMP%` 环境复跑；本结果不能替代该环境验收。
- 尚未通过桌面首启、生成、安装、升级和卸载的人工 UI 流程。
- 正式 `release/` 仍是旧候选，不应作为 2.0.4 发布物分发。
- `out/` 中旧 WPF 转储及重复模型副本尚未删除；保留策略也未落地。
- b11424/b11429 双后端同模型性能烟测已完成；交错重复实验、质量对照和干净 Windows VM 验收未完成。

## 继续推进

按 [ROADMAP.md](ROADMAP.md) 顺序继续：正常控制台基线 → 人工检查隔离候选 → 正式发布目录更新 → 本地运行时双后端与 VM 验收。提交 Git 历史前先取得项目负责人授权。
