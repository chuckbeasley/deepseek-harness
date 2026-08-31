# Agent Note: .NET port Phase 5 wave 1 — the ui-* plugin set as RCL slot contributions

Status: implemented

## Problem

The shell's chat page rendered everything inline: the session list, the composer, and the
sidebar were hard-coded in `ChatPage.razor`, so no UI surface could contribute to the shell —
the ui-* plugin set deferral (port of the ~45 TS client packages as Razor Class Libraries
contributing through slots) had nothing to compose into.

## Decision

- Four ui-* RCLs ship, one per shell surface, each registering its components through the
  shared `SlotRegistry` and withdrawing them on dispose:
  - `Dsh.Ui.Sidebar` — brand row + New Session action into the `sidebar` slot;
  - `Dsh.Ui.Sessions` — the session list (selection highlight, running/queued state) into the
    `sessions` slot;
  - `Dsh.Ui.Chat` — the composer (the interactive `dsh-input-row` form) into the
    `chat.composer` slot;
  - `Dsh.Ui.Workspace` — the workspace list over the `workspaceRegistry` seam, live from its
    four registry events, into the `sidebar` slot.
- The chat page is slot-composed: it renders the slots and owns only the transcript and the
  loop turn. Selection travels through the scoped `ShellState` (the list and transcript
  always agree); gestures travel through the scoped `ShellBus` (`RequestNewSession` /
  `RequestSend`) so contributions never know the page's internals. The spine's webHost row
  creates the shared `SlotRegistry` (the ui rows register into the same instance the DI
  serves), and the dsh-web bundle mounts the four ui rows.
- The remaining TS ui-* packages await the surfaces they build — the port's shell has no
  settings/plan/goal/jobs/skill/subagent/tool/trajectory/approval pages yet. Each is named
  in the plan README as surface-gated rather than silently dropped.

## Consequence

The shell is composable: a UI package contributes by registering into a slot, and the
prerendered HTML proves the composed result (sidebar chrome, workspace list, session list,
composer, bilingual copy). 112 host suites green (1 new composition suite over a real
Kestrel host asserting every contribution and both locales), 41 console suites total green,
full solution 0 errors, and the headless-Chrome smoke drives the composed shell end to end —
the composer publishes through the bus, the page runs the mock turn, the reply renders, and
the zh locale survives the circuit attach.

## Alternatives considered

- Porting every TS ui-* package now: the port's shell has no surfaces for most of them
  (settings pages, plan/goal/jobs views, tool/trajectory cards, the conversation renderer);
  the deferral's completion is the slot-composition mechanism plus the shell-surface set,
  with the rest explicitly surface-gated.
- Props at the renderSlot site (the TS four-shares model): the port's `SlotRegistration`
  carries a fragment factory only; scoped services carry the shared state and gestures, which
  is the port's minimal equivalent until a props-bearing slot API has a consumer that needs it.
