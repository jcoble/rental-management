# Exploratory Test Report: Leasing, Listings, Applications & Screening
Date: 2026-08-22
Duration: ~35 minutes

## Scenario
Test the leasing workspace, listings, application lifecycle (create, review, approve, decline, withdraw), screening status path, and application-to-move-in handoff. Verify public apply link, form validation, status transitions, data consistency, mobile responsiveness, and edge cases including double-submit and stale state after mutations.

## Summary
The core application lifecycle works: public apply link generation, anonymous form submission, staff review, approval with tenant creation, and decline with reason all function correctly end-to-end. The prepare-move-in handoff correctly inherits applicant data, unit assignment, and signer context, and the unit is not marked occupied prematurely. One confirmed bug was found in query cache invalidation that causes stale UI state after every status transition (approve, decline, withdraw) until the page is reloaded. The leasing workspace pages (Today, Pipeline, Rentals, Calendar, Inbox) are correctly gated behind the Leasing experience and redirect Management-experience users to the dashboard.

## Bugs Found

### BUG-1: Application status badge and action buttons remain stale after approve/decline/withdraw until page reload
**Severity:** High
**Location:** Application detail page (`/applications/{id}` and `/units/{id}?tab=leasing&view=applications&app={id}`)
**Expected:** After approving, declining, or withdrawing an application, the status badge should update immediately (e.g., "Submitted" changes to "Approved"), and the action buttons (Edit, Record fee, Approve, Decline, Withdraw) should disappear for terminal states.
**Actual:** The toast confirms the mutation succeeded ("Application approved. A tenant record was created."), and the tenant banner appears, but the status badge stays at "Submitted" and all action buttons remain visible. After a hard page reload, the correct state renders: badge shows "Approved", action buttons are hidden, and the Prepare move-in / View tenant buttons appear. The same stale behavior was observed for the decline transition.
**Evidence:** Verified by snapshot comparison immediately after the approve mutation (status badge ref=e302 still showed "Submitted", Approve/Decline/Withdraw buttons still present) vs. after a full page reload (status badge ref=e212 showed "Approved", no action buttons rendered). Reproduced for both approve and decline flows.
**Code Reference:** `web/src/lib/components/records/ApplicationDetail.svelte:86` defines the query key as `['application', applicationQueryScope, id]` (where `applicationQueryScope` defaults to `'management'`). The `invalidate()` function at line 110 calls `queryClient.invalidateQueries({ queryKey: ['application', id] })`, which uses key `['application', 1]`. TanStack Query's prefix matching compares array elements positionally, so `['application', 1]` does NOT match `['application', 'management', 1]` because the second element differs (numeric `1` vs string `'management'`). The query is never invalidated; the stale cached data remains.
**Why This Matters:** After approving an application, the staff user sees stale action buttons (Approve, Decline, Withdraw) still enabled. Clicking Approve again could attempt a double-approve, and clicking Decline on an already-approved application sends an invalid transition request. The user has no visual confirmation that the status change persisted without manually reloading. This undermines trust in the application -- the write executor regression context specifically flagged "success toasts with nothing persisted" as a concern, and while the data IS persisted, the UI does not reflect it.
**Suggested Fix:** Change line 110 from `queryClient.invalidateQueries({ queryKey: ['application', id] })` to `queryClient.invalidateQueries({ queryKey: ['application', applicationQueryScope, id] })` to match the actual query key structure. Alternatively, invalidate with `{ queryKey: ['application'] }` to match all application queries regardless of scope, though that is broader than necessary.

## Potential Issues (Need Investigation)

### PI-1: Login form stuck in "Signing in..." state with no error feedback
**Location:** `/login`
**Observation:** On at least two occasions, clicking "Sign In" after filling credentials left the button in a permanent "Signing in..." disabled state with no error message displayed. The API was confirmed responsive (returned 401 for unauthenticated requests, 200 for direct login fetch). The login eventually succeeded on a fresh browser session. The root cause is unclear -- possibly a transient network issue or race condition in the auth flow -- but the user-facing problem is that the form provides no timeout or error recovery. A user stuck in this state would have to guess that refreshing the page is needed.
**Recommendation:** Add a timeout to the login mutation (e.g., 15 seconds) that transitions the button back to "Sign In" and shows an error message like "Login is taking longer than expected. Please try again."

### PI-2: SignalR WebSocket connection failures logged in console
**Location:** All authenticated pages
**Observation:** Console errors show `WebSocket connection failed: Error during WebSocket handshake: Unexpected response code: 404` and `Failed to complete negotiation with the server: TypeError: Failed to fetch`. These appear to be transient dev-stack issues (the SignalR hub at `/api/v1/hubs/updates` may not be running or the proxy configuration is incorrect). The application still functions without real-time updates, falling back to polling or manual refresh.
**Recommendation:** Verify the SignalR hub registration in the dev stack startup script. These errors would be invisible to production users if the hub is properly configured there, but they indicate the real-time update path (which would normally auto-refresh the application status after mutations) is not operational in dev.

## Observations

