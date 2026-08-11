# Technical Baseline

This baseline records architectural intent, not an instruction to use floating package versions.

Codex must verify current compatible package versions before changing the dependency graph and pin them through Central Package Management.

## Runtime and desktop

- .NET 10
- .NET MAUI 10
- MAUI Blazor Hybrid
- Windows target through WinUI 3
- macOS target through Mac Catalyst
- shared Razor Class Library for application screens

Official .NET MAUI does not currently make Linux a first-class supported target. Linux is a later separate head using the experimental `dotnet/maui-labs` GTK4 backend if it remains viable.

## UI

- Fluent UI Blazor v5
- host-agnostic Razor components
- CSS/design tokens shared across Windows/macOS/Linux
- JavaScript interop only for specialized visualization/interaction

If v5 is still pre-release during implementation:
- pin an explicit v5 prerelease version;
- do not fall back to v4 without an ADR;
- treat stable v5 availability as a release/hardening gate rather than a reason to redesign the UI.

## Persistence

- EF Core 10
- SQLite
- SQLite FTS5

## AI

- `Microsoft.Extensions.AI` abstractions where appropriate
- OpenAI-compatible provider first
- separate chat/extraction and embedding profiles
- credentials in OS secure storage

## Document processing

- Docling Serve
- local managed on-demand mode
- remote endpoint mode
- simple Markdown/text handled in-process
- XLSX gets additional workbook-structure preservation

## Search

- FTS5 lexical index
- managed flat vector index initially
- hybrid ranking
- graph expansion
- evidence expansion

## Linux later

Current intended experiment:

- `Microsoft.Maui.Platforms.Linux.Gtk4`
- GTK4
- WebKitGTK
- separate Linux head project
- same shared Razor/Fluent UI

This is deliberately not a core dependency.

## References for maintainers

When verifying platform assumptions, prefer:
- Microsoft Learn for official MAUI support;
- `dotnet/maui-labs` for GTK experimental status;
- `microsoft/fluentui-blazor` for Fluent UI Blazor v5/hybrid compatibility;
- Docling official documentation;
- SQLite official documentation;
- Microsoft.Extensions.AI official documentation.
