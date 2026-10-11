# TSK-750 Step 7 — Contextual loading and dense lists discovery brief

**Discovery verdict:** ROADMAP DISCOVERY READY  
**Active goal:** TSK-750 mobile UI rescue  
**Roadmap boundary:** Step 7 — contextual loading and dense-list polish  
**Prior terminal checkpoints:** Step 5 deployed identity/UI proof and Step 6
real-device proof remain blocked because the protected API container is absent
and `/health` returns 502. Step 7 does not absorb or waive either blocker.  
**Planning retry:** 0 of 3

## Goal

Replace only the four audited blank initial waits with contextual Material 3
content, retain compact progress during append/paging, and allow only the four
evidenced decisive row fields to occupy a second line.

## Observable acceptance

1. **CONTEXT-01 — Contextual pending states:** Pending work-order, inspection,
   activity, ledger, and deposit requests show recognizable feature-local
   Material 3 structure instead of a blank field around one centered spinner.
2. **CONTEXT-02 — Existing chrome preserved:** Search, filter, header, error,
   empty, refresh, and custom list components remain intact.
3. **CONTEXT-03 — Compact automatic append:** Activity and ledger append
   requests retain loaded rows and show compact list-end progress.
4. **CONTEXT-04 — Explicit deposit append:** Deposit `Load more` remains
   explicit, is disabled while pending, shows compact progress inside the
   action, and resumes with the same count label afterward.
5. **CONTEXT-05 — Server paging preserved:** Existing tests continue proving
   server `skip/take`, search, sort, filters, stale-response handling, and
   append behavior. No client filtering, sorting, grouping, aggregation, or
   reconciliation is introduced.
6. **CONTEXT-06 — Decisive text wraps locally:** At 1080 × 2400, inspection
   property/unit, activity description/entity, ledger source/date, and lease
   tenant/relationship identity may use a second line without overflow or
   clipped trailing controls.
7. **CONTEXT-07 — Mandatory Android proof:** Fresh real-Android PNG and UI XML
   prove audit Evidence 05, 13, 24, 29–34, and 55. If the protected API remains
   absent or returns 502, only this proof is `UI PROOF BLOCKED`; automated
   checks do not waive it or authorize stack restoration.

## Explicit non-goals

- No protected API restoration, deployment, Azure work, database change,
  provider, repository, query, authorization, RLS, data-shape, or client-side
  shaping change.
- No global loading-component or design-system change. Preserve
  `mobile_m3_list.dart`, `mobile_grid_controls.dart`, `MobileM3ListItem`, and
  `MobileGridPagingBar`.
- No broad spinner sweep, rebrand, row-density overhaul, expense
  amount/category correction, internal-name cleanup, or unaudited audit defect.
- Do not absorb or waive Step 5 or Step 6 terminal blockers.
- Preserve every existing dirty and untracked WIP path.

## Resolved discovery owners

- **Evidence 05:** `WorkOrderDetailScreen.build` preserves its app bar but owns
  a blank pending body in
  `mobile/lib/features/maintenance/work_order_detail_screen.dart`.
- **Evidence 13:** `inspectionsPageProvider` owns the pending list state and
  `_InspectionCard` owns property/unit truncation in
  `mobile/lib/features/inspections/inspections_list_screen.dart`; the existing
  explicit `MobileGridPagingBar` remains.
- **Evidence 24:** `_ActivityBody` owns initial/append progress and
  `_ActivityRowContent` owns description/entity truncation in
  `mobile/lib/features/activity/activity_history_screen.dart`. Server paging
  remains owned by `activity_repository.dart`.
- **Evidence 29–31:** `_LedgerTab` owns initial/append presentation and
  `_TransactionCard` owns description/source/date truncation in
  `mobile/lib/features/money/money_screen.dart`. Server paging remains owned by
  `transactions_controller.dart`.
- **Evidence 32–34:** The deposits screen owns its blank initial wait and
  explicit `Load more` presentation in
  `mobile/lib/features/deposits/deposits_screen.dart`. Page size and offsets
  remain owned by `deposits_repository.dart`.
- **Evidence 55:** `_RelationshipTile` owns tenant/relationship truncation in
  `mobile/lib/features/leases/leases_list_screen.dart`; provider and
  `MobileGridPagingBar` remain unchanged.

## Exact execution map

### Task 7A — Contextual initial states

- Modify:
  `mobile/lib/features/maintenance/work_order_detail_screen.dart`,
  `mobile/lib/features/inspections/inspections_list_screen.dart`,
  `mobile/lib/features/activity/activity_history_screen.dart`,
  `mobile/lib/features/money/money_screen.dart`, and
  `mobile/lib/features/deposits/deposits_screen.dart`.
