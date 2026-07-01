# Master Simulation Clock — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce one controllable, dev-only "now" honored across the API, Engine, and web so we can time-travel through a simulated year for E2E testing — with production behavior byte-for-byte unchanged (real clock).

**Architecture:** Inject `System.TimeProvider` in place of domain wall-clock reads. Prod binds `TimeProvider.System`; non-prod binds a `SimulationTimeProvider` that computes "now" from a shared one-row `SimulationClock` table (polled ~1s by both processes). A dev-only API control surface (`/api/v1/dev/clock`) mutates that row; a dev-only command-bridge (`SimWorkerCommand` table + an Engine worker) runs the scheduled jobs on demand at sim-time. The web patches `Date`/`Date.now` via an inline dev-only shim synced from an anonymous clock endpoint. Everything is gated by `Simulation:Enabled` (false in prod).

**Tech Stack:** .NET 10 (`System.TimeProvider`, `Microsoft.Extensions.TimeProvider.Testing` for tests), EF Core + Npgsql, SvelteKit 5 (`$env/static/public`, `hooks.server` `transformPageChunk`).

## Global Constraints

- **.NET 10**; `TimeProvider` is the abstraction. Prod = `TimeProvider.System`; non-prod = `SimulationTimeProvider`. Everything dev-only is gated by config `Simulation:Enabled` (default **false**; true only in Development/test).
- **Shared clock infra lives in `RentalCommand.Api`** (both API and Engine reference it: dependency direction is `Engine → Api → {Core, Data}`). Interfaces/entities/extensions that Core/Data need live in `RentalCommand.Core`.
- **Classification rule for the sweep:** virtualize *business* time (stored on records shown in reports; drives scheduled-job due/period logic; lease/payment/expense/notice/inspection dates; due/overdue/period math; the Data audit-interceptor timestamp). **Keep real** (do NOT touch): JWT/refresh issue+expiry (`JwtTokenService`), OAuth token-expiry (`AccountingConnectionService.cs:164,362`), API-key/auth handlers, HTTP cache TTLs, rate limiting, outbox retry backoff, `Stopwatch`/perf, log/telemetry.
- **EF expression-tree mechanic:** inside `ExecuteUpdate`/`SetProperty`/query lambdas, hoist `var now = _timeProvider.UtcNow();` to a local first (can't translate a method call on an injected instance). Known sites: `ScanService.cs:1701`, `ScanProcessingWorker.cs:379`.
- **PostgreSQL only.** Migrations: `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.
- **Enums serialize as string names** app-wide (already configured on controllers + SignalR).
- **Build discipline:** parallelize edits, **sequence builds** — never rebuild the same `.csproj` concurrently. Prefer `dotnet build <project>` and filtered tests. `MSBUILDDISABLENODEREUSE=1`; `dotnet build-server shutdown` after heavy batches.
- **Commit convention:** clear subject + body, **no** AI-attribution/`Co-Authored-By` trailer. Frequent commits (one per task).
- Branch: `tsk-615-master-simulation-clock` (already cut off `main`).

---

## Phase A — Backend infrastructure

### Task A1: ClockMode enum + TimeProvider extensions (Core)

**Files:**
- Create: `RentalCommand.Core/Time/ClockMode.cs`
- Create: `RentalCommand.Core/Time/TimeProviderExtensions.cs`
- Test: `RentalCommand.Core.Tests/Time/TimeProviderExtensionsTests.cs`

**Interfaces produced:** `enum ClockMode { Real, Frozen, Offset }`; `TimeProvider.UtcNow() → DateTime`, `TimeProvider.TodayUtc() → DateOnly`, `TimeProvider.NowOffset() → DateTimeOffset`.

- [ ] **Step 1 — failing test:**
```csharp
using Microsoft.Extensions.Time.Testing;
using RentalCommand.Core.Time;
public class TimeProviderExtensionsTests
{
    [Fact]
    public void UtcNow_ReturnsProviderInstant_AsUtcDateTime()
    {
        var t = new DateTimeOffset(2025, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(t);
        Assert.Equal(t.UtcDateTime, fake.UtcNow());
        Assert.Equal(DateTimeKind.Utc, fake.UtcNow().Kind);
        Assert.Equal(new DateOnly(2025, 3, 15), fake.TodayUtc());
        Assert.Equal(t, fake.NowOffset());
    }
}
```
- [ ] **Step 2 — run, expect FAIL** (`TimeProviderExtensions` not found): `dotnet test RentalCommand.Core.Tests --filter TimeProviderExtensionsTests`
- [ ] **Step 3 — implement:**
```csharp
// ClockMode.cs
namespace RentalCommand.Core.Time;
public enum ClockMode { Real, Frozen, Offset }
```
```csharp
// TimeProviderExtensions.cs
namespace RentalCommand.Core.Time;
public static class TimeProviderExtensions
{
    public static DateTime UtcNow(this TimeProvider tp) => tp.GetUtcNow().UtcDateTime;
    public static DateOnly TodayUtc(this TimeProvider tp) => DateOnly.FromDateTime(tp.GetUtcNow().UtcDateTime);
    public static DateTimeOffset NowOffset(this TimeProvider tp) => tp.GetUtcNow();
}
```
- [ ] **Step 4 — run, expect PASS.** Add `Microsoft.Extensions.TimeProvider.Testing` to `RentalCommand.Core.Tests.csproj` if absent.
- [ ] **Step 5 — commit:** `git commit -am "Add ClockMode + TimeProvider extensions (TSK-615)"`

### Task A2: SimulationClock entity + ClockState + interfaces (Core)

**Files:**
- Create: `RentalCommand.Core/Entities/SimulationClock.cs`
- Create: `RentalCommand.Core/Time/ClockState.cs` (record) + `IClockStateProvider.cs` + `IAppTimeZoneProvider.cs`

**Interfaces produced:**
- `SimulationClock { int Id; ClockMode Mode; DateTime SimAnchorUtc; DateTime RealAnchorUtc; string? TimeZoneId; DateTime UpdatedAtRealUtc; }`
- `record ClockState(ClockMode Mode, DateTime SimAnchorUtc, DateTime RealAnchorUtc, string? TimeZoneId)`
- `interface IClockStateProvider { ClockState Current { get; } Task RefreshAsync(CancellationToken ct); }`
- `interface IAppTimeZoneProvider { TimeZoneInfo BusinessTimeZone { get; } }`

- [ ] **Step 1 — implement the entity** (int PK fixed to 1; `Mode` stored as string via EF config in A3):
```csharp
namespace RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
public class SimulationClock
{
    public int Id { get; set; } = 1;
    public ClockMode Mode { get; set; } = ClockMode.Real;
    public DateTime SimAnchorUtc { get; set; }
    public DateTime RealAnchorUtc { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTime UpdatedAtRealUtc { get; set; }
}
```
- [ ] **Step 2 — implement `ClockState`, `IClockStateProvider`, `IAppTimeZoneProvider`** (see signatures above; `ClockState` is an immutable record).
- [ ] **Step 3 — build Core:** `dotnet build RentalCommand.Core` → expect success.
- [ ] **Step 4 — commit:** `git commit -am "Add SimulationClock entity + clock-state interfaces (TSK-615)"`

### Task A3: SimulationClock DbSet + EF config + migration (Data)

**Files:**
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (add `DbSet<SimulationClock> SimulationClocks`; configure)
- Create: EF migration via CLI.

- [ ] **Step 1 — add DbSet + config** in `OnModelCreating` (or a config class following the repo's pattern): PK `Id`; `Mode` converted to string (`.HasConversion<string>()`); seed a single row `new SimulationClock { Id = 1, Mode = ClockMode.Real }` via `HasData`.
- [ ] **Step 2 — confirm RLS:** grep for how `tenant_isolation` RLS policies are applied (a raw-SQL migration). Ensure `SimulationClock` is **not** added to any policy loop (it's global, no `PortfolioId`). If policies are applied per-table by an explicit list, leave `SimulationClock` off it. Document in the migration comment.
- [ ] **Step 3 — add migration:** `dotnet ef migrations add AddSimulationClock --project RentalCommand.Data --startup-project RentalCommand.Api`
- [ ] **Step 4 — build Data + inspect migration** (confirm one table, seed row, no RLS policy). `dotnet build RentalCommand.Data`.
- [ ] **Step 5 — commit:** `git commit -am "Add SimulationClock DbSet + migration (TSK-615)"`

### Task A4: ClockStateProvider + 1s poll refresher (Api)

**Files:**
- Create: `RentalCommand.Api/Simulation/ClockStateProvider.cs` (implements `IClockStateProvider`; thread-safe cached `ClockState`; `RefreshAsync` reads row 1 via a scoped `RentalCommandDbContext`)
- Create: `RentalCommand.Api/Simulation/ClockStateRefresher.cs` (`BackgroundService`; loops `RefreshAsync` every 1s using `TimeProvider.System` for the delay)
- Test: `RentalCommand.Api.Tests/Simulation/ClockStateProviderTests.cs`

**Interfaces consumed:** `IClockStateProvider`, `ClockState`, `RentalCommandDbContext`.

- [ ] **Step 1 — failing test:** provider defaults to `ClockMode.Real` before first refresh; after seeding row (Mode=Frozen, anchor=X) and `RefreshAsync`, `Current` reflects it. Use an in-... (NOTE: repo is PostgreSQL-only; use the existing integration-test DbContext fixture pattern in `RentalCommand.IntegrationTests`/`TestCommon` rather than SQLite). If no lightweight fixture exists, make this an integration test under `RentalCommand.IntegrationTests`.
- [ ] **Step 2 — run, expect FAIL.**
- [ ] **Step 3 — implement** `ClockStateProvider` (holds `private volatile ClockState _current = new(ClockMode.Real, default, default, null)`; `RefreshAsync` opens a scope, reads `SimulationClocks.FindAsync(1)`, assigns a new `ClockState`) and `ClockStateRefresher` (a `BackgroundService` that awaits `RefreshAsync` then `Task.Delay(1000)` in a loop; swallow+log transient DB errors).
- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit.**

### Task A5: SimulationTimeProvider (Api)

**Files:**
- Create: `RentalCommand.Api/Simulation/SimulationTimeProvider.cs`
- Test: `RentalCommand.Api.Tests/Simulation/SimulationTimeProviderTests.cs`

- [ ] **Step 1 — failing test** with a stub `IClockStateProvider`: Real→returns real; Frozen→returns `SimAnchorUtc`; Offset→returns `realNow + (SimAnchor - RealAnchor)`.
```csharp
[Fact] public void Frozen_ReturnsAnchor() {
    var anchor = new DateTime(2025,6,1,0,0,0,DateTimeKind.Utc);
    var state = new StubClockState(new ClockState(ClockMode.Frozen, anchor, default, null));
    var sut = new SimulationTimeProvider(state);
    Assert.Equal(anchor, sut.GetUtcNow().UtcDateTime);
}
```
- [ ] **Step 2 — run, expect FAIL.**
- [ ] **Step 3 — implement** (subclass `TimeProvider`, override `GetUtcNow()` per §5.2 of the spec; override `LocalTimeZone` to return the sim zone when set else base; do NOT override `GetTimestamp`/`CreateTimer`).
- [ ] **Step 4 — run, expect PASS.**
- [ ] **Step 5 — commit.**

### Task A6: IAppTimeZoneProvider impl (Api)

**Files:**
- Create: `RentalCommand.Api/Simulation/AppTimeZoneProvider.cs` (returns sim override `TimeZoneId` from `IClockStateProvider.Current` if set, else `configuration["App:TimeZone"]`, else `America/New_York`)
- Test: `RentalCommand.Api.Tests/Simulation/AppTimeZoneProviderTests.cs`

- [ ] Steps: failing test (override wins over config; falls back to config; falls back to ET) → implement → pass → commit.

### Task A7: AddSimulationClock DI extension + wiring + gating

**Files:**
- Create: `RentalCommand.Api/Simulation/SimulationClockServiceCollectionExtensions.cs` — `AddSimulationClock(this IServiceCollection, IConfiguration, IHostEnvironment)`.
- Modify: `RentalCommand.Api/Program.cs` (call it after config binding, before `builder.Build()`).
- Modify: `RentalCommand.Engine/Program.cs` (call it after DbContext registration).
- Test: `RentalCommand.Api.Tests/Simulation/GatingTests.cs`.

**Behavior:**
```csharp
public static IServiceCollection AddSimulationClock(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
{
    var enabled = config.GetValue<bool>("Simulation:Enabled");
    if (!enabled)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppTimeZoneProvider, AppTimeZoneProvider>(); // config/ET only
        return services;
    }
    services.AddSingleton<IClockStateProvider, ClockStateProvider>();
    services.AddSingleton<IAppTimeZoneProvider, AppTimeZoneProvider>();
    services.AddSingleton<TimeProvider, SimulationTimeProvider>();
    services.AddHostedService<ClockStateRefresher>();
    return services;
}
```
- [ ] **Step 1 — guard test:** with `Simulation:Enabled=false`, resolved `TimeProvider` is `TimeProvider.System`; with `true`, it's `SimulationTimeProvider`. (Build a minimal `ServiceCollection`, add a fake config, assert.)
- [ ] **Step 2 — run, expect FAIL.**
- [ ] **Step 3 — implement the extension + call from both `Program.cs`.** In API, pass `builder.Environment`; in Engine, `builder.Environment`. `AppTimeZoneProvider` must tolerate a missing `IClockStateProvider` when disabled — give it an optional ctor dep (`IClockStateProvider? = null`).
- [ ] **Step 4 — run, expect PASS; build API + Engine** (sequentially): `dotnet build RentalCommand.Api` then `dotnet build RentalCommand.Engine`.
- [ ] **Step 5 — commit.**

---

## Phase B — Control surface + worker command-bridge

### Task B1: DevClockController (Api)

**Files:**
- Create: `RentalCommand.Api/Controllers/DevClockController.cs`
- Test: `RentalCommand.IntegrationTests/DevClockControllerTests.cs`

**Endpoints** (route `api/v1/dev/clock`; whole controller mapped only when `Simulation:Enabled` — guard in each action by checking the flag and returning 404 if disabled, OR register the controller conditionally; simplest: an `[ServiceFilter]`/inline check via injected `IConfiguration`):
- `GET` `[AllowAnonymous]` → `{ simNowUtc, mode, timeZoneId, offsetSeconds }` (reads `TimeProvider` + `IClockStateProvider`).
- `POST set` `[Authorize(Roles="Admin")]` `{ instantUtc?|date?, timeZoneId?, mode?="offset" }` → writes row 1 (Mode=Offset: SimAnchor=given, RealAnchor=`TimeProvider.System` now; Mode=Frozen: SimAnchor=given), `UpdatedAtRealUtc=now`; then `await IClockStateProvider.RefreshAsync`.
- `POST advance` `{ days?,hours?,minutes?,seconds? }` → shift SimAnchor (and, if Offset, keep RealAnchor) by the delta; refresh.
- `POST freeze` → set Mode=Frozen with SimAnchor = current sim now. `POST unfreeze` → Mode=Offset re-anchored to now. `POST reset` → Mode=Real.
- [ ] **Steps:** integration test (set → GET returns that instant; advance 10 days → GET moved 10 days; reset → real) → implement → pass → build Api → commit.

### Task B2: SimWorkerCommand entity + migration

**Files:**
- Create: `RentalCommand.Core/Entities/SimWorkerCommand.cs` — `{ Guid Id; string WorkerKey; DateTime RequestedSimUtc; string Status /*Pending|Running|Done|Error*/; string? ResultJson; string? Error; DateTime CreatedRealUtc; DateTime? CompletedRealUtc; }`
- Modify: `RentalCommandDbContext` + migration `AddSimWorkerCommand`.
- [ ] **Steps:** add entity + DbSet + config (index on `Status`) → `dotnet ef migrations add AddSimWorkerCommand …` → build Data → commit. (Global table; exclude from tenant RLS like A3.)

### Task B3: SimWorkerCommandWorker (Engine, dev-only)

**Files:**
- Create: `RentalCommand.Engine/Workers/SimWorkerCommandWorker.cs` (extends `EngineWorkerBase`; `PollInterval` ~500ms; registered only when `Simulation:Enabled`).
- Create: `RentalCommand.Engine/Workers/SimWorkerRegistry.cs` — maps `workerKey → Func<IServiceProvider, CancellationToken, Task<int>>` invoking the matching service (`rent-charge`→`IRentChargeService`, `late-fee`→`ILateFeeService`, `autopay`→`IAutopayChargeService`, `recurring-expense`, `recurring-maintenance`, `debt-service`, `lease-expiry-reminder`, `notice-draft`→`INoticeDraftGenerationService`, `daily-briefing`→`IDailyBriefingDeliveryService`). Plus key `run-due` → runs all in dependency order: rent-charge → notice-draft → late-fee → autopay → debt-service → recurring-expense → recurring-maintenance → (outbox is its own always-on worker).
- Modify: `RentalCommand.Engine/Program.cs` — register worker + registry only when `Simulation:Enabled`.
- Test: `RentalCommand.Engine.Tests/Workers/SimWorkerRegistryTests.cs` (registry resolves each key to the right service; `run-due` order is correct).

**ExecuteCycleAsync:** claim the oldest `Pending` command (`UPDATE … SET Status='Running' … RETURNING` or optimistic update), invoke the registry entry inside the current scope (Engine RLS = admin, `SystemCurrentActor` for audit), write `Status='Done', ResultJson={created:n}` (or `Error`). Return count processed.

- [ ] **Steps:** unit-test the registry mapping + order → implement worker + registry → build Engine → commit.

### Task B4: DevWorkersController (Api) — enqueue + long-poll

**Files:**
- Create: `RentalCommand.Api/Controllers/DevWorkersController.cs` (route `api/v1/dev/workers`; `[Authorize(Roles="Admin")]`; only when enabled).
- Test: `RentalCommand.IntegrationTests/DevWorkersControllerTests.cs`.

**Endpoints:**
- `POST {key}/run-once` → insert `SimWorkerCommand{ WorkerKey=key, RequestedSimUtc=TimeProvider.UtcNow(), Status=Pending }`; then **long-poll** the row up to ~30s (re-query every 250ms) for `Done`/`Error`; return `ResultJson` or the error (503 with a clear message on timeout — "Engine command worker not responding").
- `POST run-due` → same with key `run-due`.
- `GET commands/{id}` → the row's status/result.
- [ ] **Steps:** integration test (enqueue with the Engine worker running in the test host → returns a result) → implement → build Api → commit. If the integration harness doesn't run the Engine, assert enqueue + `GET commands/{id}` transitions with a stubbed worker; note the full cross-process path is validated in Phase E.

---

## Phase C — Web (dev-only)

### Task C1: PUBLIC_SIMULATION_ENABLED flag + inline `Date` shim

**Files:**
- Modify: `web/src/app.html` — add `<!--%sim-clock-shim%-->` placeholder as the FIRST child of `<head>` (before `%sveltekit.head%`).
- Modify: `web/src/hooks.server.ts` — add a `transformPageChunk` that replaces the placeholder with the shim `<script>` **only when** `env.PUBLIC_SIMULATION_ENABLED === 'true'` (import from `$env/static/public`), else with `''`.
- Create: `web/src/lib/dev/sim-clock-shim.ts` — exports the shim script **string** (kept as a string so it can be inlined verbatim; no imports).
- Modify: `web/.env`/`.env.example` — document `PUBLIC_SIMULATION_ENABLED`.

**Shim script (inlined):** reads cookie `rc_sim` (`{offsetMs, mode, anchorMs}`); saves `const RealDate = Date`; replaces `Date` with a subclass where `new Date()` (no args) → `new RealDate(nowMs())` and `Date.now()` → `nowMs()`, `nowMs = mode==='frozen' ? anchorMs : RealDate.now()+offsetMs`; all other constructors/`parse`/`UTC` delegate to `RealDate`; preserve `instanceof`. Expose `window.__simClock = { setOffset(ms,mode,anchorMs), offsetMs, mode }`.
- [ ] **Steps:** with `PUBLIC_SIMULATION_ENABLED=true`, the served HTML `<head>` contains the shim; with unset/false it does not (assert via a `svelte-check`/unit test of the `transformPageChunk` function, or a Playwright check) → implement → commit.

### Task C2: Client sync bootstrap

**Files:**
- Create: `web/src/lib/dev/sim-clock-client.ts` — when `PUBLIC_SIMULATION_ENABLED==='true'`: poll `GET /api/v1/dev/clock` every ~1s; compute `offsetMs = simNowUtc - RealDate.now()` (and `mode`, `anchorMs`); call `window.__simClock.setOffset(...)`; write the `rc_sim` cookie for the next warm load.
- Modify: `web/src/hooks.client.ts` (or root `+layout.svelte` `onMount`) — start the bootstrap, gated on the flag.
- [ ] **Steps:** implement; manual/Playwright check that after `POST /dev/clock/set`, `window.__simClock.offsetMs` updates within ~1s and `new Date()` in the console reflects sim time → commit.

### Task C3: SimClockPanel dev component

**Files:**
- Create: `web/src/lib/dev/SimClockPanel.svelte` — floating widget (only when the flag is on): shows sim now (live), buttons: set-date (date input), +1d/+1w/+1m (→ `advance`), freeze/unfreeze, reset. Calls the endpoints via the existing `web/src/lib/api/client.ts`.
- Modify: root `+layout.svelte` — mount `<SimClockPanel />` behind `{#if PUBLIC_SIMULATION_ENABLED}`.
- [ ] **Steps:** implement; visual check; `svelte-check` clean → commit.

---

## Phase D — The sweep (virtualize business time)

**Procedure for every sweep task (mechanical, per-site):** For each service/class: add a `TimeProvider` ctor param (store `_timeProvider`); replace `DateTime.UtcNow`→`_timeProvider.UtcNow()`, `DateTimeOffset.UtcNow`→`_timeProvider.NowOffset()`, `DateTime.Today`→`_timeProvider.TodayUtc()`; for `DateTime.Now`/local, use `_timeProvider.GetLocalNow()` only where local wall-clock is truly intended. **Apply the classification rule** (skip auth/token/cache/backoff/perf/log sites — leave those on `DateTime.UtcNow`). **Hoist to a local** before any EF expression tree/`ExecuteUpdate`. Because `TimeProvider` is registered singleton, ctor injection just works. After each batch: build the affected project(s) **sequentially** and run that project's tests.

> A representative before/after (from `RentChargeService`):
> ```csharp
> // before
> var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
> // after (ctor now takes TimeProvider _timeProvider + IAppTimeZoneProvider _tz)
> var today = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.UtcNow(), _tz.BusinessTimeZone).Date;
> ```

### Task D1: Engine automation services (9) + route business-tz through IAppTimeZoneProvider
**Files:** `RentalCommand.Engine/Services/{RentChargeService,LateFeeService,AutopayChargeService,DebtServiceService,RecurringExpenseGenerationService,RecurringMaintenanceService,LeaseExpiryReminderService,NoticeDraftGenerationService,DailyBriefingDeliveryService}.cs`. Replace the 5 `configuration["App:TimeZone"]` reads (RentCharge:51, LateFee:56, RecurringMaintenance:47, DebtService:39, RecurringExpense:40) with `_tz.BusinessTimeZone`. Add `TimeProvider` + `IAppTimeZoneProvider` to each ctor; DI already resolves them.
- [ ] Sweep sites → `dotnet build RentalCommand.Engine` → `dotnet test RentalCommand.Engine.Tests` → commit `"Virtualize clock in Engine automation services (TSK-615)"`.

### Task D2: Engine workers + remaining Engine services
**Files:** `RentalCommand.Engine/Workers/*` (OutboxDispatchWorker:5 sites, etc. — **skip** any retry-backoff/heartbeat timing per the rule) + `RentalCommand.Engine/Services/{EngineStatusReporter,…}`.
- [ ] Sweep → build Engine → test → commit.

### Task D3: API domain services — money/reports batch
**Files:** `RentalCommand.Api/Services/Domain/{AccountingImportService,AccountingConnectionService,AccountingService,ReportsService,BankingService,LeaseService,PaymentService,SecurityDepositService,ExpenseService…}.cs`. **Caution:** in `AccountingConnectionService`, leave `:164,362` (OAuth token-expiry) on the real clock.
- [ ] Sweep (respect exclusions; hoist-to-local for any lambdas) → `dotnet build RentalCommand.Api` → `dotnet test RentalCommand.Api.Tests` → commit.

### Task D4: API domain services — remainder + controllers + scanning
**Files:** the rest of `RentalCommand.Api/Services/Domain/*` (Scan lives in `Api/Scanning` — `ScanService.cs:1701` needs hoist-to-local), `Api/Services/{Voice,Esign,Payments,…}`, `Api/Controllers/*` (e.g. `AccountingController` 8 sites), `Api/Scanning/*` (`ScanProcessingWorker.cs:379` hoist-to-local — note this file is in the API assembly used by the Engine worker). **Skip** `JwtTokenService` (auth).
- [ ] Sweep → build Api → test → commit.

### Task D5: Data audit interceptor + NotificationHub + Core entities
**Files:** `RentalCommand.Data/Auditing/AuditSaveChangesInterceptor.cs` — add `TimeProvider` to its ctor (`(ICurrentActor, IAuditScope, TimeProvider)`), replace `:235` `var now = DateTime.UtcNow` → `_timeProvider.UtcNow()`; it's registered scoped in both `Program.cs` — DI resolves the singleton `TimeProvider`. `RentalCommand.Api/Hubs/NotificationHub.cs:138` alert timestamp. Core entities computing time (`ScreeningResult`, `RentalApplication`, `AdverseActionNotice` — these compute in properties; if they can't take a `TimeProvider`, set the value from the service layer instead and note it).
- [ ] Sweep → `dotnet build RentalCommand.Data` → build Api + Engine → run `RentalCommand.Data.Tests` + a broad `dotnet build RentalCommand.sln` to confirm the whole solution compiles → commit.

---

## Phase E — Verification

### Task E1: End-to-end sim-clock verification
- [ ] **Step 1** — Start the dev stack with `Simulation:Enabled=true` + `PUBLIC_SIMULATION_ENABLED=true` (`./scripts/start-dev.sh` with the flags in dev config).
- [ ] **Step 2** — `POST /api/v1/dev/clock/set {date:"2025-01-01", mode:"frozen"}`; assert `GET /api/v1/dev/clock` returns it; open the web app and confirm a relative-time element and the dev panel show the sim date; confirm the Engine logs read the sim date.
- [ ] **Step 3** — Enable `NotificationSettings` (rent charges/late fees) for the seeded portfolio; `POST /api/v1/dev/clock/advance {days:35}`; `POST /api/v1/dev/workers/run-due`; assert rent charges + late fees + any lease-expiry reminders were created for that window, **and each has an audit row dated in sim-time** (not real 2026).
- [ ] **Step 4** — Business-day boundary: set tz override to `America/New_York`, set a payment at 23:00 ET on the last grace day; run late-fee; assert it's treated as on-time.
- [ ] **Step 5** — Prod guard: build/run with `Simulation:Enabled=false`; assert `GET /api/v1/dev/clock` 404s, no `SimClockPanel`, no shim in the served `<head>`, and `TimeProvider.System` is bound.
- [ ] **Step 6** — Ping ScenarioArchitect: "clock endpoints live" with the confirmed contract, so it can wire + dry-run the replay driver.
- [ ] **Step 7** — Update the orchestration ledger + flip TSK-615 toward Done on merge.

---

## Self-review notes
- **Spec coverage:** A1–A7 (§5), B1–B4 (§6 incl. command-bridge for MF-1), C1–C3 (§7 incl. PUBLIC flag + inline shim + anonymous poll for MF-2), D1 (§8 tz via IAppTimeZoneProvider for MF-3), D5 (SF-B audit), poll-only (SF-A, no NOTIFY), classification + hoist-to-local (SF-C/D), E1 (all §13 acceptance criteria incl. prod guard + business-day boundary). Covered.
- **Types consistent:** `UtcNow()/TodayUtc()/NowOffset()`, `ClockMode`, `ClockState`, `IClockStateProvider`, `IAppTimeZoneProvider` used identically across tasks.
- **Open confirmations for the executor:** verify the exact RLS-policy application mechanism (A3 Step 2) and whether the API integration harness boots the Engine (B4) — both flagged inline.
