# Foundation remediation handoff

Date: 2026-07-24  
Task checkpoint: TSK-733  
Blueprint tasks: TSK-668, TSK-670, TSK-672, TSK-674  
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-670-role-experience-spec`  
Publication branch: `tsk-668-670-674-foundation-rewrite`  
HEAD before all dirty work: `77c69102bb440d7c47a69cdf62bb2848712f1d5f`

## Why this handoff exists

The user stopped the current session because it had drifted back into a per-lane
implementation/relevance/fixer loop after the user had explicitly rejected that workflow.
Implementation is paused. No subagent remains active.

The next session must resume from the current dirty worktree without restarting discovery,
discarding accepted work, or invoking another contract loop.

## User workflow override — binding for the next session

1. Do **not** invoke `execute-plan-contract-loop`,
   `execute-continuous-plan-contract-roadmap`, or an equivalent role-transition loop.
2. Do **not** run a relevance/scope review for every small implementation batch or every
   individual correction.
3. Default to implementing directly in the root session. Use a small number of parallel
   implementation agents only when broad, non-conflicting work is genuinely faster in parallel.
   Do not create agents merely to satisfy a workflow role.
4. Reviews are periodic coherent milestones:
   - one code review and one scope/compliance review after a meaningful combined source
     milestone;
   - one final review after a substantial correction batch if the first milestone review found
     real defects;
   - no recursive per-fix reviewer/fixer loop.
5. Acceptance is separate from review. Run focused automated acceptance through one serialized
   Azure queue, then verify the UI in batches of three or four related flows.
6. Continue implementation after an unrelated blocker by moving to another in-scope item. Do not
   mark the goal blocked merely because one command or lane is temporarily unavailable.
7. Stop before the separate `run-e2e-tests` workflow. The user will start that workflow.

## Non-negotiable engineering constraints

- Preserve the dirty shared worktree. Do not stage, commit, stash, reset, clean, or revert it.
- Current status is approximately 398 changed paths: 276 modified/added tracked, 5 tracked
  deletions, and 117 untracked paths. Much of this is accepted foundation WIP.
- All SQL filtering, joins, aggregation, grouping, sorting, and paging must remain DB-side as one
  EF-translated SQL statement or a database view/function. No materialized authorization-ID
  lists, client-side filtering/counting, or N+1 queries.
- Run only one heavy build/test command at a time. Prefer the Azure verification VPS and shut down
  .NET build servers afterward.
- Do not invoke `run-e2e-tests` during this remediation handoff.
- The user explicitly authorized replacing the stale `rc-preview-rental` test stack and recreating
  its throwaway database with the latest code. Do not preserve an old test database merely because
  it already exists. Do not touch unrelated VPS stacks.

## Current live Azure state

Stable URL: `https://rental-command.chimp-map.ts.net`  
VPS: `azureuser@100.126.201.65`  
Verification lock: `/srv/dev-stacks/.locks/azure-heavy.lock`

At handoff:

- API, web, gateway, Engine, PostgreSQL, and the Tailscale bridge are running.
- API and PostgreSQL report healthy.
- The Azure Android emulator is connected as `emulator-5554`.
- Host load was `0.19, 0.15, 0.27`; RAM was 15 GiB total with 8.7 GiB available.
- The PostgreSQL container is live with a 3-CPU limit.
- The deployed application image does **not** include the real authorization-query latency fix.
- A live user Dashboard request on 2026-07-24 took 17.18 seconds server-side:
  `PortfolioController.Dashboard` executed in 17073 ms and the request finished in 17184 ms.

Do not report the slowness as fixed. The only live improvement is a temporary PostgreSQL CPU
increase from 1 CPU to 3 CPUs. That reduced the prior 35–37 second cancel/retry experience to
roughly 14–17 seconds, which is still a confirmed regression.

## Slowness diagnosis

Evidence from the live Azure stack:

- The browser client has a 20-second API timeout plus TanStack retry. Earlier Dashboard attempts
  were cancelled at 20 seconds and retried, producing an apparent 35–37 second load.
- At the original 1-CPU limit, PostgreSQL pinned one CPU and accumulated substantial throttled
  time during one Dashboard load.
- Raising PostgreSQL to 3 CPUs improved but did not solve the issue.
- With 3 CPUs, captured statements still took approximately:
  - Dashboard money query: 5.35 seconds
  - Accounting snapshot aggregate: 4.49 seconds
  - Recent activity projection: 3.49 seconds plus planning/binding
  - Occupancy: 1.20 seconds
- `auto_explain` showed the same authorization graph expanded repeatedly inside queries:
  AuthSessions → access contexts → memberships → assignments → role capabilities, plus RLS
  subplans.
- Each authenticated management request also repeatedly opened the scoped EF connection and
  reran request-context/GUC setup.

