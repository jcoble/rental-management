# Scenario 09 Result — Foundation Operations & Communications

**Run:** `2026-07-15-001`  
**Scenario:** `Docs/Testing/Scenarios/09-foundation-operations-communications.md`  
**Preview:** `https://redacted-host.example.invalid`  
**Initial source SHA:** `9a20158a4dbc0e3ac177ddd75d369e5c65ab55ec`  
**Live source SHA:** `2404ec7abc802477569d0dfe7d4fec60b2760636`
**Status:** In progress — authenticated browser exploration underway

## Implementation read before exploration

The tester traced the role shells, client routes, endpoint modules, controller routes, and primary
query services before opening the browser. The reviewed surfaces include:

- Web navigation and authorization: `AppShell.svelte` and `experience-policy.ts`
- Mobile navigation and authorization: `app_router.dart`, `mobile_access_policy.dart`,
  `mobile_role_shell.dart`, `mobile_shell_actions.dart`, and `settings_screen.dart`
- Money: accounting, banking, expenses, payments, deposits, and owner reports
- Work: work orders, responsibility, vendors, recurring maintenance, and inspections
- Communications: portal messages, personal alerts, team routing, tenant notice policies,
  templates, drafts, and delivery status
- Relationship experiences: owner and tenant web/mobile shells and their API controllers

The reviewed web endpoint paths align with their controller route sets. The role policy separates
management, leasing, maintenance, owner, and tenant experiences; relationship identities are denied
management destinations in the client policy. Property Managers receive property-scoped operational
money/reporting capabilities without workspace bank-connection, billing, integration, team, or
security administration.

Focused query-shape inspection of `NotificationFoundationService`, `OwnerPortalService`, and
`WorkOrderService` found filtering, sorting, and paging composed on `IQueryable` before async
materialization. No client-side grouping, load-then-filter, or per-row query defect has been proven in
those reviewed paths. This is a focused check, not a repository-wide SQL certification.

## Release gate found before the refreshed preview

### OPS-001 — Password form cannot establish the web session

- **Severity:** Critical / release blocker
- **Role/route:** Seeded Workspace Administrator at `/login`
- **Reproduction:** Open the stable preview, enter the seeded administrator credentials, and submit
  the sign-in form.
- **Expected:** The web session is established and the user lands in the authenticated management
  experience.
- **Actual:** The button briefly enters its pending state, the browser remains at `/login`, and no
  useful inline error is rendered.
- **Evidence:** A direct `POST /api/v1/auth/login` with the same credentials returned HTTP 200 and a
  Workspace Administrator/Management access envelope. The real Svelte form instead issued
  `POST https://redacted-host.example.invalid/login`, which returned HTTP 403 in Playwright's
  network/console output.
- **Code boundary:** Svelte login action/proxy/origin handling, rather than credential validation in
  the API, because the API login succeeds independently.
- **Suggested fix:** Correct the Svelte server action's preview origin/proxy request handling and
  surface its server error in the form. Re-verify by establishing a cookie-backed browser session.
- **Landlord impact:** Nobody can enter the product, so no operations, communication, role, or
  mobile-parity journey can be certified.

The preview fix was deployed at the live source SHA above. The same password form now establishes a
cookie-backed session, and Google sign-in is discoverable on the login page. OPS-001 is therefore a
verified deployment/configuration fix at the refreshed SHA.

### OPS-002 — Direct message detail has an unsafe/nonexistent Back destination

- **Severity:** Medium
- **Role/route:** Management message detail at `/messages/{id}`
- **Reproduction:** Open a conversation detail through a direct link or a history entry that did not
  originate at the conversation list, then activate **Back to conversations**.
- **Expected:** The canonical conversation list at `/messages` opens.
- **Actual:** The handler used `history.back()` whenever any browser history existed, which could
  leave the app; its no-history fallback navigated to nonexistent `/notifications`, which is not in
  the authenticated route catalog and therefore fails closed.
- **Evidence/code boundary:** `web/src/routes/(protected)/messages/+page.svelte`, `backToList()`.
  The repository contains no protected `/notifications` page or route-access rule.
- **Fix made:** The handler now always opens `/messages` with `noScroll` and `keepFocus`, matching the
  button's accessible label and giving direct links deterministic recovery.
- **Landlord impact:** A landlord following a notification or shared thread link can reliably return
  to the rest of their conversations instead of reaching a 403 or an unrelated prior page.

### OPS-003 — Money summary/report timeouts silently display populated books as zero

- **Severity:** High
- **Role/route:** Workspace Administrator at `/accounting`
- **Reproduction:** Open Money after loading the supplied sample portfolio.
- **Expected:** The four all-time cards show the same collected, receivable, overdue, and expense
  values returned by the accounting API. The Ledger tab should not build the separate Reports view.
- **Actual:** The ledger itself loaded 348 records, while all four cards displayed `$0.00` because
  `/api/v1/accounting/summary` was aborted by the 20-second browser timeout. The unrelated
  `/api/v1/accounting/reports` request was also started on the Ledger tab and aborted.
- **Evidence:** An authenticated direct request returned the correct summary — `$156,371.25`
  collected, `$5,175` outstanding/overdue, and `$14,710` expenses — but still took 8.39 seconds on
  a warm run and intermittently exceeded 20 seconds. The reports endpoint exceeded 20 seconds.
  `GetSummaryCoreAsync` currently executes four sequential aggregate statements, while
  `GetReportsCoreAsync` executes several more; the tenant-income aggregate was observed consuming a
  preview database core.
- **Fix made in source:** The web page no longer fetches Reports until the Reports tab is selected.
  Summary/report failures now render **Unavailable** or a retryable error instead of false `$0.00`
  values. The root runner is diagnosing the PostgreSQL execution-plan/JIT contribution and owns the
  serialized preview refresh.
