# Phase 0 Spike — Vertical-Slice Port Design (Session / LLM / Tool)

Status: **design only**. No C# code, no `.csproj` files, nothing built. This document is the
implementation contract for the Phase 0 vertical slice (per `plan/04-execution-phases.md`), which
boots headless against a mock LLM and proves the Cordis port semantics (reversible effects,
waterfall short-circuit) before the go/no-go gate.

Branches and scope:

- This worktree: `port/dotnet10-spike-slice` (created from `port/dotnet10`, which was fast-forward
  merged here — it adds the `.NET .gitignore` rules).
- The main worktree `F:\projects\deepseek-harness` was read-only for this design.
- Agent A ports `Cordis.Core` (the real thing, from `vendor/`) into `src/Cordis/`. This spike
  declares the exact `Cordis.Core` consumer surface it expects (section 7) and **adapts to Agent
  A's port, never the other way around**. Implementation of this slice starts only after the
  orchestrator confirms Agent A's `Cordis.Core` port has landed.

---

## 1. Project layout under `src\Dsh\`

Four projects, one console app. Created only at implementation time (not now).

```
src/Dsh/
  Dsh.slnx                      # solution file (created at implementation time, wires the four)
  Dsh.Session/Dsh.Session.csproj
  Dsh.Llm/Dsh.Llm.csproj
  Dsh.Tools/Dsh.Tools.csproj
  Dsh.Spike/Dsh.Spike.csproj    # OutputType=Exe; ProjectReference to the three above
```

| csproj | Namespace | Purpose (one line) |
|---|---|---|
| `Dsh.Session` | `Dsh.Session` | Append-only `SessionEvent` log + in-memory `SessionStore` (the `sessions` service): `Session.Append`, `Session.DeriveMessages`, `session/created \| event \| disposed` events. |
| `Dsh.Llm` | `Dsh.Llm` | Provider-neutral message/stream vocabulary, `ILlmAdapter` seam, `BlockAssembler`, and the `LlmRuntime` registry (the `llm` service) with the `llm/stream` waterfall. |
| `Dsh.Tools` | `Dsh.Tools` | Scoped tool registry and guarded execution pipeline (the `tools` service): `ToolRuntime.Register/Get/Schemas/ExecuteAsync` and the `tools/pre-execute \| execute \| post-execute \| result \| change` events. |
| `Dsh.Spike` | `Dsh.Spike` | Headless console boot (composition root): registers the three services, the `MockLlmProvider`, the `TodoTool`, runs one smoke turn via `HeadlessTurnDriver`, prints the session log, and asserts effect unwind. |

Target framework: `net10.0` everywhere. All three library projects take a `ProjectReference` to
Agent A's `src/Cordis/Cordis.Core` (the ported framework the spike compiles against).

Project references (acyclic, mirrors the TS import graph):

```
Cordis.Core
  ^          ^          ^
  |          |          |
Dsh.Session Dsh.Llm   Dsh.Tools
  ^          ^          ^
  |          |          |
  +---- Dsh.Spike ------+   (console, references all three)
```

- `Dsh.Session -> Dsh.Llm` (session events carry `UserMessage`/`AssistantMessage`/
  `ToolResultMessage`/`StreamChunk`/`ToolSchema` from the LLM vocabulary, exactly like the TS
  session package imports from `@deepseek-ai/dsh-llm`).
- `Dsh.Tools -> Dsh.Llm` (tool schema, `ToolCallId`, `ContentBlock`).
- `Dsh.Tools` does **not** reference `Dsh.Session` in the spike: `deferContext` /
  `additionalContexts` (which would pull in `UserMessage`) are out of scope (section 5).
- `Dsh.Session` does not reference `Dsh.Tools`; the todo tool's `todo/write` event type is
  registered into the session event-type registry from `Dsh.Spike` at boot (section 3).

Deliberate deviation from `plan/02-target-architecture.md`, noted for the orchestrator: plan 02
consolidates `session` + `tools` (+ agent/agent-loop later) into one `Dsh.Core`. The spike keeps
`Dsh.Session`, `Dsh.Llm`, `Dsh.Tools` as siblings because it ports three independently compilable
seams; consolidating into `Dsh.Core` is a Phase 2 mechanical step and does not change this design's
type boundaries.

---

## 2. File-by-file port map

Every TypeScript file read for this design maps to a planned C# file. Files listed in the package
directories but **not read** are marked `[not read]` and are out of the spike's scope (they carry
persistence, Schemastery, PTC/presentation, or invariant machinery that Phase 2/4 owns).

### 2.1 `packages/core/session/src/` → `Dsh.Session/`

