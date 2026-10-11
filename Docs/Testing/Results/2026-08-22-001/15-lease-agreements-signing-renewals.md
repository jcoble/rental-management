# Exploratory Test Report: Lease Agreements, Signing, and Renewals (Scenario 15)
Date: 2026-08-22
Duration: ~45 minutes

## Scenario
Validate the lease lifecycle: creation via onboarding wizard, agreement draft editing, issuance, native e-signature via public signing page, correction/versioning, and edge-case validation. Regression focus on the write executor migration (idempotency, double-click, stale draft detection).

## Summary
The core lease lifecycle from creation through issuance, native signing, and correction is functional end-to-end. The e-signature path (issue, email delivery, public signing page, consent, typed signature, completion, token expiry) works correctly. Two bugs were found: the due-day field accepts invalid value 0 on save, and the agreement draft dialog does not auto-close or navigate after a successful issuance (leaving the user on a stale "no longer editable" state). Several observations around UX and data presentation are noted.

## Bugs Found

### BUG-1: Due day 0 accepted on lease agreement draft save
**Severity:** Medium
**Location:** Edit lease draft dialog, due-day spinbutton field
**Expected:** Saving a lease agreement draft with due day 0 should fail validation, since day-of-month must be 1 through 31 (or a smaller max depending on business rules). Due day 0 is not a valid calendar day and would break rent charge generation.
**Actual:** The draft saved successfully with due day 0. No frontend or backend validation error was shown.
**Evidence:** Set due day to 0 via `fill e473 "0"`, clicked Save draft (ref e499). Toast appeared: "Lease draft saved." No validation error paragraph appeared. Verified by re-opening the draft: spinbutton showed "0". (verified)
**Code Reference:** `web/src/lib/components/leases/AgreementDraftDialog.svelte` (the dialog component) and `RentalCommand.Api/Controllers/LeaseAgreementController.cs:196-206` (EditDraft action) -- neither performs range validation on `RentDueDay`.
**Why This Matters:** A due day of 0 will cause incorrect or missing rent charge generation when the engine processes recurring charges. It breaks the invariant that rent is assessed on a specific day of each month.
**Suggested Fix:** Add a range check (1-31) in the backend `EditLeaseAgreementDraftCommand` validation and mirror it in the frontend spinbutton with min/max attributes or pre-submit validation.

### BUG-2: Draft dialog does not close or refresh after successful issuance
**Severity:** Low
**Location:** Edit lease draft dialog, after clicking "Prepare and send" confirmation
**Expected:** After the issuance succeeds, the dialog should close and the agreement list should refresh to show the new "Awaiting signatures" status, or at minimum navigate the user forward (e.g., to the signature progress panel).
**Actual:** The dialog remains open with the message "This lease is no longer an editable draft. Refresh the lease history." The underlying agreement row still shows "Draft" and "Edit draft" button until the user manually closes the dialog and navigates away and back.
**Evidence:** Clicked "Prepare and send" (ref e591 in confirmation section). After 10 seconds, the dialog body showed `generic [ref=e572]: This lease is no longer an editable draft. Refresh the lease history.` while the agreement row behind it still displayed `generic [ref=e406]: Draft`. Only after closing the dialog, navigating away, and returning did the status update to "Awaiting signatures". (verified)
**Code Reference:** `web/src/lib/components/leases/AgreementDraftDialog.svelte` -- the `handleIssued` callback should close the dialog and invalidate the queries, but the dialog appears to detect the stale draft state reactively rather than via the issuance response.
**Why This Matters:** Users may be confused about whether the issuance succeeded. They may click "Prepare and send" again (which would replay idempotently, but the UX suggests failure).
**Suggested Fix:** After a successful issuance response, the dialog's `handleIssued` callback should close the dialog and call the parent's `onissued` to trigger query invalidation.

## Potential Issues (Need Investigation)

### PI-1: Signing completion shows "waiting on other signers" for single-signer leases
**Location:** Public signing page (`/sign/{token}`)
**Observation:** After the sole signer signs, the success screen says "We're now waiting on the other signer(s); you'll receive a completed copy..." rather than "Signed -- all done." The `requestCompleted` flag was not set in the sign action response. The execution did complete asynchronously (engine processed it within seconds), but the immediate response to the signer was misleading.
**Evidence:** After signing via eval-triggered click, `actionResult` showed `requestCompleted: undefined` (not explicitly false, just absent). The success text referenced "other signer(s)." Refreshing the staff view 30 seconds later showed "Signed lease" status, confirming execution did complete. (verified)
**Impact:** Low -- cosmetic confusion. The signer gets a correct completed-copy email once the engine processes execution, but the in-page message is inaccurate for the common single-signer case.
**Code Reference:** `web/src/routes/(public)/sign/[token]/+page.svelte:342-355` -- the `requestCompleted` derived value drives which heading/body to show.

