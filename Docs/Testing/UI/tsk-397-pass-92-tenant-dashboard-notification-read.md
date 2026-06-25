# TSK-397 Pass 92 - Tenant Dashboard Notification Read Before Navigation

Date: 2026-06-25

## Scope

- Task: TSK-421 - Mark tenant dashboard notification links read before navigation.
- Surface: tenant portal dashboard notification summary.
- Route: `/portal`.
- Control: notification summary button that opens the notification `actionUrl`.
- User: seeded local tenant `marcus.williams@email.example`.

## Acceptance Criteria

- An unread tenant dashboard notification renders in the dashboard summary.
- Clicking the notification marks that notification read before route navigation begins.
- The tenant lands on the notification action URL.
- The header unread badge and API unread count drop after the click.
- The notification row is persisted as read.

## Risk-Based Edge Cases

- The notification has an `actionUrl` into the tenant portal.
- The tenant notification is user-specific, not a shared broadcast row.
- The notification links to a conversation and should land on the selected message thread.
- A previously unread notification should not keep the header badge stale after navigation.
- A repeat read request must be idempotent and must not block navigation.

## Regression Test

Command:

```bash
pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/notifications-page.test.ts
```

Result:

- 2 tests passed.
- The test now asserts that dashboard notification read/refresh/unread-count invalidation happens before `goto(...)`.

## Browser Proof

Setup used local API endpoints only:

- Created staff-to-tenant Portal-channel conversation `5`.
- Created tenant notification `19`.
- Notification action URL: `/portal/messages?conversation=5`.
- Tenant unread count before click: `2`.

Before click:

- URL: `/portal`.
- Dashboard summary showed `2 unread`.
- Header badge showed `2`.
- Notification card text: `TSK-421 dashboard notification proof 1782380179705`.
- Screenshot: `output/playwright/pass92-tenant-dashboard-notification-before-click.png`.

Click result:

- Clicked the dashboard notification card.
- Landed on `/portal/messages?conversation=5`.
- Header badge dropped to `1`.
- Conversation thread `5` opened.
- API unread count after click: `1`.
- API notification `19`: `isRead: true`.
- Screenshot: `output/playwright/pass92-tenant-dashboard-notification-after-click.png`.

Network evidence:

- Request `254`: `POST /api/v1/notifications/19/read` -> `204`.
- Request `255`: `GET /api/v1/notifications/unread-count` -> `200`.
- Request `256`: `GET /api/v1/notifications?take=20` -> `200`.
- Request `261`: `GET /portal/messages/__data.json?conversation=5...` -> `200`.

The first read request completed before the messages route data loaded. A second idempotent `POST /notifications/19/read` also returned `204` during refresh; this did not affect the user-visible result or persisted read state.

## Outcome

Pass. The tenant dashboard notification workflow now has both source-level regression coverage for read-before-navigation ordering and browser evidence from a real tenant session.
