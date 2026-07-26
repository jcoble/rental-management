# TSK-750 Mobile UI Rescue — execution roadmap

## Source discovery packet

- Audit: `Docs/Reviews/2026-07-25-tsk-750-mobile-ui-audit.html`
- Coverage and acceptance checklist:
  `Docs/Reviews/2026-07-25-tsk-750-mobile-ui-checklist.md`
- Emulator artifacts: `Docs/Reviews/artifacts/tsk-750/`
- Target: Azure Android emulator `emulator-5554`, 1080 × 2400 at 420 dpi
- Backend: protected Rental Command preview stack, read-only until a deliberate
  verification deployment

## Objective

Make the landlord mobile experience simple to navigate and understand for a
person without property-management training while preserving the Material 3
foundation, unit-centered operating model, authorization boundaries, and atomic
write guarantees.

## Hard data and write boundaries

All SQL aggregation, grouping, filtering, joins, sorting, and paging must remain
DB-side in one EF-translated SQL statement or a database view. Never materialize
then filter/group/count, add per-row follow-up queries, or use lazy loading. A
roughly 20-row list should use one to three statements total, with generated SQL
inspected for every changed query.

Every write belonging to one user action remains inside one explicit transaction.
External effects happen after commit or through the outbox.

## Completed contract: Step 1 — stop the global FAB from blocking the UI

### Evidence

The closed global `Scan / Add` extended FAB overlapped primary actions and content
on Appointment Schedule/Contact, Inspection detail, Notice detail, payment
correction, and Record receipt. UI hierarchy evidence proved overlapping bounds,
not merely visual proximity:

- `11-appointment-edit-schedule.png`
- `12-appointment-edit-contact.png`
- `15-inspection-detail.png`
- `20-work-notices.png`
- `45-correct-payment-sheet.png`
- `48-record-receipt-sheet.png`

The first implementation changed the closed launcher to a compact Material 3 FAB,
but Azure emulator re-proof at commit
`f556c901e0d519ba27bea9b9aee7c7924a18735f` showed that its 147×147 hit target
still intersects five bottom actions. Notice detail now passes. The measured
failures are recorded in
`Docs/Reviews/2026-07-25-tsk-750-mobile-fab-proof.md`.

`TSK-751` is the active verified-defect fixer task within parent rescue
`TSK-750`. It closes only after FAB-07 passes on a freshly installed exact-SHA
Azure emulator build.

### Allowed source files

- `mobile/lib/features/home/mobile_quick_action_fab.dart`
- `mobile/lib/features/appointments/appointments_screen.dart`
- `mobile/lib/features/inspections/inspection_run_screen.dart`
- `mobile/lib/features/payments/payment_detail_screen.dart`
- `mobile/lib/features/payments/payments_screen.dart`

### Allowed test files

- `mobile/test/mobile_quick_action_fab_test.dart`

No other product or test file is in the active boundary. If proof shows a shared
content-inset change is also required, stop and revise this contract before
editing another file.

### Required change

- Render the closed launcher as a compact Material 3 FAB with an icon and tooltip,
  not an always-expanded text button.
- Keep the labeled `Scan / Add`, Record, Assistant, and contextual actions inside
  the expanded action menu.
- Preserve capability filtering, scoped-action registration, disabled-animation
  behavior, tooltip discoverability, and one-tap menu opening.
- Add one reusable scope-aware hider that registers a hidden owner while its
  child is mounted and always releases that owner when the child leaves the
  tree or changes scope.
- Apply that hider only to the evidenced Appointment Edit Schedule/Contact flow
  (not Appointment Create), active Inspection run,
  payment correction, and Record receipt surfaces whose persistent bottom
  actions conflict with the global FAB.
- Keep the FAB visible on ordinary pages and on Notice detail, which passed
  emulator re-proof.
- Do not change action authorization, navigation, capture behavior, or feature
  labels inside the expanded menu.

### Acceptance criteria

- **FAB-01:** Closed state renders one compact FAB and no visible `Scan / Add`
  label, while retaining a `Scan / Add` tooltip when scanning is available.
- **FAB-02:** Tapping the closed FAB opens the same authorized action set as
  before, including the visible `Scan / Add` action when permitted.
- **FAB-03:** Closing the menu restores the compact state and the closed control's
  width is no greater than its height plus Material tap-target tolerance.
- **FAB-04:** Disabled-animation mode preserves the same action set without
  `AnimatedSize`, `AnimatedSwitcher`, or `TweenAnimationBuilder`.
- **FAB-05:** Scoped and host-registered quick actions still render only one
  launcher.
- **FAB-06:** The reusable hider hides the scoped launcher while mounted, restores
  it on disposal, and does not disturb another active hidden owner.
- **FAB-07:** Emulator re-proof shows the FAB absent on Appointment Next/Save
  Changes, Inspection Complete, Append correction, and Record receipt; Notice
  detail and an ordinary page retain the compact FAB without action overlap.

### Targeted commands

Run serially:

```bash
dart format mobile/lib/features/home/mobile_quick_action_fab.dart \
  mobile/lib/features/appointments/appointments_screen.dart \
  mobile/lib/features/inspections/inspection_run_screen.dart \
  mobile/lib/features/payments/payment_detail_screen.dart \
  mobile/lib/features/payments/payments_screen.dart \
  mobile/test/mobile_quick_action_fab_test.dart
flutter test mobile/test/mobile_quick_action_fab_test.dart
flutter analyze mobile/lib/features/home/mobile_quick_action_fab.dart \
  mobile/lib/features/appointments/appointments_screen.dart \
  mobile/lib/features/inspections/inspection_run_screen.dart \
  mobile/lib/features/payments/payment_detail_screen.dart \
  mobile/lib/features/payments/payments_screen.dart \
  mobile/test/mobile_quick_action_fab_test.dart
git diff --check
```

The Flutter test/analyze commands run from `mobile/` with paths adjusted to
`test/mobile_quick_action_fab_test.dart` and
`lib/features/home/mobile_quick_action_fab.dart` as required by the tool.

### Relevance gate

After implementation, a read-only reviewer checks only:

1. the active file diff;
2. FAB-01 through FAB-06 test evidence;
3. absence of authorization/navigation changes;
4. whether any new file or behavior exceeds this contract.

Any out-of-bound discovery is recorded for a pending step; it is not absorbed.

### Emulator proof gate

The sole emulator tester re-captures these exact changed states under:

`Docs/Reviews/artifacts/tsk-750/post-fab-hidden/`

