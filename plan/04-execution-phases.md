# 4. Execution phases

## Phase 0 — Proof-of-concept spike

Port `Cordis.Core` (Context/Events/Fiber/Registry), a minimal Schemastery, plus one vertical slice: `session` (append-only log) + `llm` (mock provider) + one tool, booted headless with reversible effects and a waterfall listener asserted.

- **Deliverable:** `Cordis.Core` + the slice compile and a headless run completes against a mock LLM; effects unwind on dispose; a waterfall short-circuits correctly.
- **Exit:** go/no-go on Cordis port fidelity. If transactional-loader/effect semantics cannot be reproduced, stop and reassess.

## Phase 1 — Foundations

Full `Cordis.*` (Loader, Include + patch layers, Group, Timer, Hmr, Logger), the config-expression decision (§3.4) implemented, and the build/test pipeline: xUnit, bUnit, Playwright, Verify, analyzers, coverage gate, CI matrix, NuGet packaging.

- **Deliverable:** a config-composed tree boots from `cordis.yml`, applies patches, and reloads on change; test/CI gates run.

## Phase 2 — Core spine

`session`, `system-prompt`, `tools`, `agent`, `agent-loop`, `scope`, `llm` seam + DeepSeek provider, persistence (SQLite/JSONL), projection seam, telemetry/titles.

- **Deliverable:** `headless` profile parity — a one-shot task runs end-to-end against the real or replay LLM; the session log replays.

## Phase 3 — CLI + TUI

`hsh` tool (args, profile boot, `--dump-config`, plugin management) + the TUI: a full-screen interactive session view (chat, tool-call disclosure, approval prompts, goal/plan/jobs panels) using Terminal.Gui with Spectre.Console for prompts/tables.

- **Deliverable:** command parity with today's `hsh` plus a working interactive TUI.

## Phase 4 — Capability seams (parallelizable, group-by-group)

Migrate each group: fs, subprocess, shell, terminal, sandbox (+native bridge), lsp, web, compaction, context, subagent, jobs, workflow, webhook, skill, todo, plan, preset, guard, hooks, session-query, settings, credentials, storage, workspace, attachment, spill, goal, schedule, feedback, identity.

- **Deliverable:** each seam ships Service Definition + Provider + Consumer with unit/snapshot/e2e coverage before the next group starts. Groups proceed in parallel once Phase 2 lands.

## Phase 5 — Web (Blazor)

ASP.NET Core host + SignalR + Typert source-gen/RPC gateway; Blazor slot system + store layer; port the ~45 `ui-*` plugins to Razor Class Libraries; localization, theme, docs-site folding.

- **Deliverable:** `hsh web` serves the Blazor app with feature parity to the current GUI; browser snapshots/e2e pass.

## Phase 6 — SDK + ACP + hooks

.NET client SDK (NuGet) replacing the Python SDK; ACP server; Claude Code/Codex hook bridges; JSON-RPC protocol port; retire `python/`.

- **Deliverable:** SDK and ACP parity; `python/` deleted.

## Phase 7 — Parity, cutover, retire TS

Run snapshot/e2e parity against the TS tree; fix drift; delete `packages/`, `apps/`, `vendor/`, pnpm/pnpm-lock, and TS build tooling; final docs/Agent-Note updates.

- **Deliverable:** single .NET codebase; TS fully removed.

## Phase 8 — Hardening and distribution

OpenTelemetry observability, performance, NativeAOT single-file for CLI/runtime, NuGet publishing, container images, release pipeline parity (`hsh` + SDK + ACP).

- **Deliverable:** production-grade packaging and release.

## Effort estimate

This is a **multi-person-year** effort (comparable to rebuilding the harness from its architecture).

| Phase | Size |
|---|---|
| 0 — spike | S–M |
| 1 — Cordis + tooling | L |
| 2 — core spine | L |
| 3 — CLI/TUI | M |
| 4 — capability seams | XL (the bulk — ~30 groups, parallelizable) |
| 5 — Blazor | XL (the other bulk — ~45 UI plugins) |
| 6 — SDK/ACP/hooks | M |
| 7 — cutover | M |
| 8 — hardening/distribution | M |

Recommended staffing: a small team with one lead owning the Cordis port, parallel work on capability seams and Blazor once Phases 0–2 are green.