| TS file (read) | C# file planned | Key types / members |
|---|---|---|
| `types.ts` | `SessionEvents.cs` | `SessionId` (branded string), `SessionEvent` base (`Id`, `Seq`, `TimeMs`, abstract `Type`), spike event records (`TurnStartEvent`, `TurnEndEvent`, `StepStartEvent`, `StepEndEvent`, `UserMessageEvent`, `AssistantChunkEvent`, `AssistantMessageEvent`, `ToolCallEvent`, `ToolResultEvent`, `RequestHeaderEvent`, `RequestContextEvent`), `TurnEndReason`, `SessionHeader` (minimal: `Version`, `Id`, `CreatedAtMs`), `SessionFormatVersion` const. |
| `index.ts` | `Session.cs` | `Session` class: `Events` (frozen snapshot, cached + invalidated on append), `Seq`, `Append(SessionEvent)` (assigns `Seq = log.Count`, stamps `TimeMs`, lossless-JSON validation, store publication hook), `DeriveMessages()`, `Header`, `Id`. |
| `index.ts` | `SessionStore.cs` | `SessionStore : Service` (key `sessions`): `Create(SessionId? id, CreateOptions?)`, `Get(SessionId)`, `List()`, store entry with `announced`/`appending` flags; `session/created`, `session/event`, `session/disposed` emits; `create()` registers one effect whose disposer detaches (store removal + `session/disposed`). |
| `index.ts` | `EventValidation.cs` | Lossless-JSON envelope validation (System.Text.Json round-trip), `seq = log.Length` contiguity, "required vs ignorable" unknown-type rule (spike: unknown required type refuses the log). |
| `surface.ts` | `Surface.cs` | `SurfaceOp` (`Append`; `Replace` deferred), `DeriveEventMessage(SessionEvent)` (per-node projection: user/message → verbatim; assistant/message → null when content empty; tool/result → message), `IsSurfaceEligibleType`. Compaction replace path, provenance (`sourceEventSeqs`) validation subset, and the incremental `SurfaceManager` are **deferred** (spike logs are append-only). |
| `request-header.ts` `[not read]` | *(deferred)* | Header folding/equality (`foldRequestHeader`, `headerEquals`) not ported; the spike driver emits one fixed `request/header` (section 6). Full port in Phase 2. |
| `chunk-rows.ts`, `seq-ranges.ts`, `preparation.ts`, `repair.ts`, `known-event-types.ts`, `invariant.ts` | `[not read]` | Deferred: persistence codecs, restore/prepare transactions, repair, the known-type manifest, and the invariant companion are persistence/storage concerns outside the spike. |

### 2.2 `packages/llm/llm/src/` → `Dsh.Llm/`

| TS file (read) | C# file planned | Key types / members |
|---|---|---|
| `types.ts` | `ContentBlocks.cs` | `ContentBlock` hierarchy: `TextBlock`, `ReasoningBlock`, `ToolCallBlock` (`Id: ToolCallId`, `Name`, `Arguments` raw JSON string), `ToolResultBlock` (`ToolCallId`, `Content`, `IsError`). |
| `types.ts` | `StreamChunk.cs` | `StreamChunk` union: `BlockStart(int Index, string BlockType)`, `TextDelta`, `ReasoningDelta`, `ToolCallDelta(int Index, ToolCallId Id, string? Name, string ArgumentsDelta)`, `BlockEnd(int Index, ContentBlock Block)`, `Usage(TokenUsage)`, `Finish(FinishReason, ReplayState?)`. |
| `types.ts` | `FinishReason.cs` | `Stop`, `ToolCalls`, `MaxTokens`, `Aborted(LlmFailure)`, `Error(LlmFailure)`. |
| `types.ts` | `TokenUsage.cs` / `LlmFailure.cs` | `TokenUsage` (input/output/cache/reasoning), `LlmFailure` (`Message`, `Code`, optional `Status`). |
| `types.ts` | `ToolSchema.cs` | `ToolSchema(string Name, string Description, JsonElement Parameters)` — lives in `Dsh.Llm` exactly as it does in the TS tree. |
| `types.ts` | `GenerateOptions.cs` | `GenerateOptions` (Provider, Model, Messages, System?, Tools?, Temperature?, MaxTokens?, CancellationToken). `sessionId` and `purpose` fields are **dropped for the spike** (would create a `Dsh.Llm -> Dsh.Session` edge); noted for Phase 2. |
| `message.ts` | `Message.cs` | `Message` (`MessageId Id`, `string Role`, `IReadOnlyList<ContentBlock> Content`, `MessageSource Source`), `UserMessage`, `AssistantMessage` (source kind `model`), `ToolResultMessage` (single `tool-result` block, source kind `tool`); factories `CreateUserMessage`, `CreateAssistantMessage`, `CreateToolResultMessage`. |
| `brand.ts` | `Ids.cs` | `MessageId`, `ToolCallId`, `ProviderRequestId`, `ReasoningEffortId` — branded string wrappers (record structs with implicit string conversion; the `Branded<>` port is out of scope, a nominal wrapper is enough). |
| `call-config.ts` | `CallConfig.cs` | `LlmCallConfig` (Provider, Model, ReasoningEffort?, Temperature?, MaxTokens?, Stop?), `LlmCallConfigAdapterDefaults`, `CallConfigEquals`. Used by the driver's `request/header`; header-folding equality is deferred. |
| `index.ts` | `LlmRuntime.cs` | `LlmRuntime : Service` (key `llm`): `RegisterAdapter(string[] providers, ILlmAdapter adapter) -> IDisposable` (effect-backed, all-or-nothing, `llm/adapters-updated` emit), `ListProviders()`, `StreamAsync(GenerateOptions, CancellationToken)` → `llm/stream` waterfall wrapping the adapter. `prepareCall`/model-catalog machinery **deferred**. |
| `index.ts` | `ILlmAdapter.cs` | `ILlmAdapter` with `IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, CancellationToken ct)` (the one required method in the TS `LlmAdapter` abstract). |
| `index.ts` | `LlmError.cs` | `LlmError : Exception` carrying a `LlmFailure`; any adapter/waterfall throw normalizes to a terminal `Finish.Error` chunk. |
| `assembler.ts` | `BlockAssembler.cs` | `Push(StreamChunk)`, `Blocks()`, `InterruptedBlocks()`, `Finish` (defaults to `Stop`), `Usage`, `ReplayState`. |
| `content.ts`, `adapter-failure.ts`, `api-key.ts`, `error.ts`, `retry-policy.ts`, `attribution.ts`, `invariant.ts` | `[not read]` | Deferred: image projection, retry policy, credential handling, attribution, invariants are provider/policy machinery outside the spike. |

### 2.3 `packages/core/tools/src/` → `Dsh.Tools/`

