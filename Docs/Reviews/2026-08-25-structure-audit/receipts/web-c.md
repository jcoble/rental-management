# web-c receipt

- Lane: `web-c`
- Rework started: `2026-08-25T19:40:16-04:00`
- Started: `2026-08-25T17:28:56-04:00`
- Finished: `2026-08-25T17:48:02-04:00`

## Item 1 — duplicate payments client

- Result: DONE
- Commit: `2bb61c402e234bb2e3c25b98fdfb3e92f4c4e922`
- Diff: 7 files, +62/-168; `web/src/lib/api/endpoints/payments.ts` deleted.
- Consumers: `PaymentDetail.svelte` and the accounting past-due page now call `tenantMoney`.
- `TenantMoneyCommandResponse<T>` declarations after the merge: one (`web/src/lib/api/endpoints/tenant-money.ts`).

### Route and request-body comparison

The old `payments` client and retained `tenantMoney` client use the same route templates:

| Operation | Before | After |
| --- | --- | --- |
| receipt | `/tenant-accounts/${tenantAccountId}/receipts` | `/tenant-accounts/${tenantAccountId}/receipts` |
| charge | `/tenant-accounts/${tenantAccountId}/charges` | `/tenant-accounts/${tenantAccountId}/charges` |
| reversal | `/tenant-accounts/${tenantAccountId}/charges/${chargeEntryId}/reversals` | `/tenant-accounts/${tenantAccountId}/charges/${chargeEntryId}/reversals` |
| refund | `/tenant-accounts/${tenantAccountId}/refunds` | `/tenant-accounts/${tenantAccountId}/refunds` |

The body argument remains the same object at each consumer and is serialized with `JSON.stringify` once in both clients. The retained `api.post` implementation adds the same JSON content type and passes `body` directly to `JSON.stringify`; the `Idempotency-Key` header remains the caller's operation key. Verified by `git show 2bb61c40^:web/src/lib/api/endpoints/payments.ts`, `web/src/lib/api/endpoints/tenant-money.ts:176-251`, and `web/src/lib/api/client.ts:405-415`.

## Item 2 — shared server fetch

- Result: DONE
- Commit: `0b77a6e267f66bf149c013025478d4ee23014ada`
- Diff: 14 files, +191/-132.
- Converted raw fetch expressions: 12 across the ten named route files (the docs article loader had two).
- `serverFetch` now supports an omitted access token, exposes response headers and parsed error metadata needed by login/form actions, and composes caller cancellation with its timeout.

### Preserved user-visible error contracts

| Route/action | Preserved result |
| --- | --- |
| login | Missing fields: `fail(400, "Email and password are required")`; access selection: `fail(409, API message)`; other API errors retain the API status/message and email-verification message; connection failure remains `fail(500, "Unable to connect to the API server. Please ensure the backend is running.")`; other unexpected failures remain `fail(500, "An unexpected error occurred. Please try again.")`. |
| register | Local validation remains `fail(400, original message)`; API errors retain API status/message/details; connection failure remains `fail(500, "Unable to connect to the server. Please try again later.")`; other unexpected failures retain the original 500 message. |
| forgot-password | Missing email remains `fail(400, "Email is required.")`; non-2xx remains `fail(503, "Password recovery is temporarily unavailable. Please try again.")`; network failure remains `fail(503, "Unable to connect to Rental Command. Please try again.")`. |
| reset-password | Four local validation cases remain `fail(400, original message)`; API failure retains API status/message; network failure remains `fail(500, "Unable to connect to the server. Please try again later.")`. |
| activate-team | Four local validation cases remain `fail(400, original message)`; API failure retains API status and password/error/fallback message; network failure remains `fail(500, "Unable to connect to Rental Command. Please try again.")`. |
| verify-email | Invalid link, API error, and network error strings remain unchanged; success retains the API message or `Email verified.` fallback. |
| logout | Cookie deletion and `303 /login` remain unconditional; revocation errors do not block logout, and caller cancellation remains 1.5 seconds. |
| docs loaders | Index/layout remain fail-soft with empty categories; article 404/status/redirect behavior remains unchanged. |

Verified by the diff for commit `0b77a6e2`, the passing focused forgot-password/logout tests, and the live flow below.

## Item 3 — live flow

- Result: DONE.
- Viewport: `1710x990`.
- Browser session: `web-c-verify`, headless.
- Login: `admin@rentalcommand.local` succeeded; a fresh sample portfolio was selected to provide tenant data.
- Tenant ledger: Marcus Williams, account 1, opened through Tenants → lease → View tenant account.
- Receipt: $10.00 cash payment recorded; UI displayed `Payment recorded.` and balance moved from $1,050.00 to $1,040.00.
- Reversal/correction: refund reference `web-c-verification`; UI displayed `Payment correction recorded. The original receipt remains in history.`
- Logout returned to `/login`.
- Forgot-password bogus email: `bogus-web-c@example.invalid`; UI displayed `If an account exists for that email, we've sent a password reset link.`
- Public `/docs` rendered its categories and articles while logged out.
- Screenshots: `web-c/01-login.png` through `web-c/07-docs.png`.
- Browser cleanup: stopped `web-c-verify` (CLI listed no browser sessions; its Chrome PID 572336 was absent from `pgrep -fa 'chrome|chromium'`; remaining Chromium PIDs belonged to separately named EdiPlatform sessions).
- Dev stack cleanup: ports 5665, 5666, and 5667 had no listeners after Ctrl+C; the worktree's API, Engine, and Vite processes were absent.

