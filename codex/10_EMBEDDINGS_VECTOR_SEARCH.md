# Prompt 10 — Embeddings and Vector Search

Define `IVectorIndex` and implement managed flat cosine search.

Requirements:
- persist chunk id, profile fingerprint, dimensions, hash and vector;
- efficient normalized float storage/cache;
- embedding cache by `(chunkHash, profileFingerprint)`;
- embedding profile change makes prior index inactive/stale;
- deterministic hybrid lexical+semantic rank fusion;
- benchmarks at 1k/10k/50k/100k vectors;
- document a measured threshold for considering HNSW/native vector extension later.

Acceptance: semantic/hybrid search works and incompatible embeddings never mix.
