# Rental Command Foundation E2E Stabilization Handoff

**Date:** 2026-07-15  
**Task:** TSK-668 / TSK-670 / TSK-672 / TSK-674 foundation rewrite  
**Goal status:** Paused by the user for a session handoff  
**Worktree:** `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-670-role-experience-spec`  
**Branch:** `tsk-668-670-672-674-foundation-rewrite`  
**Current HEAD before this handoff commit:** `a0fd6694e83a5074b6119830365859bd89783a7b`  
**Protected preview:** `https://redacted-host.example.invalid`

## Start here

Do not resume atomic-write inventory work, speculative architecture review, or tiny isolated slices.
The user has explicitly rejected further drift into those areas. The immediate job is broad feature
stabilization of the actual web and mobile product.

Use the `run-e2e-tests` workflow as guidance, but follow the user's requested operating model:

1. The root agent is the orchestrator, not a fourth implementation lane.
2. Run three broad tester/fixer lanes concurrently.
3. Each lane reads the relevant code, authors or updates a scenario, explores the real UI, and fixes
   obvious failures directly in this same foundation worktree.
4. Testers must validate business truth and intended UX, not merely that a route renders.
5. Serialize all heavy builds/tests and prefer the Azure verification runner.
6. Consolidate fixes, deploy an exact committed SHA to the protected preview without resetting its
   preserved database, then repeat the browser/device walkthrough.

The user does not want to encounter obvious bugs while casually walking the product. Do not call the
system ready until the main flows have been exercised end to end.

## User's latest release-blocking findings

The user spent only a few minutes in the protected preview and found the current result unacceptable:

### Unit, lease, and account truth disagree

- `/units/18?tab=summary` labels the Unit occupied, rent current, and names Leah Garcia, while the
  summary says **No current lease for this unit**.
- `/units/18?tab=tenant-lease&view=agreements` shows Leah Garcia as Ending with **No governing
  agreement** even though the Unit presents an occupied/current-rent state.
- `/units/19?tab=money` shows an overdue balance and tenant name but also says **No active tenant
  account** and **No tenant account exists for this rental yet**.
- These are not acceptable empty-state wording issues. The canonical LeaseManagement,
  TenantAccount, Agreement, possession, occupancy, household, and ledger projections must agree.
- The seeded demo data must exercise real canonical relationships. Do not keep contradictory records
  merely to make independent cards render.

### Unit Command Center organization is not the approved result

- The Unit tabs and subviews do not match the intended, preservation-first flow.
- The user approved the clearer behavior where entering a Unit removes the higher-level Rentals peer
  navigation, while retaining useful Unit tabs. They did not approve arbitrary loss or relocation of
  functionality.
- Review the current six Unit sections against the approved blueprint and the actual older UI. Keep
  useful contextual actions, pertinent summary information, scanning, filters, FABs, bottom sheets,
  steppers, and appropriate tabs. Do not turn the product into sparse CRUD pages.

### Money is incomplete and internally contradictory

- The whole Unit Money experience was intended to be redesigned; the current page is not that result.
- Tenant account, operating costs, charges, payments, deposits, balances, lease relationship, and
  correction actions must be understandable without making the user bounce through unrelated lists.
- Posted payments/receipts are immutable, but the receipt detail currently only explains that a
  correction uses a reversal or adjustment. It must provide the easy, authorized **Correct payment**
  or **Reverse / adjust** action directly on that payment, prefilled with the original context.
- The correction must append canonical ledger entries and preserve the original receipt; it must not
  edit or delete the original financial posting.

### Lists/grids are too slow and incomplete

- Opening Units and several other grids is extremely slow.
- The Units grid visibly omits important derived facts: for example, occupied/move-out rows have an
  em dash under Lease ends even when a real relationship should provide a date.
- Earlier measurements found `/api/v1/units/list-with-health/page?take=20` taking about 11–20 seconds.
  Accounting Reports and Security Deposits also exceeded the 20-second browser timeout.
- Do not hide timeouts as zero or empty data. Keep explicit retryable errors while fixing the query.
- Apply the repository hard rule: filtering, authorization, joins, grouping, aggregation, sorting,
  and paging must remain DB-side. A list page should be 1–3 SQL statements, never N+1 or in-memory
  shaping. Inspect generated SQL and PostgreSQL query plans rather than guessing.

### Grid motion was removed

- The user says the previous grid/list entrance animations are gone across the product.
- Find the prior reusable animation behavior in git history/components and restore it consistently.
  Respect reduced-motion preferences and avoid delaying data interaction.

### Existing E2E reports overstated readiness

`Docs/Testing/Results/2026-07-15-001/` contains useful reproduction details, but its prior “pass” labels
must not be treated as release approval. The walkthrough verified that many routes rendered; it did
not sufficiently validate that the displayed relationships, balances, dates, available actions, and
navigation matched the business model or approved UX. Update those reports as fixes are proven.

## Required three lanes

### Lane 1 — Rental lifecycle and Unit Command Center

- Reproduce the Unit 18/19 contradictions from the protected preview.
- Trace the DB rows and the exact API/read-model SQL that produces each card/grid field.
- Correct seed generation and/or canonical projections so LeaseManagement, Agreement, TenantAccount,
  possession, occupancy, household, and ledger facts agree.
- Review every Unit tab/subview against the approved blueprint and restore missing contextual actions.
- Exercise draft, issue, sign, possession, correction, renewal, month-to-month, ending, successor,
  and historical agreement flows with real fixtures.
- Cover web and mobile, fixing obvious failures in place.

### Lane 2 — Money and financial correction UX

- Rebuild the Unit Money experience to the intended complete grouping.
- Add direct immutable-payment correction/reversal/adjustment UX from payment detail on web and
  mobile, with original account/Unit/tenant/payment context prefilled.
