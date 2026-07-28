# TSK-754 Fresh-Agent Handoff

Prepared 2026-07-27 for continuation of the 2027 full-company simulation.

## Authoritative continuation checkpoint — 2026-07-28, January 29 stop

This section supersedes every older checkpoint below. The preserved database is reconciled
through the completed January 29 runs listed here, and the simulation clock is intentionally
frozen at **2027-01-29 00:00 America/New_York** (`2027-01-29T05:00:00Z`). Do not advance it
until `YS-212` and `RUN-20270129-04` are resolved.

### Exact repository and runtime state

| Item | State at handoff |
| --- | --- |
| Notion task | `TSK-754` — **Doing**, High, verified 2026-07-28 |
| Worktree | `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution` |
| Branch | `tsk-754-year-simulation-execution` |
| Latest implementation commit | `d921bf1391f010d1c1d72266a4288eb5cb9918bf`; the handoff-only commit is immediately above it |
| Tracked worktree state | Clean |
| Database | `rentalcommand-tsk754-db`, healthy on local port `5754` |
| API | `tsk754-api`, rebuilt from `d921bf13`, healthy on `https://localhost:5666` |
| Web | `tsk754-web`, healthy on `https://localhost:5667` |
| Engine | Stopped; keep it off until the exact reminder-worker run |
| Browser automation | No browser session running |
| Physical phone | Samsung `SM_S906U`, wireless ADB online |
| Android package | `com.rentalcommand.rental_command.dev`, foreground |
| ADB reverse | `host-24 tcp:5666 tcp:5666` |
| Heavy tests/builds | None running |

The installed physical-phone APK is still the clean local-API artifact built from source SHA
`46a9ae36`:

```text
/Users/blackcolours/.codex/remote-artifacts/tsk754-mobile-jan27-46a9ae36-20260728t1259z/app-dev-profile-arm64-local-api-clean.apk
SHA-256 9d426f...b6604c5
```

The changes after that SHA in this checkpoint are API, web, and planner-script work; no new
mobile binary was required for the completed January 29 web acceptance.

### January 29 execution status

| Run | State |
| --- | --- |
| `RUN-20270129-01` | **Completed.** Tenant Messages is live-proved after `YS-210`; final send returned in 375 ms and rendered exactly once. |
| `RUN-20270129-02` | Safely blocked by the `YS-211` generated-field planner correction; `FLD-0030` is `Select.Content`, not a saveable control. |
| `RUN-20270129-03` | **Completed.** Real Property Type persisted, reloaded, and was restored; rental structure remained correctly locked. |
| `RUN-20270129-04` | Blocked by `YS-212`; the rent-due-in-five-days reminder correctly excludes Yara while her lease has a possession reconciliation exception. |
| `RUN-20270129-05` | Safely blocked by the existing `YS-207` setup planner correction; the live portfolio is already 100% configured. |

The ignored execution ledger has 10 valid columns on every row after these updates. The bug
ledger still has 14 older malformed legacy rows, but all newly written `YS-210` through
`YS-212` rows have the canonical 9 columns. Continue editing both ignored ledgers only with
`apply_patch`.

### `YS-210` completed realtime repair

The original tenant reply committed once but held the HTTP response for 36.7 seconds. The
measured repair sequence is:

| Commit | Result |
| --- | --- |
| `cecab709` | Bounded SignalR delivery. |
| `9151f505` | Aligned notification fanout. |
| `3ae8f77e` | Used the durable saved access context for targeted notification authorization. |
| `96a31cd6` | Batched conversation and notification invalidations; live response still took 22.1098 seconds. |
| `fbffebec` | Resolved all saved-context notification recipients in one DB-side authorized query; focused suite passed 6/6, but live response still took 15.517 seconds. |
| `d921bf13` | Moved only post-commit realtime invalidation onto a bounded nonblocking hosted queue and removed the post-commit notification reload. |

`d921bf13` passed 22 focused tests covering the conversation path, the saved-context
authorization query, bounded-queue overflow, and continuation after a queued batch failure.
The final real Yara UI send:

- returned HTTP 200 in **375 ms**;
- logged API action completion in **314.1066 ms**;
- cleared the composer and rendered the exact text once without reload;
- created exactly one `ConversationMessage 18`, sender `Tenant`, at the frozen
  `2027-01-29T05:00:00Z`;
- left no browser or test process running.

`YS-210` and `RUN-20270129-01` are recorded as fixed/completed in the ignored QA ledgers.

### Other completed January 28–29 work

