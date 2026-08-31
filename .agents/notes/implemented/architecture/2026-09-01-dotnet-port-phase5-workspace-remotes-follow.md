# Agent Note: .NET port Phase 5 wave 1 — the workspace commands and follow feed complete the namespace

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase5-workspace-remotes-follow.zh.md)

## Problem

The workspace remote namespace had only `workspace/create` (over the single-slot lifecycle). The durable registry landed first; this round rewires the commands onto it and adds the follow feed, completing the workspace namespace against the TS WorkspaceController.

## Decision

`WorkspaceRemotes` now sits entirely on the registry:

- `workspace/create` resolves by path first (idempotent re-registration answers `created: false`,   matching the TS command), creates otherwise, and wraps every seam failure as   `workspace/invalid-path { path }` (the TS wraps all non-Remote create errors the same way).
- The id-based commands map the seam codes to the TS wire codes: `workspace/not-found   { workspaceId }` for missing targets and (like the TS order-error mapping) invalid order   moves; `workspace/name-conflict { name }` for duplicate rename titles; `workspace/move-invalid   { workspaceId, sessionId, beforeSessionId? }` for non-member session moves;   `session/not-found { sessionId }` for archive requests naming an unknown session.
- `workspace/follow` streams the baseline (`{ items, archivedSessionIds }`) then   upsert/remove/order/archived deltas from the registry events, subscribing before the baseline   so mutations during the read queue behind it (the same shape as session/control). Workspace   views carry the real session membership from the registry.

## Consequences

The workspace namespace is complete: create/rename/delete/insertBefore/insertSessionBefore/ archiveSession/follow over a durable catalog, with the host suite at 85 (the remotes suite grew from 5 to 11 cases including the follow feed with its delta ordering) and the full solution building at 0 errors. The catalog's remaining wire gaps are the live projection deltas and the `$events` waterfall settlement.

## Alternatives considered

- Keeping create on the lifecycle provider: the registry supersedes it for the remote surface,   and the idempotence semantics (resolveByPath vs single-slot current) match the TS only on the   registry; the lifecycle remains available as its own seam.
- Emitting one order frame per mutation even when the order is unchanged: the registry emits   order frames on every committed order-affecting mutation (create appends, delete rewrites,   insertBefore moves), and the follow feed forwards them verbatim — the client treats them as   idempotent full-order frames, matching the TS feed's shape.
