# Clock Implementation-Plan Review (2026-07-01)

**Source:** PlanReviewer (read-only, code-grounded). **Verdict:** Approve-with-changes. Architecture
sound; 6 must-fixes + 4 should-fixes + corrections. Fold M1–M6 into the plan before executing the
affected phases. (One earlier sub-agent claim about Identity auth breakage was overstated — corrected in S1.)

## MUST-FIX
- **M1 — Npgsql UTC-Kind rejects the seed + clock writes (A2/A3/B1).** Legacy timestamp behavior is OFF
  (`Engine/Program.cs:19-22`; API mirrors), so any `DateTime` write with `Kind != Utc` throws. The
  `HasData`/seed leaves `SimAnchorUtc`/`RealAnchorUtc`/`UpdatedAtRealUtc` at `default` (Unspecified);
  B1 `POST set` parses instants as Unspecified. **Fix:** seed explicit `DateTimeKind.Utc` literals (fixed
  epoch); `DateTime.SpecifyKind(…, Utc)` every parsed instant in the controller. `_timeProvider.UtcNow()`
  already returns Utc. *(Migration seed uses raw UTC timestamptz literals → safe.)*
- **M2 — `$env/static/public` breaks `vite build` incl. prod image (C1/C2).** Nothing imports it; only
  `$env/dynamic/public` precedent (`auth/google/+server.ts:10-14`, `register/+page.server.ts:13`); a named
  import of an undeclared var is a hard compile error; no `.env`/`.env.example`. **Fix:** use
  `$env/dynamic/public` (`import { env } from '$env/dynamic/public'; env.PUBLIC_SIMULATION_ENABLED === 'true'`)
  + add the var to env/CI.
- **M3 — global `Date` shim corrupts client JWT expiry (C1/C2).** `isTokenExpired()` uses
  `accessTokenExpiration.getTime() - Date.now()` (`auth.svelte.ts:118`) vs the REAL server JWT expiry;
  shim makes `Date.now()` sim → advancing time (E1 Step 3: +35d) makes every token read expired → refresh
  storms → single-use refresh rotation risks family revocation. Fans out to `client.ts:228,320`,
  `signalr.ts:81`, `accounting.ts:122,162,200`, `leases.ts:118,179`, `(protected)/+layout.svelte:60`.
  **Fix:** expose `RealDate` from the shim; `isTokenExpired` + refresh gates use `RealDate.now()`.
