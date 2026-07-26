# TSK-747 Lane 3 discovery — money, intake, settings, reporting, help, and admin

**Status:** `DONE_WITH_CONCERNS`

Lane 3 is fully inventoried, but it is not fully browser-proved. Thirty routes/states were
visually inspected on the live Azure stack at a verified **1920 × 1080 CSS-pixel viewport**.
Four token-, role-, or seeded-data-dependent routes were source-reviewed only:
`/scan/[draftId]`, `/scan/batch/[id]`, `/sign/[token]`, and `/apply/[token]`. The valid-session
state of `/plaid/auth`, the staff-only `/profile` page, `/owner/security`, and
`/superadmin/engine` also remain only partially proved because the available signed-in account
could not reach those states.

No production code was changed in this lane. This document is the discovery, scope boundary,
and implementation handoff.

## Audit method and decision standard

- Live target: `https://rental-command.chimp-map.ts.net/`
- Browser: the user's connected Chrome session, because the in-app Browser runtime was not
  available.
- Viewport: explicitly overridden and verified in-page as `innerWidth = 1920`,
  `innerHeight = 1080`, `devicePixelRatio = 1`. The override was reset after the audit.
- Browser proof means the route or stated route state rendered on the live stack. A route that
  redirected, returned 404, or showed only an expired callback is labelled as such.
- Source proof means the route and its directly used components were inspected, including
  loading, error, empty, and permission branches where present.
- The review applies the existing app's design language. It does not introduce a new visual
  theme. The intended result is calmer hierarchy, fewer containers, plain-English outcomes,
  progressive disclosure, and the existing app `Select`, `FormStepper`, `HelpPopover`,
  `PageHeader`, and `DataGrid` patterns.
- Legally load-bearing language such as **Schedule E**, **1099**, **W-9**, **reconcile**, and
  **security deposit** must remain discoverable. It should be glossed in plain English, not
  renamed out of existence.

### Checklist notation

- **V** — visual hierarchy and density
- **W** — wording and mental model
- **N** — navigation and return path
- **C** — controls and interaction consistency
- **P** — progressive disclosure
- **H** — contextual help and documentation
- **S** — loading, empty, error, success, and permission states

## Top five Lane 3 failures

### 1. Internal identifiers and raw enum names are shown as if they were user concepts

This is the most pervasive wording failure. Examples visually proved on the live stack include
`PaymentReceipt`, `TenantLedger`, `LateFeeCharge`, `DEMO-LM-ACTIVE-007`,
`DEMO-TA-ACTIVE-007`, `DEMO-AGR-ACTIVE-001-V1`, `Ledger entry #372`, and
`Relationship DEMO-LM-ACTIVE-003`. The scan-review source also builds visible context from
`Property #`, `Unit #`, `Rental relationship #`, `Agreement #`, `Rental account #`, and
`Ledger entry #`.

These values are valid implementation identifiers, not usable navigation labels. The visible
identity should be **property → unit → resident**, with dates or amounts where needed to
disambiguate. A technical reference can remain under a collapsed “Technical details” section
only where support or audit work genuinely needs it.

### 2. Integration and audit screens speak to developers instead of customers

The Banking page tells an end user:

- “Plaid is not configured”
- “Environment: sandbox”
- “Set `Plaid:ClientId` and `Plaid:Secret` in server secrets”

The Team page says an email is “queued”; the forensic audit page describes “raw before → after
values”; QuickBooks asks the user to have an administrator add provider keys. These are
deployment or implementation details. The primary state should answer: **Is this available?
What can I do next? Who can enable it?** Technical configuration belongs in an admin-only
disclosure or deployment documentation.

### 3. Complex intake work is presented as one long form instead of one decision at a time

`/scan/[draftId]` is a 2,231-line route with destination selection, rental matching, extracted
data, entity-specific fields, validation, and confirmation in one workspace. The public
application similarly presents property choice, applicant details, employment/income, and
consent in one form. Spreadsheet import has numbered headings but not a durable step state.

The correct interaction is a short stepper:

