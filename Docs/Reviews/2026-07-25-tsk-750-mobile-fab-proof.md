# TSK-750 mobile FAB proof

## Verdict

UI DEFECT. FAB-06 fails on five of the six approved mobile surfaces. The compact closed
`Scan / Add` FAB is visually reduced to a square icon-only control, but its clickable
rectangle still intersects the primary action rectangle on Appointment Schedule,
Appointment Contact, Inspection Detail, Payment Correction, and Record Receipt.
Notice Detail is the only approved surface without an intersection.

Verified defect task: `TSK-751` — `[TSK-751] Verified UI defect: compact FAB overlaps
primary mobile actions`.

## Target

- Commit/APK source SHA: `f556c901e0d519ba27bea9b9aee7c7924a18735f`
- Device: Android emulator `emulator-5554`
- Viewport: `1080x2400` at `420dpi`
- Package: `com.rentalcommand.rental_command.dev`
- Backend: protected Azure Rental Command preview stack
- Date: 2026-07-25

No source build, source edit, data reset, APK install, deployment, or protected-stack
mutation was performed during this proof run.

## Supported flow exercised

1. Work > Calendar > Roofing crew access > Edit > Schedule > Contact.
2. Work > Inspections > Dublin Single Family scheduled inspection.
3. Work tab strip > Notices > first late-rent draft, scrolled to SMS and Approve.
4. Rentals > Unit Main > Money > first July receipt > Correct payment.
5. Expanded the compact FAB and selected Record receipt.
6. Captured a fresh emulator screenshot and matching `uiautomator` hierarchy XML at
   each approved state.

The expanded FAB retained the authorized actions `Scan / Add`, `Record receipt`,
`Record`, and `Assistant`; `Close` returned it to the compact icon-only state.

## Findings

The compact closed FAB is consistently `[891,1967][1038,2114]`, which is `147x147`.
It has no visible label and retains the `Scan / Add` accessibility description.

| Approved state | Primary action bounds | FAB bounds | Intersection | Result |
| --- | --- | --- | --- | --- |
| Appointment Schedule | `Next` `[399,1977][1028,2103]` | `[891,1967][1038,2114]` | `137x126` | FAIL |
| Appointment Contact | `Save Changes` `[399,1977][1028,2103]` | `[891,1967][1038,2114]` | `137x126` | FAIL |
| Inspection Detail | `Complete` `[42,1983][1038,2124]` | `[891,1967][1038,2114]` | `147x131` | FAIL |
| Notice Detail | `SMS` `[615,1396][854,1522]`; `Approve` `[79,1553][854,1679]`; `Dismiss` `[875,1553][1001,1679]` | `[891,1967][1038,2114]` | none | PASS |
| Payment Correction | `Append correction` `[53,1967][1028,2093]` | `[891,1967][1038,2114]` | `137x126` | FAIL |
| Record Receipt | `Record receipt` `[53,1967][1028,2093]` | `[891,1967][1038,2114]` | `137x126` | FAIL |

The Schedule step's actual primary label is `Next`; `Save Changes` appears on the
Contact step. The supplied artifact names are preserved exactly.

## Evidence

- `Docs/Reviews/artifacts/tsk-750/post-fab/appointment-schedule.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab/appointment-schedule.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab/appointment-contact.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab/appointment-contact.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab/inspection-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab/inspection-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab/notice-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab/notice-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab/payment-correction.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab/payment-correction.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab/record-receipt.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab/record-receipt.xml`

## Narrow reproduction

Open any failing approved state on the 1080x2400 emulator with the FAB closed. Dump
the UI hierarchy. The primary action's right edge reaches `x=1028` while the FAB begins
at `x=891`, and both occupy the same vertical band near `y=1967..2114`. The resulting
clickable-region intersection is visible in the screenshot and deterministic in the XML.

## Acceptance impact

FAB-06 is not satisfied. The compact treatment meets the closed shape/label expectation,
but it does not remove the FAB from the primary actions' hit boxes on five supported
screens. The smallest follow-up is to change placement or visibility behavior so the
closed FAB rectangle no longer intersects those bottom actions, then repeat these same
six emulator captures.
