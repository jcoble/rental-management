# Exploratory Test Report: Lease creation, agreements, signing, corrections, and renewals
Date: 2026-08-27
Tester: e2e-s15
Duration: about 45 minutes

## Scenario
Exercise a marked lease relationship from creation through agreement draft, issue, anonymous signing, possession gating, and the successor-agreement lifecycle.

## Summary
Created additive relationship `QA-20260827-s15 Lease966047` on Eastland 8-Plex Unit 4, edited its draft, and issued agreement 21 with a generated PDF and signer request. Draft persistence, the due-day guard, signer identity, artifact status, and the possession guard worked as expected. Two confirmed defects were found: issuing a lease emits a stray 404 draft request, and reopening an otherwise active signing link after its first view incorrectly blocks the signer; the e-sign engine then shut down, so execution, possession-after-signing, corrections, renewals, and month-to-month conversion were explicitly blocked and not claimed as passed.

## Bugs Found

### BUG-1: Prevent the closed issue dialog from requesting a null draft
**Severity:** Low
**Location:** Staff lease detail `/leases/21`, immediately after successfully sending the agreement for signature.
**Expected:** After the issue operation returns 201, the draft dialog should close and the page should load signature progress without requesting another draft. The draft query is only valid while both lease and agreement IDs are positive and the dialog is active.
**Actual:** The successful issue flow produced a red console error and an HTTP 404 for `GET /api/v1/lease-managements/21/agreements/null/draft`. The issue itself succeeded and the page still showed `Awaiting signatures`, `Issued artifact Ready`, and `Executed artifact Not ready`.
**Evidence:** `15-issue-lease.js` captured issuance preparation 201, issue 201, then `HTTP 404 GET https://localhost:5667/api/v1/lease-managements/21/agreements/null/draft`; the API log records the same request and 404 at `/tmp/rentalcommand-api.log:90628-90650`. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s15-lease-issued-progress.png`.
**Code Reference:** `web/src/lib/components/leases/AgreementDraftDialog.svelte:95-103` gates the draft query on the agreement ID, but `:324-331` calls `onclose()` before `onissued(result)`; the parent clears the ID at `web/src/lib/components/leases/LeaseManagementDetail.svelte:262-269`.
**Suggested Fix:** Complete the issue callback before clearing the parent’s edit agreement ID, or otherwise suspend the draft query during teardown so the component can never evaluate `getAgreementDraft` with a null agreement ID.
**Why This Matters:** It does not lose the lease, but it creates a false failed request during the landlord’s normal send flow, leaves a red console signal for monitoring, and can obscure a real issuance failure.

### BUG-2: Keep an active signing link usable after the signer views it
**Severity:** Medium
**Location:** Anonymous signing page `/sign/<generated signer token>` after the first page load and a subsequent reload/open.
**Expected:** The first GET should record `Viewed`; a later open or browser refresh should still return the current signing package while the signer remains unsigned. The service explicitly projects current state after commit so later opens do not replay stale status, and the view rule treats a non-Pending signer as available.
**Actual:** The first anonymous load returned 200 and displayed the document, ESIGN/UETA disclosure, consent checkbox, typed-signature field, and Sign button. A subsequent load returned 409 from the API and the public page displayed `This signing link is no longer active` / `This link has expired or has already been used`. Staff signature progress still showed `Packet status Viewed`, `Signatures 0 of 1 required`, signer `Viewed`, `Consent Not yet`, and `Signed Not yet`; no signature was submitted.
**Evidence:** `17-public-token.js` captured the active signing form at viewport `1710x990`; `19-public-recheck.js` captured the unusable-link screen; `/tmp/rentalcommand-api.log:94728-94742` records the first token GET as 200 and `:95422-95434` plus `:97088-97100` record later GETs as 409. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s15-public-signing-token-before.png`. Staff confirmation is in `/home/blackcolours/Workbox/screenshots/e2e-s15-signature-progress-pending.png`.
**Code Reference:** `RentalCommand.Api/Services/Esign/NativeSigningService.cs:59-64` reuses `TokenIdentity(token)` for every view while the command carries a new timestamp at `RentalCommand.Core/Esign/RecordNativeEsignViewCommand.cs:16-20`; `RentalCommand.Core/Atomic/AtomicCommandIdentity.cs:34-45` fingerprints the complete command and `RentalCommand.Data/Atomic/AtomicTransactionRunner.cs:120-129` turns the changed fingerprint into an idempotency conflict. The public loader then maps every 4xx, including 409, to the expired state at `web/src/routes/(public)/sign/[token]/+page.server.ts:40-45`.
**Suggested Fix:** Mark only the view command’s telemetry/time fields (`IpAddress`, `UserAgent`, and `OccurredAtUtc`) as ignored by the atomic fingerprint, leaving `TokenHash` as the stable payload identity, so repeated GETs replay the first-view receipt and re-project the current package.
**Why This Matters:** A tenant who opens the email twice, refreshes, or returns to the signing tab cannot sign. The landlord is left with an issued but unsigned lease and must resend unnecessarily.

