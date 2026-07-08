# E2E Initiative — Investigations & Mapping

Durable home for all the **mapping and investigation work** behind the true-to-life E2E
initiative. Each investigation (codebase mapping, adversarial reviews, subsystem root-causes,
feature-gap designs, scoping reports) gets a dated Markdown file here so the knowledge survives
context loss and is reusable later.

**Convention:** new investigation → `YYYY-MM-DD-<topic>.md`. Agent reports get saved here verbatim
(lightly formatted) when they land.

## Index

| Doc | Topic | Source | Status |
|---|---|---|---|
| `2026-07-01-datetime-codebase-map.md` | Every wall-clock site + workers + time-related grounding (clock subsystem) | orchestrator greps | ✅ |
| `2026-07-01-clock-spec-review.md` | Adversarial review of the Master Simulation Clock spec | SpecReviewer | ✅ |
| `2026-07-01-clock-plan-review.md` | Approve-with-changes; 6 must-fixes (UTC-Kind seed, $env build, Date-shim vs JWT expiry, run-due lease-expiry, test-compile, keep-real sites) | PlanReviewer | ✅ |
| `2026-07-01-domain-gaps-design.md` | 4 foundation refactors (F1–F4) + per-gap designs + sequence F1-4→5→1→2→3→4→6→7; defaults accepted | DomainGapsArchitect | ✅ |
| `2026-07-01-grid-filtering-scope.md` | Premise inverted — RC grids already server-side + clean; narrow gap = wire existing date-range filter to more grids + fix 1 unbounded lease-payments sub-grid | GridFilterScout | ✅ |
| `2026-07-01-signalr-rootcause.md` | SignalR barely working = Engine broadcasts into a no-op (no backplane); fix = Postgres LISTEN/NOTIFY + API-hosted broadcaster (Option A) | SignalRDoctor | ✅ |
| `2026-07-01-data-access-audit.md` | Reads DB-side-clean; defects in Engine workers (C1 outbox-dedup unbounded load, H2/H3 N+1) + 10 missing indexes (esp. Payment/Expense date) | DataAccessAuditor | ✅ |

See also: `../ORCHESTRATION-LEDGER.md` (overall state), `../../superpowers/specs/` and
`../../superpowers/plans/` (clock + corpus specs/plans).