- `appointment-schedule.png`
- `appointment-contact.png`
- `inspection-detail.png`
- `notice-detail.png`
- `payment-correction.png`
- `record-receipt.png`
- `ordinary-page.png`
- matching UI hierarchy XML for each screen

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-fab-hidden-proof.md`

Step 1 completes only when FAB-07 is evidenced on the Azure emulator.
After that proof, close and verify `TSK-751`; keep parent rescue `TSK-750` open
for the remaining roadmap boundaries.

## Completed contract: Step 2A — make every hub section discoverable

### Evidence

The shared hub segment bar shows only about three destinations at once and gives
no explicit indication that more sections exist off-screen. In Rentals, the
authorized set is Properties, Owners, Units, Tenants, Leases, and Applications.
Audit captures `52-rentals-return.png`, `53-rentals-tabs-scroll.png`, and
`57-rentals-tabs-end.png` prove that users must guess at horizontal scrolling to
find the first or last destinations.

### Allowed source files

- `mobile/lib/features/home/mobile_domain_hub.dart`

### Allowed test files

- `mobile/test/mobile_domain_hub_test.dart`

No authorization, destination construction, router, content screen, unit command
center, Today, or work-order file is in this active boundary.

### Required change

- For a hub with four or more authorized destinations, replace the mystery
  horizontal segment track with one full-width Material 3 anchored section
  selector.
- The closed selector identifies the currently selected section and has a
  discoverable section-switching label/tooltip.
- Opening it presents every authorized destination in one menu, with destination
  iconography and a selected-state check; no horizontal gesture is needed.
- Selecting a destination must call the existing `_selectIndex` path so content
  root replacement, nested detail behavior, authorization filtering, state
  restoration, quick actions, and deep-link handling remain unchanged.
- Hubs with three or fewer destinations retain the existing compact segment
  presentation.

### Acceptance criteria

- **HUB-01:** A six-destination Rentals hub renders one explicit section selector
  and no horizontal `SingleChildScrollView`.
- **HUB-02:** Opening the selector exposes Properties, Owners, Units, Tenants,
  Leases, and Applications together, with the current destination marked.
- **HUB-03:** Selecting Applications closes the menu, changes the selected label
  and root content, and selecting Units again restores Units root through the
  same navigation controller path.
- **HUB-04:** A three-destination hub retains three directly tappable segment
  labels and does not render the anchored selector.
- **HUB-05:** The authorized destination list is not copied, reordered, expanded,
  or filtered by the selector.
- **HUB-06:** Azure emulator proof shows the Rentals selector closed on Units,
  open with all six sections discoverable, and closed on Applications, with no
  clipped section label or FAB/action overlap. The existing three-destination
  Inbox hub still shows Messages, Notifications, and Activity History as three
  directly tappable segments with no anchored selector.

### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/features/home/mobile_domain_hub.dart \
  test/mobile_domain_hub_test.dart
flutter test test/mobile_domain_hub_test.dart
flutter analyze lib/features/home/mobile_domain_hub.dart \
  test/mobile_domain_hub_test.dart
git diff --check
```

### Relevance gate

After implementation, a read-only reviewer checks only:

1. the two allowed-file diffs;
2. HUB-01 through HUB-05 test evidence;
3. preservation of the existing destination/navigation path;
4. absence of authorization, router, content, or other roadmap changes.

### Emulator proof gate

The sole emulator tester captures PNG and matching hierarchy XML under:

`Docs/Reviews/artifacts/tsk-750/post-hub-selector/`

- `rentals-units-closed`
- `rentals-selector-open`
- `rentals-applications-closed`
- `inbox-three-segments`

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-hub-selector-proof.md`

Step 2A completes only when HUB-06 is evidenced on an exact-SHA Azure emulator
build.

## Completed contract: Step 2B — make Unit Command Center sections discoverable

### Evidence

The Unit Command Center has six peer sections in an `isScrollable` `TabBar`.
Audit captures `00-current-screen.png` and `42-unit-main-money.png` show the
selected end of that strip while earlier labels are clipped off-screen. A user
cannot see Summary, Leasing, Tenant & lease, Money, Maintenance, and Documents
& history together or know that the strip scrolls.

### Allowed source files

- `mobile/lib/core/widgets/mobile_section_selector.dart` (new shared component)
- `mobile/lib/features/home/mobile_domain_hub.dart`
- `mobile/lib/features/units/unit_command_center_screen.dart`

### Allowed test files

- `mobile/test/mobile_domain_hub_test.dart`
- `mobile/test/unit_navigation_test.dart`

No unit content surface, repository, authorization, router, Today, work-order,
or backend file is in this active boundary.

### Required change

- Extract the already-proven anchored hub selector presentation into one generic
  Material 3 section-selector component without changing Step 2A behavior.
- Replace the Unit Command Center's scrollable top `TabBar` with that selector,
  listing Summary, Leasing, Tenant & lease, Money, Maintenance, and Documents &
  history together with icons and a selected-state check.
- Drive the existing `DefaultTabController` through `animateTo`; retain the
  existing single `TabBarView`, tab order, `_handleTopTabChanged`, active nested
  view memory, deep-link initial tab, restoration, and content widgets.
- The selector tooltip/semantic label must say that it changes the unit section.
- Do not change the nested section controls within Leasing, Tenant & lease,
  Money, Maintenance, or Documents & history.

### Acceptance criteria

- **UNITNAV-01:** Unit Command Center renders one full-width section selector,
  no top-level `TabBar`, and the existing one top-level `TabBarView`.
- **UNITNAV-02:** Opening the selector exposes all six ordered unit sections,
  with the current section marked.
- **UNITNAV-03:** Selecting Money and Documents & history updates the selector,
  `DefaultTabController`, root content, and existing restoration state through
  the current tab-change listener.
- **UNITNAV-04:** A deep-linked initial Documents & history or Maintenance state
  opens with the matching selector value and retains its requested nested view.
- **UNITNAV-05:** The shared six-destination Rentals selector and the
  three-destination Inbox segments continue to pass their existing tests.
- **UNITNAV-06:** Azure emulator proof shows Unit Summary closed, the open
  six-section menu, Money closed with its content, and Documents & history closed
  with Documents/History content; no label is clipped and the FAB intersects no
  selector or menu action.

### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/core/widgets/mobile_section_selector.dart \
  lib/features/home/mobile_domain_hub.dart \
  lib/features/units/unit_command_center_screen.dart \
  test/mobile_domain_hub_test.dart test/unit_navigation_test.dart
flutter test test/mobile_domain_hub_test.dart test/unit_navigation_test.dart
flutter analyze lib/core/widgets/mobile_section_selector.dart \
  lib/features/home/mobile_domain_hub.dart \
  lib/features/units/unit_command_center_screen.dart \
  test/mobile_domain_hub_test.dart test/unit_navigation_test.dart
git diff --check
```

### Relevance gate

After implementation, a read-only reviewer checks only:

1. the five allowed-file diffs;
2. UNITNAV-01 through UNITNAV-05 test evidence;
3. preservation of tab order, `DefaultTabController`, `TabBarView`, nested view
   memory, deep-link initial state, and Step 2A behavior;
