# TSK-750 mobile hub selector proof

## Verdict

UI PROOF PASS. On exact SHA `1fb889ca297280f92287a93246c2d7bd80deb528`,
the six-destination Rentals hub uses one explicit full-width selector, exposes
all six authorized destinations together without clipped labels, changes the
selected root content, and restores Units through the same selector path. The
three-destination Inbox hub retains three directly tappable segments and has no
anchored selector.

## Target

- Commit/APK source SHA: `1fb889ca297280f92287a93246c2d7bd80deb528`
- APK ABI: `android-x64`
- Device: Android emulator `emulator-5554`
- Viewport: `1080x2400` at `420dpi`
- Package: `com.rentalcommand.rental_command.dev`
- Backend: protected Azure Rental Command preview stack
- Date: 2026-07-25

No source build, source edit, data reset, APK install, deployment, or protected-stack
mutation was performed by the UI verifier.

## Supported flow exercised

1. Returned from Unit Main detail to the Rentals Units root.
2. Captured the closed Units selector.
3. Tapped the full-width selector and captured the open menu.
4. Selected Applications and captured the closed Applications selector and changed
   Applications root content.
5. Opened the selector again, selected Units, and verified the Units root was restored.
6. Opened Inbox and captured its Messages, Notifications, and Activity History segments.
7. Tapped Notifications and Activity History directly and verified each destination's
   header and root content changed without opening a selector.

## Rentals selector findings

- Closed Units state shows the full `Units` label and destination icon without clipping.
- Open state shows all authorized destinations together:
  `Properties`, `Owners`, `Units`, `Tenants`, `Leases`, and `Applications`.
- `Units` is marked with the selected-state check in the open menu.
- No menu label is clipped at the `1080x2400` target.
- Selecting `Applications` closes the menu, changes the full selector label to
  `Applications`, changes the header to `Applications`, and renders Applications root
  content (`No applications yet.`).
- Selecting `Units` again restores the Units header and unit-list root.

## Inbox findings

- Inbox renders `Messages`, `Notifications`, and `Activity history` together as three
  direct segments.
- No anchored selector is visible.
- Directly tapping `Notifications` changes the header to
  `Notifications — Unread alerts and system updates`.
- Directly tapping `Activity history` changes the header to
  `Activity history — Mobile audit trail and record changes`.

## Bounds and overlap

The compact closed FAB is `[891,1967][1038,2114]` in the captured hierarchies.
The Rentals selector occupies the full-width frame near `[42,503][1038,629]`;
the open six-item menu remains above the FAB. Their vertical ranges do not
intersect. The Inbox segment frame occupies the same upper hub-navigation band
and likewise has no intersection with the FAB.

| State | Selector/menu or segments | FAB | Intersection | Result |
| --- | --- | --- | --- | --- |
| Rentals Units closed | selector `[42,503][1038,629]` | `[891,1967][1038,2114]` | none | PASS |
| Rentals selector open | selector plus six-item menu, all above `y=1400` | `[891,1967][1038,2114]` | none | PASS |
| Rentals Applications closed | selector `[42,503][1038,629]` | `[891,1967][1038,2114]` | none | PASS |
| Inbox three segments | segment frame `[42,503][1038,629]` | `[891,1967][1038,2114]` | none | PASS |

## Evidence

- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/rentals-units-closed.png`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/rentals-units-closed.xml`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/rentals-selector-open.png`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/rentals-selector-open.xml`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/rentals-applications-closed.png`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/rentals-applications-closed.xml`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/inbox-three-segments.png`
- `Docs/Reviews/artifacts/tsk-750/post-hub-selector/inbox-three-segments.xml`
