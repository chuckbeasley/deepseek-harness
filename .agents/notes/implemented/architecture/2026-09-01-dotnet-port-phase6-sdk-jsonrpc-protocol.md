# Agent Note: .NET port Phase 6 wave 1 — the SDK JSON-RPC protocol

Status: implemented

## Problem

Phase 6 (SDK + ACP + hooks) has no foundation in the port: the runtime server, the client
SDK, and the ACP server all speak one shared wire protocol — the newline-delimited JSON-RPC
2.0 stdio transport of `packages/sdk/protocol` — and nothing in the port spoke it yet.

## Decision

- `src/Dsh/Dsh.Sdk.Protocol` ports the protocol package: `JsonRpcLineTransport` reads
  newline-delimited frames from a caller-owned input stream and writes to a caller-owned
  output stream. Requests correlate through `req_<uuid>` ids; a missing request handler
  answers `-32601`, a handler failure `-32603` with the message, notifications without a
  handler are dropped, and malformed lines are ignored. Cancellation removes the pending
  entry and rejects with `OperationCanceledException`, but the registration lives until the
  request settles — a cancel after the send still fails the pending request (the TS removes
  its abort listener in the resolve/reject paths, not at send time; an early `using`-scope
  registration was the first implementation bug the suite caught). `Close` and input EOF
  reject every pending request with `JSON-RPC transport closed` / `JSON-RPC input closed`.
  Writes serialize under one gate and flush per frame, so concurrent requests cannot
  interleave with notifications (the TS relies on the event loop; documented).
- The wire contract ships as records over the ported types: the `initialize` /
  `session/prompt` / `shutdown` method names and the wire-stable server identity, the
  handshake/request/result records, and the four notification payloads referencing the
  ported `ContentBlock` (Dsh.Llm), `SessionEvent` (Dsh.Session), and `SubagentStopReason`
  (Dsh.Subagent). The prompt-content union (durable blocks + inline images) is declared; its
  wire codec joins with the runtime server wave.

## Consequence

The protocol both wire ends speak is ported and proven: 12 protocol suites (request/response
round trip, error codes, handler and notification wiring, malformed-line tolerance,
cancellation, closure and EOF semantics, and the type contract) over crossed in-memory pipe
pairs. 42 console suites total green; full solution builds at 0 errors. The runtime server,
the client SDK, the ACP server, the hook bridges, and the python/ retirement follow in later
Phase-6 waves.

## Alternatives considered

- Wrapping a stock JSON-RPC library: the protocol is small, wire-pinned, and stdio-shaped;
  the port keeps it owned like every other seam.
- Async writes with a writer task: the sync write-under-gate with per-frame flush is simpler
  and sufficient for stdio and the in-memory fixtures; an async writer joins if a consumer
  needs backpressure.
