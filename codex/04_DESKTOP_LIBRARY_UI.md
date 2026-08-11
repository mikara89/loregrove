# Prompt 04 — Shared Fluent Library UI

Read:
- `docs/05_UX_SECURITY_TESTING.md`
- `docs/10_PLATFORM_UI.md`

Implement the first useful application UI in `Loregrove.UI`.

## Requirements

1. Fluent UI Blazor v5 shell/navigation:
   - Home
   - Library
   - Search
   - Knowledge
   - Review
   - Ask
   - Settings
2. Only Home and Library require real product behavior now; the rest are explicit placeholders.
3. File-picker import through a platform capability abstraction.
4. Drag/drop import through the desktop host adapter.
5. Library list/grid:
   - display name
   - type
   - imported date
   - processing status
6. Source detail:
   - metadata
   - open/reveal original
   - processing state
7. Processing queue summary on Home.
8. Responsive desktop layout for normal and narrower windows.
9. Keyboard navigation.
10. light/dark/system theme.
11. no direct DbContext use from Razor components.
12. no duplicated Windows/macOS screen implementations.
13. component tests plus Windows/macOS smoke validation.

## Acceptance

A user can launch Loregrove on Windows or macOS, import a file, restart the application, and see the same persisted source through the same shared Razor UI.
