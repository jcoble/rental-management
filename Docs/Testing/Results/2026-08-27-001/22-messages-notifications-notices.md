# Exploratory Test Report: Communications, conversations, notifications, notices, and delivery settings
Date: 2026-08-27
Tester: e2e-s22
Duration: Approximately 50 minutes

## Scenario
Exercise the staff communication loop, notification surfaces and settings, tenant notice drafts, and relationship-scoped delivery behavior as a landlord administrator.

## Summary
I logged in as the seeded administrator and exercised staff conversations, direct thread links, validation failures, notification settings, the notification bell, the notices empty state, and authorization redirects. Portal-only conversation creation and reply preserved QA-marked Unicode and multiline text after reload, and no external email or SMS was sent. Four confirmed defects were found: a misleading direct-thread layout, missing client-side body-length protection, silent loss of SMS-only delivery, and broken notification-settings deep-link scrolling.

## Bugs Found

### BUG-1: Load the conversation list for direct thread links
**Severity:** Medium
**Location:** Staff messages, `/messages/1` (desktop)
**Expected:** Opening a valid thread from a direct URL or notification should show the existing conversation context, or intentionally hide the list pane. It should not show an empty-inbox message beside a valid thread.
**Actual:** A fresh load of `/messages/1` showed the valid QA thread on the right, but the visible left pane said `No conversations yet` and `Start one to message a tenant`. The list pane measured approximately x=240, y=97, width=384, while the valid thread occupied the right pane.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s22-message-direct-detail.png`. The right pane contained the persisted `QA-20260827-s22-1787821111632` thread; the left pane showed the empty state.
**Code Reference:** `web/src/routes/(protected)/messages/+page.svelte:44-51` disables the conversation-list query whenever a canonical conversation id exists; `web/src/routes/(protected)/messages/+page.svelte:367-405` still renders the desktop list and its empty state.
**Suggested Fix:** Remove the canonical-id condition from the list query's `enabled` predicate so the list loads on a direct detail route and can render the selected conversation context.
**Why This Matters:** A landlord following a notification or bookmark can believe messages were lost and start a duplicate conversation.

### BUG-2: Prevent overlong message bodies before submission
**Severity:** Medium
**Location:** Staff messages, New conversation dialog
**Expected:** The composer should enforce the server's 4,000-character body limit before sending and give the landlord a clear local correction path. A normal UI action should not produce a raw HTTP validation error and console error.
**Actual:** A 5,035-character QA body was accepted by the textarea and the Start conversation button remained enabled. Submission returned HTTP 400 from `/api/v1/conversations`, logged `Failed to load resource: the server responded with a status of 400`, and left a red toast containing `The field Body must be a string or array type with a maximum length of '4000'.` The compose dialog stayed open with the text intact; database proof showed zero matching conversation or message records.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s22-message-too-long.png`. Playwright output recorded `BODY_TEXTAREA_MAXLENGTH null`, `SEND_DISABLED_BEFORE_SUBMIT false`, and the HTTP 400. Read-only SQL returned `long_subject_records = 0` and `long_body_records = 0`.
**Code Reference:** `web/src/routes/(protected)/messages/+page.svelte:671-678` has no body `maxlength`; `RentalCommand.Api/DTOs/ConversationDtos.cs:101-104` applies `[MaxLength(4000)]`; `web/src/routes/(protected)/messages/+page.svelte:312-320` sends the server error to the toast.
**Suggested Fix:** Add `maxlength={4000}` to the first-message textarea so normal typing and paste are constrained to the server contract before the request is made.
**Why This Matters:** A non-technical landlord cannot tell what went wrong or how much text must be removed, and may repeatedly retry a message that can never save.

### BUG-3: Reject unavailable SMS delivery instead of silently dropping it
**Severity:** High
**Location:** Staff messages, New conversation dialog with Text (SMS) selected
**Expected:** When SMS is selected but the tenant has no phone number or the portfolio has no SMS provider, the channel should be disabled or submission should fail with an actionable unavailable-delivery message. A saved message must not imply a channel was sent when no delivery was queued.
**Actual:** Tenant 24 (`QA-20260827-s16 Zoë🏠 O'Brien-Tester`) had no phone number. With only Text (SMS) selected, the UI left Start conversation enabled and showed only the generic helper `Requires SMS setup.` The request was accepted: conversation 2 and message 3 were persisted, but the message `Channels` column was NULL, there was no `conversation:2:*` outbox row, and there were zero `MessagingProviderSettings` rows. A fresh `/messages/2` load displayed the message without a channel or failure/unavailable result. No external SMS was sent.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s22-message-sms-detail.png`. Read-only SQL showed conversation 2 for tenant 24, message 3 with blank `Channels`, no matching outbox rows, tenant 24 `Phone` blank, and `messaging_provider_settings = 0`.
**Code Reference:** `RentalCommand.Data/Conversations/SendConversationMessageRule.cs:155-164` persists the normalized channel list, while `RentalCommand.Data/Conversations/SendConversationMessageRule.cs:542-553` queues SMS only when SMS survives normalization; `RentalCommand.Data/Conversations/SendConversationMessageRule.cs:556-575` silently removes SMS at lines 572-573 when the tenant phone is blank. The UI only describes the requirement at `web/src/routes/(protected)/messages/+page.svelte:699-703` and does not block the checkbox or submit.
**Suggested Fix:** Validate a requested SMS channel against a usable tenant phone and configured SMS provider before persistence, and return a clear 422 unavailable-delivery error instead of silently removing the channel.
**Why This Matters:** The landlord can believe a time-sensitive notice or repair update was texted when the tenant never received or had a queued message.

### BUG-4: Scroll notification-settings compatibility links to their target section
**Severity:** Medium
**Location:** `/settings/notifications/team-routing` and `/settings/notifications/tenant-notices`
**Expected:** The compatibility routes explicitly promise to land on the matching section. A bookmark to Team routing or Tenant notices should put that section in the viewport after the consolidated settings page loads.
**Actual:** `/settings/notifications/team-routing` ended at `https://localhost:5667/settings/notifications#team-routing`, but the page scroll position stayed at 0 while the Team routing section began around y=2019. At the required 1710x990 viewport the screen showed the top of My alerts, not Team routing. `/tenant-notices` behaved the same way with its section below the initial viewport; only My alerts happened to be visible near the top.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s22-settings-team-routing-deeplink.png`. Playwright measured viewport `1710x990`, target section top `2019`, and the settings container `scrollTop 0` after the redirect.
**Code Reference:** `web/src/routes/(protected)/settings/notifications/team-routing/+page.ts:3-6` redirects to the hash and says old links should land on the matching section; `web/src/routes/(protected)/settings/notifications/+page.svelte:42-44` places all sections in a nested `overflow-y-auto` container; `web/src/lib/components/notifications/TeamRoutingSection.svelte:222` defines the target id.
**Suggested Fix:** Handle the hash in the consolidated settings page and explicitly scroll the matching section inside `notifications-settings-page` after the conditional sections finish rendering.
**Why This Matters:** An administrator using an old bookmark or a copied settings link lands on the wrong controls and may assume the link is broken.

