# ADR-007: Native file-drop adapters

Status: Accepted

## Context

Prompt 00 established that WebView HTML drag/drop supplies browser metadata but is insufficient for
reliable native document import. Mac Catalyst also requires careful treatment of sandboxed,
security-scoped access.

## Decision

Native operating-system drops are captured by a platform adapter. The adapter owns native access and
passes opaque `PickedFile` handles into shared code through an Application contract.

```mermaid
flowchart LR
    OS[Operating-system drop] --> ADAPTER[Host drop adapter]
    ADAPTER --> PICKED[Opaque PickedFile handle]
    PICKED --> UI[Shared UI]
    UI --> IMPORT[Application import workflow]
```

DOM drag events may support visual feedback, but they are not the authoritative source of importable
file access.

## Consequences

- Windows and Mac Catalyst implement native drop behavior independently.
- Shared UI and Application remain free of WinUI, WebView2, Mac Catalyst, and WKWebView APIs.
- Native handles can preserve platform-specific lifetime and security requirements.
- Prompt 01 keeps only the contract and a no-op placeholder; runtime implementation is deferred.
