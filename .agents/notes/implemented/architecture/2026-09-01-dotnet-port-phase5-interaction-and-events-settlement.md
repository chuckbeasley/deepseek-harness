# Agent Note: .NET port Phase 5 wave 1 — the interaction seam and the $events waterfall settlement

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase5-interaction-and-events-settlement.zh.md)

## Problem

The last deferred wire surface: the `$events` stream forwarded only plain emits, there was no waterfall delivery and no `$events/result` settlement, and the ask-user/approval surface was not ported — the TUI owned its own approval dialog on `tools/pre-execute` and nothing else could answer a human question.

## Decision

- `src/Dsh/Dsh.Interaction` ports the interaction capability seam: the approval service   (`approval/request` waterfall with the closed outcome vocabulary allowed-once /   rejected / cancelled / unavailable, fail-closed with no answerer, the ask/never session   policy with the `approval/policy` log override, and the turn-enclosed   `approval/asked` + `approval/decided` audit pair — an idle ask rejects before appending,   because a bare event between turns is crash-tail garbage on reload), the user-questions   service (`user-questions/ask` answerer waterfall with the ASK_ABORTED / EMPTY_QUESTIONS /   UNAVAILABLE taxonomy), and the `ask_user_question` model-facing tool. The interaction/*   session events register in the session event-type registry like every other plugin-merged   marker.
- The web host bridges both waterfalls onto every live `$events` stream: a proposal forwards   as `{type: "waterfall", event, eventId, agentId, request}` with the request projected to   JSON-safe fields, the pending continuation is held in the per-client settlement, and the   `$events/result` unary settles it — `next` delegates to the waterfall chain, `result`   maps the value into the closed vocabulary (anything else fails closed), `rejected`   restores the remote error (name/code/details) and fails the ask closed. An aborted   request or a closing stream delivers `{type: "cancel", eventId}` and settles the ask   cancelled/aborted. The wire shapes mirror the TS stream protocol: an unknown clientId   settles `gateway/internal` ("identifies no active event stream"), an unknown eventId on a   known client acks as the no-op, and a malformed payload settles `gateway/bad-request`.
- The spine mounts `approval` (policy config, loud on an unknown value), `userQuestions`,   and `toolAskUser` rows; the webHost row owns the settlement and registers the   `$events/result` method.

## Consequences

A remote GUI can now answer approval asks and user questions over the mux, and the seam is the single place the ask/audit/policy semantics live (the TUI keeps its own dialog as one answerer-shaped consumer). 111 host suites (7 new settlement suites over a real Kestrel host and mux WebSocket, including the cancel-frame path and both bridges) and 12 interaction suites green; full solution builds at 0 errors. Two implementation facts worth keeping: a lambda parameter named `_` shadows the `out _` discard (the $events/result handler must name its cancellation token), and a registered-but-idle client generation is still "active" for the no-op ack, so the settlement tracks clients separately from pending proposals.

## Alternatives considered

- Forwarding every waterfall event automatically: the seam names the interaction surface,   and the bridge subscribes exactly the two interaction waterfalls; a general remote-waterfall   mechanism waits for a consumer that needs it.
- The full TS approval surface (live-agent ownership checks, the policy user-message   injection, permission presets, commands): the deferral names the ask-user/approval   surface; the live checks and the message injection join when their consumers land.
