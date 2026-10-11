# TSK-750 Step 5 — Deposits performance execution contract

**Goal:** Re-prove the unchanged Security Deposits request path before
authorizing any performance change.  
**Source brief:**
`Docs/superpowers/plans/2026-07-25-tsk-750-step-5-deposits-performance-discovery.md`  
**Active goal:** TSK-750 mobile UI rescue  
**Active step:** Step 5 — Deposits performance read-only re-proof  
**Plan state:** Ready for execution  
**Planning retry:** 0 of 3

## Authority and preserved checkpoint

- Roadmap:
  `Docs/superpowers/plans/2026-07-25-tsk-750-mobile-ui-rescue-execution.md`.
- Prior terminal packet: `/root/step4c_recovery`, terminal status
  `STEP 4C ACCEPTANCE PASS`, no fixer.
- Preserve the accepted Step 4C plan/proof, Step 4A/4B proof artifacts, and all
  existing uncommitted implementation WIP. This contract does not amend or
  validate those boundaries.

## Contract acceptance

1. **DEP-PERF-01 — Exact identity:** The result names the exact source and
   deployed API identity, API image/deployment identity, APK SHA-256, emulator
   `emulator-5554`, authenticated portfolio, request URL, and capture time used
   for the measurements. All ten samples and the mobile proof use that same
   identity.
2. **DEP-PERF-02 — Two translated statements:** The unchanged
   `TenantAccountQueryServiceSqlTests` focused suite passes. A single
   authenticated deposits page GET emits exactly two sequential PostgreSQL
   statements: one DB-side count and one deterministic DB-side page query.
   There is no client evaluation, materialize-then-shape behavior, lazy load,
   N+1, or per-row query.
3. **DEP-PERF-03 — Separated trace and plans:** Any 401/token-refresh replay is
   listed separately from the authenticated 200 request and excluded from its
   duration. For the authenticated request, the result includes request/API
   timing, statement timing, generated SQL, bound parameters, and
   `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` output for both statements.
4. **DEP-PERF-04 — Endpoint performance:** Ten authenticated, UI-driven deposits
   endpoint samples are reported individually; p95 is at most 2.0 seconds and
   no sample is over 3.0 seconds.
5. **DEP-PERF-05 — Real mobile proof:** The exact APK on Azure Android emulator
   `emulator-5554` reaches populated Security Deposits content within 3.0
   seconds of the user action. The proof identifies the spinner-to-content
   screenshot and includes the corresponding ADB threadtime evidence.
6. **DEP-PERF-06 — Measurement-only disposition:** Only
   `RentalCommand.IntegrationTests/SharedRequestPathPerformanceTests.cs` and the
   nine named result/proof artifacts may change;
   `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs` is
   exercised unchanged. Exact identity and every timing/SQL artifact are
   obtained read-only. If evidence is unavailable or any threshold fails, the
   result records the failure and Step 5 stops for fresh discovery with no
   production change.

## Explicit non-goals

- No production code/query/view/index/auth/RLS/API/web/mobile change.
- No query redesign, caching change, client shaping, in-memory
  grouping/filtering/counting, N+1 repair, migration, fallback, compatibility
  work, broad test, hardening, or hypothetical edge-case requirement.
- No protected database/stack reset, reseed, role edit, test-data mutation, or
  write-flow exercise.
- No Step 4 plan/proof/WIP edit and no Step 6 or Step 7 work.
- A passing route render without exact identity, database trace, ten-sample
  timing, and real emulator timing is not acceptance.

## Supported flow and proof target

- **UI impact:** Yes — this step validates the user-visible deposits loading
  delay without changing the UI.
- **Supported flow:** An already-authorized landlord opens Money, selects
  Security Deposits, and waits for the existing first page to replace its
  loading state with populated deposit rows. The associated authenticated
  deposits page GET is measured; a token-refresh 401, if present, is a separate
  event.
- **Required target:** The protected Rental Command preview API and the exact
  installed APK on Azure Android emulator `emulator-5554`, 1080 × 2400 at
  420 dpi.
