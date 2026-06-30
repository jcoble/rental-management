# TSK-564 Recurring Rent Generation Verification

Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-564-recurring-rent-generation`

## Scope

Verify that creating/importing an active lease no longer silently backfills rent history unless the user explicitly chooses it, and that the lease ledger exposes the past-due settlement action.

## Setup

1. Open the local web app.
2. Sign in with the dev admin account.
3. Use seeded/example portfolio data.

## Checks

### Lease Creation Rent Tracking

1. Open the Leases page or a Unit Command Center lease tab.
2. Start adding a lease and move to the Status step.
3. Set Status to Active.
4. Assert Rent tracking start defaults to `Start from today`.
5. Change Rent tracking start to `Use opening balance`.
6. Assert Opening balance, As of date, and Opening note fields are visible.
7. Enter an opening balance amount and leave As of date blank.
8. Attempt to continue/save and assert the form shows an As of date validation error.
9. Fill As of date and assert validation allows continuing/saving.

### New-Rental Scan Review

1. Open `/scan/new-rental`.
2. Reach the Lease step with a draft or seeded scan.
3. Set Rent tracking start to `Use opening balance`.
4. Assert the opening balance fields appear and are included in the final confirmation path.

### Lease Ledger Settlement

1. Open a lease with past-due unpaid rent charges.
2. Open the Ledger tab.
3. Assert the Account History card shows `Settle past due`.
4. Click `Settle past due`.
5. Assert the dialog asks for Paid date, Method, Reference, and Note.
6. Submit with a paid date.
7. Assert the ledger and payments refresh, and the settled charges no longer show as past-due unpaid.

## Evidence

To be filled by the merged-main browser run.