- **Landlord impact:** A landlord must never be told that populated books contain no money simply
  because a report query is slow. Separating the reads also prevents the routine Ledger workflow
  from competing with year-end/report calculations.

### OPS-004 — Security Deposits never finishes loading because authorization is duplicated

- **Severity:** High
- **Role/route:** Workspace Administrator at `/deposits`
- **Reproduction:** Open Security Deposits in the supplied sample portfolio.
- **Expected:** The authorized, paged deposit register loads its first 20 rows.
- **Actual:** The grid remains on **Loading…**. All three browser attempts to
  `/api/v1/tenant-accounts/deposits/page?take=20` were aborted at the 20-second client timeout.
- **Code boundary:** Deposit authorization built two complete property authorization queries and
  UNIONed them. That duplicated the session, access-revision, membership, role-assignment,
  capability, and selected-property graph inside both the server-side count and page statements.
- **Fix made in source:** `TenantAccountDepositAuthorization` now uses the existing multi-capability
  `WhereAuthorized` overload, so one database predicate accepts either `money.deposits.manage` or
  `leasing.deposits.read`. The SQL translation test now requires both capability keys, no UNION,
  and continued denial of balance-only access.
- **Landlord impact:** Deposit funds are a legal/financial operational surface; a permanent loading
  state makes received funds, deductions, refunds, and move-out handling unusable.

### OPS-005 — Vendor detail fails its scorecard request and hides the failure as empty data

- **Severity:** Medium
- **Role/route:** Workspace Administrator at `/vendors/{id}`
- **Reproduction:** Open any seeded vendor from the Vendors list.
- **Expected:** The scorecard renders cached rating/job counts and the database-computed average
  response time, or clearly reports that the request failed.
- **Actual:** `GET /api/v1/vendors/1/scorecard` returned HTTP 500 on all three query retries. The
  page then rendered **No scorecard yet**, making an API failure indistinguishable from a vendor
  with no performance history.
- **Code boundary:** `VendorDispatchService.GetScorecardAsync` projected `DateTime.Ticks` inside
  the average query. SQLite translated that expression in the focused service test, but Npgsql does
  not translate it. This failed before PostgreSQL could execute the aggregate.
- **Fix made in source:** The PostgreSQL path now subtracts the two timestamps and projects
  `TimeSpan.TotalHours`, which Npgsql translates to server-side interval/epoch SQL. The SQLite test
  provider retains its own DB-side ticks expression. The vendor page now renders an explicit
  scorecard error with **Try again** instead of displaying false empty data.
- **Landlord impact:** Vendor selection depends on trustworthy response-time and rating history;
  silently hiding a broken scorecard can cause the wrong service provider to be assigned.

## Browser journeys

### Live gate and sample-data startup — PASS

- Full browser viewport: 1440×1000.
- The `/login` page displayed password sign-in and **Sign in with Google**.
- `admin@rentalcommand.local` successfully signed in and reached `/choose-setup`.
- **Explore with sample data** completed and redirected to the authenticated Dashboard.
- The Dashboard loaded operational sample data (20 Units, 17 occupied, six open work orders,
  briefing items, money totals, appointments, and activity) with no browser-console error.
- The Money navigation group exposed Money, Reconciliation & banking, Security Deposits, and
  Reports without hidden/duplicate top-level destinations.
- `/accounting` loaded its paged ledger (348 records, 20 per page), server-backed search/filter/sort
  controls, and the seven-step New Expense flow with no console error.

### Work order and vendor startup — PASS with OPS-005 pending refreshed-deploy proof

- `/maintenance` loaded 15 seeded work orders with search, status, priority, and date filters.
- Six seeded inspection summaries and the three supplied checklist templates loaded on the same
  Work surface.
- **New Work Order** opened the intended six-step flow: Issue, Triage, Location, Schedule, People,
  and Budget. The tester cancelled before submission, so no sample work order was created.
- `/vendors` loaded six seeded vendors with contact and 1099/W-9 compliance data, search, add,
  edit, delete, and row-detail actions.
- The Apex Plumbing detail displayed the expected contact and tax/compliance information. Its
  scorecard request exposed OPS-005; source is corrected but the live preview has not yet been
  refreshed with that fix.

Remaining live journeys:

- Management money loop
- Work order/vendor/recurring maintenance loop
- Messages, My alerts, Team routing, and Tenant notices
- Owner relationship shell and direct-route denials
- Tenant portal shell and direct-route denials
- Mobile route/action parity and physical-phone follow-up

## Fixes made during this scenario

- `web/src/routes/(protected)/messages/+page.svelte` — made **Back to conversations** use the
  canonical `/messages` destination instead of browser-history/nonexistent-route behavior.
- `web/src/routes/(protected)/accounting/+page.svelte` — fetch Reports only when its tab is opened,
  and show explicit retryable failures instead of silently converting timed-out money data to zero.
- `RentalCommand.Api/Services/Domain/TenantAccountDepositAuthorization.cs` — authorize either
  deposit capability through one DB-side predicate instead of UNIONing two full access graphs.
- `RentalCommand.Api/Services/Domain/VendorDispatchService.cs` — use Npgsql-translatable,
  database-side timestamp subtraction for average vendor response hours.
- `web/src/routes/(protected)/vendors/[id]/+page.svelte` — distinguish a scorecard request failure
  from a legitimate vendor with no scorecard and provide a retry action.

Additional obvious in-scope failures found after the refreshed preview will be fixed directly in
the shared foundation worktree and handed to the root runner for serialized build, test, and
preview refresh.

## Screenshots and external effects

No screenshots, videos, or traces were captured. No real bank was connected, no provider-backed
payment was attempted, and no Email/SMS delivery was sent.
