# TSK-489 Secondary Deep-Link Verification

Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-489-secondary-deeplinks`

Stack:
- Web: `https://localhost:5687`
- API: `https://localhost:5686`
- DB: `rentalcommand_tsk489`

## Scope

Verify secondary links now land in the unit Command Center tab when the source record is tied to a unit, while records without unit context still use their generic fallback route.

## Setup

1. Open `https://localhost:5687`.
2. Sign in with the dev admin account.
3. Use seeded/example portfolio data.

## Checks

### Dashboard Activity

1. Open the dashboard.
2. Find daily briefing or recent activity items for unit-tied records such as payments, leases, expenses, work orders, or rental applications.
3. Click one unit-tied item.
4. Assert the browser lands on `/units/{unitId}` with the relevant tab/query parameter:
   - Payment: `?tab=rent&payment={paymentId}`
   - Expense: `?tab=expenses&expense={expenseId}`
   - Lease: `?tab=lease&lease={leaseId}`
   - Work order: `?tab=maintenance&wo={workOrderId}`
   - Application: `?tab=applications&app={applicationId}`

### Unit Activity Feed

1. Open a unit detail page.
2. Switch to the Timeline tab.
3. Click a unit-tied activity entry.
4. Assert the link stays in the unit Command Center tab for that record.

### Scan Confirmation

1. Open a scan draft or use a confirmed scan response with a unit-tied created record.
2. Confirm the draft or inspect the created-record link after confirmation.
3. Assert unit-tied created records link through the unit Command Center tab.
4. Assert untied records still use the generic route.

## Evidence

Playwright CLI verification against `https://localhost:5687` on 2026-06-30:

- Dashboard latest work order card linked to `/units/12?tab=maintenance&wo=15`; clicking it opened Unit 3 with the Maintenance tab selected and the work order detail visible.
- Unit recent activity showed unit-scoped links for work orders (`/units/12?tab=maintenance&wo=2`, `/units/12?tab=maintenance&wo=15`), payments (`/units/12?tab=rent&payment=147` and neighboring payments), and lease (`/units/12?tab=lease&lease=11`). Inspection remained on the generic `/maintenance/inspections/3` fallback because it is not yet routed through the unit command center.
- Clicking unit activity payment `/units/12?tab=rent&payment=147` opened Unit 3 with the Rent tab selected and the payment detail visible.
- From Unit 3, `Scan receipt` created scan draft `/scan/1?type=Expense&propertyId=6&unitId=12&returnTo=%2Funits%2F12%3Ftab%3Drent`; the worker moved it from Processing to Reviewing.
- Confirming that scan recorded payment 265 and rendered `View/Edit Record` as `/units/12?tab=rent&payment=265`.
- Clicking `View/Edit Record` opened `/units/12?tab=rent&payment=265`; Unit 3 showed the Rent tab selected, the $1,150.00 paid payment detail, the scanned document preview, and unit recent activity remained unit-scoped.
- Verifier follow-up fixes added unit-aware immediate navigation for Lease/Application scan confirmations and unit-aware links from the scan history list. Regression coverage: `ScanControllerTests.ListPage_IncludesCreatedUnitId_ForUnitTiedConfirmedRecords` plus `createdRecordHref('RentalApplication', ..., unitId)`.
- After restarting the patched feature stack, `/scan?status=Confirmed` showed the confirmed payment row's `View record` link as `/units/12?tab=rent&payment=265`.
