# Prompt 19 — Post-MVP Linux GTK4 Preview

This is post-MVP and must not block Windows/macOS releases.

Before implementation, re-check the current status of:
- `dotnet/maui-labs`;
- `Microsoft.Maui.Platforms.Linux.Gtk4`;
- Linux Essentials package;
- Linux BlazorWebView/WebKitGTK package;
- supported .NET/GTK/WebKitGTK versions.

If the backend is no longer viable, stop and document alternatives instead of forcing integration.

## Goal

Add Linux as a separate head project while reusing:

- Loregrove.Domain
- Loregrove.Application
- Loregrove.UI
- local infrastructure projects where portable

## Requirements

1. Create `Loregrove.Desktop.Linux`.
2. Keep it separate from official MAUI target frameworks.
3. Reference the current GTK4 backend packages explicitly as prerelease/experimental dependencies.
4. Host the same `Loregrove.UI` through WebKitGTK BlazorWebView.
5. Implement Linux platform capabilities:
   - files/folders
   - drag/drop
   - clipboard
   - secure storage
   - notifications
   - open/reveal file
   - application data paths
6. Validate SQLite/local files/search/AI/Docling on Linux.
7. Re-run the platform spike:
   - Fluent v5
   - 10k rows
   - Markdown
   - graph
   - dialogs
   - keyboard
   - theme
8. Add Linux-specific CI separately.
9. Create initial packaging experiment for one target distribution first.
10. Mark the release Preview until a stability checklist is passed.

## Architecture guardrails

- no GTK references in Domain/Application/UI;
- no Linux conditionals sprinkled through Razor pages;
- use platform capability abstractions;
- preserve an exit path if the experimental backend changes.

## Acceptance

Linux uses the same product UI with only host/platform integration differences.

If that cannot be achieved cleanly, do not ship Linux and document the blocking incompatibilities.