| TS file (read) | C# file planned | Key types / members |
|---|---|---|
| `index.ts` | `ToolRuntime.cs` | `ToolRuntime : Service` (key `tools`): `Register(ToolDefinition) -> IDisposable` (effect-backed layer insert; dispose unregisters + emits `tools/change`), `Get(string name)`, `Schemas()` (model-facing allowlist projection: name/description/parameters only), `ExecuteAsync(ToolExecutionInput, CancellationToken)` running the three waterfalls (`tools/pre-execute` → `tools/execute` → `tools/post-execute`) and emitting `tools/result`. Scoped layers (`ScopedLayers`, per-agent shadowing, `restrict`/`guard`), the PTC collapse, and `executionMode` **deferred** (spike is a single global layer). |
| `index.ts` | `ToolDefinition.cs` | `ToolDefinition` record: `Name`, `Description`, `Parameters` (JsonElement), `OutputSchema` (JsonElement), `Execute(Func<JsonElement, ToolRunContext, Task<JsonElement>>)`, `Render(Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>>)`. `output.presentationMeta`, `timeoutMs`, `isConcurrencySafe`, `finalizeContent`, `presentCall/Result` **deferred**. |
| `index.ts` | `ToolExecution.cs` | `ToolExecutionInput` (CallId, Name, Arguments JsonElement, optional Agent, CancellationToken), `ToolRunContext`, `ToolExecutionResult` (`ToolExecutionSuccess { IsError=false, Value, Content }` / `ToolExecutionFailure { IsError=true, Error: ToolFailure, Content }`), `ToolFailure` (Message, optional Code). `deferContext`/`additionalContexts`/`concludesTurn` **deferred**. |
| `index.ts` | `ToolErrors.cs` | `ToolNotFoundError`, `ToolOutputError` (carry a stable code, mirroring `UNKNOWN_TOOL` / `INVALID_TOOL_OUTPUT`). |
| `schema.ts` `[not read]` | `ToolSchemaBuilder.cs` | Minimal `defineTool` analog: validate name/description/parameters shape; parameters stay raw `JsonElement` (no Schemastery port in the spike). |
| `json-schema.ts` `[not read]` | `JsonSchema.cs` | Minimal subset validator for the todo `OutputSchema`: `object`/`array`/`string`/`integer`/`enum`/`required`/`additionalProperties: false`. Full schema support in Phase 2/4. |
| `types.ts` (read) | *(deferred)* | PTC dispatch event payloads (`tool/code-dispatch*`) — out of spike scope. |
| `presentation.ts`, `ptc.ts`, `py-types.ts`, `ts-types.ts`, `testing.ts`, `invariant.ts` | `[not read]` | Deferred: UI presentation, `run_code` transport, SDK renderers, fixtures, invariants. |

### 2.4 `packages/todo/tool-todo/src/` → `Dsh.Spike/` (the spike's ONE tool)

| TS file (read) | C# file planned | Key types / members |
|---|---|---|
| `index.ts` | `TodoTool.cs` | Registers the `todo_write` `ToolDefinition` on `ToolRuntime`: exact description literal (see section 6), `parameters` schema (array of `{content, status}` with `enum` statuses, `additionalProperties: false`), `output` schema (`{todos, counts}`), `Execute` (validate: trimmed non-empty unique content, at most one `in_progress` unless `allowParallelInProgress`; append `TodoWriteEvent` to the owning session; return canonical `{todos, counts}`), `Render` → text `Updated todo list: {n} pending, {m} in progress, {k} completed.` |
| `types.ts` | `TodoTypes.cs` | `TodoItem` record (`Content`, `TodoItemStatus` enum: `Pending`/`InProgress`/`Completed`), `TodoWriteEvent` record, and the boot-time registration of the `todo/write` type into `Dsh.Session`'s event-type registry. |

### 2.5 `packages/core/agent/` + `packages/core/agent-loop/` (skimmed, NOT ported)

The spike does **not** port the agent or agent-loop packages. It extracts the straight-line turn
shape only (turn/start → step/start → user/message → request/header → stream → assistant/message →
tool/call → tool/result → step/end → turn/end) into one driver.

| TS file (skimmed) | C# file planned | What it contributes |
|---|---|---|
| `agent-loop/src/agent.ts` (`turn()` / `step()` / `buildRequest()` straight-line path) | `HeadlessTurnDriver.cs` | `RunOneTurn(Session, LlmRuntime, ToolRuntime, UserMessage)`: the exact ordered append sequence of section 6. |
| `agent-loop/src/tool-calls.ts` | *(folded into `HeadlessTurnDriver.cs`)* | Single-call execution (no scheduler): `appendToolCall` → `tools.ExecuteAsync` → `appendToolResult` (with `sourceEventSeqs = [callSeq]`). |
| `agent/src/types.ts`, `inbox.ts`, `index.ts`, `runtime-types.ts`; `agent-loop/src/index.ts`, `runtime-context.ts`, `constants.ts`, `invariant.ts` | *(not ported)* | Deferred to Phase 2. Notably the `Inbox` and its `agent/inbox/spliced` log events are omitted (documented in section 6), as are `agent/*` events, the parallel tool scheduler, and loop teardown ownership. |


---

## 3. Session event model sketch

### 3.1 Record hierarchy

One sealed record hierarchy; the envelope and payload live on the same record (a C#-idiomatic
discriminated union, as planned in `plan/03-decisions-and-risks.md` §3.5 for
`[JsonPolymorphic]`/`[JsonDerivedType]`). The discriminant is the `Type` string, mirroring the TS
`SessionEvent` envelope `{ type, seq, time, data }`.

