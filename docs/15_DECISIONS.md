# Current Decisions

| Area | Decision |
|---|---|
| Product name | Loregrove |
| Repository | New repo |
| Runtime | .NET 10 |
| Host architecture | .NET MAUI Blazor Hybrid |
| Primary UI | Fluent UI Blazor v5 in shared Razor Class Library |
| Windows | First-class, MAUI/WinUI 3/WebView2 |
| macOS | First-class, MAUI/Mac Catalyst/WKWebView |
| Linux | Post-MVP experimental MAUI GTK4 head |
| Web version | Not planned |
| Mobile | Not MVP |
| Database | SQLite |
| Persistence programming model | EF Core allowed in Application; SQLite provider isolated in Infrastructure.Sqlite |
| Search | FTS5 + managed vector index MVP |
| Files | Content-addressed local storage |
| Source object identity | Lowercase SHA-256 with `<prefix>/<hash>` keys |
| Capture transaction | Finalize object first; document + version + pending job commit atomically later |
| Parsed evidence | Immutable versioned ParsedArtifacts + structured SourceAnchors; original source remains authoritative |
| Parser identity | Deterministic parser fingerprint over identity, version, schema, and configuration |
| Parsed artifact files | Content-addressed JSON under `artifacts/parsed/<prefix>/<sha256>.json` |
| AI runtimes | User-managed; Loregrove only calls configured providers |
| Docling | Pinned async Serve v1 adapter; ManagedLocal pack or consented Remote endpoint; Disabled/OneShot defer |
| Complex parsed evidence | Canonical Docling JSON + Markdown; XLSX also preserves deterministic Open XML structure |
| Complex source locators | Typed PDF page/region, DOCX hierarchy, PPTX slide, image region, and XLSX sheet/range locators |
| Retrieval chunks | Deterministic versioned ChunkSets with exact half-open ChunkEvidenceSpans to SourceAnchors |
| Lexical search | SQLite FTS5 external-content projection over current source names and chunks; literal queries only |
| Knowledge truth model | Evidence separate from generated interpretation |
| Clarification | Review Inbox + durable user resolutions |
| Docs diagrams | Mermaid |
