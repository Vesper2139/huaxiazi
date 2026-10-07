# Windows 本地运行时制品核验（2026-10-05）

## 当前结论

已找到 Windows x64 CPU/Vulkan llama.cpp prerelease 候选，已下载并做 SHA-256 与 GitHub attestation 验证，也实际调用两包的 `llama-server.exe --version`。2026-10-06 的 workflow/artifact 追溯已将源码提交映射到成功的 Release run `#4773`、head SHA `6c59c40076c00eab49754dc955d7652d93f9e125`；此前看到的 `c06f…` 是发布/attestation workflow 自身的提交，不是编译源码提交。两包仍只列为**测试候选**：prerelease 属性、Vulkan/CRT 许可 notice、干净 Windows 与目标 GPU 验收仍待完成。

## 制品身份

| 字段 | Windows x64 CPU | Windows x64 Vulkan |
|---|---|---|
| release label | `b11424` | `b11424` |
| prerelease | true | true |
| asset | `llama-b11424-bin-win-cpu-x64.zip` | `llama-b11424-bin-win-vulkan-x64.zip` |
| 大小 | 19,394,018 bytes | 33,332,009 bytes |
| SHA-256 | `d613ef281e23e91b0c9cba171da421223b3b346c6b20ef4825230e08efacb5c7` | `97de9ac35768f0eb85b88e8a6c4a1c409597fc4eb0d307a34cf36c2988f146e9` |
| ZIP 内容 | 51 entries；49,485,085 bytes 解压内容 | 52 entries；94,790,429 bytes 解压内容 |
| 主程序入口 | `llama-server.exe` (9,216 B launcher) + `llama-server-impl.dll` (9,160,704 B) | 同 CPU 包 + `ggml-vulkan.dll` (45,305,344 B) |
| 附带许可文件 | `LICENSE-LLVM-OpenMP`；没有 llama.cpp 根 `LICENSE` | 同 CPU 包；没有 llama.cpp 根 `LICENSE` |
| `--version` | `0.5.0-dev (build 11424, commit 6c59c4007)`；Clang 20.1.8 / Windows x86_64 | 相同 |
| 下载地址 | `https://github.com/ggml-org/llama.cpp/releases/download/b11424/llama-b11424-bin-win-cpu-x64.zip` | `https://github.com/ggml-org/llama.cpp/releases/download/b11424/llama-b11424-bin-win-vulkan-x64.zip` |

