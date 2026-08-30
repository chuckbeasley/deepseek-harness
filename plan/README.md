# DeepSeek Harness → C# / .NET 10 Conversion Plan

This folder holds the plan to convert the repository from a TypeScript monorepo into a **C# / .NET 10** codebase: a **TUI** for the CLI and **Blazor** for the Web UI.

## Locked decisions

These were confirmed with the requester before the plan was written:

- **Web UI model:** Blazor Server / Blazor Web App with server interactivity.
- **Scope:** full .NET port — the Python SDK is rewritten (no Python remains), the docs website is folded into the Blazor app, and the native `landlock-run` sandbox stays native (P/Invoke or sidecar).
- **Sequencing:** proof-of-concept spike first, then an incremental strangler migration, retiring the TypeScript tree only after parity.

## Contents

1. [Objective and scope](01-objective-and-scope.md) — what the conversion targets, what is in and out of scope, and a snapshot of the current state.
2. [Target architecture](02-target-architecture.md) — the .NET solution layout and the technology mapping table.
3. [Key decisions and risks](03-decisions-and-risks.md) — the hard problems (Cordis port, plugin loading, config expressions, Blazor slots) plus the risk register and remaining open decisions.
4. [Execution phases](04-execution-phases.md) — Phases 0–8 with deliverables, exit criteria, and an effort estimate.

## How to read

Read `01` for scope, `02` for the target shape, `03` for the decisions that carry the most risk, and `04` for the order of work. Phase 0 (the Cordis-core + vertical-slice spike) is the go/no-go gate for the whole effort; everything else is sequenced off its outcome.

## Status

**Phase 0 (spike): COMPLETE — 2026-08-30** — integrated on branch `port/dotnet10` (merges `c4b3adc41b`; solution commit included):

- `src/Cordis/Cordis.Core` — Cordis port (Context/Service/Events with all five dispatch modes, Fiber effects) — 37 xUnit tests.
- `src/Cordis/Cordis.Cosmokit` + `src/Cordis/Cordis.Schemastery` — utility + schema-validation ports — 37 console tests.
- `src/Dsh/Dsh.Llm`, `Dsh.Session`, `Dsh.Tools`, `Dsh.Spike` — the vertical slice — 28 console tests, including the byte-pinned 22-event headless smoke.
- Solution `deepseek-harness.slnx`: 10 projects, builds with 0 warnings / 0 errors on .NET 10; the smoke exe prints the pinned 22-event log and exits `== PASS ==`.

Go/no-go verdict: **GO** — the Cordis semantics (waterfall short-circuit, next()-return propagation, reverse-order effect unwind, dispatch modes) port faithfully to C#. Phase 1 (Loader / Include / config expressions) can proceed.

**Phase 1 (Foundations): COMPLETE — 2026-08-30** — integrated on branch `port/dotnet10` (head `ea2b8ce485`):

- `src/Cordis/Cordis.Plugin.Loader` — row/tree model, dependency-gated activation, transactional reconciliation — 10 tests.
- `src/Cordis/Cordis.Plugin.Include` — file-backed entry lists, patch layers with insert-then-patch semantics, the restricted `!!js` expression language (literals, `$env` lookups, if/else, and/or/not, concat, lists, indexing; loud failure otherwise; Roslyn scripting deferred), and a minimal YAML-subset parser — 23 tests.
- `src/Cordis/Cordis.Plugin.Timer` + `src/Cordis/Cordis.Logger.Console` — 28 tests.
- `src/Cordis/Cordis.Plugin.Hmr` — exact-config FileSystemWatcher with coalescing, serialized refreshes, contained failures — 5 tests.
- Pipeline: `Directory.Build.props` (analyzers), C# `.editorconfig`, `.github/workflows/dotnet-ci.yml`, `PIPELINE-README.md`.
- Solution `deepseek-harness.slnx`: 19 projects; full build 0 errors; all seven suites green (168 tests, 0 failures); the Phase 0 headless smoke still exits `== PASS ==`. The cordis.yml-composed boot, patch layers, and reload are covered by the Include and Hmr suites against the real Loader.

