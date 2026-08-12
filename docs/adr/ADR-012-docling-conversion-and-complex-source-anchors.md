# ADR-012: Docling conversion and complex source anchors

Status: Accepted

## Context

Loregrove must derive reproducible Tier-2 observations from complex documents without weakening the
immutability, trust, or transaction boundaries established for TXT and Markdown. Docling provides a
useful structured document plus Markdown, but its HTTP contract, runtime failures, provenance, and
format-specific fidelity require an adapter rather than becoming Application or Domain concepts.
XLSX also contains formulas, hidden sheets, merges, styles, and named tables that a general document
conversion may flatten.

Remote conversion can disclose whole documents and credentials. Managed local conversion can lose
its child generation during a request. Both paths need explicit availability and retry semantics so
missing machinery is not confused with bad source evidence.

## Decision

Loregrove implements one Infrastructure.Docling parser for PDF, DOCX, PPTX, XLSX, PNG, JPEG, TIFF,
BMP, and WEBP. It consumes only the immutable captured source stream. ManagedLocal uses the
exclusive Processing Pack lease from ADR-011; Remote uses an explicitly configured endpoint and
requires document-upload consent. Disabled and OneShot defer before claiming work.

The adapter targets the pinned asynchronous Docling Serve v1 file API and owns all multipart,
polling, response-size, timeout, redirect, proxy, and API-key details. A deterministic conservative
conversion profile and the pack/endpoint identity contribute to the parser fingerprint. A managed
request may be resubmitted once only after its exact lease generation becomes invalid.

The schema-2 artifact preserves canonical structured Docling JSON and normalized Markdown. XLSX adds
a deterministic read-only Open XML structural representation. Parsed blocks carry strict typed
locators for paged regions, structured documents, presentations, images, and spreadsheets. Complex
locator schema 2 preserves every ordered upstream provenance region; presentation locators obtain
slide identity from structural slide-group context rather than fabricating slide 1. Upstream
volatile execution data and machine paths are removed, and raw errors and secrets are never
persisted.

Partial conversion with usable evidence commits as an explicitly partial artifact. Explicit
conversion failure or no usable evidence creates no evidence. Malformed or structurally incompatible
Docling output is an infrastructure failure, consumes the already-acquired attempt, and restores the
job to retryable Pending/Parsing. Availability failures consume no attempt. Per-operation HTTP
timeouts cover headers, body download, and JSON parsing.

## Consequences

Positive:

- all initial complex formats use one bounded and testable conversion boundary;
- artifacts are reproducible and sensitive execution metadata is excluded;
- citations retain format-appropriate source coordinates;
- XLSX evidence preserves structure without evaluating formulas;
- local and remote modes share mapping and persistence semantics;
- missing packs and remote consent/credential problems remain recoverable availability states.

Tradeoffs:

- compatibility is intentionally tied to a pinned Docling Serve v1 contract and Processing Pack;
- structured-output mapping and locator schemas now require versioned fixtures;
- non-seekable inputs can require bounded temporary disk space;
- remote mode deliberately has stricter endpoint and consent constraints;
- a real Processing Pack remains an opt-in validation dependency and is reported separately from
  hermetic automated coverage.

This decision does not approve OneShot execution, pack installation or auto-update, AI enrichment,
formula evaluation, remote URL conversion, retrieval chunking, or generated knowledge.
