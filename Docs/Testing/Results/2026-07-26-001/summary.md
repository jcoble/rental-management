# TSK-754 full-year execution run

## Run identity

- Run ID: `2026-07-26-001`
- Notion task: `TSK-754`
- Environment: `https://redacted-host.example.invalid`
- Source SHA: `d69c4d5926d4923c60a518ad44fc7e8edba99763`
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

## Checkpoint 2026-07-26

- Status: environment initialization
- Completed run rows: 0 / 1,996
- Uploaded scan assets: 0 / 953
- Confirmed defects: 0
- Current blocker: reusable preview runtime is stopped and the stable URL returns HTTP 502.
- Next action: restore the canonical `rental` stack without resetting its persistent data.
