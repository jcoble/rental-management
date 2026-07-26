# TSK-750 Step 4A Android empty-copy proof

## Verdict

**UI PROOF BLOCKED — EMPTY-05's denied/tenant branch is unproved**

The authorized management branches pass on the exact-SHA Azure emulator APK:
Recurring Maintenance names the real `New recurring task` action, and Messages
names the real `New conversation` action. Both actions are present in the
opened quick-action launcher.

The required denied/tenant Messages branch cannot be proved without changing
the protected fixture. The installed administrator has only the Management
experience, and the two documented pre-existing tenant credentials available
to this lane were rejected by the protected Azure API. Per the proof contract,
no user, role, password, relationship, workspace, or seed data was created or
changed to manufacture authorization state.

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

`adb install -r` returned `Success`. Every emulator ADB interaction in this
proof also ran under the same shared lock.

## Passing authorized evidence

### Recurring Maintenance

The authorized management empty state renders:

- `No recurring tasks yet`
- `Open the action button and choose New recurring task.`

The combined hierarchy node is `[0,771][1080,1558]`. It contains no `Tap +`
instruction. The copy is centered, readable, and unclipped.

Opening the existing action button exposes:

- `Scan / Add`
- `New recurring task`
- `Record`
- `Assistant`
- `Close`

`New recurring task` is present at `[552,1547][1038,1662]`. The action was not
tapped; no recurring task was created.

### Authorized Messages

The authorized management empty state renders:

- `No conversations yet`
- `Open the action button and choose New conversation.`

The combined hierarchy node is `[0,844][1080,1684]`. The copy is readable and
does not refer to a pencil or another absent control.

Opening the existing action button exposes:

- `Scan / Add`
- `New conversation`
- `Record`
- `Assistant`
- `Close`

`New conversation` is present at `[572,1547][1038,1662]`. The action was not
tapped; no conversation was created.

## Denied/tenant branch blocker

The installed administrator account exposes no Work-area selector in its
Account sheet, so it has no existing nonprivileged or Tenant experience to
select.

Two existing tenant identities documented by prior local verification were
attempted without altering credentials:

- `tenant-alice@rc.local`
- `explore-tenant-a@rc.local`

Both returned `Invalid email or password` from the protected Azure fixture.
No password reset, invitation, account creation, role change, relationship
change, database write, or reseed was attempted.

The plan explicitly requires an already-existing nonprivileged/tenant
experience and says to leave EMPTY-05 unproved when none is available.
Accordingly, these required artifacts were not fabricated:

- `messages-empty-no-create-access.png` / `.xml`
- `messages-actions-no-create-access.png` / `.xml`

The authorized captures do not substitute for the denied branch.

## Layout and stability

- Every captured PNG is 1080x2400, and every matching hierarchy XML parses.
- The authorized empty-state copy and contextual actions render without
  clipping, ellipsis, overflow stripes, blank screens, or loading hangs.
- The opened action launcher does not obscure either empty-state instruction
  or the named primary action.
- PID remained `12997` through authorized proof, sign-out/login checks, and
  restoration of the administrator session.
- `MainActivity` remained the app target; no crash occurred.

## Artifacts

Passing authorized captures:

- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/recurring-maintenance-empty.png`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/recurring-maintenance-empty.xml`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/recurring-maintenance-action-open.png`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/recurring-maintenance-action-open.xml`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-empty-authorized.png`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-empty-authorized.xml`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-action-open-authorized.png`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-action-open-authorized.xml`

Unproved and intentionally absent:

- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-empty-no-create-access.png`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-empty-no-create-access.xml`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-actions-no-create-access.png`
- `Docs/Reviews/artifacts/tsk-750/post-empty-copy/messages-actions-no-create-access.xml`

## Cleanup and preserved state

- The unique remote APK handoff directory was removed after installation.
- Existing package data was never uninstalled or cleared.
- No source edit, source build, test run, commit, user creation, role change,
  reset, reseed, or protected-service mutation was performed.
- No recurring task or conversation action was submitted.
- The authenticated administrator session was restored after the two
  read-only tenant-login checks.
- The emulator app was left alive on Today at PID `12997`.
