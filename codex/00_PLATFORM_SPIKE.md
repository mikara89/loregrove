# Prompt 00 — Desktop Platform Spike

This prompt is a decision gate. Do not implement Loregrove product infrastructure yet.

Read:
- `docs/10_PLATFORM_UI.md`
- `docs/07_RELEASE_DECISIONS.md`
- `docs/08_TECH_BASELINE.md`
- `docs/adr/ADR-006-maui-blazor-fluent.md`
- `codex/00_EXECUTION_RULES.md`

Create a disposable/prototype solution or a clearly isolated `spikes/desktop-platform` area that proves the chosen presentation architecture.

## Stack

- .NET 10
- .NET MAUI Blazor Hybrid
- Fluent UI Blazor v5
- Windows official MAUI target
- macOS Mac Catalyst official MAUI target
- shared Razor components

Verify current compatible package versions before pinning them.

If Fluent UI v5 is still prerelease, use an explicit v5 prerelease version. Do not silently fall back to v4.

## Spike UI

Implement the same shared Razor page/components for Windows and macOS:

1. Fluent navigation/sidebar.
2. Fluent toolbar/search box.
3. document-style data grid/list with 10,000 synthetic rows.
4. Fluent dialog.
5. light/dark/system theme.
6. Markdown rendering.
7. simple evidence comparison card.
8. JavaScript-interoperated graph with ~100 nodes.
9. C# service invoked directly from Razor through DI.
10. progress/status updates from C# to Razor.

## Platform capabilities

Prove:

- file picker;
- folder picker;
- drag/drop if supported by the chosen MAUI host path;
- clipboard;
- secure storage;
- open/reveal file;
- keyboard shortcut handling.

Use small platform adapters where needed.

## Build/package

Windows:
- build;
- self-contained publish/package spike;
- launch smoke test.

macOS:
- build on a Mac;
- launch smoke test;
- identify signing/notarization requirements.

If macOS cannot be executed in the current environment, create the CI/build path and clearly mark runtime validation pending; do not pretend it passed.

## Measurements

Record:

- startup time;
- 10k-row interaction observations;
- graph render/interact behavior;
- memory snapshot after basic navigation;
- any WebView-specific rendering issues;
- any platform-specific UI forks required.

## Acceptance gate

Pass only if:

- Windows and macOS share the same Razor UI project;
- Fluent UI v5 works on both;
- no duplicated screen implementation is required;
- no localhost server is introduced;
- platform capabilities can be isolated;
- graph/Markdown work;
- performance is acceptable for the spike;
- macOS does not expose a fundamental blocker.

Output `docs/spikes/desktop-platform-result.md`.

If the spike fails materially, STOP and recommend the smallest architecture adjustment before Prompt 01.
