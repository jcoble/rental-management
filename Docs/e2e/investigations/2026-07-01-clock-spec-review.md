# Adversarial Review — Master Simulation Clock Design Spec (2026-07-01)

**Source:** SpecReviewer agent (code-grounded adversarial review). **Verdict:** Approve-with-changes.
All findings were folded into the spec revision (`Docs/superpowers/specs/2026-07-01-master-simulation-clock-design.md`, §14) and the implementation plan. Preserved here verbatim for the record.

## Verdict: Approve-with-changes

Core architecture is sound and well-matched to the codebase (injected `TimeProvider`, shared
one-row `SimulationClock`, non-prod gating, global `Date` shim). Load-bearing claims checked out
(zero DB-side time defaults; services invocable + idempotent; auth stays real; server-side web
dates are all infra). But three must-fixes would make the plan wrong as written.

## Must-fix

### MF-1 — Worker-trigger endpoints infeasible on the API (circular ref; services Engine-only)
- `RentalCommand.Api.csproj` references only Core + Data (`:28-29`); `RentalCommand.Engine.csproj`
  references the API (`:33`). Direction is **Engine → API**, so the API cannot reference the Engine.
- Every trigger service (`RentChargeService`, `LateFeeService`, `LeaseExpiryReminderService`,
  `AutopayChargeService`, `NoticeDraftGenerationService`, `DebtServiceService`,
  `RecurringExpenseGenerationService`, `RecurringMaintenanceService`,
  `DailyBriefingDeliveryService`) lives in `Engine.Services`, registered only in
  `Engine/Program.cs:111-136`. The API can't resolve or name them.
- The Engine can't host the endpoint either — no HTTP port (`Host.CreateApplicationBuilder`).
- **Secondary trap:** these services sweep ALL portfolios with no filter (`RentChargeService.cs:64`).
  In the Engine that's fine — `EngineRlsInterceptor` pins `app.is_admin='true'` (`:21`). Run in an
  API request scope, the API's `RlsConnectionInterceptor` sets `is_admin` from the caller
  (`:90` — true only for role Admin or a no-portfolio principal); a dev admin with a `portfolioId`
  claim + non-Admin role → the sweep is silently RLS-filtered to one portfolio.
- **Resolution taken:** dev-only **command-bridge** — API enqueues a `SimWorkerCommand`; a dev-only
  Engine worker executes it in the Engine's native admin/RLS + `SystemCurrentActor` context; API
  long-polls the result. Keeps prod services + both bootstraps untouched.

### MF-2 — Web shim gating/sync won't cover the built image or the pre-login cold-start
- `import.meta.env.DEV` is a build-time constant → strips the shim from any built image. **Fix:** gate
  on `PUBLIC_SIMULATION_ENABLED` via `$env/static/public` (precedent `PUBLIC_GOOGLE_CLIENT_ID` at
  `register/+page.server.ts:19`, `login/+page.server.ts:50`).
- Sync path was auth-gated + post-login: SignalR needs a JWT (`signalr.ts:78`), starts only from
  `(protected)`/`(portal)`, every broadcast is group-scoped (`DataUpdateService.cs:38,66`;
  `NotificationHub.cs:109,123,137`) — **no `Clients.All`**; and `GET /dev/clock` required admin. So
  register/confirm-email pages couldn't sync. **Fix:** make `GET /dev/clock` `AllowAnonymous` in
  non-prod; shim **polls** the anonymous GET ~1s.
- "Loaded before app boot" via a module import is false, and SSR is on app-wide
  (`+layout.ts: ssr = true`) → relative-time renders (`daysFromTodayUtc`) mismatch on hydration.
  Svelte 5 patches text mismatches (no hard error). **Fix:** inline `<script>` in `app.html` (like the
  splash loader `:110-227`); optionally `ssr=false` for the sim run.

### MF-3 — §8 timezone control wired to code that never reads `TimeProvider.LocalTimeZone`
- Business-day rollover reads **`App:TimeZone`** config: `RentChargeService.cs:51-54`,
  `LateFeeService.cs:56-59`, `RecurringMaintenanceService.cs:47-50`, `DebtServiceService.cs:39-42`,
  `RecurringExpenseGenerationService.cs:40-43`, `DailyBriefingDeliveryService.cs:156`.
