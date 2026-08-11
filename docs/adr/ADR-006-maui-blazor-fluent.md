# ADR-006: MAUI Blazor Hybrid + Fluent UI v5

Status: Accepted

## Context

Loregrove requires a rich desktop UI on Windows and macOS. Linux is desirable later, but a web version is not part of the product strategy.

The UI needs rich information presentation, Markdown, evidence comparison, review workflows, and graph visualization.

## Decision

Use .NET 10 MAUI Blazor Hybrid as the desktop host architecture.

Use a shared Razor Class Library with Fluent UI Blazor v5 as the primary UI.

First-class targets:
- Windows through WinUI 3/WebView2;
- macOS through Mac Catalyst/WKWebView.

Future Linux:
- separate MAUI GTK4 head through the experimental `dotnet/maui-labs` backend and WebKitGTK, only after a dedicated validation gate.

## Consequences

Positive:
- one primary UI implementation;
- strong Fluent design system;
- rich HTML/CSS presentation;
- straightforward JS graph/visualization integration;
- official Windows/macOS MAUI support;
- potential later Linux reuse.

Negative:
- Linux path is experimental;
- application depends on WebView quality;
- Mac Catalyst must be tested for desktop UX;
- Fluent UI v5 lifecycle must be monitored if implementation begins on prerelease packages.

## Guardrails

- MAUI XAML stays minimal.
- Linux packages never enter shared projects.
- No local web server between UI and Application.
- UI never accesses persistence/process-management directly.