```csharp
namespace Dsh.Session;

// Base envelope. `Id` is NEW versus the TS envelope (TS has type/seq/time/data only) —
// see open question Q1.
public abstract record SessionEvent
{
    public required string Id { get; init; }      // event identity (fixture: "evt-<seq>")
    public required long Seq { get; init; }       // monotonic; always == log length at append
    public required long TimeMs { get; init; }    // Unix epoch milliseconds (DateTimeOffset.UtcNow)
    public abstract string Type { get; }          // discriminant, e.g. "user/message"
}

// Boundary / log-only events (no surface metadata).
public sealed record TurnStartEvent(long Turn)        : SessionEvent { public override string Type => "turn/start"; }
public sealed record TurnEndEvent(long Turn, TurnEndReason Reason) : SessionEvent { public override string Type => "turn/end"; }
public sealed record StepStartEvent(long Turn, long Step) : SessionEvent { public override string Type => "step/start"; }
public sealed record StepEndEvent(long Turn, long Step)   : SessionEvent { public override string Type => "step/end"; }

// Message-producing (surface-eligible) events carry SurfaceOp and optional SourceEventSeqs.
public sealed record UserMessageEvent(UserMessage Message, SurfaceOp SurfaceOp, IReadOnlyList<long>? SourceEventSeqs = null)
    : SessionEvent { public override string Type => "user/message"; }
public sealed record AssistantChunkEvent(long Turn, long Step, StreamChunk Chunk)
    : SessionEvent { public override string Type => "assistant/chunk"; }
public sealed record AssistantMessageEvent(long Turn, long Step, AssistantMessage Message,
    TokenUsage? Usage = null, bool Interrupted = false, SurfaceOp SurfaceOp, IReadOnlyList<long>? SourceEventSeqs = null)
    : SessionEvent { public override string Type => "assistant/message"; }
public sealed record ToolCallEvent(long Turn, long Step, ToolCallId CallId, string Name, string Arguments)
    : SessionEvent { public override string Type => "tool/call"; }          // Arguments = raw model JSON string
public sealed record ToolResultEvent(long Turn, long Step, ToolResultMessage Message,
    ToolErrorInfo? Error = null, JsonElement? Meta = null, SurfaceOp SurfaceOp, IReadOnlyList<long>? SourceEventSeqs = null)
    : SessionEvent { public override string Type => "tool/result"; }
public sealed record RequestHeaderEvent(EpochHeader Header, RequestHeaderReason Reason, bool StartsSeries = false)
    : SessionEvent { public override string Type => "request/header"; }
public sealed record RequestContextEvent(string Provider, string Model, long? ContextWindow = null)
    : SessionEvent { public override string Type => "request/context"; }

// Plugin-merged extension (todo package), registered at boot via the event-type registry.
public sealed record TodoWriteEvent(IReadOnlyList<TodoItem> Todos) : SessionEvent { public override string Type => "todo/write"; }

public enum SurfaceOp { Append }   // Replace is deferred with compaction (Phase 2)
public abstract record TurnEndReason;
public sealed record CompletedReason : TurnEndReason;
public sealed record AbortedReason(TurnEndCancelCause Cause) : TurnEndReason;
// blocked / error / max-tokens / interrupted reasons are declared but only Completed is used by the spike fixture.
```

**Merge-extensible `SessionEventMap` analog:** the TS union grows by declaration merging. The spike
emulates this with a static `SessionEventTypeRegistry` (type-string → CLR type + JSON shape)
populated at boot; `Dsh.Spike` registers `TodoWriteEvent` before the session is used. Unknown
required types refuse reconstruction (mirrors the TS `ignorable` rule); the spike logs all events as
required.

### 3.2 Append-only log + in-memory store

`Session` (one per session, owned by `SessionStore`):

- Internal `List<SessionEvent> _log`; **the only mutation is `Append`** — there is no update,
  delete, or reorder API. `Append` assigns `Seq = _log.Count` (the `seq = log.length` contiguity
  contract), stamps `TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`, validates the payload
  is lossless JSON (System.Text.Json round-trip; a non-serializable payload throws at the append
  site, never at a backend flush), freezes by construction (records are immutable), pushes, and
  notifies the attached store.
- `Events` returns a **frozen snapshot** (cached, invalidated on the next append) so a previously
  returned array never grows later — same contract as the TS `events` getter.
- `DeriveMessages()` folds `Events` in order through `DeriveEventMessage` (the `surface.ts`
  projection: `user/message` → verbatim; `assistant/message` → null when `Content` empty;
  `tool/result` → message; everything else → skip). Because the spike log is append-only, surface
  order equals seq order — no `SurfaceManager` needed. Cached with a `derived`/`derivedNodes`
  pair invalidated per append (mirrors TS).

`SessionStore : Service` (key `sessions`):

- `Create(SessionId? id = null, CreateOptions? options = null)` — mints `session-<n>` ids
  (counter, starting at `session-1`), registers ONE effect whose disposer detaches the store entry
  and emits `session/disposed` (the TS `enter`+`announce` pair folded into one effect, so a
  throwing `session/created` listener rolls the attach back). `prepare`/`enter`/`announce` as
  separate public primitives are deferred — the spike uses `Create` only.
- `Get(SessionId)`, `List()`.
- Emits `session/created` (once, on create), `session/event` (post-commit, per append, with
  per-listener containment — a throwing observer is logged, never fails the append), and
  `session/disposed` (on detach). `session/flush` (parallel durability checkpoint) is deferred —
  the spike has no persistence.

---

## 4. LLM seam sketch

### 4.1 Chat provider interface (the adapter seam)