- `f7cb24c2` fixed Lease Template Designer width/height validation; the invalid height stayed
  unsaved in live proof and the canonical field was restored.
- `5512384e` removed ignored Status and Notes controls from scan-first lease review.
- `0324d354` fixed the field-inventory parser so only exact native `input`, `select`, and
  `textarea` tags are inventoried. The regression ignores Svelte `Select.Root`, `Trigger`,
  `Content`, and `Item`. Active year artifacts were intentionally not regenerated mid-run.
- `RUN-20270129-03` used the real Property Manager UI on Arbor House Property 1. Property Type
  changed Single-family to Condo, survived reload, and was restored to Single-family.
  `AtomicAuditLogs 3579` and `3580` record the frozen-time transitions.

### Current blocker: `YS-212` historical possession reconciliation

Do not treat this as a notice-worker bug. The DB-side candidate filter is correctly excluding
a contradictory lease.

Yara Brooks has:

```text
LeaseManagementId: 8
RelationshipNumber: LM-SCAN-00000009
AgreementId / AgreementNumber: 8 / SCN-0008
PlannedPossessionAtUtc: 2026-03-01T00:00:00Z
PossessionGivenAtUtc: NULL
Agreement term: 2026-03-01 through 2027-08-31
Agreement fully executed: yes
Derived lifecycle: Upcoming
Exception: GoverningAgreementWithoutPossession
HasReconciliationException: true
```

A DB-side portfolio aggregation found **13** legacy `Upcoming` relationships with this same
exception across LeaseManagement IDs 1 through 14. The current production scan confirmation
path already validates and persists a reviewed `PossessionGivenAtUtc` for a newly imported,
fully signed, already-active lease. Do not duplicate or loosen that safeguard.

The required smallest repair is a supported staff reconciliation action exposed only when the
DB-derived exception is exactly `GoverningAgreementWithoutPossession`:

1. An authorized rentals-management user enters the real historical possession date.
2. The server requires a fully executed governing agreement.
3. The date must fall within that agreement term and cannot be after effective business time.
4. Future/upcoming relationships and every other reconciliation state fail closed.
5. Possession, semantic audit, and data-update outbox persist atomically and idempotently in
   one explicit transaction.
6. Authorization and eligibility remain DB-side; no materialized-row filtering, grouping,
   aggregation, joins, N+1, or per-row follow-up queries.
7. Add PostgreSQL failure-injection proof and a focused web contract test.

No implementation for this action is in progress at handoff. The focused coding agents were
stopped cleanly before this document was written, and the tracked tree is clean. Do not use the
existing generic **Give possession** action for Yara: it would stamp January 29 and destroy the
true March 1, 2026 occupancy history. Do not patch the database directly.

### Exact next sequence

1. Verify the state without mutating it:

   ```bash
   git status --short
   git rev-parse HEAD
   curl -sk https://localhost:5666/api/v1/dev/clock
   tmux list-sessions
   adb devices -l
   adb reverse --list
   ```

   Expected history has the handoff-only commit immediately above implementation commit
   `d921bf13`; clock is Frozen `2027-01-29T05:00:00Z`; API and web are running; Engine is
   absent.

2. Dispatch one focused coder for `YS-212` using the exact reconciliation contract above. Do
   not create a reviewer swarm or another worktree.
3. Run only focused PostgreSQL/failure-injection and web contract tests, with
   `MSBUILDDISABLENODEREUSE=1`, then shut down build servers.
4. Rebuild and restart only `tsk754-api`. Confirm the frozen clock again.
5. In the real Property Manager web UI, open LeaseManagement 8 through normal navigation and
   reconcile possession to **2026-03-01**. Live-prove:
   - a future date is rejected without mutation;
   - the valid historical date commits once;
   - duplicate replay is idempotent;
   - DB-derived lifecycle becomes `Occupied`;
   - the reconciliation exception clears;
   - exactly one semantic audit and one data-update outbox fact exist.
6. Reconcile the other 12 affected legacy leases only through the same supported action or a
   separately proven supported batch command. Do not run direct SQL updates.
7. Only after LeaseManagement 8 is repaired, advance the frozen clock to the policy's genuine
   Jan 29 send time, **09:00 America/New_York** (`2027-01-29T14:00:00Z`).
8. Start the Engine only for the rent-reminder worker. Policy 10 is `rent-reminder`, five lead
   days, send hour 9, email + portal enabled, push disabled, and currently Draft. Follow the
   product's actual draft/approval lifecycle; do not invent a delivered notice if approval is
   required.
