# Agent Note: .NET port Phase 6 wave 3 — the SDK client and runtime profile

Status: implemented

## Problem

The runtime server could be driven over a transport, but the port had no client: the
TypeScript SDK client (spawn the runtime subprocess, speak the stdio protocol, fan
notifications into subscriptions, tear the child down to quiescence) and the high-level
run API had no .NET counterpart, and no shipped profile booted the SDK server over a
process's stdio.

## Decision

- `src/Dsh/Dsh.Sdk.Client` ports `packages/sdk/client`: `HarnessClient` spawns the runtime
  as a subprocess and owns it — the launch resolution (`SdkLaunch.ResolveLaunch`, the port
  of `resolveDshLaunch`: profile, ordered patches, home, process cwd, and env overrides with
  `DSH_HOME`; a `.dll` entry spawns via `dotnet`; the default entry is the current
  executable, other hosts name `DshBin` explicitly), the typed `initialize`/`session/prompt`
  surface with wire-identity and message-id validation (`SdkProtocolError`), per-request
  timeouts as abandonments (`RequestTimeoutError`; the transport drops the pending entry
  while the server-side work runs to close), notification subscriptions with the
  filter/queue/waiter/failure semantics, the session-tree scope derived from
  `subagent.started` lineage edges, and the close ladder — a best-effort `shutdown` bounded
  by `shutdownTimeoutMs`, then stdin EOF with the EOF grace, then the forced tree kill
  (Windows has no graceful signal, so the ladder skips SIGTERM exactly like the TS does on
  win32). The high-level `DeepSeekHarness` (memoized handshake with retry after a proved
  cleanup, `session()` handles, `RunAsync` returning the owned `RunResult` interval) and the
  wire helpers (`NormalizeInput`, `ValidatedSessionEvent`, `FinalResponse`) complete the
  surface.
- The server now projects every session record to the SDK wire envelope (`{type, seq,
  timeMs, data}` — the TS `SessionEvent` shape both SDK clients and the subagent output
  fold read). The ported session records keep their payload inline, so the projection
  strips the envelope fields and wraps the rest under `data`; the wire-boundary probes in
  the client validate only the variants they read (`assistant/message` content,
  `turn/end` reasons) and pass plugin event types unknown to the client process through
  under their envelope shape.
- The `sdk` runtime profile ships as the `dsh-sdk` bundle (`sdkRuntime` spine row booting
  `SdkJsonRpcServer` over console stdio, exiting when the client closes stdin — the
  process's exit is the client's ladder rung). The profile template registers under
  `ProfileTemplates`, and the subagent seam's `sdkSubagent` row already names `sdk` as its
  default child profile, so it now runs the real runtime.
- The client's enqueue receipt is the `user/message` session event carrying the queued
  message id: the port's inbox seam logs no `agent/inbox/spliced` event (documented
  deviation), and the durable splice is the user message itself.

## Consequence

A .NET consumer can run agent turns against a real harness runtime end to end: 11 client
suites prove the launch resolution, the wire semantics, and the real-process round trip
(handshake identity, unknown-method errors, the turn streaming to idle with session-tree
scoping, the timeout abandonment and close ladder, and the `DeepSeekHarness` run interval
with the plugin event pass-through). 54 console suites total green; full solution builds
at 0 errors. The ACP server, the hook bridges, and the python/ retirement follow in later
Phase-6 waves.

## Alternatives considered

- Reusing the subagent seam's `SdkChildConnection`: that driver is internal to the
  provider with its own failure-fact wrapping; the client SDK is the public surface with
  subscriptions, session-tree scoping, and the high-level run API, so the port keeps them
  separate.
- Emitting the ported session records inline on the wire: the SDK wire contract is the TS
  envelope, and both SDK clients plus the subagent fold read `data`; the server projects
  instead of changing the contract.
