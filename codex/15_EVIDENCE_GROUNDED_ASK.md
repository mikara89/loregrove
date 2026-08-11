# Prompt 15 — Evidence-Grounded Ask

Implement layered query planning: catalogue + FTS + vectors + bounded graph expansion -> rerank -> exact source anchors.

Use ChatProfile. Prefer a structured citation-bearing answer. Validate citations against supplied evidence.

UI: answer, inline citations, evidence panel, retrieved-context view.

Save answers only as Tier-5 GeneratedArtifacts. User may explicitly turn selected output into a user-authored note while preserving citations.

Acceptance: grounded answers have navigable evidence; invalid/unsupported citations are rejected or visibly flagged.