9. Prove `NTF-002` once on web and the connected physical phone, including safe copy, scoped
   deep link, read synchronization, configured-channel outcome, retry, duplicate suppression,
   and adjacent-role denial. No screenshots are required for this continuation.
10. Stop the Engine immediately, finish `RUN-20270129-04`, reconcile January 29, and only then
    advance to the January 31 close.

`RUN-20270122-03` still has physical-phone proof pending for the earlier rent-charge
notification. It can be paired with the `NTF-002` phone pass after `YS-212` rather than opening
another mobile setup lane.

### Cleanup state

- Browser cleanup: no Playwright browser is running.
- Build cleanup: no Rental Command build/test process is running.
- Worktree cleanup: no new worktree was created. Preserve the active TSK-754 worktree and the
  user-owned TSK-749 worktree.
- Engine cleanup: stopped.

## Authoritative continuation checkpoint — 2026-07-28

This section supersedes the older January 14 checkpoint below. The preserved database was
reconciled through January 21 and the simulation clock is now intentionally frozen at
**2027-01-22 00:00 America/New_York** (`2027-01-22T05:00:00Z`). Do not roll it back or advance it
until all three January 22 runs are reconciled.

- Local API and web are running in `tsk754-api` and `tsk754-web`; Engine is stopped.
- The Rental Command web URL is **`https://localhost:5667`**. Port 5173 belongs to another local
  application and must not be used for this simulation.
- `RUN-20270122-01` is complete. `YS-165` was fixed in `adfe0ab2` and live-proved with generic
  signed-lease copy while the authorized stored JPEG downloaded successfully.
- `RUN-20270122-02` remains blocked by planner data. `7525fa80` and `70bdc625` added the
  role-specific Leasing Agent application route and canonical PrepareMoveInDialog without the
  unauthorized management/screening calls. The rebuilt endpoint correctly fails closed for the
  only application because the test user is assigned Properties 21 and 23 while the application
  belongs to Property 30.
- `YS-168` records an independent planner contradiction: the only application was atomically
  prepared on January 7, while the same one-submit Prepare-move-in dialog fields are split across
  January 21, 22, 25, and 26. Do not fabricate a replacement application.
- `RUN-20270122-03` remains blocked. `YS-163` is committed in `ed5c5567`, but no safe unposted
  charge exists on January 22. A DB-side prospective query found the next genuine event on
  January 26 for one January 31 charge, then 37 February 1 charges on January 27.
- `YS-167` is fixed in `275be564` with focused PostgreSQL proof. Live proof is queued for the
  first genuine January 26 charge because no safe unposted charge exists on January 22.
- `YS-169` is fixed and live-proved in `d00da2af`: the preference update, atomic audit, and
  data-update outbox all used the frozen January 22 business timestamp.
- `YS-157`, `YS-158`, `YS-159`, and `YS-164` are fixed and live-proved. The latest committed
  sequence is `d00da2af`, `275be564`, `70bdc625`, `7525fa80`, `adfe0ab2`, `4ac5d65a`,
  `ed5c5567`, and `9ee3738d`.
- No TSK-754 browser automation session is intentionally retained between proof runs. Each
  named session must still be closed and its process tree verified immediately after use.

## Outcome and current checkpoint

Continue TSK-754 from the preserved January database. Do not restart the year, replace the
database, or treat the historical January 14 checkpoint below as current.

The latest chronologically executed scenario is `RUN-20270121-04`, but the authoritative
simulation clock is intentionally frozen at **2027-01-14 00:00 America/New_York**. The clock
was rolled back after the run exposed missing January 8-15 property, ownership, deed, and
mortgage prerequisites. Complete those prerequisites and the critical loan-payment workflow
before resuming after January 21.

## Task and repository state

| Item | Current state |
| --- | --- |
| Notion task | `TSK-754` — **Doing**, High |
| Task URL | <https://app.notion.com/p/TSK-754-Execute-the-2027-full-company-simulation-in-Rental-Command-3a9394b0689d81b7a786fed90a67ea89> |
| Worktree | `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution` |
| Branch | `tsk-754-year-simulation-execution` |
| HEAD | `024b55c5fe2bdd3e83263b40b97268d015de54f6` |
| Upstream | `origin/tsk-754-year-simulation-execution`, same SHA |
| Tracked state before this handoff | Clean |
| Database | `rentalcommand-tsk754-db`, running on local port `5754` |
| API | Stopped |
| Web | Stopped |
| Engine | Stopped |
| Browser automation | No TSK-754 Playwright or Chrome session running |
| Preferred mobile target | Physical Samsung `SM_S906U` over wireless ADB |
| Azure emulator | Fallback and checkpoint/final-environment verification only |

