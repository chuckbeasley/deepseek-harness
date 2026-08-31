# Agent Note: .NET port Phase 5 wave 1 — the in-shell approval UI, the settings/plan pages, and LAN trust

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase5-approval-ui-pages-lan-trust.zh.md)

## Problem

Three follow-up surfaces were left surface-gated after the Phase-5 deferrals: the shell had no in-process approval/question UI (the interaction seam was only answerable by remote `$events` clients), the settings and plan seams had no GUI pages (the remaining ui-* packages awaited surfaces), and the fence could not serve an all-interfaces bind (LAN literals were never derived).

## Decision

- `Dsh.Ui.Approval` (the ui-approval port) renders one component into the shell's   `shell.overlay` slot that answers the interaction waterfalls in-process: the approve/deny   dialog for `approval/request`, a text dialog for `user-questions/ask`, and the   tools/pre-execute adapter that routes every shell tool call through the approval seam while   the shell is live. Everything dies with the circuit; the TUI keeps its own dialog on its own   profile and the headless profile stays unapproved.
- `Dsh.Ui.Settings` and `Dsh.Ui.Plan` add routed pages (`/settings` shows the settings   document path and the redacted namespace catalog; `/plan` shows the selected session's plan   fold, following the shared selection and the store live) plus sidebar nav links. The static   Router's `AdditionalAssemblies` alone does not route these pages: in .NET 10 the SSR router   matches through the endpoint-level route data, so the page assemblies must be registered on   the RazorComponents endpoint at map time (`AddAdditionalAssemblies`). That forced the web   bundle ordering webCore (which creates the slot and page-assembly registries) before the   ui-* rows and webHost (which maps) last.
- The fence's LAN trust (the TS `resolveLanTrust`): binding the all-interfaces host (`0.0.0.0`)   derives the machine's non-loopback IPv4 literals as port-less trusted authorities — an   IP-literal Host is safe on any port and the bound port is unknowable before bind — with the   explicitly configured entries following, in config order.

## Consequences

The shell answers its own approvals and questions, the settings and plan seams have GUI surfaces, and an all-interfaces bind serves LAN clients through the same fence. 113 host suites green (2 new: the LAN derivation and the composed-shell pages over a real Kestrel host), 41 console suites total green, full solution 0 errors, and the headless-Chrome smoke drives the whole loop end to end: submit → the approval dialog appears for the mock turn's tool call → Approve → the reply renders.

## Alternatives considered

- Routing the ui-* pages through the static Router's AdditionalAssemblies alone: the endpoint   never saw the assemblies, so `/settings` and `/plan` 404'd — the map-time registration is   mandatory in the .NET 10 SSR model.
- A settings page that edits namespaces: the page reads the redacted catalog and document   path; editing stays on the remote/CLI surfaces (documented), matching the port's   read-only-first stance until a write UI has a consumer need.