- **M4 — `run-due` omits lease-expiry reminders → E1 Step 3 fails (B3).** `ILeaseExpiryReminderService.RemindAsync`
  is distinct from `INoticeDraftGenerationService.GenerateAllAsync` (Engine/Program.cs:119 vs :136).
  **Fix:** add `lease-expiry-reminder` to `run-due` (after `notice-draft`). Decide on `daily-briefing` (E1 doesn't assert it).
- **M5 — Phase-D ctor changes break Engine.Tests compile (D1-D4).** 9 services constructed directly via
  `SqliteTestContext` in 10 files (`RentChargeServiceTests.cs:224`, `LateFeeServiceTests.cs:157`,
  `AutopayChargeServiceTests.cs:191`, `RecurringMaintenanceServiceTests.cs:170`,
  `RecurringExpenseGenerationServiceTests.cs:29`, `DebtServiceServiceTests.cs:32`,
  `LeaseExpiryReminderServiceTests.cs:77`, `DailyBriefingDeliveryServiceTests.cs:46,95`,
  `NoticeAutoSendTests.cs:108`). **Fix:** each D task updates its test construction sites in the SAME commit;
  add `Microsoft.Extensions.TimeProvider.Testing` to `Engine.Tests.csproj` (+ `Api.Tests.csproj` for D3/D4).
- **M6 — sweep exclusion list under-names keep-real sites (D2/D3/D4).** Add to keep-real:
  e-sign link expiry (`Esign/NativeSigningService.cs:505`, `NativeEsignProvider.cs:73,102`,
  `Domain/LeaseEsignService.cs:633,639`); OAuth refresh horizon (`AccountingTokenRefreshWorker.cs:43,51,74`);
  OAuth CSRF state-token TTL **write** (`AccountingConnectionService.cs:124` — plan only excluded the :164/:362 reads);
  portal-disabled sentinel (`TenantPortalProvisioningService.cs:315`); outbox backoff `FailedAt`
  (`OutboxDispatchWorker.cs:82,91,115,143,158`).

## SHOULD-FIX
- **S1 — DI `TimeProvider` leaks into ASP.NET auth in non-prod, but contained.** A DI-registered `TimeProvider`
  auto-populates cookie/JwtBearer/OAuth handler options + SecurityStampValidator. BUT: JWT-bearer `exp`/`nbf`
  needs an explicit `TokenValidationParameters.TimeProvider` (plan doesn't set → stays real); 2FA + email-confirm/
  reset read `UtcNow` directly (unaffected). So only cookie/external-OAuth/security-stamp timing drifts, non-prod
  only (prod binds System). **Earlier sub-agent claim that email-confirm/reset/2FA break is WRONG.** Cheap guard:
  pin auth options to `TimeProvider.System`, or inject the business clock under a distinct type/keyed service.
- **S2 — `hooks.client.ts` must be CREATED (doesn't exist); root layout uses runes** (`$effect`/`onNavigate`, no
  `onMount`). `transformPageChunk` slots into the single existing `handle`/`resolve` (`hooks.server.ts:38,94`) —
  `resolve(event, { transformPageChunk })`, no `sequence`. Mount `SimClockPanel` in root `+layout.svelte` (covers
  all 5 route groups). Placeholder above `%sveltekit.head%` (`app.html:94`).
- **S3 — Dev controllers ship in the prod binary** (runtime 404-gated, not compiled out). Prod behavior unchanged;
  just don't claim byte-identical artifacts.
- **S4 — Keep the audit timestamp on SIM (affirms D5).** Required by spec (SF-B/AC#2); safe — one `now` per
  unit-of-work, no uniqueness/monotonic dependency, every audit `OrderBy(Timestamp)` has `.ThenBy(Id)`.

## Corrections (verified)
- **RLS exclude-from-policy accurate + sufficient.** Per-table allowlist (`20260615155023_AddRlsTenantIsolation…:35-66,118-144`)
  + a 2nd migration (`20260618110855_AddAccountingRls.cs`). Global tables (Outbox/Heartbeat/StripeWebhook) already
  handled this way (":68-75 Intentionally NOT RLS-scoped"). Omit SimulationClock/SimWorkerCommand from BOTH. No FORCE-all catch-all.
- Shared-infra-in-Api + Engine hosted service: correct. `AppTimeZoneProvider(IConfiguration, IClockStateProvider?=null)` resolves when disabled.
- `Core.Tests` + FakeTimeProvider: correct pair; add the package.
- **`SqliteTestContext` EXISTS** (`TestCommon/SqliteTestContext.cs`, in-memory SQLite via `AutomationTestDbContext`) — A4's "PG-only, avoid SQLite" caveat is WRONG; A4's ClockStateProvider test can use it.
- Integration harness does NOT boot the Engine (B4) — plan's hedge correct; or drive `SimWorkerCommandWorker.ExecuteCycleAsync` directly against the Testcontainers PG.
- `app.html`/`transformPageChunk` correct for SvelteKit 5; inline `Date` subclass safe (`instanceof` holds `DataGrid.svelte:195,202`; no structuredClone/date-libs). Only M3 auth math breaks.
- **Registry method names differ per service (B3):** `GenerateAsync` (RentCharge/DebtService/RecurringExpense/RecurringMaintenance),
  `AssessAsync` (LateFee), `ChargeDueAsync` (Autopay), `RemindAsync` (LeaseExpiry), `GenerateAllAsync` (NoticeDraft),
  `EnqueueDueAsync(DateTime? utcNow=null, ct)` (DailyBriefing — pass null to use the injected sim clock). Registry lambdas must hardcode the right name.
- Claim-race impossible (single Engine instance via advisory lock); optimistic claim is belt-and-suspenders.
- **`ScanProcessingWorker` is in `Engine/Workers/ScanProcessingWorker.cs:379`**, not `Api/Scanning` (D4 mislabel). `ScanService.cs:1701` correct. All 5 `App:TimeZone` citations match. `AccountingConnectionService:164/362` = OAuth state-token TTL (keep-real).
- `SimWorkerCommand` `Guid` PK deviates from int-key convention — fine for a dev queue table.

**Bottom line:** proceed; land M1–M6 into task text first. M1/M2/M5 are guaranteed walls; M4 fails an acceptance criterion; M3/M6 are correctness/security bugs.
