# TSK-397 Pass 90 - Unit Rent Payment Refresh

Date: 2026-06-25
Branch: `tsk-397-418-real-user-pass-90-unit-rent-refresh`
Scope: Real-user verification for Notion `TSK-418` / prior bug `TSK397-B083`.

## Acceptance Criteria

- Staff can open a current leased unit's Rent tab from local sanitized data.
- Posting a payment from the inline Unit Rent form stays on the same `/units/[id]?tab=rent` route.
- The success toast appears after save.
- The newly created payment row appears immediately in the visible Unit Rent list without reload.
- Outstanding balance, header rent state, next action, and recent activity refresh from the same mutation/cache flow.
- The created payment persists through the live API.
- The focused regression test still proves the create success handler renders the returned payment before cache refetch completes.

## Edge Cases Checked

- Current leased unit was discovered through the live API instead of a stale hard-coded unit id.
- The first historical repro URL `/units/9?tab=rent` no longer exists in this sanitized DB and was discarded after the app showed `This unit could not be loaded.`
- The pass used a scheduled payment rather than a paid payment, so the balance/header/next-action refresh path was visible.
- The browser stayed on `https://localhost:6042/units/10?tab=rent` after submit.
- API persistence proof handled the endpoint's bare-array response shape.

## Evidence

- Before post: `output/playwright/pass90-unit-rent-before-post.png`
- After post: `output/playwright/pass90-unit-rent-after-post.png`

Browser findings:

- Logged in as `admin@rentalcommand.local` using the visible local dev-admin helper.
- Live API discovery found Unit `10` / Unit A, current lease `9`, tenant Marcus Williams, and initial outstanding balance `$0.00`.
- Opened `https://localhost:6042/units/10?tab=rent`; the Rent tab rendered existing paid rent rows and `Post payment`.
- Submitted a local synthetic payment with amount `$18.90`, date `2026-06-25`, type `Rent`, and status `Scheduled`.
- The page stayed on `https://localhost:6042/units/10?tab=rent`.
- The UI showed `Payment posted.`
- The header changed to `Overdue - $18.90`, next action changed to `Collect $18.90`, and the Rent tab outstanding balance changed to `$18.90`.
- The first visible rent row became `Jun 25, 2026 - Rent - Scheduled - $18.90` without reload.
- Recent unit activity added `Recorded a payment just now` linking to `/accounting/payments/537`.

API proof:

```json
{
  "id": 537,
  "leaseId": 9,
  "amount": 18.90,
  "paymentType": "Rent",
  "status": "Scheduled",
  "dueDate": "2026-06-25T00:00:00Z"
}
```

Regression test:

```bash
pnpm --dir web exec node --test --experimental-strip-types src/lib/components/unit/rent-tab-create.test.ts
```

Result: Passed. The focused command reported 2/2 passing tests.

## Result

Pass. No product code change was needed for this slice because the current Unit Rent tab already prepends the returned payment and invalidates shared payment, unit dashboard, and unit timeline caches.
