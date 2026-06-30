# Scenario 01 — Money & Leases (lease lifecycle + payments + accounting)

**Domain:** Money & leases. **Suggested tester session:** `tester1`.

## Mission
Exercise the financial core a landlord lives in: create/advance a lease, record rent payments
against it, and confirm every number reconciles across the Unit, Lease, Payment, and Accounting
views. Money that doesn't reconcile, or status that lies about reality, is the worst class of bug
here.

## Get acquainted with the code first
- Frontend: `web/src/routes/(protected)/leases/` (list + `[id]`), `web/src/routes/(protected)/units/[id]/+page.svelte`
  (Rent tab), `web/src/routes/(protected)/accounting/+page.svelte` and `accounting/payments/[id]/+page.svelte`.
- Web API client: `web/src/lib/api/endpoints/leases.ts`, `payments.ts`, `accounting.ts`.
- API: `LeaseController.cs`, `PaymentController.cs`, `AccountingController.cs` (`/summary`).
- Services: `Services/Domain/LeaseService.cs`, `PaymentService.cs`, `AccountingService*` (the
  summary aggregation — confirm totals are computed DB-side, not in memory).
- Entities: `Core/Entities/Lease.cs`, `Payment.cs`, and rent/charge related types + their enums.

## Flows to exercise
1. **Create a lease** for a unit that doesn't already have a current lease (or create a property+unit
   first). Set tenant, rent, deposit, due day, start/end dates. Confirm it shows as the unit's
   current lease and the Rent tab becomes usable.
2. **Record one or more rent payments** against that lease (method, amount, reference, paid/due dates).
   Confirm the payment list updates without a manual refresh (SignalR) and the amounts match.
3. **Reconcile**: does the Unit Rent tab, the Lease detail, the Payment detail, and the Accounting
   `/summary` all agree on amounts, dates, method, and which lease the payment belongs to?
4. **Edit** a payment you created and a lease field you created; confirm the change persists (re-open
   the page) and propagates to the accounting summary.
5. **Edge cases**: $0 / negative / very large rent or payment; payment with no lease selected; due day
   like 31; lease end before start; duplicate reference.

## Watch especially for
- Payment attributed to the wrong lease/tenant, or a lease picker that lets you pick a tenant/unit
  outside the portfolio (IDOR) or mismatched to the lease.
- Accounting `/summary` totals that change incorrectly under a date filter (in-memory aggregation
  smell) or don't include a payment you just recorded.
- Status badges (lease active/ended, payment posted) that don't match the DB.
- Currency rounding / formatting inconsistencies across views.

## Data hygiene
Create your own records with the marker `QA-T1-<HHMMSS>` (property/tenant/lease/reference). Don't
delete seed data or edit records likely owned by the Operations tester.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/01-money-leases.md`
Use the BUG-N report format from `Docs/Testing/exploratory-tester.md`.
