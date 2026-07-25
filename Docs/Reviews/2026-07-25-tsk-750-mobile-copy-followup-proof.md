# TSK-750 Step 3B Android copy-follow-up proof

## Verdict

**UI PROOF PASS — COPYFOLLOW-05 passes on the exact-SHA Azure emulator APK**

The supported Today work-order row, Today-origin detail push/back, Analytics
chart, and Vendors list passed on the real Azure Android emulator. The tested
copy is readable and unclipped, navigation retained the Today origin, and no
blank screen, hang, meaningful FAB obstruction, or PID regression occurred.

## Proof target

- Source SHA: `a1eb500391b79b62399ab57c5650d419e9ec0e95`
- Exact APK:
  `/Users/blackcolours/.codex/remote-artifacts/tsk750-copy-followup-x64-azureapi-rootcert-a1eb5003/app-dev-debug.apk`
- APK SHA-256:
  `16f0a14ff7277e391a93286757790cd40e60f53e9487b0314c999d34f79b9ab6`
- APK signer SHA-256:
  `c3a34969aca92701c9e277591f220c6d547c4eb7198b05dcf2da3188fd8ff17e`
- APK ABI evidence: `lib/x86_64/libflutter.so`
- Compiled API:
  `https://redacted-host.example.invalid/api/v1`
- Device: Azure `emulator-5554`, `sdk_gphone64_x86_64`
- Target: physical `1080x2400`, density `420`
- Package/activity:
  `com.rentalcommand.rental_command.dev/com.rentalcommand.rental_command.MainActivity`
- Proof PID: `12187`

The worktree HEAD matched the requested source SHA. The APK hash was verified
locally and after transfer, `apksigner` reported the expected signer, and the
archive contained the x86_64 Flutter library.

Installation used data-preserving replacement under the Azure heavy-operation
lock:

```bash
sudo flock -w 1800 /srv/dev-stacks/.locks/azure-heavy.lock \
  sudo -u azureuser /bin/bash -lc \
  'source /opt/runner-tools/mobile-env.sh >/dev/null 2>&1; \
   adb -s emulator-5554 install -r <exact-apk>'
```

`adb install -r` returned `Success`. No uninstall, package-data clear, reset, or
reseed occurred. The existing authenticated example workspace persisted.

## Supported flow and evidence

### Today work-order copy

On Today, the captured Work Orders section renders:

- `Roof shingle repair`
- property `Maple Ridge Duplex`
- `In progress · High priority`

The row hierarchy is
`Roof shingle repair / Maple Ridge Duplex / In progress · High priority` at
`[53,812][1028,1043]`. The property and status/priority occupy two separate
subtitle lines. `InProgress` does not appear in the Today row, and all three
lines render without clipping or ellipsis.

The same capture also proves sentence-case copy on the additional visible rows,
including:

- `Scheduled · Normal priority`
- `In progress · Normal priority`
- `New · Normal priority`
- `New · Low priority`

### Today-origin push and Back

Tapping the first Today work-order row at `(540,925)` opened the unit-aware
detail for:

- `Unit A / Maple Ridge Duplex`
- `Work Order`
- `Roof shingle repair`

PID remained `12187`, and `MainActivity` stayed focused. Android Back returned
to the same scrolled Today Work Orders section. The returned hierarchy again
placed the first row at `[53,812][1028,1043]`, and the bottom navigation
hierarchy retained `Today`. No shell-destination switch occurred.

### Analytics chart months

The Analytics capture shows the existing Income vs Expenses bars with readable
month labels in server-provided order:

`Aug`, `Sep`, `Oct`, `Nov`, `Dec`, `Jan`, `Feb`, `Mar`, `Apr`, `May`, `Jun`,
`Jul`.

No raw `-08`-style label appears. All twelve labels are visible below their
bars without clipping or overlap. The chart, legend, and surrounding KPI cards
loaded without a blank state or hang.

The chart is a custom-painted surface, so its month glyphs are evidenced by the
PNG while the matching hierarchy XML evidences the surrounding
`Income vs Expenses`, `Income`, and `Expenses` surface.

### Vendors completed-job count

The Vendors hierarchy shows six visible rows with the exact completed-work
meaning, including:

- `Apex Plumbing Co. / Plumbing / Not rated yet / 0 completed jobs`
- `ComfortZone HVAC / HVAC / Not rated yet / 0 completed jobs`
- `Green Thumb Landscaping / Landscaping / Not rated yet / 0 completed jobs`
- `Handy Pro Services / Handyman / Not rated yet / 0 completed jobs`
- `Reliable Electric LLC / Electrical / Not rated yet / 0 completed jobs`
- `Summit Roofing Inc. / Roofing / Not rated yet / 0 completed jobs`

The first five complete row bounds are respectively:

- `[42,876][1038,1107]`
- `[42,1109][1038,1340]`
- `[42,1343][1038,1574]`
- `[42,1577][1038,1808]`
- `[42,1810][1038,2041]`

The numeric value remains `0`, while the label now communicates
`completed jobs` rather than the ambiguous `jobs`.

## Layout and stability

- Every captured PNG is 1080x2400; every matching hierarchy XML parses.
- Today’s required title, two subtitle lines, and row action are fully visible.
- All twelve Analytics month labels are readable; no label is reduced to a raw
  numeric suffix.
- Vendor names, categories, ratings, completed-job labels, and row actions are
  readable and unclipped.
- The FAB does not obscure the evidenced Today row, chart labels, or required
  vendor copy/actions.
- No blank screen, loading hang, overflow stripe, or crash occurred.
- PID remained `12187`, and `MainActivity` remained focused across Today,
  detail push/back, Analytics, and Vendors navigation.

## Artifacts

Required plan captures:

- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/today-work-order-readable.png`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/today-work-order-readable.xml`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/analytics-readable-months.png`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/analytics-readable-months.xml`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/vendors-completed-jobs.png`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/vendors-completed-jobs.xml`

Supplemental navigation captures:

- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/today-work-order-detail.png`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/today-work-order-detail.xml`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/today-work-order-return.png`
- `Docs/Reviews/artifacts/tsk-750/post-copy-followup/today-work-order-return.xml`

## Cleanup and preserved state

- The unique remote APK handoff directory was removed immediately after the
  successful locked install.
- Existing app data was never uninstalled or cleared.
- No source edit, source build, commit, write action, reset, reseed, or
  protected-service mutation was performed by the emulator proof lane.
- Protected web, API, gateway, engine, PostgreSQL, and persistent data were not
  stopped, replaced, reset, or deleted.
- The emulator app was left alive on Vendors at PID `12187`.
