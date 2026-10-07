# Internal human review packet v2

This packet prepares, but does not perform, W3.3 human review of the 16 provisional task families in `polish-regression-v1` and their AI-assisted draft labels in `polish-regression-v2-draft`.

## Review order

1. A human reviewer completes `independent_first_pass` in `source-first-pass.jsonl` without opening `draft-comparison.jsonl`. For every family, review the input, context, claims, legacy decision, reference output, and source metadata. Fill proposed fields and record `reviewer_id`, UTC review time, rationale, and uncertainty notes in a controlled review copy.
2. Only after the first pass is recorded, compare with `draft-comparison.jsonl`. Fill `human-decisions.template.jsonl`, including each field decision, the proposed backbone mapping, and the reference-output reuse decision.
3. Keep the untouched packet files. Completed decisions are a new child artifact, never an edit to this packet. A reviewer must not be the draft author; unresolved or ambiguous decisions stay unresolved.

The packet separates source-first review from the AI draft comparison to reduce anchoring, but file separation cannot enforce reviewer blinding. Its labels and reference outputs are synthetic project material, not real-user or external evaluation data. Two provisional backbones overlap development and regression splits; each backbone also reuses the same reference output across eight intent variants. The packet surfaces these conditions for adjudication but does not decide whether reuse is acceptable.

`manifest.json` binds the review forms to the frozen parent, draft, hierarchy report, and packet files. `phase_0_gate_contribution` is **0** and `not_admissible_as_blind_eval` is true. This packet cannot be used to score or promote a model.

## Decision values

- Field outcome: `accept`, `revise`, `reject`, `uncertain`.
- Backbone outcome: `confirm`, `split`, `merge`, `unresolved`.
- Reference reuse outcome: `acceptable`, `requires_distinct_reference`, `exclude_family_from_scoring`, `unresolved`.
- Do not mark a field or family as verified unless a named human independently reviewed it and supplied a UTC timestamp and rationale.
