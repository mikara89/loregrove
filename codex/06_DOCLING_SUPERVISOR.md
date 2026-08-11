# Prompt 06 — On-Demand Docling Supervisor

Implement `IDoclingProcessManager` with states Stopped, Starting, Ready, Busy, Idle, Stopping, Faulted.

Requirements:
- lifecycle lock; concurrent EnsureReady starts one process;
- localhost only, UI off, local engine, configurable/dynamic port;
- readiness timeout;
- default 3-minute idle stop;
- graceful shutdown then bounded kill-tree fallback;
- bounded stdout/stderr diagnostics;
- restart once after process/start failure;
- fake-process CI harness;
- optional real Docling smoke test.

Acceptance: lifecycle is deterministic and process failure never crashes Loregrove.
