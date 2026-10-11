# TSK-750 Step 7 — Contextual loading and dense lists execution contract

**Goal:** Replace audited blank waits with contextual loading, retain compact
append feedback, and expose only the audited decisive row text at phone width.
**Source brief:**
`Docs/superpowers/plans/2026-07-25-tsk-750-step-7-contextual-loading-dense-lists-discovery.md`
**Active goal:** TSK-750 mobile UI rescue
**Active step:** Step 7 — contextual loading and dense-list polish
**Plan state:** Implementation complete; one optional real-device branch remains fixture-limited
**Planning retry:** 0 of 3

## Preserved checkpoints

The protected API was previously absent, with `/health` returning `HTTP/2 502`
at `2026-07-25 16:05:40 GMT`. It was later restored: `/health` returned HTTP
200, and exact runtime metadata identified SHA `51c2f88b`.

Step 7 Tasks 7A, 7B, and 7C each received `RELEVANCE PASS`. Automated
acceptance now passes 45/45, and targeted analyze returns exactly
`No issues found!`. Real-device proof then found the TSK-752 work-order defect;
the defect was fixed, work-order proof returned `UI PROOF PASS`, and TSK-752 is
`Done and verified`.

A later artifact-content audit rejected the original
`inspections-loading`, `activity-loading`, and `ledger-loading` pairs because
they showed final populated lists rather than in-flight placeholders. It also
rejected `ledger-append-row-wrapped` as append proof because no append progress
was visible and the long title was ellipsized.

Fresh fixed-build proof now covers the work-order, inspections, and deposits
contextual loaders; activity append with retained rows and compact progress;
and inspection/activity/lease row wrapping. The first Activity rerun exposed a
real empty-state flash while `/audit` was pending. Commit `f5c8ae7c` fixes that
first-frame state, passes the expanded 45-test suite, and was installed
in-place with preserved authentication. A final bounded capture held the API
request pending for 5.309 seconds and produced a matched PNG/XML pair containing
`Loading activity` and `Recent changes and who made them`; populated activity
returned after the API resumed.

Ledger initial loading is likewise partial: its PNG shows `Loading ledger`,
but its XML contains the completed rows. The first Ledger append capture
proved server paging and retained rows but exposed that the footer spinner was
built whenever `hasMore` was true, even while idle. Commit `ef3f7d23`
restricts the footer to `loadingMore` and adds a failing-before/passing-after
regression. Exact-APK emulator reproof returned `UI PROOF PASS`: settled rows
have no progress, `skip=40` and `skip=80` fire naturally, active paging retains
rows and shows compact progress, and appended rows remain scrollable.
Deposits append is not reachable in the preserved fixture because both
legitimate sample portfolios expose exactly 20 rows and the page size is 20.
The pending/disabled/restored action and server-owned skip/take behavior pass
widget and repository tests; no page-size change or fake data was introduced
only to display the control. Ledger's initial-loader PNG/XML pairing remains
the sole optional device-artifact gap.

Step 5's pre-fix deployed baseline remains `FAIL`: cold load took 16.579
seconds, first refresh took 15.557 seconds, the next valid sample exceeded 30
seconds and failed closed, and no request returned 401. The implemented
page-before-display-join query shape passed its focused SQL and PostgreSQL
tests; its live-data plan completed in 600.555 ms with about 87% fewer
shared-buffer hits, and independent source review found no blocking issue. The
first fixed API at commit `e95b1435` removed the former 15–30 second behavior
but narrowly missed the strict app p95 and cold-content gates. Commit
`5e437dc2` then constrained both display-view lookups to each paged deposit
with correlated `JOIN LATERAL` queries. Exact emulator reproof returned
`UI PROOF PASS`: cold content was populated by 2.776 seconds, app refresh
p95/max was 1.514 seconds, API p95/max was 417.3813 ms, and no 401, replay, or
timeout occurred. Step 5 is complete subject only to its recorded deployment
provenance limitation. Step 6 returned `UI PROOF PASS` with `Collection Rate` `74.3%` and
`$15,000.00 of $20,175.00`, plus `Signed lease rent` `$1,050.00` and
`currently governing`. The parent roadmap currently ends at Step 7.