### PI-2: "Give possession" button missing on the lease detail page
**Location:** Lease detail page, Possession section
**Observation:** The page shows "Possession not yet given" and "Update past move-in details" but no "Give possession" button. The `PossessionActions.svelte` component may require specific conditions (e.g., lifecycle state, capabilities) that aren't met, or the button text may have changed.
**Evidence:** Searched for "Give possession" in page text via eval -- not found. Found "Update past move-in details" in the Possession section. (verified)
**Impact:** Might block the give-possession step of the lifecycle. Needs investigation of `PossessionActions.svelte` conditions.

### PI-3: Lease list shows "No signed lease yet" while agreement is in "Awaiting signatures" state
**Location:** `/leases` list page
**Observation:** The "Lease" column shows "No signed lease yet" even after the agreement has been issued and is awaiting signatures. The list view may not distinguish between Draft and AwaitingSignatures states in its "Review" column.
**Evidence:** After issuance, the lease list row showed `cell "No signed lease yet"`. (verified)
**Impact:** Low -- the list doesn't convey that an agreement is out for signature.

## Observations

1. **Correction flow works well**: Creating a correction successor from "Fix a typo" correctly prompts for a reason (required field), shows the predecessor version context, and creates a version 2 draft. The predecessor stays "Active / Current lease" until the correction is signed.

2. **Stale draft detection is robust**: When the draft dialog detects the agreement is no longer editable (e.g., after issuance), it displays a clear message rather than silently failing. The message "This lease is no longer an editable draft" is accurate.

3. **Token expiry on re-visit works correctly**: After signing, re-visiting the same signing URL shows "This signing link is no longer active" with a clear message. The 410 Gone status is handled gracefully.

4. **Signing page is well-designed for desktop**: The consent disclosure, typed name pre-fill from signer data, signature preview, and document preview (PDF iframe) are all functional and clearly laid out.

5. **End-before-start validation works**: Setting term end before term start produces a clear error message: "Term end cannot be before term start."

6. **$0 rent is accepted on draft save**: While this could be intentional (some lease types might have $0 rent during renovations or owner-occupied scenarios), it merits a business rule review.

7. **Idempotency on issuance**: The system tolerated the first "Prepare and send" click (which showed the two-step confirmation), and the re-issue attempt after page reload used the same idempotency key correctly (the engine log showed only one email delivery).

8. **Session auth persistence across pages**: Navigating between pages occasionally caused session timeout (redirects to login). This happened at least twice during the test, requiring re-login via the dev fill button. May indicate short session TTL in dev mode.

## What Was Tested

1. **Property/unit/tenant creation** via onboarding wizard (S15 Birch Manor, unit S15-101, tenant S15 Maria Vasquez)
2. **Lease creation** through onboarding "Create lease & finish" (L-2026-001, $1,850/month, fixed term 08/22/2026 to 08/22/2027)
3. **Draft editing**: Opened Edit draft dialog, reviewed pre-populated fields (term, rent, deposit, due day, grace period, signer details)
4. **Edge case: $0 rent** -- saved successfully (no validation)
5. **Edge case: due day 0** -- saved successfully (BUG-1)
6. **Edge case: due day 31** -- saved successfully (acceptable)
7. **Edge case: end-before-start** -- validation caught it (correct)
8. **Draft save and re-open**: Saved draft, closed dialog, re-opened -- values persisted correctly, "Prepare and send" enabled when form matches saved state
9. **Issuance (Prepare and send)**: Two-step confirmation dialog with subject line, confirmation worked, email sent to s15.maria@example.com (verified via OutboxMessages table on port 5434 DB rentalcommand_qa20260822)
10. **Public signing page**: Opened in separate browser session, confirmed signer identity display, consent disclosure, typed name pre-fill, sign button enabled after consent, successful signature submission
11. **Signed token re-use**: Re-visiting the same signing URL showed "This signing link is no longer active" (410 Gone handled correctly)
12. **Staff view after signing**: Refreshed lease detail showed "Signed lease" status, executed artifact available
13. **Correction successor**: Clicked "Fix a typo", validated empty reason rejected, filled reason, created version 2 draft, confirmed predecessor stays "Active / Current lease"
14. **Mobile signing page check (390x844)**: Terminal/expired state rendered correctly at phone width

Browser cleanup: stopped e2e-s15 (daemon + Chrome helper tree)
Browser cleanup: stopped e2e-s15sign (daemon + Chrome helper tree)