```csharp
namespace Dsh.Llm;

// One provider route adapter — the TS LlmAdapter, trimmed to its one required method.
public interface ILlmAdapter
{
    // Stream one model call as raw chunks; MUST honor ct. Terminal failure = Finish.Error/Aborted
    // chunk (never a thrown exception past this boundary — the runtime normalizes anyway).
    IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, CancellationToken ct);
}

public sealed class LlmRuntime : Service   // key "llm"
{
    // All-or-nothing route registration; effect-backed, returns the disposer
    // (dispose removes every route and emits "llm/adapters-updated").
    public IDisposable RegisterAdapter(string[] providers, ILlmAdapter adapter);

    public IReadOnlyList<string> ListProviders();            // route ids, for assertions

    // llm/stream WATERFALL, then the resolved adapter. Listeners may yield their own
    // chunks and short-circuit by never calling next().
    public IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, CancellationToken ct);
}
```

### 4.2 Message vocabulary (from `message.ts` / `types.ts`)

```csharp
public sealed record Message(MessageId Id, string Role, IReadOnlyList<ContentBlock> Content, MessageSource Source);
public sealed record UserMessage : Message;                    // role "user", any source
public sealed record AssistantMessage : Message;               // role "assistant", source kind "model"
public sealed record ToolResultMessage : Message;              // role "user", one ToolResultBlock, source kind "tool"

// MessageSource: { kind: user | model | tool } (+ plugin/context forms deferred).
// ContentBlock: TextBlock | ReasoningBlock | ToolCallBlock | ToolResultBlock (sealed records).
// Factories: CreateUserMessage, CreateAssistantMessage(provider, model, content),
//            CreateToolResultMessage(callId, content, isError)  — mint deterministic ids in the fixture.
```

### 4.3 Streaming chunks + assembler

`StreamChunk` is the discriminated union from section 2.2. `BlockAssembler` is ported 1:1
(`Push` → `Blocks`/`Finish`/`Usage`/`InterruptedBlocks`) and is the single canonical
chunk-to-message algorithm the driver feeds while appending raw `assistant/chunk` events
(recorded for replay fidelity, mirroring the "raw chunks stay in the log" invariant).

### 4.4 `MockLlmProvider` (Dsh.Spike, canned fixture)

Stateful mock implementing `ILlmAdapter`:

- **Call 1:** streams exactly one assistant tool call — `BlockStart(0, tool-call)` →
  `ToolCallDelta(0, "call-1", "todo_write", argumentsDelta)` → `BlockEnd(0, tool-call block)` →
  `Finish(ToolCalls)`. No `usage` chunk.
- **Call 2+:** streams one plain-text block — `BlockStart(0, text)` → `TextDelta(0, "Todo list recorded.")`
  → `BlockEnd(0, text block)` → `Finish(Stop)`.
- Exposes `int CallCount` for assertions (the waterfall probe in section 6 uses it to prove the
  adapter was bypassed / restored). All ids are fixture-fixed (`call-1`, `msg-assistant-1`, …) so
  the smoke stdout is deterministic.

---

## 5. Tools seam sketch

```csharp
namespace Dsh.Tools;

// A registered tool: model-facing schema + canonical output contract + body.
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,                                    // JSON-schema-ish, as sent to the model
    JsonElement OutputSchema,                                  // canonical output contract (minimal subset)
    Func<JsonElement, ToolRunContext, Task<JsonElement>> Execute,
    Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>>? Render = null); // value -> model content

public sealed record ToolExecutionInput(ToolCallId CallId, string Name, JsonElement Arguments, CancellationToken Ct);
public sealed record ToolRunContext(ToolCallId CallId, string Name, JsonElement Arguments, CancellationToken Ct);

public abstract record ToolExecutionResult { public bool IsError; public IReadOnlyList<ContentBlock> Content; }
public sealed record ToolExecutionSuccess(JsonElement Value, IReadOnlyList<ContentBlock> Content) : ToolExecutionResult;
public sealed record ToolExecutionFailure(ToolFailure Error, IReadOnlyList<ContentBlock> Content) : ToolExecutionResult;
public sealed record ToolFailure(string Message, string? Code = null);

public sealed class ToolRuntime : Service   // key "tools"
{
    public IDisposable Register(ToolDefinition tool);          // effect-backed; dispose unregisters + emits "tools/change"
    public ToolSchema[] Schemas();                             // allowlist projection: name/description/parameters
    public ToolDefinition? Get(string name);
    // Guarded pipeline: tools/pre-execute (waterfall, PreToolDecision allow|deny) →
    // tools/execute (waterfall, around-dispatch) → tools/post-execute (waterfall, accept|block) →
    // tools/result (emit, contained observers). Unknown tool -> ToolNotFoundError (code "UNKNOWN_TOOL").
    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionInput input, CancellationToken ct);
}
```

The spike's pipeline is the straight-line path of the TS registry: pre-execute default `allow`,
execute default = call the body, post-execute default `accept`, then freeze + notify. The three
waterfalls are real (they are part of the go/no-go proof), but `restrict`/`guard`, scoped layers,
the `ask` approval path, PTC collapse, and the parallel scheduler are deferred. `TodoTool`
(section 2.4) is the one registered tool; its `Execute` validates the list, appends a
`TodoWriteEvent` to the owning `Session`, and returns the canonical `{ todos, counts }`.


---

## 6. Headless smoke scenario

The console app `Dsh.Spike` runs these steps in order and prints the fixed stdout of section 6.2.
Assertions fail the process with a non-zero exit code.

