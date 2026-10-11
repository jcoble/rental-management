# Scenario 01 — Money & Leases (lease lifecycle + tenant-account ledger + accounting)

**Domain:** Money & leases. **Suggested tester session:** `tester1`.

## Mission
Exercise the financial core a landlord lives in: create/advance a lease, post charges and receipts
to its tenant account, and confirm every number reconciles across the Unit, Lease, Ledger, and Accounting
views. Money that doesn't reconcile, or status that lies about reality, is the worst class of bug
here.

## Get acquainted with the code first
- Frontend: `web/src/routes/(protected)/leases/` (list + `[id]`), `web/src/routes/(protected)/units/[id]/+page.svelte`
  (Rent tab), tenant-account entry detail, and `web/src/routes/(protected)/accounting/+page.svelte`.
- Web API clients: `web/src/lib/api/endpoints/leases.ts`, `payments.ts`, `tenant-accounts.ts`, and `accounting.ts`.
- API: `LeaseController.cs`, `TenantAccountsController.cs`, `TenantAccountMoneyController.cs`, and `AccountingController.cs`.
- Services: lease-management, tenant-account query/money services, and accounting query services.
  Confirm all filtering, sorting, paging, and totals remain one translated SQL query per read surface.
- Entities: lease-management lifecycle, `TenantAccount`, `TenantLedgerEntry`, allocations, and provider attempts.

## Flows to exercise
1. **Create a lease** for a unit that doesn't already have a current lease (or create a property+unit
   first). Set tenant, rent, deposit, due day, start/end dates. Confirm it shows as the unit's
   current lease and the Rent tab becomes usable.
2. **Post a charge and one or more receipts** to the lease's tenant account (method, amount,
   reference, effective/due dates). Confirm the ledger updates without a manual refresh and amounts match.
3. **Reconcile**: do the Unit Rent tab, Lease detail, exact tenant-account entry detail, and Accounting
   summary agree on amount, date, method, and tenant-account identity?
4. **Correct** a posted entry with a supported reversal, refund, or adjustment. Confirm both the
   original and correction remain visible and the accounting balance reflects the net effect.
5. **Edge cases**: $0 / negative / very large charge or receipt; missing tenant account; due day like
   31; lease end before start; duplicate idempotency key or external reference.

## Watch especially for
- Entry attributed to the wrong tenant account, or an account picker that exposes a tenant/unit
  outside the portfolio (IDOR).
- Accounting `/summary` totals that change incorrectly under a date filter (in-memory aggregation
  smell) or don't include a payment you just recorded.
- Lifecycle/status labels that don't match the append-only ledger or lease-management state.
- Currency rounding / formatting inconsistencies across views.

## Data hygiene
Create your own records with the marker `QA-T1-<HHMMSS>` (property/tenant/lease/reference). Don't
delete seed data or edit records likely owned by the Operations tester.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/01-money-leases.md`
Use the BUG-N report format from `Docs/Testing/exploratory-tester.md`.