4. absence of content, data, authorization, router, or other roadmap changes.

### Emulator proof gate

The sole emulator tester captures PNG and matching hierarchy XML under:

`Docs/Reviews/artifacts/tsk-750/post-unit-selector/`

- `unit-summary-closed`
- `unit-selector-open`
- `unit-money-closed`
- `unit-documents-history-closed`

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-unit-selector-proof.md`

Step 2B completes only when UNITNAV-06 is evidenced on an exact-SHA Azure
emulator build.

## Completed contract: Step 2C — preserve Today work-order origin

### Evidence

Audit captures `04-today-briefing-bottom.png`,
`05-work-order-detail-from-today.png`, `06-work-order-detail-loaded.png`, and
`07-back-from-work-order.png` prove that a work order opened from Today changes
the selected shell destination to Work. Back therefore returns to the Work root
instead of the prior Today scroll position.

Source tracing confirms two Today-owned work-order entry points make that
destination switch:

- `_FieldQueueCard.onTap` calls `MobileShellNavigator.openTab` for
  `MobileShellTabId.work`.
- a briefing bullet whose entity type is `WorkOrder` maps to a
  `_BriefingTarget` that also opens the Work destination.

The existing `WorkOrderUnitAwareLoaderScreen` already supplies the correct
unit-centered detail. The defect is the origin-changing shell navigation before
that loader is pushed.

### Allowed source files

- `mobile/lib/features/home/home_shell.dart`

### Allowed test files

- `mobile/test/navigation_contract_test.dart`
- `mobile/test/work_order_shell_target_loader_test.dart`

No work-order content, unit content, domain hub, repository, authorization,
router, Today data provider, backend, or database file is in this boundary.

### Required change

- Work orders tapped in Today's Work Orders queue must push the existing
  `WorkOrderUnitAwareLoaderScreen` over the current Navigator route without
  selecting the Work shell destination.
- Work-order bullets tapped in Today's briefing must use the same
  current-origin policy.
- Keep the Today `CustomScrollView` mounted underneath the pushed detail so Back
  restores the exact prior scroll offset.
- Retain `WorkOrderUnitAwareLoaderScreen` unchanged so unit-scoped work orders
  still open the Unit Command Center's Maintenance / Work orders view and
  non-unit work orders still open ordinary work-order detail.
- Keep `View all` switching to Work; it is an explicit destination change, not a
  detail drill-in.
- Keep direct `/work-orders/{id}` routes, notification/deep-link routing, voice
  navigation, and work orders opened from the Work hub on their existing shell
  destination paths.
- Do not change authorization, work-order fetching, detail content, restoration
  persistence, or any write/data boundary.

### Acceptance criteria

- **TODAYNAV-01:** Tapping a Today Work Orders queue row pushes the unit-aware
  detail without changing the selected bottom destination from Today.
- **TODAYNAV-02:** Tapping a Today briefing bullet for a WorkOrder uses the same
  current-origin push; other briefing entity targets retain their existing
  destination routing.
- **TODAYNAV-03:** Back from both the loading state and loaded unit-aware detail
  returns to Today at the prior scroll position.
- **TODAYNAV-04:** `View all` still selects Work and ordinary Work-hub rows still
  return to the Work root.
- **TODAYNAV-05:** Existing direct work-order route and unit-aware shell-target
  tests continue to pass without changes to the loader or router.
- **TODAYNAV-06:** Exact-SHA Azure emulator proof captures Today at its Work
  Orders section, the selected unit-aware work-order detail, and Back on Today
  at the same scroll position; the bottom bar remains on Today before and after.

### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/features/home/home_shell.dart \
  test/navigation_contract_test.dart \
  test/work_order_shell_target_loader_test.dart
flutter test test/navigation_contract_test.dart \
  test/work_order_shell_target_loader_test.dart
flutter analyze lib/features/home/home_shell.dart \
  test/navigation_contract_test.dart \
  test/work_order_shell_target_loader_test.dart
git diff --check
```

### Relevance gate

After implementation, a read-only reviewer checks only:

1. the allowed-file diff;
2. TODAYNAV-01 through TODAYNAV-05 evidence;
3. preservation of the unit-aware loader, direct/deep-link routing, explicit
   `View all` tab switching, Work-hub origin, and non-work-order briefing paths;
4. absence of content, data, authorization, router, or other roadmap changes.

### Emulator proof gate

The sole emulator tester captures PNG and matching hierarchy XML under:

`Docs/Reviews/artifacts/tsk-750/post-today-origin/`

