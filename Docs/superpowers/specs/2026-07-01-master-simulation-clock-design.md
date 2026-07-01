# Master Simulation Clock — Design Spec

- **Date:** 2026-07-01
- **Status:** Approved (design) — implementation pending
- **Notion task:** TSK-615
- **Branch:** `tsk-615-master-simulation-clock`
- **Scope:** RentalCommand (this repo). EdiPlatform port tracked separately.

## 1. Context & goal

We are building a true-to-life, **web-only** end-to-end test of Rental Command: a landlord
comes in cold — registers, confirms email, onboards ~2 years of history from scans, then runs
~1 year of day-to-day operations — while we **advance simulated time** and verify scheduled
jobs, reminders, the lease lifecycle, and **year-end reports** all behave and reconcile against
an independently-kept ground-truth ledger.

This is impossible unless **"now" is a single, controllable value** shared by every part of the
system that reasons about time: the API, the Engine background workers, and the web UI. Today
there is no such control — **339** backend call sites and **113** web call sites read the
machine clock directly.

**Goal:** introduce one controllable clock, honored everywhere business time matters, changeable
on demand in dev/test, and completely inert (real time) in production.

## 2. Non-goals

- **Not** changing the OS clock or Postgres clock. The app already never asks Postgres for the
  time (zero DB-side time defaults), so an app-layer clock captures 100% of domain time.
- **Not** virtualizing infrastructure time: JWT/refresh-token lifetimes, TLS validity, HTTP
  caches, rate limiters, outbox retry backoff, `Stopwatch`/perf timing, and log timestamps stay
  on the real clock.
- **Not** a production feature. The entire simulation surface is compiled/served only in non-prod.
- **Mobile (Flutter):** the clock swap will be mirrored for parity later, but it is off the
  web-only test path and out of scope for this spec.

## 3. Current state (grounded in the code)

- **.NET (`net10.0`):** ~339 wall-clock sites (`DateTime.UtcNow/Now/Today`,
  `DateTimeOffset.UtcNow/Now`) across Core/Data/Api/Engine. No clock abstraction. Hotspots:
  `AccountingImportService` (26), `AccountingConnectionService` (13), `ScanService` (12),
  `LeaseService` (10), `ReportsService`/`BankingService`/`AccountingController` (8 each).
- **Engine:** 12+ wall-clock-driven workers — `RentChargeWorker`, `AutopayChargeWorker`,
  `LateFeeWorker`, `RecurringExpenseWorker`, `RecurringMaintenanceWorker`, `DebtServiceWorker`,
  `LeaseExpiryReminderWorker`, `NoticeDraftWorker`, `DailyBriefingDeliveryWorker`,
  `AccountingPullWorker`, `OutboxDispatchWorker`, `ScanProcessingWorker`. Each has a paired
  service (`IRentChargeService`, `ILateFeeService`, …) — the loop body is already callable,
  which makes on-demand triggering feasible.
- **Web:** 113 `new Date()/Date.now()` sites (~40 files), hub `web/src/lib/utils/date.ts`. **All**
  server-side date usage is auth/cookie/cache timing (`login/+page.server.ts`,
  `auth/google/callback/+server.ts`, `lib/server/jwt-claims.ts`, `lib/server/token-refresh.ts`)
  → stays real.
- **Existing helper:** `RentalCommand.Api/Services/Domain/DateTimeNormalization.cs` coerces
  `Kind`→UTC for Npgsql `timestamptz`. This **complements** (does not replace) the clock:
  normalization is about *Kind*; the clock is about *now*.
- **Precedent:** EdiPlatform already uses the `TimeProvider ?? TimeProvider.System` DI idiom
  (`EdiPlatform.Engine/Services/ApiChannel/Auth/OAuth2TokenCache.cs:34`) — the pattern to mirror.

## 4. Architecture overview

Three tiers, one source of truth.

