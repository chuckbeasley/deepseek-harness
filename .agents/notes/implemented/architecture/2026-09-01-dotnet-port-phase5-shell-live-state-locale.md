# Agent Note: .NET port Phase 5 wave 1 — the shell's live-state parity and locale-owned copy

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase5-shell-live-state-locale.zh.md)

## Problem

The Blazor chat shell rendered committed transcript events but no live agent state: a session mid-turn looked identical to an idle one, queued messages were invisible, and failures vanished. Its product copy was also hardcoded English, violating the "client UI copy is locale-owned" rule that the port spec carries over for the C# shell.

## Decision

- `WebSessionStore` now projects three live facts per session entry, all from events the ported   seams already emit: `Running` from `agent/status` (baselined from the AgentRegistry at store   construction), `Queued` from the three `agent/inbox/*` events (the live inbox counts), and   `Error` from `agent/error` (cleared when a new activity starts, so a stale failure never   outlives the next turn). Every notification stays post-commit, matching the store's existing   contract.
- `WebLocale` ports the copy rule minimally: one typed English dictionary resolved through   `T(key)` (a missing key renders as itself), registered in DI and injected into the chat page.   The locale-selection machinery and further dictionaries stay deferred, named in the class   comment. The chat page renders the running/queued state in the session list and an error   banner in the transcript.

## Consequences

The shell now shows live parity with the seams (running/queued/error), the host suite grew from 76 to 79 (three store suites over real mock-LLM turns, including the mock's two-phase todo-then-text fixture with the todo row mounted exactly like the profile), all green, with the full solution building at 0 errors. The pre-rendered shell smoke drives one real mock turn over the wire (session/create + session/prompt) and asserts the locale copy, the session row, and the user/assistant transcript text in the served HTML.

## Alternatives considered

- Deriving running/queued from session events alone: the session log carries no inbox or status   facts, so the agent events are the authoritative sources (the same selection the session/control   stream uses).
- A full locale system with selection and multiple dictionaries: deferred by the port spec; the   typed single dictionary keeps the copy rule without pretending the selection machinery exists.
