# Prompt 12 — Canonical Knowledge and Change Sets

Persist KnowledgeNode, Alias, KnowledgeAssertion, AssertionEvidence and KnowledgeLink.

Implement KnowledgeChangeSet validation/application and KnowledgeRevision audit records.

Reversible operations must include alias add/remove, assertion create/supersede, categorization, and simple node merge.

Source-derived assertions require evidence. Generated answers cannot be source evidence.

Acceptance: canonical changes only happen through audited transactional change sets.