The source correction therefore has two parts:

1. hold one scoped EF connection through the authenticated request; and
2. replace the expanded capability graph with one opaque, composable, DB-side capability-scope
   rowset.

## Backend performance source — implemented but not accepted or deployed

### Request-scoped connection reuse

Files:

- `RentalCommand.Api/Auth/CanonicalAccessContextMiddleware.cs`
- `RentalCommand.Api.Tests/Auth/CanonicalAccessContextMiddlewareConnectionTests.cs`

Behavior:

- After successful authenticated canonical-context resolution, the middleware opens the same
  scoped `RentalCommandDbContext` once, holds it through downstream request execution, and closes
  it in `finally`.
- Unauthenticated requests remain unchanged.
- `/api/v1/hubs...` requests resolve context but do not hold a long-lived SignalR DB lease.
- Malformed, unavailable, and stale-context behavior is preserved.

This source received a prior milestone review pass. It has not been built, tested on Azure, or
deployed.

### Opaque capability-scope authorization query

Files:

- `RentalCommand.Data/Authorization/EffectiveCapabilityScopeQuery.cs`
- `RentalCommand.Data/Authorization/WorkspaceAuthorizationQuery.cs`
- `RentalCommand.Data/Authorization/WorkspaceAuthorizationEvaluator.cs`
- `RentalCommand.Data/FoundationBaselinePostgreSql.cs`
- `RentalCommand.Data/Migrations/20260724150000_AddEffectiveCapabilityScopeAuthority.cs`
- `RentalCommand.Data.Tests/FoundationBaselinePostgreSqlTests.cs`
- `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs`
- `RentalCommand.IntegrationTests/RlsResourceScopePlanRegressionTests.cs`

Implemented behavior:

- Adds composable `rc_api_effective_capability_scopes(...)`.
- Keeps capability, assignment, selected-property, and assigned-work filtering DB-side.
- Explicit session/user/context/revision coordinates must match the current request GUCs.
- Uses `STABLE SECURITY DEFINER`, fixed `search_path`, NOLOGIN authority ownership, PUBLIC revoke,
  and API-only execute permission.
- Reorders `rc_api_scope_allows` so a supplied normal session is evaluated first. An invalid or
  stale supplied session cannot fall through to registration bootstrap.
- Adds immutable migration `20260724150000`.

The periodic backend review found one exact acceptance-fixture defect:

- `WorkspaceAuthorizationKernelTests.NewContext()` still uses the PostgreSQL owner connection and
  sets no canonical request GUCs.
- The new production function correctly requires `session_user = 'rentalcommand_api'` and matching
  GUC coordinates.
- Therefore the focused valid-property authorization test would receive an empty result.

The correction was assigned and then interrupted at the user's request before any source edit was
made. Resume by changing only the test fixture:

1. keep owner contexts for migration and seeding;
2. use a real `rentalcommand_api` login connection for authorization-query contexts;
3. apply the canonical request GUCs before those queries;
4. do not use `SET ROLE`, because the function intentionally checks `session_user`;
5. do not weaken the API-only guard or grant Engine execution.

After that correction, do not start another micro-review loop. Run the focused acceptance below;
fold any genuine test failure into one backend correction batch, then perform one final combined
backend code/scope review.

## Loading and navigation regressions — source complete, not tested or deployed

The user confirmed that bare page text such as “Loading tenant and lease relationship...” is a
regression even if a coincident rebuild temporarily amplified latency. Major page/section loaders
must preserve layout with a skeleton or centered spinner. Query failures must show a useful error
and retry action rather than blank, false-empty, or endlessly loading UI. Compact operation states
such as “Downloading...”, “Saving...”, and “Load more” remain compact.

### Shared navigation/loading foundation

Key files:

- `web/src/lib/components/AppShell.svelte`
- `web/src/lib/components/shared/LoadingState.svelte`
- `web/src/lib/components/unit/unit-loading-states.test.ts`

Implemented:

- Replaces the unsupported `account_tree` ligature that rendered as raw `ACCOUNT_TF`.
- Replaces the unsupported technician `schedule` ligature.
- Removes a dormant unsupported profile glyph mapping.
- Adds accessible page, section, and spinner loading variants with stable geometry and
  reduced-motion behavior.

### Unit and Lease detail

Files include:

- `web/src/routes/(protected)/units/[id]/+page.svelte`
- `web/src/routes/(protected)/leases/[id]/+page.svelte`
- `web/src/lib/components/unit/tabs/LeaseTab.svelte`
- `web/src/lib/components/unit/tabs/ListingTab.svelte`
- `web/src/lib/components/unit/tabs/ApplicationsTab.svelte`
- `web/src/lib/components/unit/tabs/ExpensesTab.svelte`
- `web/src/lib/components/unit/tabs/MaintenanceTab.svelte`
- `web/src/lib/components/unit/tabs/RentTab.svelte`
- `web/src/lib/components/unit/tabs/TimelineTab.svelte`