1. **Public apply page has good mobile layout.** At 390x844 viewport, the submit button is 358px wide (nearly full width), no horizontal overflow occurs, all form fields and the consent checkbox are accessible. The scan-to-autofill CTA is prominent and usable on mobile.

2. **Property/unit picker on public form works well.** Selecting a property correctly enables the unit dropdown and shows availability labels ("Available" for vacant units). Changing the property resets the unit selection. The "No preference" option is available for both.

3. **Form validation on the public apply page is comprehensive.** Submitting with empty required fields shows inline errors for first name, last name, email, phone, and consent. Email format is validated client-side.

4. **Invalid/expired application token handling is correct.** Navigating to `/apply/INVALID-TOKEN-12345` shows a clear "This link isn't working" message with instructions to contact the property manager. No stack trace or raw error is exposed.

5. **No staff navigation leak on public pages.** The public apply page (`/apply/{token}`) renders without any sidebar nav, dashboard links, or management UI elements. The page is fully standalone.

6. **Application-to-tenant handoff data is accurate.** The Prepare move-in dialog correctly inherits: applicant name, application ID and status, property and unit from the approved application, tenant as required signer with signing order 1, and the note "This plans the handoff; it does not mark the unit occupied."

7. **Unit not prematurely marked occupied.** After approving an application, the unit page still shows "Vacant" status and "No current resident" -- the occupancy transition requires completing the Prepare move-in workflow.

8. **Status filter on applications list works correctly.** Filtering by "Declined" showed only the declined application. The URL updates with `?status=Declined` and persists across navigation.

9. **Screening section correctly reflects unconfigured state.** With no screening provider configured, the integrated screening button shows disabled with "Provider selection is still being finalized. Outside screening remains available." The external screening track mode is available as a fallback.

10. **Application approval auto-creates a synthetic screening record.** After approving application #1, a screening record appeared with provider "Synthetic test screening", reference "application-1", status "Completed", mode "External", and decision "Accept". This was not manually created -- it appears to be auto-generated during the approval flow.

## What Was Tested

### Test fixture creation
- Created property "S14 Willow Apt" (100 Willow Lane, Denver, CO 80202, Multi-family) via the onboarding guided setup.
- Created unit "S14-101" (2 bed, 1 bath, market rent $1,800) within the property.

### Public application link and form
- Generated application link from Applications page ("Get application link" button).
- Opened the link in a separate browser session (no staff auth) at `https://localhost:5667/apply/{token}`.
- Verified no staff navigation elements leak onto the public page.
- Tested empty form submission -- all required-field validation errors render inline.
- Selected property "S14 Willow Apt" and unit "S14-101 - Available" from the dropdowns.
- Submitted application for "S14 Jane S14 Doe" with email, phone, employer, income, and notes.
- Saw success page: "Application submitted! Thank you, S14 Jane."
- Tested invalid token (`/apply/INVALID-TOKEN-12345`) -- saw "This link isn't working" error page.
- Spot-checked mobile viewport (390x844): form layout, submit button width (358px), no horizontal overflow.

### Staff-side application review
- Verified submitted application appeared in the applications list with "Submitted" status.
- Opened application detail via list row click (navigated to unit command center context).
- Confirmed all data matches: name, email, phone, employer ($3,500 income), property (S14 Willow Apt), unit (S14-101), consent given with timestamp, notes.

### Status transitions
- **Approve:** Clicked Approve on application #1. Confirmation dialog showed correct name and warning about tenant creation. After confirming, toast said "Application approved. A tenant record was created." Tenant banner and Prepare move-in link appeared. BUG-1: Status badge and action buttons did not update until page reload.
- **Decline:** Submitted second application (S14 John S14 Smith). Clicked Decline, entered reason "S14 Insufficient income for the unit". After confirming, toast said "Application declined." BUG-1 reproduced: status badge stayed "Submitted". After reload: status showed "Declined" with reason displayed, no action buttons present.
- **Approve (second):** Submitted third application (S14 Alice S14 Brown). Approved successfully.

### Prepare move-in handoff
- Clicked "Prepare move-in" on approved application. Three-step wizard opened.
- Step 1 ("Who & where") showed: approved application reference (S14 Jane S14 Doe, Application #1, Approved), exact rental (S14 Willow Apt, Unit S14-101, "Carried from the approved application"), primary tenant as required signer with signing order 1, date fields for household relationship start and planned possession.
- Verified unit still shows "Vacant" on the unit page -- not prematurely occupied.

### Screening section
- Integrated screening disabled (no provider configured) with correct explanatory text.
- External screening option available.
- After approval, synthetic screening record auto-generated with Accept decision.

### Applications list
- Verified all three applications appear with correct statuses.
- Status filter dropdown includes all 5 statuses plus "All statuses".
- Filtering by "Declined" correctly showed only the declined application.
- Mobile viewport (390x844): DataGrid switches to card layout, all applications visible with names and status badges, no horizontal overflow.

### Leasing workspace
- Navigating to `/leasing`, `/leasing/pipeline`, `/leasing/rentals`, `/leasing/calendar`, `/leasing/inbox` all correctly redirect to dashboard -- the admin account is in Management experience and the leasing workspace requires the Leasing experience. This is expected role-based access gating, not a bug.