1. **What did we receive?**
2. **Where does it belong?**
3. **Check the important details**
4. **Confirm what Rental Command will create**

Optional and technical fields remain available under “More details”; they are not deleted.

### 4. Reports, Tax, Settings, and several Money surfaces are card or form walls

Reports opens as a three-column catalog of equal-weight cards. Tax repeats a large section per
property. Settings combines eight tabs with a long configuration form and duplicated entry
points. Accounting starts with four colored KPI cards, then tabs, filters, and a dense grid.
Past Due stretches each resident into a large full-width card.

The redesign should lead with a question or outcome:

- “What do you need to answer?” for Reports
- “Get my accountant packet” and “Review tax details” for Tax
- a task-based Settings index
- “Money needing attention” before totals and ledger history

Use one dominant work area, one compact summary strip, and disclosures for secondary settings.
Do not replace one card wall with another.

### 5. Contextual Help is disconnected from the live workflow

Many difficult screens have no route-level link to the already-written help library. More
seriously, the Security Deposits article tells users to click **New Holding** and
**Create Holding**, but the live Deposits page now auto-prepares accounts and offers
**Record funds**. This is a confirmed documentation defect, not a preference.

Every complex page should have:

- one concise “How this works” entry near the page title;
- inline help only for unfamiliar or legally important terms;
- a current `/docs/[slug]` article using the same labels as the UI.

## Per-route review checklist

Every checkbox means the route was reviewed at the stated proof level. It does **not** turn a
source-only or partial-state review into visual proof.

