# Prompt 11 — Knowledge Compiler: Candidate Extraction

Implement extraction-run records and versioned structured schemas for source summary, categories/topics, entities, claims, relationships, dates/events/decisions and likely version relationships.

Rules:
- results are candidates only;
- every candidate references evidence anchors;
- record model/provider/prompt/schema/input hashes;
- validate structured output;
- source text is untrusted data, not instructions;
- prompts live as version-controlled files;
- deterministic fake AI adapter for tests;
- evaluation fixtures include forbidden unsupported claims.

Acceptance: extraction produces inspectable candidates and cannot directly mutate canonical nodes/assertions.
