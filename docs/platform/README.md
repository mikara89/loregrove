# Desktop platform targets

Windows and macOS are first-class MVP targets and share the same production Razor UI.

```mermaid
flowchart TB
    SHARED["Domain + Application + shared Razor UI"] --> WIN["Windows<br/>MAUI + WinUI 3 + WebView2"]
    SHARED --> MAC["macOS<br/>MAUI + Mac Catalyst + WKWebView"]
    SHARED -. post-MVP .-> LINUX["Linux experiment<br/>separate future head"]
```

- [Windows validation](windows-validation.md)
- [macOS validation](macos-validation.md)

There is no Linux head in the production repository bootstrap.
