# Prompt 02 — Local Library Foundation

Read architecture, domain/trust, security sections.

Implement without AI/Docling:
- document/version IDs and entities;
- `ILibraryPaths`, `IObjectStore`, document repository/application ports;
- streaming SHA-256 content-addressed file store;
- atomic writes using temp file then move;
- library initialization;
- file import command;
- exact-byte deduplication;
- original filename as metadata only, never object-store path.

Tests: duplicate import, unsafe filenames, partial writes, streaming large files.

Acceptance: source bytes are durably stored and reusable before any processing begins.
