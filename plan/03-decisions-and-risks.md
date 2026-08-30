# 3. Key decisions and risks

## Key design decisions

### 3.1 Cordis port (highest risk — do first)

Microsoft DI cannot express Cordis's load-bearing semantics: **lazy service dependency** (`inject` waits for a provider), **reversible effects**, **typed events with five dispatch modes**, and **transactional loader reconciliation**. The plan is a **faithful port of `Cordis.Core`** rather than forcing MS DI to do everything. The spike (Phase 0) must prove:

- `Context`/`Service`/`Fiber` with correct load/unload/reentrancy semantics (the vendored source carries 19 logged local modifications and subtle transactional-loader behavior that must be reproduced, not simplified away).
- Waterfall short-circuit and `next()`-return propagation.
- `emit`/`waterfall`/`parallel`/`serial`/`bail` dispatch fidelity.

### 3.2 Plugin loading model

Today `cordis.yml` can mount arbitrary JS/TS files, and custom profiles live-reload them. In .NET, out-of-tree plugins are **compiled assemblies loaded via `AssemblyLoadContext`** (unloadable for reload). This is the biggest UX change: "config = arbitrary code" becomes "config = declared assemblies + declarative YAML". Shipped plugins remain first-party; the plan documents a `dsh plugin` packaging model (NuGet or a plugin dir of DLLs) early.

### 3.3 HMR / live reload

`.NET Hot Reload` covers method-body edits, not structural plugin-tree changes. Plan: **config-watch + full fiber reload** (dispose + re-compose the affected subtree) for YAML/row changes, and .NET Hot Reload for in-process method edits. Full parity with the current TS `hmr` watcher is a known partial gap to state up front.

### 3.4 `!!js` config expressions

Runtime JS evaluation has no clean managed equivalent. Options, decided in Phase 1:

- (a) a **Roslyn scripting** evaluator (most flexible, heavier, compiles at load), or
- (b) a **restricted declarative expression language** (subset: env lookups, `if/else`, string/list ops) — safer and sufficient for shipped usage.

Recommendation: (b) with (a) as an opt-in escape hatch.

### 3.5 Session event model

Durable discriminated-union events (`SessionEventMap`) map to a sealed hierarchy with `[JsonPolymorphic]`/`[JsonDerivedType]`. The "model-visible ⟺ logged" invariant and the projection seam (`stateOf`, `snapshot`) port directly to a reactive projection engine over the append-only log.

### 3.6 Blazor slot system

The React slot model maps cleanly: each plugin registers a named slot → a Razor component via DI; a `<SlotOutlet Name="..."/>` renders children with the four props shares re-expressed as typed component parameters + an injected store. Live data flows over **SignalR** (replacing SSE/WebSocket); stores use `IAsyncEnumerable`/change-notification. Server interactivity preserves the server-authoritative plugin/DI model.

### 3.7 SDK wire protocol

The stdio JSON-RPC protocol is the stable SDK boundary. The .NET client SDK (replacing Python) keeps the **same JSON-RPC wire**, so recorded fixtures and ACP/TS-SDK consumers migrate without a protocol redesign. A native AOT single-file runtime replaces the `sdk-runtime` wheel.

### 3.8 Native sandbox

`landlock-run` stays native. Bind via P/Invoke on Linux and a sidecar process protocol where spawning is cleaner; Windows sandboxing (ConPTY/Job objects/pwsh sandbox) remains managed.

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Cordis port fidelity (loader transactions, effect reentrancy, 19 local mods) | Critical | Phase 0 spike proves it first; port test-for-test from vendored source |
| `!!js` config eval has no clean C# equivalent | High | Decide in Phase 1; restricted expression language + Roslyn escape hatch |
| HMR parity limits | Medium | Config-watch fiber reload + .NET Hot Reload; document the gap |
| Out-of-tree plugin model changes (JS files → DLLs) | Medium | AssemblyLoadContext + explicit packaging model documented in Phase 1 |
| PTY/terminal cross-platform fidelity | Medium | ConPTY + pty providers; port the existing Win32 notes |
| Native sandbox binding | Medium | P/Invoke + sidecar; keep native untouched |
| Blazor Server stateful-circuit scalability | Medium | Accept for v1 (matches server-authoritative model); revisit WASM interactivity only if demanded |
| Test-infra parity (100% coverage, snapshots, e2e) | Medium | Verify/bUnit/Playwright mapped up front in Phase 1 |
| Sheer volume (~90 packages) | High | Strangler sequencing; capability groups parallelized after Phase 2 |

## Remaining open decisions

Confirm in Phase 0–1:

1. TUI framework split — Terminal.Gui vs. a Spectre.Console.Live-only UI.
2. Config expression language for `!!js` (§3.4).
3. Store/observable library (raw `INotifyPropertyChanged` vs. a thin `Store<T>` port).
4. Out-of-tree plugin packaging (NuGet vs. plugin-directory DLLs).
5. SDK JSON-RPC (stdio) vs. an additional SignalR transport for the .NET client.
