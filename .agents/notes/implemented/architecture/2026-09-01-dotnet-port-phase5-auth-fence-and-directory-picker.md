# Agent Note: .NET port Phase 5 wave 1 — the loopback auth fence and the directoryPicker stub land

Status: implemented

## Problem

The Phase 5 port spec (§1.6) requires the auth-fence shape "in place" for Wave 1 — 403 for an
untrusted Host/Origin, 401 for a missing or invalid browser session, index authorization through
the process-token exchange or persistent cookie, and upgrades rejected with plain HTTP 401/403 —
while the full hardening stays deferred. The port also records the directoryPicker namespace as a
stub answering `directory-picker/unavailable` because native directory pickers are deferred. Until
this round, the C# gateway, hub, and mux served anyone who could reach the port, with no fence.

## Decision

A `WebAuthFence` in `Dsh.Web.Host` ports the TS `browser-auth` + `api-request-trust` pair
faithfully at loopback scope:

- **Trust fence** (`IsTrustedRequest`): the Host header must name the loopback authority
  (`localhost`, `[::1]`, or any 127/8 IPv4 — the TS predicate), an explicit `sec-fetch-site:
  cross-site` marker is refused, and a present Origin must equal the Host authority through URL
  normalization (`null` origins refused). No `trustedHosts` deployment authorities are ported:
  loopback binding is the Wave-1 surface (documented reduction).
- **Browser-session cookie** (`IsAuthenticated`): an authority-bound HMAC-SHA256-signed cookie
  (`dsh-auth-<base64url(sha256(authority))> = v1.<payload>.<signature>`, 24 h max age, HttpOnly,
  SameSite=Strict), with the TS expiry windows and timing-safe comparisons. The signing secret is
  per host instance — the TS persists it in a credential record so cookies survive host restarts;
  the C# credentials seam has no record API, so a restart invalidates every cookie and the
  operator reopens the URL `dsh web` prints (documented reduction).
- **Launch-token exchange** (`AuthorizeIndex`): `GET /?token=<launch>` mints the cookie and
  redirects 303 to clean `/`; a valid cookie serves the index; everything else receives the TS 401
  text. The middleware gates the index, the gateway (`/api`), the hub, the mux (upgrades rejected
  with plain HTTP 401/403 before the WebSocket accept), and the Blazor circuit; static assets stay
  open (they carry no secrets, matching the TS where only the index and API surfaces are gated).
  `dsh web` prints the authenticated URL at boot.

The `directoryPicker` stub registers all three verbs (`pick` needs the native capability,
`list`/`createDirectory` the browse capability) and answers `directory-picker/unavailable` with the
capability detail; the create name grammar (single non-blank segment, no `/` or `\`) is still
enforced before the capability refusal, mirroring the TS validation order.

## Consequence

The gateway, hub, mux, and shell circuit are now behind the fence on `dsh web`; the host suite
grew from 44 to 59 (10 fence cases over a real Kestrel host — 401/403 vocabulary, exchange,
authority-bound and tampered cookies, hub and mux gating — plus 4 directoryPicker cases), all
green, with the full solution building at 0 errors. An HTTP smoke over the real profile proves the
boot-printed URL, the 401/403 fence, the exchange, and gated API round-trips. The `$events`
waterfall/`$events/result` settlement machinery stays deferred with a named reason: the port emits
no waterfall-mode events yet (approval/request and user-questions/request are not ported), so the
machinery would be dead code until a waterfall event exists.

## Alternatives considered

- Persisting the signing secret through the credentials managed store: the C# credentials seam
  has no record/modifyRecord API (the authorization record half is flow-shaped), so the honest
  loopback variant keeps the secret per instance and documents the restart behavior.
- Defaulting the fence off with the web profile opting in: the product surface would rely on a
  bundle config flag for security; the fence defaults on and the carrier test hosts opt out
  explicitly.
- Porting the `$events/result` settlement now with a synthetic waterfall source: no real C#
  event would drive it, so the machinery would be untestable-in-production dead code; it lands
  with the first waterfall event (the ask-user/approval surface).