- **Verdict report:** Create
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance.md`.
  It must contain or identify immutable captures for the APK/API/source
  identity, raw ten-sample table, request and statement durations, generated
  SQL and parameters, both JSON plans, ADB timestamps, and the
  spinner-to-populated-content screenshot.

## Step 5 — Read-only deposits performance re-proof

**Status:** Active  
**Acceptance:** DEP-PERF-01 through DEP-PERF-06

### Allowed files

- Modify:
  `RentalCommand.IntegrationTests/SharedRequestPathPerformanceTests.cs`
  — DEP-PERF-01, DEP-PERF-03, DEP-PERF-04, DEP-PERF-06.
- Test unchanged:
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`
  — DEP-PERF-02.
- Create:
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance.md`
  — DEP-PERF-01 through DEP-PERF-06.
- Create:
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/request-path.md`
  — DEP-PERF-02 through DEP-PERF-04.
- Create:
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/generated-sql-and-bound-parameters.md`
  — DEP-PERF-02 and DEP-PERF-03.
- Create:
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/explain-analyze-buffers.json`
  — DEP-PERF-03.
- Create:
  `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/spinner.png`,
  `content.png`, `window.xml`, `logcat-epoch.txt`, and
  `api-deposits-since-start.log`
  — DEP-PERF-01, DEP-PERF-04, and DEP-PERF-05.

No other file may be created or modified.

### Required actions

1. Record the exact local/source, deployed API/image, installed APK SHA-256,
   emulator, authenticated portfolio, request URL, and capture-time identity.
   If those identities cannot be obtained read-only and shown to match for the
   proof, return `BLOCKED` under DEP-PERF-01 and DEP-PERF-06. Resolve the API
   using only Compose labels `com.docker.compose.project=rc-preview-rental` and
   `com.docker.compose.service=api`; record `/health`, image ID/name, OCI
   revision, Compose working directory, and its source SHA/tree. If its working
   directory has no usable `.git` identity, record `UNAVAILABLE` and stop.
2. Extend only `SharedRequestPathPerformanceTests.cs` so the discovered
   PostgreSQL harness captures the single authenticated deposits GET, separates
   any 401 replay, asserts exactly two sequential statements, records request
   and statement timings, generated SQL and parameters, both JSON-format
   analyzed/buffered plans, and ten individual endpoint samples. Do not alter
   the production query or authorization path.
3. Exercise the unchanged SQL-shape suite and require its translated count/page
   assertions to pass. A correct value computed after materialization is a
   contract failure.
4. Under the remote heavy lock, drive the supported flow ten times against the
   same exact identity with the exact pull-to-refresh swipe. After each swipe,
   wait for one new
   `[HTTP Nms] GET /tenant-accounts/deposits/page ->` line with a 30-second
   fail-closed deadline. Retain raw samples and calculate p95; require p95 at
   most 2.0 seconds and every sample at most 3.0 seconds. An authenticated curl
   loop is forbidden because the token exists only in Flutter secure storage.
5. Clear ADB logcat immediately before the first sample, record UTC start, open Security
   Deposits in the installed app, capture the loading-to-populated transition,
   then dump epoch/threadtime logs and the UI Automator hierarchy. Capture
   sanitized API Docker logs since UTC start, filtered to `deposits/page`,
   auth/refresh, and `Request finished`. Require populated content within 3.0
   seconds.
6. Write the verdict report with a DEP-PERF-01 through DEP-PERF-06 verdict
   table and direct evidence for every item. If any item fails or cannot be
   evidenced, stop and route to fresh Step 5 discovery. Do not propose or
   implement a likely fix.

### Targeted commands

Run serially. Do not run any heavy build or test in parallel.

```bash
STEP5_ROOT=/Users/blackcolours/dev/work/worktrees/rental-management/tsk-750-mobile-ui-rescue
STEP5_RESULTS=$STEP5_ROOT/Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance
STEP5_UI=$STEP5_ROOT/Docs/Reviews/artifacts/tsk-750/step5-deposits-performance
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
  --filter 'FullyQualifiedName~TenantAccountQueryServiceSqlTests'
FOUNDATION_PERF_EVIDENCE_OUTPUT="$STEP5_RESULTS/request-path.md" \
FOUNDATION_PERF_SQL_OUTPUT="$STEP5_RESULTS/generated-sql-and-bound-parameters.md" \
FOUNDATION_PERF_EXPLAIN_OUTPUT="$STEP5_RESULTS/explain-analyze-buffers.json" \
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter 'FullyQualifiedName=RentalCommand.IntegrationTests.SharedRequestPathPerformanceTests.TenantAccountDeposits_FirstPage_RecordsSeparatedCountPageSqlAndPlans'
dotnet build-server shutdown
adb -s emulator-5554 logcat -c
adb -s emulator-5554 shell input swipe 540 760 540 1580 500
adb -s emulator-5554 logcat -d -v threadtime
```

