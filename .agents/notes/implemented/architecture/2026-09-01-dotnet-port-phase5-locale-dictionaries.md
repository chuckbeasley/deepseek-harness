# Agent Note: .NET port Phase 5 wave 1 — bilingual shell copy and the locale handoff

Status: implemented

## Problem

The shell carried one English dictionary (`WebLocale.English`) and no selection machinery:
the locale-selection machinery and further dictionaries were deferred, so a non-English
browser always saw English copy.

## Decision

- `WebLocale` is now bilingual — English and Simplified Chinese, the repo's bilingual pair —
  with the fallback chain active dictionary → English → key (a missing key renders as itself).
  `Negotiate(Accept-Language)` picks the first language whose primary subtag matches a shipped
  locale, in header order, defaulting to English.
- The active locale is pinned per request by a small middleware in `MapDshApp`
  (`HttpContext.Items["dsh.locale"]`), and the page carries it across the prerender → circuit
  boundary through `PersistentComponentState`: the prerender registers an `OnPersisting`
  callback, the interactive circuit takes the value back, so both renders agree. The scoped
  `WebLocale` the components inject is a facade over the per-scope `LocaleScope`, resolving the
  language on every `T()` call — the page pins the language in `OnInitialized`, which runs after
  the component's injected services were resolved.

## Consequence

The shell speaks the browser's language end to end: the headless-Chrome smoke now requests
`Accept-Language: zh-CN`, proves the Chinese shell prerenders, proves the zh copy survives the
interactive attach (the PersistentComponentState handoff), and still completes the full mock
turn. 99 host suites (5 new locale suites including a real-prerender test over Kestrel) green;
full solution builds at 0 errors. Two implementation facts worth keeping: Razor HTML-encodes
non-ASCII copy in the prerendered HTML (assertions match the encoded forms), and
`PersistAsJson` is only legal inside an `OnPersisting` callback.

## Alternatives considered

- Resolving the locale client-side via a JS initializer (navigator.languages): the server
  renders the copy, so the server must know the language; the Accept-Language header already
  carries it on every request, including the circuit handshake.
- A singleton `WebLocale` switched by a static current locale: per-scope state must never be
  static — the scoped facade keeps concurrent circuits independent.
