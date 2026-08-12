# ADR-011: Managed Docling Processing Pack

Status: Accepted

## Context

Complex document conversion requires a large Python/native dependency set. Requiring users to
install Python, create a virtual environment, or reconcile Docling versions would make results and
support nondeterministic. Placing that runtime in the user's Loregrove library would mix replaceable
application machinery with backed-up evidence. A permanently listening or LAN-bound service would
also violate the local-first, least-exposure design.

Loregrove's existing AI boundary is different: chat and embedding runtimes are user-managed
providers, and Loregrove must never install, download, start, stop, or update those processes.

## Decision

Loregrove treats Docling as an optional document-processing runtime distributed separately as a
versioned Processing Pack. In `ManagedLocal` mode, Loregrove owns exactly one loopback-only Docling
Serve child process, starts it on demand, reuses it for sequential conversions, and stops it after an
idle timeout. This lifecycle management applies only to Docling and does not extend to chat or
embedding model runtimes.

The Processing Pack contains a private Python/runtime installation, pinned Docling and
docling-serve, required native files and redistributable assets, a stable pack launcher, and a strict
schema-versioned manifest. It lives under application runtime data, never under the user library.
Normal application startup validates but never installs, repairs, downloads, or updates it.

The pack launcher binds only to `127.0.0.1`, disables interactive UI and arbitrary remote fetching,
uses a dynamic port by default, and exposes private readiness and graceful-shutdown controls. One
manager-owned startup task coalesces demand, one exclusive lease gates conversion, and a three-minute
idle timer bounds warm reuse. Startup retries exactly once. Shutdown first requests graceful exit,
then uses bounded owned-process-tree termination as recovery.

`Disabled`, `OneShot`, and `Remote` never start this reusable local process. Their later conversion
behavior remains outside this decision.

## Consequences

Positive:

- users do not manage Python;
- runtime and conversion versions are deterministic;
- process ownership and lifecycle are controlled and testable;
- processing remains local-first and loopback-only;
- warm-process reuse avoids one launch per document;
- bounded diagnostics isolate Docling failures;
- remote-mode compatibility remains possible without weakening local ownership.

Tradeoffs:

- each supported platform requires a separately built Processing Pack;
- release/download size increases;
- lifecycle, port, readiness, cancellation, and process-exit races require dedicated tests;
- process diagnostics need explicit privacy and memory limits;
- Loregrove/pack/Docling compatibility requires version management;
- later release packaging must address signing, notarization, quarantine, and runtime asset licenses.

This ADR does not approve document conversion, pack auto-download/update, installer integration,
remote conversion, OneShot conversion, or management of AI runtimes.