This worktree is intentionally retained because TSK-754 is still active and unmerged. The
separate TSK-749 planning worktree is also not owned by this continuation and must not be
deleted.

## Read these first

1. `/Users/blackcolours/.codex/AGENTS.md`
2. Repository `AGENTS.md`
3. Repository `CLAUDE.md`
4. This handoff
5. `Docs/Testing/YearSimulation2027/README.md`
6. `Docs/Testing/YearSimulation2027/schedule.csv`
7. `Docs/Testing/YearSimulation2027/financial-oracle.csv`
8. `Docs/Testing/YearSimulation2027/journal.csv`
9. `output/qa/tsk-754-execution-ledger.csv`
10. `output/qa/tsk-754-bug-ledger.csv`

The committed planner and financial oracle are the expected-state authority. The two ignored
QA ledgers and evidence folder are the execution-state authority. Database readback is the
authoritative product-state check.

## Preserved artifacts

- The planner covers 30 properties, 40 units, 45 leases, every role, web and mobile, scan and
  manual entry, notifications, messaging, maintenance, CRUD, and financial reconciliation.
- There are **953 rendered uploadable scan files** under
  `output/pdf/tsk-749-year-simulation-scan-corpus/documents/`.
- There are currently **416 evidence files** under `output/qa/tsk754-evidence/`.
- The execution ledger has **104 rows, 89 unique run IDs, and 10 duplicated run IDs**.
  Deduplicate by run ID and evidence before final control totals.
- The bug ledger has **164 rows**:
  - 81 statuses beginning with `Fixed`
  - 78 `Open`
  - 1 `Open intermittent`
  - 1 `Reopened`
  - 2 `Fix in progress`
  - 1 `Planner correction required`
- Fourteen rows still say `Fixed locally and live verified; awaiting commit`. Reconcile every
  such row against Git history before claiming it is committed; the ledger wording may be
  stale.

Do not add the generated corpus, QA ledgers, screenshots, tokens, build outputs, or other
execution artifacts to Git. They are intentionally ignored.

## Mobile targets

### Physical phone — preferred now

The user confirmed wireless debugging is connected and wants the phone used instead of the
emulator.

```bash
/Users/blackcolours/Library/Android/sdk/platform-tools/adb devices -l
```

Expected device:

```text
adb-RFCT60SMJNN-9BQpeO._adb-tls-connect._tcp
model:SM_S906U
```

Installed development package:

```text
com.rentalcommand.rental_command.dev
```

Before using the local API, verify rather than assume the reverse:

```bash
/Users/blackcolours/Library/Android/sdk/platform-tools/adb reverse --list
/Users/blackcolours/Library/Android/sdk/platform-tools/adb reverse tcp:5666 tcp:5666
```

Run the Flutter app in debug mode against this device so UI changes can use hot reload. Do not
leave a stale Flutter process or ADB log stream running when handing the lane off again.

### Azure emulator — fallback/checkpoint

- Host: `azureuser@100.126.201.65`
- SSH key: `~/.ssh/rental-build-runner-01-key.pem`
- Remote ADB: `/opt/runner-tools/android-sdk/platform-tools/adb`
- Emulator: `emulator-5554`
- Package: `com.rentalcommand.rental_command.dev`

Use the `$remote-verification-handoff` runbook for Azure work. Preserve the reusable
`rc-preview-rental` database, volumes, credentials, uploads, and data-protection keys. Do not
improvise transfers, locks, deployment, or cleanup.

## Restarting the local isolated stack

The database is already running. Start only the services needed for the next test.

**Critical:** the API must start with `Simulation__Enabled=true`. A previous restart omitted
that flag and made `/api/v1/dev/clock` return 404. Verify the clock endpoint before doing any
product work. Use `Auth__ExposeDevTokens=true` only in this isolated QA environment.

Load the isolated container's owner password without printing it. Use that credential only for
the one-shot migration process. The long-running API and Engine must use their direct restricted
development logins; current startup code rejects `MigratorConnection` in a long-running process.

```bash
export TSK754_DB_OWNER_PASSWORD="$(
  docker inspect rentalcommand-tsk754-db \
    --format '{{range .Config.Env}}{{println .}}{{end}}' |
  sed -n 's/^POSTGRES_PASSWORD=//p'
)"
```