```
[ dev control ] ──▶ SimulationClock (1-row table) ◀── read by both processes
     │                       │
     │            ┌──────────┴──────────┐
     ▼            ▼                     ▼
 /api/v1/dev/  API process         Engine process
   clock       TimeProvider        TimeProvider
   + workers   = SimulationTP      = SimulationTP
                    │                    │
                    ▼                    ▼
             domain reads sim-now   workers read sim-now;
             everywhere             triggered on demand
                    │
                    ▼  (SignalR "clock-changed")
                Web browser: global Date shim (offset synced)
```

- **Backend source of truth:** an injected `TimeProvider`. Prod binds `TimeProvider.System`;
  non-prod binds `SimulationTimeProvider`, which computes sim-now from the shared
  `SimulationClock` row.
- **Cross-process agreement:** API and Engine are separate OS processes; they agree because both
  read the same DB row (cached in-memory, invalidated by Postgres `NOTIFY` / SignalR).
- **Web:** the browser mirrors backend sim-now via a dev-only global `Date` shim; SSR stays real.

## 5. Backend design

### 5.1 The abstraction

Use `System.TimeProvider`. Add Core extension methods so the sweep is mechanical and call sites
stay terse:

```csharp
// RentalCommand.Core/Time/TimeProviderExtensions.cs
public static class TimeProviderExtensions
{
    public static DateTime UtcNow(this TimeProvider tp) => tp.GetUtcNow().UtcDateTime;                 // was DateTime.UtcNow
    public static DateOnly TodayUtc(this TimeProvider tp) => DateOnly.FromDateTime(tp.GetUtcNow().UtcDateTime); // was DateTime.Today (UTC intent)
    public static DateTimeOffset NowOffset(this TimeProvider tp) => tp.GetUtcNow();                    // was DateTimeOffset.UtcNow
}
```

Call-site sweep pattern: inject `TimeProvider` (ctor), then
`DateTime.UtcNow` → `_timeProvider.UtcNow()`, `DateTimeOffset.UtcNow` → `_timeProvider.NowOffset()`,
`DateTime.Today` → `_timeProvider.TodayUtc()`. Local-time sites (`DateTime.Now`) map via
`GetLocalNow()`/`LocalTimeZone`, enumerated case-by-case (most code is UTC-based already).

### 5.2 `SimulationTimeProvider` (non-prod only)

```csharp
public sealed class SimulationTimeProvider(IClockStateProvider state) : TimeProvider
{
    public override DateTimeOffset GetUtcNow()
    {
        var s = state.Current; // cheap in-memory read
        return s.Mode switch
        {
            ClockMode.Real   => base.GetUtcNow(),
            ClockMode.Frozen => s.SimAnchorUtc,
            ClockMode.Offset => base.GetUtcNow() + (s.SimAnchorUtc - s.RealAnchorUtc),
            _                => base.GetUtcNow(),
        };
    }
    public override TimeZoneInfo LocalTimeZone => state.Current.SimTimeZone ?? base.LocalTimeZone;
    // GetTimestamp()/CreateTimer(): NOT overridden → real monotonic timing + real timers.
}
```

Rationale for leaving `GetTimestamp`/`CreateTimer` **real**: `Stopwatch`/perf stay accurate and
background loops keep ticking in real time. We drive scheduled work deterministically via
**on-demand triggers** (§7), not by faking timers — simpler and robust.

### 5.3 Shared state: `SimulationClock`

One-row EF entity + migration (present in all envs; only *read* when `SimulationTimeProvider` is
bound):

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | always `1` |
| `Mode` | text/enum | `Real` \| `Frozen` \| `Offset` |
| `SimAnchorUtc` | timestamptz | the simulated instant … |
| `RealAnchorUtc` | timestamptz | … at this real instant (for `Offset` ticking) |
| `TimeZoneId` | text null | simulated local tz |
| `UpdatedAtRealUtc` | timestamptz | audit |

Default seed: `Mode=Real`.

### 5.4 Cheap reads + invalidation

`GetUtcNow()` is called constantly and is synchronous — it must not hit the DB. An
`IClockStateProvider` holds the current `ClockState` in memory, refreshed:

- immediately on a `clock-changed` notification (Postgres `LISTEN/NOTIFY` channel `sim_clock`,
  and SignalR for the web), and
- by a **1 s fallback poll** (a lightweight hosted service in both API and Engine).

Worst-case staleness is 1 s, which never affects determinism: time-travel is always followed by
an explicit trigger/observe step.

### 5.5 DI wiring & gating

- Config flag `Simulation:Enabled` (default **false**; `true` only in Development/test).
- **Prod (`false`):** `services.AddSingleton(TimeProvider.System)`; no dev routes; clock row never read.
- **Non-prod (`true`):** register `IClockStateProvider` + refresher hosted service +
  `services.AddSingleton<TimeProvider, SimulationTimeProvider>()`; map dev routes.
- One shared `AddSimulationClock(config)` extension, called by both API and Engine startup.

### 5.6 Classification rule (which of the 339 sites to virtualize)

- **Virtualize (inject `TimeProvider`):** anything that (a) is stored on a business record that
  appears in reports, (b) drives a scheduled job's due/period logic, or (c) is a
  lease/payment/expense/notice/inspection date or a due/overdue/period-boundary computation.
- **Keep real (do NOT touch):** JWT/refresh issue+expiry (`JwtTokenService`), API-key/auth
  handlers, HTTP cache TTLs, rate limiting, outbox retry backoff scheduling, `Stopwatch`/perf,
  and log/telemetry timestamps.

The sweep (§9 Phase D) applies this rule per site; auth/infra sites are explicitly excluded.

## 6. Control surface (dev-only)

All under `/api/v1/dev/*`, mapped only when `Simulation:Enabled`, requiring an authenticated
admin. Every mutation persists the `SimulationClock` row and broadcasts `clock-changed`
(`NOTIFY` + SignalR).

- `GET  /api/v1/dev/clock` → `{ simNowUtc, mode, timeZoneId, offsetSeconds }`
- `POST /api/v1/dev/clock/set` `{ instantUtc | date, timeZoneId?, mode?="offset" }` — anchor
  sim-now to the given instant; `mode=frozen` holds it, `offset` ticks forward from it.
- `POST /api/v1/dev/clock/advance` `{ days?, hours?, minutes?, seconds? }` — shift the anchor by
  a delta (works frozen or ticking).
- `POST /api/v1/dev/clock/freeze` / `POST /api/v1/dev/clock/unfreeze`
- `POST /api/v1/dev/clock/reset` → back to `Real`.

### Worker triggers

- `POST /api/v1/dev/workers/{key}/run-once` — invoke that worker's due-work service once at
  sim-now, synchronously; return a summary (e.g. `{ created: 12, skipped: 3 }`).
- `POST /api/v1/dev/workers/run-due` — run all due workers once (convenience after a time jump).

Keys map to the existing services (`rent-charge`, `autopay`, `late-fee`, `recurring-expense`,
`recurring-maintenance`, `debt-service`, `lease-expiry-reminder`, `notice-draft`,
`daily-briefing`). Non-prod + admin gated.

## 7. Web design

### 7.1 Global `Date` shim (dev-only)

`web/src/lib/dev/sim-clock.ts` — a ~40-line shim (no prod dependency) that, when active:

- overrides `Date.now()` → `realNow + offsetMs` (or a fixed instant when frozen),
- overrides `new Date()` (no args) → simulated now; `new Date(...args)` passes through unchanged,
- preserves `instanceof Date`, `Date.parse`, `Date.UTC`.

Loaded **before app boot** (guarded by `import.meta.env.DEV` and a runtime `Simulation:Enabled`
check). Zero edits to the 113 call sites; `date.ts` helpers (which use `Date.now()`/`new Date()`)
become sim-aware automatically.

### 7.2 Sync