| Reviewed | Route | Proof | V / W / N / C / P / H / S notes |
|---|---|---|---|
| [x] | `/accounting` | Browser + source | **V:** four colored KPI cards, tabs, filters, then a dense grid compete for first attention. **W:** raw kinds such as `PaymentReceipt`, `TenantLedger`, and `LateFeeCharge`. **N:** record rows open details, but the unit/resident return context is weak. **C:** app `Select` is used. **P:** advanced filters are always open. **H:** isolated tooltips exist; no clear route-level article. **S:** loading, error, empty, and populated states exist. |
| [x] | `/accounting/expenses/[id]` | Browser with `/accounting/expenses/70` + source | **V:** important facts are readable but spread across large bordered panels. **W:** “Capitalize” and “Receipt details (raw)” are unexplained. **N:** returns only to Accounting, not a visible unit context. **C:** existing dialogs/actions are consistent. **P:** raw receipt data should be technical disclosure content. **H:** no route-level article. **S:** load/error/delete states are implemented. |
| [x] | `/accounting/past-due` | Browser + source | **V:** five very tall, repeated cards use excessive space. **W:** “Who’s behind” is unusually colloquial; core amounts/actions are otherwise clear. **N:** “Record receipt” does not foreground the resident's unit context. **C:** payment method uses app `Select`. **P:** secondary contact/action details can collapse. **H:** no route-level help. **S:** loading, error, empty, dialog, and paging states exist. |
| [x] | `/accounting/year-end` | Browser + source | **V:** live route remained on a centered spinner; the loaded source layout is another stacked report. **W:** “depreciation and debt service finally in the picture” is dramatic rather than instructional. **N:** year-end relationship to Tax/Reports is unclear. **C:** both filters use native `<select>` controls. **P:** accountant detail can follow a concise summary. **H:** no direct article link. **S:** source has loading and error, but the live loading state did not resolve during the audit. |
| [x] | `/banking` | Browser + source | **V:** four equal KPI cards plus review/connection/import cards dilute the main action. **W:** Plaid secrets and sandbox environment leak into customer copy. **N:** no clear “what should I do now?” path when unavailable. **C:** transaction controls use app `Select`. **P:** technical setup belongs behind an admin disclosure. **H:** no visible Banking article link. **S:** unavailable and empty states render, but the unavailable state is developer-facing. |
| [x] | `/plaid/auth` | Browser expired-state only + source | **V:** a tiny message sits in a nearly empty viewport. **W:** copy says the user can return to Banking. **N:** there is no visible button or link to do that. **C/P/H:** not applicable in expired state. **S:** expired state proved; valid callback and successful return were not proved. |
| [x] | `/deposits` | Browser + source | **V:** usable table, but raw identifiers dominate each row. **W:** relationship numbers are not customer language. **N:** rows open a deposit detail but do not foreground unit/resident folding. **C:** app `Select` is used. **P:** secondary identifiers should be hidden. **H:** no current article link. **S:** loading, error, empty, and populated states exist. |
| [x] | `/deposits/[id]` | Browser with `/deposits/27` + source | **V:** actions and totals are findable; several panels are empty or oversized. **W:** raw relationship, rental-account, and agreement IDs are visible. **N:** breadcrumb does not make the unit the organizing record. **C:** actions are consistent. **P:** technical IDs and empty photo area should collapse or appear only when useful. **H:** no route-level help. **S:** load/error and mutation states exist. |
| [x] | `/tax` | Browser + source | **V:** year-end packet, 1099 checklist, and one large card per property form a long wall. **W:** necessary tax terms are present but not consistently glossed. **N:** Reports vs. Tax ownership is unclear. **C:** app `Select` is used. **P:** lead with accountant packet; expand property detail on request. **H:** no direct Taxes/1099 article link. **S:** loading/error and year-data states exist. |
| [x] | `/reports` | Browser + source | **V:** three-column equal-card catalog with colored icon tiles. **W:** P&L, General Ledger, Schedule E, and Aging appear before the user chooses an outcome. **N:** “Opens existing” reveals routing architecture, not user intent. **C:** cards are the only discovery control; no search. **P:** common reports and advanced reports need tiers. **H:** no Reports article link. **S:** catalog and paid custom-report callout render. |
| [x] | `/reports/[report]` | Browser with `/reports/rent-roll` + source | **V:** large filter area above a sparse grid. **W:** raw agreement number displayed. **N:** results do not visibly fold into the unit-centered record. **C:** shared report actions are usable. **P:** filters should start with the two most likely inputs. **H:** no route-level help. **S:** source supports multiple report/loading/error/empty variants; only Rent Roll was visually proved. |
| [x] | `/tenant-accounts/[tenantAccountId]/entries/[tenantLedgerEntryId]` | Browser with `/tenant-accounts/23/entries/372` + source | **V:** receipt amount is prominent. **W:** ledger entry number, account number, relationship number, “append-only posting,” and “Correct payment” expose implementation/accounting language. **N:** no obvious unit-centered return path. **C:** correction action exists. **P:** audit details should be a disclosure. **H:** no Recording Payments article link. **S:** loading/error/correction states exist. |
| [x] | `/scan` | Browser + source | **V:** seven equal capture choices, a large dropzone, voice input, tabs, and history all compete. **W:** mostly approachable; “Auto / classify” still describes system behavior. **N:** scan is a good entry point but does not state the next review step. **C:** capture controls are consistent. **P:** one primary “Upload any document” action should lead; manual type selection can be secondary. **H:** field help exists, but no obvious route-level Scanning article. **S:** loading, empty history, queued, processing, failed, and retry handling exist. |
| [x] | `/scan/[draftId]` | Source only — no seeded draft | **V:** two-column workspace plus proposed-command panel and many stacked forms. **W:** visible `#id` context and entity terminology. **N:** context can inherit internal IDs; post-confirm destination varies. **C:** app `Select` is used consistently. **P:** strongest stepper candidate in Lane 3. **H:** no route-level Reviewing Drafts article. **S:** source includes loading, error, failed, processing, retry, validation, and confirm states; none visually proved. |
| [x] | `/scan/batch` | Browser + source | **V:** simple and focused. **W:** “Import 0 leases” is clear once files are selected. **N:** cancel path is visible. **C:** dropzone/buttons match the app. **P:** appropriate for one-step upload. **H:** no batch-specific help. **S:** initial/selected/uploading/error states exist. |
| [x] | `/scan/batch/[id]` | Source only — no seeded batch | **V:** progress plus per-file rows is structurally sound. **W:** “Reading” and “Failed” are understandable. **N:** created leases route to `/leases`, which may conflict with the unit-centered Lane 1 model. **C/P:** review actions are progressive. **H:** no contextual article. **S:** progress, complete, failed, review, and created states exist; none visually proved. |
| [x] | `/scan/new-rental` | Browser + source | **V:** narrow page with two nearly identical, oversized dropzones and excessive dead space. **W:** photo vs. PDF distinction is clear but should not require separate primary choices. **N:** next review step is not explained. **C:** combine into one multi-file/PDF dropzone. **P:** a single input is enough. **H:** no direct article. **S:** upload state comes from `LeaseFirstImport`. |
| [x] | `/import` | Browser + source | **V:** numbered sections help, but six equal import-type buttons and raw CSV headers are dense. **W:** `firstName`, `lastName`, `email`, and `phone` expose schema naming. **N:** preview/validation outcome is not clear from the opening screen. **C:** file selection is conventional. **P:** make type, upload, map/check, confirm actual step state. **H:** no spreadsheet import article. **S:** source includes upload, parse, preview, validation, submit, error, and result states. |
| [x] | `/ai` | Browser + source | **V:** two-column Briefing/Question structure is understandable. **W:** “Action mode” needs a plain-language explanation before “opt in.” **N:** citation links are useful. **C:** ask/action controls are consistent. **P:** write permissions are already gated. **H:** inline help and document citations are present. **S:** briefing loading state was observed; source includes error/empty/conversation states. |
| [x] | `/settings` | Browser + source | **V:** eight tabs plus a long portfolio form and setup warnings create a settings wall. **W:** “Everything that runs your account” is broad; several labels assume rental-operations knowledge. **N:** sidebar and page tabs duplicate discovery paths. **C:** app `Select` is used. **P:** task-based index first, one focused panel second. **H:** “Walk me through this” and Learn More exist and should be retained. **S:** source includes load/error/save and destructive example-data states. |
| [x] | `/settings/accounting` | Browser + source | **V:** one large mostly empty QuickBooks card. **W:** asks users to have an administrator add QuickBooks keys. **N:** no direct route to current setup guidance. **C:** connect action is disabled/unavailable. **P:** technical configuration can be admin-only. **H:** no direct accounting-integration article. **S:** not-configured state proved; configured/OAuth states source-only. |
| [x] | `/settings/integrations/ai` | Browser + source | **V:** compact, but lacks an explanatory setup path. **W:** “Provider,” “Model,” and “write-only after save” require technical knowledge. **N:** no visible link to the existing AI Provider article. **C:** provider uses native `<select>`, inconsistent with the app. **P:** advanced model choice can follow provider/key basics. **H:** missing direct help. **S:** test, save, activate, validation, and error states exist. |
| [x] | `/settings/security` | Browser + source | **V/W:** simple password form and generally clear. **N:** profile/security relationship depends on role context. **C:** standard form. **P:** appropriate. **H:** no security article, but the task is familiar. **S:** validation/success/error handled by shared account-security component. |
| [x] | `/profile` | Browser redirect + source | **V/W:** source defines a clean staff profile page. **N:** current admin context redirected to `/`, so the role-appropriate route was not visually proved. **C/P/H:** minimal. **S:** redirect behavior proved; staff page unproved. |
| [x] | `/owner/security` | Source only — role blocked | **V/W:** wraps the shared password page. **N:** current role redirected away. **C/P/H/S:** inherited from account-security component; owner experience unproved. |
| [x] | `/docs` | Browser + source | **V:** search, category cards, and a long left contents list all compete. **W:** article titles are mostly plain. **N:** direct slugs work. **C:** search is clear. **P:** recently used or task-based help could lead before categories. **H:** this is the help destination. **S:** index/search/no-results states exist. |
| [x] | `/docs/[slug]` | Browser with `/docs/security-deposits` + source | **V:** article is readable. **W:** confirmed stale workflow: “New Holding” and “Create Holding” are not present on live Deposits. **N:** article does not deep-link back to the task. **C/P:** article structure is usable. **H:** content contract is broken. **S:** article/not-found handling exists. |
| [x] | `/privacy` | Browser + source | **V/W:** readable public policy at an appropriate line length. **N:** standalone public route is reasonable. **C/P/H:** not applicable. **S:** static content renders. |
| [x] | `/sign/[token]` | Source only — token blocked | **V:** source uses a document preview plus signature workspace. **W:** consent, decline, and completion copy are mostly clear. **N:** token owns the flow. **C:** typed/drawn signature and decline dialog are present. **P:** acceptable focused flow; do not add decorative steps without browser proof. **H:** no help link, which may be intentional for a public signing task. **S:** invalid, expired, signed, declined, loading, and error states exist; none visually proved. |
| [x] | `/apply/[token]` | Source only — token blocked | **V:** property choice, personal details, employment/income, and consent are one long form. **W:** mostly plain; legal consent must remain exact. **N:** token owns the flow. **C:** app `Select` and date controls are used. **P:** strong four-step candidate with save-in-page state. **H:** no application guidance. **S:** loading, missing, error, submitting, and success states exist; none visually proved. |
| [x] | `/admin/users` | Browser + source | **V:** table is reasonably compact. **W:** “queued email” is implementation language, and the current signed-in user displayed “Activation required,” creating a visible contradiction. **N:** invite/edit flow is local. **C:** actions are conventional. **P:** advanced assignments can remain in dialogs. **H:** no Team help. **S:** loading/error/empty/invite/edit/activation states exist. |
| [x] | `/admin/audit` | Browser + source | **V:** large empty result area after filters. **W:** “Audit — forensic,” IP address, and raw before/after values are specialist language. **N:** should remain reachable from Activity History's Advanced path, not primary navigation. **C:** filters are usable. **P:** raw JSON belongs behind row disclosure. **H:** no explanation of when to use it. **S:** live route showed “Failed to load audit trail”; source has loading/error/empty/paged rows. |
| [x] | `/audit` | Browser + source | **V:** compact activity-history workspace. **W:** clearer than admin audit. **N:** Advanced path exists for privileged users. **C:** filters are conventional. **P:** correct separation from forensic detail. **H:** no Activity History article. **S:** loading observed; source has error/empty/results. |
| [x] | `/superadmin/engine` | Source only — role blocked | **V/W:** operator-only surface; ordinary rental-user simplification is not its primary goal. **N:** correct fail-closed 404 for non-platform admins. **C/P/H/S:** source reviewed only; no platform-admin visual proof. |