- Use feature-local placeholders composed from existing cards,
  `MobileM3ListItem`, theme colors, icons, and compact progress.
- Modify:
  `mobile/test/work_order_detail_edit_tabs_test.dart`.
- Create:
  `mobile/test/contextual_loading_dense_rows_test.dart` for the inspection
  placeholder portion.

### Task 7B — Append and paging feedback

- Modify:
  `mobile/lib/features/activity/activity_history_screen.dart`,
  `mobile/lib/features/money/money_screen.dart`, and
  `mobile/lib/features/deposits/deposits_screen.dart`.
- Modify:
  `mobile/test/activity_history_mobile_test.dart`,
  `mobile/test/transactions_controller_test.dart`, and
  `mobile/test/deposits_repository_test.dart`.
- Retain existing repository/controller paging semantics and explicit
  inspection/lease grid paging.

### Task 7C — Decisive row wrapping

- Modify only the named row presentations in
  `mobile/lib/features/inspections/inspections_list_screen.dart`,
  `mobile/lib/features/activity/activity_history_screen.dart`,
  `mobile/lib/features/money/money_screen.dart`, and
  `mobile/lib/features/leases/leases_list_screen.dart`.
- Modify:
  `mobile/test/activity_history_mobile_test.dart`,
  `mobile/test/transactions_controller_test.dart`, and
  `mobile/test/contextual_loading_dense_rows_test.dart`.
- Preserve `MobileM3ListItem`; do not globally relax row truncation.

Exercise unchanged:

- `mobile/test/mobile_list_grid_server_query_test.dart`.
- `mobile/test/grid_period_query_params_test.dart`.

## Exact serial commands

From `mobile/`:

```bash
flutter test test/work_order_detail_edit_tabs_test.dart test/activity_history_mobile_test.dart test/transactions_controller_test.dart test/deposits_repository_test.dart test/contextual_loading_dense_rows_test.dart test/mobile_list_grid_server_query_test.dart test/grid_period_query_params_test.dart
flutter analyze lib/features/maintenance/work_order_detail_screen.dart lib/features/inspections/inspections_list_screen.dart lib/features/activity/activity_history_screen.dart lib/features/money/money_screen.dart lib/features/deposits/deposits_screen.dart lib/features/leases/leases_list_screen.dart test/contextual_loading_dense_rows_test.dart
```

- The test command maps to CONTEXT-01 through CONTEXT-06.
- Analyze maps to CONTEXT-01 through CONTEXT-04 and CONTEXT-06.
- A read-only relevance review after each completed Task 7A, 7B, and 7C maps
  its task-local diff to the corresponding acceptance items and exact paths.
- Real Android proof maps to CONTEXT-06 and CONTEXT-07.

## Real-device proof files

The verifier may create only the following files under
`Docs/Reviews/artifacts/tsk-750/step7-contextual-loading-dense-lists/`:

- `work-order-loading.png` and `work-order-loading.xml`.
- `inspections-loading.png` and `inspections-loading.xml`.
- `inspection-row-wrapped.png` and `inspection-row-wrapped.xml`.
- `activity-loading.png` and `activity-loading.xml`.
- `activity-append-row-wrapped.png` and
  `activity-append-row-wrapped.xml`.
- `ledger-loading.png` and `ledger-loading.xml`.
- `ledger-append-row-wrapped.png` and `ledger-append-row-wrapped.xml`.
- `deposits-loading.png` and `deposits-loading.xml`.
- `deposits-load-more-pending.png` and
  `deposits-load-more-pending.xml`.
- `lease-row-wrapped.png` and `lease-row-wrapped.xml`.

## Stop condition

Discovery is complete because the audited screen-local owners, preserved
paging owners, exact source/tests, commands, acceptance items, and 1080 × 2400
flows are known. If the protected API remains absent or `/health` returns 502,
record the response and return `UI PROOF BLOCKED`; do not waive proof or restore
the stack within this contract.

## Evidence references

- `S7-ROADMAP` — approved Step 7 boundary.
- `S7-AUDIT-05`, `S7-AUDIT-13`, `S7-AUDIT-24`,
  `S7-AUDIT-29-34`, `S7-AUDIT-55` — audited UI evidence.
- `S7-PAGING-OWNERS` — existing server-paging owners and tests.
- `PROTECTED-API-502` — current real-device proof blocker.
- `S5-S6-TERMINAL` — preserved incomplete proof boundaries.
