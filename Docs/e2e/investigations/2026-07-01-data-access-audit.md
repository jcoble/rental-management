# Data-Access Audit — Api + Engine (2026-07-01)

**Source:** DataAccessAuditor (read-only). **Verdict:** read/reporting side strong and DB-side clean
(prior remediation pass evident). Real defects concentrated in **Engine workers** (per-item N+1 + one
unbounded growing-table load with in-memory filter) + a few unpaged materializations + **10 missing
indexes**. All fixes are mechanical — no product decision needed. Priority: **C1 → H2/H3 → H1 → indexes
#1/#2/#3 → M1 → M2**.

## Confirmed DB-side clean
- **ReportsService** — all 12 reports filter+group+sum DB-side (in-memory only reshapes bounded aggregates).
  *Caveat:* running-balance correlated subqueries `:339-344`, `:945-949` are O(n²) SQL (watch-item).
- **ScheduleEService** — clean; `foreach :123` is depreciation math over ONE query (no N+1).
- **OwnerStatementService** — clean; *caveat:* non-sargable `.Year ==` at `:50,59,184` (use half-open ranges).
- **AccountingService** — reads clean (view `vw_accounting_transactions`). Two non-agg issues: `GetReportsAsync`
  unpaged (LOW); `AccountingImportService` write-side N+1 (M1).
- Also clean: Dashboard/UnitDashboard, DailyBriefing**Service** (not Delivery), SecurityDeposit, OpeningBalance,
  `PaymentAttentionQueryExtensions`, RentCharge/RecurringExpense/RecurringMaintenance generation,
  OutboxDispatchWorker, EngineRlsInterceptor.

## Ranked findings
### CRITICAL
- **C1 — `DailyBriefingDeliveryService.AlreadyQueuedAsync` (`Engine/Services/DailyBriefingDeliveryService.cs:121-126`, called `:60`).**
  Loads ALL historical sms+email `OutboxMessage.Payload` for the portfolio (no date filter, no limit) and
  `JsonDocument.Parse`s each in C# to find one dedup row. Hourly, ~18×/day/portfolio, unbounded over time.
  Two rule breaks: unbounded growing-table load + in-memory filter. **Fix:** indexed `DedupKey` column =
  `"daily-briefing:{portfolioId}:{dateKey}"` → `AnyAsync(m => m.DedupKey == key)`; or server-side `jsonb`
  (`Payload->>'purpose'`/`'date'`) + expression index, terminated by `AnyAsync`.

### HIGH
- **H1 — `LeaseService.GetLedgerAsync` (`Api/Services/Domain/LeaseService.cs:708-727`→`746-804`)** *(also = the grid lease-payments defect).*
  Materializes a lease's full payment history unpaged, expands in memory via `SelectMany`. Grows unbounded per
  tenancy. Totals `:811-829` are already a DB `GroupBy(_=>1)` Sum (so row-transfer/paging defect, not wrong value).
  **Fix:** `.Skip/.Take` before `ToListAsync`; `SelectMany` over the page; emit opening-balance anchor separately;
  back with covering index #5.
- **H2 — `AutomationNotifier.StaffUserIdsAsync` (`Engine/Services/AutomationNotifier.cs:150-158`, called `:54-55`).**
  A 3-table staff join (Users⋈UserRoles⋈Roles) re-run per notification, inside per-item loops
  (`RentChargeService.cs:176`, `LateFeeService.cs:221`, `LeaseExpiryReminderService.cs:138`) → O(active leases)
  identical joins. **Fix:** memoize per portfolio (`Dictionary<int,IReadOnlyList<int>>`) or pass a precomputed set.
- **H3 — `LateFeeService.cs:213` (inside `foreach` `:110`).** `Tenants.FirstOrDefaultAsync` per overdue payment.
  **Fix:** `.Include(p=>p.Lease).ThenInclude(l=>l.Tenant)` or batch-load tenants into a dict.

### MEDIUM
- **M1 — per-row `SaveChanges` write N+1** in `AccountingImportService` Import/Promote loops (`:481-482` loop `:440`;
  `:562-563` loop `:509`; `:616-617` loop `:586`; `:667-668` loop `:634`). Full QB backfill = thousands of round-trips.
  **Fix:** two-phase — `Add` all, one `SaveChanges` (Npgsql batches + populates Ids), second pass builds sync-map rows, one more save.
- **M2 — `PortfolioQaService.ListOverdueRentAsync:633-654`** — uncapped `.ToListAsync()` into an LLM tool result
  (every sibling caps at `Take(51)`). **Fix:** `.Take(51)` + `truncated` flag.
- **M3 — `LeaseExpiryReminderService.cs:100,107-110`** — per-lease `Owners.FindAsync` + user-account fallback.
  **Fix:** `.Include(l=>l.Property).ThenInclude(p=>p.Owner)`; batch fallback emails. (Also `FindAsync` on Owners bypasses soft-delete filter — latent.)