## Proposed information structure and copy

### A. Money becomes “attention first, history second”

**Accounting opening order**

1. **Needs attention** — unmatched bank items, overdue rent, or failed postings.
2. **This month** — received, spent, and net in one neutral summary strip.
3. **Money history** — search and recent entries.
4. **More filters** — kind, status, category, property, and date range.
5. **Reports and tax details** — links, not competing tabs above the core task.

**Plain-English display mapping**

| Current visible value | Proposed visible label |
|---|---|
| `PaymentReceipt` | Payment received |
| `TenantLedger` | Rent or resident charge |
| `LateFeeCharge` | Late fee |
| `ApplicationFee` | Application fee |
| `PaymentReversal` or correction posting | Payment correction |
| `Relationship DEMO-LM-…` | `{Property} · Unit {unit} · {resident}` |
| `Tenant account DEMO-TA-…` | Rental account for `{resident}` |
| `Ledger entry #372` | Payment recorded `{date}` |
| `Receipt details (raw)` | Technical receipt data |
| `Capitalize` | Track as a long-term property improvement |
| `Correct payment` | Fix this payment |
| `Append-only posting and its audit trail` | Change history for this payment |

Do not alter stored enum values or accounting semantics. Map presentation labels only.

**Deposits**

- Keep **Security deposit** because it is the real legal/user term.
- List by property, unit, resident, amount held, and next move-out action.
- Replace relationship/account/agreement identifiers with a collapsed “Technical details”
  section if support still requires them.
