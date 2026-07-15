# Foundation Replacement E2E Run — 2026-07-15-001

- Environment: `https://rental-command.chimp-map.ts.net`
- Starting source: `9a20158a4dbc0e3ac177ddd75d369e5c65ab55ec`
- Goal: stabilize the complete clean-replacement foundation before user acceptance and merge.
- Method: three domain testers read the implementation, author durable scenarios, explore the real UI,
  fix obvious failures directly in the shared foundation worktree, and record reproducible evidence.
- Constraint: heavy builds, test suites, and preview refreshes are serialized by the orchestrator.
- Evidence policy: no generated screenshots; observable UI state, request/response behavior, logs, code
  references, and automated regression checks are recorded in the scenario results.

## Scenario queue

1. `07-foundation-auth-onboarding-roles.md` — authentication, sample data, guided setup, team and roles.
2. `08-foundation-rentals-scan-leasing.md` — rentals, Unit Command Center, scan/import, applications and leases.
3. `09-foundation-operations-communications.md` — money, work, messages, notifications, owner and tenant UX.

## Initial release-gate failure

- The Google sign-in action is absent from the live login page.
- Password login briefly submits, remains on `/login`, and renders no error.
- Direct `POST /api/v1/auth/login` with the seeded administrator succeeds, proving the identity and API
  authentication path are present.
- The web form request is rejected with HTTP 403 before it reaches the API, so the initial investigation
  is focused on SvelteKit origin/runtime configuration and preview secret propagation.

## Outcome

Partial pass. The release-blocking sign-in failure and every source defect repaired during this run
are now proven against the protected preview. Unknown and wrong-password accounts receive the same
visible error; valid administrator login succeeds; Guided Setup waits for canonical resume detection;
Unit Scan names its inherited property and Unit; Team Routing shows its effective-recipient preview;
and a newly generated anonymous application link accepts a submission that immediately appears in
the authenticated Applications list.

The public-application test exposed one additional PostgreSQL boundary after the form first loaded:
`INSERT ... RETURNING` also requires token-scoped SELECT policy on the inserted application, audit,
and outbox rows. Commit `ae9eb1cf` adds those policies without granting update/delete or ordinary
workspace access. The focused foundation SQL contract passes 21/21 tests, and the exact policies
were applied to the preserved preview schema before the successful browser submission.

Known remaining release work is explicit rather than hidden: Units/list-detail, Accounting Reports,
and Security Deposits still have slow server-side queries; Security Deposits now ends in a retryable
error instead of a false empty state. Google OAuth still requires external callback registration,
and the positive Property Manager, Leasing, Maintenance, Owner, and Tenant persona walkthroughs need
activated test identities.
