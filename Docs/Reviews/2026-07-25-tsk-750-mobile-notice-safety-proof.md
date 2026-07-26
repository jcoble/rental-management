# TSK-750 Step 4B Android notice-safety proof

## Verdict

**UI PROOF BLOCKED — NOTICE-SAFE-07's enabled-after-edit device branch is unproved**

The standalone unsafe Draft branch passes on the exact-SHA Azure emulator APK:
persisted brace residue and the internal `Workspace administrator:` instruction
produce plain-English correction guidance, and `Approve` is disabled.

The safe-edited review-sheet branch cannot be reached without first calling the
real scoped-generation endpoint and creating a persisted notice draft. The
proof lane was explicitly prohibited from mutating Azure data, so it did not
enter that flow and did not fabricate `safe-edited-notice-ready`.

## Proof target

- Source SHA: `51c2f88b32cb0443597fbce7732284c773afe621`
- Exact APK:
  `/Users/blackcolours/.codex/remote-artifacts/tsk750-empty-notice-x64-azureapi-rootcert-51c2f88b/app-dev-debug.apk`
- APK SHA-256:
  `6bffcfd135d2c099cbb97c4678267484b3f96f1587a10c48a7cf78c6852a8b03`
- APK signer SHA-256:
  `c3a34969aca92701c9e277591f220c6d547c4eb7198b05dcf2da3188fd8ff17e`
- APK ABI evidence: `lib/x86_64/libflutter.so`
- Compiled API:
  `https://rental-command.chimp-map.ts.net/api/v1`
- Device: Azure `emulator-5554`, `sdk_gphone64_x86_64`
- Target: physical `1080x2400`, density `420`
- Package/activity:
  `com.rentalcommand.rental_command.dev/com.rentalcommand.rental_command.MainActivity`
- Proof PID: `12997`

The APK was installed data-preservingly with `adb install -r` under the Azure
heavy-operation lock. Every proof interaction also ran under that lock.

## Passing unsafe-Draft evidence

The real persisted Draft for Sofia Rodriguez visibly contains multiple reserved
brace-delimited fragments, including:

- `Hello {Sofia Rodriguez},`
- `{$1,050}`
- `{Maple Ridge Duplex Unit A}`
- `{July 24, 2026}`

It also visibly contains the internal sentence beginning
`Workspace administrator:`.

The card renders the stable correction guidance:

`Review the notice and remove any unfinished merge fields or internal
instructions before sending.`

In the guidance capture, the combined Draft hierarchy is
`[42,666][1038,1573]`, and the disabled `Approve` control is
`[79,1394][854,1520]` with XML `enabled="false"`.

The separate blocked-state capture also records `Approve` at
`[79,666][854,789]` with XML `enabled="false"`.

No channel was changed. `Approve` and `Dismiss` were not tapped.

## Safe-edit device blocker

Source tracing showed the only real-app route to the editable review sheet:

1. `showCreateTenantNoticeFlow(...)`
2. choose a notice type
3. call `noticesRepositoryProvider.generateScoped(...)`
4. persist a new Draft through the Azure API
5. open `_ReviewNoticeSheet(drafts: drafts)`

There is no route from an existing standalone Draft card into that editable
sheet. Entering the only available path would therefore mutate protected Azure
data before any local controller edit occurred.

The proof instructions prohibit generating or persisting a Draft. They also
prohibit approving, sending, dismissing, updating, or saving a safe edit.
Accordingly:

- no generation action was invoked;
- no review-sheet controller was edited;
- no notice was approved, sent, dismissed, or updated;
- no safe edit was saved;
- `safe-edited-notice-ready.png` / `.xml` was not fabricated.

`mobile/test/notice_review_edit_test.dart` contains the focused widget test
`unsafe review copy blocks send until corrected`, implemented through
`ReviewNoticeSheetTestHarness`. That is static/automated presenter evidence
only. This emulator lane did not run builds or tests, and the harness is not a
substitute for the missing real-device review-sheet proof.

## Layout and stability

- Both unsafe-Draft PNGs are 1080x2400, and both matching hierarchy XML files
  parse.
- The brace residue, internal instruction, correction guidance, channel
  controls, and disabled `Approve` state render without clipping or overflow.
- The FAB does not obscure the correction guidance or disabled `Approve`
  control in the decisive guidance capture.
- No blank screen, loading hang, or crash occurred.
- PID remained `12997`, and the administrator session was restored after the
  combined Step 4 proof.

## Artifacts

Passing unsafe-Draft captures:

- `Docs/Reviews/artifacts/tsk-750/post-notice-safety/unsafe-notice-blocked.png`
- `Docs/Reviews/artifacts/tsk-750/post-notice-safety/unsafe-notice-blocked.xml`
- `Docs/Reviews/artifacts/tsk-750/post-notice-safety/unsafe-notice-guidance.png`
- `Docs/Reviews/artifacts/tsk-750/post-notice-safety/unsafe-notice-guidance.xml`

Unproved and intentionally absent:

- `Docs/Reviews/artifacts/tsk-750/post-notice-safety/safe-edited-notice-ready.png`
- `Docs/Reviews/artifacts/tsk-750/post-notice-safety/safe-edited-notice-ready.xml`

## Cleanup and preserved state

- The unique remote APK handoff directory was removed after installation.
- Existing package data was never uninstalled or cleared.
- No source edit, source build, test run, commit, reset, reseed, notice
  generation, approval, send, dismiss, update, or protected-service mutation
  was performed.
- The emulator app was left alive on Today at PID `12997`.
