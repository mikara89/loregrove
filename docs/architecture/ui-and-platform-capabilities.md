# UI and platform capabilities

## Shared UI

Primary application screens exist only in `Loregrove.UI`. The native MAUI host creates a window,
hosts a `BlazorWebView`, registers platform capabilities, and manages lifecycle.

```mermaid
flowchart TB
    HOST["MAUI host<br/>startup + lifecycle + DI"] --> WEBVIEW[BlazorWebView]
    WEBVIEW --> SHELL["Loregrove.UI<br/>Fluent shell"]
    SHELL --> HOME[Home]
    SHELL --> LIBRARY[Library]
    SHELL --> SEARCH[Search]
    SHELL --> KNOWLEDGE[Knowledge]
    SHELL --> REVIEW[Review]
    SHELL --> ASK[Ask]
    SHELL --> SETTINGS[Settings]
```

Home is a bootstrap status surface. The other routes are explicit placeholders and do not claim
unimplemented product behavior.

## UI-facing application facade

```mermaid
flowchart LR
    UI[Razor components] --> CLIENT[ILoregroveClient]
    CLIENT --> LIBRARY[ILibraryClient]
    CLIENT --> SEARCH[ISearchClient]
    CLIENT --> KNOWLEDGE[IKnowledgeClient]
    CLIENT --> REVIEW[IReviewClient]
    CLIENT --> ASK[IAskClient]
```

Razor components depend on this stable facade rather than arbitrary handlers. Product operations
will be added to the area interfaces only when their owning milestones define them.

## Desktop capability boundary

`IDesktopPlatform`, `IDesktopDropAdapter`, and `ISecretStore` live in Application. Implementations
live outside Domain, Application, and UI. Prompt 01 registers safe non-persisting placeholders;
native Windows and Mac Catalyst capability implementations remain validation-gated work.

Opaque picker handles must not be interpreted as filesystem paths by shared code. This is important
for sandboxed and security-scoped access on macOS.

## Native drag/drop

HTML drag/drop exposes browser metadata but does not provide reliable durable access to native files.
The production direction is therefore a host adapter rather than a DOM-only import path.

```mermaid
flowchart LR
    OS[Native OS drop] --> HOST[Platform drop adapter]
    HOST --> HANDLE[Neutral picked-file handle]
    HANDLE --> UI[Shared Razor UI]
    UI --> APP[Import application service]
```

The `IDesktopDropAdapter` subscription contract reserves this integration point. Prompt 01 does not
implement native drop registration or document import.