Apply migrations once after rebuilding:

```bash
env \
  ASPNETCORE_ENVIRONMENT=Development \
  DOTNET_ENVIRONMENT=Development \
  Simulation__Enabled=true \
  ConnectionStrings__MigratorConnection="Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=postgres;Password=${TSK754_DB_OWNER_PASSWORD}" \
  ConnectionStrings__DefaultConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_api;Password=rentalcommand_api_dev' \
  ConnectionStrings__EngineConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_engine;Password=rentalcommand_engine_dev' \
  Jwt__SecretKey='dev_only_super_secret_signing_key_at_least_64_chars_long_0123456789' \
  Jwt__Issuer=RentalCommand \
  Jwt__Audience=RentalCommandWeb \
  Seed__Enabled=false \
  dotnet run --no-build --project RentalCommand.Api -- --migrate-only
```

API:

```bash
tmux new-session -d -s tsk754-api \
  -c '/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/RentalCommand.Api' \
  "env ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development Simulation__Enabled=true Auth__ExposeDevTokens=true ASPNETCORE_Kestrel__Certificates__Default__Path='/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/web/.cert/api-cert.pem' ASPNETCORE_Kestrel__Certificates__Default__KeyPath='/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/web/.cert/api-key.pem' ConnectionStrings__DefaultConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_api;Password=rentalcommand_api_dev' Jwt__SecretKey='dev_only_super_secret_signing_key_at_least_64_chars_long_0123456789' Jwt__Issuer=RentalCommand Jwt__Audience=RentalCommandWeb Seed__Enabled=false dotnet run --no-build --urls 'https://localhost:5666;http://localhost:5665'"
```

Web:

```bash
tmux new-session -d -s tsk754-web \
  -c '/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/web' \
  "env NODE_EXTRA_CA_CERTS='/Users/blackcolours/Library/Application Support/mkcert/rootCA.pem' pnpm dev"
```

Vite currently binds Rental Command to `https://localhost:5667`. Confirm the printed URL after
startup instead of assuming its preferred port was available.

Engine, only when a scheduled worker is required:

```bash
tmux new-session -d -s tsk754-engine \
  -c '/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/RentalCommand.Engine' \
  "env DOTNET_ENVIRONMENT=Development Simulation__Enabled=true ConnectionStrings__DefaultConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_engine;Password=rentalcommand_engine_dev' dotnet run --no-build"
```

Keep the Engine stopped during manual backfill and inspection unless a scenario explicitly
needs a worker. This prevents unplanned background mutation while the clock moves.

Immediately after API startup:

1. Confirm `/api/v1/dev/clock` exists.
2. Confirm it reports the current authoritative frozen checkpoint. As of 2026-07-28 that is
   `2027-01-22T05:00:00Z`.
3. Log in fresh. Never reuse bearer tokens from `/tmp/tsk754-*`.
4. Confirm the phone's ADB reverse.
5. Start the web or Flutter debug client.

The clock provider starts in an in-memory Real default and refreshes from PostgreSQL
asynchronously. A first HTTP 200 can therefore briefly say Real even when the preserved database
row is Frozen. Poll until both `mode=Frozen` and the exact expected instant are returned; do not
mutate product data after a merely reachable but not-yet-refreshed clock response.

Synthetic QA credentials previously used include:

- Administrator: `qa.tsk754.azure.mobile@example.local` / `Admin123!`
- Verified administrator: `qa.tsk754.azure.verified@example.local` / `Admin123!`
- Property manager: `qa.tsk754.property.manager@example.local` / `Admin123!`
- Tenant 008 was reset to `Tenant123!`
- Default sandbox admin: `admin@rentalcommand.local` / `Admin123!`

Freshly verify access context and portfolio after login. Do not rely on old tokens or cached
role state.

## What has been completed

The test reached January 21 through real web and Android workflows while recording every
observed defect. It has exercised, among other areas:

- Scan-first lease, expense, insurance, receipt, mortgage, work-order, and loan-statement
  flows using exact rendered source files.
- Manual leases without an application, including the restored rent-charge start choices:
  current date, lease-start backfill, and custom date.
- Property/unit/tenant/lease setup and edit flows.
- Team creation, scoped assignments, assignment replacement/end state, suspension, and
  reactivation.
- Tenant portal navigation, payments/messages/maintenance links, validation, role isolation,
  and maintenance creation.
- Property-manager and leasing-agent role routes.
- Forgot password, reset password, change password, session revocation, and negative
  validation on web and Android.
