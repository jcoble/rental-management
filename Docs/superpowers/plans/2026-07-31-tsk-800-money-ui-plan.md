# TSK-800 Money UI Implementation Plan (Web + Mobile)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild every Money surface (global Money, Unit Money, tenant portal, reports, record
detail screens, and their mobile mirrors) on top of the new double-entry ledger so a landlord sees
plain-language ledgers while staff can drill from any row to its balanced journal entry.

**Architecture:** The backend (TSK-803 lanes A/B) owns every computed number; clients render
server-returned values only. Web work extends the existing SvelteKit 5 (runes) screens in place —
no parallel routes — reusing `DataGrid`, TanStack Query keys, the SignalR invalidation bridge, and
the M3 token system. Mobile mirrors the same endpoints in the existing Flutter feature folders.
A small set of new shared ledger components (row anatomy, month groups, accounting-impact block,
journal drill-in) carries the visual language across all surfaces.

**Tech Stack:** SvelteKit 5 runes, TanStack Query, Tailwind 4 via CSS tokens (`--m3c-*`), SignalR,
Flutter (existing `mobile/` app), frozen TSK-803 DTOs.

**Companion contract:** `Docs/superpowers/plans/2026-07-31-tsk-803-accounting-backend-and-money-ui-contract.md`
("the contract"). Its "Fable frontend contract" section is binding; this plan turns it into tasks.

## Global constraints

- **Hard gate:** no task below starts until the TSK-803 API DTOs are frozen (backend lane B
  merged). Task 0 verifies the freeze.
- **No client-side financial math.** No 3/6/9/12 totals, running balances, aging, statement totals,
  or grouping computed in Svelte or Dart. Every displayed number arrives in a DTO field. A correct
  value computed in memory is still a defect.
- **Plain language everywhere.** Row types read "Charge", "Payment received", "Credit", "Expense",
  "Bill", "Bank transfer", "Loan payment", "Owner activity", "Reversal". Debit/credit vocabulary
  appears only inside journal drill-in and accounting reports, never as a required input.
- **Two dates on every ledger row:** effective date (primary) and entered/posted date (secondary).
- **Extend, never fork.** Modify the existing routes/components named below; no `v2` screens, no
  new route trees for existing surfaces.
- **Enums are string names** end-to-end; render exactly the string the API returns via a label map,
  never `enum.toString()` cleverness.
- **Capability gating fails closed** through the existing policy
  (`web/src/lib/auth/experience-policy.ts:191-208`, `295-309`). Journal drill-in and Chart of
  Accounts admin require staff accounting capability; the tenant portal never receives journal,
  account-configuration, or other-tenant data.
- **Query keys and invalidation** follow the existing feature-key pattern
  (`web/src/lib/realtime/invalidate-keys.ts:20-45`); every new key registers in the SignalR bridge.
- **M3 tokens only** (`web/src/app.css:21-100`); dark theme is the generated default, light comes
  from the `.light` override — verify every new surface in both.
- **Testing posture:** this project defers broad UI test suites. Each task ends with a live-proof
  checklist (dev server, seeded data) instead of component tests; the final task is the physical
  Samsung verification pass required by the contract.
- **Commits:** plain subject + body, no AI attribution, one task per commit minimum.

## Design direction

One ledger language across every surface, tuned for a non-technical landlord first:

- **Row anatomy (all ledgers):** left = effective date + type badge; middle = plain-language title
  ("February rent", "ACH payment", "Late fee reversal") with source context line beneath
  ("Recurring · Lease #12" / "Scanned bill · Home Depot"); right = signed amount in
  `tabular-nums` plus running balance where the surface has one. Reversed rows render struck-through
  amount + "Reversed" badge linking both directions of the lineage.
