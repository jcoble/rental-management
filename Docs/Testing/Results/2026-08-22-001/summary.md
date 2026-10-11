# E2E Run 2026-08-22-001 — Summary

**Purpose:** desktop web regression pass after Phase 7 legacy write-kernel removal (all 138 write operations migrated to the shared write executor, PRs #577–#593; base main 490a3c94).

**Verdict:** the Phase 7 migration held up. One true Phase 7 regression was found and it was Critical — the Engine host never registered `IRequestWriteExecutor`, so the Engine was dead on startup (no email, e-sign notifications, or notices). Everything else found was pre-existing or cosmetic. All 8 fixes are merged and verified on the running stack. Final main: 436ab50d.

## Coverage
Six exploration dispatches (full budget): auth/session recovery (10), guided onboarding (11), dashboard/navigation/deep-links (12), properties/units (13), leasing/listings/applications/screening (14), lease agreements/e-sign/renewals (15). Fresh QA database `rentalcommand_qa20260822`; headless Chrome at 1710x990 throughout; each tester did a 390x844 mobile spot-check.

## Bugs: 10 reported → 8 fixed+verified, 2 refuted, 1 needs-user, 2 follow-ups

| Task | Sev | Bug | Fix PR | Verified |
|---|---|---|---|---|
| TSK-965 | Critical | Engine dead: `IRequestWriteExecutor` not registered in Engine DI (Phase 7 regression) | #594 | PASS — outbox delivering |
| TSK-973 | High | Application detail stale after approve/decline (query-key scope mismatch) | #596 | PASS |
| TSK-968 | Med | Unit endpoints returned 403 instead of 404 for nonexistent unit | #597 | PASS |
| TSK-974 | Med | Lease draft accepted rent due day 0 | #599 | PASS |
| TSK-967 | Low | /register rendered for authenticated users | #595 | PASS |
| TSK-969 | Low | Filter Selects never wrote/cleared URL params | #597, re-fix #600 | PASS (see below) |
| TSK-975 | Low | Draft dialog stayed open after successful issuance | #599 | PASS |
| TSK-976 | Low | Console 404 on unit Money tab without a deposit | #598 | PASS |

- **TSK-969 failed batch-1 verification** in its first form: the shared `syncGridUrl` helper compared against `page.url`, which SvelteKit's shallow `replaceState` never refreshes, so clearing a filter after setting one computed "no change" against a stale baseline. Re-fixed to compare against `window.location` (PR #600, affects all 16 grid pages), re-verified live.
- **Refuted (scenario 11):** "writes never commit" and "wrong user IDs" — the tester queried the wrong database; direct queries against `rentalcommand_qa20260822` showed every write persisted. Disposition appended to that report.
- **Needs user:** TSK-966 [Med] — /register email enumeration; product decision between generic-success+email, explicit error, or rate-limited error.
- **Follow-ups:** TSK-979 [Low] onboarding wizard footer clipped at 390px (owned by the upcoming on-device mobile pass); TSK-986 [Low] console 404 `/agreements/null/draft` during dialog unmount.

## What was positively exercised
Login/logout/session recovery, register guard, onboarding wizard end-to-end (writes persist), properties/units CRUD, server-side grid filters/sort/paging, applications approve/decline with live refresh, e-sign issuance→signing→countersign lifecycle with idempotent issuance, deposit/ledger panels, dashboard deep-links, Engine background workers (outbox email, rent charges).

## Environment notes for future runs
Old `rentalcommand` DB on :5434 predates the 2026-07-15 migration squash — always use/create a post-squash DB. `rentalcommand_api` role needed `NOINHERIT`; cluster needed one-time `REINDEX SYSTEM` after unclean shutdown. Tester DB claims must be verified against the exact env DB before acting (scenario 11 lesson).

## Pending
- On-device mobile pass when the owner's phone is available (TSK-979 folds in).
- TSK-966 product decision.
- Pre-existing Data.Tests 8 failures on main — separate lane, unstarted.