Implemented:

- Replaces major bare loaders with shared loading states.
- Adds useful retry branches.
- Preserves the flat Unit tab hierarchy; no tabs-within-tabs were introduced.
- Uses one neutral bounded Property-money placeholder while rental-structure eligibility is
  unknown, avoiding skeletons for sections that may disappear.

### Leasing, Technician, Owner, and Tenant role surfaces

Implemented across the role landing/detail/list routes and related contract tests:

- Leasing Today, list workspaces, and record detail
- Technician assignment list and detail
- Owner overview, properties, approvals/messages, statements, statement detail, distributions
- Tenant dashboard appointments, appointments, maintenance list/detail, payments, and lease

These loader/error hunks received one combined role milestone review pass. No build/test/runtime
proof has run.

### Management, settings, money, and reports

Implemented in:

- `web/src/lib/components/records/PaymentDetail.svelte`
- `web/src/lib/components/records/ExpenseDetail.svelte`
- `web/src/lib/components/records/WorkOrderDetail.svelte`
- `web/src/routes/(protected)/settings/integrations/ai/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/my-alerts/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/team-routing/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/tenant-notices/+page.svelte`
- `web/src/routes/(protected)/tax/+page.svelte`
- `web/src/routes/(protected)/accounting/+page.svelte`
- `web/src/routes/(protected)/accounting/past-due/+page.svelte`
- `web/src/routes/(protected)/banking/+page.svelte`
- `web/src/routes/(protected)/owners-report/+page.svelte`
- `web/src/lib/components/management-loading-states.test.ts`

These loader/error hunks received one combined management milestone review pass. No
build/test/runtime proof has run.

## Azure outbound integrations and user secrets

The user wants outbound development integrations active on the Azure test stack: email, SMS,
Google login, Google Places, Plaid, QuickBooks, and the configured AI provider. Nothing is live
production; the user will rotate secrets before launch.

Source files:

- `RentalCommand.Api/Program.cs`
- `deploy/docker-compose.preview.yml`
- `RentalCommand.Api.Tests/Configuration/PreviewIntegrationConfigurationContractTests.cs`

The compose source now maps:

- Twilio to API and Engine
- SendGrid to Engine
- Google OAuth and Google Places to API/web as appropriate
- Plaid to API
- QuickBooks to API and Engine
- Platform administrator email to API and web
- existing Assistant provider settings to API and Engine

The local user-secret allowlist was merged atomically into the root-owned Azure preview `.env`
without printing values. The remote file remains mode `0600`. Twenty-one allowlisted integration
keys are present and nonblank.

The values are **not active** until the reviewed API, Engine, and web containers are rebuilt and
recreated from the new compose source. After recreation, verify only enabled/presence booleans;
never print secret values.

Notes:

- Google OAuth must allow the stable Tailscale origin/callback in its provider console.
- Outbound Twilio can work. Inbound Twilio webhooks cannot reach a Tailscale-only URL without
  separately approved public ingress.
- QuickBooks wiring does not by itself complete any intentionally unfinished product callback.

## Prior accepted source and proof that must be preserved

### SignalR and Help

- SignalR fanout now includes effective relationship tenants.
- Web/mobile invalidation includes tenant portal snapshot and notification keys.
- AI provider help article and Knowledge Base test are present.
- Focused prior tests passed:
  - server/help: 14/14
  - web: 4/4
  - Flutter: 1/1

The current web container and Azure emulator APK do not yet prove the latest SignalR source.
Runtime proof remains pending after the final rebuild.

### Android proof already completed

Previously proven on a real emulator:

- zero-dollar/not-due charges are hidden;
- singular “1 overdue item” renders correctly;
- bottom navigation labels do not wrap;
- Repairs is a filtered list with FAB and bottom-sheet creation;
- authentication survives force-stop;
- raw backend exception text is not shown.

Still pending:

- live SignalR push without manual refresh;
- original lease PDF download using a seeded tenant with an agreement;
- plural overdue branch with two overdue items.

## Known DB-side-rule defects still needing a direct correction

These were observed during the loading work and were not changed:

1. `web/src/routes/(protected)/tax/+page.svelte` derives `vendorsNeedingW9` with
   `vendors1099.filter((vendor) => vendor.needsW9).length`.
   The API must project the count DB-side.
2. `web/src/lib/components/unit/tabs/LeaseTab.svelte` loads a bounded Unit list and excludes the
   current Unit client-side for transfer choices.
   Transfer options must be filtered and paged server-side.

Handle these together as one direct data-access correction batch. Do not create separate task or
review loops for each line.

## Immediate resume order