- `today-work-order-origin`
- `today-work-order-detail`
- `today-work-order-back`

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-today-origin-proof.md`

Step 2C completes only when TODAYNAV-06 is evidenced on an exact-SHA Azure
emulator build.

## Pending roadmap boundaries

These are evidence-backed but inactive. Each receives its own exact-file contract
and relevance gate only after the active step closes.

### Step 3 — plain-English money and rental presentation

- Translate audited wire values and internal identifiers at presentation
  boundaries.
- Replace “allocated/open” accounting jargon with explicit paid/original/remaining
  labels.
- Make chart months readable and preserve distinguishing list context.
- Clarify the vendor work-count label according to its authoritative metric.
- Evidence: 06, 07, 14, 16, 24–26, 30, 31, 41, 42, 55.

#### Completed contract: Step 3A — make Unit summary and money language understandable

Independent review passed after expanding the contract to all thirteen ledger
entry types and every existing raw Unit status/type/stage call site.

##### Evidence and authoritative meaning

Audit captures `41-unit-main-overview.png`, `42-unit-main-money.png`, and
`55-unit-main-overview-bottom.png` show raw identifiers and wire values including
`TenantAccount`, `PaymentReceipt`, `NoGoverningAgreement`, `NotAvailable`, and
`RentReady`.

The charge-balance projection proves:

- `OriginalAmount` is the posted charge amount;
- `NetAllocations` is the sum of ledger allocations applied to that debit;
- `OpenAmount` is the amount still due after reversals and allocations.

Because reversals are a separate server field not included in the mobile charge
model, the UI must not imply that `OriginalAmount - NetAllocations` always equals
`OpenAmount`. The understandable and accurate mobile summary is therefore
“Charge amount” plus “Still due”; the internal allocation term is omitted.

##### Allowed source files

- `mobile/lib/core/presentation/plain_english_labels.dart` (new)
- `mobile/lib/features/units/unit_command_center_screen.dart`
- `mobile/lib/features/payments/payment_detail_screen.dart`

##### Allowed test files

- `mobile/test/plain_english_labels_test.dart` (new)
- `mobile/test/unit_money_contract_test.dart`
- `mobile/test/payment_detail_copy_contract_test.dart` (new)

No repository, DTO, API, database, paging, navigation, authorization, write
path, validation, submission behavior, or non-listed screen is in this
boundary. The only form change allowed is presentation copy inside the listed
payment-correction sheet.

##### Required change

- Add one presentation-only label translator with explicit understandable
  mappings for the Unit condition values and all thirteen tenant-ledger entry
  types emitted by the current backend. Unknown non-empty Pascal/camel-case
  values receive a readable spaced fallback; blank values receive a
  caller-supplied fallback.
- In Unit Summary, rename `TenantAccount` to `Tenant account` and translate the
  five independent condition values plus Unit status and lifecycle stage without
  changing the underlying values or their independent rows.
- In Unit Money:
  - translate account-activity entry types such as `PaymentReceipt` to
    `Payment received` and `RentCharge` to `Rent charged`;
  - rename `Charge allocation and open amount` to `Rent charges`;
  - render each charge as `Charge amount: <amount> · Still due: <amount>`;
  - do not display `allocated`, `allocation`, `open amount`, or the internal
    tenant-account numeric ID;
  - translate deposit statuses at the presentation boundary.
- Apply the same translator to every other raw status/type/stage string already
  rendered by the Unit Command Center: inspection type/status, payment
  type/status, work-order priority/status, appointment type/status, Unit status,
  lifecycle stage, and deposit status. Preserve specialized enum labels that
  are already user-facing.
- Keep descriptions, dates, currency amounts, row actions, payment-detail
  navigation, server paging, provider lifecycles, and all wire values unchanged.
- On payment detail and its correction sheet, replace accounting-system language
  (`immutable posted record`, `compensating allocations`, `payout provenance`,
  and raw ledger-entry numbers) with plain-English receipt, refund, payment
  reference, and account-history wording. Label the two stable business
  identifiers `Account number` and `Lease number`; do not alter or hide their
  values.

##### Acceptance criteria

- **UNITCOPY-01:** Unit Summary displays `Tenant account`, `Not available`,
  `No signed lease`, and `Ready to rent` for the corresponding audited wire
  values, with no raw `TenantAccount`, `NotAvailable`,
  `NoGoverningAgreement`, or `RentReady` text.
- **UNITCOPY-02:** Account activity displays understandable entry labels for
  every current `TenantLedgerEntryType`; tests cover `OpeningBalance`,
  `RentCharge`, `AddendumCharge`, `LateFeeCharge`, `DepositCharge`,
  `ManualCharge`, `PaymentReceipt`, `Credit`, `Adjustment`, `Refund`,
  `TransferIn`, `TransferOut`, and `Reversal`.
- **UNITCOPY-03:** Rent-charge rows display only the description,
  `Charge amount`, and `Still due`; the existing `NetAllocations` model and API
  parsing remain unchanged and are not used to create misleading arithmetic.
- **UNITCOPY-04:** Unit Money never displays a raw internal tenant-account ID
  when no agreement number exists; the row instead says that the account is
  available without exposing its database key.
- **UNITCOPY-05:** Existing payment rows remain tappable and continue opening
  payment detail; all existing Unit Money providers and independent server
  pagers remain present.
- **UNITCOPY-06:** Focused source/widget assertions prove the Unit Command
  Center's existing condition, Unit status, lifecycle stage, deposit status,
  inspection type/status, payment type/status, work-order priority/status, and
  appointment type/status call sites all pass raw values through the
  presentation translator. Existing specialized enum labels remain unchanged.
- **UNITCOPY-07:** Exact-SHA Azure emulator proof captures Unit Summary and Unit
  Money with the audited raw values absent, the charge explanation readable,
  and payment-detail navigation still working at 1080x2400 / 420 density.
- **UNITCOPY-08:** Payment detail and the correction sheet explain permanent
  receipt/refund behavior in plain English, label the business identifiers as
  `Account number` and `Lease number`, and expose no `allocation`,
  `immutable`, `provenance`, or raw ledger-entry number to the user.

##### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/core/presentation/plain_english_labels.dart \
  lib/features/units/unit_command_center_screen.dart \
  lib/features/payments/payment_detail_screen.dart \
  test/plain_english_labels_test.dart \
  test/unit_money_contract_test.dart \
  test/payment_detail_copy_contract_test.dart
flutter test test/plain_english_labels_test.dart \
  test/unit_money_contract_test.dart \
  test/payment_detail_copy_contract_test.dart \
  test/unit_navigation_test.dart
flutter analyze lib/core/presentation/plain_english_labels.dart \
  lib/features/units/unit_command_center_screen.dart \
  lib/features/payments/payment_detail_screen.dart \
  test/plain_english_labels_test.dart \
  test/unit_money_contract_test.dart \
  test/payment_detail_copy_contract_test.dart
git diff --check
```

##### Relevance gate

After implementation, a read-only reviewer checks only:

1. the six allowed-file diffs;
2. UNITCOPY-01 through UNITCOPY-06 plus UNITCOPY-08 evidence;
3. preservation of wire values, repositories, independent server paging,
   payment-detail actions, correction validation/submission behavior,
   navigation, authorization, and activity history;
4. absence of data, backend, form-behavior, or other roadmap changes.

##### Emulator proof gate

The sole emulator tester captures PNG and matching hierarchy XML under:

`Docs/Reviews/artifacts/tsk-750/post-unit-copy/`

- `unit-summary-plain-english`
- `unit-money-plain-english`
- `unit-money-payment-detail`
- `unit-money-payment-correction`

