# 1. Objective and scope

## Objective

Convert this repository from a TypeScript monorepo into a **C# / .NET 10** codebase:

- **CLI** → a `hsh` dotnet tool / single-file executable with a **TUI** (Spectre.Console + Terminal.Gui).
- **Web UI** → **Blazor** (Blazor Server / Blazor Web App, server interactivity), replacing the React client + Node host.
- **Python SDK** → retired and replaced by a **.NET client SDK** (no Python remains).
- **Docs website** → folded into the Blazor app (content preserved).
- **Native `landlock-run` sandbox** → stays native (C/C++/Rust), surfaced via P/Invoke or sidecar; not rewritten in managed code.

## Strategy

**Proof-of-concept spike first, then incremental strangler.** Port the Cordis core plus one vertical slice to validate the framework mapping; then migrate capability groups group-by-group while the TypeScript tree stays runnable until parity; then retire the TypeScript tree.

## In scope

- The vendored Cordis framework (`vendor/`) and every harness package under `packages/` (~90 packages across ~40 groups).
- The CLI (`apps/cli`) and the Web GUI (`apps/web`, the `client/` and `host/` package groups).
- The TypeScript and Python SDKs (both replaced by a .NET client SDK over the same stdio JSON-RPC wire).
- The build, test, lint, and CI toolchain.

## Out of scope (or deliberately unchanged)

- The native `landlock-run` sandbox source — retained and bound via P/Invoke or a sidecar process.
- Docs *content* — preserved; the VitePress *build* is folded into the Blazor app.

## Current state

| Axis | Today |
|---|---|
| Scale | ~3,055 `.ts` + 299 `.tsx` + ~2,000 config/fixture files; ~90 npm packages across ~40 groups |
| Language | TypeScript, ESM, `strict`, 100%-coverage CI gate |
| Framework | Vendored **Cordis** (Context / Service / typed Events / Fiber effects / Loader / Include / HMR / Schemastery / Cosmokit) |
| Type/RPC | **Typert** build-time type-graph generator + runtime registry + Remote-method RPC |
| CLI | `apps/cli` — `hsh` profile boot, plugin mgmt, `--dump-config`, web alias |
| Web | Node host half (HTTP + Typert gateway) + React client half (~45 `ui-*` plugins, slot system, observable stores, SSE/WS) |
| SDKs | TypeScript SDK + Python SDK, both over stdio JSON-RPC |
| Persistence | SQLite + JSONL session log, monotonic schema/format versions |
| Sandbox | bwrap/Landlock/Seatbelt backends + a native `landlock-run` Node addon |
| Tests | vitest, jsdom, keyless recorded-session snapshots, real-API e2e |
