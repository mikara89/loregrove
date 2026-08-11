# Loregrove desktop platform spike

This isolated spike validates a .NET 10 MAUI Blazor Hybrid host with one shared Fluent UI Blazor v5 Razor Class Library. It intentionally contains no Loregrove product domain, persistence, search, parsing, or AI implementation.

## Projects

```text
src/
  Loregrove.PlatformSpike.UI/       shared Razor/Fluent/Markdown/graph UI
  Loregrove.PlatformSpike.Desktop/ MAUI composition and OS adapters
  Loregrove.PlatformSpike.Services/ in-process contracts and deterministic demo data
tests/
  Loregrove.PlatformSpike.Tests/
```

There is no ASP.NET host, HTTP API, REST client, or localhost server.

## Build

```powershell
dotnet workload install maui
dotnet test tests/Loregrove.PlatformSpike.Tests/Loregrove.PlatformSpike.Tests.csproj -c Release
dotnet build src/Loregrove.PlatformSpike.Desktop/Loregrove.PlatformSpike.Desktop.csproj -f net10.0-windows10.0.19041.0 -c Release
dotnet build src/Loregrove.PlatformSpike.Desktop/Loregrove.PlatformSpike.Desktop.csproj -f net10.0-maccatalyst -c Release -p:EnableCodeSigning=false
```

From Windows, publish an unpackaged self-contained build with:

```powershell
dotnet publish src/Loregrove.PlatformSpike.Desktop/Loregrove.PlatformSpike.Desktop.csproj -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win10-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```

Fluent UI v5 RC4 requires its Hybrid initializer loader before `blazor.webview.js`; the host `index.html` includes that package-provided workaround. Cytoscape.js is checked in as a pinned browser asset and does not introduce Node.js as an application runtime.
