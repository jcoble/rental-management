# TSK-754 full-year execution run

## Run identity

- Run ID: `2026-07-26-001`
- Notion task: `TSK-754`
- Environment: `https://rental-command.chimp-map.ts.net`
- Initial source SHA: `ccac9ee24331bf42695772d745fb3a5a6992d10c`
- Current verified source SHA: `b688c1729ea38895c200bdc2b75aaadb731afb6b`
- Planner: `Docs/Testing/YearSimulation2027/index.html`
- Schedule: `Docs/Testing/YearSimulation2027/schedule.csv`
- Corpus: `/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus`
- Financial oracle: `Docs/Testing/YearSimulation2027/rental-command-2027-simulation-oracle.xlsx`

## Safety boundary

- Use a new dedicated synthetic portfolio; preserve all unrelated preview data.
- Do not invoke preview `reset-data`.
- Do not use production.
- Notification delivery remains suppressed in preview; verify outbox/in-app state and delivery suppression evidence.
- Stop on unexplained financial variance, partial mutation, cross-portfolio access, or confidentiality failure.

## Queue

1. Restore the reusable `rental` verification runtime at the recorded source SHA.
2. Authenticate and create the dedicated simulation portfolio.
3. Execute the schedule in `run_id` order with the simulated clock.
4. Capture month-end financial comparisons and role/client evidence.
5. Capture, fix, and independently verify confirmed defects one at a time.
6. Complete year-end reconciliation and close the run.

## Defects

### TSK-754-D001 — Preview simulation clock disabled

- Status: Fixed; web and API verified, Engine date observation still required
- Severity: Blocking
- Evidence:
  - The real preview login screen did not render the `SIM CLOCK` panel.
  - `GET /api/v1/dev/clock` returned HTTP 404 on source SHA
    `ccac9ee24331bf42695772d745fb3a5a6992d10c`.
  - `deploy/docker-compose.preview.yml` did not enable the web, API, or Engine
    simulation-clock gates.
- Fix:
  - Enable `PUBLIC_SIMULATION_ENABLED` for web.
  - Enable `Simulation__Enabled` for API and Engine.
- Verification required:
  - The panel renders in a new browser session.
  - The authenticated clock route returns a clock state.
  - Setting a date updates the web clock and the Engine observes the same date.

### TSK-754-D002 — Guided Setup detection can remain pending forever

- Status: Fixed and independently verified in the real preview
- Severity: Blocking
- Reproduction:
  - Register and verify a new user.
  - Choose `Set up my real portfolio`.
  - Observe `Checking your existing setup…` indefinitely.
- Evidence:
  - All five detection endpoints returned HTTP 200 with valid JSON.
  - TanStack results for tenants and leases remained `pending/fetching`.
  - Calling each result's existing `refetch()` resolved immediately and advanced the UI.
- Root cause:
  - `detectionReady` joined the five `isSuccess` flags with a short-circuiting `&&` chain.
  - TanStack tracks only result properties that consumers read. Later queries could finish before
    their `isSuccess` property was observed, so the component never received their completed state.
- Fix:
  - Read every success and error flag into an array before reducing with `every` or `some`.
  - Add a regression assertion that rejects the short-circuiting readiness pattern.
- Verification required:
  - Passed: a fresh browser load advanced to the lease-first setup screen without a manual refetch.
  - Passed: focused onboarding tests and both web type-check lanes.
  - Evidence: `browser/d002-after-guided-setup-lease-first.png`.

### TSK-754-D003 — Scan extraction requires a workspace AI credential

- Status: Configuration prerequisite resolved for the pilot portfolio
- Severity: Blocking until configured
- Reproduction:
  - Upload `SCN-0001` through Guided Setup before configuring a workspace AI connection.
  - Draft 2 transitions from `Pending` to `Failed`.
- Evidence:
  - Upload returned HTTP 201 and created only the scan draft.
  - Failure reason was `AI extraction unavailable: configure a workspace OpenAI or Anthropic credential in Settings`.
  - Property, Unit, Tenant, LeaseManagement, LeaseAgreement, and TenantAccount counts for portfolio 3
    remained zero.
- Resolution:
  - Exercised Settings > AI connection through the real form.
  - Verified and saved the preview OpenAI integration as `gpt-4o`.
  - Re-uploaded the same PDF; extraction completed and opened the five-step review.
- Security note:
  - The credential remains encrypted in the application database and the saved form clears it.

### TSK-754-D004 — Shared preview clock changes every portfolio's worker time

- Status: Shared-preview execution stopped before bulk worker execution; isolated runtime/database required
- Severity: Blocking safety boundary
- Evidence:
  - The simulation clock is a single global `SimulationClocks` row with no portfolio scope.
  - Both API and Engine register a clock-state refresher against that shared row.
  - Scheduled worker commands operate in the Engine's system/admin context across due portfolio data.
  - Therefore a year-long worker replay cannot be restricted to portfolio 3 merely by selecting that
    portfolio in the browser.
  - The shared clock was reset to `Real` immediately after discovery.
- Resolution required:
  - Stop only the shared `rental` application runtime while preserving its data.
  - Run the year under a distinct preview stack ID with a fresh persistent database and its own
    uploads/data-protection volumes.
  - Do not resume the schedule against the shared `rental` database.

## Pilot scan proof

- `SCN-0001` was uploaded twice: the first attempt intentionally established the missing-credential
  failure boundary; the second extracted successfully after configuration.
- Before confirmation, portfolio 3 contained 0 Properties, 0 Units, 0 Tenants, 0 LeaseManagements,
  0 LeaseAgreements, and 0 TenantAccounts.
- The review populated Arbor House, 117 Arbor Street, Columbus, OH 43201; Unit Main; Dana Garcia;
  the 2026-02-01 through 2027-12-31 term; $1,575 monthly rent; $1,575 deposit; and rent due day 1.
- Manual completion also exercised bedrooms, bathrooms, tenant email, tenant phone, emergency
  contact, late fee, and lease notes.
- After `Confirm & create`, each of the six aggregate counts was exactly 1.
- This is pilot evidence only. The official schedule counter remains zero until the isolated run is
  initialized and `SCN-0001` is repeated there.

## Checkpoint 2026-07-26

- Status: shared-preview pilot complete; isolated run initialization required
- Completed run rows: 0 / 1,996
- Uploaded scan assets: 0 / 953
- Pilot scan confirmations: 1
- Findings and safety blockers: 4
- Current blocker: the shared master clock affects all preview portfolios.
- Next action: preserve and stop the shared `rental` runtime, start an isolated `yearsim754` runtime/database,
  configure its synthetic admin and AI connection, and repeat the first opening lease as official run evidence.