Go/no-go verdict for Phase 2 (core spine: session, system-prompt, tools, agent, agent-loop): **GO**.

**Phase 2 (Core spine): COMPLETE — 2026-08-30** — integrated on branch `port/dotnet10` (head `1abcae841b`):

- `src/Dsh/Dsh.Agent` + `src/Dsh/Dsh.Scope` — live agent registry/inbox/status, agent-scoped registrations — 23 tests.
- `src/Dsh/Dsh.Session.Persistence` (JSONL, sync/batched flush), `src/Dsh/Dsh.Session.Projection`, `src/Dsh/Dsh.Session.Titles` — 16 tests, pinned-log replay included.
- `src/Dsh/Dsh.SystemPrompt` — ordered sections, tool-schema providers, tool ordering — 22 tests.
- `src/Dsh/Dsh.Llm.DeepSeek` — real provider: request builder, SSE translation, error mapping; additive `LlmError(message, code, status)` — 49 tests against a fake handler (no network).
- `src/Dsh/Dsh.AgentLoop` — the real turn driver: `turn/start` → inbox claim → `agent/pre-step` (rejectable waterfall) → `step/start` → durable history derivation → `agent/request` waterfall → stream chunks/assistant message → tool-call scheduler → `step/end` → loop → `agent/turn-stopping` → `turn/end`; create/resume factory on the agent registry, request-reconstruction invariant on `llm/stream`, runtime-context snapshot projection, and the session event-type registry so plugin-merged events round-trip the JSONL — 5 console suites (full mock turn with persistence replay, pre-step reject, cancellation, resume + invariant, runtime context).
- Solution `deepseek-harness.slnx`: 23 projects; full build 0 errors; all twelve suites green (283 tests, 0 failures). The `Dsh.Spike` exe now runs both smokes: the pinned Phase 0 scenario (`== PASS ==`) and the Phase 2 gate (`== PHASE2 PASS ==`) — one headless task through the real agent loop against the mock provider (tool call, then text) with the 22-event JSONL log replaying identically.

Go/no-go verdict for Phase 3 (CLI/TUI, boot, and launcher): **GO**.

**Phase 3 (CLI + TUI): COMPLETE — 2026-08-30** — integrated on branch `port/dotnet10` (head `75c1e07a3b` + integration):

- `src/Dsh/Dsh.Cli` — the `dsh` launcher: hand-rolled commander port (launcher-flags-first parsing, verbatim inner-argument pass-through, `web` alias, `plugin` subcommand, `-V`, exact TS error strings/exit codes); profile boot composing `cordis.yml` patch layers through the Cordis Loader/Include ports (in-box `@deepseek-ai/dsh-base`/`dsh-headless`/`dsh-tui` bundles, `profile.json` manifest — documented deviation from package.json, `$DSH_HOME/cordis.patch.yml` home layer, `--patch` overlays); boot-free `--dump-config` with per-layer comments; `dsh plugin add/remove/list` manifest editing (documented deviation from pnpm forwarding until NuGet packaging); the spine row registry (sessions/llm/tools/systemPrompt/agents/agentLoop/sessionPersistence/deepseek/mock/todo/headless/tui; unknown rows fail loud with the row id) — 17 tests.
- `src/Dsh/Dsh.Tui` — Terminal.Gui 1.17.1 interactive UI: live transcript pane, input line driving the real agent loop, todo panel from `todo/write`, tool-call disclosure, and approval dialogs on the `tools/pre-execute` waterfall; fake-driver headless smoke — 4 tests.
- Additive upstream fixes: `YamlSubset` now honors the documented top-level empty flow collections and the `insert:`/nested-block key form.
- Solution `deepseek-harness.slnx`: 27 projects; full build 0 errors; all fourteen suites green (304 tests, 0 failures). Verified smokes on the merged tree: `dsh --profile headless "task"` runs a real loop turn and prints the assistant text; `dsh --profile headless --dump-config` prints the composed layers; `dsh plugin --profile headless list` prints the template bundles; `dsh --profile tui --smoke` boots the interactive UI and exits 0.

Go/no-go verdict for Phase 4 (capability seams): **GO**.
