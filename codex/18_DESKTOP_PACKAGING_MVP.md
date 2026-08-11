# Prompt 18 — Windows + macOS Packaging and MVP Release

Read:
- `docs/07_RELEASE_DECISIONS.md`
- `docs/06_ROADMAP_REUSE.md`
- `docs/10_PLATFORM_UI.md`

Prepare the Windows and macOS MVP release.

## Windows

1. Produce self-contained Windows desktop package.
2. Handle required WebView2 runtime packaging/prerequisite according to current Microsoft guidance.
3. Create signed-installation-ready packaging.
4. If signing credentials are unavailable, make signing an explicit release input.
5. Verify first launch without admin rights.
6. Verify local library creation and Docling-pack detection.

## macOS

1. Produce Mac Catalyst release package.
2. Add signing/notarization pipeline structure.
3. Use Keychain-compatible secure storage path.
4. Verify app sandbox/entitlement decisions are minimal and documented.
5. Validate file/folder import and local-library access.
6. If signing/notarization credentials are unavailable, make them explicit release inputs.

## Shared

1. About/version screen.
2. changelog.
3. migrations + backup behavior.
4. release GitHub Actions workflow.
5. full test suite.
6. Windows packaging smoke.
7. macOS packaging smoke.
8. MVP release checklist.
9. known limitations.
10. Docling Processing Pack remains optional/separate.

## Acceptance

A clean supported Windows machine can install and run Loregrove without Docker, PostgreSQL, or a separately installed .NET runtime.

A supported Mac can install and run the Mac Catalyst build through the documented signing/notarization path.

The same shared Razor/Fluent UI is used on both.
