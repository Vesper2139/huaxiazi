# Internal schema v1

This schema is only for internal regression artifacts under `datasets/polish-regression-v1/`. It is intentionally incompatible with the blind-evaluation `source` field.

## Origin values

`origin` is required and must be exactly one of:

- `project_synthetic_legacy`: lineage from the existing synthetic corpus.
- `ai_assisted_draft`: content or labels drafted with AI and not yet approved.
- `human_authored_internal`: authored internally by a human; this does not imply external authorization.

The values `project_owned`, `licensed`, and `user_authorized_deidentified` are explicitly forbidden. They describe source kinds in a separate blind-evaluation protocol and cannot be used to relabel internal work.

## Artifact records

`cases.jsonl` contains one representative case per semantic family. `variants.jsonl` preserves lineage for every imported legacy row and points each row to one family. `labels.jsonl` contains draft or reviewed annotations. The JSON Schema in `case.schema.json` defines the required case identity, task, content, origin, provenance and review status. Schemas and labels are versioned; do not edit an already frozen version in place.

Each AI-drafted label must carry `draft_by`, `model`, `prompt_hash`, `drafted_at`, and `human_status`. The human review fields (`reviewer_id`, `reviewed_at`, and decision) remain absent until an actual human review occurs. A draft is not a gold label. The reviewer must not be the draft author.

## Split and family rules

All variants of a semantic family must share one split. Family IDs are stable within a dataset version. Exact duplicates may be deterministically grouped; near-duplicate boundary pairs remain pending until a human adjudication record identifies the adjudicator and decision. Coverage statistics count families, and reports also disclose row counts.

## Privacy and evidence boundary

Do not put real user content in this internal dataset. Scan inputs, outputs, and context for personal identifiers and credentials. The manifest must state `purpose: internal_regression_only`, `phase_0_gate_contribution: 0`, and `not_admissible_as_blind_eval: true`.