- Reports bucket in **pure UTC** (`ReportsService.cs:207`, `:597`, `:620`, `:663-667`).
- So a sim `LocalTimeZone` moves nothing §8/AC#3 targets. **Fix:** expose an **`App:TimeZone`
  override** (via `IAppTimeZoneProvider`); reframe AC#3 to a **business-day boundary** case; note
  reports are UTC-bucketed by design.

## Should-fix
- **SF-A — drop Postgres LISTEN/NOTIFY; the 1s poll suffices.** No LISTEN/NOTIFY exists anywhere;
  cross-process today is polling (`OutboxDispatchWorker.cs:41` 10s). Make Phase A poll-only; the
  trigger can force a refresh; removes the backend `clock-changed` SignalR path (keep SignalR for
  the browser only — later dropped entirely for the clock in favor of the anonymous poll).
- **SF-B — sweep must include the Data audit interceptor.** `AuditSaveChangesInterceptor.cs:235`
  (`var now = DateTime.UtcNow`) is omitted from the API/Engine hotspot list. Audit rows surface in
  `RecordHistory.svelte` and are the story-of-record; leaving them real means a sim-2024 Payment gets
  a real-2026 audit row. Virtualize → inject `TimeProvider` into `RentalCommand.Data`. (Same for
  `NotificationHub.cs:138` alert timestamp.)
- **SF-C — hotspot counts include keep-real infra.** `AccountingConnectionService` (13) holds OAuth
  token-expiry (`:164,362` `stateRow.ExpiresAt < DateTime.UtcNow`) — must stay real. Caution against a
  blanket sweep of hotspot files.
- **SF-D — two EF `ExecuteUpdate`/`SetProperty` sites need hoist-to-local.** `ScanService.cs:1701`
  (`ConfirmedAt`), `ScanProcessingWorker.cs:379` (`ReviewedAt`): `_timeProvider.UtcNow()` inside an
  expression tree throws at runtime — capture `var now = …` first. Only ~4 lambda sites; no domain
  `.Where(… < DateTime.UtcNow)` translating to SQL `now()` (codebase already hoists, e.g.
  `RentChargeService.cs:62`).

## Verified-correct (worth keeping)
- **Zero DB-side time defaults** accurate (all `ValueGeneratedOnAdd` are int PKs;
  `vw_accounting_transactions` computes no server-side time).
- **Singleton `TimeProvider` into scoped/transient is safe** (never a captive dependency). Leaving
  `CreateTimer`/`GetTimestamp` real is safe — nothing calls `TimeProvider.CreateTimer`;
  `EngineWorkerBase.cs:163` uses BCL `Task.Delay`.
- **Auth stays real, not swept:** `JwtTokenService.cs:115-116`; bearer `ValidateLifetime`;
  `lib/server/jwt-claims.ts:67`, `lib/server/token-refresh.ts:73,103,112,147-196` (server-side, shim is client-only).
- **All 9 trigger services parameterless `Task<int> …Async(CancellationToken)` + idempotent**
  (RentCharge/LateFee dedupe on `(LeaseId, PeriodKey)` + partial unique index; LeaseExpiry latches on
  `ExpiryReminderSentAt`). AC#2 feasible once MF-1 resolved; one `run-due` after a big jump back-fills all missed periods.
- **New `SimulationClock` is global (no PortfolioId):** keep out of the `tenant_isolation` policy set so
  a portfolio-scoped session can read it; shipping unread in prod is harmless.

## SSR/CSR hydration (asked specifically)
Real but bounded: client shim makes `Date.now()`-based relative renders differ from the server's
real-clock render → Svelte-5 hydration **text** mismatch, which Svelte patches (unlike React's hard
error) and reactivity re-renders on sim time. Cosmetic for a dev harness; spec should acknowledge and
consider `ssr=false` for the sim run.

## LISTEN/NOTIFY (asked specifically)
Codebase uses **only** the SignalR hub (group-scoped) + a DB outbox with polling — **zero** Postgres
LISTEN/NOTIFY. Adding it is net-new infra, unjustified for a dev tool; the 1s poll suffices.
