# Codebase Map — Date/Time & the Clock Subsystem (2026-07-01)

Mapping done to design the Master Simulation Clock (TSK-615). Counts are from `grep` sweeps;
the orchestrator counted ~339 backend / ~113 web, the SpecReviewer's independent grep found
~349 / ~123 — same order of magnitude.

## .NET wall-clock sites (`DateTime.UtcNow/Now/Today`, `DateTimeOffset.UtcNow/Now`)

~339–349 sites across `RentalCommand.{Core,Data,Api,Engine}` (excl. tests). No clock abstraction
existed. On **.NET 10** → `System.TimeProvider` is the fix. Top hotspot files:

| Sites | File |
|---|---|
| 26 | `Api/Services/Domain/AccountingImportService.cs` |
| 13 | `Api/Services/Domain/AccountingConnectionService.cs` ⚠️ incl. OAuth token-expiry (`:164,362`) — **keep real** |
| 12 | `Api/Scanning/ScanService.cs` (has an `ExecuteUpdate` lambda `:1701` — hoist-to-local) |
| 10 | `Api/Services/Domain/LeaseService.cs` |
| 8 | `Api/Services/Domain/ReportsService.cs` (buckets in **pure UTC** — `:207` etc.) |
| 8 | `Api/Services/Domain/BankingService.cs`, `Api/Controllers/AccountingController.cs` |
| 7 | `Api/Services/Auth/JwtTokenService.cs` ⚠️ **keep real** (token issue/expiry) |
| 5 | `Engine/Workers/OutboxDispatchWorker.cs` (retry backoff — **keep real**), `Engine/Services/RentChargeService.cs`, several Api domain services |

Data-layer (not in the hotspot-by-file API/Engine list): `Data/Auditing/AuditSaveChangesInterceptor.cs:235`
`var now = DateTime.UtcNow` — the **audit "story of record"** timestamp; **virtualize** (inject
`TimeProvider` into `RentalCommand.Data`).

Lambda-embedded `DateTime.UtcNow` (can't translate in EF expression trees — hoist to a local):
`ScanService.cs:1701`, `ScanProcessingWorker.cs:379`. Reviewer confirmed only ~4 such sites and
**no** domain `.Where(x => x < DateTime.UtcNow)` compiling to SQL `now()`.

## Engine scheduled workers (all fire on wall-clock)

`OutboxDispatchWorker`, `ScanProcessingWorker`, `RentChargeWorker`, `DebtServiceWorker`,
`RecurringExpenseWorker`, `AutopayChargeWorker`, `RecurringMaintenanceWorker`, `LateFeeWorker`,
`LeaseExpiryReminderWorker`, `DailyBriefingDeliveryWorker`, `NoticeDraftWorker`,
`AccountingPullWorker`, `AccountingTokenRefreshWorker` (+ `AdvisoryLockWatcherService`,
`WorkerWatchdogService`). Paired services (`IRentChargeService`, `ILateFeeService`, …) are all
parameterless `Task<int> …Async(CancellationToken)` and **idempotent** → invocable on demand.
`IDailyBriefingDeliveryService.EnqueueDueAsync(DateTime? utcNow = null,…)` already has an injectable now.

**Workers OFF by default:** new-portfolio `NotificationSettings` seeds `EnableRentCharges=false`,
`EnableLateFees=false`, `NotifyTenants=false`. A test harness must PUT settings to enable.

**Business timezone** read from **`App:TimeZone`** config (NOT any provider zone):
`RentChargeService.cs:51`, `LateFeeService.cs:56`, `RecurringMaintenanceService.cs:47`,
`DebtServiceService.cs:39`, `RecurringExpenseGenerationService.cs:40`. → the tz control overrides
`App:TimeZone` via a new `IAppTimeZoneProvider`. Reports bucket in **UTC** regardless.

## Web date sites (`new Date()`, `Date.now()`, `Date.UTC()`)

~113–123 sites across ~40 files; hub `web/src/lib/utils/date.ts` (`formatDateOnly`,
`daysFromTodayUtc` uses `Date.now()`, `isPastDueUtc`, `localInputToOffsetIso`). **All server-side
(SSR) date usage is auth/cookie/cache timing** → stays real:
`login/+page.server.ts` (cookie expiry), `auth/google/callback/+server.ts` (cookie expiry),
`lib/server/jwt-claims.ts:67` (token `exp` check), `lib/server/token-refresh.ts` (cache TTL +
cookie expiry). SSR on app-wide (`+layout.ts: ssr = true`). → web shim is **client-only**.

## Structural facts that shaped the design

- **Project dependency direction:** `Engine → Api → {Core, Data}`. The **API references only Core +
  Data**; the **Engine references the API**. So the API cannot reference the Engine (build cycle),
  and the Engine has **no HTTP port** (`Host.CreateApplicationBuilder`). → worker triggers use a
  DB **command-bridge**, not an API→Engine call.
- **Zero DB-side time defaults** (no `defaultValueSql`/`now()`/`CURRENT_TIMESTAMP` on any timestamp
  column; the accounting view computes no server-side time). → the app is the sole source of domain
  time; no Postgres clock hacking needed.
- **No Postgres `LISTEN/NOTIFY`** anywhere; cross-process = SignalR hub (auth-gated, per-group, no
  `Clients.All`) + a DB outbox with **polling**. → clock cross-process sync is a **1s poll**, not NOTIFY.
- Existing helper `Api/Services/Domain/DateTimeNormalization.cs` coerces `Kind`→UTC for Npgsql
  `timestamptz` (about *Kind*, complements the clock which is about *now*).
- Precedent: EdiPlatform uses `TimeProvider ?? TimeProvider.System` DI
  (`EdiPlatform.Engine/Services/ApiChannel/Auth/OAuth2TokenCache.cs:34`).
- Web env-flag precedent: `PUBLIC_GOOGLE_CLIENT_ID` via `$env/static/public`; inline-before-boot
  script precedent: the splash loader in `web/src/app.html`.

## Classification rule for the sweep

**Virtualize** business time (on records shown in reports; drives scheduled-job due/period logic;
lease/payment/expense/notice/inspection dates; due/overdue/period math; the audit-interceptor
timestamp). **Keep real:** JWT/refresh + OAuth token expiry, API-key/auth handlers, HTTP cache
TTLs, rate limits, outbox retry backoff, `Stopwatch`/perf, log/telemetry.
