# Agent Note: .NET port Phase 6 wave 5 — the Claude Code and Codex hook bridges

Status: implemented

## Problem

The port's hooks seam had the codec, matcher, and event types, but no bridges: the Claude
Code and Codex command-hook bridges (config parsing, hook execution, restrictive merging,
detached-run quiescence, durable invoked/result pairs, and the extension-point mappings)
had no .NET counterparts, and the shell seam had no environment slot for the dialect
environment.

## Decision

- `src/Dsh/Dsh.Hooks` gains the shared protocol pieces the bridges run on: `HookRunner`
  (command hooks through the shell seam with the per-hook timeout override, the
  bridge-owned default timeout, the trusted stdin payload and dialect environment, the
  trailing-newline framing difference, and the expected-event-name scoping; infrastructure
  rejection becomes a no-exit-code outcome so a hook never crashes the turn), `HookMerge`
  (deny > ask > allow precedence, sticky first `continue:false`, reasons for the winning
  rank, ordered context/system-message accumulation), `DetachedRuns` (the emit-shaped
  SessionStart runs are tracked, aborted on disposal, and drained), and `HookLog` (the
  turn-enclosed invoked/result pair with the 500-char stderr summary cap).
- The Claude Code bridge parses the seven-event matcher-group format with
  `${CLAUDE_PLUGIN_ROOT}`/`${CLAUDE_PROJECT_DIR}` substitution and skipped non-command
  hooks, listens on `agent/session-start`, `agent/pre-step`, `tools/pre-execute`,
  `tools/post-execute`, and `agent/turn-stopping`, builds the CC payloads with
  `CLAUDE_PROJECT_DIR` exported, and runs matchers in the literal-alternation mode. The
  Codex bridge parses the five-event subset with `async`/non-command skips and the
  `timeout`/`timeoutSec` alias, owns the snake_case payloads (model, permission_mode,
  turn_id, `{ command }` tool_input), regex-only matchers, no trailing newline, and the
  clean-plain-stdout-as-context rule.
- The shell request/spec gained the extra-env slot the dialect environment flows through
  (request → spec → subprocess spawn, merged after the ambient scrub). The spine mounts
  `hooksClaudeCode` and `hooksCodex` rows whose `configPath` is deployment-owned (no
  shipped profile bundle).
- Documented reductions, each named: the ported session header carries no workspace cwd
  (payload cwd and hook workdir fall back to the process cwd), SubagentStart/SubagentStop
  parse but never fire (the port's subagent seam has no start/end lifecycle events), a
  hook `ask` maps to a deny (the port's pre-tool decisions have no ask seat), the post-tool
  `additionalContext` injects into the next step (the tool decisions carry no
  additional-context slots), and a blocking Stop hook force-continues unbounded (the TS's
  loop-guard TODO).

## Consequence

Unmodified Claude Code and Codex hooks run on the harness's interception points: 23 hook
suites prove the merge precedence, both config parsers, the real-process runner (payload
framing, blocking exits, contained infrastructure failures), and both bridges end to end
over a real loop (payload capture, deny blocking, context injection, the durable
invoked/result pairs, and the Codex plain-stdout rule). 45 console suites total green; full
solution builds at 0 errors. The python/ retirement is the last Phase-6 wave.

## Alternatives considered

- Per-bridge copy of the run/merge/append logic: the shared protocol pieces are identical
  across dialects; they live once in the seam, with the payloads and decision mappings
  owned by each bridge.
- Honoring the post-tool context through the tool decisions: the port's PostToolDecision
  records have no additional-context slots; the next-step inject keeps the context
  model-visible with the documented deviation.