- Verify tenant account, charges, receipts, allocation, deposits, operating costs, and balances agree.
- Profile and fix Accounting Reports and Security Deposit queries with generated SQL/query-plan proof.
- Keep all aggregation/filtering/paging DB-side and preserve append-only financial history.

### Lane 3 — Lists, navigation, visual quality, and broad workflows

- Inventory every management grid/list and its required columns, search, filters, paging, loading,
  empty, error, and retry states.
- Restore the product's reusable grid animations, including reduced-motion behavior.
- Fix incomplete data and slow queries, starting with Units.
- Validate Guided Setup, sample-data creation, scan/import, Properties, Owners, Units, Tenants,
  Leases, Applications, Work, Messages, Notices, roles/capabilities, and both web/mobile navigation.
- Use a full usable viewport; do not maximize the headed browser in a way that obscures the real page.
- Do not create screenshots or other temporary evidence unless the user asks. Record durable textual
  evidence, request timing, API results, and code/test references.

## Current repository state

The foundation worktree was clean at `a0fd6694` before this handoff document was added.

Recent commits:

```text
a0fd6694 Enable anonymous native signing under RLS
5c129a70 Assert demo leases use supplied source
cb2997a6 Use supplied lease as move-in default
bbd2f630 Allow assigning unbound applications at move-in
314877bb Record foundation preview verification
ae9eb1cf Complete public application RLS writes
ed3fdbdd Fix live foundation walkthrough defects
8419a55 Fix lease detail page title
```

Do not work from `main`; continue this existing worktree/branch. Do not create more worktrees for the
three shared-lane testers. Preserve unrelated main work.

## Public signing change at current HEAD

`a0fd6694` fixes a real anonymous native-signing failure:

- Only a SHA-256 hash of the opaque signing token enters a dedicated PostgreSQL session setting.
- Narrow token-scoped RLS policies expose only the signing request, signer, legal artifacts, files,
  pending drawn-signature upload, and signing audit records required by the public signing endpoint.
- The anonymous request no longer receives ordinary workspace scope.
- The public sign transaction records `ExecutionPending`; the existing Engine reconciler creates the
  final executed PDF.

Focused local foundation SQL tests passed 22/22. The first Azure PostgreSQL migration test on the
pre-amend commit failed because `StoredFiles.EntityId` is `bigint` while the SQL helper accepted
`integer`. The commit was amended to `a0fd6694` with the correct `bigint` signature and the static
tests passed again. The exact amended commit has **not** yet passed the Azure PostgreSQL integration
test and has **not** been deployed. Do that only after the broad lanes establish their first coherent
checkpoint, unless another flow specifically depends on signing.

The prior signing token/fixture may still exist in the preserved preview database, but do not rely on
it as the only lifecycle fixture. Create deterministic, clearly labeled E2E fixtures through the
application where practical.

## Protected preview and verification rules

- The protected preview is user-owned persistent state. Replace application containers to update it;
  do not reset or delete PostgreSQL volumes/data.
- Azure verification runner: `azureuser@100.126.201.65` using
  `~/.ssh/rental-build-runner-01-key.pem`.
- Use the `remote-verification-handoff` skill and the shared heavy lock. Only one heavy build/test at
  a time.
- Send an exact clean committed SHA. Do not auto-commit unrelated dirty work for a handoff.
- Current protected-preview evidence in the reports was mostly against `8419a55`, not `a0fd6694`.
- Temporary remote handoff directories may contain root-owned build output. Remove only the exact
  temporary handoff directories with appropriate permissions; never prune Docker broadly or touch
  `rc-preview-rental` volumes.

## Existing scenario material

- `Docs/Testing/Scenarios/07-foundation-auth-onboarding-roles.md`
- `Docs/Testing/Scenarios/08-foundation-rentals-scan-leasing.md`
- `Docs/Testing/Scenarios/09-foundation-operations-communications.md`
- `Docs/Testing/Results/2026-07-15-001/`
- Notion E2E run:
  `https://app.notion.com/p/E2E-run-2026-07-15-001-Stabilize-the-foundation-replacement-39e394b0689d81948b15c3e3c450dc3f`

Useful already-fixed boundaries include visible invalid-login errors, Guided Setup waiting for resume
detection, contextual Unit Scan naming its inherited rental, anonymous application intake under
token-scoped RLS, team-routing preview SQL translation, and deterministic Messages back navigation.
Reverify them after integration, but do not spend the next session reopening them without evidence.

## Known remaining boundaries beyond the latest screenshots

- Google OAuth callback registration/configuration still needs real success proof.
- Positive Property Manager, Leasing Agent, Maintenance Technician, Owner, and Tenant sessions still
  need activated test identities and full capability/scope walkthroughs.
- Mobile needs physical-device proof after the main data/UX defects are fixed.
- Notification settings still need final user-centered verification of My alerts, Team routing,
  Tenant notices, supplied editable templates, recipients, delivery modes, and help copy.
- The possession planned date previously rendered one day early, likely from treating a date-only
  value as a UTC instant. Reproduce and correct it.

## Completion standard

Do not hand the product back merely because it builds or routes render. Before user acceptance:

1. Main grids load promptly and show complete, internally consistent data.
2. A Unit's summary, leasing, tenant/lease, money, maintenance, and history views agree on the same
   canonical lifecycle.
3. The lease lifecycle, payment correction, scan/import, setup, and role-specific journeys work in
   real browser/device flows.
4. Web and mobile preserve useful interaction patterns and scanning as a primary differentiator.
5. Obvious failures have visible, actionable errors.
6. Changes are committed, remotely verified, deployed to the protected preview, re-walked, merged
   without overwriting unrelated work, Notion is synchronized, and this worktree is removed only
   after merge.