The payment-detail and payment-correction captures must both satisfy
UNITCOPY-08 while preserving the correction sheet's existing fields and submit
behavior.

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-unit-copy-proof.md`

#### Completed contract: Step 3B — clarify Today, chart months, and vendor counts

Step 3A's exact-SHA emulator proof and independent review passed. Step 3B source
commit `a1eb500391b79b62399ab57c5650d419e9ec0e95` passed its focused tests,
targeted analysis, relevance review, and exact-SHA emulator proof. COPYFOLLOW-05
passed on APK SHA-256
`16f0a14ff7277e391a93286757790cd40e60f53e9487b0314c999d34f79b9ab6`;
the report is
`Docs/Reviews/2026-07-25-tsk-750-mobile-copy-followup-proof.md`.

##### Evidence and authoritative meaning

- Audit capture `04-today-briefing-bottom.png` shows `InProgress` leaking into
  Today's Work Orders row. Its subtitle also forces property, status, and
  priority into one truncated line.
- Capture `25-money-dashboard.png` shows chart labels such as `-08` because the
  painter takes the last three characters of a `YYYY-MM` value.
- Capture `16-work-vendors.png` shows an assigned vendor beside `0 jobs`. The
  authoritative mobile field is named
  `jobsCompleted`, its model documents it as completed work orders, and Vendor
  detail already labels the same scorecard value `Jobs completed`.

##### Allowed source files

- `mobile/lib/core/presentation/date_labels.dart` (new)
- `mobile/lib/features/home/home_shell.dart`
- `mobile/lib/features/analytics/insights_screen.dart`
- `mobile/lib/features/vendors/vendors_list_screen.dart`

##### Allowed test files

- `mobile/test/date_labels_test.dart` (new)
- `mobile/test/navigation_contract_test.dart`
- `mobile/test/vendor_mobile_crud_test.dart`

The existing `plain_english_labels.dart` translator is reused unchanged. No
model, repository, API, database, query, chart metric, navigation, list paging,
vendor scorecard, or work-order action is in this boundary.

##### Required change

- Today Work Orders passes status and priority through the existing
  presentation translator and gives property, status, and priority two readable
  lines instead of one slash-delimited truncated line. Preserve the current
  detail push and Today-origin behavior.
- Add one pure short-month label helper that converts canonical `YYYY-MM` values
  to `Jan` through `Dec`, preserves already readable month labels, and returns a
  safe fallback for blank values. The analytics painter uses the helper without
  changing bar values, order, dimensions, or repaint behavior.
- Vendor list labels `jobsCompleted` explicitly as `completed job` or
  `completed jobs`. Preserve the numeric value and scorecard/detail behavior.

##### Acceptance criteria

- **COPYFOLLOW-01:** A Today row renders `In progress` rather than `InProgress`,
  preserves its property name, renders priority in understandable sentence
  case, permits two subtitle lines, and still pushes the same unit-aware detail
  without switching the shell destination.
- **COPYFOLLOW-02:** `2026-01`, `2026-08`, and `2026-12` render `Jan`, `Aug`, and
  `Dec`; `Jan` remains `Jan`; blank data renders `—`; invalid non-empty data is
  preserved rather than guessed.
- **COPYFOLLOW-03:** Analytics continues to paint the same income/expense
  `TrendPoint` values in their existing server order; no client aggregation,
  sorting, filtering, or paging is introduced.
- **COPYFOLLOW-04:** Vendor rows render `0 completed jobs`, `1 completed job`,
  and plural counts accurately from the existing `jobsCompleted` field.
- **COPYFOLLOW-05:** Exact-SHA Azure emulator proof captures the Today work-order
  row, Analytics chart, and Vendor list with readable, unclipped copy and no
  blank screen, hang, FAB overlap, or navigation regression.

##### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/core/presentation/date_labels.dart \
  lib/features/home/home_shell.dart \
  lib/features/analytics/insights_screen.dart \
  lib/features/vendors/vendors_list_screen.dart \
  test/date_labels_test.dart \
  test/navigation_contract_test.dart \
  test/vendor_mobile_crud_test.dart
flutter test test/date_labels_test.dart \
  test/navigation_contract_test.dart \
  test/vendor_mobile_crud_test.dart
flutter analyze lib/core/presentation/date_labels.dart \
  lib/features/home/home_shell.dart \
  lib/features/analytics/insights_screen.dart \
  lib/features/vendors/vendors_list_screen.dart \
  test/date_labels_test.dart \
  test/navigation_contract_test.dart \
  test/vendor_mobile_crud_test.dart
git diff --check
```

##### Relevance gate

After implementation, a read-only reviewer checks only:

1. the seven allowed-file diffs;
2. COPYFOLLOW-01 through COPYFOLLOW-04 evidence;
3. preservation of Today-origin navigation, analytics values/order, the
   `jobsCompleted` numeric authority, server paging, and all actions;
4. absence of repository, DTO, backend, query, data, or other roadmap changes.

##### Emulator proof gate

The sole emulator tester captures PNG and matching hierarchy XML under:

`Docs/Reviews/artifacts/tsk-750/post-copy-followup/`

- `today-work-order-readable`
- `analytics-readable-months`
- `vendors-completed-jobs`

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-copy-followup-proof.md`

### Step 4 — honest empty states and legal-notice safety

#### Proposed contract: Step 4A — make empty-state instructions truthful

##### Evidence and authoritative behavior

- Audit capture `18-work-recurring-maintenance.png` says `Tap + to schedule
  recurring maintenance`, but the screen exposes a shared quick-action launcher
  whose contextual action is labeled `New recurring task`; there is no
  permanently visible plus control.
- Audit capture `21-inbox-messages.png` says `Tap the pencil to message a
  tenant`. The pencil is only the contextual icon inside the shared quick-action
  launcher, and that action is omitted when the signed-in user lacks
  `rentals.manage` and `leasing.onboarding.manage`.
- The action implementation, capability decision, and navigation are already
  correct. Only the instructions are false or unavailable to some users.

##### Allowed source files

- `mobile/lib/features/recurring_maintenance/recurring_maintenance_list_screen.dart`
- `mobile/lib/features/messages/messages_list_screen.dart`

##### Allowed test files

- `mobile/test/mobile_empty_state_copy_test.dart` (new)

No FAB behavior, authorization policy, repository, paging, filter, navigation,
backend, or notification workflow is in this boundary.

##### Required change

- Recurring Maintenance tells the user to open the action button and choose
  `New recurring task`; it does not refer to a visible plus.
- Messages receives the already-computed `canStartConversation` and
  `tenantMode` decisions. When creation is allowed, its empty state tells the
  user to open the action button and choose `New conversation`. In tenant
  experience it says that messages from the rental team will appear here. In a
  management experience without either create capability, it explains that the
  user can read conversations here but needs workspace access to start one. No
  branch may point to a control that is absent.
- Filtered-empty instructions remain `Try changing your search or filters.`

##### Acceptance criteria

- **EMPTY-01:** The recurring-maintenance empty state names the existing
  `New recurring task` action and contains no `Tap +`.
- **EMPTY-02:** An authorized management user with no conversations sees an
  instruction naming `New conversation`; a management user without either
  start-message capability sees an access explanation; and a tenant sees
  tenant-appropriate waiting copy. Neither denied branch shows a pencil or
  unavailable action instruction.
- **EMPTY-03:** A filtered-empty Messages result keeps the existing search/filter
  guidance regardless of create capability.
- **EMPTY-04:** Existing quick actions, capabilities, page queries, search,
  filters, paging, refresh, and navigation are byte-for-byte unchanged outside
  passing the existing capability result into the empty-state presenter.
- **EMPTY-05:** Exact-SHA Azure emulator proof captures both empty states with
  readable wrapping and verifies that the named contextual action is present
  for the authorized test user.

##### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/features/recurring_maintenance/recurring_maintenance_list_screen.dart \
  lib/features/messages/messages_list_screen.dart \
  test/mobile_empty_state_copy_test.dart
flutter test test/mobile_empty_state_copy_test.dart
flutter analyze lib/features/recurring_maintenance/recurring_maintenance_list_screen.dart \
  lib/features/messages/messages_list_screen.dart \
  test/mobile_empty_state_copy_test.dart
git diff --check
```

##### Relevance and emulator gates

