# Agent Note: .NET port Phase 5 wave 1 — the session control stream and the stream-cancel contract

Status: implemented

## Problem

The session catalog's last live wire surface, `session/control` (one baseline then queue/jobs/projection deltas), was deferred because its data sources looked absent: the C# session vocabulary carries no durable `agent/inbox/spliced` event, and the jobs seam appeared event-less. Cancellation of a registry stream also violated the TS cancel contract: the mux answered a cancelled logical stream with a `gateway/internal` error frame ("A task was canceled") instead of ending quietly.

## Decision

`SessionControlRemotes.Control` ports the feed over surfaces that already existed:

- **Baseline**: per-session queues read from the live inbox (`Agent.Inbox.NextTurn` then `NextStep`; every item placed `queued` — the steering/context placements need the TS splice projection and are deferred), per-session jobs from `IJobsService.List` filtered to the owner session (unowned jobs have no wire seat), and the consistent projection cut from `SessionProjectionRegistry.Snapshot` (the registry now mounts as a `sessionProjections` spine row in dsh-base).
- **Deltas**: a queue frame per `agent/inbox/inserted|claimed|discarded` event — the Agent itself already emits these through `IInboxNotifications` — each frame carrying the full current items (idempotent; the port's inbox-event substitute for the durable `agent/inbox/spliced` event, which the Inbox seam documents as deferred). A jobs frame per `IJobsService.OnJobsChanged` for the affected owner session. Live projection deltas stay deferred: the registry has no per-key change events, and the baseline carries the consistent cut.
- **Cancellation**: the mux no longer answers a cancelled logical stream with an error frame; `session/follow` and `session/control` end quietly through a token-registered channel completion (the iterator constraint that a `yield return` cannot sit in a try-with-catch made the channel-completion approach the natural shape).

## Consequence

The session catalog is now fully ported at the live-feed level: `session/control` serves the baseline and queue/jobs deltas over real mock-LLM turns and a real jobs provider, the host suite grew from 62 to 67 (4 control suites plus a mux registry-stream cancel suite), all green, with the full solution building at 0 errors and the CLI suite at 17. The wire now matches the TS cancel contract for every registry stream.

## Alternatives considered

- Adding a durable `agent/inbox/spliced` session event to the C# session vocabulary: the Inbox seam's class note already defers it, and the live inbox events carry the same information; the control stream re-derives full items per event instead.
- Catching `OperationCanceledException` in the stream iterators: a `yield return` cannot appear in a try block with a catch clause, so the channel-completion-on-cancellation shape is the compiler-honest form of the same contract.
- Emitting projection deltas per `session/event`: the registry computes whole cuts, not per-key changes, so a per-event frame would be chatty and still not match the `SessionProjectionUpdate` wire shape; the baseline cut is the honest surface until per-key change events exist.
