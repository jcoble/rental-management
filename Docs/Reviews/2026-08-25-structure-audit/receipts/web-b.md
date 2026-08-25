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
