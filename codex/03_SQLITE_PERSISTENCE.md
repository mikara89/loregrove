# Prompt 03 — SQLite Persistence

Implement EF Core 10 SQLite persistence for SourceDocument, SourceDocumentVersion, ProcessingJob and ParsedArtifact placeholder.

Requirements:
- initial migration;
- repositories;
- startup migration service;
- schema/application metadata;
- transaction for import metadata + job creation;
- real-file SQLite integration tests;
- crash-recovery query for transient jobs;
- evaluate/configure WAL only if tests support the local pattern.

Acceptance: fresh library initializes, restarts preserve state, interrupted jobs become resumable.
