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

#### Active contract: Step 3A — make Unit summary and money language understandable

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

##### Allowed test files

- `mobile/test/plain_english_labels_test.dart` (new)
- `mobile/test/unit_money_contract_test.dart`

No repository, DTO, API, database, paging, navigation, authorization, form,
write path, or non-Unit screen is in this boundary.

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

##### Targeted commands

Run serially from `mobile/`:

```bash
dart format lib/core/presentation/plain_english_labels.dart \
  lib/features/units/unit_command_center_screen.dart \
  test/plain_english_labels_test.dart \
  test/unit_money_contract_test.dart
flutter test test/plain_english_labels_test.dart \
  test/unit_money_contract_test.dart \
  test/unit_navigation_test.dart
flutter analyze lib/core/presentation/plain_english_labels.dart \
  lib/features/units/unit_command_center_screen.dart \
  test/plain_english_labels_test.dart \
  test/unit_money_contract_test.dart
git diff --check
```

##### Relevance gate

After implementation, a read-only reviewer checks only:

1. the four allowed-file diffs;
2. UNITCOPY-01 through UNITCOPY-06 evidence;
3. preservation of wire values, repositories, independent server paging,
   payment-detail actions, navigation, and authorization;
4. absence of data, backend, form, or other roadmap changes.

##### Emulator proof gate

The sole emulator tester captures PNG and matching hierarchy XML under:

`Docs/Reviews/artifacts/tsk-750/post-unit-copy/`

- `unit-summary-plain-english`
- `unit-money-plain-english`
- `unit-money-payment-detail`

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-unit-copy-proof.md`

### Step 4 — honest empty states and legal-notice safety

- Make Automations and Messages empty-state instructions match real available
  actions.
- Reproduce the legal-notice placeholder/admin-instruction source boundary.
- Prevent approval/sending when unresolved merge fields or internal instructions
  remain.
- Evidence: 18, 20, 21.
- No general notification workflow redesign is authorized by this audit.

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

## Global exclusions

- No protected stack/database reset or broad reseed.
- No replacement of correct native Material date/time pickers.
- No redesign of tenant/owner role products without new evidence.
- No client-side data shaping to hide server defects.
- No unrelated visual rebrand.
- No parallel heavy builds/tests.
