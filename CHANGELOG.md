# Changelog

All notable changes to ReactiveBlazor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.6.0] — 2026-06-20

### Added
- **Declarative authorization** — ReactiveBlazor now honors the framework's standard `[Authorize]` and `[AllowAnonymous]` attributes on both `[ReactiveAction]` methods and `ReactiveComponent` subclasses. Evaluated through the app's own `IAuthorizationService` / `IAuthorizationPolicyProvider`, so roles, named policies, authentication schemes, and custom requirements behave exactly as in MVC/SignalR. No new attributes are introduced; the library leverages the existing ASP.NET Core authorization pipeline and never replaces it.
  - **Action-level**: authorized on the server before the action runs. Unauthenticated → `401`, authenticated-but-denied → `403` (matching ASP.NET semantics). Authorization **fails closed** — a missing policy or throwing handler denies access without leaking a `500`.
  - **Component-level**: enforced on *every* render path (initial SSR, action dispatch, and signal-driven sibling refresh). A denied component renders an empty boundary with **no state token and no content**, and its actions never run. Unauthorized signal-refreshed siblings are omitted from the response entirely.
  - **`<AuthorizeView>` / cascading auth state** work inside reactive components during dispatch — the current user is seeded from `HttpContext.User` into the dispatch renderer.
- **Session-expiry handling** — when authentication lapses while idle, the next action/poll returns `401`; the client runtime stops polling and full-page-reloads so the app's normal login redirect (with `returnUrl`) fires. Controlled by the new `ReactiveOptions.ReloadOnUnauthorized` option (default `true`); set to `false` to handle `401` via the `reactive:error` event instead.
- **Cross-user state-token binding** — new opt-in `ReactiveOptions.BindStateToUser` (default `false`) binds each encrypted state token to the user it was issued to (a hash of their stable id claim). A token replayed under a different identity silently resets the component to default state instead of loading the original user's data, closing a cross-user state-data confidentiality gap on shared/kiosk machines, screen shares, or support attachments. Not an authorization control (dispatches are already re-authorized against the live user); overhead is one short hash computed once per request plus 16 bytes per token.

### Fixed
- **Authorization-suppressed components no longer break sibling dispatches** — the client runtime skips boundaries rendered without a state token (denied components), so clicking any action on a page containing a denied component no longer fails the whole batch with a `400 "State token is missing"`.
- **Idle-expiry returns `401`, not `400`** — when an antiforgery token (bound to the prior user) fails validation on a now-unauthenticated request in an auth-enabled app, the endpoint returns `401` so the client reloads to login, instead of a dead-end `400`.

## [1.5.0] — 2026-06-20

### Added
- **Polling (periodic auto-refresh)** — components can refresh themselves on a timer with no user interaction. Set `PollAction` and `PollInterval` (ms) on `<ReactiveRoot>`; each tick fires the named action through the normal dispatch pipeline, so polling reuses request queuing, OOB signal fan-out, and DOM morphing. Bind `PollInterval` to a state property to start/stop or retune polling at runtime — when it returns to `0` the timer is cleared automatically on the next morph. Optional `PollArgs` (JSON array string) passes arguments to the poll action.
  - Client enforces a **250ms minimum interval**, uses `"latest"` queue semantics (ticks never pile up), **skips ticks while a dispatch is in flight**, and **pauses polling while the browser tab is hidden** (resuming on focus).
  - New `/polling` demo: a live metrics dashboard with a Start/Stop toggle and an OOB activity-log subscriber updated by each tick.

## [1.0.0] — 2026-06-04

### Added
- **Multi-Component OOB Updates** — Automatic out-of-band updates. The server batch renders the target component and all sibling components on the page, and the client runtime morphs all matching elements dynamically.
- **NavLink Isolation Support** — Resolved Isolated rendering context error by injecting a custom request-aware `NavigationManager` wrapper during dispatches.
- **Robustness Check** — Added HTTP 400 Bad Request error response if the client sends a null or empty state token, preventing HTTP 500 crashes.
- **Render Order Sequencing** — Ensured actions run and mutate shared state/services before sibling components are rendered.
- **Interactive Dashboard Demos** — Refactored the entire demo into a responsive sidebar+header dashboard featuring individual pages for counter, two-way config builder, task manager, debounced live search, e-commerce catalog, and cart.

### Changed
- Changed JSON dispatch payload and response schema to support multi-component batches.
- Updated integration tests to assert against the new JSON payload schema.

## [0.2.0] — 2026-06-03

### Added
- **State token expiration** — tokens now include a UTC timestamp and are rejected after `StateTokenLifetime` (default: 24 hours) to prevent replay attacks.
- **Pre-decryption payload size check** — `MaxTokenBytes` option (default: 256 KB) rejects oversized encrypted payloads before any cryptographic work.
- **Cancellation support** — the dispatch endpoint now respects `HttpContext.RequestAborted`.
- **Frozen component registry** — `ReactiveComponentRegistry` is now frozen after service registration, making it immutable and thread-safe for concurrent reads.
- **Default `reactive-loading` CSS** — loading spinner styles are now included in the RCL.
- Multi-target: `net8.0`, `net9.0`, `net10.0`.
- `LICENSE` file (MIT).
- This `CHANGELOG.md`.

### Changed
- **Breaking**: State token binary format now includes an 8-byte timestamp. Tokens generated by v0.1.0 will fail decryption and reset to default state (graceful degradation).
- `AddReactiveComponents()` no longer calls `AddDataProtection()` internally — consumers must register Data Protection themselves.
- Error messages from the dispatch endpoint no longer leak component type names.

### Removed
- Empty `ReactiveBlazor.Generators` project.
- Duplicate Idiomorph CDN reference in demo app (the RCL bundles Idiomorph).

## [0.1.0] — 2026-05-01

### Added
- Initial release.
- `ReactiveComponent` base class with state serialization, action dispatch, and two-way binding.
- `ReactiveRoot` wrapper component with signed/encrypted state envelope.
- `ReactiveScripts` component for CSRF and script injection.
- `ReactiveActionAttribute` and `ReactiveIgnoreAttribute`.
- Client-side JS runtime (~220 lines) with Idiomorph DOM morphing.
- Request queuing with "latest" and "all" modes.
- Debounce support via `data-debounce`.
- Redirect support via `RedirectUrl`.
- Configurable `MaxStateBytes` and `DispatchPath`.
