# ADR-010: Durable parsed artifacts and source anchors

Status: Accepted

## Context

Loregrove needs reproducible text observations for later chunking, retrieval, citations, and
knowledge evidence. Parser output is derived from an immutable source and may change when parser code,
configuration, or output schemas change. Treating the latest output as mutable or canonical would
erase provenance and blur the boundary between evidence and interpretation.

## Decision

Loregrove persists immutable parser outputs as versioned `ParsedArtifact` records and
content-addressed JSON files, then projects their normalized ordered observations into structured
`SourceAnchor` records. Parsed artifacts and anchors are Tier-2 derived evidence. They remain tied to
an immutable `SourceDocumentVersion`, source content hash, deterministic parser fingerprint, and
schema-versioned typed locator. They never replace the original source as authoritative evidence.

One artifact is transactionally current per source version, enforced by a filtered SQLite unique
index. Historical artifacts and their anchors remain immutable when a parser fingerprint changes.
The current application-owned processing job provides the durable parsing claim and advances to
Pending/Chunking after a successful relational commit.

Artifact-file finalization precedes the relational artifact/anchor transaction. If that transaction
fails, the finalized immutable file remains as a safe orphan and is never deleted during rollback.

## Consequences

Positive:

- parser output is reproducible and traceable;
- identical fingerprints are idempotent;
- parser changes can be reprocessed without erasing history;
- stable structured evidence units are available for later chunking and citations;
- source observations can be extracted without AI;
- original evidence retains its higher trust authority.

Tradeoffs:

- derived artifact history consumes additional storage;
- parser and locator schemas require explicit version management;
- relational metadata and immutable artifact files have deliberate orphan-safe consistency semantics;
- current-artifact switching and concurrent claims require transactional constraints;
- locator serialization is infrastructure-owned and must remain deterministic and validated.
