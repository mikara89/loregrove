# Architecture

## Architectural style

Loregrove is a local modular monolith with Clean/Onion boundaries.

The desktop host is .NET MAUI Blazor Hybrid. MAUI owns platform hosting and OS integration. The primary application UI is shared Razor components styled with Fluent UI Blazor v5.

```mermaid
flowchart TB
    subgraph Presentation["Presentation"]
        UI["Loregrove.UI<br/>Razor Class Library<br/>Fluent UI Blazor v5"]
        DESKTOP["Loregrove.Desktop<br/>MAUI Blazor Hybrid host"]
        DESKTOP --> UI
    end

    DESKTOP --> APPLICATION[Loregrove.Application]
    APPLICATION --> DOMAIN[Loregrove.Domain]

    SQLITE[Loregrove.Infrastructure.Sqlite] --> APPLICATION
    FILES[Loregrove.Infrastructure.LocalFiles] --> APPLICATION
    SEARCH[Loregrove.Infrastructure.Search] --> APPLICATION
    AI[Loregrove.Infrastructure.AI] --> APPLICATION
    DOCLING[Loregrove.Infrastructure.Docling] --> APPLICATION
    PLATFORM[Loregrove.Infrastructure.Desktop] --> APPLICATION

    DESKTOP --> SQLITE
    DESKTOP --> FILES
    DESKTOP --> SEARCH
    DESKTOP --> AI
    DESKTOP --> DOCLING
    DESKTOP --> PLATFORM
```

## Projects

```text
src/
  Loregrove.Domain/
  Loregrove.Application/

  Loregrove.UI/
  Loregrove.Desktop/

  Loregrove.Infrastructure.Sqlite/
  Loregrove.Infrastructure.LocalFiles/
  Loregrove.Infrastructure.Search/
  Loregrove.Infrastructure.AI/
  Loregrove.Infrastructure.Docling/
  Loregrove.Infrastructure.Desktop/

tests/
  Loregrove.UnitTests/
  Loregrove.ContractTests/
  Loregrove.IntegrationTests/
  Loregrove.UITests/
  Loregrove.EndToEndTests/
```

Post-MVP Linux adds a separate head:

```text
src/
  Loregrove.Desktop.Linux/
```

Do not invent a `net10.0-linux` MAUI TFM. The Linux head remains a separate project referencing shared application/UI code.

## UI boundary

```mermaid
flowchart LR
    RAZOR["Shared Razor UI<br/>Fluent v5"] --> FACADE[ILoregroveClient / UI-facing facade]
    FACADE --> APP[Application services]

    APP --> DB[(SQLite)]
    APP --> FS[Local files]
    APP --> IDX[Search]
    APP --> AI[AI clients]
    APP --> DOC[Docling]

    WIN[Windows MAUI host] --> RAZOR
    MAC[Mac Catalyst host] --> RAZOR
    GTK[Linux GTK head later] -.-> RAZOR
```

Rules:

- Razor components do not use EF `DbContext`.
- Razor components do not open arbitrary files directly.
- Razor components do not start Docling.
- Razor components do not call provider SDKs directly.
- Platform-specific capabilities are injected through application/platform abstractions.
- Shared UI must not reference Windows, Mac Catalyst, GTK, WebView2, or WebKitGTK APIs.

## Runtime topology

```mermaid
flowchart LR
    subgraph Desktop["Loregrove desktop process"]
        HOST[MAUI host]
        WEBVIEW[BlazorWebView]
        UI[Fluent Razor UI]
        BG[Durable background processing]
        DB[(SQLite)]
        FS[Local object/artifact store]
        IDX[FTS + vector index]
        AICLIENT[AI provider clients]

        HOST --> WEBVIEW
        WEBVIEW --> UI
        UI --> BG
        BG --> DB
        BG --> FS
        BG --> IDX
        BG --> AICLIENT
    end

    AICLIENT -->|user-configured endpoint| PROVIDER[External or user-run AI provider]
    BG -->|start only when needed| DOC[Docling child process]
    DOC -->|normalized output| BG
```

There is no local ASP.NET server between the Blazor UI and application services.

## Platform topology

```mermaid
flowchart TB
    SHARED["Shared<br/>Domain + Application + Razor UI + Infrastructure"]

    WIN["Windows<br/>MAUI / WinUI 3<br/>WebView2"]
    MAC["macOS<br/>MAUI / Mac Catalyst<br/>WKWebView"]
    LINUX["Linux later<br/>MAUI GTK4 head<br/>WebKitGTK"]

    WIN --> SHARED
    MAC --> SHARED
    LINUX -. experimental .-> SHARED
```

## Local library layout

```text
MyLoregrove/
  library.db
  objects/
    aa/
      <sha256>
  artifacts/
    <document-id>/
      <version-id>/
        normalized.json
        content.md
        previews/
  indexes/
    vector/
  backups/
  logs/
```

Original files are immutable content-addressed objects.

## Dependency rules

- Domain references nothing.
- Application references Domain.
- UI references Application-facing contracts/view models, never Infrastructure.
- Infrastructure references Application and reaches Domain only through Application-owned boundaries.
- Desktop composes UI + Infrastructure.
- Domain/Application do not reference MAUI, Razor, Fluent UI, EF Core, Docling DTOs, provider SDKs, SQLite, or direct OS APIs.
- Linux GTK packages exist only in the later Linux head/integration project.

## Architectural invariants

1. Source bytes are preserved before parsing/enrichment.
2. Generated artifacts are rebuildable.
3. Canonical knowledge is mutated only through audited change sets.
4. AI extraction creates candidates, not canonical facts.
5. User resolutions are durable and reversible.
6. Provider outages do not prevent source capture.
7. Docling failure does not corrupt source or prior knowledge.
8. Windows/macOS share the same primary Razor UI.
9. Linux support may be removed/delayed without changing shared Domain/Application/UI.