- **Charges vs money-in never share a column.** Ledger tables keep separate Charges and
  Payments/credits columns (matching the contract's tenant-ledger table) or, on cards/mobile,
  color-coded signed amounts: charges neutral, payments `--m3c-success`, credits/info
  `--m3c-info`, reversals muted.
- **Running balance is a first-class column** ("Amount owed" in tenant language, "Balance" on
  account ledgers) — rendered only where the API defines one: tenant/unit rows and single-account
  ledgers. The mixed global grid shows none (contract rule).
- **Drill-in is a right-side sheet on web** (existing `Dialog` pattern, full-height variant), a
  full screen on mobile. Layered disclosure: what happened → allocations → documents/bank match →
  audit trail → "Accounting impact" (staff only).
- **"Accounting impact" block:** a compact bordered card listing balanced lines as
  "account name — debit/credit amount", totals row, and a "Balanced ✓" affirmation; expandable to
  the full journal detail (entry id, dates, actor, lineage). Same component everywhere.
- **Month groups** (Unit Money, portal, mobile unit money): sticky month header with opening
  balance, itemized rows, footer with charges/payments/credits subtotals + closing balance — all
  server-supplied. Period switch is a 3/6/9/12-month segmented control driving the query, not a
  client filter.
- **Numbers:** `tabular-nums` everywhere, two decimals, currency symbol from the DTO, negative as
  "−1,102.50" (typographic minus), never parentheses (landlord-first, not accountant-first).

## Task 0: DTO freeze verification

**Files:** none created — read `web/src/lib/api/endpoints/accounting.ts`,
`tenant-accounts.ts`, `portal.ts`, `reports.ts` against the merged backend.

- [ ] Confirm backend lane B is merged and the endpoints in the contract's "API contract freeze"
      section respond on the dev API (`./scripts/start-dev.sh`, then curl each with the seeded
      admin token): chart-of-accounts CRUD, general-ledger, journal-entries/{publicId},
      trial-balance, balance-sheet, income-statement, tenant-account ledger/month-summary,
      recurring-charge CRUD.
- [ ] Record each response shape in `web/src/lib/api/types.ts` (or the existing type module the
      endpoint files import) as TypeScript interfaces copied field-for-field from the DTOs.
- [ ] Any missing field required by the contract's "Required tenant-ledger row fields" /
      "Required journal detail fields" lists → stop, report to the controller; do not synthesize
      client-side.

## Task 1: Shared ledger components

**Files:**
- Create: `web/src/lib/components/ledger/LedgerTypeBadge.svelte`
- Create: `web/src/lib/components/ledger/LedgerAmount.svelte`
- Create: `web/src/lib/components/ledger/MonthGroup.svelte`
- Create: `web/src/lib/components/ledger/AccountingImpactBlock.svelte`
- Create: `web/src/lib/components/ledger/JournalEntryDetail.svelte`
- Create: `web/src/lib/components/ledger/LedgerRowSheet.svelte`
- Create: `web/src/lib/components/ledger/labels.ts`
- Modify: `web/src/lib/api/endpoints/accounting.ts:65-111` (add journal-entry + ledger accessors)

**Interfaces (produced for every later task):**
- `labels.ts`: `ledgerTypeLabel(type: string): string`, `ledgerTypeTone(type: string):
  'charge'|'payment'|'credit'|'expense'|'bank'|'loan'|'owner'|'reversal'` — single source of the
  plain-language map.
- `LedgerTypeBadge` props: `{ type: string }`.
- `LedgerAmount` props: `{ amount: number, currency: string, tone?: string, struck?: boolean }`.
- `MonthGroup` props: `{ month: MonthSummaryDto, children: Snippet }` renders sticky header
  (label + opening), slot rows, footer (subtotals + closing) — all values from the DTO.
- `AccountingImpactBlock` props: `{ journalEntryPublicId: string, compact?: boolean }` — fetches
  `GET /accounting/journal-entries/{publicId}` under query key
  `['journal-entry', portfolioId, publicId]`, renders lines/totals/balanced state, expands into
  `JournalEntryDetail`.
- `JournalEntryDetail` props: `{ entry: JournalEntryDto }` — full field list from the contract:
  description, effective/posted dates, source link, actor, idempotency identity, lines with
  account code/name + dimensions, totals, lineage, audit link, documents, bank evidence.
- `LedgerRowSheet` props: `{ row: LedgerRowDto, onClose }` — the layered drill-in shell used by
  Tasks 2/3/4; staff-only sections gated via `hasCapability`
  (`web/src/lib/stores/auth.svelte.ts:106-113`).

- [ ] Build `labels.ts` with the full type map; every string reviewed against the plain-English
      rule (no internal enum names leaking).
- [ ] Build the six components with loading/error/empty states (reuse `LoadingState`).
- [ ] Register `journal-entry` in the SignalR invalidation bridge
      (`web/src/lib/realtime/invalidate-keys.ts:20-45`) so payment/expense events refresh open
      drill-ins.
- [ ] Live proof: render `AccountingImpactBlock` for one seeded payment and one seeded expense on
      a scratch route or Storybook-style harness page, both themes, then delete the harness.
- [ ] Commit.

## Task 2: Global Money history upgrade (`/accounting`)

**Files:**
- Modify: `web/src/routes/(protected)/accounting/+page.svelte:79-139` (filters),
  `:141-167` (query), `:623-712` (columns), `:729-839` (cells + drill-in)
- Consume: Task 1 components; `GET /accounting/transactions` (upgraded DTO),
  `GET /accounting/chart-of-accounts` for the account filter.

- [ ] Replace the flat category filter with a working account/category filter fed by the chart of
      accounts (income + expense accounts grouped by type); keep it URL-persisted like the existing
      filters (`:79-139`).
- [ ] Extend the type filter + `LedgerTypeBadge` rendering to distinguish charge, payment received,
      credit, expense/bill, bank movement, loan payment, owner activity, and reversal.
- [ ] Columns become: Effective date, Entered (secondary style), Type badge, What happened
      (title + source context line — never a raw business key), Paid by/to, Charges,
      Payments/credits, Account, Property, Status, Receipt, actions. **No global running-balance
      column.** Tenant-account rows show their tenant running balance inside the drill-in sheet.
- [ ] Row click opens `LedgerRowSheet` (replacing the scatter of `recordHref` navigations at
      `:238-247` for in-place detail; keep a "Open full record" link inside the sheet to the
      existing detail routes so deep links still work).
- [ ] Preserve server paging/sort through `DataGrid` exactly as today (`:1118-1145`).
- [ ] Live proof: filters combine (account + type + property + dates), pagination stable, reload
      restores URL state, drill-in shows source, allocations, bank match, documents, audit,
      accounting impact; adjacent-role user sees no journal section.
- [ ] Commit.

## Task 3: Unit Money ledger (`units/[id]` Money tab)

**Files:**
- Modify: `web/src/lib/components/unit/tabs/RentTab.svelte:72-113` (queries), `:284-391`
  (rendering), `:203-250` (mutations)
- Modify: `web/src/lib/components/unit/tabs/LedgerTab.svelte:43-65` (view wiring only if needed)
- Create: `web/src/lib/components/unit/tabs/RecurringChargeDialog.svelte`
- Consume: Task 1 `MonthGroup`, `LedgerRowSheet`; tenant-account ledger/month-summary endpoints;
  recurring-charge CRUD endpoints.

- [ ] Replace the flat activity/charge/receipt sections with `MonthGroup`-grouped ledger rows from
      the month-summary endpoint: opening balance, itemized charges (each named obligation — base
      rent, pet, parking, utilities, storage, concession — as its own line, never one collapsed
      rent number), payments, credits/adjustments, closing balance.
- [ ] Running "Amount owed" after every row, from the DTO.
- [ ] Add the 3/6/9/12-month segmented period switch driving the query key (server period param;
      pattern mirrors the portal period selector at
      `web/src/routes/(portal)/portal/payments/+page.svelte:241-304`).
- [ ] Keep existing record-payment/scan actions; add **Post charge** (existing manual-charge
      endpoint) and **Recurring charge** (new `RecurringChargeDialog`: name, amount, income
      account select defaulted by mapping, monthly due day, start/end, active toggle; list +
      deactivate existing schedules). Both gated by the same capability as payment manage
      (`web/src/lib/components/unit/money.ts:39-57` pattern).
- [ ] Row click opens `LedgerRowSheet`; reversal action stays, now showing lineage in the sheet.
- [ ] Live proof: month groups reconcile with portal view for the same account; recurring charge
      created → next-run occurrence appears after engine tick; adjacent role denied the new
      actions.
- [ ] Commit.

## Task 4: Tenant portal Money (`/portal/payments`)

**Files:**
- Modify: `web/src/routes/(portal)/portal/payments/+page.svelte:171-188` (drop the local
  sign/format helpers in favor of DTO fields), `:306-357` (table)
- Create: `web/src/routes/(portal)/portal/statement/+page.svelte` (print/download statement view)
- Consume: portal history endpoints (upgraded DTO), Task 1 `LedgerAmount`, `MonthGroup`.

- [ ] Upgrade the five-column table to the contract's tenant table: Date, What happened, Charges,
      Payments/credits, Amount owed — grouped by month with opening/closing rows.
- [ ] Row drill-in (tenant-safe subset of `LedgerRowSheet`): receipt details, allocations
      ("applied to February rent"), documents. **No journal, no account config, no other tenants.**
- [ ] Statement page: server-generated rows for a selected period, print stylesheet, download via
      the existing statement/CSV endpoint if present in the frozen DTOs (otherwise print-only and
      note it).
- [ ] Keep Pay Now/autopay flows untouched (`:75-169`).
- [ ] Live proof: parity check — same account rendered in Unit Money and portal shows identical
      server numbers; print preview clean in both themes.
- [ ] Commit.

## Task 5: Reports — real accounting reports (`/reports`)

**Files:**
- Modify: `web/src/routes/(protected)/reports/+page.svelte:50-67` (catalog),
  `web/src/lib/reports/report-display.ts:1-28` (labels)
- Modify: `web/src/routes/(protected)/reports/[report]/+page.svelte:98-168`, `:626-657`
  (viewer configs)
- Modify: `web/src/lib/api/endpoints/reports.ts:435-454` (new report accessors)
- Consume: trial-balance, balance-sheet, income-statement, general-ledger, chart-of-accounts
  endpoints.

- [ ] Rename the pseudo-General Ledger card to "Complete money history" permanently (it already
      carries that display label — make the route key/label consistent) and add the real reports:
      Chart of Accounts, General Ledger (account-filtered, with per-account opening/running
      balance from the server), Trial Balance (debit/credit columns, balanced footer), Balance
      Sheet, Income Statement.
- [ ] General Ledger filters: account, property, unit, source type, effective range, search —
      server-side, URL-persisted, same param pattern as the existing viewer (`:98-139`).
- [ ] Journal-entry rows in the General Ledger link into `JournalEntryDetail` (staff capability).
- [ ] Schedule E surfaces (`/tax`) get an explicit "Cash basis — tax oriented" caption
      (`web/src/routes/(protected)/tax/+page.svelte:99-181`); accrual reports get effective-date
      captions. No Rent Roll (TSK-801).
- [ ] CSV/print reuse the existing generic viewer machinery (`:98-168`).
- [ ] Live proof: trial balance foots to zero on the converted simulation portfolio; balance sheet
      equation holds; drill from income-statement line → general ledger → journal entry → source.
- [ ] Commit.

## Task 6: Chart of Accounts administration

**Files:**
- Create: `web/src/routes/(protected)/accounting/accounts/+page.svelte`
- Modify: `web/src/routes/(protected)/accounting/+page.svelte:55-67` (add "Accounts" tab or
  header link)
- Consume: chart-of-accounts CRUD endpoints, capability gate.

- [ ] Grouped list by account type (Asset → Expense) showing code, name, Schedule E mapping,
      system badge, active state, "has history" state — server-supplied flags.
- [ ] Add account (code auto-suggested in type range, name, type, optional parent, optional
      Schedule E mapping), rename, deactivate. System accounts: rename only — destructive controls
      absent, not merely disabled, with a sentence explaining why. Accounts with posted lines
      cannot be retyped/deleted (server enforces; UI explains).
- [ ] Live proof: create → appears in global Money account filter and expense form selects;
      deactivated account hidden from pickers but intact in history.
- [ ] Commit.

## Task 7: Accounting impact on record details

**Files:**
- Modify: `web/src/lib/components/records/PaymentDetail.svelte:40-103`
- Modify: `web/src/lib/components/records/ExpenseDetail.svelte:50-124`
- Modify: the loan, deposit, and owner detail components in `web/src/lib/components/records/`
  (locate by the pattern above) and the owner statement drill-in at
  `web/src/routes/(protected)/owners-report/+page.svelte:40-88`
- Consume: Task 1 `AccountingImpactBlock`; each record DTO's `journalEntryPublicId`.

- [ ] Each detail view shows mapped account name inline and a compact `AccountingImpactBlock`
      (staff capability), expanding to `JournalEntryDetail`.
- [ ] Forms keep sensible defaults: account selects appear only where source facts cannot
      determine the account or the user opens an "Advanced" override — audit every existing money
      form (expense create at `ExpensesTab.svelte:154-208`, payment record, deposit, loan) against
      this rule.
- [ ] Live proof: expense with account override posts to the chosen account; reversal from
      PaymentDetail shows linked opposite entry.
- [ ] Commit.

## Task 8: Mobile parity (Flutter)

**Files:**
- Modify: `mobile/lib/features/money/money_screen.dart:28-138`,
  `money_repository.dart:213-362`
- Modify: `mobile/lib/features/units/unit_command_center_screen.dart:2479-2684`
- Modify: `mobile/lib/features/portal/tenant_account_history_screen.dart:9-355`,
  `tenant_portal_repository.dart:604-699`
- Create: `mobile/lib/features/money/widgets/ledger_type_badge.dart`,
  `accounting_impact_block.dart`, `journal_entry_screen.dart`, `month_group.dart`

**Sequencing:** after Tasks 1–5 stabilize the DTO usage; one lane, builds serialized.

- [ ] Global Money tab: corrected type badges/labels (same label map, ported), dual dates, account
      filter, row drill-in screen mirroring `LedgerRowSheet` sections.
- [ ] Units > Money: month groups with opening/closing + running balance (server values), manual
      charge + recurring charge access for authorized staff (same fields as web dialog).
- [ ] Tenant money: identical server values as portal web; drill-in with allocations and receipts.
- [ ] Record details (expense, receipt, loan, deposit, owner money): plain-language accounting
      impact block; read-only journal drill-in for staff. No COA editing on mobile.
- [ ] Live proof on emulator; physical-device proof deferred to Task 9.
- [ ] Commit per surface.

## Task 9: Live verification pass (contract acceptance)

- [ ] Web, against the converted simulation database: every checklist item under the contract's
      "Frontend acceptance" — global Money clarity/filters/pagination/reload/drill-in; Unit Money
      monthly + running balance + manual/recurring charge; tenant history + statement parity;
      expense/payment/deposit/loan impact drill-ins; staff authorization and adjacent-role denial.
- [ ] Physical Samsung: fresh APK build, same checklist on mobile surfaces; record APK hash and
      device per the TSK-754 handoff requirements.
- [ ] Parity audit: pick three rows per surface, verify web value = mobile value = database ledger
      value (SQL), journal balanced, audit + outbox rows present.
- [ ] Browser/session cleanup per global rules; report results with receipts.

## Sequencing and dependencies

```
Task 0 (freeze gate)
  └─ Task 1 (shared components)
       ├─ Task 2 (global Money)   ─┐
       ├─ Task 3 (unit Money)      ├─ Task 7 (record details)
       ├─ Task 4 (portal)          │
       ├─ Task 5 (reports)        ─┘
       └─ Task 6 (COA admin)
Task 8 (mobile) after 1–5 · Task 9 last
```

Tasks 2–6 are parallel-safe for non-build work but share `+page.svelte` files only within their own
task; no two tasks modify the same file. Mobile is a single serialized lane.

## Self-review record

- Contract coverage: every numbered item in the contract's "Web management surfaces" (1→Task 2,
  2→Task 3, 3→Task 4, 4→Task 5, 5→Task 7, 6→Task 6) and "Mobile surfaces" (1-5→Task 8) has a task;
  acceptance list → Task 9; no-client-math rule → global constraints + Task 0.
- No placeholders: every task names exact files with recon-verified anchors; the one deliberate
  conditional (statement download endpoint) states its fallback.
- Naming consistency: `LedgerRowSheet`, `AccountingImpactBlock`, `JournalEntryDetail`,
  `MonthGroup`, `labels.ts` are used identically across Tasks 1–8.