On load, `GET /api/v1/dev/clock` → `offsetMs = simNow - realNow` (+ mode). Subscribe to the
existing SignalR hub's `clock-changed` → re-apply. Expose
`window.__simClock = { sync(), set(iso), offsetMs, mode }` so the browser-driving tester can
read/force it directly.

### 7.3 Dev panel

`web/src/lib/dev/SimClockPanel.svelte` — a small floating widget (dev + `Simulation:Enabled`
only) showing sim-now with set-date / +1d / +1w / +1m / freeze / reset controls that call the
endpoints. Mounted in the root layout behind a dev guard.

### 7.4 SSR stays real

No server-side patching. The handful of SSR display dates are corrected on client hydration;
auth/cookie/cache timing stays real by design.

## 8. Timezone testing

The control sets an explicit `timeZoneId`/offset so we can exercise "landlord Central, tenant
Pacific, traveling to UTC+9" and surface the date-boundary bugs that hide in reports (month/day
rollover between UTC and local).

## 9. Rollout plan (phased; drives the implementation plan)

- **A — Infra:** Core `TimeProvider` extensions; `SimulationTimeProvider`; `SimulationClock`
  entity + migration; `IClockStateProvider` + refresher + `NOTIFY`/SignalR invalidation;
  `AddSimulationClock` DI + gating; bind `System` in prod. Unit tests with `FakeTimeProvider`.
- **B — Control + triggers:** dev clock endpoints + worker `run-once`/`run-due`; broadcast
  wiring. Integration tests.
- **C — Web:** shim + sync + dev panel.
- **D — The sweep:** replace domain wall-clock sites with the injected `TimeProvider` across
  services → workers → Core/Data, in reviewed batches, applying the §5.6 rule (skip auth/infra).
  **Parallelize edits, sequence builds** (concurrent rebuilds of one `.csproj` corrupt bin/obj).
- **E — Verify:** set date → API/Engine/web agree; advance + trigger → correct rent
  charges/late fees/lease-expiry reminders; reconcile a slice against SA's ledger; confirm the
  prod build has no dev routes/shim and uses `System`.

## 10. Testing the clock

- **Unit:** `SimulationTimeProvider` offset/frozen/tz math; `ClockState` invalidation; endpoint
  set/advance/reset; extension helpers. Services under test use `FakeTimeProvider`
  (`Microsoft.Extensions.TimeProvider.Testing`).
- **Integration:** set→GET round-trip; advance→worker-trigger→DB records; cross-process (API
  sets, Engine sees via `NOTIFY`).
- **Guard test:** with `Simulation:Enabled=false`, dev routes 404 and `TimeProvider.System` is
  bound.

## 11. Safety

Whole surface behind `Simulation:Enabled` (false in prod) + admin auth on endpoints. Prod:
`TimeProvider.System`, no routes, no shim, clock row unread. Never touches OS/Postgres clocks.
The `SimulationClock` table is harmless if present in prod (never read).

## 12. Open questions / risks

- **`DateTime.Now` (local) sites:** enumerate; map to `GetLocalNow()`/simulated `LocalTimeZone`.
- **Mixed real/sim within one worker:** e.g. outbox retry backoff (real) vs due-date (sim) in
  the same loop — keep backoff real, due-date sim; verify no comparison mixes the two.
- **Third-party libs** calling `DateTime.UtcNow` internally (token/crypto) — out of scope,
  correctly stays real.
- **1 s cache staleness** — acceptable; time-travel is always followed by an explicit
  trigger/observe.

## 13. Acceptance criteria

1. In dev, `POST /dev/clock/set {date}` makes API responses, Engine worker logic, and web
   display all report that date.
2. `advance 35 days` then `workers/run-due` produces exactly the rent charges + late fees +
   lease-expiry reminders expected for that window.
3. A timezone set exercises a date-boundary case (e.g. UTC-vs-Central month rollover) without an
   off-by-one in a report.
4. Prod build: dev routes absent, shim absent, `TimeProvider.System` bound, all existing
   behavior unchanged.
5. Reconciliation: a sampled month of the sim run matches SA's ground-truth ledger.