- Empty photo panels should appear only when photos exist or when the user is in the move-out
  workflow.
- Every deposit screen links to a corrected `/docs/security-deposits` article.

**Banking**

- Available state: “Connect a bank” → “Review suggested matches” → “Confirm.”
- Unavailable state: “Bank connections are not available in this workspace yet. Contact your
  workspace administrator, or import a statement instead.”
- Environment names and secret keys never appear in the ordinary page.
- Keep the existing custom `Select`; do not introduce another picker style.

### B. Scan, application, and import become guided work

**Scan review**

Use the existing `FormStepper` and preserve all current mutation, validation, retry, and
idempotency behavior:

1. **Document** — preview, detected type, change type.
2. **Rental** — choose the property/unit/resident using names, never raw IDs.
3. **Details** — show only fields relevant to that document type; optional fields under
   “More details.”
4. **Confirm** — one sentence stating exactly what will be created or updated, plus warnings.

The stepper is presentation state only. It must not split one server-side confirmation command
into multiple commits or HTTP writes.

**New rental scan**

One dropzone: “Add lease photos or a PDF.” Supporting copy: “You can upload several phone
photos; Rental Command will put them in order before review.”

**Spreadsheet import**

1. Choose what the spreadsheet contains.
2. Upload the file.
3. Match and check columns.
4. Confirm how many records will be created or skipped.

