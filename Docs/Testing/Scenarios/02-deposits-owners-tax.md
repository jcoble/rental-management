# Scenario 02 — Security Deposits, Owners Report, Tax/Year-end

**Domain:** Money (second slice). **Suggested tester session:** `tester1` (run after 01) or `tester2`.

## Mission
Test the money flows that sit beside rent: holding and disbursing **security deposits**, the
**owners report / owner statements**, and **tax / year-end**. These touch real liabilities and
owner trust — a deposit that can be refunded twice, or an owner statement that double-counts, is
high severity.

## Get acquainted with the code first
- Frontend: `web/src/routes/(protected)/deposits/`, `owners/`, `owners-report/`, `tax/`.
- Web API client: `web/src/lib/api/endpoints/securityDeposits.ts`, `owners.ts`, `reports.ts`.
- API: `SecurityDepositsController.cs`, `OwnerEntityController.cs`, `ReportsController.cs`,
  `AccountingController.cs`.
- Services under `Services/Domain/` for deposits, owners, reports. Confirm any statement/report
  totals and groupings are computed **DB-side** (one SQL query / a view), not by materializing rows
  and summing in C#.
- Entities: `Core/Entities/SecurityDeposit*.cs`, `OwnerEntity.cs`, and related charge/ledger types.

## Flows to exercise
1. **Security deposit lifecycle**: record a deposit held for a lease/tenant, add a deduction (e.g.
   damage), then refund/disburse the remainder. Confirm `held − deductions = refundable`, and that
   you cannot refund more than held or refund twice.
2. **Owners report**: open an owner's statement/report. Confirm income, expenses, and net
   reconcile with the underlying payments/expenses, and that filtering by date or property gives a
   consistent, correct subtotal (not a wrong total under filter — aggregation smell).
3. **Tax / year-end**: open the tax/year-end view. Confirm the categorized totals match the
   accounting data and the selected year actually filters server-side.

## Watch especially for
- Deposit math that doesn't add up, negative refundable, or refunding before move-out when the code
  intends to block it.
- Owner statement double-counting a payment or expense, or omitting one.
- Report/summary totals that are correct unfiltered but wrong when a property/date filter is applied
  (classic in-memory grouping defect) — flag with the service file:line.
- Money formatting / rounding drift between the deposit view, owner statement, and accounting.

## Data hygiene
Use marker `QA-T1-<HHMMSS>` on anything you create. Don't disburse/refund deposits on seed leases
you didn't set up; create your own deposit record to exercise the lifecycle.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/02-deposits-owners-tax.md`
