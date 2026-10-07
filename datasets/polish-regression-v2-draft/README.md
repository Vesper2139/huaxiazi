# Internal AI annotation draft v2

This is a versioned annotation sidecar for the frozen `polish-regression-v1` set. It contains AI-authored draft labels for its 16 provisional task families. It does not add or rewrite any cases.

**Every label remains `unreviewed`.** The parent family-to-backbone mapping is provisional. Two backbones are shared across the development and regression splits, and the references are identical across the eight intent variants under each backbone. Review those flags before using any label for scoring or model comparison.

`phase_0_gate_contribution` is **0** and `not_admissible_as_blind_eval` is true. Never pass this package to `blind-validate` or `blind-evaluate`. AI is the drafter only; a human must review each family representative and adjudicate the backbone mapping and reference-reuse flags. The AI draft author cannot approve these labels.

The manifest binds this sidecar to the parent v1 manifest, `cases.jsonl`, source canonical, hierarchy report, prompt, schema, and output hashes. A draft build is create-only. If regeneration is needed, create a new version directory.