### 1. Finish the single backend fixture correction

Modify only the authorization-query test setup described above. Preserve the production API-only
guard.

### 2. Run one serialized Azure backend acceptance batch

Use a unique source handoff and the shared Azure heavy lock. The worktree is intentionally dirty;
do not create a surprise commit. Record the base SHA and a source-manifest/diff hash for the exact
handoff.

Focused commands:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
  --filter "FullyQualifiedName~CanonicalAccessContextMiddlewareConnectionTests|FullyQualifiedName~PreviewIntegrationConfigurationContractTests"

MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.Data.Tests/RentalCommand.Data.Tests.csproj \
  --filter "FullyQualifiedName~FoundationBaselinePostgreSqlTests"

MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter "FullyQualifiedName~RlsResourceScopePlanRegressionTests|FullyQualifiedName~SameAssignmentMustSupplyCapabilityAndScope_DecoyDoesNotLeak|FullyQualifiedName~AuthorizationExistsPrecedesOrderingAndPagingInGeneratedSql|FullyQualifiedName~MoneyAuthorizationRemainsDbSideBeforeOrderingAndPaging"

dotnet build-server shutdown
```

If one command fails, correct the coherent backend batch directly, rerun the failed command and
its nearest regression coverage, then perform one final combined backend code/scope review.

### 3. Run one serialized web acceptance batch

```bash
pnpm --dir web check:native
pnpm --dir web check
pnpm --dir web test:unit
```

Do not run these concurrently with the .NET/PostgreSQL commands.

### 4. Replace the Azure test stack with the accepted source

Under the shared lock:

- build API, Engine, and web images from the exact accepted dirty-source handoff;
- run the compose `migrate` service;
- recreate API, Engine, web, gateway, and PostgreSQL as needed;
- the user has authorized recreating the throwaway rental preview database;
- activate the mode-0600 integration environment;
- verify container health and stable Tailscale URL;
- build/install the latest Android APK on Azure `emulator-5554`;
- do not consume the local Mac with the stack or emulator.

### 5. Prove performance before broad UI acceptance

After warm-up, collect at least 10 serial samples:

- Dashboard API: target p50 ≤ 2 seconds and p95 ≤ 3 seconds
- Accounting snapshot: target p95 ≤ 1 second
- Dashboard money, recent activity, and occupancy SQL: target p95 ≤ 500 ms
- no browser 20-second cancellation/retry
- Guided Setup and cross-page navigation must no longer show the global 10–17 second pattern

If the shared authorization correction does not meet the target, capture the actual remaining
slow SQL and fix that evidenced shared source. Do not mask it with more timeout or retry changes.

### 6. Prove loading/error states in real UI batches

Use three- or four-flow batches, for example:

1. Dashboard, Guided Setup, Unit detail, Lease detail
2. Leasing, Technician, Owner, Tenant portal
3. Accounting, Banking, Tax/Owner reports, notification settings

For each:

- verify skeleton/spinner geometry;
- verify no raw loading ligature/text regression;
- force one API failure or unavailable state and verify explicit error plus retry;
- verify success after retry;
- check console/runtime errors.

Route any reproduced defects into one direct correction batch and re-prove the affected flows.
Do not create a reviewer/fixer loop for each screenshot.

### 7. Finish remaining product acceptance

- SignalR notification appears without refresh
- AI Help search article
- Engine-backed Scan / Add
- Tenant original signed lease PDF download
- plural overdue copy
- outbound email/SMS/Google/Places smoke checks using controlled test recipients/accounts
- DB-side correction for Tax W-9 count and lease transfer options

### 8. Update TSK-733 and stop before `run-e2e-tests`

Persist:

- exact source handoff identity;
- automated command results;
- Azure deployment identity;
- 10-sample latency evidence;
- browser/emulator proof;
- remaining discrepancies, if any.

Do not invoke `run-e2e-tests`; hand control back to the user.

## Source-of-truth notes

- `Docs/Reviews/2026-07-24-foundation-remediation-status.html` is useful historical context but is
  now stale regarding the live latency regression, new loading-state work, Azure secret wiring,
  and current acceptance state.
- `Docs/Reviews/2026-07-16-foundation-blueprint-compliance-review.html` remains the broad
  discrepancy inventory.
- `Docs/Reviews/2026-07-15-foundation-e2e-stabilization-handoff.md` remains the original
  stabilization context.
- This handoff is the resume authority for work performed after those documents.

## State at pause

- All subagents are completed or interrupted; none should be assumed to be working.
- The authorization test-fixture correction has not been applied.
- No new builds/tests were run for the performance, Azure integration, or loading-state batches.
- No latest-source Azure deployment was performed.
- The live Dashboard remains approximately 17 seconds.
- `run-e2e-tests` has not been invoked.
