# Prompt 16 — Knowledge Health and Reflection

Implement deterministic health checks where possible:
- orphan nodes
- broken evidence refs
- possible duplicates
- unsupported source-derived assertions
- contradictions
- stale artifacts
- failed jobs

Add Home health summary.

Reflection may propose cross-document links, contradictions, missing concepts and synthesis candidates, but only as review/proposals. Track input hashes to avoid repeated unchanged work and add AI-call budget safeguards.

Acceptance: health works even without AI where possible; reflection never silently rewrites canonical knowledge.
