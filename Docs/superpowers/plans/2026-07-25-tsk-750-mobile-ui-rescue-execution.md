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

## Active contract: Step 2A — make every hub section discoverable

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

## Pending roadmap boundaries

These are evidence-backed but inactive. Each receives its own exact-file contract
and relevance gate only after the active step closes.

### Step 2B — visible, unit-centered navigation

- Replace clipped/mystery horizontal navigation in the Unit Command Center with
  an explicit section affordance.
- Preserve unit/property context, destination restoration, and deep links.
- Restore Today as the Back destination for work orders opened from Today.
- Evidence: 00, 05–07, 42, 52, 53, 57.

### Step 3 — plain-English money and rental presentation

- Translate audited wire values and internal identifiers at presentation
  boundaries.
- Replace “allocated/open” accounting jargon with explicit paid/original/remaining
  labels.
- Make chart months readable and preserve distinguishing list context.
- Clarify the vendor work-count label according to its authoritative metric.
- Evidence: 06, 07, 14, 16, 24–26, 30, 31, 41, 42, 55.

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