- Simulation clock movement and time-dependent authorization.
- File type validation, rejection, retry, idempotency, scan confirmation, and audit search.
- Manual and scanned expenses with PostgreSQL allocation reconciliation.
- Notifications, message targets, appointments, vendors, work orders, audit activity, and
  destructive-action cancellation paths.

The latest completed chronological run is:

```text
2027-01-21 RUN-20270121-04
Manual web Create lease without an application
```

It created the Casey ManualJan lease on Dogwood Duplex Unit B and live-proved all three
rent-charge start choices. Do not repeat it unless a regression specifically requires it.

## Important committed fixes

The branch contains many fixes. The latest verified sequence is:

| Commit | Fix |
| --- | --- |
| `024b55c5` | Password reset/change now revoke prior sessions atomically; simulation clock no longer intercepts the Security submit button |
| `4cca0db8` | Team selected-property scope replacement no longer 500s; ended assignments render and behave as ended |
| `16c3886d` | Scan-confirmed expenses persist the required allocation |
| `f2f5283d` | Global scan authorization uses the correct security-time boundary under the simulation clock |
| `81824d17` | Failed scan retry is idempotent server-side |
| `a1de76d5` | Scan retries send an idempotency key |
| `3d349ccb` | Mobile quick actions no longer overlap content; actions use modal sheets |
| `dd7841e9` | Failed scans expose recovery actions again |

Other important fixed areas in the bug ledger include:

- Lease-scan possession date and guided-lease square footage.
- Simulation gates surviving correct restarts.
- Scan claim, timeout, and LLM process handling.
- Scan-created work-order images.
- Date pickers and vendor/work-order timestamps using simulation time.
- Outbox claiming and captured email delivery.
- Tenant appointments and conversation read state.
- Historical loan-period generation.
- Late-fee automation controls.
- Lease templates, native e-sign, RLS, rollback, and proration.
- Check-number account scoping.
- Mobile/global scan overlays and message-recipient targeting.
- Audit filters and search.
- Manual expense recovery, dates, and field exposure.
- Atomic work assignment.
- Future-token rejection, analytics zeros, and navigation overlays.

Use `output/qa/tsk-754-bug-ledger.csv` for the complete evidence-linked list. Do not rely on
this summary as the bug authority.

## High-impact open bugs

The most important known open defects include:

- `YS-109` — mobile scan context missing.
- `YS-110` — stale mobile property/owner cache.
- `YS-111` — state selector clears itself.
- `YS-112` — mobile property pagination is incorrect.
- `YS-113` — imported in-term executed leases can appear Upcoming/unoccupied without
  possession.
- `YS-114` — a confirmed scan can reopen as editable/create action.
- `YS-115` — January 15 control counts are polluted by demo/seed data.
- `YS-117` — move-out notice lease misclassification.
- `YS-118` — wrong scan is rejected.
- `YS-119` — incomplete expense can confirm.
- `YS-120`, `YS-122` — update/confirmed expense field and allocation loss.
- `YS-123` through `YS-126` — manual expense scope and field defects.
- `YS-128` — scan-created work order can return an unrelated tenant.
- `YS-133` — duplicate Created audit entries.
- `YS-147` — Android DocumentsUI returns to Money without uploading.
- `YS-154` — no supported web/mobile/API workflow to post a scheduled loan payment.
- `YS-156` — loan-payment statements are treated as new loans, not matched payments.
- `YS-157` — tenant-created work-order timestamp uses real time.
- `YS-158` — Property Manager startup calls Getting Started and receives 403.
- `YS-159` — Leasing Agent Today endpoint returns 403.
- `YS-160` — selected scan property is not bound to the loan destination.

Record every newly reproduced defect before dispatching a fixer. A fixer owns one clear bug or
one tightly related batch and must return the fix commit plus focused proof.

## Property and mortgage backfill checkpoint

P010 Juniper is complete:

- Exact mortgage scan `SCN-0053` was uploaded on Android.
- `ScanDraft 113` confirmed as `Loan 15`.
- The real web form completed every loan section.
- Active balance is `171,500.00`, monthly principal and interest `1,270.00`, escrow `318.00`.
- Deed `SCN-0052`, ownership, and property basis are attached/completed.
- Execution row `RUN-20270108-10` was appended.

The following property basis and ownership edits succeeded, despite later confusing 401
output:

| Planner property | Database property | Basis/ownership state | Deed | Mortgage |
| --- | --- | --- | --- | --- |
| P017 Quarry | 17 | Complete | Missing | Missing |
| P019 Summit | 19 | Complete | Missing | Missing |
| P020 Terrace | 20 | Complete | Missing | Missing |
| P022 Valley | 23 | Complete | Missing | Missing |
| P023 Walnut | 24 | Complete | Missing | Missing |

These remain incomplete:

| Planner property | Database property | Missing |
| --- | --- | --- |
| P025 Zenith | 26 | Basis, ownership, deed, mortgage |
| P026 Ash | 27 | Basis, ownership, deed, mortgage |
| P028 Chestnut | 29 | Basis, ownership, deed, mortgage |
| P029 Dogwood | 30 | Basis, deed, mortgage; ownership already exists |

Exact assets:

| Property | Deed | Mortgage |
| --- | --- | --- |
| P017 | `SCN-0064` Jan 9 | `SCN-0065` Jan 14 PDF |
| P019 | `SCN-0067` Jan 9 | `SCN-0068` Jan 14 PDF |
| P020 | `SCN-0069` Jan 9 | `SCN-0070` Jan 14 JPEG |
| P022 | `SCN-0072` Jan 10 | `SCN-0073` Jan 14 JPEG |
| P023 | `SCN-0074` Jan 10 | `SCN-0075` Jan 14 PDF |
| P025 | `SCN-0077` Jan 10 | `SCN-0078` Jan 15 PDF |
| P026 | `SCN-0079` Jan 10 | `SCN-0080` Jan 15 JPEG |
| P028 | `SCN-0082` Jan 10 | `SCN-0083` Jan 15 JPEG |
| P029 | `SCN-0084` Jan 10 | `SCN-0085` Jan 15 PDF |

A supported `POST /api/v1/documents` attempt returned 404 saying the referenced record was not
found in the portfolio. The later API restart accidentally omitted simulation support, so this
is only a **candidate authorization defect**. Reproduce it under the correctly configured
stack before assigning a new bug ID. If it persists, inspect document target authorization and
simulation/security time; keep all filtering and authorization DB-side.

## Critical financial checkpoint

January is **not reconciled**.

The database currently contains 11 active loans. Among the ten backfill target properties,
only P010 has its loan. Nine mortgages remain to be created.

January scheduled payment rows 53 through 56 are:

| Payment | Loan | Principal | Interest | Escrow | Total | Balance after | State |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 53 | 5 | 464.24 | 661.76 | 318.00 | 1,444.00 | 140,585.76 | Scheduled, unpaid |
| 54 | 6 | 475.39 | 674.61 | 318.00 | 1,468.00 | 145,649.61 | Scheduled, unpaid |
| 55 | 7 | 497.37 | 700.63 | 318.00 | 1,516.00 | 155,777.63 | Scheduled, unpaid |
| 56 | 8 | 508.03 | 713.97 | 318.00 | 1,540.00 | 160,841.97 | Scheduled, unpaid |

The scheduler incorrectly reduced `Loan.CurrentBalance` while merely creating these unpaid
rows. That must be repaired as part of `YS-154`.

Required `YS-154` behavior:

1. Add a supported web, mobile, and API action to post a scheduled loan payment.
2. Wrap the entire posting command in one explicit database transaction.
3. Make it idempotent with a business/idempotency key.
4. Change the live loan balance, cash/journal state, paid date, and payment status only when
   the payment becomes paid.
5. Keep scheduled rows as projections with no cash or balance mutation.
6. Keep all lookup, authorization, filtering, and reconciliation DB-side.
7. Add PostgreSQL failure-injection proof for all-or-nothing behavior.
8. Repair/recompute Loans 5-8 and Payments 53-56.
9. Live-prove the action on web and the physical phone.

`YS-156` must then allow each exact statement scan to match the correct existing loan and
scheduled payment rather than offering `Add Loan`.

The financial oracle's monthly `Amount Due` is principal and interest; the product
`TotalAmount` includes escrow. Reconciliation must explicitly compare the correct components
instead of reporting escrow as a variance.

## Exact continuation sequence

1. Confirm TSK-754 is still `Doing`; do not create a replacement task.
2. Confirm the database container and physical phone connection.
3. Start the API with simulation enabled and prove the frozen January 14 clock.
4. Start web and Flutter debug only; leave Engine stopped.
5. Reproduce the property-document 404 under the correctly configured stack.
6. Complete deeds and exact mortgage scans for P017, P019, P020, P022, and P023, alternating
   web/manual and phone/scan surfaces as the planner requires.
7. Move to January 15 and complete every field, ownership, deed, and mortgage for P025, P026,
   P028, and P029.