## Contract acceptance

1. **CONTEXT-01 — Contextual pending states:** Pending work-order, inspection,
   activity, ledger, and deposit requests show recognizable feature-local
   Material 3 structure rather than a blank field around one centered spinner.
2. **CONTEXT-02 — Existing chrome preserved:** Existing search, filter, header,
   error, empty, refresh, and custom list components remain intact.
3. **CONTEXT-03 — Compact automatic append:** Activity and ledger append
   requests retain loaded rows and show compact list-end progress.
4. **CONTEXT-04 — Explicit deposit append:** Deposit `Load more` remains
   explicit, is disabled while pending, shows compact progress inside the
   action, and restores the same count label after completion.
5. **CONTEXT-05 — Server paging preserved:** Existing tests continue proving
   server `skip/take`, search, sort, filters, stale-response handling, and
   append behavior. No client filtering, sorting, grouping, aggregation, or
   reconciliation is introduced.
6. **CONTEXT-06 — Local second-line identity:** At 1080 × 2400, inspection
   property/unit, activity description/entity, ledger source/date, and lease
   tenant/relationship identity may use a second line without overflow or
   clipped trailing controls.
7. **CONTEXT-07 — Mandatory Android proof:** Fresh PNG and UI XML prove audit
   Evidence 05, 13, 24, 29–34, and 55. Protected API absence or HTTP 502 blocks
   only this real-device gate; static/widget checks do not waive it.

## Explicit non-goals

- No protected API restoration/deployment, Azure work, database change,
  provider, repository, controller, query, authorization, RLS, data-shape, or
  client-side shaping change.
- No edit to `mobile_m3_list.dart`, `mobile_grid_controls.dart`,
  `MobileM3ListItem`, or `MobileGridPagingBar`.
- No global loading component, new design system, broad spinner sweep, rebrand,
  row-density overhaul, expense correction, internal-name cleanup, or
  unaudited audit finding.
- No Step 5 or Step 6 change or proof waiver.

## Supported flows and proof target

- **UI impact:** Yes — loading, append feedback, and selected row text change.
- **Required target:** Existing authenticated Android management experience at
  1080 × 2400 against the protected Rental Command API.
- **Flows:** Today → work-order detail; Work → Inspections; Inbox → Activity;
  Money → Ledger; Money → Deposits; Rentals → Leases.
- **Known blocker:** If no protected API container exists or `/health` returns
  502, return `UI PROOF BLOCKED`; do not restore/deploy the stack or substitute
  static proof.
- **Proof files:** Only the twenty PNG/XML files named in the real-device gate
  below.

## Step 7 — Contextual loading and dense-list polish

**Status:** Incomplete — partial real-device proof; remaining branches unproved
or `FIXTURE BLOCKED`
**Acceptance:** CONTEXT-01 through CONTEXT-07

### Global allowed paths for this step

Modify only:

- `mobile/lib/features/maintenance/work_order_detail_screen.dart`.
- `mobile/lib/features/inspections/inspections_list_screen.dart`.
- `mobile/lib/features/activity/activity_history_screen.dart`.
- `mobile/lib/features/money/money_screen.dart`.
- `mobile/lib/features/deposits/deposits_screen.dart`.
- `mobile/lib/features/leases/leases_list_screen.dart`.
- `mobile/test/work_order_detail_edit_tabs_test.dart`.
- `mobile/test/activity_history_mobile_test.dart`.
- `mobile/test/transactions_controller_test.dart`.
- `mobile/test/deposits_repository_test.dart`.

Create only:

- `mobile/test/contextual_loading_dense_rows_test.dart`.
- The twenty exact real-device proof files named below.

Exercise unchanged:

- `mobile/test/mobile_list_grid_server_query_test.dart`.
- `mobile/test/grid_period_query_params_test.dart`.

No other file may be created or modified.

### Task 7A — Contextual initial states

**Acceptance:** CONTEXT-01, CONTEXT-02, CONTEXT-05

Allowed task paths:

- `mobile/lib/features/maintenance/work_order_detail_screen.dart`.
- `mobile/lib/features/inspections/inspections_list_screen.dart`.
- `mobile/lib/features/activity/activity_history_screen.dart`.
- `mobile/lib/features/money/money_screen.dart`.
- `mobile/lib/features/deposits/deposits_screen.dart`.
- `mobile/test/work_order_detail_edit_tabs_test.dart`.
- `mobile/test/contextual_loading_dense_rows_test.dart`.

Required actions:

1. Replace only each audited blank initial body with feature-local Material 3
   placeholders composed from existing cards, `MobileM3ListItem`, theme colors,
   icons, and compact progress.
2. Preserve work-order app bar; inspection/deposit search, sort, and filter
   chrome; and every existing error, empty, refresh, and list component.
3. Do not alter providers, repositories, controllers, request parameters, or
   paging controls.
4. Extend the named tests for pending work-order detail and inspection
   placeholder behavior.
5. Run the targeted test/analyze commands, then run a read-only relevance review
   against only Task 7A paths and acceptance. Require `RELEVANCE PASS` before
   Task 7B.

### Task 7B — Compact append and paging feedback

**Acceptance:** CONTEXT-03, CONTEXT-04, CONTEXT-05

Allowed task paths:

- `mobile/lib/features/activity/activity_history_screen.dart`.
- `mobile/lib/features/money/money_screen.dart`.
- `mobile/lib/features/deposits/deposits_screen.dart`.
- `mobile/test/activity_history_mobile_test.dart`.
- `mobile/test/transactions_controller_test.dart`.
- `mobile/test/deposits_repository_test.dart`.

Required actions:

1. Retain activity and ledger rows during append and constrain their existing
   list-end progress indicators to compact inline dimensions.
2. Add screen-local pending state around the existing deposits notifier
   `loadMore` call. Disable the existing action while pending, place compact
   progress inside it, and restore its unchanged count label afterward.
3. Preserve server paging, filters, offsets, stale-response handling, and append
   semantics. Leave inspection and lease `MobileGridPagingBar` controls
   explicit and unchanged.
4. Extend only the three named tests for compact feedback and retained paging.
5. Run the targeted test/analyze commands, then run a read-only relevance review
   against only Task 7B paths and acceptance. Require `RELEVANCE PASS` before
   Task 7C.

### Task 7C — Local decisive row wrapping

**Acceptance:** CONTEXT-05, CONTEXT-06

Allowed task paths:

- `mobile/lib/features/inspections/inspections_list_screen.dart`.
- `mobile/lib/features/activity/activity_history_screen.dart`.
- `mobile/lib/features/money/money_screen.dart`.
- `mobile/lib/features/leases/leases_list_screen.dart`.
- `mobile/test/activity_history_mobile_test.dart`.
- `mobile/test/transactions_controller_test.dart`.
- `mobile/test/contextual_loading_dense_rows_test.dart`.

Required actions:

1. Allow `_InspectionCard` property/unit identity to use at most two lines.
2. Allow `_ActivityRowContent` description and entity context to use at most
   two lines.
3. Allow `_TransactionCard` source/date context to use at most two lines.
4. Allow `_RelationshipTile` tenant/relationship identity to use at most two
   lines.
5. Preserve trailing controls and `MobileM3ListItem`; do not relax other rows.
6. Extend only the named tests for long phone-width content.
7. Run the targeted test/analyze commands, then run a read-only relevance review
   against only Task 7C paths and acceptance. Require `RELEVANCE PASS` before
   real-device proof.

### Targeted commands

From `mobile/`, run serially after each completed task with that task's expected
tests present:

```bash
flutter test test/work_order_detail_edit_tabs_test.dart test/activity_history_mobile_test.dart test/transactions_controller_test.dart test/deposits_repository_test.dart test/contextual_loading_dense_rows_test.dart test/mobile_list_grid_server_query_test.dart test/grid_period_query_params_test.dart
flutter analyze lib/features/maintenance/work_order_detail_screen.dart lib/features/inspections/inspections_list_screen.dart lib/features/activity/activity_history_screen.dart lib/features/money/money_screen.dart lib/features/deposits/deposits_screen.dart lib/features/leases/leases_list_screen.dart test/contextual_loading_dense_rows_test.dart
```

- The test command proves CONTEXT-01 through CONTEXT-06.
- Analyze proves CONTEXT-01 through CONTEXT-04 and CONTEXT-06.
- Each task-local read-only relevance review proves its allowed-path boundary,
  non-goals, and mapped acceptance before the next task.

### Relevance gates

After each completed task, the reviewer inspects the complete implementation
diff attributable to that task and verifies:

- only that task's allowed paths and named symbols/behaviors changed;
- its mapped CONTEXT acceptance items and focused command results;
- no provider, repository, controller, query, auth, data-shape, global
  component, or unrelated row/loading change;
- previously accepted task changes remain preserved and are not broadened.

Require `RELEVANCE PASS` after 7A, after 7B, and after 7C.

### Real-device proof gate

After all three relevance gates pass, drive the six supported flows at
1080 × 2400 and create only:

- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/work-order-loading.png`
  and `work-order-loading.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/inspections-loading.png`
  and `inspections-loading.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/inspection-row-wrapped.png`
  and `inspection-row-wrapped.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/activity-loading.png`
  and `activity-loading.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/activity-append-row-wrapped.png`
  and `activity-append-row-wrapped.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/ledger-loading.png`
  and `ledger-loading.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/ledger-append-row-wrapped.png`
  and `ledger-append-row-wrapped.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/deposits-loading.png`
  and `deposits-loading.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/deposits-load-more-pending.png`
  and `deposits-load-more-pending.xml`.
- `Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/lease-row-wrapped.png`
  and `lease-row-wrapped.xml`.

Require `UI PROOF PASS` for CONTEXT-06 and CONTEXT-07. If the protected API is
absent or `/health` returns 502, record the exact response and return
`UI PROOF BLOCKED`. Do not waive proof, restore/deploy the stack, mutate data,
or substitute widget/static checks.

## Completion gate

- [x] Task 7A, 7B, and 7C each received `RELEVANCE PASS`.
- [x] Every changed code/test path is globally allowed and maps to a task.
- [x] Contextual initial states replace only the audited blank waits.
- [x] Append progress is compact, active-only, and loaded rows remain.
- [x] Deposit `Load more` retains its server-owned semantics and count label.
- [x] All server paging and stale-response contracts pass unchanged.
- [x] Only the four audited decisive row fields gain a second line.
- [x] Targeted test and analyze commands pass: 45/45 tests and exactly
      `No issues found!`.
- [ ] Fresh 1080 × 2400 proof returns `UI PROOF PASS`; a protected API outage
      leaves Step 7 incomplete. Work-order, inspections, and deposits
      initial-loading; activity append; and the audited
      inspection/activity/lease wrapping branches pass. Activity now has a
      matched pending-state PNG/XML pair. Ledger initial loading remains
      partial; corrected Ledger append passes exact-APK real-device proof.
      Deposits append is test-proven but does not appear with the fixture's
      exactly-full single page.

## Deferred boundaries

- Step 5's pre-fix deployed baseline remains historical `FAIL`. Commit
  `5e437dc2` is deployed and healthy; final emulator reproof passes the app
  p95, hard maximum, cold initial-content, and no-auth-replay gates.
- Step 6 real-device proof is complete with `UI PROOF PASS`.
- Every later roadmap boundary remains deferred.