A read-only relevance review checks the three allowed-file diffs, EMPTY-01
through EMPTY-04, and the absence of FAB, authorization, data, or navigation
changes. The sole emulator tester then captures PNG and hierarchy XML under
`Docs/Reviews/artifacts/tsk-750/post-empty-copy/` as:

- `recurring-maintenance-empty`
- `recurring-maintenance-action-open`
- `messages-empty-authorized`
- `messages-action-open-authorized`
- `messages-empty-no-create-access`
- `messages-actions-no-create-access`

The denied capture must use an existing non-privileged or tenant experience on
the installed app without changing roles, clearing data, or reseeding. Its
hierarchy must contain the denied/tenant explanation and must not expose a
`New conversation` action. If the installed account has no such existing
experience, EMPTY-05 remains unproved and Step 4A stays open rather than
manufacturing authorization state.

The proof report is
`Docs/Reviews/2026-07-25-tsk-750-mobile-empty-copy-proof.md`.

#### Proposed contract: Step 4B — block unsafe notice delivery at the atomic boundary

##### Reproduced source boundary

- Audit capture `20-work-notices.png` shows a Draft notice containing a
  brace-wrapped tenant value and the internal sentence beginning
  `Workspace administrator:` while the `Approve` action remains enabled.
- `SuppliedNoticeTemplateBaseline.V2Legal` currently places that internal
  instruction directly inside both tenant-facing legal template bodies.
- `TenantNoticeDraftSetStore` renders canonical facts in one PostgreSQL set
  statement, but persisted/customized malformed tokens can still leave brace
  markers in a draft.
- `AtomicNoticeDeliveryHandler` freezes `draft.Subject` and `draft.Body` and
  creates recipient/outbox evidence inside the canonical transaction without
  checking for unresolved merge content or internal instructions. Both mobile
  approval entry points call this same API boundary.

##### Allowed source files

- `RentalCommand.Api/Services/Domain/NoticeDeliveryContentSafety.cs` (new pure policy)
- `RentalCommand.Api/Services/Domain/AtomicNoticeDeliveryMutation.cs`
- `mobile/lib/features/notices/notice_content_safety.dart` (new presentation mirror)
- `mobile/lib/features/notices/notices_screen.dart`
- `mobile/lib/features/notices/create_tenant_notice.dart`

##### Allowed test files

- `RentalCommand.Api.Tests/Notices/NoticeDeliveryContentSafetyTests.cs` (new)
- `RentalCommand.IntegrationTests/SuppliedNoticeTemplateBaselineTests.cs`
- `mobile/test/notice_content_safety_test.dart` (new)
- `mobile/test/notice_review_edit_test.dart`

Supplied-template version correction is a separate Step 4C boundary. No
controller route, DTO, repository, delivery-channel selection, recipient
resolution, outbox construction, legal-jurisdiction rule, or transaction
structure changes in Step 4B.

##### Required change

- Add a deterministic server-side content-safety policy that reserves balanced
  non-empty brace-delimited fragments (`{...}`, including `{{...}}`) as unsafe
  merge residue and also rejects the internal `Workspace administrator:`
  instruction. Literal balanced braces are intentionally unsupported in
  outgoing notice copy because this product uses braces exclusively for merge
  syntax. Unbalanced braces and ordinary punctuation outside that reserved
  grammar are not guessed to be tokens. The policy returns one stable,
  plain-English correction message without echoing tenant content.
- Call that policy inside `AtomicNoticeDeliveryHandler` after the authorized
  editable draft is loaded and before any `RenderedNotice`, inbox message,
  delivery evidence, outbox row, status transition, or flush is created. A
  failure therefore rolls back/no-ops inside the existing atomic command.
- Mirror the same deterministic check in mobile presentation so the standalone
  Draft card disables `Approve` and explains what must be corrected. The
  editable tenant-scoped review sheet disables `Send notice` until its current
  subject/body controllers contain safe text and shows the same guidance.
- The API remains authoritative: direct or stale clients cannot bypass the
  block. Mobile does not claim that editing alone proves legal sufficiency.

##### Acceptance criteria

- **NOTICE-SAFE-01:** Subject or body containing `{{tenant_name}}`,
  `{Sofia Rodriguez}`, another non-empty brace-delimited fragment, or
  `Workspace administrator:` is rejected before any delivery mutation.
- **NOTICE-SAFE-02:** Safe tenant-facing copy, ordinary punctuation, and
  unbalanced literal braces pass. Balanced non-empty braces are the one
  explicitly reserved punctuation form. The policy does not inspect or
  transform recipients, channels, amounts, dates, or jurisdiction facts.
- **NOTICE-SAFE-03:** Pure server tests cover the exact reserved grammar and one
  non-sensitive correction message. The existing PostgreSQL
  `ApproveAndQueue_UsesDispatchableOutboxTypes_AndProjectsDurableStatus`
  handler fixture first submits unsafe content through
  `NotificationFoundationService.ApproveAndQueueAsync`, then asserts zero
  `RenderedNotice`, `NoticeDeliveryEvidence`, tenant-notice `OutboxMessage`,
  `Conversation`, `ConversationMessage`, and tenant `Notification` rows; the
  draft remains `Draft`, its work item remains `Claimed`, and no status or
  conversation ID changes. The same fixture then replaces the body with safe
  content and retains its existing successful-delivery assertions.
- **NOTICE-SAFE-04:** The standalone mobile Draft card disables `Approve` and
  shows the correction guidance for unsafe persisted content; safe Draft cards
  preserve channel selection and approval.
- **NOTICE-SAFE-05:** The editable review sheet reevaluates current controller
  text, disables `Send notice` while unsafe, and enables it after the user
  removes all residue/internal guidance. `Keep as draft` remains available.
- **NOTICE-SAFE-06:** Existing atomic locking, authorization, one-transaction
  delivery, recipient SQL projection, outbox/evidence graph, idempotency, and
  external-after-commit behavior remain unchanged.
- **NOTICE-SAFE-07:** Exact-SHA Azure emulator proof shows the captured unsafe
  legal draft cannot be approved, explains why in plain English, and a safe
  edited draft can reach an enabled send state without submitting a real
  delivery during proof.

##### Targeted commands

Run the focused API and mobile commands serially; no parallel build/test:

```bash
dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
  --filter FullyQualifiedName~NoticeDeliveryContentSafetyTests
dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter FullyQualifiedName~ApproveAndQueue_UsesDispatchableOutboxTypes_AndProjectsDurableStatus
cd mobile
dart format lib/features/notices/notice_content_safety.dart \
  lib/features/notices/notices_screen.dart \
  lib/features/notices/create_tenant_notice.dart \
  test/notice_content_safety_test.dart \
  test/notice_review_edit_test.dart
flutter test test/notice_content_safety_test.dart \
  test/notice_review_edit_test.dart
flutter analyze lib/features/notices/notice_content_safety.dart \
  lib/features/notices/notices_screen.dart \
  lib/features/notices/create_tenant_notice.dart \
  test/notice_content_safety_test.dart \
  test/notice_review_edit_test.dart
git diff --check
```

