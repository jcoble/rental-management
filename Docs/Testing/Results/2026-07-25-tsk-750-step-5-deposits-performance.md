# TSK-750 Step 5 deposits performance verdict

**Scope-implementer verdict:** `ISOLATED SQL EVIDENCE PASS`
**Pre-fix real-emulator verdict:** `UI DEFECT — STRICT PERFORMANCE FAIL`
**First fixed-build real-emulator verdict:** `MAJOR IMPROVEMENT — STRICT THRESHOLDS NARROWLY FAIL`
**Final fixed-build real-emulator verdict:** `UI PROOF PASS`
**Step 5 status:** `FIX DEPLOYED AND REPROVED — PERFORMANCE GATES PASS`
**Isolated capture time UTC:** `2026-07-25T14:32:40.9798290+00:00`
**Real-device capture start UTC:** `2026-07-25T20:41:45.255544115Z`
**Fixed-build reproof start UTC:** `2026-07-25T22:39:34.741341294Z`
**Final lateral-build reproof start UTC:** `2026-07-25T23:41:13.309363590Z`

The isolated PostgreSQL and translated-SQL acceptance remains accepted exactly
as recorded below. The original real-device flow failed both performance
thresholds, and the first fixed build narrowly missed them. A second query
rewrite then constrained the two display-view lookups to each already-paged
deposit row. On the final real-emulator reproof, cold content was populated by
3 seconds, all ten exact refreshes passed the 2-second p95 and 3-second maximum
limits, and no authentication replay occurred.

## Final lateral-query remediation and proof

The first seed-page fix still allowed the lifecycle and balance display joins
to cross-expand the 20 paged rows. Its fixed live page plan took about
`596 ms`, performed `57,291` buffer hits, and included `8,000` inner probes.
The final query correlates each display lookup to one already-paged deposit
seed. Npgsql translates those two lookups as `JOIN LATERAL`; authorization,
filtering, stable sorting, paging, and the separate count remain DB-side.

Final source and verification:

- commit: `5e437dc2fe1d9a127f38e77bc512a359acb1d04f`;
- `TenantAccountQueryServiceSqlTests`: `19/19` passed;
- focused PostgreSQL request-path integration: `1/1` passed;
- isolated authenticated request: `128.876 ms`;
- isolated count statement: `28.382 ms`;
- isolated page statement: `17.420 ms`;
- generated SQL contains exactly two `JOIN LATERAL` display lookups;
- independent narrow source review: `REVIEW PASS`, no blocking authorization,
  translation, filtering, ordering, paging, or count finding.

The exact source was built and deployed to the reusable Azure stack. Public
`/health` returned HTTP 200 with `{"status":"ok"}`. The API container was
healthy on image
`sha256:96141b8d017b3dfc16e311d6ff5ed6ac3d8de2ae459c790cb2b2f2daf32e8a4e`.
The export has no Git metadata and the image has no revision label, so the
commit remains controller-supplied deployment provenance.

Final real-emulator results:

- cold request: app `629 ms`; API `524.5763 ms`;
- screenshot capture began `2.776 seconds` after the cold navigation gesture
  and shows populated deposits;
- refresh app durations: `1,514`, `406`, `483`, `438`, `369`, `436`, `415`,
  `424`, `379`, and `397 ms`;
- app nearest-rank p95 and maximum: `1,514 ms`;
- refresh API durations: `344.2173`, `382.2082`, `402.9128`, `358.0363`,
  `354.4832`, `417.3813`, `342.3681`, `401.9274`, `334.8470`, and
  `347.0288 ms`;
- API nearest-rank p95 and maximum: `417.3813 ms`;
- HTTP 401 count: `0`;
- token-refresh/replay markers: `0`.

Final raw proof:

- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-lateral-5e437dc2/README.md`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-lateral-5e437dc2/loading.png`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-lateral-5e437dc2/cold-3s.png`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-lateral-5e437dc2/refresh-samples.txt`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-lateral-5e437dc2/api-deposits-refresh-finished.log`

## Current remediation checkpoint

The failure was reproduced again after restoring the emulator's documented
seeded-admin session:

- initial Deposits request: HTTP 200 in `16,524 ms`;
- exactly one pull-to-refresh: HTTP 200 in `15,935 ms`;
- no 401, token-refresh replay, unauthorized response, lock wait, or I/O wait;
- exact PostgreSQL statement hash:
  `3bad46dfb3908724a198b1b3e4502c77`;
- exact statement remained CPU-active for at least `15.418 seconds`.

The complete captured statement reproduced under `rentalcommand_api` in
`10,161.361 ms`, with `442,080` shared-buffer hits. Its nested-loop display
joins expanded before the first 20 authorized deposit IDs were paged.

The current worktree now pages the authorized deposit seed rows by
`CreatedAtUtc` and unique deposit ID before joining lifecycle, balance,
property, and unit display data. Authorization, filtering, deterministic
sorting, and paging remain in one translated PostgreSQL page statement; the
separate DB-side count keeps the request at two statements total.

Verification of the corrected shape:

- SQL-shape regression: failed before the change and passes afterward;
- `TenantAccountQueryServiceSqlTests`: `19/19` passed;
- focused PostgreSQL request-path integration: `1/1` passed;
- isolated fixed request: `157.331 ms`;
- isolated fixed page statement: `29.223 ms`;
- live Azure fixed-shape plan under `rentalcommand_api`: `600.555 ms`;
- live shared-buffer hits: `57,291`, about `87%` below the reproduced slow
  plan;
- the `LIMIT 20` node is before both
  `vw_lease_management_lifecycle` and `vw_security_deposit_balances`.
- independent narrow source review found no blocking correctness,
  authorization, deterministic-ordering, paging, or SQL-translation defect;
- the web Deposits grid now explicitly defaults to `-createdAtUtc`, matching
  mobile and ensuring its unfiltered first page also uses the optimized shape.

The corrected query was committed as
`e95b1435944375a44a0ade3ad23db216e3de703b`, built from that clean source, and
deployed to the reusable Azure verification stack. The fixed API remained
healthy throughout the strict emulator rerun. All ten exact refreshes returned
200 with no 401, refresh-token replay, unauthorized response, or timeout.

Fixed-build refresh results:

- app-observed durations: `2,130`, `1,285`, `1,198`, `1,202`, `1,207`,
  `1,177`, `1,175`, `1,083`, `1,200`, and `1,119 ms`;
- app nearest-rank p95 and maximum: `2,130 ms`;
- API durations: `1,141.4660`, `1,139.7671`, `1,153.1857`, `1,180.2943`,
  `1,060.5332`, `1,140.4078`, `1,137.2125`, `1,060.6283`, `1,162.5512`,
  and `1,079.7278 ms`;
- API nearest-rank p95 and maximum: `1,180.2943 ms`;
- no sample exceeded the `3,000 ms` hard maximum.

The former 15–30 second defect is gone, but DEP-PERF-04 remains a strict fail:
the contract measures the app-observed nearest-rank p95, and `2,130 ms` is
`130 ms` above its `2,000 ms` limit.

The fixed cold selection began at `2026-07-25T22:39:34.741341294Z`. The app
reported the deposits request as 200 in `2,270 ms`, while the API itself
reported `1,157.5027 ms`. A screenshot started about `2,591 ms` after the
selection gesture and still captured the contextual loader; the populated
render could not be strictly proved by the `3,000 ms` user-action deadline.
DEP-PERF-05 therefore also remains a narrow strict fail. The immediate
`spinner.png` attempt fired before Deposits painted and shows Portfolio, so it
is explicitly rejected rather than used as loader evidence.

New direct evidence:

- `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/live-slow-page-explain.json`
- `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/live-fixed-page-explain.json`
- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/pre-fix-initial-loader.png`
- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/pre-fix-refresh-final.png`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-fixed-e95b143/refresh-samples.txt`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-fixed-e95b143/api-deposits-since-start.log`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-fixed-e95b143/content.png`
- `/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-fixed-e95b143/window.xml`

## Acceptance verdicts

| Item | Verdict | Evidence |
|---|---|---|
| DEP-PERF-01 | PASS WITH PROVENANCE LIMITATION | Controller deployment metadata records exact final commit `5e437dc2fe1d9a127f38e77bc512a359acb1d04f`, and `/health` returned `{"status":"ok"}`. The exported source has no `.git` directory and the image has no revision label. The final API image is `sha256:96141b8d017b3dfc16e311d6ff5ed6ac3d8de2ae459c790cb2b2f2daf32e8a4e`. |
| DEP-PERF-02 | PASS | `TenantAccountQueryServiceSqlTests` passed 19/19. The focused PostgreSQL request-path integration passed 1/1 and captured exactly two sequential statements: one DB-side count and one deterministically ordered/paged DB-side query. The final page SQL contains exactly two correlated `JOIN LATERAL` lookups. |
| DEP-PERF-03 | PASS | The authenticated 200 actions have separate app/API timing, no observed 401 replay, complete generated SQL and bound parameters, and valid slow/fixed `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` plans. |
| DEP-PERF-04 | PASS | All ten final-build refreshes completed with no timeout. App-observed nearest-rank p95/max was 1.514 seconds, below both the 2.0-second p95 and 3.0-second maximum limits. API p95/max was 417.3813 ms. |
| DEP-PERF-05 | PASS | The final cold request reported 200 in 629 ms in-app and 524.5763 ms at the API. A screenshot begun 2.776 seconds after the navigation gesture shows populated deposits, proving content by the 3.0-second deadline. |
| DEP-PERF-06 | PASS | The measurement-only boundary was preserved. No production, query, authorization, view, index, API, web, mobile, database, or protected-stack data mutation was made by the verifier. |

## Real-emulator strict failure

Verification ran serially under:

```text
/srv/dev-stacks/.locks/azure-heavy.lock
```

Target and identity:

- Device: `emulator-5554`, `sdk_gphone64_x86_64`, 1080 × 2400, 420 dpi.
- Package/activity:
  `com.rentalcommand.rental_command.dev/com.rentalcommand.rental_command.MainActivity`.
