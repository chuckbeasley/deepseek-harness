# Agent Note: .NET port Phase 5 wave 1 — the typed-codec source generator

Status: implemented

## Problem

The gateway's wire envelopes were hand-written JsonElement plumbing: the unary carrier
built the server envelope field by field with a Utf8JsonWriter, and the mux built error
frames through anonymous objects. The deferral named the Roslyn source generator for typed
codecs — the typert codec half — so the wire code is generated and compile-time-checked
instead of maintained by hand.

## Decision

- `src/Dsh/Dsh.Rpc.Generator` is an incremental Roslyn source generator: a record marked
  `[RpcCodec]` (the attribute is emitted by the generator itself) gains a static
  `<Name>Codec` class with property-by-property `Encode` (Utf8JsonWriter-based) and
  `TryDecode` (per-property type checks with readable refusals). Supported member types:
  string, int, long, double, bool, `System.Text.Json.JsonElement`, nullable forms of those,
  and nested `[RpcCodec]` records whose generated codecs compose. An unsupported member type
  emits a `#error` so the build fails loud instead of shipping a half codec. A nullable
  JsonElement member encodes as an empty object when absent — the RPC error vocabulary
  always carries `details`.
- The consumer is the gateway itself: `RpcError` carries `[RpcCodec]`, and both the unary
  carrier's result-error branch and the mux's error frame now render through the generated
  `RpcErrorCodec.Encode`. The wire shape is unchanged — the codec tests pin the exact
  `{code, message, details}` object, the round trip, and the refusal vocabulary, and the
  existing gateway/mux/fence suites prove the shape over the real carriers.

## Consequence

The gateway's error encoding is generated code; adding or changing a codec member type
fails the build with a pointed diagnostic instead of silently mis-serializing. 103 host
suites green (4 new codec suites); full solution builds at 0 errors. Implementation facts
worth keeping: the generator targets netstandard2.0 (records need an `IsExternalInit`
polyfill, and `Environment`/range syntax are banned by the analyzer rules), positional
records must be constructed through their primary constructor (object initializers need a
parameterless one), and `JsonElement`-nullables need an explicit `(JsonElement?)null` cast
in generated conditionals.

## Alternatives considered

- Hand-writing every codec (status quo ante): the deferral names the generator, and the
  gateway was the one consumer that made the generated code real rather than speculative.
- Generating the full envelope writer (type/rpcId/result nesting): the envelope has
  value-or-error branching that a plain record cannot express; the generated codec covers
  the shared error vocabulary, and the fixed envelope shell stays in the carrier where the
  TS host also keeps it.