##### Relevance and emulator gates

A read-only relevance review checks only the nine allowed files,
NOTICE-SAFE-01 through NOTICE-SAFE-06, and confirms that no query, route,
recipient, channel, legal-review, or atomicity behavior changed beyond the
pre-mutation safety rejection. The sole emulator tester captures PNG and XML
under `Docs/Reviews/artifacts/tsk-750/post-notice-safety/` as:

- `unsafe-notice-blocked`
- `unsafe-notice-guidance`
- `safe-edited-notice-ready`

The proof report is
`Docs/Reviews/2026-07-25-tsk-750-mobile-notice-safety-proof.md`.

#### Proposed contract: Step 4C — correct the supplied legal template version

##### Reproduced source boundary

- `SuppliedNoticeTemplateBaseline.V2Legal` contains the internal phrase
  `Workspace administrator:` in both bodies that are rendered for tenants.
- Step 4B now prevents those unsafe persisted drafts from being delivered, but
  it intentionally does not mutate immutable supplied templates, workspace
  versions, policies, or existing drafts.
- System and workspace template versions are append-only. Existing
  `NoticeDraft.WorkspaceNoticeTemplateVersionId` and
  `RenderedNotice.WorkspaceNoticeTemplateVersionId` references preserve the
  content used at draft/render time.
- Fresh account bootstrap initially creates v1 workspace copies in the
  transaction-bound PostgreSQL bootstrap function and then replaces only the
  two legal copies with the latest supplied legal system versions. Its count
  assertions are currently named against `V2Legal`, even though its translated
  latest-per-key query already selects the latest immutable version.

##### Allowed source files

- `RentalCommand.Data/Notifications/SuppliedNoticeTemplateBaseline.cs`
- `RentalCommand.Data/Auth/AccountSecurityCommandHandlers.cs`
- `RentalCommand.Data/Migrations/20260725090000_AddSafeSuppliedLegalNoticeTemplateV3.cs` (new)

##### Allowed test files

- `RentalCommand.Data.Tests/NotificationFoundationModelTests.cs`
- `RentalCommand.Data.Tests/FoundationBaselinePostgreSqlTests.cs`
- `RentalCommand.IntegrationTests/NotificationSettingsPostgreSqlTests.cs`
- `RentalCommand.IntegrationTests/SuppliedNoticeTemplateBaselineTests.cs`
- `RentalCommand.IntegrationTests/SuppliedLegalNoticeTemplateV3MigrationTests.cs` (new isolated migration harness)

No mobile presentation, notice-delivery handler, draft-rendering SQL, policy
editor, authorization/RLS policy, notification channel, recipient, outbox, or
general seeding redesign is in this boundary.

##### Required change

- Append two fixed-id v3 legal system-template definitions for
  `lease-non-renewal` and `late-rent-late-fee`. The subjects are byte-for-byte
  equal to v2. The non-renewal body is byte-for-byte equal to v2 after removing
  this entire final paragraph and its preceding blank line:
  `Workspace administrator: before sending, review and adapt this starting copy
  for every state and local timing, delivery, and content requirement. This
  jurisdiction-neutral template is not a determination that the notice is
  legally sufficient.` The late-rent body is byte-for-byte equal to v2 after
  replacing its final paragraph with only its existing first sentence:
  `Any future action will be taken only under the rental agreement and
  applicable requirements.` No other legal-copy edit is authorized. Do not edit
  v1 or v2 constants, IDs, timestamps, provenance, or bodies.
- Add an irreversible migration that inserts the two fixed v3 system rows, then
  performs one set-based PostgreSQL upgrade statement inside the migration
  transaction:
  - select only Draft legal policies currently bound to an uncustomized,
    unreviewed v2 workspace copy whose subject/body/system key/version and
    supplied-system reference still match the immutable v2 system row;
  - append one uncustomized v3 workspace version per qualifying portfolio/key;
  - rebind only those selected policies to the new v3 workspace rows and reset
    no unrelated policy field;
  - leave customized workspace versions, jurisdiction-reviewed copies/policies,
    Auto/Off policies, unbound historical workspace versions, existing
    `NoticeDraft` rows, rendered notices, evidence, conversations, messages, and
    outbox rows untouched.
- Keep fresh registration's one-query latest-per-key selection and
  transaction-bound replacement/deletion behavior. Replace v2-named count
  assertions/messages with a latest-legal definition so the code does not
  silently depend on v2 after v3 is published. Do not introduce concurrent
  `DbContext` access or client-side filtering/grouping.
- Keep `BuildWorkspaceV1CopyCommand` and the transaction-bound database
  bootstrap function on immutable v1; fresh account bootstrap remains the
  explicit in-transaction bridge from v1 to the latest legal version.
- Add `SuppliedLegalNoticeTemplateV3MigrationTests` as a dedicated PostgreSQL
  Testcontainers fixture, independent of the fully migrated shared fixture. Its
  setup creates a new database, calls EF `IMigrator.MigrateAsync` only through
  `20260724150000_AddEffectiveCapabilityScopeAuthority`, seeds one user and
  separate portfolios for each complete candidate/exclusion variant so the
  unique `(PortfolioId, AutomationKey)` policy key is preserved, and then calls
  `IMigrator.MigrateAsync("20260725090000_AddSafeSuppliedLegalNoticeTemplateV3")`.
  The single test
  `Migration_UpgradesOnlyExactUnreviewedDraftV2Bindings_AndPreservesHistory`
  includes: one qualifying Draft binding for each legal key; customized,
  jurisdiction-reviewed, Auto, and Off exclusions; one unbound historical v2
  workspace version; a NoticeDraft and RenderedNotice referencing v2; and
  baseline frozen content. It verifies all selected/excluded IDs and content
  after migration. A second test,
  `Migration_FailureRollsBackSystemWorkspaceAndPolicyChanges`, deliberately
  makes one qualifying v3 workspace insert violate the existing unique
  portfolio/key/version constraint, applies only the v3 migration, asserts the
  migration throws, and verifies that neither v3 system row, no other v3
  workspace row, and no policy rebind committed. This proves the migration
  operations participate in one transaction rather than merely inspecting SQL.

##### Acceptance criteria

- **NOTICE-SEED-01:** v3 uses fixed unique IDs 8 and 9, version 3, the same two
  legal system keys, fixed UTC publication metadata, required merge fields, and
  full subject/body equality to the exact v2-minus-quoted-text transformation
  above. It contains neither `Workspace administrator:` nor the removed
  legal-sufficiency meta-comment.
- **NOTICE-SEED-02:** v1 and v2 definitions remain byte-for-byte unchanged; the
  migration Down path remains intentionally unsupported because historical
  rows may reference every version.
