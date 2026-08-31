# Agent Note: .NET port Phase 6 wave 2 — the SDK runtime server

Status: implemented

## Problem

The protocol transport was ported but nothing served it: an out-of-process SDK needs the
runtime server — the piece that hosts one booted harness context over the JSON-RPC transport,
validates and records the SDK route, lazily creates agent+session pairs, and streams the
runtime's lifecycle back to the client.

## Decision

- `src/Dsh/Dsh.Sdk.Server` ports the TS `HarnessSdkJsonRpcServer`: `initialize` validates the
  handshake (a malformed reasoning effort or non-positive token cap fails loud; an unowned
  `deepseek-official` route mounts the DeepSeek adapter like the spine's deepseek row; any
  other unregistered provider fails loud), `session/prompt` lazily creates the agent+session
  pair on the ported agent loop with the recorded route (the route's `maxTokens` flows into
  `AgentOptions`) and enqueues the durable user message, and `shutdown` disposes the
  server-owned sessions, adapter, and subscriptions while the surrounding context keeps
  running. `session.event` and `session.status` stream live over the transport from the
  context's own events.
- The prompt-block wire codec ships in the protocol project: a block with `type: "image"`
  decodes to the inline-image member, anything else decodes through the session log's
  polymorphic `ContentBlock` codecs (the same wire the durable log speaks), and the inverse
  writes explicitly.
- Documented reductions, each named: the `subagent.started`/`subagent.finished`
  notifications await the port's subagent lifecycle events and parent lineage (the session
  header carries none), inline image prompt blocks are rejected until the attachment seam
  admits base64 (it ingests from paths only), and the reasoning effort validates but has no
  `AgentOptions` seat (the loop's call config does; wiring it through the options is a
  loop-seam change).

## Consequence

An SDK client can handshake, prompt, and observe a real harness runtime: 8 server suites
prove the handshake validation, the fallback adapter mount, lazy session creation with real
mock turns on the ported loop, the live notifications, the image reduction, shutdown
semantics, and the unknown-method error. 43 console suites total green; full solution builds
at 0 errors. The client SDK, the ACP server, the hook bridges, and the python/ retirement
follow in later Phase-6 waves.

## Alternatives considered

- Serving through the web gateway: the SDK protocol is stdio-line JSON-RPC by design; the
  transport is the only wire, and the server composes the same seams the gateway does.
- Adding the reasoning effort to `AgentOptions` now: the loop's call config already carries
  the seat; the options plumbing is a loop-seam change better made with a consumer that
  needs it beyond validation.
