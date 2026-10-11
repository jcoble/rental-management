# TSK-617 Bulk Financial Import UI Guide

## Target

Verify that `/import` is reachable and supports the new financial entity types: payments, expenses, and loans. The browser pass must prove preview, commit, duplicate re-upload, and downstream visibility.

## Verified 2026-07-08

- Stack: `https://localhost:5777` web, `https://localhost:5776` API.
- Run ID: `tsk617-1783550170340`.
- Proof covered payment preview/commit, payment duplicate re-upload, expense preview/commit, loan preview/commit, accounting payment visibility, accounting expense visibility, and property-detail loan visibility.
- Screenshots are ignored build artifacts under `web/output/playwright/`:
  - `tsk-617-payment-preview.png`
  - `tsk-617-payment-result.png`
  - `tsk-617-payment-duplicate-preview.png`
  - `tsk-617-expense-preview.png`
  - `tsk-617-expense-result.png`
  - `tsk-617-loan-preview.png`
  - `tsk-617-loan-result.png`
  - `tsk-617-accounting-payment-visible.png`
  - `tsk-617-accounting-expense-visible.png`
  - `tsk-617-property-loan-visible.png`

## Setup

- Run an isolated Rental Command stack.
- Log in as the seeded dev admin.
- Ensure one property, one unit, one tenant, and one active lease exist for payment import.

## Payment Import

1. Open `/import`.
2. Select `Payments`.
3. Upload a CSV with `propertyName,unitNumber,paymentType,amount,paidDate,method,externalReference,notes`.
4. Confirm the preview shows the row ready to import.
5. Commit the import.
6. Confirm the result shows one created payment.
7. Re-upload the same CSV.
8. Confirm the preview/result shows the row as a duplicate and disables import when nothing new remains.
9. Open `/accounting` and confirm the imported payment appears.

## Expense Import

1. Return to `/import`.
2. Select `Expenses`.
3. Upload a CSV with `propertyName,category,description,amount,incurredAt,paidAt,notes`.
4. Confirm preview, commit, and duplicate re-upload behavior.
5. Open `/accounting` and confirm the imported expense appears.

## Loan Import

1. Return to `/import`.
2. Select `Loans`.
3. Upload a CSV with `propertyName,lender,originalAmount,currentBalance,annualInterestRatePct,termMonths,startDate,dayOfMonthDue,monthlyPrincipalInterest,monthlyEscrow`.
4. Confirm preview, commit, and duplicate re-upload behavior.
5. Open the property detail and confirm the loan appears in the mortgage/loans section.

## Failure Signals

- Import cards are not reachable.
- Template/download columns do not match the selected entity type.
- Valid duplicate rows are counted as importable.
- Commit creates duplicates on re-upload.
- Created records do not appear in their downstream ledger/property surface.
