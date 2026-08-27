# Exploratory Test Report: Leasing workspace, listings, applications, and screening
Date: 2026-08-27
Tester: e2e-s14
Duration: about 45 minutes

## Scenario
Exercise the landlord lead-to-approval path from a vacant rental listing through public application, screening, approval, and move-in preparation.

## Summary
I logged in with the prescribed account, created a new vacant QA property/unit (`QA-20260827-s14-554972`, unit 27), and exercised the reachable Management-side listing and unit-application surfaces. Listing generation, editing, validation, persistence, and the guided publication state worked; anonymous invalid/expired links also showed the correct no-login error page. The Leasing workspace was unavailable to this Management-only account, and application creation/screening/move-in could not proceed because link generation did not return and the scan upload ended with Chromium `ERR_NETWORK_CHANGED` before creating a draft. Two confirmed bugs were found in the current source/runtime behavior.

## Bugs Found

### BUG-1: Keep previously issued public application links valid
**Severity:** High  
**Location:** Unit Applications tab and `/applications` link-generation flow; public `/apply/[token]` resolution

**Expected:** Public application links must be additive and token-scoped. Generating a new link must not invalidate an earlier link that may already be on a flyer, email, or message.

**Actual:** The application-link command generates one new random token and overwrites the portfolio's single `PublicApplicationToken`. Public resolution only matches that one current value. The unit UI explicitly tells the landlord that generating another link “refreshes the public application token” and to use the newest link.

**Evidence:** The live unit page at `https://localhost:5667/units/27?tab=leasing&view=applications` loaded the QA unit and the application-share entry point. Its current copy is visible in `/home/blackcolours/Workbox/screenshots/e2e-s14-unit-applications-loaded.png`. The overwrite and exact-current-token lookup are verified at `RentalCommand.Api/Services/Domain/AtomicWorkspaceCoreMutationRule.cs:155-176` and `RentalCommand.Api/Services/Domain/ApplicationService.cs:463-470`; the UI warning is at `web/src/lib/components/unit/tabs/ApplicationsTab.svelte:306-307`. Two attempts to get a live URL did not return, so I did not claim a live old/new URL comparison; the invalidation is deterministic from the write and resolver code.

**Code Reference:** `RentalCommand.Api/Services/Domain/AtomicWorkspaceCoreMutationRule.cs:155-176` overwrites `Portfolio.PublicApplicationToken`; `RentalCommand.Api/Services/Domain/ApplicationService.cs:463-470` resolves only that value; `web/src/lib/components/unit/tabs/ApplicationsTab.svelte:306-307` documents the invalidating behavior.

**Suggested Fix:** Store each issued application token as an additive, portfolio/unit-scoped link record with an explicit active/revoked state, and resolve public requests against active link records instead of replacing the portfolio's one token.

**Why This Matters:** A landlord who generates a replacement link can silently break prospects using an older printed or previously sent link, losing leads and creating avoidable “invalid or expired” support requests.

### BUG-2: Do not request screening for an application that failed to load
**Severity:** Low  
**Location:** `https://localhost:5667/applications/999999`

**Expected:** An invalid or inaccessible application URL should make the one application request, show a clean not-found/error state, and avoid ancillary requests that cannot succeed.

**Actual:** The page eventually showed the correct `Could not load application / Application not found / Retry` state, but it also sent `GET /api/v1/applications/999999/screening`, which returned 403 and produced a red browser console error, while the main application request returned 404.

**Evidence:** Browser output for the direct invalid-ID reproduction:

```text
HTTP 403 https://localhost:5667/api/v1/applications/999999/screening
CONSOLE ERROR: Failed to load resource: the server responded with a status of 403 ()
HTTP 404 https://localhost:5667/api/v1/applications/999999
CONSOLE ERROR: Failed to load resource: the server responded with a status of 404 ()
BODY: Could not load application / Application not found / Retry
```

**Code Reference:** `web/src/lib/components/records/ApplicationDetail.svelte:334-338` enables the screening query for every positive numeric ID without waiting for `applicationQuery`; `RentalCommand.Api/Controllers/ApplicationsController.cs:461-468` routes the ancillary request through the screening authorization check and returns 403 when the application is unavailable.

**Suggested Fix:** Add the loaded application object to the screening query's enabled condition, so screening loads only after the application request succeeds (for example, require `!!application` in `ApplicationDetail.svelte:337`).

**Why This Matters:** It creates noisy red errors and unnecessary authorization/database work for stale, mistyped, or cross-portfolio URLs, making real failures harder to diagnose while the landlord is trying to open an application.

## Potential Issues (need investigation)

