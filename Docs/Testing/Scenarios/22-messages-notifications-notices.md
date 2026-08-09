# Scenario 22 — Communications, conversations, notifications, notices, and delivery settings

## Purpose

Cover the communication loop between staff, tenants, owners, and responsible workers: conversations, unread/read state, notices and drafts, personal alerts, team routing, tenant notice policies, and email/SMS availability.

## Preconditions and login

Use a QA tenant/owner/work-order context and additive QA message/notice text. Do not send external email/SMS or change global routing on a shared preview; use local or a safe marked policy when mutation is necessary.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/messages/+page.svelte and web/src/routes/(protected)/messages/[id]/+page.svelte
- web/src/routes/(protected)/notices/+page.svelte
- web/src/routes/(protected)/settings/notifications/my-alerts/+page.svelte
- web/src/routes/(protected)/settings/notifications/team-routing/+page.svelte
- web/src/routes/(protected)/settings/notifications/tenant-notices/+page.svelte
- web/src/routes/(portal)/portal/messages/+page.svelte and web/src/routes/(portal)/portal/notifications/+page.svelte
- web/src/lib/components/notifications/NotificationSetupJourney.svelte and web/src/lib/api/endpoints/messages.ts
- RentalCommand.Api/Controllers/ConversationsController.cs, NotificationsController.cs, MyAlertsController.cs, NoticeDraftsController.cs, TenantNoticePoliciesController.cs, TeamRoutingController.cs, SmsWebhookController.cs, and OwnerPortalController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Start and open a staff conversation, reply from a tenant/owner context, mark read/unread, search/filter, and follow navigation intents; compare thread identity and unread counts after reload.
- Create/edit/save/cancel/dismiss a tenant notice draft, choose portal/email/SMS delivery where supported, and inspect approval, retry, failed, and unavailable provider states.
- Open My alerts, Team routing, and Tenant notices as separate settings surfaces; verify copy and authorization make their scopes distinct and a change does not alter unrelated policies.
- Inspect notification list and dashboard/header surfaces, open an item, confirm read state and destination, and test a notification for an inaccessible/deleted record.
- Compare tenant, owner, leasing, technician, and administrator delivery visibility; relationship users must not reach team routing or tenant policy administration.

## Specific edge cases worth trying

- Empty subject/body, whitespace, long/Unicode/emoji message, pasted multiline text, duplicate operation key, send while offline, and reply to a closed thread.
- Notice without tenant/unit, invalid date, huge body, unsupported channel, missing template, provider credentials absent, retry/replay, and duplicate draft.
- Two tabs changing read state, stale notification target, notification pagination boundary, and a direct route with an invalid intent.
- Mobile message split pane, long unread badge count, toasts obscured by keyboard, and screen-reader label/focus checks.

## What to verify visually

- Conversation list/detail selection, unread/read indicators, sender/recipient context, composer errors, and scroll-to-latest behavior are obvious.
- Notice status, channel, draft provenance, policy mode, delivery result, and retry affordances are distinguishable without color alone.
- Settings section headings, help links, loading/error/empty states, toasts, and 390px layout remain legible.

## Data safety and evidence

Prefix all new subject/body/notice/template text with QA-YYYYMMDD. Do not send external messages or alter existing shared routing/policy records.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
