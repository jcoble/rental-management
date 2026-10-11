# TSK-750 mobile FAB hidden-state proof

## Verdict

UI PROOF PASS. On exact SHA `ea5da553a31b65e067130472b90cc8a0d391e031`,
the closed `Scan / Add` FAB is absent from all five approved form/action states
where it previously conflicted with a primary action. It remains present on the
approved Notice state and an ordinary Unit Money page.

## Target

- Commit/APK source SHA: `ea5da553a31b65e067130472b90cc8a0d391e031`
- APK ABI used for product proof: `android-x64`
- Device: Android emulator `emulator-5554`
- Viewport: `1080x2400` at `420dpi`
- Package: `com.rentalcommand.rental_command.dev`
- Backend: protected Azure Rental Command preview stack
- Date: 2026-07-25

No source build, source edit, data reset, APK install, deployment, or protected-stack
mutation was performed by the UI verifier.

## Supported flow exercised

1. Captured the ordinary Rentals > Unit Main > Money page.
2. Opened the first July receipt, then Correct payment.
3. Opened the FAB from the receipt detail and selected Record receipt.
4. Opened Work > Inspections > the active Dublin Single Family inspection.
5. Opened Work > Calendar > Roofing crew access > Edit > Schedule > Contact.
6. Opened Work > Notices and scrolled the first late-rent draft to SMS, Approve,
   and Dismiss.
7. Captured a fresh emulator screenshot and matching `uiautomator` hierarchy XML
   at every approved state.

## Bounds and intersections

For hidden states, no `Scan / Add` node exists in the fresh hierarchy, so its
rectangle is the empty set and the intersection with the primary action is zero.
For visible states, rectangle intersection was checked from the XML bounds.

| Approved state | Primary/action bounds | FAB state and bounds | Intersection | Result |
| --- | --- | --- | --- | --- |
| Appointment Schedule | `Next` `[399,1977][1028,2103]` | absent | none | PASS |
| Appointment Contact | `Save Changes` `[399,1977][1028,2103]` | absent | none | PASS |
| Active Inspection Detail | `Complete` `[42,1983][1038,2124]` | absent | none | PASS |
| Payment Correction | `Append correction` `[53,1967][1028,2093]` | absent | none | PASS |
| Record Receipt | `Record receipt` `[53,1967][1028,2093]` | absent | none | PASS |
| Notice Detail | `SMS` `[615,1307][854,1433]`; `Approve` `[79,1464][854,1590]`; `Dismiss` `[875,1464][1001,1590]` | present `[891,1967][1038,2114]` | none | PASS |
| Ordinary Unit Money page | no conflicting bottom primary action | present `[891,1967][1038,2114]` | not applicable | PASS |

The Schedule step's actual primary label is `Next`; `Save Changes` appears on the
Contact step.

## Product evidence

- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/ordinary-page.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/ordinary-page.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/appointment-schedule.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/appointment-schedule.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/appointment-contact.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/appointment-contact.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/inspection-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/inspection-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/notice-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/notice-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/payment-correction.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/payment-correction.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/record-receipt.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/record-receipt.xml`

## Diagnostic history excluded from the verdict

The first APK supplied for the same SHA contained only
`lib/arm64-v8a/libflutter.so` and could not launch on the x86_64 emulator. Its
`UnsatisfiedLinkError` and crash-dialog captures are retained as diagnostic
history:

- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/launch-crash-dialog.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/launch-crash-dialog.xml`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/launch-crash.png`
- `Docs/Reviews/artifacts/tsk-750/post-fab-hidden/launch-crash.xml`

That packaging mismatch was corrected by reinstalling an android-x64 APK of the
same exact SHA before this product proof. It is not counted as a product acceptance
failure.
