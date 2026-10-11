# TSK-750 Step 3A Android unit-copy proof

## Verdict

**UI PROOF PASS — UNITCOPY-07 and UNITCOPY-08 pass on the corrected exact-SHA APK**

The supported Unit Summary, Unit Money, payment Receipt, and correction-sheet
flow passed on the real Azure Android emulator. Receipt copy is now plain
English, its reference labels are `Account number` and `Lease number`, and the
correction sheet opens with its fields and action intact. The correction was
not submitted.

## Final proof target

- Source SHA: `e14ab342c3c8eb688ccb4ff168b679661a075d2f`
- Exact APK:
  `/Users/blackcolours/.codex/remote-artifacts/tsk750-payment-copy-x64-azureapi-rootcert-e14ab342/app-dev-debug.apk`
- APK SHA-256:
  `62eaf29947e84ddc7cf035410de4ee0637e45840b5bcfd895b3f549914566fb0`
- APK signer SHA-256:
  `c3a34969aca92701c9e277591f220c6d547c4eb7198b05dcf2da3188fd8ff17e`
- APK ABI evidence: `lib/x86_64/libflutter.so`
- Compiled API:
  `https://redacted-host.example.invalid/api/v1`
- Device: Azure `emulator-5554`, `sdk_gphone64_x86_64`
- Target: physical `1080x2400`, density `420`
- Package/activity:
  `com.rentalcommand.rental_command.dev/com.rentalcommand.rental_command.MainActivity`
- Proof PID: `11621`

The worktree HEAD and requested source SHA matched. The APK hash was verified
locally and after transfer, `apksigner` reported the expected signer, and the
archive contained the required x86_64 Flutter library.

Installation used data-preserving replacement under the shared Azure heavy
lock:

```bash
sudo flock -w 1800 /srv/dev-stacks/.locks/azure-heavy.lock \
  sudo -u azureuser /bin/bash -lc \
  'source /opt/runner-tools/mobile-env.sh >/dev/null 2>&1; \
   adb -s emulator-5554 install -r <exact-apk>'
```

`adb install -r` returned `Success`. No uninstall, package-data clear, reset,
or reseed occurred. The existing authenticated example-workspace session
persisted, so no login action was required.

## Recovered blocker history

The earlier install and copy blockers remain recorded for provenance:

1. APK SHA-256
   `ea7ae8fe28fc1066594d94bd680ad343a8807ade46706296e680df933bf8575f`
   used signer
   `5451eca4a2d5c4031f46cfac734cbf6dd124fc6cfcada343c74b120561de78b1`.
   Android rejected data-preserving replacement with
   `INSTALL_FAILED_UPDATE_INCOMPATIBLE`; no uninstall or data clear followed.
2. Same-certificate APK SHA-256
   `7f6530d196963b44a824d852ff29938e5e0c420e147c416f999ab099d02a851a`
   installed but targeted `https://10.0.2.2:5666/api/v1`, where the Azure
   emulator received `Connection refused`.
3. Endpoint-correct APK SHA-256
   `3b9b0410ebf2732911186461b4dfa0110bda36ca57bea1f9a64b2a4563b19d63`
   proved Summary and Money but exposed the receipt phrase
   `compensating allocation history` and the generic reference labels
   `Account` and `Relationship`.
4. The final APK identified above replaces that Receipt copy and is the passing
   artifact for this report.

The authenticated app data and protected Azure stack were preserved through
all recovered blockers.

## Supported flow and evidence

### Unit Summary

The current exact-SHA capture shows:

- `Marketing availability / Not available`
- `Tenant account / Current`
- `Legal / notice / No signed lease`
- `Maintenance / turnover / Ready to rent`
- `Status / Occupied`
- `Stage / Active`

Condition-card bounds remain `[42,881][1038,1498]`. The audited raw values
`TenantAccount`, `NotAvailable`, `NoGoverningAgreement`, and `RentReady` do not
appear in the screenshot or hierarchy. No internal account ID appears.

