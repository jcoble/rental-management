# Needs-User Bucket — Run 2026-06-28-001 — ✅ RESOLVED

All items below were resolved per "do all of them, best judgement" (2026-06-29). Code fixes were
implemented in isolated worktrees, gated (build + tests + svelte-check), merged to `main`, and the
stack restarted (OP-2 migration applied; notifications enabled). Decide-and-document items have my
call recorded.

## Bugs / code items — IMPLEMENTED + merged + gated
- **OP-2** soft-deleted unit number reuse — ✅ filtered partial unique index `(PropertyId, UnitNumber) WHERE "DeletedAt" IS NULL` + migration `20260629061046_UnitNumberPartialUniqueIndex`. **Live-verified**: DB index now carries the filter.
- **Unit "outstanding" lease scope** — ✅ scoped to the current lease via `ForCurrentLeaseAttention(now)` (UnitDashboardService), so it reconciles with the Accounting KPI. 10/10 tests.
- **"Withheld" deposit state** — ✅ added `SecurityDepositStatus.Withheld`; full-withhold now reads "Withheld" (badge added), partial → "Partially Returned", none → "Returned".
- **Over-deduction** — ✅ UI warning when cumulative deductions exceed the held amount.
- **Owner-statement mgmt-fee rounding** — ✅ totals now foot to the rounded per-line fees.
- **Schedule-E vs owner-statement basis** — ✅ kept both (correct); added a clarifying note on `/tax` and `/owners-report`.
- **Money KPI vs date filter** — ✅ labeled the KPI strip "All-time" (kept their meaning).
- **Auth resilience (P-1)** — ✅ SSR now preserves the session on a transient `/auth/me` failure (decodes the first-party token's claims, honoring `exp` only); a real 401/expiry still bounces. 8/8 helper tests.
- **Stray `target_entity_type`** — ✅ removed from Application, WorkOrder, **and** Lease/Loan extraction schemas (consistency).
- **Client/server validation parity** — ✅ added max-length / upper bounds to `propertySchema`/`unitSchema` mirroring the server DTO limits. 16/16 tests.
- **Unit status UI** — ✅ occupancy is lease-driven, so fixed the misleading help text (no conflicting manual field). Noted: `UnitStatus` has `Reserved`/`Offline` if you ever want a manual-status feature.
- **Resident access** — superseded by the canonical relationship-scoped household grant/revoke commands. A tenant directory record never creates login authority; an effective `LeaseManagementParty` grant atomically creates the login, access context, `TenantUserAccess`, audit, and invitation outbox.
- **Team-member invite email** (user-reported) — ✅ `AdminUsersController` now sends an invite via `OutboxAuthEmailSender`. **Live-verified**: SendGrid accepted it (HTTP 202), subject "You've been added to Rental Command". Same plaintext-temp-password caveat as portal access.

## Decide-and-document — my call (no code change)
- **Schedule-E expense date = IncurredAt only** — KEPT (accrual is correct for Schedule E; the new on-page note explains the basis).
- **Payment DueDate = transaction date** — KEPT (acceptable for a received-payment scan; payments don't carry a meaningful rent-period due date).
- **Generic 409 copy** — KEPT (OP-3 already gives the unit-number case a specific message; the generic copy is a reasonable catch-all).
- **Pay-rent IDOR** — guard is correct in code (`StripePaymentService`); can only be exercised once Stripe is enabled. Re-verify the 404 path then.

## Cleanup — DONE
- **Sandbox orphans** — soft-deleted the 2 orphaned units (live unit under a soft-deleted property); OP-1's guard prevents recurrence.
- **Pre-existing worktrees** — committed each inactive worktree's WIP to its branch (nothing lost) and removed 7 of them (disk freed); left the **live** `tsk-382` (active session) untouched. Restore any with `git worktree add <path> <branch>`.

## Future enhancements I deliberately did NOT build (flagged, your call)
- A secure **per-recipient set-password link** flow for team members and residents, replacing temporary plaintext passwords in invitation emails.
- Manual unit status (Reserved/Offline) UI, reconciled with lease-driven occupancy.
