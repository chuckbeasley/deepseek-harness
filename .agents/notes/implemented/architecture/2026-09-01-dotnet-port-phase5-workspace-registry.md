# Agent Note: .NET port Phase 5 wave 1 — the durable workspace registry

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase5-workspace-registry.zh.md)

## Problem

The workspace remote namespace sat on the single-slot lifecycle (one current workspace, no persistence), while the TS side is a durable registry with display order, session membership, and an archive set. The six registry-based commands and the follow stream were deferred behind it.

## Decision

`WorkspaceRegistry` (ctx.workspaceRegistry) ports the TS registry core over the existing JSON storage seam: one `workspace_registry` store holds per-workspace records (id, canonical path, title, instants, sessionIds) in the `workspaces` table and the display order + archive set in the global singleton. Every committed mutation persists then emits its registry event (`workspace/upserted|removed|order|archived`), which the follow feed will consume. The command surface matches the TS shapes: create (path validation with the seam codes, duplicate-path rejection; the command layer answers idempotent re-opens through resolveByPath), rename (unique non-blank titles), delete, insertBefore (DOM-like moves), attach/insertSessionBefore, and archiveSession.

Two documented reductions: session membership is explicit (`AttachSession`) because the C# session persistence carries no header-level workspace accounting (the TS derives membership from sessionPersistence headers); archive validation uses an injected session-known predicate defaulting to accept-any when no session store is composed (the TS validates against live sessions plus persistence). The lifecycle provider stays as the identity/root core; the registry is the durable catalog the remote namespace sits on.

## Consequences

The workspace seam now has the durable catalog: 10 new registry suites (19 workspace suites total) cover create/resolve/rename/delete/order, membership moves, the archive set with the known-session gate, the change events, and persistence across instances, all green, with the full solution building at 0 errors. The workspace remote commands and the follow stream land on this registry next.

## Alternatives considered

- Deriving session membership from session-persistence headers like the TS: the C# persistence   format carries no workspace accounting, and extending the format is a separate wave; explicit   membership with the attach entry point keeps the registry honest without inventing a header   contract.
- Persisting a whole-document blob instead of the storage unit: the storage seam's tables +   global exactly match the registry's shape (records + order/archive), so the unit is the natural   medium and keeps the document inspectable JSON.