- **Application-link request stalls.** On two separate clicks of the enabled `Get application link` button, the button changed to `Generating…`, no dialog or link appeared after 1.5 seconds, and no `/api/v1/applications/link` response was observed. The run ended only after the browser context waited roughly the client’s 20-second fetch timeout. The current UI mutation is at `web/src/routes/(protected)/applications/+page.svelte:115-128,267-275`, the request is `web/src/lib/api/endpoints/applications.ts:233-237`, and the client timeout is `web/src/lib/api/client.ts:36,131-143`. Shared-database contention from the concurrent tester is possible; inspect server/database lock timing. A user-facing timeout should leave the button usable and show a clear retryable error.

- **Scan upload could not be attributed to the application feature.** On `/scan`, I selected the visible `Rental Application` type and uploaded the generic Rental Command icon (not applicant data). Chromium reported `ERR_NETWORK_CHANGED`; no scan POST response or draft appeared after 12 seconds. The page remained usable and showed no draft. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s14-scan-application-unavailable.png`. This needs a repeat in a stable browser/network environment before treating it as a product defect.

- **Required Leasing workspace was inaccessible under the prescribed account.** `/leasing`, `/leasing/rentals`, `/leasing/pipeline`, `/leasing/calendar`, and `/leasing/inbox` all redirected to `/`. The active account had only the Management experience, with no experience selector. This matches the current route policy (`web/src/lib/auth/experience-policy.ts:59-113` and `web/src/routes/(protected)/+layout.server.ts:34-40`), but it prevented live verification of Today, listings, pipeline, showings, and inbox. Confirm whether the seeded admin/landlord account is expected to receive Leasing access; if so, this is an access/provisioning gap rather than a route bug.

- **Negative listing rent is accepted by the Save button and rejected only after submission.** Filling `-1` left Save enabled because `web/src/lib/components/unit/tabs/ListingTab.svelte:40,113-114` checks only that the value is finite. The server correctly returned HTTP 400 with `The field Rent must be between 0 and 99999999.` from the DTO range at `RentalCommand.Api/DTOs/ListingWorkspaceDtos.cs:152-158`, and the invalid value was not persisted. Consider client-side range validation to avoid an unnecessary failed save; data integrity was protected in this run.

- **The valid, reused, and wrong-unit token matrix could not be completed.** The historical token checked anonymously returned HTTP 404 and the clear expired/invalid state; no current valid token was available because staff link generation stalled. No application was submitted and no screening provider was called.

## Observations

- Blank property creation was handled well: the form showed inline errors for name, address, city, state, ZIP, and rent before accepting the QA values. The setup response was HTTP 201 and created property 12/unit 27 with unit status `Vacant`.
- The listing workspace generated successfully and saved through HTTP 200. The QA headline, description, rent 1500, and deposit 1500 remained after reload. The guided publication showed `Ready`; the connected Zillow publication remained unavailable with the explicit provider-approval explanation. The core listing and publication states are modeled separately, so I did not treat that distinction as a bug. Screenshots: `/home/blackcolours/Workbox/screenshots/e2e-s14-listing-before.png`, `/home/blackcolours/Workbox/screenshots/e2e-s14-listing-after-generate.png`, and `/home/blackcolours/Workbox/screenshots/e2e-s14-listing-after.png`.
- The vacant unit's Applications tab clearly carried the QA property/unit identity and offered a share action. The empty state correctly said no applications were tied to the unit.
- Anonymous invalid/expired public links rendered a focused “This link isn't working” message with no staff navigation. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s14-public-invalid-recheck.png`.
- The invalid application detail page's user-facing error state was understandable despite the extra screening request described in BUG-2.
- No suspect in-memory totals or per-row request pattern was observed in the reachable list surfaces; application and scan pages expose server-paged list APIs.

## What Was Tested

1. Logged in at `/login` with `admin@rentalcommand.local` / `Admin123!`; confirmed the browser viewport was `1710x990`.
2. Enumerated the named Leasing routes and confirmed their Management-only redirect behavior.
3. Created the new QA property/unit through the visible Add rental form, including a blank-submit validation check.
4. Opened unit 27, prepared a listing, inspected generated copy/photo slots/publication states, edited the headline/description/rent/deposit, saved, and reloaded to verify persistence.
5. Submitted negative rent once; captured the HTTP 400 validation response, then restored and saved the valid rent.
6. Opened the unit Applications tab and the global Applications page; confirmed empty states, unit identity, filters/share controls, and the token-rotation warning.
7. Attempted application-link generation twice; no link response or public URL was returned.
8. Opened an arbitrary invalid public token and a historical token in a separate anonymous browser profile; verified the invalid/expired message and absence of staff navigation.
9. Selected Rental Application in Scan / Add and uploaded a generic non-applicant image; recorded Chromium `ERR_NETWORK_CHANGED` with no draft created.
10. Opened `/applications/999999` to exercise the invalid application path and captured the extra screening 403 plus the main 404.
11. Screening, approval/decline/withdraw, and prepare-move-in were not executed because no application could be created or safely opened; no paid screening provider was contacted.

Browser cleanup: stopped e2e-s14
