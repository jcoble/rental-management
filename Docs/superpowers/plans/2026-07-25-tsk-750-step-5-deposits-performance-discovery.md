# TSK-750 Step 5 — Deposits performance discovery brief

**Discovery verdict:** ROADMAP DISCOVERY READY  
**Active goal:** TSK-750 mobile UI rescue  
**Roadmap:** `Docs/superpowers/plans/2026-07-25-tsk-750-mobile-ui-rescue-execution.md`  
**Prior terminal checkpoint:** `/root/step4c_recovery`, `STEP 4C ACCEPTANCE PASS`,
no fixer  
**Planning retry:** 0 of 3

## Goal

Re-prove the unchanged Security Deposits request path on the exact deployed API,
source, and Android APK before authorizing any performance fix. The active
boundary is measurement-first proof only.

## Observable acceptance

1. **DEP-PERF-01 — Identity:** The proof records the exact local/source
   identity, deployed API identity, APK SHA-256, emulator
   `emulator-5554` identity, and the authenticated portfolio/request identity
   used for every sample. Resolve the API by Docker Compose labels
   `com.docker.compose.project=rc-preview-rental` and
   `com.docker.compose.service=api`; record `/health`, image ID/name, OCI
   revision, Compose working directory, and source SHA/tree. If source identity
   is unavailable, fail closed.
2. **DEP-PERF-02 — SQL contract:** The unchanged SQL-shape test passes. One
   authenticated deposits page GET produces exactly two sequential database
   statements: one DB-side count and one deterministic DB-side page query. Any
   401/token-refresh replay is recorded separately and is not combined with the
   authenticated 200 timing.
3. **DEP-PERF-03 — Query-plan evidence:** The proof contains the generated SQL
   and bound parameters for both statements plus PostgreSQL
   `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` output for each statement.
   These correlated statement timings and SQL artifacts come only from the
   isolated PostgreSQL test because the deployed API does not expose them.
4. **DEP-PERF-04 — Endpoint threshold:** Ten authenticated, UI-driven deposits
   endpoint samples are listed individually. Their p95 is no more than 2.0
   seconds and no sample exceeds 3.0 seconds.
5. **DEP-PERF-05 — Mobile threshold:** On the exact APK, the authorized
   Security Deposits screen reaches populated content within 3.0 seconds of the
   user action. A spinner-to-content screenshot and timestamp evidence are
   retained and identified in the proof.
6. **DEP-PERF-06 — Fail closed:** If exact identity, separated request timing,
   the two-statement trace, generated SQL/parameters, JSON plans, ten samples,
   or emulator timing cannot be obtained read-only, Step 5 is blocked. If a
   threshold fails, record the evidence and route to fresh discovery; do not
   guess or implement a fix.

## Explicit non-goals

- No production code, query, view, index, authorization, RLS, API, web, or
  mobile change.
- No client-side shaping, in-memory filtering/grouping/counting, N+1 repair,
  compatibility lane, migration, caching change, fallback, scaffolding,
  hardening, or speculative edge-case test.
- No protected stack/database reset, reseed, role change, or data mutation.
- No modification of the accepted Step 4C plan/proof or its uncommitted
  implementation WIP.
- No work on roadmap Steps 6 or 7.

## Discovery questions and resolved evidence

1. **Which request is measured?** The mobile deposits list uses one
   authenticated deposits page GET. Evidence: `S5-MOBILE-REQUEST`.
2. **Which server boundary must remain unchanged?** The tenant-account deposits
   query must retain a DB-side count followed by a deterministic DB-side page
   query. Evidence: `S5-API-QUERY`.
3. **Which focused automated proof applies?** Extend only
   `RentalCommand.IntegrationTests/SharedRequestPathPerformanceTests.cs` for
   measurement evidence and exercise unchanged
   `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs` for SQL
   translation/shape. Evidence: `S5-PG-HARNESS`.
4. **Why is this re-proof rather than a fix?** Earlier audit captures 32–34
   reported a roughly 17-second first page, while later evidence recorded ten
   Security Deposits route samples at 1.266–1.452 seconds and endpoint samples
   at 0.912–1.063 seconds. The current state therefore requires exact-identity
   measurement before any change. Evidence:
   `S5-AUDIT-32-34`, `S5-PRIOR-PERF`.

## Exact file map

- Modify:
  `RentalCommand.IntegrationTests/SharedRequestPathPerformanceTests.cs`
  — DEP-PERF-01 through DEP-PERF-04 and DEP-PERF-06.
- Exercise unchanged:
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`
  — DEP-PERF-02.
- Create:
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance.md`
  — DEP-PERF-01 through DEP-PERF-06.
- Create isolated-test evidence:
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/request-path.md`,
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/generated-sql-and-bound-parameters.md`,
  and
  `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/explain-analyze-buffers.json`
  — DEP-PERF-02 through DEP-PERF-04.
- Create real-emulator evidence:
  `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/spinner.png`,
  `content.png`, `window.xml`, `logcat-epoch.txt`, and
  `api-deposits-since-start.log`
  — DEP-PERF-01, DEP-PERF-04, and DEP-PERF-05.

No other product, test, configuration, proof, or planning path is authorized by
the execution boundary.

## Bounded commands

Run serially; no parallel build or test:

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

- The first command maps to DEP-PERF-02.
- The exact-method PostgreSQL command maps to DEP-PERF-02 through DEP-PERF-04
  and DEP-PERF-06. If the symbol or any named output is absent, stop.
- Build-server shutdown maps to the serial-heavy-verification boundary in
  DEP-PERF-06.
- The ADB clear/swipe/dump sequence maps to DEP-PERF-04 and DEP-PERF-05. Repeat
  the exact swipe ten times, waiting after each for one new
  `[HTTP Nms] GET /tenant-accounts/deposits/page ->` line with a 30-second
  fail-closed deadline.

Identity and emulator actions run under
`/srv/dev-stacks/.locks/azure-heavy.lock` after sourcing
`/opt/runner-tools/mobile-env.sh`, via `azureuser@100.126.201.65` and SSH key
`/Users/blackcolours/.ssh/rental-build-runner-01-key.pem`. Resolve the installed
APK only with `adb shell pm path com.rentalcommand.rental_command.dev`, pull it
into an exact `mktemp -d` directory, and record `sha256sum`, `dumpsys package`
versionCode, and versionName. Copy the named artifacts locally, validate the
exact temporary directory, then clean only it with
`find "$STEP5_REMOTE_TMP" -depth -delete`; never use `rm -rf`.

An authenticated curl loop is forbidden because the token exists only in
Flutter secure storage. Samples must be UI-driven. Capture sanitized API Docker
logs since the recorded UTC start, filtered to `deposits/page`, auth/refresh,
and `Request finished`.

## Stop condition

Discovery is complete because the exact request, test/proof files, serial
commands, real emulator target, thresholds, and fail-closed route are known.
The execution plan may contain one active numbered step: read-only re-proof.

## Evidence references

- `S4C-TERMINAL` — Step 4C terminal acceptance; protected prior WIP.
- `S5-ROADMAP` — approved TSK-750 Step 5 boundary.
- `S5-AUDIT-32-34` — original deposits delay evidence.
- `S5-MOBILE-REQUEST` — mobile request ownership and supported flow.
- `S5-API-QUERY` — count/page SQL boundary.
- `S5-PRIOR-PERF` — later sub-two-second performance evidence.
- `S5-PG-HARNESS` — focused PostgreSQL measurement harness and SQL-shape proof.
