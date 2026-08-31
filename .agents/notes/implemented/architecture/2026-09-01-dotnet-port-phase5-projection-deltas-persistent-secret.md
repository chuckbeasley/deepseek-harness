# Agent Note: .NET port Phase 5 wave 1 — live projection deltas and the persistent fence secret

Status: implemented

## Problem

Two documented gaps remained: the session control stream's live projection deltas (the baseline
carried the consistent cut but no per-key updates, because the projection registry has no change
events), and the fence's per-instance signing secret (cookies died on host restarts, unlike the TS
credential-record secret).

## Decision

- `SessionControlRemotes` now subscribes `session/event` and, after every committed event, diffs
  the affected session's projection cut against the last sent one, emitting one
  `projection { sessionId, key, value, seq }` frame per changed key (the TS
  SessionProjectionUpdate shape). The cut is seeded from the same state the baseline reads, so
  the first delta only carries real changes (the baseline already showed e.g. `title: null`);
  frames are full-value and idempotent, and non-JSON views are omitted the same way the baseline
  omits them. The diff state and the snapshot reads serialize under one lock, so concurrent
  events from different sessions cannot race the registry's lazy cell building.
- `WebHostService` now resolves the cookie signing secret through the credentials seam when one
  is composed: it reads the `DSH_WEB_SESSION_SECRET` reference (an environment value wins; a
  malformed stored value fails loud), or creates a fresh 32-byte base64url value in the managed
  store, so cookies survive host restarts like the TS credential record. Without a credentials
  seam the fence keeps the per-instance random secret.

## Consequence

`session/control` is now complete (baseline + queue/jobs/projection deltas), and the fence's
documented reduction is closed whenever the credentials seam is composed. The host suite grew
from 85 to 87 (a projection-delta suite over real mock turns and a restart suite proving a
pre-restart cookie authenticates against a fresh host sharing the managed store), all green, with
the full solution building at 0 errors.

## Alternatives considered

- Making the projection registry emit per-key change events: the registry computes whole cuts and
  has no per-key diff contract; the control stream owns the diff, keeping the registry unchanged.
- Always creating the secret even without a credentials seam: the managed store IS the credentials
  seam; a host without it has nowhere durable to put the secret, so the random fallback is the
  honest bound.
