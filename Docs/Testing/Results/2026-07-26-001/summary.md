# TSK-754 full-year execution run

## Run identity

- Run ID: `2026-07-26-001`
- Notion task: `TSK-754`
- Environment: `https://rental-command.chimp-map.ts.net`
- Initial source SHA: `ccac9ee24331bf42695772d745fb3a5a6992d10c`
- Current verified source SHA: `5c428f3bd163b29fcbf7940c098e3609453e4d67`
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
  - The preview OpenAI key appeared once in diagnostic tool output during the shared-stack pilot.
    It must be rotated before this run is treated as security-complete; the key value is not
    reproduced in this evidence.

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

### TSK-754-D005 — Tall camera JPEG can produce unrelated high-confidence lease data

- Status: Fixed and verified with the original camera JPEG in the isolated runtime
- Severity: Blocking scan-safety defect
- Reproduction:
  - Upload `SCN-0002`, a 1,360 by 13,210 camera JPEG containing nine vertically composited
    photographed lease pages.
  - Extraction opens the lease review but suggests the existing Arbor House and returns unrelated
    tenant, dates, rent, deposit, and fee values at 100% self-reported confidence.
- Safety evidence:
  - The source image was inspected and contains the planned `SCN-0002` Briar Cottage / Gray Lewis
    values; its SHA differs from `SCN-0001`.
  - The unconfirmed draft has a distinct stored-file hash and remains in Reviewing.
  - No second Property, Unit, Tenant, LeaseManagement, LeaseAgreement, or TenantAccount was saved.
- Root cause:
  - Non-PDF images were sent as one `image_url` using the configured `low` detail default.
  - A low-detail request reduces the entire 9.7:1 image to one small vision viewport, making each
    photographed page unreadable.
  - Local OCR is disabled in preview, so the model received no readable text fallback and guessed
    from the portfolio grounding context.
- Fix:
  - Detect images taller than three image widths.
  - Split them into overlapping page-sized JPEG tiles and send every tile at high detail.
  - Preserve the existing single-image behavior and configured detail for ordinary images.
  - Add a regression test proving a tall image produces multiple high-detail image parts.
- Verification:
  - Passed: all seven `OpenAiLlmProviderTests`.
  - Passed: deployed exact source SHA `5c428f3bd163b29fcbf7940c098e3609453e4d67`
    to `yearsim754`.
  - Passed: re-uploading the original `SCN-0002` JPEG extracted Briar Cottage, 134 Briar Street,
    Columbus, OH 43202; Gray Lewis; lease `SCN-0002`; and the 2026-03-01 through 2027-12-31 term.
  - The scan correctly left fields it could not read blank instead of fabricating values. Monthly
    rent, deposit, unit details, tenant contacts, late fee, due day, and notes were completed
    manually in the review.
  - Passed: the bad draft remains unconfirmed; the corrected draft was confirmed only after its
    visible values were checked against the planner.
  - Evidence: `browser/d005-scn-0002-fixed-confirmed.png`.

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

## Isolated official run proof

- The shared `rental` application runtime was stopped without deleting or resetting its PostgreSQL
  data, uploads, credentials, data-protection keys, or volumes.
- The official run uses stack ID `yearsim754`, with a distinct PostgreSQL bind mount and distinct
  uploads and data-protection volumes.
- A fresh synthetic administrator selected the real-portfolio path, creating portfolio 2 in the
  isolated database.
- Settings > AI connection was exercised through the real web form. The `gpt-4o` connection test
  passed, Save connection became available, the connection saved as Connected, and the API-key
  field cleared to zero characters.
- The authenticated simulation clock moved to `2027-01-02`; the existing session was invalidated
  as expected and the administrator signed in again at the simulated date.
- Before official confirmation, portfolio 2 contained 0 Properties, 0 Units, 0 Tenants,
  0 LeaseManagements, 0 LeaseAgreements, and 0 TenantAccounts.
- `SCN-0001` was uploaded from its rendered PDF and extracted into the five-step review. Extracted
  values were read back and every available field was completed, including bedrooms, bathrooms,
  tenant contact data, emergency contact, late fee, and lease notes.
- The executed-agreement choice was set to `Yes, everyone has signed`.
- After `Confirm & create`, portfolio 2 contained exactly 1 of each of the six aggregate records.
  No aggregate existed before confirmation, proving the official first scan did not partially
  save the rental graph.
- Evidence: `browser/isolated-official-scn-0001-confirmed.png`.
- `SCN-0002` exercised the rendered camera-JPEG path after D005 was fixed. Before confirmation,
  all six aggregate counts remained exactly 1. The review created Briar Cottage, Unit Main, Gray
  Lewis, and the planned active lease; after confirmation, all six counts advanced atomically to
  exactly 2.
- The failed D005 reproduction remains as draft 2 in Reviewing while corrected draft 3 is
  Confirmed. It did not create any business aggregate.
- Evidence: `browser/d005-scn-0002-fixed-confirmed.png`.

## Checkpoint 2026-07-26

- Status: isolated run initialized; first two official scans confirmed
- Completed run rows: 0 / 1,996
- Uploaded and confirmed scan assets: 2 / 953
- Pilot scan confirmations: 1
- Official scan confirmations: 2
- Findings and safety blockers: 5
- Current blocker: none; D005 is fixed and verified with the original source image.
- Next action: complete the remaining 10 opening-lease confirmations in run row
  `RUN-20270102-01`, then advance the row counter and reconcile the created rental graph.
