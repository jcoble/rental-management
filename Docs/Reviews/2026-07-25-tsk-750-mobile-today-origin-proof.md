# TSK-750 Step 2C Android Today-origin proof

## Verdict

**UI PROOF PASS — TODAYNAV-06**

The exact-SHA x86_64 APK passed the supported Today Work Orders drill-in and
Back flow on the sole Azure Android emulator. A Today queue row opened its
loaded unit-aware work-order detail without changing the shell origin to Work.
The detail Back arrow returned to Today with the same Work Orders heading and
row at identical physical bounds, proving that the prior Today scroll position
remained mounted underneath.

## Proof target

- Source SHA: `45e0f224959286303cb2b03729fcb86053e1bb0b`
- APK:
  `/Users/blackcolours/.codex/remote-artifacts/tsk750-today-origin-x64-45e0f224/app-dev-debug.apk`
- APK SHA-256:
  `cc03ae991fc86af48dcc45eb2656c0d066e5645c0eef6476e0f95c54bbc27503`
- APK ABI evidence: `lib/x86_64/libflutter.so`
- Device: Azure `emulator-5554`, `sdk_gphone64_x86_64`
- Target: physical `1080x2400`, density `420`
- Package/activity:
  `com.rentalcommand.rental_command.dev/com.rentalcommand.rental_command.MainActivity`
- Backend/session: existing protected `rc-preview-rental-*` stack and existing
  authenticated example-data session

The local APK hash and transferred remote APK hash matched before installation.
Installation used data-preserving replacement under the shared Azure heavy lock:

```bash
sudo flock -w 1800 /srv/dev-stacks/.locks/azure-heavy.lock \
  sudo -u azureuser /bin/bash -lc \
  'source /opt/runner-tools/mobile-env.sh >/dev/null 2>&1; \
   adb -s emulator-5554 install -r <exact-apk>'
```

`adb install -r` returned `Success`. No uninstall, data clear, reset/reseed,
source build, source edit, or protected-service mutation was performed.

## Supported flow

1. Launched the installed app and selected Today at `(108,2240)`.
2. Scrolled the Today `CustomScrollView` to Work Orders using three upward
   swipes from `(540,1900)` to `(540,700)`, each over 500 ms.
3. Established `Roof shingle repair` as the origin row and captured Today with
   its Work Orders section and selected Today bottom destination.
4. Tapped the row at `(500,975)` and waited ten seconds for the loader and
   destination content to settle.
5. Confirmed the loaded destination was unit-aware:
   - Unit A / Maple Ridge Duplex
   - Maintenance
   - Work orders
   - Work Order / Roof shingle repair
6. Used the Work Order detail Back action at `(74,666)`, inside
   `[11,603][137,729]`, and waited five seconds.
7. Confirmed the app returned directly to the same Today Work Orders position,
   with Today selected and Work unselected.

The pushed detail covers the shell bottom bar while open. The underlying
destination is proven to remain Today because Back returns directly to the
identical Today hierarchy and scroll position instead of showing the Work root.

## Origin and Back bounds

All bounds are physical screen pixels from the matching hierarchy XML.

| Element | Origin bounds | Back bounds | Delta |
| --- | --- | --- | --- |
| Rental Command heading | `[42,382][497,456]` | `[42,382][497,456]` | `0 px` |
| No recent messages | `[53,493][1028,654]` | `[53,493][1028,654]` | `0 px` |
| Work Orders heading | `[53,717][1028,843]` | `[53,717][1028,843]` | `0 px` |
| View all | `[825,717][1028,843]` | `[825,717][1028,843]` | `0 px` |
| Roof shingle repair | `[53,864][1028,1085]` | `[53,864][1028,1085]` | `0 px` |
| Today destination | `[0,2168][216,2328]` | `[0,2168][216,2328]` | `0 px` |
| Work destination | `[648,2168][864,2328]` | `[648,2168][864,2328]` | `0 px` |

The origin and Back XML files are byte-identical, both with SHA-256
`253867de9cea17bf0dbff50bd2ec2d8c5656e8bed2982a9de62ed322ac07a644`.
Both screenshots visibly show the purple selected treatment on Today and no
selected treatment on Work.

## Detail evidence

- Outer unit heading `Unit A / Maple Ridge Duplex`:
  `[189,152][471,267]`
- Unit section selector `Maintenance`: `[42,304][1038,430]`
- Nested surface `Work orders`: `[0,456][1080,2400]`
- Detail heading `Work Order`: `[189,630][489,703]`
- Detail title `Roof shingle repair`: `[42,782][1038,866]`

The app PID was `10013` before and after the ten-second detail load and before
and after Back. `MainActivity` remained focused throughout. There was no blank
screen, loading hang, activity restart, or Work-root interstitial.

## Artifacts

Each PNG is `1080x2400`; each matching XML parsed successfully.

- `Docs/Reviews/artifacts/tsk-750/post-today-origin/today-work-order-origin.png`
- `Docs/Reviews/artifacts/tsk-750/post-today-origin/today-work-order-origin.xml`
- `Docs/Reviews/artifacts/tsk-750/post-today-origin/today-work-order-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-today-origin/today-work-order-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-today-origin/today-work-order-back.png`
- `Docs/Reviews/artifacts/tsk-750/post-today-origin/today-work-order-back.xml`

## Cleanup and final state

The unique remote handoff directory
`/srv/agent-handoffs/tsk750-today-origin-proof-45e0f224-20260725T1625Z`
was removed after the successful install. The emulator app remained alive and
focused at PID `10013`. The protected web, API, gateway, engine, and PostgreSQL
containers remained running; API, engine, and PostgreSQL remained healthy.
