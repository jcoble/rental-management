# TSK-754 full-year execution run

## Run identity

- Run ID: `2026-07-26-001`
- Notion task: `TSK-754`
- Environment: `https://redacted-host.example.invalid`
- Initial source SHA: `ccac9ee24331bf42695772d745fb3a5a6992d10c`
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

- Status: Fix pending rebuild and runtime verification
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

## Checkpoint 2026-07-26

- Status: environment initialization
- Completed run rows: 0 / 1,996
- Uploaded scan assets: 0 / 953
- Confirmed defects: 1
- Current blocker: the restored runtime has the simulation clock disabled at all three deployment gates.
- Next action: rebuild the canonical `rental` stack with the preview-clock fix, preserving persistent data, then verify the web, API, and Engine clock behavior.