### Unit Money

The current exact-SHA top capture shows:

- `Tenant balance / $0`
- `Market rent / $1,950/mo`
- `Tenant account / Available`, not database ID `#7`
- tappable `Payment receipt · $1,950` rows
- no raw `PaymentReceipt`

The preserved decisive Money capture and hierarchy show:

- `Payment received · 3/1/2026 · $1,950`
- `Rent charged · 3/1/2026 · $1,950`
- `Charge amount: $1,950 · Still due: $0`

The decisive Account activity hierarchy bounds are `[42,519][1038,1038]`;
Rent charges bounds are `[42,1075][1038,2156]`. Unit Money contains none of
`PaymentReceipt`, `allocated`, `allocation`, `open amount`, or internal numeric
account ID `#7`.

### Receipt and correction sheet

From Unit Money, tapping the first payment row at `(540,1270)` opened Receipt
after a seven-second stability wait. PID stayed `11621`, and `MainActivity`
remained focused.

The corrected Receipt hierarchy contains:

- `Account number / DEMO-TA-ACTIVE-007` at `[53,1385][1028,1480]`
- `Lease number / DEMO-LM-ACTIVE-007` at `[53,1480][1028,1574]`
- `This receipt is a permanent account record. If it needs a correction,
  Rental Command creates a linked refund and keeps both records in the account
  history.` at `[53,1627][1028,1753]`
- `Correct payment` at `[53,1795][1028,1921]`

The corrected Receipt hierarchy contains none of `allocation`, `immutable`,
`provenance`, raw `entry #`, `PaymentReceipt`, or `TenantAccount`.

Tapping `Correct payment` opened the correction sheet without changing PID or
activity focus. Its hierarchy preserves:

- plain-English explanation:
  `The original receipt stays in the account history. Saving this correction
  creates a linked refund.`
- `Account number / DEMO-TA-ACTIVE-007`
- `Unit / Clintonville Townhome · Unit Main`
- `Tenant / Carlos Rivera`
- `Payment / $1,950.00`
- editable `Reason`, `Payment method`, and `Payment reference` fields
- `Correction date · Jul 25, 2026`
- `Append correction` action

The sheet contains none of `allocation`, `immutable`, `provenance`, raw
`entry #`, `PaymentReceipt`, or `TenantAccount`. No field was edited and
`Append correction` was not tapped. The sheet was dismissed using Back after
capture, leaving the business state unchanged.

## Layout and stability

- All final captures are 1080x2400 at density 420.
- Summary, Money, Receipt, and correction-sheet labels render without clipping,
  ellipsis, overflow stripes, or blank/loading hangs.
- The Receipt FAB does not overlap any field, explanatory copy, or
  `Correct payment` action.
- The correction sheet fully exposes its fields, date, and
  `Append correction` action without FAB overlap.
- PID remained `11621` and `MainActivity` stayed focused across Money,
  Receipt, correction-sheet, and Summary navigation.

## Artifacts

Required and final exact-SHA captures:

- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-summary-plain-english.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-summary-plain-english.xml`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-plain-english-top.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-plain-english-top.xml`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-payment-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-payment-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-payment-correction.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-payment-correction.xml`

Preserved decisive scrolled-Money captures:

- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-plain-english.png`
- `Docs/Reviews/artifacts/tsk-750/post-unit-copy/unit-money-plain-english.xml`

## Cleanup and preserved state

- The unique remote APK handoff directory was removed immediately after the
  successful locked install.
- Existing app data was never uninstalled or cleared.
- No source edit, source build, commit, reset, reseed, payment submission, or
  protected-service mutation was performed by the emulator proof lane.
- Protected web, API, gateway, engine, and PostgreSQL containers were not
  stopped, replaced, reset, or deleted.
- The emulator app was left alive on Unit Summary at PID `11621`, with no
  correction submitted.
