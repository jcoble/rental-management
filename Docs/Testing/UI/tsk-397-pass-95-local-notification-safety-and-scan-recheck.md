# TSK-397 Pass 95 Local Notification Safety And Scan Recheck

Date: 2026-06-25
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-real-user-pass-30`
Branch: `tsk-397-real-user-pass-30`
Status: Interim checkpoint. Local notification safety fixed and image-scan bootstrap rechecked from a clean browser session.

## Scope

Resume the production-scale real-user audit after the pass-94 checkpoint, clear stale Playwright sessions, verify local dev cannot silently use production notification providers from user-secrets, and recheck the image-first scan spine from a fresh browser session.

## Local Stack

- API: `https://localhost:5716`
- Web: `https://localhost:5717`
- Database: `rentalcommand_tsk397_pass30_clean` in local container `rentalcommand-tsk397-pass30-db`
- External providers: not used after the fix. Email/SMS provider env vars are blanked by `scripts/start-dev.sh` unless `ALLOW_EXTERNAL_NOTIFICATIONS=1` is explicitly set.
- Scan LLM: local Claude CLI path with model `claude-cli:sonnet`.

## Bug Fixed

| ID | Surface | Reproduction evidence | Fix | Regression coverage |
| --- | --- | --- | --- | --- |
| TSK-431 | Local dev launcher notifications | Before the fix, registering the synthetic owner `jordan.pass30.20260625@rentalcommand.local` under `scripts/start-dev.sh` allowed Development user-secrets to configure SendGrid and the Engine attempted a real provider send for the verification email. | `scripts/start-dev.sh` now defaults `ALLOW_EXTERNAL_NOTIFICATIONS=0` and blanks SendGrid, SMTP, SignalWire, Twilio, Telnyx, and Vonage notification env vars for local dev. Explicit opt-in via `ALLOW_EXTERNAL_NOTIFICATIONS=1` is required to use external providers. | `node --test scripts/qa/start-dev-script.test.mjs`; `bash -n scripts/start-dev.sh scripts/qa/start-scan-audit-local.sh`; fresh stack log showed only `[Email suppressed -- not configured]` and no SendGrid/SMS provider calls. |

## Browser And Process Hygiene

- Closed the previous Playwright CLI session `pass30safe`.
- Verified `playwright-cli list` returned `(no browsers)`.
- Reopened a fresh session `pass30continue` and logged in through the app as `jordan.pass30.safe.20260625@rentalcommand.local`.
- Process scan after close showed no lingering `cliDaemon.js`, Playwright, or remote-debugging browser processes before the fresh session was opened.

## Image-Scan Recheck

Started from the already empty-portfolio pass-30 owner state created through real user flows. The following camera-style JPEG scans were verified again through the web app and DB/file proxies:

| Workflow | Browser proof | Persistence proof |
| --- | --- | --- |
| Lease image scan | `/scan/new-rental` created lease `/leases/1` for `Cedar Point Flats`, Unit `1A`, tenant `Avery Ellis`, lease `QA-2026-001-1A`. | SQL join showed 1 property, 1 unit, 1 tenant/lease, rent/deposit `$1125.00`, due day `1`, dates `2026-01-01` to `2027-01-01`; `/lease-file/1` returned `200 image/jpeg` with JPEG magic bytes. |
| Receipt image scan | `/scan/2?type=Expense` created `/accounting/expenses/1` for `Green Thumb Landscaping`, `$63.75`, `Visa`, card last four `4242`, property/unit linked. | SQL showed subtotal `$58.75`, tax `$5.00`, paid date `2026-02-02`, 3 extracted line descriptions; `/expense-file/1` returned `200 image/jpeg`. |
| Rent payment image scan | `/scan/3?type=Payment` created `/accounting/payments/1` for `Avery Ellis`, `$1125.00`, method `Check`, reference `8001`. | SQL showed payment linked to lease `QA-2026-001-1A`, paid/due date `2026-02-03`, bank `First QA Bank`; `/payment-file/1` returned `200 image/jpeg`. |
| Rental application image scan | `/scan/4?type=Application` created `/applications/1`, then approving it created tenant `Gray Johnson` and exposed `Create lease` and `View tenant`. | SQL showed application `Approved`, monthly income `$3850.00`, requested `Cedar Point Flats` Unit `1A`, `ApprovedTenantId=2`; `/application-file/1` returned `200 image/jpeg`. |
| Work-order image scan | `/scan/5?type=WorkOrder` created `/maintenance/1` for `Front door lock sticks`; detail showed status controls, property/unit link, history, and Photos & documents row. | SQL showed WorkOrder `1` linked to property `Cedar Point Flats`, Unit `1A`, tenant `Avery Ellis`, lease `QA-2026-001-1A`; `/workorder-file/1` returned `200 image/jpeg`. |

Stored-file proof: the local DB contained 10 `StoredFiles` rows, one source JPEG plus one thumbnail JPEG for each of Lease, Expense, Payment, Application, and WorkOrder. All record-level source proxies returned `200`, `image/jpeg`, and JPEG magic bytes.

Screenshots:

- `output/playwright/pass30-lease-photo-review.png`
- `output/playwright/pass30-expense-image-review.png`
- `output/playwright/pass30-payment-image-review.png`
- `output/playwright/pass30-application-image-review.png`
- `output/playwright/pass30-workorder-image-review.png`

## Remaining Boundaries

- Plaid Link and QuickBooks OAuth remain blocked by the explicit production/sensitive-provider rule until sandbox credentials are approved.
- The pass-94 remaining DB-side lane should be treated as source-first only. Current code shows the provider retry path stores neutral DTOs rather than raw provider JSON; any further DB-side work in accounting/provider mapping must be proven against the exact current source, not the older handoff wording.
