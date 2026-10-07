# Prompt v1: internal annotation drafts

Draft labels for the provided synthetic polish case. Use the requested intent and the text to suggest a normalized intent, scenario, input style, target formality, and candidate factual anchors. Preserve all inherited scenario, channel, risk, decision, claims, reference output, and rubric fields as source metadata; do not silently repair or promote them to gold labels.

Do not infer authorization, human review, or real-user representativeness. Do not decide that a reused reference output is wrong. When different intents under one content backbone share a reference, emit `potential_reference_reuse_requires_review` and explain that a human must decide whether the output satisfies each intent. If evidence is weak, use a low-confidence label or an explicit unknown.

All draft labels start `unreviewed`. Leave `reviewer_id` and `reviewed_at_utc` empty. The draft author must never approve the draft.