- Command 1 proves DEP-PERF-02.
- The exact-method PostgreSQL command proves the automated portion of
  DEP-PERF-01 through DEP-PERF-04 and DEP-PERF-06.
- Build-server shutdown maps to the serial-heavy-verification boundary in
  DEP-PERF-06.
- The ADB clear/swipe/dump sequence proves the UI-driven sample and timestamp
  portions of DEP-PERF-04 and DEP-PERF-05. Repeat the exact swipe ten times.
  The screenshot and populated-content observation are
  required real-device evidence; static checks cannot replace them.

Identity and emulator proof run under
`/srv/dev-stacks/.locks/azure-heavy.lock` after sourcing
`/opt/runner-tools/mobile-env.sh`, over `azureuser@100.126.201.65` using
`/Users/blackcolours/.ssh/rental-build-runner-01-key.pem`. Resolve the installed
APK only with `adb shell pm path com.rentalcommand.rental_command.dev`; pull it
to an exact `mktemp -d` directory and record `sha256sum`, `dumpsys package`
versionCode, and versionName. Copy the five named UI artifacts locally, validate
the exact temporary directory, then clean only it with
`find "$STEP5_REMOTE_TMP" -depth -delete`. Never use broad cleanup or `rm -rf`.

The deployed API cannot provide request-correlated auth/count/page timings or
SQL. Those separated timings, generated SQL/parameters, and JSON plans must come
only from the isolated PostgreSQL test. If the exact test symbol or any named
output is absent, return `BLOCKED`; do not substitute deployed logs or guess a
fix.

### Relevance gate

After the step, run a read-only `relevance-reviewer` against only:

- `RentalCommand.IntegrationTests/SharedRequestPathPerformanceTests.cs` and the
  nine named result/proof artifacts as the only changed paths, with
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`
  exercised unchanged;
- DEP-PERF-01 through DEP-PERF-06;
- the exact-identity chain;
- the separated 401/authenticated request evidence;
- the two-statement DB-side SQL shape and plans;
- the ten endpoint samples and real-emulator threshold.

Require `RELEVANCE PASS`. Any production/query/auth/mobile change or unrelated
test/proof edit is a blocker. A possible optimization unsupported by the
measurement is deferred, not implemented.

### Real-emulator proof gate

After relevance passes, a real-device verifier repeats the declared flow on
`emulator-5554` and requires:

- exact APK/API/source identity;
- spinner-to-populated screenshot evidence;
- ADB threadtime evidence;
- populated Security Deposits content within 3.0 seconds.

Require `UI PROOF PASS`. Static analysis, route rendering, API-only timing, or
an emulator running a different APK/API/source identity cannot replace this
gate.

## Completion gate

- [ ] Each changed file is one of the allowed paths and maps to a numbered
      acceptance item.
- [ ] Both focused test commands pass serially.
- [ ] Exactly two sequential DB-side statements, generated SQL/parameters, and
      both JSON plans are evidenced.
- [ ] All ten authenticated endpoint samples meet DEP-PERF-04.
- [ ] The exact-identity emulator flow meets DEP-PERF-05.
- [ ] `RELEVANCE PASS` and `UI PROOF PASS` are fresh.
- [ ] No production change or deferred boundary was absorbed.
- [ ] If any item failed, the result says `BLOCKED`, preserves the evidence, and
      routes to fresh discovery without guessing a fix.

## Deferred roadmap boundaries

- Step 6 — Portfolio rent consistency.
- Step 7 — contextual loading and dense-list polish.
- Any query, authorization, view, index, caching, API, or mobile change suggested
  by failed measurement; it requires a fresh discovery packet and contract.