Show “First name” in the UI; keep `firstName` only in downloadable templates or mapping detail.

**Public application**

1. Rental preference
2. About you
3. Work and income
4. Review and consent

The legally required consent remains verbatim and visible before submission.

### C. Reports, Tax, and Settings start from user goals

**Reports**

Start with a compact prompt: “What do you need to answer?”

- “What rent is expected right now?” → Rent roll
- “Who still owes money?” → Overdue rent
- “How did a property perform?” → Property income and expenses
- “What should I send my accountant?” → Year-end packet
- “What changed?” → General ledger / activity

Keep report names such as P&L and Schedule E as searchable aliases and secondary subtitles:
“Property income and expenses (P&L).”

**Tax**

Primary action: **Get my accountant packet**.

Secondary sections:

- “Items to check before filing”
- “Properties included”
- “Detailed Schedule E breakdown”
- “1099 forms and missing vendor information”

Property details are collapsed by default. Tax terms are glossed inline and linked to
`/docs/taxes-and-1099`.

**Settings**

The Settings landing page becomes a task index:

- Business and properties
- Team access
- Messages and notifications
- Money connections
- AI assistant
- Sign-in and security
- Import or reset data
- Activity history

Opening a task shows one focused panel. Lane 3 must not restructure the notification subroutes
owned by Lane 2.

### D. Help is part of the feature contract

- Reuse `HelpPopover.svelte` for unfamiliar field-level terms.
- Add one “How this works” link near the title of Accounting, Banking, Deposits, Tax, Reports,
  Scan, Import, and relevant Settings pages.
- Link to existing slugs where possible:
  `accounting-overview`, `banking-and-reconciliation`, `security-deposits`,
  `taxes-and-1099`, `reports`, `scanning-documents`, `reviewing-drafts`, and `ai-provider`.
- Update article copy whenever a button or workflow label changes.
- Add content-contract tests for task-critical articles; parser/search tests alone cannot catch
  stale instructions.

## Exact implementation and test files

The following is the proposed Lane 3 ownership set. New files are marked **new**.

### Money and reporting

Implementation:

- `web/src/routes/(protected)/accounting/+page.svelte`
- `web/src/routes/(protected)/accounting/past-due/+page.svelte`
- `web/src/routes/(protected)/accounting/year-end/+page.svelte`
- `web/src/routes/(protected)/banking/+page.svelte`
- `web/src/routes/(protected)/plaid/auth/+page.svelte`
- `web/src/routes/(protected)/deposits/+page.svelte`
- `web/src/routes/(protected)/deposits/[id]/+page.svelte`
- `web/src/routes/(protected)/tax/+page.svelte`
- `web/src/routes/(protected)/reports/+page.svelte`
- `web/src/routes/(protected)/reports/[report]/+page.svelte`
- `web/src/lib/components/records/ExpenseDetail.svelte`
- `web/src/lib/components/records/PaymentDetail.svelte`
- `web/src/lib/accounting/money-display.ts` **new**
- `RentalCommand.Api/KnowledgeBase/accounting-overview.md`
- `RentalCommand.Api/KnowledgeBase/banking-and-reconciliation.md`
- `RentalCommand.Api/KnowledgeBase/security-deposits.md`
- `RentalCommand.Api/KnowledgeBase/taxes-and-1099.md`
- `RentalCommand.Api/KnowledgeBase/reports.md`

