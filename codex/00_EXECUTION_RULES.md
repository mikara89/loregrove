# Codex Execution Rules

Apply these rules to every implementation prompt.

## Workflow

1. Read the requested prompt and relevant architecture/ADR docs.
2. Inspect the current repository; do not assume files exist.
3. Keep the PR scoped to the prompt.
4. Prefer simple implementations that preserve boundaries.
5. Add/update tests with behavior changes.
6. Run formatting/build/tests before finishing.
7. Do not introduce infrastructure outside accepted ADRs without a new ADR.
8. Do not add cloud sync, accounts, multi-tenancy, autonomous agents, or model process management unless explicitly requested.
9. AI provider and Docling DTOs must not enter Domain.
10. Generated knowledge must remain distinguishable from source evidence.
11. Primary product screens belong in shared Razor components, not duplicated MAUI XAML.
12. Fluent UI Blazor v5 is the UI component/design system unless an ADR changes it.
13. Linux GTK dependencies may exist only in the later Linux head/integration project.
14. Do not add a localhost ASP.NET server for desktop UI/backend communication.
15. Documentation architecture/process diagrams must use GitHub-compatible Mermaid.
16. Keep literal repository trees, commands, JSON, code, and schemas in ordinary code fences.
17. Pin package versions through Central Package Management; do not use floating production dependencies.
18. Treat experimental dependencies as isolated adapters with an explicit exit path.
19. In shared Razor UI, prefer Fluent UI v5 for interactive controls, navigation affordances,
    feedback surfaces, disclosure controls, data widgets, and standard layout primitives. Keep native
    semantic HTML for document structure; do not hand-build raw HTML widgets when a suitable Fluent
    component exists.

## Git

One prompt normally maps to one branch/PR.

Suggested branch format:

`feat/<short-scope>`

Do not mix unrelated refactors.

## Required completion report

End every Codex task with:

### Status Report
- **Current stage**
- **Completed**
- **Key files changed**
- **Tests/build**
- **Windows/macOS validation**
- **Architecture/behavior notes**
- **Blockers or risks**
- **Recommended next step**

For Linux-specific work, replace/add a Linux validation line.

If a task cannot be completed, leave the repository buildable and report the exact remainder.
