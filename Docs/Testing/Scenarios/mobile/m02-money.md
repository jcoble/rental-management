# Mobile scenario M02 — Money: rent, payments, expenses, deposits

## Purpose
The landlord checks who owes rent, records a payment and an expense from the phone, and reads a tenant's balance. Exercise the Money tab end to end.

## Read the implementation first
- mobile/lib/features/money/, payments/, accounting/, deposits/, banking/
- RentalCommand.Api/Controllers/PaymentController.cs, ExpenseController.cs, AccountingController.cs, tenant-account/ledger endpoints

## Rough exploration areas
- Money overview: totals, overdue list, this-month numbers — do they reconcile with the Today screen and with the API values?
- Record a rent payment for a sample tenant (amount, date, method); confirm it appears in the tenant's ledger and the overdue list updates. Prefix any note/reference with QA-20260827-m02.
- Record an expense (category, vendor, amount, receipt optional); reopen it from the list.
- Tenant account / ledger detail: entries, running balance, deposit held.
- Any "simple vs detailed" money mode switch; help text/tooltips.

## Edge cases worth trying
- Amount 0, negative, very large, decimals with a comma, future date, blank required fields; cancel mid-form; save twice quickly (duplicate?).

## What to verify visually
- Currency formatting, sign conventions (owed vs paid) obvious to a non-accountant; forms fit the screen with the keyboard open; success/error feedback visible.
