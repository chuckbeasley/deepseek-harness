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
