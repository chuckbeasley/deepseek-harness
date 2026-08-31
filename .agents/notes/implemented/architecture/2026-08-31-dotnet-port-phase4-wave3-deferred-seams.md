# Agent Note: .NET port Phase 4 wave 3 — deferred capability seams land

Status: implemented

## Problem

The .NET 10 port (branch `port/dotnet10`) recorded Phase 4 wave 2 complete with a named wave-3 remainder: the sandbox + native landlock bridge, webhook ingress, out-of-process subagent drivers, the LSP process host + tool, and the PTY/ConPTY terminal backends. Closing Phase 4 meant landing those surfaces with the port's seam discipline: Service Definition + Provider + Consumer, zero-dependency console suites, spine mounting, and documented reductions.

## Decision

Each remaining seam ships as a faithful but bounded C# port, verified by process-backed console suites:

- `Dsh.Authorization` (the credentials authorization half of the credentials capability): one attempt per key, decline/cancel semantics, and a commit-confirmed success observed through a write-observing credentials facade (the `credentials/record-updated` event is not ported yet).
- `Dsh.Sandbox`: the confine contract (`Confine(argv, policy) -> ConfinedArgv?`) plus the Landlock sidecar backend. The sidecar's `--probe` and `--ro/--rw -- argv` contract is the native-bridge boundary: the managed side probes, wraps, and fails closed (`SANDBOX_UNAVAILABLE`) when no usable sidecar exists. The native `landlock-run` binary itself remains source-of-record in `native/` until the cutover phase.
- `Dsh.Webhook`: rule registry with contained dispatch and abort-and-drain teardown, the GitHub HMAC-SHA256 handler (exact status/message vocabulary), and a loopback `HttpListener` ingress. Session creation is a required composition hook (`IWebhookSessionAction`), deferred with the Phase-5 agent/workspace/preset spine; a rule whose request has no mounted action fails loud.
- `Dsh.Subagent`: a provider registry over named drivers, the in-process driver as a named provider, and `SdkOutOfProcessProvider` — one child runtime per delegation over newline-delimited JSON-RPC (initialize/session/prompt/shutdown), the assistant-output fold, the `sdkChildOutcome` reason mapping, and an idempotent dispose ladder. The child runtime server arrives with the SDK phase; a scripted fake child pins the wire contract, and the config carries an argv seam for it.
- `Dsh.Lsp`: streaming Content-Length decoder with the exact 65536-byte header cap, structural-guard protocol translation, the JSON-RPC connection over a private `Process` handle (the subprocess seam has no pipe modes yet — documented fallback), the serialized abortable server instance with the transient didOpen/didClose lifecycle, `$/cancelRequest` grace, and the shutdown/terminate ladder, plus the pure tool renderers. A fixture server pins 90 suites.
- `Dsh.Terminal`: the ConPTY backend (Windows) with the controlled-prompt readiness model (`stdin_read`/`inferred_idle`), the OSC 133 sanitizer, serialized resize, and the ClosePseudoConsole → tree-kill teardown ladder; suites self-skip on non-Windows.

## Consequence

The wave-2 "deferred to wave 3" list is cleared except for surfaces whose dependencies genuinely belong to later phases (LSP provider pooling + fs host helpers, SDK/ACP child servers, Unix pty, and the per-seam named surfaces such as sqlite backends, fs diff/edit tools, and the observation policy). Those stay named in the seam sources, and the `dotnet-ci.yml` lane now runs every console suite so the platform matrix owns the signal. Phase 4 is recorded COMPLETE; Phase 5 (Blazor) remains the next phase.

## Alternatives considered

- Extending `Dsh.Subprocess` with pipe stdin/stdout modes instead of the private process handle in Dsh.Lsp: the seam change is a later wave; the connection's spawner seam makes the swap mechanical.
- Porting the native landlock-run binary now: it is Linux-only and cannot build or run on the port's current hosts; the sidecar contract and fail-closed managed side are the honest deliverable.
- Implementing webhook session creation through the existing agent/workspace seams: the creation path needs presets, titles, and permission resolution that are not ported; the required-action hook keeps the seam honest without pretending parity.