- **NOTICE-SEED-03:** Migration operation inspection proves two deterministic
  system inserts plus one set-based upgrade statement; the SQL filters by
  Draft, Legal, uncustomized, unreviewed, exact v2 supplied content/reference,
  and updates policies by the selected portfolio/key/current-template identity.
  It contains no per-row application loop and no broad delete/update.
- **NOTICE-SEED-04:** The dedicated pre-v3 PostgreSQL harness contains v3 as
  latest for both legal keys after applying only the v3 migration. Both
  qualifying uncustomized Draft bindings point to new v3 workspace rows, while
  customized, reviewed, Auto, Off, and unbound historical rows retain their
  original workspace-template IDs. Existing drafts and rendered evidence retain
  their original template IDs and frozen content. The deliberate unique-key
  failure proves the system inserts, workspace inserts, and policy updates roll
  back together.
- **NOTICE-SEED-05:** Fresh account bootstrap creates exactly five workspace
  templates and five Draft policies, uses v3 for the two legal bindings, has no
  older legal workspace copy left behind, and retains its existing single
  explicit transaction, latest-per-key DB query, RLS authority, and atomic audit
  behavior.
- **NOTICE-SEED-06:** Step 4B continues to block already-persisted unsafe v2
  drafts; Step 4C does not rewrite them or infer that new supplied copy is
  legally sufficient for a jurisdiction.

##### Targeted commands

Run serially with no parallel build/test:

```bash
dotnet test RentalCommand.Data.Tests/RentalCommand.Data.Tests.csproj \
  --filter 'FullyQualifiedName~NotificationFoundationModelTests|FullyQualifiedName~FoundationBaselinePostgreSqlTests'
dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~NotificationSettingsPostgreSqlTests|FullyQualifiedName~SuppliedNoticeTemplateBaselineTests|FullyQualifiedName~SuppliedLegalNoticeTemplateV3MigrationTests'
git diff --check
```

##### Relevance gate

A read-only relevance review checks only the eight allowed files,
NOTICE-SEED-01 through NOTICE-SEED-06, the generated/inspected SQL shape, and
the absence of historical rewrites, customized/reviewed/active policy changes,
authorization changes, client shaping, or unrelated notification work.

No general notification workflow redesign is authorized by this audit.

### Step 5 — Deposits performance

- Measure authorization, count, and page query timings and capture generated SQL.
- Use safe query-plan evidence before changing authorization shape, a view, or an
  index.
- Retain one DB-side translated count and deterministic page query; no client
  shaping or N+1 repair.
- Evidence: 32–34, approximately 17 seconds for a small first page.

### Step 6 — Portfolio rent consistency

- Trace scheduled rent and recurring monthly rent to their authoritative
  definitions.
- Prove whether the displayed contradiction is seed data, a lifecycle projection,
  a query, or a label defect.
- Correct the authority or label; never reconcile figures in Flutter.
- Evidence: 25.

### Step 7 — contextual loading and dense-list polish

- Replace audited blank initial waits with contextual Material 3 placeholders.
- Preserve explicit server paging and use compact inline progress for loading more.
- Allow decisive row context to wrap or occupy a second line where truncation
  makes records indistinguishable.
- Evidence: 05, 13, 24, 29–34, 55.

**Terminal checkpoint:** Tasks 7A, 7B, and 7C each received `RELEVANCE PASS`.
Automated acceptance now passes 45/45, and targeted analyze returns exactly
`No issues found!`. The earlier API outage remains historical evidence:
`/health` returned `HTTP/2 502` at `2026-07-25 16:05:40 GMT`. The stack was
subsequently restored; `/health` returned HTTP 200, and exact runtime metadata
identified SHA `51c2f88b`.

Step 5's pre-fix deployed baseline remains `FAIL`: cold load took 16.579
seconds, first refresh took 15.557 seconds, the next valid sample exceeded 30
seconds and failed closed, and no request returned 401. The exact slow page
statement was then reproduced at 10.161 seconds. The implemented
page-before-display-join shape passed its SQL and PostgreSQL regression tests,
and replaying the fixed shape against the live Azure database completed in
600.555 ms with about 87% fewer shared-buffer hits. Independent source review
found no blocking issue. Commit `e95b1435` removed the former 15–30 second
behavior but narrowly missed the strict app p95 and cold-content gates. Commit
`5e437dc2` then constrained both display-view lookups to each paged deposit
with correlated `JOIN LATERAL` queries. Exact emulator reproof returned
`UI PROOF PASS`: cold content was populated by 2.776 seconds, app refresh
p95/max was 1.514 seconds, API p95/max was 417.3813 ms, and no 401, replay, or
timeout occurred. Step 5 is complete subject only to its recorded deployment
provenance limitation. Step 6 returned `UI PROOF PASS`: `Collection Rate` was
`74.3%` with
`$15,000.00 of $20,175.00`, and `Signed lease rent` was `$1,050.00` with
`currently governing`.

Step 7 real-device proof found the TSK-752 work-order defect; the defect was
fixed, work-order proof returned `UI PROOF PASS`, and TSK-752 is `Done and
verified`. A later artifact-content audit rejected the original
`inspections-loading`, `activity-loading`, and `ledger-loading` pairs because
they showed final populated lists, not pending states. Fresh fixed-build proof
now covers the work-order, inspections, and deposits contextual loaders;
activity append with retained rows; and the audited
inspection/activity/lease wrapping.

The first Activity rerun exposed a real empty-state flash while `/audit` was
pending. Commit `f5c8ae7c` fixes that first frame, passes the expanded 45-test
suite, and was installed in place with preserved authentication. Two bounded
reruns visually captured the corrected Activity loader during 562 ms and
543 ms requests, but UIAutomator finished after each request, so strict
PNG/XML pairing remains partial. Ledger initial loading has the same
fast-request pairing limitation. The first Ledger append capture proved
server paging and retained rows but exposed that the footer spinner was built
whenever `hasMore` was true, even while idle. Commit `ef3f7d23` restricts the
footer to `loadingMore` and adds a failing-before/passing-after regression.
Exact-APK emulator reproof returned `UI PROOF PASS`: settled rows have no
progress, `skip=40` and `skip=80` fire naturally, active paging retains rows
and shows compact progress, and appended rows remain scrollable. Deposits
append is `FIXTURE BLOCKED` because the fixture exposes only 15 final rows and
no `Load more`; no data was mutated to manufacture that state. Step 7 remains
incomplete only for the unmatched fast initial-loader semantics and the
fixture-blocked Deposits append branch. This roadmap currently ends at Step 7.

## Global exclusions

- No protected stack/database reset or broad reseed.
- No replacement of correct native Material date/time pickers.
- No redesign of tenant/owner role products without new evidence.
- No client-side data shaping to hide server defects.
- No unrelated visual rebrand.
- No parallel heavy builds/tests.
