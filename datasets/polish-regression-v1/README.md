# Internal Polish Regression Set v1

> **Internal engineering material. `phase_0_gate_contribution: 0`. Not admissible as blind evaluation evidence.**

This directory is reserved for reproducible internal regression work derived from the legacy synthetic polish corpus. It can support tool-chain checks, schema smoke tests, and explicitly labeled coverage exploration. It does not represent real user needs or distribution, does not establish external quality, and must not be used to activate prompt, routing, or model-weight changes.

## Isolation boundary

- Never pass files from this directory to `blind-validate` or `blind-evaluate`.
- Internal `origin` values are only `project_synthetic_legacy`, `ai_assisted_draft`, and `human_authored_internal`.
- The blind-evaluation source kinds `project_owned`, `licensed`, and `user_authorized_deidentified` are forbidden here. Do not relabel synthetic or AI-authored material as an authorized blind-evaluation source.
- Nothing in this directory contributes to the formal phase 0 authorized blind-evaluation count. The contribution is fixed at **0**.
- No request body, real user data, or credentials may be introduced. Synthetic cases must be checked for direct identifiers and secrets before they are accepted into the internal set.

## Version and change control

Each build starts in a new version directory. The bootstrap `manifest.json` may transition exactly once from `initializing` to `frozen` after every artifact and hash is present. After freezing, do not overwrite any file or regenerate in place. A changed schema, clustering rule, label, or data build must be written to a new version directory (for example `polish-regression-v2/`) with a new manifest and hashes. Keep `datasets/polish-agent-v2/` unchanged as the legacy corpus and source of lineage. Generated data/report files use create-only writes; a collision is a failed build requiring a new destination.

## Counting and review

Semantic families are the unit for coverage and split isolation; rows are variants, not independent samples. Generated labels remain drafts until a human reviewer records a decision. AI must not approve its own draft. Family boundary pairs require a recorded human adjudication. Until that work is complete, report review status as pending/unreviewed and do not claim the dataset DoD is complete.

## Known limitations

The legacy corpus is a synthetic template-rotation smoke corpus. Its 3,000 rows are not 3,000 independent examples. Current reported baseline findings are 16 distinct input texts, 2 distinct reference outputs, one expected-decision class (`polish`), no clarification examples, and 5 of 5 test inputs also present in train. These measurements must be reproduced by the audit command before the baseline is frozen. Legacy train/dev/test splits are not evidence of unseen-input generalization.

This dataset cannot establish real-user quality, external validity, safety performance, or the behavior of scenarios absent from its reviewed families. Formal blind evaluation remains a separate authorized-data track.