- API: `https://rental-command.chimp-map.ts.net/api/v1`.
- API container: `/rc-preview-rental-api-1`.
- API image: `rc-preview-api:rental`,
  `sha256:25b17b8d40398335c5aef8d583b3308047107c90e67e085088526032fb3a2801`.
- Installed APK SHA-256:
  `b086147ab5678af4ce57d0a11ab8459280766f8c46d527a1ac954ca293b0cca0`.
- App version: `1.0.0 (1)`.
- No 401 response or token-refresh replay was observed.

Measured outcome:

```text
Cold open: 16,579 ms
Exact swipe sample 1: 15,557 ms
Exact swipe sample 2: >30,000 ms, fail-closed timeout
Initial-content deadline: still loading at 3,000 ms
```

Required artifacts:

- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/spinner.png`
- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/content.png`
- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/window.xml`
- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/logcat-epoch.txt`
- `Docs/Reviews/artifacts/tsk-750/step5-deposits-performance/api-deposits-since-start.log`

## Fixed-build real-emulator reproof

Target and identity:

- API commit:
  `e95b1435944375a44a0ade3ad23db216e3de703b`.
- API image:
  `sha256:47f74027494cdca772b4d981c9a5166d44cf84d778c8da53dcdce89d7ccc5003`.
- Installed APK SHA-256:
  `c1daa93ac352072fecbc61418c6b0196900e0298af89f5b05649ec41bad9a866`.
- Cold request: app `2,270 ms`; API `1,157.5027 ms`.
- Refresh app nearest-rank p95/max: `2,130 ms`.
- Refresh API nearest-rank p95/max: `1,180.2943 ms`.
- Authentication: no 401, unauthorized response, token refresh, replay, or
  timeout across the cold request and ten refreshes.

The fixed-build content PNG, UI XML, refresh samples, API log, and logcat are
under:

```text
/Users/blackcolours/.codex/remote-artifacts/tsk750-step5-fixed-e95b143/
```

The candidate `spinner.png` in that folder is rejected because it shows
Portfolio before Deposits painted.

## Targeted verification

Accepted terminal replacement evidence:

```text
TenantAccountQueryServiceSqlTests: 18/18 passed
Unchanged PostgreSQL acceptance: 5/5 passed
```

Scope-implementer command:

```bash
FOUNDATION_PERF_EVIDENCE_OUTPUT="$STEP5_RESULTS/request-path.md" \
FOUNDATION_PERF_SQL_OUTPUT="$STEP5_RESULTS/generated-sql-and-bound-parameters.md" \
FOUNDATION_PERF_EXPLAIN_OUTPUT="$STEP5_RESULTS/explain-analyze-buffers.json" \
MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter 'FullyQualifiedName=RentalCommand.IntegrationTests.SharedRequestPathPerformanceTests.TenantAccountDeposits_FirstPage_RecordsSeparatedCountPageSqlAndPlans'
```

Result:

```text
Passed: 1, Failed: 0, Skipped: 0
Process exit code: 0
```

The method was rerun after correcting bound-array evidence formatting; the
second passing capture is authoritative.

## Isolated request and statement evidence

- Authenticated action:
  `GET /api/v1/tenant-accounts/deposits/page?skip=0&take=20`
- Result: `200`
- Request/action duration: `178.930 ms`
- 401/token-refresh replay: none observed; no replay duration was combined with
  the authenticated 200 action.
- Statement 1: DB-side `count(*)`, `49.359 ms`.
- Statement 2: deterministic DB-side page with `ORDER BY`, `LIMIT`, and
  `OFFSET`, `22.126 ms`.
- Sequential proof: statement 1 completed at
  `2026-07-25T14:32:41.1077200+00:00`; statement 2 began at
  `2026-07-25T14:32:41.1355520+00:00`.
- Client evaluation/materialize-then-shape/lazy load/N+1/per-row queries:
  none; only the two translated PostgreSQL statements were observed.

Direct artifacts:

- `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/request-path.md`
- `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/generated-sql-and-bound-parameters.md`
- `Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/explain-analyze-buffers.json`

The SQL artifact records every bound parameter, including
`[money.deposits.manage, leasing.deposits.read]`. The JSON artifact contains two
plans:

| Sequence | Shape | Captured duration | Top plan node | Planning time | Execution time |
|---:|---|---:|---|---:|---:|
| 1 | count | 49.359 ms | Aggregate | 8.006 ms | 0.120 ms |
| 2 | page | 22.126 ms | Limit | 10.242 ms | 0.451 ms |

## Final disposition

The earlier protected-stack outage no longer blocks proof. The final lateral
query removed the remaining display-view cross expansion after pagination.
On commit `5e437dc2`, the cold deposits request completed in `629 ms` in-app,
populated content was visible by `2.776 seconds`, and all ten refreshes
completed between `369 ms` and `1.514 seconds` in-app. No 401 or token replay
occurred. DEP-PERF-01 through DEP-PERF-06 now pass, subject only to the recorded
deployment-provenance limitation.
