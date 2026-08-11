# Prompt 05 — Parsing Abstraction and Simple Formats

Define `IDocumentParser`, normalized document/block/anchor contracts, and `ParsedDocumentResult`.

Implement TXT and Markdown parsers.

Add durable Parsing stage, ParsedArtifact + SourceAnchor persistence, normalized artifacts under the library artifacts directory, retry/reprocess support, and fixture tests.

Acceptance: TXT/MD reach Parsed state and expose searchable normalized text/source anchors without AI.
