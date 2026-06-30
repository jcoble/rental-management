# TSK-605 Expense Autofill And Stepper Forms

Date: 2026-06-30

Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-605-expense-autofill-stepper-forms`

Branch: `cdx/tsk-605-expense-autofill-stepper-forms`

## Scope

- New Expense should pull safe defaults from a selected work order: property, unit, vendor, description, amount, and incurred date only while those fields are blank.
- New Expense should pull vendor receipt contact details from a selected vendor: address, phone, and tax ID only while those fields are blank.
- The web lease and work-order create flows should remain split into stepper-sized steps with per-step validation.
- The web lease tenant picker should continue using the server-side `availableForLease=true` filter for create, while edit mode can preserve the existing tenant.

## Browser Assertions

1. Sign in as the seeded dev admin and open `/accounting`.
2. Click `New Expense`; assert the expense stepper starts on `Source`.
3. Select a real vendor; advance to `Vendor`; assert vendor address, phone, and tax ID fields are populated when that vendor has those fields.
4. Reopen `New Expense`, manually type a vendor address, select a vendor, advance to `Vendor`; assert the manually typed address is not overwritten.
5. Reopen `New Expense`, select a real work order; assert the source step shows a linked property/unit/vendor when the work order has those references.
6. Advance to `Details` and `Dates`; assert description, amount, and incurred date are filled from the work order when available.
7. Start a blank expense and click `Next` from `Details` without description/amount; assert the validation message stays on the current step and no expense is posted.
8. Open `/leases`, start `New Lease`, and verify the flow exposes small steps (`Location`, `Tenants`, `Lease #`, `Dates`, `Money`, `Status`) instead of one dense form.
9. Open `/maintenance`, start `New Work Order`, and verify the flow exposes small steps (`Issue`, `Triage`, `Location`, `Schedule`, `People`) before final save.

## Mobile Assertions

1. Open the mobile Money tab and tap Add expense.
2. Select a vendor; assert the Notes tab shows vendor address, phone, and tax ID prefilled when available.
3. Type a vendor address manually before selecting another vendor; assert the manual value is retained.
4. Select a work order; assert description, amount, vendor, and incurred date are filled only when the user has not already provided them.
5. Save an expense with vendor receipt details and assert the API payload includes `receiptData.vendor`.
