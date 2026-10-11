# TSK-750 Step 2B Android unit-selector proof

## Verdict

**UI PROOF PASS — UNITNAV-06**

The exact-SHA x86_64 APK passed the supported Unit Command Center flow on the
sole Azure Android emulator. The closed Summary selector, open six-section
selector with Summary checked, closed Money selector with money content, and
closed Documents & history selector with both Documents and History content
were all rendered at 1080x2400/420 dpi. Zero of six menu labels clipped, and the
FAB intersected zero selector or menu actions.

## Proof target

- Source SHA: `947af6f3e8e1232dc6ce9c9337c6ef9e066345c5`
- APK:
  `/Users/blackcolours/.codex/remote-artifacts/tsk750-unit-selector-x64-947af6f3/app-dev-debug.apk`
- APK SHA-256:
  `625d43ef11fed8c08e637a08d72a2f5c7916de05e6ceb3d51fa901fda1898671`
- APK ABI evidence: `lib/x86_64/libflutter.so`
- Device: Azure `emulator-5554`,
  `sdk_gphone64_x86_64`, Android emulator
- Target: physical `1080x2400`, density `420`
- Package/activity:
  `com.rentalcommand.rental_command.dev/com.rentalcommand.rental_command.MainActivity`
- Backend/session: existing protected `rc-preview-rental-*` stack and existing
  authenticated example-data session

The local APK hash and the transferred remote APK hash matched before install.
Installation used data-preserving replacement under the shared Azure heavy lock:

```bash
sudo flock -w 1800 /srv/dev-stacks/.locks/azure-heavy.lock \
  sudo -u azureuser /bin/bash -lc \
  'source /opt/runner-tools/mobile-env.sh >/dev/null 2>&1; \
   adb -s emulator-5554 install -r <exact-apk>'
```

`adb install -r` returned `Success`. No uninstall, data clear, database
reset/reseed, source build, source edit, or protected-service mutation was
performed.

## Supported flow and observations

1. Launched the existing activity after install and allowed restoration to
   settle. The existing session restored Unit Main on the persisted Money
   section.
2. Opened the unit selector and selected Summary. After a four-second stability
   wait, Summary content rendered and the selector was closed.
3. Opened the selector from Summary. All six destinations appeared together in
   order: Summary, Leasing, Tenant & lease, Money, Maintenance, and Documents &
   history. The Summary row displayed the selected-state check.
4. Selected Money. After a four-second stability wait, the selector read Money
   and the populated Tenant balance, Market rent, Tenant account, Payments, and
   Account activity content rendered.
5. Reopened the selector and selected Documents & history. After a four-second
   stability wait, the selector read Documents & history, and both Documents
   and History content rendered.

The application PID was `9374` before and after both measured section
transitions, the focused activity stayed `MainActivity`, and every post-switch
hierarchy contained the destination selector plus destination content. There
was no blank destination, loading hang, activity restart, or page-refresh-like
loss of the app process.

## Bounds and clipping/intersection measurements

All bounds are physical screen pixels from the matching UI hierarchy.

- Closed Summary selector: `[42,514][1038,640]`
- Open menu actions:
  - Summary: `[42,661][547,787]`
  - Leasing: `[42,787][547,913]`
  - Tenant & lease: `[42,913][547,1039]`
  - Money: `[42,1039][547,1165]`
  - Maintenance: `[42,1165][547,1291]`
  - Documents & history: `[42,1291][547,1417]`
- Closed Money selector in the retained scrolled position:
  `[42,367][1038,493]`
- Closed Documents & history selector: `[42,514][1038,640]`
- FAB (`Scan / Add`): `[891,1967][1038,2114]`

Clipping result: **0/6 menu labels clipped**. The open screenshot visibly
contains every full label with no ellipsis or cropped edge, and the matching
hierarchy exposes each complete label. Each menu action is `505x126` pixels.

Intersection result: **0/7 selector/menu actions intersect the FAB**. The open
menu right edge is `x=547` while the FAB left edge is `x=891`, leaving 344
pixels of horizontal clearance; the lowest menu action ends at `y=1417` while
the FAB starts at `y=1967`, leaving 550 pixels of vertical clearance. The
full-width closed selector ends at `y=640` (or `y=493` in the retained Money
scroll position), also outside the FAB rectangle.

## Artifacts

Each PNG is `1080x2400`; each matching XML parsed successfully.

- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-summary-closed.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-summary-closed.xml`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-selector-open.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-selector-open.xml`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-money-closed.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-money-closed.xml`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-documents-history-closed.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-selector/unit-documents-history-closed.xml`

## Cleanup and final state

The unique remote handoff directory
`/srv/agent-handoffs/tsk750-unit-selector-proof-947af6f3-20260725T1605Z`
was removed after the successful install. The emulator app remained alive and
focused at PID `9374`. The protected web, API, gateway, engine, and PostgreSQL
containers remained running; API, engine, and PostgreSQL remained healthy.
