# Agent Note: .NET port Phase 5 wave 1 — auth beyond the loopback fence

Status: implemented

## Problem

The fence bound every request to the loopback authority (documented reduction): a deployment
serving the GUI on a LAN IP or a declared hostname had no way to open the trust fence, and the
preset seam carried no trust classification, so the settings opener could not refuse a
deployment-shipped preset (`agent-preset/read-only` was deferred with the shipped-preset
concept).

## Decision

- `WebAuthFence` now takes a `trustedHosts` list. Every configured entry is asserted as a bare
  `host[:port]` authority at host start (`AssertTrustedAuthority`, the TS
  `assertTrustedAuthority`): a path, userinfo, whitespace, a dangling or zero-padded port, or a
  non-canonical host spelling fails the boot loudly instead of silently changing the grant.
  Matching follows the TS rules: a port-less entry trusts the hostname on any port (the
  LAN-serving shape), an explicit entry compares WHATWG hosts with the http default port (80)
  dropped on both sides, and IPv6 literals are bracketed on both sides. The webHost row takes
  `trustedHosts` from its config, and a non-string list element fails loud (the TS zod array).
- The preset seam gains `PresetTrust` (`System`/`User`, the TS `PresetTrust`): every roster row
  and resolved preset carries the trust of the root it was discovered under (the provider takes
  the root trust; the preset row takes a `trust` config). The settings opener refuses a non-user
  preset with `agent-preset/read-only {agentPreset, reason: "it ships with the deployment"}`,
  the exact TS refusal. The default spine root remains user-authored; a deployment mounts a
  system root explicitly — the port ships no bundled presets to seed one.

## Consequence

A deployment can serve the GUI beyond loopback with the same confused-deputy defenses (DNS
rebinding Host fence, cross-site marker, Origin equality) and the same loud-failure surface for
misconfigured grants; preset authoring surfaces can now enforce the system/user boundary. 94
host suites (7 new fence suites incl. a full trusted-authority cookie round trip) and 15 preset
suites green; full solution builds at 0 errors.

## Alternatives considered

- Deriving LAN IP literals automatically when binding `0.0.0.0` (the TS `resolveLanTrust`):
  the port does not support non-loopback binding yet, so the declared-list surface is the whole
  story; the derivation joins when a bind host lands.
- A multi-root preset roster with precedence (the full TS `roots` model): the deferral names
  trust classification, and the port's seam is single-root by design; the roster stays a
  documented reduction.
