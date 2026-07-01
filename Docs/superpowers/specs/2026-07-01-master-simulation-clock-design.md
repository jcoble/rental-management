# Master Simulation Clock — Design Spec

- **Date:** 2026-07-01
- **Status:** Approved-with-changes (design) — review incorporated; implementation pending
- **Notion task:** TSK-615
- **Branch:** `tsk-615-master-simulation-clock`
- **Scope:** RentalCommand (this repo). EdiPlatform port tracked separately.
- **Review:** adversarial code-grounded review folded in (see §14). Verdict was
  Approve-with-changes; the three must-fixes (worker-trigger feasibility, web gating/cold-start
  sync, timezone control surface) are now the baseline design below.

## 1. Context & goal

True-to-life, **web-only** end-to-end test of Rental Command: a landlord comes in cold —
registers, confirms email, onboards ~2 years of history from scans, then runs ~1 year of
day-to-day operations — while we **advance simulated time** and verify scheduled jobs, reminders,
the lease lifecycle, and **year-end reports** all behave and reconcile against an independently
kept ground-truth ledger.

This requires **one controllable "now"** shared by the API, the Engine workers, and the web UI.
Today there is no such control — ~349 backend and ~123 web call sites read the machine clock
directly.

**Goal:** one controllable clock, honored everywhere business time matters, changeable on demand
in dev/test, completely inert (real time) in production.

## 2. Non-goals

- **Not** changing the OS or Postgres clock. The app never asks Postgres for the time (zero
  DB-side time defaults — verified), so an app-layer clock captures 100% of domain time.
- **Not** virtualizing infrastructure time: JWT/refresh lifetimes, OAuth token-expiry checks, TLS,
  HTTP caches, rate limiters, outbox retry backoff, `Stopwatch`/perf, log timestamps stay real.
- **Not** a production feature. The whole simulation surface is compiled/served only in non-prod.
- **Mobile (Flutter):** clock swap mirrored for parity later; off the web-only test path, out of
  scope here.

## 3. Current state (grounded in the code)

- **.NET (`net10.0`):** ~349 wall-clock sites across Core/Data/Api/Engine; no clock abstraction.
  Hotspots: `AccountingImportService` (26), `AccountingConnectionService` (13 — **but includes
  OAuth token-expiry checks that must stay real**, e.g. `AccountingConnectionService.cs:164,362`),
  `ScanService` (12), `LeaseService` (10). **Note:** hotspot counts are per-file totals and
  include keep-real infra sites — the sweep classifies per-site (§5.6), it is not a blanket swap.
- **Data layer:** audit timestamps are set in `RentalCommand.Data/Auditing/AuditSaveChangesInterceptor.cs:235`
  (`var now = DateTime.UtcNow`). This is the "story of record" (surfaces in `RecordHistory.svelte`)
  and **must be virtualized too** (§5.6), which means giving `RentalCommand.Data` a `TimeProvider`.
- **Engine:** 12+ wall-clock workers. Their automation services
  (`RentChargeService`, `LateFeeService`, `LeaseExpiryReminderService`, `AutopayChargeService`,
  `NoticeDraftGenerationService`, `DebtServiceService`, `RecurringExpenseGenerationService`,
  `RecurringMaintenanceService`, `DailyBriefingDeliveryService`) live in `RentalCommand.Engine.Services`
  and are registered **only** in the Engine. All are parameterless `Task<int> …Async(CancellationToken)`
  and **idempotent** (RentCharge/LateFee dedupe on `(LeaseId, PeriodKey)` + partial unique index;
  LeaseExpiry latches on `ExpiryReminderSentAt`). `IDailyBriefingDeliveryService.EnqueueDueAsync(DateTime? utcNow=null,…)`
  already exposes an injectable "now".
- **Project dependency direction (verified):** `Engine → Api → {Core, Data}`. The **Api references
  only Core + Data**; the **Engine references the Api**. So the API **cannot** reference the Engine
  (build cycle), and the Engine has **no HTTP port** (`Host.CreateApplicationBuilder`). This shapes
  the worker-trigger design (§6).
