# Agent Note: .NET port Phase 5 wave 1 — settings, credentials, and workspace remote namespaces land

Status: implemented

## Problem

The Phase 5 web foundation (gateway, mux, `$events`, the Blazor shell, and the session remotes) recorded the settings, credentials, and workspace remote namespaces as deferred. Closing the wave's remote catalog meant porting those three namespaces over the already-ported seams (`Dsh.Settings`, `Dsh.Credentials`, `Dsh.Workspace`) with the exact TS controller behaviors: redacted reads, classified write refusals, grammar and batch bounds, and the provider-absent diagnostic.

## Decision

Three remote classes in `Dsh.Web.Host` mirror the TS controllers field by field:

- `SettingsRemotes` — `settings/describe` (redacted catalog: writable/hasDocument + one view per namespace with ns, schema, redacted value/base/user, applies, secret slots, revision), `settings/update`, and `settings/replace`. Writes classify every seam refusal: a stale revision becomes `settings/conflict { ns, expected, actual }`; anything else becomes `settings/rejected { ns }`. The wire schema rides the Schemastery `toJSON()` refs envelope, ported as `Cordis.Schemastery.Schema.ToJson()` (`{ uid, refs }` with child references as uid numbers, callables never serialized).
- `CredentialsRemotes` — `credentials/describe` (batch ≤ 64 refs, grammar `^[A-Za-z_][A-Za-z0-9_]*$`, per-ref configured/source/writable, never the value), `credentials/set` (non-empty value gate), and `credentials/unset`; a shadowed write refuses with `credential/rejected { ref }` and the seam's own message.
- `WorkspaceRemotes` — `workspace/create` over the ported single-slot lifecycle: idempotent re-open answers `created: false`, failures classify `workspace/invalid-path { path }` (the TS wrap of every non-Remote error), and the view carries the stable id, canonical path, title, an empty `sessionIds` (accounting is deferred with the registry), and ISO-8601 instants.

The gateway transports any string code verbatim through the new open-string `RpcDomainError` (code + optional details), the same way the TS `RemoteErrorCode` union stays open. The namespaces stay registered without a provider and answer an actionable `gateway/internal`, matching the TS controllers. A `settings` spine row (FileSettingsProvider over `<dshHome>/settings.json` — the port is JSON-only, so the default document deviates from the TS `settings.yaml`) joins the dsh-base bundle, and the seam exposes one public wire-value converter (`SettingsWireValues.FromJsonElement`) so the host does not duplicate the seam's JSON-value representation.

## Consequence

The wave-1 remote catalog now covers session, settings, credentials, and workspace; the host suite grew from 26 to 44 (settings 6, credentials 7, workspace 5), all green, with the full solution building at 0 errors and the settings/credentials/workspace seam suites unchanged and green. `dsh web` profiles now expose all three namespaces on the gateway. `settings/mutate` path ops landed afterwards on the same seam (ordered set/unset edits over the serialized write chain, later ops observing earlier ones, root-path semantics, and the redacted-view secret-field case), bringing the settings suite to 16 and the host suite to 62. Still deferred and named in the port spec and sources: the settings document/preset openers, the workspace registry methods (rename/delete/insertBefore/insertSessionBefore/archiveSession/follow), directoryPicker (stubbed with `directory-picker/unavailable`), the Roslyn source generator, the ui-* plugin set, locale dictionaries, and the trust/auth fence beyond the loopback token.

## Alternatives considered

- Emitting the schema as its TypeScript-like type string instead of the `toJSON()` refs envelope: the string is a display artifact and cannot rehydrate; the envelope is the documented wire form the client rehydrates with `new Schema(json)`.
- Hard-requiring the settings/credentials/workspace providers in the `rpc` spine row: profiles without those rows would fail boot; the TS controllers keep the namespaces registered and answer the missing-provider diagnostic, so the C# handlers resolve the provider at invoke time instead.
- Duplicating the JsonElement-to-seam-value conversion in the host: the conversion is the seam's documented JSON-value representation, so a single public converter on the seam is the honest boundary.
