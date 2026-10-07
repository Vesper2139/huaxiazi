# llama.cpp b11424 / b11429 本地运行时对照（2026-10-07）

## 结论

b11429 CPU 与 Vulkan 包都通过官方发布 attestation 摘要校验，并能由话匣子产品运行时链路加载同一份 Qwen3-4B GGUF。有效单机对照的结果几乎相同：CPU/Vulkan 的中位延迟和输出速率差异都很小，5 次重复不足以支持升级决策。**继续将 b11424 保持为当前锁定版本，b11429 仅作为通过烟测的候选。**

这不是模型质量评估、跨设备性能结论，也不代表干净 Windows 安装验收。

## 复现实验配置

- 执行日期：2026-10-07（本地时区）
- 应用链路：产品 `LocalRuntimeManager`、`LocalTextGenerationClient`、`GenerationDiagnosticsService`
- 模型：Qwen3-4B Q4_K_M，2,497,280,256 bytes，SHA-256 `7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5`
- 硬件：沿用 2026-10-06 的同机记录，Intel Core i7-13650HX、NVIDIA GeForce RTX 4060 Laptop GPU、25,551,560,704 bytes RAM、Windows 11；本轮未重新采集硬件字段
- 参数：temperature 0.4、top_p 1、max_tokens 512、seed 42、repeat_penalty 1.17、context 4096、batch 512、自动 CPU 线程、保持进程
- 请求：固定合成中文润色输入及 JSON Schema；每个运行时/后端 5 次；每组启动新 server，组内复用进程
- 顺序：b11424 CPU → b11429 CPU → b11424 Vulkan → b11429 Vulkan
- 隔离：未启动、连接或打包 Ollama；`external_api_requests=0`；生成文本没有写入观测文件；`phase_0_gate_contribution=0`
- 运行时身份门禁：运行前对四个已安装 `llama-server.exe` 执行 `--version`，build 编号不符即中止；b11424 Vulkan 从经摘要校验的原始 ZIP 重新解压。

## 运行时制品身份

| 版本 | 后端 | 包 SHA-256 | 本机 `--version` |
|---|---|---|---|
| b11424 | CPU | `d613ef281e23e91b0c9cba171da421223b3b346c6b20ef4825230e08efacb5c7` | `0.5.0-dev`, build 11424, commit `6c59c4007` |
| b11424 | Vulkan | `97de9ac35768f0eb85b88e8a6c4a1c409597fc4eb0d307a34cf36c2988f146e9` | `0.5.0-dev`, build 11424, commit `6c59c4007` |
| b11429 | CPU | `1283323272b04cd07905816a597a0da810918102de958f4ff6f7bbaa70ed2efe` | `0.6.0-dev`, build 11429, commit `d81235049` |
| b11429 | Vulkan | `1bfe78ad9168b79fa02bf67f6af9f5e17a966d824d77238517f7bef12ac73b36` | `0.6.0-dev`, build 11429, commit `d81235049` |

b11429 官方资产摘要见 [release attestation 52904614](https://github.com/ggml-org/llama.cpp/attestations/52904614)；该版本页面标记为 prerelease：[b11429 release](https://github.com/ggml-org/llama.cpp/releases/tag/b11429)。摘要校验只能证明下载文件与发布制品身份一致，不代表已满足话匣子的发布验收标准。

## 结果

| 版本/后端 | 成功 | p50 请求延迟 | p95 请求延迟 | p50 输出速率* | 启动到 ready | 进程峰值工作集** |
|---|---:|---:|---:|---:|---:|---:|
| b11424 CPU | 5/5 | 19.57 s | 26.34 s | 13.54 tok/s | 3.51 s | 4.62 GiB |
| b11429 CPU | 5/5 | 19.62 s | 25.86 s | 13.51 tok/s | 3.10 s | 4.64 GiB |
| b11424 Vulkan | 5/5 | 4.76 s | 6.60 s | 55.68 tok/s | 3.55 s | 3.00 GiB |
| b11429 Vulkan | 5/5 | 4.76 s | 6.56 s | 55.72 tok/s | 3.62 s | 3.00 GiB |

\* 输出速率为每次 HTTP 请求返回的 output token 数除以请求时延的近似值，含 loopback HTTP 开销，不等于纯解码速度。分位数使用 nearest-rank；每组仅 5 次。

\*\* 进程 working set 不等于 GPU 显存。硬件和驱动未在本轮元数据里重新采集，引用的是前一日同机基线描述。

各组首轮请求输出 320/360 tokens，其余四轮均为 265 tokens。这反映输出长度会显著影响请求时延，不能把微小的 p50 差异解释为确定的运行时性能变化。文本内容没有保存，因此本轮不比较润色质量、Schema 稳定性或事实保留。

质量控制记录：首轮试跑发现旧 b11424 Vulkan 解压目录里的 server 实际为 build 11429，该轮数据作废。有效结果来自 b11424 原始 Vulkan ZIP（SHA-256 与官方摘要一致）的全新解压；基准程序对 CPU/Vulkan × b11424/b11429 四个 server 全部执行 build 编号断言后才开始计时。

## 证据位置

- 本机忽略目录下的原始统计：`out/test-artifacts/runtime-version-bench-20261007-verified/benchmark-metadata.json`
- 请求遥测：`out/test-artifacts/runtime-version-bench-20261007-verified/diagnostics/generation-diagnostics.jsonl`
- 运行时聚合摘要：`out/test-artifacts/runtime-version-bench-20261007-verified/diagnostics/generation-diagnostics-summary.json`
- metadata SHA-256：`f0adab455d74f3bc63215a7ac8ce012ba195dec1f71ac19c5b053d7cdef994fb`
- summary SHA-256：`6a8d330357f1b86b3978104555de52ff05bd0cc83c4203ccba01d10d7d0a931a`

## 后续动作

1. 保留 b11424 产品版本锁定，不更新下载目录或默认版本。
2. 如需升级，下一轮采用版本/后端交错顺序、多轮独立重启和更大样本量，补足预热控制与配对统计。
3. 只有在锁定版本后，再用冻结的本地回归切片评估结构化输出与润色质量，并在目标 Windows/GPU 矩阵完成安装恢复验收。