- **Business-day timezone (verified):** rollover logic reads **`App:TimeZone` config**, not any
  provider zone — `RentChargeService.cs:51`, `LateFeeService.cs:56`, `RecurringMaintenanceService.cs:47`,
  `DebtServiceService.cs:39`, `RecurringExpenseGenerationService.cs:40`. Reports bucket in **pure UTC**
  (`ReportsService.cs:207` etc.). This shapes the timezone control (§8).
- **Web:** ~123 `new Date()/Date.now()` sites (~40 files), hub `web/src/lib/utils/date.ts`. **All**
  server-side date usage is auth/cookie/cache timing → stays real. SSR is on app-wide
  (`+layout.ts: ssr = true`).
- **No Postgres `LISTEN/NOTIFY` anywhere** (verified). Cross-process signaling today is the SignalR
  hub (auth-gated, portfolio/user-group scoped — **no `Clients.All`**) + a DB outbox with polling.
- **Zero DB-side time defaults (verified):** no `defaultValueSql`/`now()`/`CURRENT_TIMESTAMP` on any
  timestamp column; the accounting view computes no server-side time.
- **Precedent:** EdiPlatform uses the `TimeProvider ?? TimeProvider.System` idiom; web uses
  `PUBLIC_*` env flags (`PUBLIC_GOOGLE_CLIENT_ID`).

## 4. Architecture overview

Three tiers, one source of truth, poll-based cross-process agreement.

```
[ dev control (API) ] ──▶ SimulationClock (1-row table) ◀── polled ~1s by both processes
        │                          │
        │                ┌─────────┴─────────┐
        ▼                ▼                   ▼
  GET /dev/clock     API process         Engine process
  (anonymous, RO)    TimeProvider=SimTP  TimeProvider=SimTP
  set/advance/...    (+ IAppTimeZone)    (+ IAppTimeZone)
  (admin)                 │                   │
  POST /dev/workers/* ────┼── SimWorkerCommand table ──▶ dev-only SimWorkerCommandWorker
  (enqueue command)       │   (API enqueues; Engine     runs the service in Engine's
                          │    executes; result polled)  native admin/RLS+system-actor ctx
                          ▼
   Web browser: inline app.html Date shim; offset polled from anonymous GET /dev/clock
```

- **Backend source of truth:** injected `TimeProvider`. Prod = `TimeProvider.System`; non-prod =
  `SimulationTimeProvider` reading the shared `SimulationClock` row.
- **Cross-process:** API and Engine each **poll** the one-row table (~1s cache). No NOTIFY, no
  backend SignalR for the clock.
- **Worker triggers:** because the API can't call Engine services, the API **enqueues a command
  row**; a dev-only Engine worker **executes** it in the Engine's correct context and writes the
  result back; the API returns it (long-poll).
- **Web:** browser mirrors sim-now via an inline `Date` shim whose offset is polled from an
  anonymous dev endpoint (works pre-login). SSR stays real.

## 5. Backend design

### 5.1 The abstraction
`System.TimeProvider`, with Core extension methods so the sweep is mechanical:

```csharp
// RentalCommand.Core/Time/TimeProviderExtensions.cs
public static DateTime UtcNow(this TimeProvider tp) => tp.GetUtcNow().UtcDateTime;                   // was DateTime.UtcNow
public static DateOnly TodayUtc(this TimeProvider tp) => DateOnly.FromDateTime(tp.GetUtcNow().UtcDateTime); // was DateTime.Today (UTC)
public static DateTimeOffset NowOffset(this TimeProvider tp) => tp.GetUtcNow();                      // was DateTimeOffset.UtcNow
```

### 5.2 `SimulationTimeProvider` (non-prod only)

```csharp
public sealed class SimulationTimeProvider(IClockStateProvider state) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => state.Current.Mode switch
    {
        ClockMode.Real   => base.GetUtcNow(),
        ClockMode.Frozen => state.Current.SimAnchorUtc,
        ClockMode.Offset => base.GetUtcNow() + (state.Current.SimAnchorUtc - state.Current.RealAnchorUtc),
        _                => base.GetUtcNow(),
    };
    // LocalTimeZone override is kept but is NOT the business-tz lever (see §8) — business-day
    // logic reads App:TimeZone. GetTimestamp()/CreateTimer() are NOT overridden (real timing/timers;
    // verified nothing calls TimeProvider.CreateTimer today).
}
```

### 5.3 Shared state: `SimulationClock` (one row)

