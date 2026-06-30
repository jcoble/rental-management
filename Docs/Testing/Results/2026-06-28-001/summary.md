# E2E Orchestrator Run Summary — 2026-06-28-001

- Mode: no-containers. Single running dev app; up to 4 exploratory testers + fixers in parallel,
  each in isolated playwright-cli sessions / git worktrees.
- App: Web https://localhost:5667 · API https://localhost:5666 · shared dev Postgres `rentalcommand`.
- Method: each tester read its scenario → got acquainted with the code (route → endpoint → controller
  → service → entity) → explored the live app → wrote a BUG-N report. Orchestrator triaged, fixed
  auto-resolvable bugs in worktrees, merged to main, gated (build/test/svelte-check), restarted, and
  verified live. Anything money-definition / schema-migration / auth / ambiguous → NEEDS-USER.md.

## Headline
- **19 bugs found · 18 fixed + merged + gated + live-verified · 1 bucketed (needs a prod migration).**
- 6 scenarios explored, 2 verification passes (wave-1 8/8, wave-2/3 10/10) — all PASS.
- No regressions: final `dotnet build` 0 errors, focused suites green, `svelte-check` 0 errors.

## Scenarios run
| # | Scenario | Domain | Bugs |
|---|----------|--------|------|
| 01 | Money & leases | Money | 5 |
| 03 | Operations core (property/unit/tenant/vendor) | Operations | 4 |
| 06 | Tenant portal & public apply/e-sign | Portal | 2 |
| 05 | Scan → draft → confirm | Scan | 2 |
| 02 | Deposits · owners report · tax | Money | 3 |
| 04 | Maintenance · work orders · appointments | Operations | 3 |

## Bugs fixed (18) — all merged to main + live-verified
**High (5)**
- BUG-1 Unit "Outstanding" overstated a partial payment (told landlord to collect full rent) → remainder only.
- BUG-2 `/leases/{id}/signature-queue` HTTP 500 on every un-sent lease (Postgres untyped NULL) → 200.
- OP-1 Property delete orphaned its units / could orphan an active lease, no guard → blocked w/ clear message.
- DEP-1 Security-deposit return status INVERTED (partial→"Returned", written to the legal audit log) → corrected.
- WO-1 Future-scheduled work order couldn't be Completed (auto-stamp tripped a guard) → completes + stamps.

**Medium (8)**
- BUG-3 Unit Rent quick-post offered an unsubmittable "Partial" (silent no-op) → removed from quick form.
- BUG-4 Lease ledger hid a partial's collected amount → companion +AmountPaid line.
- OP-3 Opaque "conflicts with existing data" on duplicate unit number → clear named message.
- OP-4 Tenant delete not blocked on a NoticeGiven (still-occupying) lease → now blocked.
- PORTAL-1 Tenant balance excluded Failed payments (self-contradictory "$0.00 / 1 overdue") → counts them, one source of truth.
- SCAN-2 Image originals unreachable in-app (thumbnail only) → "Open in new tab" serves the original.
- DEP-2 Tax 1099 checklist summed all years, ignored the year selector → year-scoped.
- WO-2 "Vendor has the job" banner showed on mere assignment (no dispatch) → driven by real dispatch state.

**Low (5)**
- BUG-5 "Total Collected" tooltip wrongly claimed it included deposits → copy fixed.
- PORTAL-2 Staff could open the tenant portal (6×403 broken dashboard) → redirected to staff home.
- SCAN-1 `?full=1` returned 400 (bool binder) → accepts 1/0 and true/false.
- DEP-3 Owner statement / tax showed whole dollars (rows didn't sum) → cents.
- WO-3 Editing a work order truncated RequestedAt/CompletedAt to UTC-midnight → dirty-checks timing fields.

## Merge commits (all on main)
Wave 1: 95ff4ab, e981e59, 7d96241. Wave 2/3: 1c605a3 (portal), 48ed367 (scan), 9d4973e (deposits),
beb458a (maintenance). All fixer worktrees removed; all fix branches deleted.

## Needs-User (NEEDS-USER.md) — deliberately not auto-changed
- **OP-2** [bug] soft-deleted unit number unreusable — needs a partial unique index + **EF migration**
  (auto-applies in prod). Fix is written and ready to run.
- **Auth resilience (P-1):** a transient `/auth/me` error bounces authenticated users to /login
  (`hooks.server.ts:67-87`) — same class as the cert incident below; deny-vs-preserve is a decision.
- **Accounting-basis questions:** Schedule-E (accrual) vs owner statement (cash) show different expense
  totals; optional "Withheld" deposit state; Money KPIs vs the ledger date filter; mgmt-fee rounding (latent).
- **Portal gaps:** no UI to provision tenant portal access (seeded at startup only); pay-rent IDOR
  guard correct but unexercised while Stripe is off (re-verify when enabled).
- **Minor polish:** scan `?full` doc, stray `target_entity_type` field, Payment DueDate cosmetic, etc.
- **7 pre-existing worktrees** with uncommitted/unmerged work from other sessions — left intact, reported.

## Environment incidents (resolved; transparency)
1. **API cert** — restarting the API to load merged code, I first omitted its mkcert cert → it served a
   self-signed cert the web SSR rejected → all browser login bounced to /login. Fixed by pinning
   `Kestrel__Certificates__Default__Path=web/.cert/api-cert.pem`. Recipe recorded in orchestrator-state.md.
2. **Engine down** — RentalCommand.Engine died in the shared-stack churn; scans stalled in Pending.
   Restarted with the matching Upload__BasePath + suppressed notifications.
3. **Shared/volatile DB** — another concurrent agentic session reseeded the dev DB mid-run (18 portfolios).
   Not caused by this run (API restart didn't reseed; tests use SQLite/Testcontainers). Testers/verifiers
   were made resilient (create + uniquely-mark their own data).
- Notifications were blanked on the API+Engine for the whole run, so no real email/SMS was sent.
