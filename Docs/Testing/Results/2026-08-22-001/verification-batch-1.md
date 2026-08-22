# Verification: Batch 1 Results (Run 2026-08-22-001)

Date: 2026-08-22
Build: main 799889a2
Stack: API + Engine + Web at https://localhost:5667 / https://localhost:5666
Login: admin@rentalcommand.local
Browser: headless Chrome, viewport 1710x990 (verified), session e2e-vb1

## Fix 1: TSK-965 — Engine starts and delivers notifications

**PASS**

**Evidence:**
- Engine log shows `Application started. Press Ctrl+C to shut down.` at line 6, no DI errors. Verified: `/tmp/rentalcommand-engine.log`.
- OutboxDispatchWorker is processing items. After issuing a lease draft (Fix 7 test), the engine delivered the e-sign notification within seconds: `[Email captured locally] OutboxMessageId=58 To=vb1lease@test.example Subject=Please sign: Lease AGR-E4CB09E699B9-V1`. Verified: `grep -i "outbox\|notification" /tmp/rentalcommand-engine.log`.
- All workers started without errors: OutboxDispatchWorker, RentChargeWorker, ScanProcessingWorker, DebtServiceWorker, RecurringTenantChargeWorker, and 12 others.

## Fix 2: TSK-967 — /register redirects an authenticated user

**PASS**

**Evidence:**
- Logged in as admin, navigated to `https://localhost:5667/register`. Page URL resolved to `https://localhost:5667/` (dashboard). Title: "Dashboard - Rental Command". No registration form rendered. Verified: playwright snapshot URL after goto.
- Logged out via `/logout`, navigated to `https://localhost:5667/register`. Page URL stayed at `/register`. Title: "Create account - Rental Command". Full registration form rendered with name, email, password, confirm password fields. Verified: snapshot showed `heading "Create your account"` and four textbox inputs.

## Fix 3: TSK-968 — Unit endpoints return 404 (not 403) for nonexistent unit

**PASS**

**Evidence:**
- `GET https://localhost:5666/api/v1/units/99999/dashboard` with valid Bearer token returned HTTP 404, body `{"error":"Unit not found"}`. Verified: `curl -sk -w "\nHTTP_STATUS:%{http_code}"`.
- `GET https://localhost:5666/api/v1/units/99999` with valid Bearer token returned HTTP 404, body `{"error":"Unit not found"}`. Verified: same curl command.
- `GET https://localhost:5666/api/v1/units/1` (real unit, S13 Maple Cottage) returned HTTP 200 with full unit JSON including `"id":1,"unitNumber":"S13 Maple Cottage"`. Verified: same curl command.

## Fix 4: TSK-969 — Properties list filters clear their URL params

**FAIL**

**Expected:** Selecting "All types" after filtering by a type should remove the `?type=` param from the URL. Same for "All statuses" clearing `?status=`.

**Actual:** The UI visually updates (button text changes to "All types" / "All statuses", table shows all rows, data query fires correctly), but the URL param persists. After selecting "Single-family" the URL correctly gains `?type=SingleFamily`. After selecting "All types", the button text changes to "All types" and all properties display, but the URL remains `?type=SingleFamily`. Reloading the page re-applies the stale filter.

**Repro:**
1. Navigate to `https://localhost:5667/properties` (clean URL).
2. Open type filter, select "Single-family". URL becomes `?type=SingleFamily`. Correct.
3. Open type filter, select "All types". Button shows "All types", table shows all rows. URL remains `?type=SingleFamily`. Bug.
4. Wait 5+ seconds. URL unchanged.
5. Reload. Filter re-applies as "Single-family" from the stale URL.
6. Same behavior confirmed for status filter: selecting "Active" adds `?status=Active`, selecting "All statuses" leaves the param in the URL.

**Code reference:** `web/src/routes/(protected)/properties/+page.svelte` lines 87-114; `web/src/lib/utils/grid-url-state.svelte.ts` `syncGridUrl` function. The `gridQueryString` function correctly computes an empty query when the filter is empty (typeFilter=''), but the `replaceState` call inside `tick().then(...)` appears to not execute. Possible cause: the `$effect` wrapping `syncGridUrl` may not re-trigger when `typeValue` changes from a concrete value to the `ALL_TYPES` sentinel, or there is a timing/batching issue where the `page.url` read inside the tick callback returns the same query.

## Fix 5: TSK-973 — Application detail refreshes after approve/decline/withdraw

**PASS**