`Id`(=1) · `Mode`(Real|Frozen|Offset) · `SimAnchorUtc` · `RealAnchorUtc` · `TimeZoneId`(business-tz
override, nullable) · `UpdatedAtRealUtc`. Seed `Mode=Real`.
**RLS:** the table is global (no `PortfolioId`) and MUST be **excluded from the `tenant_isolation`
policy set** so a portfolio-scoped session can still read it. Harmless if shipped-but-unread in prod.

### 5.4 Cheap reads + invalidation (poll-only)
`GetUtcNow()` is hot + synchronous → never hits the DB. `IClockStateProvider` holds `ClockState`
in memory, refreshed by a **~1s poll** hosted service in both API and Engine (**no LISTEN/NOTIFY** —
none exists in the codebase and it's unjustified for a dev tool). The worker-command executor
forces a state refresh before running; the replay driver **freezes** the clock while firing workers,
so the 1s staleness never affects determinism.

### 5.5 DI wiring & gating
- Config flag `Simulation:Enabled` (default **false**; true only in Development/test).
- **Prod:** `AddSingleton(TimeProvider.System)`; no dev routes; clock row never read; no command worker.
- **Non-prod:** register `IClockStateProvider` + 1s refresher + `AddSingleton<TimeProvider, SimulationTimeProvider>()`
  + `IAppTimeZoneProvider` (sim override) + (Engine only) the `SimWorkerCommandWorker`; map dev routes.
- One shared `AddSimulationClock(config)` extension called by both API and Engine startup.

### 5.6 Classification rule (which sites to virtualize) + mechanics
- **Virtualize:** business time — stored on a business record that appears in reports; drives a
  scheduled job's due/period logic; lease/payment/expense/notice/inspection dates; due/overdue/
  period-boundary computations; **and the Data audit-interceptor timestamp** (`AuditSaveChangesInterceptor.cs:235`)
  + the `NotificationHub` alert timestamp, so a sim-dated record gets a sim-dated audit/alert.
- **Keep real (do NOT touch), even inside hotspot files:** JWT/refresh issue+expiry
  (`JwtTokenService`), OAuth token-expiry (`AccountingConnectionService.cs:164,362`), API-key/auth
  handlers, HTTP cache TTLs, rate limiting, outbox retry backoff, `Stopwatch`/perf, log/telemetry.
- **EF expression-tree mechanic (SF-D):** inside `ExecuteUpdate`/`SetProperty` or any query lambda,
  `_timeProvider.UtcNow()` cannot be translated — **hoist to a local first**
  (`var now = _timeProvider.UtcNow();`). Known sites: `ScanService.cs:1701`,
  `ScanProcessingWorker.cs:379`. (Only ~4 lambda-embedded sites exist; no domain
  `.Where(x => x < DateTime.UtcNow)` translates to SQL `now()` — the codebase already hoists.)

## 6. Control surface (dev-only)

Mapped only when `Simulation:Enabled`. Every clock mutation persists the row.

### 6.1 Clock (on the API — API references Data, can read/write the row)
- `GET  /api/v1/dev/clock` → `{ simNowUtc, mode, timeZoneId, offsetSeconds }` — **`AllowAnonymous`
  in non-prod** (`Simulation:Enabled` is the real gate) so the web can sync it **before login**
  (register / confirm-email pages).
- `POST /api/v1/dev/clock/set` `{ instantUtc | date, timeZoneId?, mode?="offset" }` · `advance`
  `{ days?,hours?,… }` · `freeze`/`unfreeze` · `reset` → back to `Real`. (admin-gated)

### 6.2 Worker triggers (command bridge — API cannot call Engine services)
Because `Engine → Api` (verified), the API cannot reference the Engine's automation services, and
the Engine has no HTTP port. So:
- API `POST /api/v1/dev/workers/{key}/run-once` and `POST /api/v1/dev/workers/run-due` **insert a
  row** into a dev-only `SimWorkerCommand` table (`key`, `requestedSimUtc`, `status`, `resultJson`).
- A dev-only **`SimWorkerCommandWorker`** in the Engine (fast poll, ~500ms, only when
  `Simulation:Enabled`) picks up pending commands and invokes the matching service **in the Engine's
  native context** — `EngineRlsInterceptor` pins `app.is_admin='true'` (correct cross-portfolio
  sweep) and `SystemCurrentActor` provides the audit actor — then writes `{created,skipped,error}`.
- The API endpoint **long-polls** the row (or `GET /api/v1/dev/workers/commands/{id}`) and returns
  the result, giving the driver effectively-synchronous behavior.
- `run-due` expands to the dependency order: **RentCharge → notices → LateFee → Autopay →
  DebtService/Recurring → Outbox.** Determinism: the driver **freezes** the clock before firing.
- **Rationale over relocating services into the API:** keeps the 9 production automation services
  and both process bootstraps **untouched** (no prod-behavior risk), reuses the existing DB-as-bridge
  + polling pattern, and inherits correct RLS + system audit actor for free. The only cost is a
  dev-only table + worker + brief poll latency, which is immaterial for a deliberate step-through driver.

## 7. Web design

### 7.1 Gating + loading
- Gate on **`PUBLIC_SIMULATION_ENABLED`** via `$env/static/public` (**not** `import.meta.env.DEV`,
  which strips the shim from any built image). Precedent: `PUBLIC_GOOGLE_CLIENT_ID`.
- **Load via an inline `<script>` in `web/src/app.html`** (same pattern as the existing splash
  loader) so `Date` is patched **truly before app boot** — a module import from a layout is not.
  The inline shim initializes its offset+mode from a `rc_sim` cookie (warm, synchronously correct on
  reload) else 0; overrides `Date.now()` and `new Date()` (no-arg) only; `new Date(...args)`,
  `Date.parse`, `Date.UTC`, and `instanceof` pass through.

### 7.2 Sync (works pre-login)
A tiny client bootstrap polls the **anonymous** `GET /api/v1/dev/clock` (~1s), updates the shim's
offset + mode, and writes the `rc_sim` cookie for the next warm load. Exposes
`window.__simClock = { sync(), set(iso), offsetMs, mode }` for the browser-driving tester. **No
SignalR dependency for the clock** (the hub is auth-gated/portfolio-scoped and wouldn't reach
cold-start pages).

### 7.3 Dev panel
`web/src/lib/dev/SimClockPanel.svelte` — floating widget (dev + `PUBLIC_SIMULATION_ENABLED`) with
set-date / +1d / +1w / +1m / freeze / reset calling the endpoints.

### 7.4 SSR stays real (+ hydration note)
No server patching. Because SSR is on app-wide, server-rendered relative-time (`daysFromTodayUtc`/
`isPastDueUtc`) renders on the real clock and the client re-renders on sim time — a **cosmetic
Svelte-5 hydration text patch** (no hard error; reactivity re-renders). Acceptable for a dev harness;
the sim run may set `ssr=false` (precedent: `apply/[token]/+page.ts`) to eliminate it entirely.

## 8. Timezone testing (corrected)
The meaningful lever is **`App:TimeZone`**, not `TimeProvider.LocalTimeZone`: the business-day
rollover in rent/late-fee/recurring/debt services reads `App:TimeZone` (verified), while **reports
bucket in pure UTC regardless**. So:
- The `SimulationClock.TimeZoneId` feeds an **`IAppTimeZoneProvider`** that returns the sim override
  (else the `App:TimeZone` config default); the 5 business-day services read it instead of raw
  `configuration["App:TimeZone"]` (part of the Phase-D sweep). Default **America/New_York** (matches
  the corpus ground truth).
- This lets us exercise real **business-day-boundary** bugs (e.g., a payment at 23:00 ET on the last
  grace day = next-day UTC — is it on-time?). Report month/day buckets are UTC by design and are
  asserted as such, not "fixed" by a tz.

## 9. Rollout plan (drives the implementation plan)
- **A — Infra:** Core `TimeProvider` extensions; `SimulationTimeProvider`; `SimulationClock` entity
  + migration (RLS-excluded, global); `IClockStateProvider` + **1s poll** refresher (no NOTIFY);
  `IAppTimeZoneProvider`; `AddSimulationClock` DI + gating; bind `System` in prod. Unit tests with
  `FakeTimeProvider`.
- **B — Control + command bridge:** anonymous `GET /dev/clock` + admin set/advance/freeze/reset;
  `SimWorkerCommand` table + migration; Engine `SimWorkerCommandWorker`; API enqueue+long-poll
  endpoints; `run-due` ordering. Integration tests (set→GET; advance→enqueue→Engine executes→result).
- **C — Web:** `PUBLIC_SIMULATION_ENABLED` gate; inline `app.html` shim + cookie; ~1s anonymous poll
  bootstrap; `window.__simClock`; dev panel.
- **D — The sweep:** replace domain wall-clock sites with injected `TimeProvider` across
  services → workers → **Data audit interceptor** → Core, in reviewed batches, applying §5.6 (skip
  auth/infra; hoist-to-local for EF lambdas). Route the 5 business-day services through
  `IAppTimeZoneProvider`. **Parallelize edits, sequence builds.**
- **E — Verify:** set date → API/Engine/web agree; freeze + `run-due` after a 35-day jump → correct
  rent charges/late fees/lease-expiry reminders + matching sim-dated audit rows; a business-day
  boundary (23:00 ET) case; prod build has no dev routes/shim/command-worker and uses `System`;
  reconcile a slice against SA's ledger.

## 10. Testing the clock
- **Unit:** `SimulationTimeProvider` offset/frozen math; `ClockState` poll refresh; endpoint
  set/advance/reset; extension helpers; `IAppTimeZoneProvider` override. Services under test use
  `FakeTimeProvider`.
- **Integration:** set→anonymous GET round-trip; advance→enqueue command→Engine `SimWorkerCommandWorker`
  executes→result row (cross-process); audit row carries sim time.
- **Guard test:** `Simulation:Enabled=false` → dev routes 404, no command worker, `TimeProvider.System`
  bound.

## 11. Safety
Whole surface behind `Simulation:Enabled` (false in prod) + admin auth on mutating endpoints (GET is
anonymous but read-only and non-prod-only). Prod: `System`, no routes, no shim, no command worker,
clock row unread. Never touches OS/Postgres clocks.

## 12. Open questions / risks
- **`DateTime.Now` (local) sites:** enumerate in the sweep; most are UTC-based already.
- **Mixed real/sim in one worker:** keep outbox retry backoff real, due-date sim; verify no
  comparison mixes them.
- **Third-party libs** calling `DateTime.UtcNow` internally — out of scope, correctly real.
- **Command-bridge latency / crash:** if the Engine command worker is down, the API long-poll times
  out → surface a clear error to the driver (don't hang).

## 13. Acceptance criteria
1. `POST /dev/clock/set {date}` → API responses, Engine worker logic, and web display all report that date.
2. Freeze + advance 35 days + `workers/run-due` → exactly the expected rent charges + late fees +
   lease-expiry reminders, **each with a sim-dated audit row**.
3. A **business-day boundary** case (payment 23:00 America/New_York on the last grace day) is treated
   correctly by late-fee logic under the `App:TimeZone` override. (Reports remain UTC-bucketed by design.)
4. Prod build: dev routes/shim/command-worker absent, `TimeProvider.System` bound, existing behavior
   unchanged; cold-start (register/confirm-email) still syncs sim time via the anonymous GET.
5. Reconciliation: a sampled month of the sim run matches SA's ground-truth ledger.

## 14. Review incorporated (2026-07-01)
Adversarial review verdict **Approve-with-changes**; all folded into the baseline above:
- **MF-1** worker triggers can't run on the API (Engine→API cycle; services Engine-only; Engine has
  no HTTP) → **command-bridge** (§6.2), keeping prod services untouched + correct RLS/audit.
- **MF-2** web gating/sync for cold-start → `PUBLIC_SIMULATION_ENABLED`, inline `app.html` shim +
  cookie, **anonymous** `GET /dev/clock` ~1s poll, no SignalR clock dependency (§7).
- **MF-3** timezone control targeted the wrong seam → override **`App:TimeZone`** via
  `IAppTimeZoneProvider`; reports are UTC-bucketed; AC#3 reframed to a business-day boundary (§8, §13).
- **SF-A** drop LISTEN/NOTIFY → **1s poll** (§5.4). **SF-B** virtualize the Data audit interceptor +
  hub alert timestamp (§5.6). **SF-C** don't virtualize token/expiry/cache sites inside hotspot
  files (§5.6). **SF-D** hoist-to-local for EF expression trees (§5.6). **RLS:** `SimulationClock`
  excluded from `tenant_isolation` (§5.3). Counts corrected to ~349/~123 (§3).