8. Append or correct the original planner run IDs in the execution ledger. Do not invent
   substitute runs and do not add duplicate rows.
9. Fix and prove `YS-154`, including the existing data repair.
10. Return to January 20, scan/match all 20 loan statements, test duplicate idempotency, and
    reconcile principal, interest, escrow, total cash, and loan balances to the oracle.
11. Deduplicate the execution ledger using evidence.
12. Resume after `RUN-20270121-04`; do not redo completed January 21 work without a specific
    regression reason.
13. Continue the remaining year in date order, alternating web/mobile and scan/manual paths,
    recording every bug and all financial effects.

## Faster operating model for the continuation

The previous run spent too much time serially switching from testing into coding, rebuilding,
restarting, redeploying, and then reconstructing the exact test context. Use this operating
model:

### Keep the tester moving

- The primary agent owns the calendar, exact scan selection, browser/phone sessions, execution
  ledger, bug ledger, and financial oracle.
- Use only one or two GPT-5.5 coding agents at a time for reproduced defects.
- Give each fixer an exact bug ID, reproduction, owned files, acceptance proof, atomicity and
  DB-side constraints, and an instruction not to revert other work.
- The tester continues with independent playbook runs while fixes are underway.
- Do not create a separate reviewer lane for every small fix. Use focused proof, then a
  checkpoint review for major lease, auth, money, or atomic-workflow changes.

### Keep the stack local and hot

- Local API, web, Engine-on-demand, and Flutter debug on the physical phone are the fastest
  feedback loop.
- Web uses Vite hot updates; Flutter debug uses hot reload.
- Batch related UI-only changes before one rebuild/restart.
- Keep critical auth, ledger, and transaction fixes isolated and prove each one before
  continuing.
- Use Azure only for a checkpoint, environment-specific behavior, or final verification.
  Rebuilding and moving every small change to Azure caused substantial avoidable delay.

### Prevent lost chronology

- Before advancing a simulation date, run a prerequisite check against `schedule.csv` and the
  database. The January 8-15 mortgage gap was discovered only after January 21 and forced this
  rollback.
- At the end of each simulated day, reconcile expected run IDs, scans, CRUD rows,
  notifications, and financial entries before moving the clock.
- Treat the Engine as opt-in. Start it for the exact worker run, wait for the expected
  idempotent result, then stop it.
- Take a recoverable database snapshot at month boundaries and before high-risk automation
  batches.

### Reduce repeated setup

- Add or use a small QA “doctor” command that verifies services, simulation flag and clock,
  database, fresh login, ADB device/reverse, scan corpus, and writable evidence paths.
- Keep stable role-specific QA accounts instead of repeatedly resetting passwords just to
  enter a surface.
- Pre-stage that day's exact scan files in a uniquely named phone folder so Android
  DocumentsUI does not require repeated ambiguous searches.
- Use API and PostgreSQL readback as verification after a real UI action, not as a substitute
  for the required UI action.
- Upsert ledger rows by `run_id` or check for an existing run ID before append. The 10 duplicate
  IDs now create avoidable reconciliation work.

### Build and resource discipline

- Never run more than two heavy builds/tests concurrently.
- Prefer focused PostgreSQL and UI tests; reuse build output.
- Set `MSBUILDDISABLENODEREUSE=1` and shut down the .NET build server after heavy batches.
- The agent that opens a browser owns closing its entire named process tree immediately.
- A fixer that creates a worktree owns removing it immediately after its branch is
  merged/pushed/abandoned; the primary agent verifies cleanup.

## Definition of done

TSK-754 is not complete until:

- Every planner run is executed through a real allowed role and surface.
- Every applicable screen and field has saved and read back on web and mobile.
- Scan and manual paths are both covered everywhere they apply.
- Every generated scan asset required by the planner is actually uploaded where specified.
- Every observed bug is recorded, with fixed bugs re-proved in the real browser/device.
- Notifications, reminders, messages, assignments, work orders, tenant portal, CRUD,
  lifecycle, and destructive/recovery paths are certified.
- Execution-ledger run IDs are unique and traceable to evidence.
- The product's year-end cash, receivables, deposits, income, expenses, debt service, loan
  balances, and journal/control totals reconcile to the external oracle with explained zero
  variance.
- The final checkpoint is run in the Azure verification environment and on a real mobile
  target.
- The branch is merged and TSK-754 is verified `Done`.

Until then, preserve the database, corpus, evidence, ignored ledgers, branch, and worktree.
