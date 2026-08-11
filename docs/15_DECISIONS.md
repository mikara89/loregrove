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
| AI runtimes | User-managed; Loregrove only calls configured providers |
| Docling | Optional on-demand managed child process or remote |
| Knowledge truth model | Evidence separate from generated interpretation |
| Clarification | Review Inbox + durable user resolutions |
| Docs diagrams | Mermaid |
