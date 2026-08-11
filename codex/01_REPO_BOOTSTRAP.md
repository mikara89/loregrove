# Prompt 01 — Repository Bootstrap

Run only after Prompt 00 passes.

Read:
- `README.md`
- `docs/01_ARCHITECTURE.md`
- `docs/10_PLATFORM_UI.md`
- `docs/15_DECISIONS.md` if present
- all ADRs
- `codex/00_EXECUTION_RULES.md`
- platform spike result

Create the initial Loregrove .NET 10 solution.

## Projects

Create:

- `Loregrove.Domain`
- `Loregrove.Application`
- `Loregrove.UI` — Razor Class Library
- `Loregrove.Desktop` — MAUI Blazor Hybrid app targeting Windows + Mac Catalyst
- `Loregrove.Infrastructure.Sqlite`
- `Loregrove.Infrastructure.LocalFiles`
- `Loregrove.Infrastructure.Search`
- `Loregrove.Infrastructure.AI`
- `Loregrove.Infrastructure.Docling`
- `Loregrove.Infrastructure.Desktop`

Tests:

- `Loregrove.UnitTests`
- `Loregrove.ContractTests`
- `Loregrove.IntegrationTests`
- `Loregrove.UITests`
- `Loregrove.EndToEndTests`

## Requirements

1. Central Package Management.
2. `Directory.Build.props`.
3. nullable enabled.
4. analyzers/warnings policy.
5. deterministic builds.
6. `global.json`.
7. `.editorconfig`.
8. `.gitignore`.
9. GitHub Actions CI.
10. architecture tests.
11. minimal MAUI host containing `BlazorWebView`.
12. shared `Loregrove.UI` with Fluent UI Blazor v5.
13. minimal Fluent shell titled “Loregrove”.
14. platform capability abstractions.
15. Windows/macOS conditional platform implementations only where required.
16. no Linux GTK package yet.
17. no product features yet.
18. Mermaid remains documentation standard.

## CI

At minimum:

- Linux runner for non-MAUI unit/architecture tests where practical;
- Windows runner for desktop build/tests;
- macOS runner for Mac Catalyst build/tests.

Structure CI so expensive platform jobs can be separated from fast core validation.

## Acceptance

- core solution builds;
- tests pass;
- Windows app launches;
- Mac Catalyst app builds and launch validation exists/runs where environment permits;
- shared Razor UI is used by both;
- architecture tests enforce no Infrastructure references from Domain/Application/UI;
- no .NET MAUI/WPF dependency exists.