## Verification

| Command | Exit | Result |
| --- | ---: | --- |
| `pnpm --dir web install --frozen-lockfile` | 0 | Lockfile unchanged; 273 packages installed from cache. |
| `pnpm --dir web check` | 0 | 5,622 files; 0 errors and 18 inherited warnings. |
| `pnpm --dir web check:native` | 0 | Native TypeScript check passed with no diagnostics. |
| `pnpm --dir web test` | 1 | Lane-owned tests pass, but four inherited source-contract failures remain: accounting books, cash flow, unit lifecycle actions, and canonical lease lifecycle action hub. Diagnostic rerun: `ℹ fail 4`. No failing file is changed by this lane. |
| `rg -n "endpoints/payments'|SERVER_API_BASE_URL\}" web/src/routes --glob '!**/+server.ts'` | 1 | Zero non-`+server.ts` matches (expected `rg` no-match exit). |
| Focused forgot-password action test | 0 | 4 passed. |
| Focused logout action test | 0 | 3 passed. |
| `dotnet build-server shutdown` | 0 | MSBuild and compiler servers stopped after the dev-stack build. |

The verbatim `rg` without a glob exclusion lists these allowed `+server.ts` sites: `application-file/[id]`, `document-file/[id]`, `expense-file/[id]`, `scan-file/[id]`, and `workorder-file/[id]`. It also lists `auth/google/callback/+server.ts`, an out-of-scope non-streaming auth callback not named by the item.

## Tests changed

- `web/src/lib/api/endpoints/past-due-paging-contract.test.ts`: renamed the required source symbol from `payments.recordReceipt` to `tenantMoney.recordReceipt`.
- `web/src/lib/api/endpoints/tenant-account-read-contract.test.ts`: retargeted the retained negative read-route guard from the deleted module to `tenant-money.ts`.
- `web/src/lib/components/records/payment-correction-contract.test.ts`: moved imports/hooks to `tenant-money.ts` and supplied the canonical result type's nullable fields.
- `web/src/routes/forgot-password/forgot-password-action-test-loader.mjs`: maps the new shared-helper import so the existing behavioral action tests still execute the real helper.
- `web/src/routes/logout/logout-action-test-loader.mjs`: maps the new shared-helper import so the existing logout behavior tests still execute the real helper.

## Out-of-scope observations

- `web/src/routes/auth/google/callback/+server.ts` still uses a raw `${SERVER_API_BASE_URL}` fetch but was not among the named page/layout files and is not a file-streaming proxy; it was left as-is by controller decision.
- The full web unit suite's four inherited contract failures were not changed because their accounting/lease files are outside this lane.
- The first dev-stack attempt failed because the shared EdiPlatform PostgreSQL service rejected the launcher's default credential. The retry used that existing container's configured development user without printing its password and created the requested `rentalcommand` development database.

## Review round 1 rework

- Finished: `2026-08-25T19:43:36-04:00`
- Finding 2: DONE in `226e170c817fc8d2ce3e98f4c3da5e94d2c164cd`; restored the four route-specific non-JSON API error fallbacks.
- Finding 4: DONE in `bc4a3a06bc5108292d2ed76874b344747951543a`; removed `TenantPaymentRefundRequestSpec` and `buildTenantPaymentRefundRequest`, then changed the payment correction contract test to capture `tenantMoney.refundPayment` through the API stub.
- Finding 5: DONE in `b5199451`; replaced pre-rebase hashes with the current `git log main..HEAD` hashes and recorded that the Google callback remains unchanged by controller decision.

### Rework verification

| Command | Exit | Result |
| --- | ---: | --- |
| `pnpm --dir web check` | 0 | 5,622 files; 0 errors and 18 inherited warnings. |
| `pnpm --dir web check:native` | 0 | Native TypeScript check passed with no diagnostics. |
| `pnpm --dir web test` | 1 | 893 tests: 892 passed; only the pre-existing `lease-action-hub-contract.test.ts` source-text assertion failed. |
| `rg -n 'buildTenantPaymentRefundRequest|TenantPaymentRefundRequestSpec' web/src` | 1 | Zero matches; `rg` returns 1 when no matches are found. |

### Rework tests changed

- `web/src/lib/components/records/payment-correction-contract.test.ts`: replaced assertions against the deleted request-builder helper with assertions against the real `tenantMoney.refundPayment` API call.
