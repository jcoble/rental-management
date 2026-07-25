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

## Active contract: Step 1 — stop the global FAB from blocking the UI

### Evidence

The closed global `Scan / Add` extended FAB overlaps primary actions and content
on Appointment Schedule/Contact, Inspection detail, Notice detail, payment
correction, and Record receipt. UI hierarchy evidence proves overlapping bounds,
not merely visual proximity:

- `11-appointment-edit-schedule.png`
- `12-appointment-edit-contact.png`
- `15-inspection-detail.png`
- `20-work-notices.png`
- `45-correct-payment-sheet.png`
- `48-record-receipt-sheet.png`

### Allowed source files

- `mobile/lib/features/home/mobile_quick_action_fab.dart`

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
- **FAB-06:** Emulator re-proof shows no FAB overlap on Appointment Save Changes,
  Inspection Complete, Notice SMS, Append correction, or Record receipt.

### Targeted commands

Run serially:

```bash
dart format mobile/lib/features/home/mobile_quick_action_fab.dart \
  mobile/test/mobile_quick_action_fab_test.dart
flutter test mobile/test/mobile_quick_action_fab_test.dart
flutter analyze mobile/lib/features/home/mobile_quick_action_fab.dart \
  mobile/test/mobile_quick_action_fab_test.dart
git diff --check
```

The Flutter test/analyze commands run from `mobile/` with paths adjusted to
`test/mobile_quick_action_fab_test.dart` and
`lib/features/home/mobile_quick_action_fab.dart` as required by the tool.

### Relevance gate

After implementation, a read-only reviewer checks only:

1. the active file diff;
2. FAB-01 through FAB-05 test evidence;
3. absence of authorization/navigation changes;
4. whether any new file or behavior exceeds this contract.

Any out-of-bound discovery is recorded for a pending step; it is not absorbed.

### Emulator proof gate

The sole emulator tester re-captures these exact changed states under:

`Docs/Reviews/artifacts/tsk-750/post-fab/`

- `appointment-schedule.png`
- `appointment-contact.png`
- `inspection-detail.png`
- `notice-detail.png`
- `payment-correction.png`
- `record-receipt.png`
- matching UI hierarchy XML for each screen

The proof report is:

`Docs/Reviews/2026-07-25-tsk-750-mobile-fab-proof.md`

Step 1 completes only when FAB-06 is evidenced on the Azure emulator.

## Pending roadmap boundaries

These are evidence-backed but inactive. Each receives its own exact-file contract
and relevance gate only after the active step closes.

### Step 2 — visible, unit-centered navigation

- Replace clipped/mystery horizontal navigation in Rentals and the Unit Command
  Center with an explicit section affordance.
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
