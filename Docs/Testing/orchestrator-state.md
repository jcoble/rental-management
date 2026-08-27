# Orchestrator State

## Run
- ID: 2026-08-27-001
- Status: running
- Started: 2026-08-27T04:45:00
- Dispatch budget: 6
- Dispatches used: 7 (web 16,18 opus; 19,20,22,24 luna; mobile m01 opus) — owner: run all in parallel, Luna Max for the runs
- Single-environment refresh needed: false

## Single environment (Rental Command, no slots)
- Environment URL: https://localhost:5667 (web HMR from main checkout; API https://localhost:5666, sandbox-seeded DB rentalcommand on rc-ux-db :5435)
- Source SHA: 1ff44ff9
- Current role: tester (s16 + s18 concurrent)

## Scenario Tracker
- 16-tenants-contacts-vendors.md: completed (2026-08-27) — 4 bugs → TSK-1038..1041
- 18-accounting-ledger-charges-deposits.md: completed (2026-08-27) — 6 bugs → TSK-1049..1054
- 19-reports-tax-owner-statements.md: running (luna e2e-s19)
- 20-maintenance-workorders-inspections.md: running (luna e2e-s20)
- 22-messages-notifications-notices.md: running (luna e2e-s22)
- 24-settings-profile-team-permissions.md: running (luna e2e-s24)
- mobile/m01-today-navigation-rentals.md: completed (2026-08-27) — 8 bugs → TSK-1042..1048
- mobile/m02-money.md: running (phone, luna)
- mobile/m03-work-maintenance-inspections.md: pending
- mobile/m04-inbox-messages-notifications-settings.md: pending
- mobile/m05-scan-add-ask-leasing.md: pending

- 10-auth-session-recovery.md: queued re-run on current build (owner: June/July results are stale)
- 11-guided-setup-onboarding.md: queued re-run on current build (owner: June/July results are stale)
- 12-dashboard-navigation-deeplinks.md: queued re-run on current build (owner: June/July results are stale)
- 13-properties-units-command-center.md: queued re-run on current build (owner: June/July results are stale)
- 14-leasing-listings-applications-screening.md: queued re-run on current build (owner: June/July results are stale)
- 15-lease-agreements-signing-renewals.md: queued re-run on current build (owner: June/July results are stale)
- 01-money-leases.md: queued re-run on current build (owner: June/July results are stale)
- 02-deposits-owners-tax.md: queued re-run on current build (owner: June/July results are stale)
- 03-operations-core.md: queued re-run on current build (owner: June/July results are stale)
- 04-maintenance-appointments.md: queued re-run on current build (owner: June/July results are stale)
- 05-scan-draft-confirm.md: queued re-run on current build (owner: June/July results are stale)
- 06-portal-public.md: queued re-run on current build (owner: June/July results are stale)
- 07-foundation-auth-onboarding-roles.md: queued re-run on current build (owner: June/July results are stale)
- 08-foundation-rentals-scan-leasing.md: queued re-run on current build (owner: June/July results are stale)
- 09-foundation-operations-communications.md: queued re-run on current build (owner: June/July results are stale)

## Bug Pipeline

### Found (awaiting triage)

### Auto-Resolving
- TSK-1038 [High] blank optional contact field ignored (tenant/vendor/owner) — status: implementing (SOL lane, worktree tsk-1038-clear-optional-fields)
- TSK-1039 [Med] lease-less tenant empty History — status: implementing (Luna lane)
- TSK-1040 [Med] owner form bounds + field errors — status: implementing (SOL lane)
- TSK-1042 [High] Android back exits app — implementing (SOL)
- TSK-1043 [High] RentStillOwed nets credits — queued (server; waits for a dotnet slot)
- TSK-1045 [Med] LeaseAgreement case + InProgress label — implementing (Luna)
- TSK-1046 [Med] Legal/notice 'Active' — implementing (SOL)
- TSK-1049 [Med] ledger link tab=rent — implementing (Luna)
- TSK-1051 [High] ledger sign inversion — implementing (SOL)
- TSK-1054 [Med] fix-charge dialog — implementing (SOL)

### Needs User
- TSK-1048 [Low] Today AI summary flicker/timeout — cache vs stream vs leave: owner decision
- TSK-1050 [High] expense with no property vanishes from books — require property vs include NULL-property rows (recommend include)
- TSK-1052 [Med] 'Needs review' on every month (no JournalEntries) — backfill vs suppress vs seeder
- TSK-1053 [High] 'Kept this month' counts allocations not cash — confirm definition before changing headline number

### Fixed & Awaiting Verification
- TSK-1044 Today 'N more today' row — merged PR #667 (mobile; verify on device)
- TSK-1047 search-as-you-type — merged PR #668 (mobile; verify on device)
- TSK-1041 vendor website row — merged PR #666 (controller looked at screenshot)

### Verified

### Failed Verification

## Verification Batches

## Report Queue
- Scenarios run: 0 exploration, 0 verification
- Bugs found: 18 (web 10, mobile 8)
