Lane: web-b — one money formatter and one date formatter for the web app
Start: 2026-08-25T17:00:34-04:00

## Item 1 — one money formatter

`formatAccountingCurrency` (web/src/lib/accounting/accounting-display.ts) is now the only place
in `web/src` that constructs an `Intl.NumberFormat` with `style: 'currency'`. It gained one
optional third argument, `wholeDollars`, for the pages that deliberately drop the cents.

Deleted helpers: `money` (lib/components/unit/money.ts), `formatExpenseMoney`
(lib/accounting/expense-receipt-display.ts), `currencyFormatter` (lib/utils/status-labels.ts),
plus 20 per-file `money`/`fmtMoney`/`fmtCurrency`/`fmt`/`formatCurrency` functions.

Kept as thin wrappers because their empty/zero text differs from the canonical `—`, so deleting
them would change what the page shows:
- `formatAssistantMoney` → "unknown amount" (lib/assistant/actions.ts)
- `money` → "Rent not set" (lib/components/leasing/LeasingListPage.svelte)
- `money` → "Not set" (routes/(protected)/leasing/[record]/[id]/+page.svelte)
- `formatUsd` → "" (routes/(protected)/scan/[draftId]/+page.svelte)
- `usd` → "" (lib/components/records/ExpenseDetail.svelte)
- `money` on the dashboard and past-due pages → whole dollars via the new argument
- `money` on both portal pages → coerces a string amount before formatting

Fraction digits preserved everywhere. Whole-dollar sites (unchanged output): dashboard tiles
(routes/(protected)/+page.svelte), accounting/past-due, LeasingListPage asking rent, onboarding
unit summary rent. Every other site already resolved to two digits, which is what
`formatAccountingCurrency` produces.

Output changes recorded, both cosmetic-free:
- The two portal pages formatted with the viewer's locale (`toLocaleString(undefined, …)`);
  they now use `en-US` like the rest of the app. Same output for a US viewer.
- `formatLandlordAmount` in accounting-display.ts is left alone: it is a private helper in the
  canonical file with a deliberate "no cents when the amount is whole" rule.

`web/src/lib/accounting/accounting-display.ts` imported `./money-display` without the `.ts`
extension, which the node test runner cannot resolve. Adding the extension was required to make
the shared formatter importable from the plain-TypeScript modules that node tests load, and it
also repairs the pre-existing failure of `src/lib/accounting/accounting-display.test.ts`.

Verification: `rg -c "style: 'currency'" web/src` → `web/src/lib/accounting/accounting-display.ts:2`
(one file). `pnpm --dir web check` → 0 errors. `pnpm --dir web check:native` → clean.

## Item 2 — one date formatter

Replaced with the canonical `$lib/utils/date` functions (identical output):
- `lib/components/shared/DocumentsPanel.svelte` local `formatDate` → `formatDate`
- `routes/(protected)/owners-report/+page.svelte` local `formatDate` → `formatDateOnly`
- `lib/components/property/PropertyLoansSection.svelte` `fmtDate` → `formatDateOnly`
- `routes/(protected)/properties/[id]/+page.svelte` `fmtDateOnly` → `formatDateOnly`

Off-by-one bug fixes (date-only values that were being rendered in local time, so they showed
the previous day west of UTC; both now read the UTC calendar day, and the style changes from
`3/3/2026` to `Mar 3, 2026`):
- `routes/(protected)/scan/[draftId]/+page.svelte` loan-payment due date
- `lib/components/maintenance/UnitRecurringMaintenanceView.svelte` next due date

Left alone, with the reason (each would change what the page shows):
- The compact numeric UTC family `toLocaleDateString(undefined, { timeZone: 'UTC' })` renders
  `3/3/2026`, not the canonical `Mar 3, 2026`: `lib/components/data-grid/DataGrid.svelte:200`,
  `lib/components/records/ApplicationDetail.svelte:559`,
  `lib/components/unit/tabs/ApplicationsTab.svelte:94`,
  `routes/(admin)/admin/users/+page.svelte:80`,
  `routes/(protected)/accounting/+page.svelte:608`,
  `routes/(protected)/accounting/year-end/+page.svelte:248`.
- Date-and-time formats, which no canonical function produces:
  `routes/(protected)/maintenance/inspections/[id]/+page.svelte:372`,
  `lib/components/leases/AgreementSignatureProgress.svelte:82`,
  `lib/components/records/WorkOrderDetail.svelte:439`,
  `routes/(protected)/lease-templates/+page.svelte:183`,
  `lib/components/leasing/LeasingListPage.svelte:61`,
  `routes/(protected)/leasing/[record]/[id]/+page.svelte:67`,
  `routes/(protected)/appointments/[id]/+page.svelte:182`.
- Deliberately compact or special formats: the "Jun 3" reconciliation chips
  (`routes/(protected)/accounting/+page.svelte:601`), the long weekday briefing date
  (`routes/(protected)/ai/+page.svelte:62`), the blog publish dates
  (`routes/(public)/blog/+page.svelte:8`, `routes/(public)/blog/[slug]/+page.svelte:11`).
- Bare `new Date(x).toLocaleDateString()` on real timestamps (created/updated/last-message/
  requested/possession-given), which is numeric local and not what any canonical function
  returns: `lib/components/leases/LeaseManagementDetail.svelte:365,388`,
  `routes/(protected)/banking/+page.svelte:152` (commented as deliberately local),
  `routes/(protected)/owners/[id]/+page.svelte:298,304`,
  `routes/(protected)/tenants/[id]/+page.svelte:332`,
  `routes/(protected)/+page.svelte:319,357`.

Not covered by the spec, left in place: four copies of the same "long month + year, UTC" ledger
header formatter (`lib/components/accounting/PortalAccountHistoryRows.svelte:31`,
`lib/components/accounting/PortfolioLeaseLedgerPanel.svelte:14`,
`lib/components/accounting/TenantLedgerMonth.svelte:33`,
`lib/accounting/tenant-ledger-display.ts:18`) plus the private `formatAccountingMonthYear` in
`lib/accounting/accounting-display.ts:220`. Merging them needs a new exported function, which
this lane was not asked for.
