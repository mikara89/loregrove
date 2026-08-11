# Prompt 08 — Chunking and FTS5

Define `IChunker`. Selectively port/adapt OpenRAG Markdown/Docling-aware behavior.

Requirements:
- stable chunk identity from source/content hashes;
- source anchors + heading/context;
- persisted chunks;
- SQLite FTS5 for chunks, source names and future notes;
- lexical search service;
- Search UI result snippet and source navigation;
- stability/idempotency tests.

Acceptance: parsed content is useful/searchable without embeddings/chat.
