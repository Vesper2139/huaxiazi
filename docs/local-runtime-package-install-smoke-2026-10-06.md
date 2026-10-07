# Runtime package installation and product workflow smoke (2026-10-06)

## Subtask contract

- **Question:** Do the pinned CPU/Vulkan release archives work through the application's real package installer and installed-runtime path, or did earlier model smoke rely only on manually extracted binaries?
- **Inputs:** Official b11424 Windows x64 CPU/Vulkan ZIPs already downloaded to the isolated artifact directory; the existing imported Qwen3-4B Q4_K_M test model; `LocalRuntimePackageService`, `LocalRuntimeManager`, `LocalTextGenerationClient`, and `PolishWorkflowService`.
- **Constraints:** Use a separate temporary model/runtime root; do not read or alter user configuration; do not configure a cloud provider; do not change model/prompt source or production model files.
- **Acceptance:** Both product installer descriptors pass SHA verification, extraction, executable version check, license copy and registration; installed backend starts and `/health` returns success; a product polish request returns `Final`; manager stops and installer uninstalls the package.

## Implementation and observed result

A temporary harness under ignored `out/test-artifacts/runtime-package-install-smoke/` fed the previously downloaded, attestation-verified archives to the production installer through a local HTTP handler. This exercised the installer without re-downloading 52.7 MB or relying on an external service. Each archive was checked against the production catalog SHA before extraction. Qwen3-4B was read from the existing isolated test model store; it was not recopied.

| Runtime | Verified archive SHA-256 | Backend | `/health` | Workflow | Workflow time | Startup ready | Peak process working set | Uninstall |
|---|---|---|---:|---|---:|---:|---:|---|
| CPU | `d613ef281e23e91b0c9cba171da421223b3b346c6b20ef4825230e08efacb5c7` | CPU | 200 | Final; no repair; 0 quality issues | 27.86 s | 2.57 s | 5,053,988,864 bytes | passed |
| Vulkan | `97de9ac35768f0eb85b88e8a6c4a1c409597fc4eb0d307a34cf36c2988f146e9` | Vulkan | 200 | Final; no repair; 0 quality issues | 4.90 s | 3.96 s | 3,222,388,736 bytes | passed |

Both installed directories contained the **264,887-byte** `THIRD-PARTY-NOTICES-llama-ui.txt`. The same request and runtime values were used for both: temperature 0.4, top_p 1.0, max_tokens 512, seed 42, repeat_penalty 1.17, context 4096, batch 512. Exactly one synthetic input and one request per backend were run. No cloud provider was configured or called.

Full machine-readable observations: `out/test-artifacts/runtime-package-install-smoke/installed-runtime-qwen3-4b-e2e-20261006.json`, SHA-256 `8e478995de759b42af071d881ea136668f833fcc8e24d362a3a71c6bd2ddffee`.

## Interpretation and limits

This closes the integration gap between *real archive → production installer → installed versioned runtime → model load → loopback health → product polish workflow → stop/uninstall* on this Windows RTX 4060 device. It is not a cold install on a clean Windows image: the machine already has VC++ runtime and Vulkan drivers. The local file handler means internet download resume/cancel was not exercised in this run. Working set is not Vulkan dedicated VRAM. The single synthetic result is not model-quality evidence and contributes **0** to Phase 0.

The approximately 5.7× ratio between these one-shot workflow times is not a defensible CPU-versus-Vulkan performance result. Startup, scheduling, warm state, and single-sample variation are not controlled well enough for that claim; the values are retained as smoke observations only.

## Follow-up smoke on the same Windows device (2026-10-06)

The exact package workflow was rerun from a fresh isolated runtime root using the retained, SHA-pinned archives and the same Qwen3-4B model/settings/input. Hardware observed at execution: Intel i7-13650HX (14 cores / 20 logical processors), NVIDIA GeForce RTX 4060 Laptop GPU, driver `32.0.15.8180`.

| Runtime | SHA-256 | Health | Product workflow | Workflow time | Startup ready | Peak working set | Uninstall |
|---|---|---:|---|---:|---:|---:|---|
| CPU | `d613ef281e23e91b0c9cba171da421223b3b346c6b20ef4825230e08efacb5c7` | 200 | Final; no repair; 0 issues | 36.32 s | 4.83 s | 5,098,491,904 bytes | passed |
| Vulkan | `97de9ac35768f0eb85b88e8a6c4a1c409597fc4eb0d307a34cf36c2988f146e9` | 200 | Final; no repair; 0 issues | 6.02 s | 5.12 s | 3,221,966,848 bytes | passed |

The installed package again contained the 264,887-byte Web UI notice. The harness made zero external API requests, read no user configuration, and uninstalled each runtime from the isolated root. The follow-up machine report is `out/test-artifacts/runtime-package-install-followup-20261006.json`, SHA-256 `5777cc2548b4769a68055d5e726cd9e76d33c0c16865de6bf6967b5c82ea5828`.

This is a second one-shot smoke on the same already-provisioned device, not a clean-Windows test or performance benchmark. Workflow time changed from the earlier one-shot observations (CPU 27.86 s; Vulkan 4.90 s); two observations are too few to estimate p50/p95 or claim a stable CPU/Vulkan speed ratio. Both still prove package identity, start, health, one product generation and uninstall on this device. Phase 0 contribution remains 0.

## Next linked task

With package install/start/generate/uninstall verified on one supported device, the next Stage 4 task is the clean-Windows dependency matrix: identify and verify the CRT/Vulkan preconditions on a clean image, ensure missing components produce actionable diagnostics and permitted CPU fallback, then repeat package install and `/health` there. Separately, CPU/Vulkan speed and memory comparisons require repeated, controlled runs and remain engineering benchmarks, not Phase 0 blind evaluation.