Tests:

- `web/src/lib/accounting/money-display.test.ts` **new**
- `web/src/lib/accounting/expense-receipt-display.test.ts`
- `web/src/lib/accounting/report-ledger-preview.test.ts`
- `web/e2e/year-end.spec.ts`
- `web/e2e/money-clarity.spec.ts` **new**
- `web/e2e/help-content-contract.spec.ts` **new**
- `RentalCommand.Api.Tests/Domain/KnowledgeBaseServiceTests.cs`

### Scan and import

Implementation:

- `web/src/routes/(protected)/scan/+page.svelte`
- `web/src/routes/(protected)/scan/[draftId]/+page.svelte`
- `web/src/routes/(protected)/scan/batch/+page.svelte`
- `web/src/routes/(protected)/scan/batch/[id]/+page.svelte`
- `web/src/routes/(protected)/scan/new-rental/+page.svelte`
- `web/src/routes/(protected)/import/+page.svelte`
- `web/src/lib/components/scan/ScanReviewStepper.svelte` **new**
- `web/src/lib/components/scan/ScanReviewDocumentStep.svelte` **new**
- `web/src/lib/components/scan/ScanReviewRentalStep.svelte` **new**
- `web/src/lib/components/scan/ScanReviewDetailsStep.svelte` **new**
- `web/src/lib/components/scan/ScanReviewConfirmStep.svelte` **new**
- `web/src/lib/scan/scan-copy.ts`
- `web/src/lib/scans/scan-review-state.ts`
- `RentalCommand.Api/KnowledgeBase/how-scanning-works.md`
- `RentalCommand.Api/KnowledgeBase/scanning-documents.md`
- `RentalCommand.Api/KnowledgeBase/reviewing-drafts.md`

Tests:

- `web/src/lib/scan/scan-copy.test.ts`
- `web/src/lib/scans/scan-review-state.test.ts`
- `web/src/lib/scans/scan-review-steps.test.ts` **new**
- `web/e2e/scan.spec.ts`
- `web/e2e/import.spec.ts`
- `web/e2e/scan-review-guided-flow.spec.ts` **new**

### Settings, help, public flows, and admin

Implementation:

- `web/src/routes/(protected)/settings/+page.svelte`
- `web/src/routes/(protected)/settings/accounting/+page.svelte`
- `web/src/routes/(protected)/settings/integrations/ai/+page.svelte`
- `web/src/routes/(protected)/settings/security/+page.svelte`
- `web/src/routes/(protected)/ai/+page.svelte`
- `web/src/routes/(protected)/audit/+page.svelte`
- `web/src/routes/(admin)/admin/users/+page.svelte`
- `web/src/routes/(admin)/admin/audit/+page.svelte`
- `web/src/routes/(public)/docs/+page.svelte`
- `web/src/routes/(public)/docs/[slug]/+page.svelte`
- `web/src/routes/apply/[token]/+page.svelte`
- `web/src/lib/applications/application-step-state.ts` **new**
- `RentalCommand.Api/KnowledgeBase/ai-provider.md`
- `RentalCommand.Api/KnowledgeBase/settings-and-notifications.md`
- `RentalCommand.Api/KnowledgeBase/tenants-and-applications.md`

Tests:

- `web/src/lib/applications/application-step-state.test.ts` **new**
- `web/src/lib/auth/team-assignment-ui.test.ts`
- `web/src/lib/audit/pagination.test.ts`
- `web/e2e/application-guided-flow.spec.ts` **new**
- `web/e2e/settings-clarity.spec.ts` **new**
- `web/e2e/admin-copy-and-states.spec.ts` **new**