- **M4 — `AutopayChargeService.cs:83-86,96-97`** — 2 `PaymentTransaction` lookups per candidate. **Fix:** batch via `Contains(paymentIds)`.
- **M5 — `DebtServiceService.cs:100,103-106`** — per-period existence check in the amortization loop (360 lookups for a 360-mo mortgage on first gen). **Fix:** preload `(PeriodKey,BalanceAfter)` per loan into a dict.
- **M6 — `ScanProcessingWorker.cs:119-120`** — `StoredFiles.FirstOrDefaultAsync(f=>f.FilePath==…)` on an UNINDEXED column; 2 s poll. **Fix:** index `StoredFile.FilePath` (#7) or carry content-type on the draft.

### LOW (correctness-safe; hygiene)
- `BankingService` per-transaction audit `SaveChanges` N+1 (`:337-373`, `:530-540`; `AuditTrailService.cs:65,83`) — bulk audit path.
- `AccountingService.GetReportsAsync/ReportLedgerQuery :835-839` — unpaged portfolio ledger materialization (add paging / date-range floor).
- **Dead in-memory matcher** `BankingService.cs:1056-1144` (`SuggestMatch`/`ScorePayment`/`ScoreExpense`) — NO callers → **delete**.
- `PortfolioQaService` list tools `:674,784,814,902` uncapped-but-bounded — add `Take(maxRows+1)` + flag.
- `OutboxMessagePublisher.PublishAsync:39` per-publish save (×3/notification, multiplied by H2/H3).
- `AnalyticsService.cs:168-171` in-memory `.OrderBy(Priority)` over GROUP BY output (bounded ~4-5 rows) — order DB-side.
- `AccountingImportService` Map* `:263,296,335` re-materialize candidate list per row — hoist above loop.
- `ScheduledOwnerStatementWorker.cs:127` re-loads the owner it already has (`OwnerStatementEmailService.cs:38-42`).

### Slow-query / translation watch-items (compliant, flag)
- Running-balance correlated subqueries `ReportsService.cs:339-344,945-949` — O(n²) SQL → window function / view at scale.
- Non-sargable `.Year ==` `OwnerStatementService.cs:50,59,184` → half-open ranges.
- Verify translation: `AccountingService.GetYearEndPacketDataAsync:1401-1417`, `PortfolioQaService.ListExpiringLeasesAsync:793-804` (formats in the server projection).
- `COALESCE(PaidDate,DueDate)` date ranges — DB-side but non-sargable (expression index if slow).

## Index recommendations (verified vs the EF model snapshot)
| # | Table | Columns | Serves |
|---|---|---|---|
| 1 | Payment | `(PortfolioId, PaidDate)` | **No PaidDate index today.** Collected-MTD, cash-flow/GL/ScheduleE income, owner statements, recent-payments. **Highest value.** Also powers grid PaidDate filter. |
| 2 | Expense | `(PortfolioId, IncurredAt)` + `(PortfolioId, PaidAt)` | Every financial report; grid Expense date filter. |
| 3 | Expense | `(PortfolioId, PropertyId, Category, IncurredAt)` | ScheduleE + property P&L group-by-Category. |
| 4 | Payment | `(PortfolioId, Status, DueDate)` | Overdue/receivables (PortfolioQa, Dashboard, PaymentAttention). |
| 5 | Payment | `(PortfolioId, LeaseId, PaidDate, DueDate, Id)` covering | Paged lease ledger (H1), order `LedgerDate desc, Id desc`. |
| 6 | Lease | `(PortfolioId, Status, EndDate)` or partial `(EndDate) WHERE ExpiryReminderSentAt IS NULL AND Status=Active` | Expiry sweep + lease-expiration report. |
| 7 | StoredFile | `(FilePath)` | ScanProcessingWorker:119 (M6). |
| 8 | OutboxMessage | new `DedupKey` col + index (or jsonb expr index) | C1 fix. |
| 9 | RecurringMaintenanceTask | `(IsActive, NextDueDate)` | Sweep is cross-portfolio; existing `(PortfolioId,…)` is a skip-scan. |
| 10 | AccountingConnection | `(Status)` (+ TokenExpiresAt) | Both accounting workers filter Status; scale note. |

Already well-covered (no action): AuditLog, OutboxMessage unsent, LoanPayment, RecurringExpense, PaymentTransaction, AutopayEnrollment, Accounting maps, BankTransaction.

## Coverage
Audited in full: all named non-list services + all 17 Engine services + 16 workers + EngineRlsInterceptor, cross-referenced vs the EF snapshot. **Follow-up:** verify `NoticeDraftService.GenerateAsync` (Api) lease sweep + open-draft idempotency is set-based (Engine wrapper clean). The 12 list/grid services were pre-cleared by GridFilterScout.

## Orchestrator disposition
Becomes the **data-access hardening track** (pre-test): all mechanical, no user decision. Shares the Payment/Expense
**date + category indexes (#1/#2/#3)** with the grid date-filter work → do those indexes once, serving both. Serializes
on the build slot with the clock/gaps. H1 == the grid lease-payments defect (one fix). Sequence: C1 → H2/H3 → H1 →
indexes #1/#2/#3 → M1 → M2 → the rest.
