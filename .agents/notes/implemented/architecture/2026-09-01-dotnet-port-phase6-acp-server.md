# Agent Note: .NET port Phase 6 wave 4 — the ACP server and runtime profile

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase6-acp-server.zh.md)

## Problem

The port had no automation-only surface for trusted programmatic clients: the Agent Client Protocol server (persistent sessions, standard configuration, committed updates, cancellation, one-shot permission decisions) had no .NET counterpart, and no shipped profile booted it.

## Decision

- `src/Dsh/Dsh.Acp` ports `@deepseek-ai/dsh-acp` over the ported JSON-RPC transport (the   ACP wire is the same newline-delimited JSON-RPC 2.0): the full method surface   (`initialize` with the capability declaration and the wire-stable identity   `deepseek-harness-acp`/`0.0.1`, `authenticate`, `session/new`, `session/list` with the   base64url keyset cursor, `session/resume` through the loop's resume flow, `session/close`,   `session/setConfigOption`, `session/prompt`, and the `session/cancel` notification), the   ordered committed updates (message/thought chunks, tool lifecycle, the `sessionUpdate`   discriminator on every concrete update), and the approval bridge — the `tools/pre-execute`   gate asks the composed answerers and the `approval/request` waterfall routes owned   sessions' one-shot decisions to the client as `session/requestPermission`.
- The transport now dispatches incoming requests concurrently: the reader must keep reading   while a handler runs, or a handler's own outbound request (the `requestPermission` bridge)   and the notifications behind it could never be processed. Responses stay id-correlated.
- The `acp` runtime profile ships as the `dsh-acp` bundle (`approval` + `acpRuntime` rows   over console stdio, exiting on stdin EOF; the route follows `DEEPSEEK_API_KEY` like the   headless/web rows).
- The session store's `Remove` is exercised by the resume flow exactly as its doc describes   (release the identity before its stored log rehydrates a fresh session).
- Documented reductions, each named: MCP mounts are refused until the port has an MCP client   seam, inline image prompts await the attachment admission seam, the model option   advertises the session's fixed route only (the loop reads `AgentOptions` at creation; no   catalogs or reasoning efforts), usage updates await the port's token meter, the persisted   header carries no origin/parent/cwd so the resume checks are existence-only and the list's   workspace filter is vacuous, and prompt cancellation flows through `session/cancel` only   (the ported transport has no server-side request abort).

## Consequences

An ACP client can create, prompt, observe, approve, cancel, list, resume, and close real harness sessions: 13 ACP suites prove the codec and model-control state, the identity and capabilities, the validation and reductions, the committed-update stream, the one-shot approval bridge, deterministic cancellation, the list cursor paging, the resume flow, and the real `acp` profile end to end over process stdio. 45 console suites total green; full solution builds at 0 errors. The hook bridges and the python/ retirement follow in the remaining Phase-6 waves.

## Alternatives considered

- Porting the `@agentclientprotocol/sdk` wire layer wholesale: the ported transport already   speaks the same newline-delimited JSON-RPC 2.0, so the ACP server wires it directly with   the wire records and constants it needs.
- Per-session persistence attach: the spine's sessionPersistence row attaches the whole   store, so the server relies on the deployment wiring instead of double-subscribing.
