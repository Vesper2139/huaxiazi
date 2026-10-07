# 项目结构与开发入口

更新时间：2026-10-07

## 产品架构

```text
WPF Views
  -> ViewModels
    -> Services（工作流、上下文、偏好、质量门、路由）
      -> Provider（配置的云端 API 或受管本地 GGUF/llama.cpp）
    -> Models / Config / 本地 SQLite 数据
```

- `App.xaml.cs`：桌面应用启动、依赖装配和生命周期入口。
- `Views/`、`ViewModels/`：WPF 交互和界面状态。
- `Services/`：润色、提示词优化、结构化输出、Provider 路由、本地模型与运行时、偏好、隐私和存储服务。
- `Models/`、`Config/`、`Prompts/`、`Skills/`：领域数据、默认设置、提示模板和可选 Skill 资源。
- `Resources/`、`design-system/`：品牌、皮肤、主题和界面规范。
- `deploy/`、`publish.ps1`、`release/`：安装、发布策略和交付物。`release/` 与 `out/` 不应混作源码。

## 质量、数据和训练

- `Huaxiazi.Tests/`：桌面应用与服务回归测试。
- `DatasetBuilder/`：合成/内部评测数据、人工审阅包和数据血缘校验工具。数据集的结构验证不等同于内容质量评估。
- `datasets/`：按用途和版本组织的源样本、内部回归集、人工审阅材料和评测 Schema。`datasets/ai-evaluation/` 是独立评测协议；内部合成数据不能冒充授权盲评数据。
- `training/`：历史提示/输出协议实验记录与未投产训练原型。当前产品路线直接采用已训练的开源 Instruct 权重，不训练自有模型；实验性 Ollama 脚本仅为历史对照工具。
- `out/`、`outputs/`、`training/runs/`：构建/测试工件、人工审阅包和实验记录。清理前需按下方保留边界辨别，不能按目录名一概删除。
- `docs/STATUS.md`、`docs/ROADMAP.md`：当前状态与执行顺序；本文为目录地图；文档分类见 `docs/README.md`；模型直接采用路线见 `docs/OPEN_MODEL_ADOPTION_ROADMAP_2026-10-07.md`。

## 当前模型路线

- 直接采用已经预训练/指令微调完成的开放权重；不开展项目内 LoRA/SFT。
- `datasets/polish-agent-v2/` 的 3,000 行来自合成模板语料，输入与参考输出高度重复，测试输入与训练输入重叠；只可用于流程和格式冒烟，不作为正式训练或泛化证据。
- 近重复审阅副本是数据治理裁定材料，不是润色答案的训练标签。只有完成来源核验、人工内容审阅、去重与独立切分后，才能决定是否形成训练样本。
- `Services/LocalModelCatalogService.cs` 已列出多个 Qwen3 GGUF 下载项；后续聚焦候选模型许可、来源、哈希、chat template、llama.cpp 兼容和任务质量比较。模型与运行时仍独立下载，不放入主安装包。

## 本机开发与保留边界

- WPF 日常构建以仓库 `global.json`、`Huaxiazi.sln`、NuGet 锁文件和 `build.ps1` 为准；发布/安装器检查参照 `deploy/` 文档及 `docs/STATUS.md`。
- 构建缓存和临时数据放在 `out/` 或被忽略的临时目录。`training/.venv/` 是被忽略的本机历史训练环境，不是 WPF 构建或产品运行依赖。
- 保留人工审阅输入、副本、被冻结的 JSONL/manifest、基线报告与可追溯实验记录。确知可再生的 Python `__pycache__`、过期桌面测试 temp 可清理。
- 当前工作区含大量未提交的功能、数据和审阅文件。后续修改需继续保留这些工作，不进行批量重置、清空或重命名。