## Potential Issues (need investigation)

- Owner messaging appears read-only in the current frontend even though the owner experience scenario requires starting and replying to messages. The scenario states that requirement at `Docs/Testing/Scenarios/28-owner-experience-and-relationship-scope.md:25-29`; the API exposes `POST /owner/messages/{notificationId}/replies` at `RentalCommand.Api/Controllers/OwnerPortalController.cs:108-125`, but `web/src/lib/components/owner/OwnerItemsPage.svelte:22-28` only renders cards and `web/src/lib/api/endpoints/owner-portal.ts:76-90` has no owner-message mutation. This was not live-confirmed because the database had no non-administrator owner login, and the administrator was correctly redirected away from `/owner/messages`.
- The full portal notification page may not make read/unread state distinguishable. The API response includes `Severity`, `NavigationIntent`, and `IsRead` at `RentalCommand.Api/DTOs/NotificationDtos.cs:6-17`, while `web/src/routes/(portal)/portal/notifications/+page.svelte:37-45` renders every item with the same card treatment and only displays title and message. No tenant portal identity was available for a safe live check.
- The named `web/src/lib/components/notifications/NotificationSetupJourney.svelte` file is absent from this checkout. This looks like scenario/source drift rather than a confirmed user-facing defect.

## Observations

- Creating a Portal-only conversation with subject/body prefixed `QA-20260827-s22` preserved Unicode and multiline content after reload. The first message and reply were visible in the same thread, and read state was reflected in the staff flow; SQL showed conversation 1 with `LandlordUnreadCount = 0` and `TenantUnreadCount = 2`.
- Clearing every delivery checkbox correctly showed `Pick at least one way to send.` and disabled the send action.
- Invalid `/messages/99999` showed a clear `Couldn't load this conversation. Try again` error with a Back to conversations action. The resulting HTTP 404 and console error were expected for this deliberate negative test.
- The notification bell opened successfully and showed `0 unread notifications` / `No notifications yet`; no valid-flow console errors were observed.
- `/settings/notifications` presented distinct My alerts, Tenant notices, and Team routing sections. The administrator could see the correct management copy, but no shared routing or policy record was changed.
- `/notices` loaded the Draft filter and showed `No notice drafts in this view.` I did not click the portfolio-wide Generate drafts action because it could create unmarked notices for shared seed leases, contrary to the scenario's QA data-safety rule. Therefore edit/save/cancel/dismiss and approval delivery outcomes were not exercised in this run.
- Staff messages showed paging but no visible search or unread-only filter; the page currently calls the list endpoint with only `skip` and `take` at `web/src/routes/(protected)/messages/+page.svelte:44-50`. I recorded this as an observation because the current source/docs did not establish those controls as a confirmed contract.
- Direct `/owner` and `/owner/messages` routes redirected the administrator to `/`, which is consistent with the role guard. No relationship-user scope mutation was attempted.

## What Was Tested

1. Logged in through the UI with the dev login and verified the required `1710x990` headless viewport.
2. Opened the staff Messages page, started a QA-marked Portal conversation for tenant 23, entered Unicode and multiline text, replied, reloaded the thread, and checked persisted unread counts.
3. Opened a valid thread directly at `/messages/1`, captured the split-pane empty-list defect, and opened invalid `/messages/99999` to verify the error state.
4. Submitted a QA-marked 5,035-character first message to verify client/server length handling and confirmed no record was persisted.
5. Selected SMS only for tenant 24, whose phone is blank, then reloaded the created conversation and used read-only SQL to verify the missing channel and absent outbox/provider state.
6. Opened the notification bell, the consolidated notification settings page, all three legacy notification-settings routes, and the notices page. Captured the Team routing deep-link screenshot and inspected settings/policy copy without saving changes.
7. Checked owner/portal direct-route authorization as the administrator and recorded the absence of an eligible relationship-user context.
8. Screenshots captured: `/home/blackcolours/Workbox/screenshots/e2e-s22-message-direct-detail.png`, `/home/blackcolours/Workbox/screenshots/e2e-s22-message-too-long.png`, `/home/blackcolours/Workbox/screenshots/e2e-s22-message-sms-detail.png`, and `/home/blackcolours/Workbox/screenshots/e2e-s22-settings-team-routing-deeplink.png`.
9. Browser cleanup: stopped e2e-s22.
