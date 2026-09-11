# Ollama Agent 架构实验协议

该目录只存放离线/本地模拟 API 的实验脚本、预测、报告和运行日志，不属于桌面产品运行时。

## 固定边界

- 默认 Endpoint 必须是 `localhost`、`127.0.0.1` 或 `::1` 的 Ollama API。
- `invoke-ollama-architecture-eval.ps1` 默认拒绝远程 Endpoint，避免误调用线上 Provider。
- 输入只允许 `train`/`dev` split；冻结 `test` 由独立的一次性评测流程处理。
- API Key 不从脚本参数读取，也不写入输出 JSONL、日志或报告。
- 实验输出放在 `training/`，生产构建不打包该目录。

## 最小运行示例

```powershell
ollama serve
.\training\invoke-ollama-architecture-eval.ps1 `
  -Model qwen3:4b `
  -InputPath datasets/architecture-v1/architecture_dev_requests.jsonl `
  -Output training/runs/local-dev/predictions.jsonl `
  -JsonMode
```

实验结果只能用于候选架构比较和报告生成，不得自动修改 `Prompts/`、运行时配置或安全策略。

多候选矩阵使用 `run-ollama-architecture-matrix.ps1`，按 `architecture_id` 显式筛选请求，不依赖固定行号偏移；每个候选单独生成预测文件，便于复现、审计和成对比较。
