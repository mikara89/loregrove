# ADR-013: Provenance-preserving chunks and SQLite FTS5

Status: Accepted

## Context

Loregrove needs useful offline lexical retrieval before AI provider or embedding configuration.
Retrieval units need bounded context, but treating a chunk or search index as evidence would weaken
the existing immutable-source and parsed-observation trust model. Parser and chunker changes must
also be replayable without deleting history or returning stale material.

## Decision

Loregrove stores versioned deterministic `ChunkSet` derivations from current `ParsedArtifact`
records and maps every `Chunk` back to one or more exact `SourceAnchor` spans. Anchor and chunk
offsets are half-open. Composite database constraints prevent cross-artifact or cross-version
provenance. The canonical chunk content and its SHA-256 are fixed for later embeddings.

SQLite FTS5 is a disposable local lexical-search projection over current source names and chunks.
The relational `LexicalSearchEntries` table is its rebuild source; external-content triggers update
FTS in the same transaction as chunking. Literal query compilation, weighted BM25, deterministic
ties, bounded plain-text snippets, and database paging are owned by Infrastructure.Search. Search
never changes evidence or canonical knowledge and remains fully useful without AI providers.

## Consequences

Positive:

- offline filename, heading, and body search;
- stable evidence traceability through exact spans;
- rebuildable lexical index;
- historical re-chunking and parser replay;
- stable future embedding content hashes;
- stable identities for later hybrid-search composition.

Tradeoffs:

- additional ChunkSet, Chunk, span, and projection rows;
- exact offset bookkeeping;
- FTS migration and synchronization triggers;
- explicit chunker schema and fingerprint management;
- retained historical chunk storage;
- SQLite-specific querying remains an infrastructure responsibility.

This decision does not approve embeddings, vector storage/search, hybrid fusion, AI provider UI,
chat, generated knowledge, source preview, or Docling changes.