## Potential Issues (need investigation)

- The local native e-sign engine shut down after its advisory-lock watcher reported that another engine instance had taken over. The engine log shows `NativeEsignReconciliationWorker stopped`; no executed artifact was produced. This environment condition prevented completion of the signature, executed-PDF, possession-after-signing, correction, renewal, and month-to-month checks. It is not counted as a product bug in this report.
- The public loader intentionally treats all 4xx responses as “no longer active.” BUG-2 identifies the observed 409 cause for this flow; other legitimate 409 cases may also be presented with the same expired-link copy and should be reviewed when the fix is made.
- A second portfolio and non-admin role were not available in this run, so cross-portfolio lease/agreement IDs and owner/technician/tenant route authorization were not independently exercised.

## Observations

- Creating the relationship was coherent: HTTP 201 returned lease-management, tenant-account, agreement, deposit-account, and tenant IDs. The marked tenant appeared on the detail page with account `#21` and “Continues across renewals and corrections.”
- Entering due day `0` was blocked in the create dialog with `Enter a due day from 1 through 31.`; restoring it to `31` allowed the save. Rent, deposit, late fee, planned possession, fixed-term dates, and the built-in Rental Command template were accepted and shown in the draft.
- Editing agreement number to `QA-20260827-s15-AGR-966047` and rent to `$1,275.50` persisted after save and reload. The response reported version 1, draft revision 2, and one required signer.
- Issuance preparation and issue both returned HTTP 201. The staff view clearly showed agreement history, signer identity/order, issued artifact readiness, and that the executed artifact was not yet ready. The built-in template worked while the custom template library was empty.
- The possession workflow correctly rejected the out-of-order action with HTTP 422: `An executed governing agreement is required before possession can be given.` No possession mutation was observed.
- On the anonymous signing page, the consent disclosure, signer identity, required consent, typed/drawn choices, and recoverable document fallback were visible at the required desktop viewport. Chromium did not render the PDF inline, but the page provided an explicit Open/download PDF link.

## What Was Tested

- Read `CLAUDE.md`, the master specification, lease/e-sign plans, prior lease reports, the requested Svelte routes/components/API clients, controllers, domain rules, entities, atomic transaction code, signing service, native e-sign rules, and possession rules before browser use.
- Logged in through the dev login at `https://localhost:5667/login` and verified every browser script at viewport `1710x990` in headless Chromium.
- Inspected the leases list and lease-template screen, then opened the current manual lease-preparation flow.
- Created the marked tenant and lease on Eastland 8-Plex Unit 4 with fixed dates, rent, due day, deposit, late fee, planned possession, and built-in template; verified the invalid due-day validation and successful 201 response.
- Opened the generated agreement draft, edited money and agreement number, saved, reloaded, and verified the persisted revision and signer details.
- Issued the exact saved revision, captured the preparation/issue 201 responses and the resulting issued-artifact/signature-progress state, and observed the BUG-1 null-draft 404.
- Obtained the marked signer’s generated link from the local outbox by read-only inspection, opened it anonymously, verified the active signing controls, then reopened it and reproduced BUG-2 without submitting a signature.
- Opened staff signature progress and confirmed the request remained Viewed with 0 of 1 signatures; attempted possession once and verified the expected 422 guard.
- Did not void or delete any existing agreement, reset the sandbox, or perform any destructive database action. Corrections, renewals, month-to-month conversion, executed-artifact reconciliation, and possession after execution remain untested because the required executed agreement could not be produced while the local engine was stopped.

Browser cleanup: stopped e2e-s15