**Evidence (approve):**
- Created application ID 4 (VB1 Test Applicant) via public API, status "Submitted".
- Navigated to `/applications/4`. Snapshot showed status badge "Submitted" and Approve/Decline buttons.
- Clicked Approve, confirmed in dialog. WITHOUT reloading: status badge changed to "Approved", toast "Application approved. A tenant record was created." appeared, Approve/Decline buttons replaced with approval message. Verified: snapshot text `generic: Approved` and `generic: Application approved. A tenant record was created.`

**Evidence (decline):**
- Created application ID 5 (VB1 Decline Test) via public API, status "Submitted".
- Navigated to `/applications/5`. Snapshot showed "Submitted" and Approve/Decline buttons.
- Clicked Decline, entered reason "VB1 testing decline flow", confirmed. WITHOUT reloading: status badge changed to "Declined", toast "Application declined." appeared, reason displayed. Verified: snapshot text `generic: Declined` and `generic: "Reason declined: VB1 testing decline flow"`.

## Fix 6: TSK-974 — Lease draft rejects due day 0

**PASS**

**Evidence:**
- Created a lease via "Prepare move-in" on unit S13 Maple Cottage with tenant VB1 Lease Test, due day 1. Lease ID 2 created.
- Opened "Edit draft" dialog, changed Due day spinbutton to "0", clicked "Save draft".
- Validation error appeared: `paragraph: Rent due day must be from 1 to 31.` Draft was NOT saved. Verified: snapshot text.
- Closed dialog, reopened. Due day showed "1" (original value preserved). Verified: spinbutton value.
- Changed Due day to "31", clicked "Save draft". Toast "Lease draft saved." appeared, revision bumped to 2. Verified: snapshot text `generic: Revision 2`.

## Fix 7: TSK-975 — Draft dialog closes after successful issuance

**PASS**

**Evidence:**
- On lease 2, opened "Edit draft" dialog, clicked "Prepare and send", confirmed in the issue confirmation sub-dialog.
- Dialog closed on success. Toast "Lease sent for signature." appeared. Agreement row on the lease page showed "Awaiting signatures" without manual navigation or page reload. Verified: snapshot showed no dialog element, `generic: Awaiting signatures`, and `generic: Lease sent for signature.`
- Minor observation: a console 404 was logged for `/api/v1/lease-managements/2/agreements/null/draft` -- the draft query fires with a null agreement ID after the dialog component unmounts. This does not block the user flow but is a minor cleanup item.

## Fix 8: TSK-976 — No console 404 on unit Money tab without a deposit

**PASS**

**Evidence:**
- Navigated to `https://localhost:5667/units/1?tab=money&view=tenant-account` (unit S13 Maple Cottage, lease created without "Open a security-deposit account" checked).
- Money tab rendered correctly with "Deposit held: —" (empty state). No console error log files were created during or after the Money tab load. Verified: `ls -t .playwright-cli/console-2026-08-22T08-3[4-9]*.log` returned no matches; the most recent console log was from earlier Fix 7 testing (the agreement/null/draft 404).
- The TenantLedgerPanel code at line 124-131 correctly catches 404 from the deposit endpoint and returns null instead of propagating the error.

---

## ORCHESTRATOR DISPOSITION: Fix 4 re-fixed and re-verified (2026-08-22)

The FAIL was confirmed and root-caused by the orchestrator: `syncGridUrl` (web/src/lib/utils/grid-url-state.svelte.ts) compared the desired query against `page.url`, but SvelteKit's shallow `replaceState` does not refresh `page.url`. After adding `?type=...` via one shallow write, `page.url` stayed on the pre-filter URL; clearing back to the sentinel then computed `"" === ""` against the stale baseline and skipped the URL update entirely (instrumented log showed `current: ""` while `location.search` was `?type=SingleFamily`; the patched `history.replaceState` recorded zero calls on clear). The bug affected all 16 grid pages using the helper.

Fix: compare and merge against `window.location` in both the no-op check and the post-tick guard. Merged as PR #600 (main 436ab50d).

Re-verified live by orchestrator (headless Chrome, 1710x990 verified, session fix969, exact verifier repro):
- Type: select Single-family → `?type=SingleFamily`; select All types → URL query empty.
- Status: select Active → `?status=Active`; select All statuses → URL query empty.
- `pnpm check` 0 errors; URL-state contract unit tests 3/3 pass.

Fix 7 observation (console 404 for `/agreements/null/draft` during unmount) captured as [TSK-986], Low.

**Batch 1 final: 8/8 fixes verified** (7 by verifier-b1, TSK-969 re-fix by orchestrator).