来源：GitHub 官方 Release API 的 `digest` / `size` 字段；[b11424 release](https://github.com/ggml-org/llama.cpp/releases/tag/b11424) 与 [构建证明 52864580](https://github.com/ggml-org/llama.cpp/attestations/52864580)。本机下载字节的 SHA-256 与官方 digest 一致；`gh attestation verify --repo ggml-org/llama.cpp --signer-workflow ggml-org/llama.cpp/.github/workflows/release-publish.yml --source-ref refs/heads/master` 对两个 ZIP 均 exit 0。该命令验证 attestation 签名、workflow 身份和 source ref；它不能消除下述 build commit 字段差异。

## 身份差异及其处理

- GitHub tag `b11424` 指向 `6c59c40076c00eab49754dc955d7652d93f9e125`；两份 exe 的 `--version` 均报告该 commit 短哈希 `6c59c4007`。
- attestation 52864580 的 SLSA predicate 把 `c06f84160a30c66d7b5a2829ae9b3ea15275cbc3` 记录为 publisher workflow source。单看 attestation 不足以确定编译源码，但 Actions 证据补齐了映射：Release run `37315349497`（run #4773）于 2026-10-05 13:15 UTC 启动、14:43:05 UTC 成功完成，`head_sha=6c59…`；其产物列表含 `llama-bin-win-cpu-x64.zip`（19,394,018 bytes）和 `llama-bin-win-vulkan-x64.zip`（13,363,060 bytes）。
- 发布 run `37326958846` 在该成功 run 结束两秒后启动。固定在 `c06f…` 的 [`release-publish.yml`](https://github.com/ggml-org/llama.cpp/blob/c06f84160a30c66d7b5a2829ae9b3ea15275cbc3/.github/workflows/release-publish.yml) 明确从 `github.event.workflow_run.head_sha` checkout 源仓库，并用 `github.event.workflow_run.id` 下载前序 Release artifacts；随后将 Windows CPU 包内容合并进其它 Windows backend ZIP 并改成 release tag 文件名。Windows 构建 job 的 `actions/checkout` 未覆写 ref，按其[官方默认语义](https://github.com/actions/checkout/blob/main/README.md)，checkout 触发该 workflow 的 SHA。attestation subjects 中最终 CPU/Vulkan assets 的 SHA-256 与本机验证值完全一致，exe 报告值也为 `6c59…`。因此可将这两个 b11424 ZIP 的 `compiler_source_commit=6c59…` 标记为由公开 workflow/artifact lineage 核验；`c06f…` 保留为发布证明的 signer/workflow revision。
- 这条公开证据证明 workflow 选择的源码 SHA 与 release artifacts 的身份链相符，不等于可复现构建或二进制级独立证明；source-level provenance 已映射，但两个包仍不是稳定发行候选。
- 资产 hash 可唯一识别归档，签名证明可验证发布流程声明；二者都不能替代本项目发行签名，也不能说明应用已验证运行时行为。

## 许可与交付范围

- llama.cpp 在 b11424 所用源码根 `LICENSE` 声明 MIT；实际 ZIP 没有带该文件，但两个 ZIP 均带 `LICENSE-LLVM-OpenMP`，CPU 包有 `libomp.dll`，Vulkan 包还含 45 MB `ggml-vulkan.dll`。发布 workflow 说明 Windows CPU 包的 server/CPU 文件会合并到其它后端 ZIP。
- 应用所附 `Resources/Licenses/llama.cpp-MIT.txt` 已与上游 `6c59…/LICENSE` 按字节核验一致（SHA-256 `94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d`）；runtime installer 会把该 MIT 文本复制进安装目录，解压也会保留 ZIP 内 `LICENSE-LLVM-OpenMP`。
- 归档检查补上了之前初步审阅的遗漏：不能据“没有 LICENSE 文件”断言没有任何第三方许可，也不能把 OpenMP notice 当作完整清单。发行前仍需确认 Vulkan backend/loader、Windows CRT 及实际动态依赖的许可与再分发边界，并形成完整第三方清单；MIT 与 OpenMP 文件齐全不等于完整 notice 审核完成。
- 运行时须安装到用户可写的独立目录，不进入主安装包；CPU 与 Vulkan 包分别下载、分别做 SHA-256 校验。Vulkan 启动失败时 CPU 回退仍由应用管理器完成。

## Windows 动态依赖与启动前诊断（2026-10-06）

对本机解压的 b11424 CPU/Vulkan 制品使用 PE 导入表检查后，依赖分为三类：

| 范围 | 导入项/组件 | 处理方式 |
|---|---|---|
| 两个运行时 | `MSVCP140.dll`、`VCRUNTIME140.dll`、`VCRUNTIME140_1.dll` | 应用在健康检查阶段实际尝试加载这三个 x64 VC runtime DLL；缺失时明确建议安装 Microsoft Visual C++ 2015–2022 x64 Redistributable，并显示官方 evergreen 下载地址。微软说明使用动态 CRT 的应用需要目标机存在兼容的 Redistributable；是否将其纳入产品安装流程仍需许可与干净系统验证。[Microsoft Learn](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) |
| Vulkan 运行时 | `vulkan-1.dll` | 健康检查尝试加载系统 Vulkan loader；缺失时跳过 Vulkan 可执行文件探测并尝试 CPU 后端。Khronos 文档指出 Windows loader 通常随显卡驱动安装，且 loader 与 Vulkan-capable driver 是不同组件。[Loader 文档](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderApplicationInterface.md) · [Driver 文档](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/BUILD.md) |
| 运行时/操作系统 | llama/ggml 组件、`libomp.dll`、Windows 系统 DLL 与 UCRT API sets | llama/ggml DLL 和 `libomp.dll` 随运行时归档；OpenMP notice 已由安装器保留。系统 DLL 由 Windows 提供。完整组件许可清单仍须逐项完成。 |

产品行为：缺少 Vulkan loader 时，如果 CPU 依赖齐全且 CPU `--version` 探测通过，健康检查显示 Vulkan 缺项并确认已切换到 CPU；缺少 VC++ runtime 时不启动相应 runtime，并显示可操作的官方安装地址。该检查只加载/释放系统 DLL，不执行安装、不发网络请求、不加载 GGUF。`vulkan-1.dll` 能加载不代表驱动 ICD 可用；驱动枚举和真实 Vulkan 推理仍须通过模型服务启动验证。注入探测器的两条行为回归已加入 `HealthCheckServiceTests`，该测试不等价于干净 Windows 实机验证。

## 构建期组件及额外许可文本（2026-10-06）

- 固定源码 `6c59c40076c00eab49754dc955d7652d93f9e125` 的 Windows CPU release job 配置了 `GGML_OPENMP=ON` 与 `GGML_OPENMP_FETCH=ON`；其 CMake 固定 LLVM OpenMP 20.1.8 安装器摘要，并把 `LICENSE-LLVM-OpenMP` 复制进制品。包内该文件 SHA-256 为 `fdad1758a9e1f9d5a81e18879b3406772115edc92c24bfa36b70c654f325e8e4`，与 CMake 声明一致。
- Windows Vulkan job 固定 Vulkan SDK `1.4.357.0`；`ggml-vulkan` 构建显式链接 `Vulkan::Vulkan`，并要求 Vulkan 与 SPIRV headers。已从相同 SDK tag 的 Khronos 上游保存 Vulkan-Headers 的 `LICENSE.md` 与 Apache 2.0 全文，以及 SPIRV-Headers 的完整 `LICENSE`。Vulkan-Headers 采用其双许可中的 Apache-2.0 路径；安装器将这些文本随版本文件放入 runtime 目录。原 ZIP 本身没有这些文本。
- 安装器现在除 `LICENSE-llama.cpp` 与 ZIP 自带的 OpenMP notice 外，也复制 Vulkan-Headers/SPIRV-Headers 许可文件；运行时交付测试确认附加文本随版本安装。对应源码与文件 SHA：Vulkan-Headers `LICENSE.md` `95ad366d23fadf701d355bc45fb8b82ae2d700239471d35d41286ac3b08ff903`，Apache-2.0 `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`，SPIRV-Headers `ea43b1de38a6f90c488800d66dec1ed671e68cda530266bc96951fb5b6307613`。
- **Web UI notice 已按实际客户端 bundle 收口（2026-10-06）：** 从固定源码 `6c59c40076c00eab49754dc955d7652d93f9e125/tools/ui` 安装锁文件依赖并执行生产构建，通过临时 Rollup 插件读取有非零 `renderedLength` 的客户端 chunk 模块。得到 **2,437 个已渲染模块、163 个 package name/version**；构建主 JS 为 8,918,504 bytes，官方 UI artifact 中对应主 JS 为 8,918,508 bytes，说明构建规模吻合，但不是逐字节构建复现。相比之下，全锁文件的 1,207 个 package 条目包含大量服务端、构建和测试依赖，不能作为交付清单。
- 已按 163 个实际打包 package 的固定版本汇总 npm 原始许可文本和 NOTICE；另从实际 bundle 的虚拟 `nerdamer` 模块识别并附加 3 个 vendored 组件（nerdamer-prime、BigInteger.js、decimal.js）。总计 **166 个组件、264,887 bytes**，写入 `Resources/Licenses/llama-ui-third-party-notices.txt`，SHA-256 `2e95d732f4839faede875f062968d42907fadf8052f920858f0033253f7cb8f3`；机器可读模块/许可清单为 `docs/local-runtime-ui-bundle-dependencies-2026-10-06.json`。两项 package 元数据无 `license` 字段（`khroma@2.1.0`、`svelte-toolbelt@0.10.6`），但固定 npm 包内都带 MIT license 文件；另两个实际 bundle 包（`rehype-katex@7.0.1`、`remark-math@6.0.0`）的 npm tarball没有打包 license 文件，已按 npm `gitHead` 从对应上游提交取到原始 MIT 文本。inventory 为这些情形保留证据来源和文件 hash。许可文本随版本安装为 `THIRD-PARTY-NOTICES-llama-ui.txt`，并由现有 `Resources/Licenses/*` publish 规则进入应用输出。此项关闭的是嵌入式 UI 的 JS/vendored package notices 缺口；CRT、驱动和运行时二进制依赖的许可/干净 Windows 验收仍未完成。

## 后续验收

1. ✅ 已由 Release run #4773、发布 run #35、两份 artifact 和 attestation digest 将 build source commit 映射为 `6c59…`；应用目录与安装 manifest 分别存 `CompilerSourceCommit=6c59…`、`AttestationSourceCommit=c06f…`，并设置 `CompilerSourceVerified=true`。仍保留不可复现构建的证据边界。
2. ✅ 两个真实 ZIP 均通过固定 SHA/attestation 与 `--version` 检查；2026-10-06 又用生产 `LocalRuntimePackageService` 对隔离根完成真实安装、校验、提取、许可复制和卸载，并通过已安装包启动 Qwen3-4B：CPU/Vulkan 均 `/health` 200，产品润色工作流均返回 Final。机器与模型参数、单样本烟测记录见[端到端记录](local-runtime-package-install-smoke-2026-10-06.md)。仍未在干净 Windows 环境验证缺失 CRT/Vulkan 组件诊断与恢复。
3. ✅ 实现 create-only/原子安装、压缩包路径穿越与大小限制、取消/续传、失败清理和卸载；清单绑定精确 SHA、size、release label、报告 commit、attestation ref 和许可证说明。2026-10-06 补齐 Range `Content-Range` 起点/总长校验、旧分片遇 416 后一次从头恢复，以及每个 URL 最多一次的响应前瞬态连接重试（250 ms）。安装服务 + 健康检查组合 **25/25** 通过。真实 CPU ZIP 已通过本地 handler 的“部分下载→416→全量重下”和生产 HTTPS 取消/Range 续传/固定 SHA 安装/版本探测/卸载；有界重试更新后，CPU 与 Vulkan 均再次通过公网取消续传、SHA 安装、版本探测和卸载，二者续传后的首个进度都高于保留偏移 16,384 bytes。单次流程耗时记录仅为烟测，不作模型或性能结论。证据见[实施状态记录](archive/ai-customization-implementation-status.md)、`out/test-artifacts/runtime-public-download-smoke/runtime-public-download-cancel-resume-20261006.json`、`out/test-artifacts/runtime-public-download-smoke/runtime-public-download-cancel-resume-after-retry-20261006.json`、`out/test-artifacts/runtime-public-download-smoke/runtime-public-download-vulkan-cancel-resume-20261006.json`、`out/test-artifacts/runtime-public-download-smoke/runtime-public-download-vulkan-cancel-resume-after-retry-20261006.json` 与此前 416 恢复记录。干净 Windows 与 Vulkan 驱动矩阵仍待验。
4. 在干净 Windows 镜像验证 VC++ Runtime/Vulkan loader 缺失与恢复行为，并在更多受支持 GPU 上重复安装和服务检查；b11424 仍是 prerelease，不能据单机烟测升级为稳定默认版。性能对比须做重复控制实验，不能引用本次单请求数据。
