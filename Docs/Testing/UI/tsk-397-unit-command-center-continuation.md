# TSK-397 Unit Command Center Continuation UI Guide

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-43`
Local target: `https://localhost:6042`

## Scope

Exercise the Unit Command Center as a live landlord user after the Documents, Expenses, and Timeline fixes.

## Setup

- User: `tsk397.pass39.202606231342@example.local`
- Unit: `/units/6`
- Use synthetic camera/image fixtures from `output/qa/production-scale-scans`.

## Actions And Assertions

1. Open `/units/6?tab=documents`.
   - Assert the header action says `Scan receipt`.
   - Assert the tab action says `Scan a record`.
   - Assert the `Unit files` upload panel is visible.
   - Assert existing lease, unit, and work-order files render in the rollup.
2. Upload a camera-style JPG through `Unit files`.
   - Assert the unit header document count refreshes.
   - Assert the uploaded filename appears in the direct Unit files list and the Unit rollup group.
   - Open the file link and assert the stored-file proxy returns a renderable image.
   - Open delete confirmation and cancel unless the test data can be discarded.
3. Open `/units/6?tab=expenses`.
   - Submit an empty Add expense form and assert required-field errors are visible.
   - Save a synthetic expense with amount, date, category, and status.
   - Inline edit the amount and assert the row, detail area, and Recent activity update.
   - Click `Scan receipt` and assert the scan URL includes `type=Expense`, `propertyId`, `unitId`, and return URL back to `tab=expenses`.
4. Open `/units/6?tab=timeline`.
   - Assert newest-first rows include the just-updated expense.
   - Expand the updated expense row and assert amount diffs are formatted as USD currency.
   - Follow the recorded-expense link to the expense detail page.
   - Expand the detail History row and assert the same currency formatting appears there.

## Evidence To Record

- Browser URL, route ids, uploaded file id, and screenshot/snapshot path for any newly captured proof.
- Any user-facing label that still implies a generic upload while routing to a specific scan type.
- Any audit diff that leaks raw enum, raw decimal money, or implementation-only values.