## No-overlap boundary

Lane 3 may edit only the files listed above or new files placed in the listed Lane 3 feature
folders.

Lane 3 must **not** edit:

- `web/src/lib/components/AppShell.svelte`
- `web/src/lib/components/CommandCenterNav.svelte`
- `web/src/lib/components/unit/**`
- routes under `web/src/routes/(protected)/units/**`,
  `web/src/routes/(protected)/properties/**`, `web/src/routes/(protected)/leases/**`, or
  `web/src/routes/(protected)/tenants/**`
- `web/src/routes/(protected)/settings/notifications/**`
- `web/src/lib/components/notifications/**`
- `web/src/lib/settings/notification-settings-matrix.test.ts`
- shared primitives under `web/src/lib/components/ui/**`
- `web/src/lib/components/shared/FormStepper.svelte`

Lane 1 owns shell/navigation, Command Center restoration, unit-centered links, and unit/lease
folding. Lane 2 owns notification settings and delivery/routing flows. Lane 3 may consume their
public route/link contracts but must hand off any required change rather than modifying their
files.

Cross-lane handoffs already identified:

1. Money, deposit, and report rows need a Lane 1-supported unit-centered destination contract.
2. `/scan/batch/[id]` currently routes created lease work to `/leases`; Lane 1 should decide the
   Command Center destination.
3. Settings may link to Lane 2 notification routes but must not duplicate or redesign their
   internal navigation.

## Smallest coherent implementation slice

**Slice name:** Lane 3A — Money language, control consistency, and trustworthy help.

This is the smallest slice that materially improves daily usability without touching data
queries, server mutations, notification ownership, or the Command Center shell.

Implementation:

1. Add presentation-only money labels in
   `web/src/lib/accounting/money-display.ts`; map raw entry kinds and technical identifiers to
   property/unit/resident labels already present in each response.
2. Apply those labels in Accounting, Deposits, Rent Roll, Expense Detail, and Payment Detail.
   Move genuinely required IDs to a collapsed “Technical details” section.
3. Replace the native `<select>` elements on Year End and AI Provider with the existing app
   `Select` component.
4. Replace Plaid environment/secret-key copy with an availability state and a useful next
   action. Keep deployment instructions out of the ordinary Banking page.
5. Add route-level help links for Accounting, Banking, Deposits, Tax, and Reports.
6. Correct `security-deposits.md` so it describes auto-prepared deposit accounts,
   **Record funds**, deductions, refunds, and the move-out statement using the live labels.

Acceptance checks:

- No `DEMO-LM-*`, `DEMO-TA-*`, `DEMO-AGR-*`, raw relationship/account IDs, or raw
  `PaymentReceipt`/`TenantLedger` labels appear on the audited customer-facing money routes.
- The user can identify a money record by property, unit, resident, date, and amount.
- Year End and AI Provider contain no native `<select>` controls.
- Banking contains no environment name, configuration key, or secret-name instructions.
- The Security Deposits article's buttons and sequence match the live Deposits flow.
- Loading, empty, error, and mutation behavior remains unchanged.
- Both TypeScript checks and focused browser proof run serially after implementation:
  `pnpm --dir web check:native`, then `pnpm --dir web check`, then focused Playwright at
  1920 × 1080. No parallel heavy build/test processes.
- Any changed data access remains server-side in one translated SQL statement. This slice
  should require no data-access change.

## Concerns that block a clean “done”

1. `/accounting/year-end` did not leave its loading state during live proof. Source has an error
   branch, but the live request/result needs diagnosis before redesign can be accepted.
2. `/admin/audit` rendered its explicit load-failure state on the live stack.
3. Valid Plaid callback, scan draft, scan batch detail, signing, public application, staff
   profile, owner security, and platform-admin states were not available for browser proof.
4. The Team page showed “Activation required” for the currently signed-in user; the state
   derivation or copy needs separate verification.
5. Command Center return destinations are a Lane 1 dependency and must be agreed before Lane 3
   changes record-row navigation.