1. **Boot a Cordis Context**: `using var ctx = new Context()` (Agent A's ported `Cordis.Core`).
2. **Register services** by string key with typed access: `ctx.Set("sessions", new SessionStore(ctx))`,
   `ctx.Set("llm", new LlmRuntime(ctx))`, `ctx.Set("tools", new ToolRuntime(ctx))`.
3. **Register the mock provider**: `ctx.Get<LlmRuntime>("llm").RegisterAdapter(["mock"], new MockLlmProvider())`.
4. **Register the todo tool**: `ctx.Get<ToolRuntime>("tools").Register(TodoTool.Definition(allowParallelInProgress: false))`.
5. **Create the session**: `var session = ctx.Get<SessionStore>("sessions").Create()` → id `session-1`.
6. **Run ONE turn** via `HeadlessTurnDriver.RunOneTurn(session, llm, tools, userMessage)` — the
   straight-line loop (deliberately NOT the full agent-loop; see 6.3):
   1. append `turn/start { turn: 1 }`
   2. append `step/start { turn: 1, step: 1 }`
   3. append `user/message` (fixed prompt, `surfaceOp: Append`)
   4. append `request/header` (reason `initial`, fixed config `mock`/`mock-todo`, fixed system text,
      `tools = tools.Schemas()`) and `request/context { provider: "mock", model: "mock-todo" }`
   5. for each chunk of `llm.StreamAsync(request)`: append `assistant/chunk` and push into a
      `BlockAssembler`
   6. append `assistant/message` (assembled blocks, `sourceEventSeqs` = the chunk seqs)
   7. for the one `tool-call` block: append `tool/call`; run `tools.ExecuteAsync` (the todo tool
      appends `todo/write` inside its body); append `tool/result` (`sourceEventSeqs = [callSeq]`)
   8. append `step/end { turn: 1, step: 1 }`
   9. **step 2** (the tool result owes the model another request): append `step/start { turn: 1, step: 2 }`
      — no `user/message` (the driver has no inbox to claim); request derives history from the log
      (now including the tool result); stream; append chunks + `assistant/message` (plain text);
      append `step/end { turn: 1, step: 2 }`
   10. append `turn/end { turn: 1, reason: completed }`
7. **Print the session log to stdout in order** (section 6.2).
8. **Waterfall short-circuit probe** (the Phase 0 exit criterion): register an `llm/stream`
   waterfall listener that yields a fixed two-chunk stream and **never calls next()**; call
   `llm.StreamAsync` with a hand-built request; assert the two chunks came from the listener and
   `MockLlmProvider.CallCount` is unchanged (short-circuit works); dispose the listener (an
   effect); call `llm.StreamAsync` again and assert `CallCount` incremented (the effect unwind
   restored the adapter path).
9. **Dispose the context**: `await ctx.DisposeAsync()`.
10. **Assert every effect unwound**: `sessions.Get(session-1)` is null and `session/disposed` was
    emitted; `tools.Get("todo_write")` is null; `llm.ListProviders()` is empty; the session log is
    still readable (all 22 events intact); the session store is empty. Print the assertion lines and
    `== PASS ==`.

### 6.1 Fixed fixture values

- User prompt text: `Record your plan for the .NET spike as todos.` (message id `msg-user-1`).
- Mock tool-call arguments (raw JSON string, exactly one `in_progress` item):

```json
{"todos":[{"content":"Port the session event log","status":"in_progress"},{"content":"Port the mock LLM adapter","status":"pending"},{"content":"Port the todo tool","status":"pending"}]}
```

- System prompt: `You are the Dsh port spike assistant.`
- Message ids are fixture-fixed: `msg-user-1`, `msg-assistant-1`, `msg-tool-1`, `msg-assistant-2`;
  tool-call id `call-1`; event ids `evt-0` … `evt-21`.
- The `todo_write` description is the exact composed literal (single-active variant):

`Record and update a structured task list for the current work. Send the ENTIRE list every call — it REPLACES the previous list (there are no partial updates, no per-item edits). Use it to plan multi-step work and show progress: add one todo per concrete step before you start. Keep AT MOST ONE todo `in_progress` at a time; while work remains, exactly one active task should be `in_progress`. Mark a todo `completed` the moment it is done (do not batch completions), and allow no `in_progress` item only once all work is complete. Skip the list for trivial single-step tasks. Statuses: `pending` (not started), `in_progress` (being worked on now), `completed` (finished).`

### 6.2 Exact expected stdout

Line format: `[%02d] %-20s %s` (seq, event type, compact JSON of the payload). The JSON is
`System.Text.Json` serialization of each event's payload, one line per event, in seq order.

```
== Dsh.Spike headless smoke ==
context booted; services: sessions, llm, tools
session created: session-1
[00] turn/start            {"turn":1}
[01] step/start            {"turn":1,"step":1}
[02] user/message          {"message":{"id":"msg-user-1","role":"user","content":[{"type":"text","text":"Record your plan for the .NET spike as todos."}],"source":{"kind":"user"}},"surfaceOp":"append"}
[03] request/header        {"header":{"config":{"provider":"mock","model":"mock-todo"},"system":"You are the Dsh port spike assistant.","tools":[{"name":"todo_write","description":"Record and update a structured task list for the current work. Send the ENTIRE list every call — it REPLACES the previous list (there are no partial updates, no per-item edits). Use it to plan multi-step work and show progress: add one todo per concrete step before you start. Keep AT MOST ONE todo `in_progress` at a time; while work remains, exactly one active task should be `in_progress`. Mark a todo `completed` the moment it is done (do not batch completions), and allow no `in_progress` item only once all work is complete. Skip the list for trivial single-step tasks. Statuses: `pending` (not started), `in_progress` (being worked on now), `completed` (finished).","parameters":{"todos":{"type":"array","required":true,"description":"The COMPLETE task list, replacing any previous list.","items":{"type":"object","additionalProperties":false,"properties":{"content":{"type":"string","required":true,"description":"What the task is — a short imperative line."},"status":{"type":"string","required":true,"enum":["pending","in_progress","completed"],"description":"pending (not started) | in_progress (now) | completed (done)."}}}}}}]},"reason":"initial"}
[04] request/context       {"provider":"mock","model":"mock-todo"}
[05] assistant/chunk       {"turn":1,"step":1,"chunk":{"type":"block-start","index":0,"blockType":"tool-call"}}
[06] assistant/chunk       {"turn":1,"step":1,"chunk":{"type":"tool-call-delta","index":0,"id":"call-1","name":"todo_write","argumentsDelta":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}}
[07] assistant/chunk       {"turn":1,"step":1,"chunk":{"type":"block-end","index":0,"block":{"type":"tool-call","id":"call-1","name":"todo_write","arguments":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}}}
[08] assistant/chunk       {"turn":1,"step":1,"chunk":{"type":"finish","reason":{"kind":"tool-calls"}}}
[09] assistant/message     {"turn":1,"step":1,"message":{"id":"msg-assistant-1","role":"assistant","content":[{"type":"tool-call","id":"call-1","name":"todo_write","arguments":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}],"source":{"kind":"model","provider":"mock","model":"mock-todo"}},"surfaceOp":"append","sourceEventSeqs":[5,6,7,8]}
[10] tool/call             {"turn":1,"step":1,"callId":"call-1","name":"todo_write","arguments":"{\"todos\":[{\"content\":\"Port the session event log\",\"status\":\"in_progress\"},{\"content\":\"Port the mock LLM adapter\",\"status\":\"pending\"},{\"content\":\"Port the todo tool\",\"status\":\"pending\"}]}"}
[11] todo/write            {"todos":[{"content":"Port the session event log","status":"in_progress"},{"content":"Port the mock LLM adapter","status":"pending"},{"content":"Port the todo tool","status":"pending"}]}
[12] tool/result           {"turn":1,"step":1,"message":{"id":"msg-tool-1","role":"user","content":[{"type":"tool-result","toolCallId":"call-1","content":[{"type":"text","text":"Updated todo list: 2 pending, 1 in progress, 0 completed."}],"isError":false}],"source":{"kind":"tool","callId":"call-1"}},"surfaceOp":"append","sourceEventSeqs":[10]}
[13] step/end              {"turn":1,"step":1}
[14] step/start            {"turn":1,"step":2}
[15] assistant/chunk       {"turn":1,"step":2,"chunk":{"type":"block-start","index":0,"blockType":"text"}}
[16] assistant/chunk       {"turn":1,"step":2,"chunk":{"type":"text-delta","index":0,"text":"Todo list recorded."}}
[17] assistant/chunk       {"turn":1,"step":2,"chunk":{"type":"block-end","index":0,"block":{"type":"text","text":"Todo list recorded."}}}
[18] assistant/chunk       {"turn":1,"step":2,"chunk":{"type":"finish","reason":{"kind":"stop"}}}
[19] assistant/message     {"turn":1,"step":2,"message":{"id":"msg-assistant-2","role":"assistant","content":[{"type":"text","text":"Todo list recorded."}],"source":{"kind":"model","provider":"mock","model":"mock-todo"}},"surfaceOp":"append","sourceEventSeqs":[15,16,17,18]}
[20] step/end              {"turn":1,"step":2}
[21] turn/end              {"turn":1,"reason":{"kind":"completed"}}
-- waterfall short-circuit probe --
probe stream: 2 chunks from listener; mock adapter calls still 1   (short-circuit OK)
listener disposed; next stream served by mock adapter (calls 2)     (effect unwind OK)
-- context dispose --
sessions: store empty (session-1 detached; session/disposed emitted)
tools: todo_write unregistered (tools/change emitted)
llm: 0 adapters (llm/adapters-updated emitted)
session log: 22 events intact after dispose
== PASS ==
```

Notes pinned to the fixture: seqs 0–21 (22 events); step 2 carries no `user/message` because the
driver has no inbox (6.3); `todo/write` (seq 11) lands between `tool/call` and `tool/result`
because the tool appends it inside its body; `assistant/message` seqs cite their chunk seqs via
`sourceEventSeqs`; no `usage` field appears because the mock emits no usage chunk.

### 6.3 Deliberate omissions vs the full loop (Phase 2 scope)

- No `Inbox` and no `agent/inbox/spliced` events (the full loop logs one when it claims the turn
  message — see open question Q9). The driver holds the single user message in memory.
- No `agent/pre-step`, `agent/request`, `agent/turn-stopping` events, no system-prompt assembly
  beyond a fixed string, no request-header equality/folding (one fixed header), no parallel tool
  scheduler, no `session/flush`, no persistence.

---

## 7. Cordis.Core integration seam (contract the spike compiles against)

Agent A ports the real `Cordis.Core` from `vendor/` (Context / Service / Fiber / Events /
Registry, per `plan/03-decisions-and-risks.md` §3.1). This section is the **spike's declared
consumer contract**: the exact surface the four `Dsh.*` projects will call. The integration agent
reconciles this against the real port. **The spike adapts to Agent A's API, not the other way
around** — if the port lands first, the spike compile step absorbs signature drift; if the spike
compiles first against this contract, Agent A treats it as one consumer to validate the port.

```csharp
namespace Cordis.Core;   // actual namespace owned by Agent A; the spike adapts

public sealed class Context : IAsyncDisposable
{
    // --- Services: string-keyed store with typed access (the "repository of services" idea) ---
    public void Set<T>(string key, T service) where T : class;       // register under a stable key
    public T? Get<T>(string key) where T : class;                    // strict read; null when absent
    public T Require<T>(string key) where T : class;                 // throws when absent (fail loud)

    // --- Reversible effects: every registration is an effect; disposal unwinds in reverse order ---
    public IDisposable Effect(Func<IDisposable> register);           // register() returns the disposer
    // (an async teardown variant / ordered generator-effect analog is Agent A's call — Q2)

    // --- Events ---
    public IDisposable On(string name, Delegate listener);           // listener registration is an effect too
    public void Emit(string name, params object?[] args);            // observe-only; containment TBD (Q4)

    // --- Waterfall middleware: around-middleware; calling next() delegates, NOT calling it short-circuits;
    //     values propagate through next()'s return value (cordis-primer "Waterfall Semantics") ---
    public TResult Waterfall<TResult>(string name, object?[] args, Func<TResult> next);

    // Minimal logger (containment diagnostics, e.g. session/event observer warnings).
    public ILogger Logger { get; }

    public ValueTask DisposeAsync();                                 // unwinds all effects (sync Dispose() also fine for the spike)
}

public abstract class Service
{
    protected Service(Context ctx, string key);   // registers `this` under `key` on `ctx` (TS: super(ctx, key))
    public Context Ctx { get; }
}
```

Spike usage of the contract (every call site category):

| Contract member | Used by |
|---|---|
| `Set` / `Get<T>` / `Require<T>` | `Dsh.Spike` boot (register `sessions`/`llm`/`tools`; resolve them for the driver); `Service` constructor registration |
| `Effect(Func<IDisposable>)` | `SessionStore.Create` (attach/announce + detach disposer), `LlmRuntime.RegisterAdapter`, `ToolRuntime.Register`, listener registrations |
| `On` / `Emit` | `session/created`, `session/event`, `session/disposed`, `tools/change`, `tools/result`, `llm/adapters-updated` |
| `Waterfall` | `llm/stream` (short-circuit probe in section 6), `tools/pre-execute`, `tools/execute`, `tools/post-execute` |
| `Logger` | contained observer-failure warnings (session/event, tools/result) |
| `DisposeAsync` | end-of-smoke effect unwind (section 6 step 9) |

Deliberately NOT required by the spike (the port may still ship them): `parallel`/`serial`/`bail`
dispatch, scoped dispatch carriers (`scopeOf`/`scopeTarget`/`Scoped`), `inject` lazy dependencies,
loader/config machinery, and the internal `ctx.events.dispatch` primitive. The spike emits on the
plain root context only.

---

## 8. Open questions for the integration agent

| # | Question | Why it matters |
|---|---|---|
| Q1 | **Per-event `Id`**: the TS envelope is `{type, seq, time, data}` — no event id; this design adds `Id` (section 3.1) per the mandate. Must the durable/JSON shape stay byte-identical to the TS log? If yes, `Id` must be in-process only (non-serialized) or dropped; if no, keep it. | Persistence parity in Phase 2; seed/replay compatibility with existing logs. |
| Q2 | **`ctx.effect` disposer shape**: sync `IDisposable`, `Func<ValueTask>`, or `IAsyncDisposable`? Does the port keep the TS generator-effect (ordered `yield` of disposers, reverse unwind on throw)? The spike only needs reverse-order unwind at `DisposeAsync`. | Every `Register`/`Create` signature in sections 3–5. |
| Q3 | **Service base**: does the port's `Service` auto-register on construction (`super(ctx, key)`), and are the spike's keys exactly `sessions` / `llm` / `tools`? | Boot sequence (section 6 step 2) and typed `Get<T>`. |
| Q4 | **Emit containment**: TS `session/event`/`tools/result` observers are contained per listener (throw → warn, append still commits). Does the port's `Emit` throw on a listener failure? The spike will contain its own dispatch regardless, but the port's default changes how much the spike must wrap. | Publication semantics of `Session.Append`. |
| Q5 | **Waterfall signature**: confirm (a) a listener that never calls `next()` short-circuits (the exit-criterion probe relies on it), (b) the return value of `next()` is what downstream sees, (c) listener resolution is per-dispatch (snapshot) — mirroring `cordis-primer.md`. | `llm/stream` and the `tools/*` pipelines. |
| Q6 | **Cancellation mapping**: TS `AbortSignal` ↔ C# `CancellationToken`. Is a token threaded through `Context`/effects, or is `DisposeAsync` the only cancellation? The spike's `ToolRunContext`/`GenerateOptions` carry a `CancellationToken`. | Tool-call cancellation semantics; `MockLlmProvider` honoring `ct`. |
| Q7 | **Logger surface**: minimal `ILogger` (Info/Warn/Error) vs `Microsoft.Extensions.Logging`? Spike uses `Warn` for containment messages only. | `Dsh.Session`/`Dsh.Tools` containment diagnostics. |
| Q8 | **Scoped dispatch**: the spike avoids `scopeOf`/`scopeTarget`/`Scoped` entirely and emits on the plain root context. Confirm plain-context `On`/`Emit`/`Waterfall` is supported by the port (no carrier required). | Whether `SessionStore`/`ToolRuntime` need the carrier plumbing in the spike. |
| Q9 | **Inbox omission**: the full loop logs `agent/inbox/spliced` when claiming a turn message; the spike driver deliberately has no inbox, so the smoke fixture's 22 events omit it (section 6.2). Confirm a straight-line turn without inbox events is an acceptable Phase 0 stand-in — the go/no-go tests Cordis semantics (effects, waterfall, service lifecycle), not loop parity. | Whether the orchestrator accepts the fixture as-is or wants an inbox-splice event added. |
| Q10 | **Timestamp source**: TS uses `Date.now()` (Unix ms). Confirm `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` is fine (no monotonicity requirement) and whether `TimeMs` must be validated as non-negative. | `Session.Append` stamping; seed validation. |

