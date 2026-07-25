# Scenario 09 Result — Foundation Operations & Communications

**Run:** `2026-07-15-001`  
**Scenario:** `Docs/Testing/Scenarios/09-foundation-operations-communications.md`  
**Preview:** `https://redacted-host.example.invalid`  
**Initial source SHA:** `9a20158a4dbc0e3ac177ddd75d369e5c65ab55ec`  
**Live source SHA:** `8419a55ff9ae850ef833431ac1c5cff65fd85bd7`
**Status:** Partial pass — core work, messages, and notification configuration are usable; two
financial reads and the team-recipient preview require the source fixes or further query work noted
below, and activated Owner/Tenant fixtures were unavailable for live persona proof

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
- **Original actual:** The ledger itself loaded 348 records, while all four cards displayed `$0.00`
  because `/api/v1/accounting/summary` was aborted by the 20-second browser timeout. The unrelated
  `/api/v1/accounting/reports` request was also started on the Ledger tab and aborted.
- **Evidence:** An authenticated direct request returned the correct summary — `$156,371.25`
  collected, `$5,175` outstanding/overdue, and `$14,710` expenses — but still took 8.39 seconds on
  a warm run and intermittently exceeded 20 seconds. The reports endpoint exceeded 20 seconds.
  `GetSummaryCoreAsync` currently executes four sequential aggregate statements, while
  `GetReportsCoreAsync` executes several more; the tenant-income aggregate was observed consuming a
  preview database core.
- **Refreshed result:** The summary now loads and renders `$156,371.25` collected, `$5,175.00`
  outstanding, `$5,175.00` overdue, and `$14,710.00` expenses. The 348-row ledger loads on its own,
  and the Reports request does not start until the Reports tab is selected. Reports still aborts at
  the 20-second client timeout on all three retries, but the page now says **Could not load
  accounting reports** and offers Retry instead of fabricating zero values.
- **Remaining boundary:** A bounded source review found several sequential database reads in
  `GetReportsCoreAsync`, including repeated tenant-income work, but did not prove one dominant plan
  node. The query needs focused server-side redesign/profiling; no speculative compatibility path was
  added during this walkthrough.
- **Landlord impact:** A landlord must never be told that populated books contain no money simply
  because a report query is slow. Separating the reads also prevents the routine Ledger workflow
  from competing with year-end/report calculations.

### OPS-004 — Security Deposits still exceeds the client timeout

- **Severity:** High
- **Role/route:** Workspace Administrator at `/deposits`
- **Reproduction:** Open Security Deposits in the supplied sample portfolio.
- **Expected:** The authorized, paged deposit register loads its first 20 rows.
- **Actual:** The grid remains on **Loading…**. All three browser attempts to
  `/api/v1/tenant-accounts/deposits/page?take=20` were aborted at the 20-second client timeout.
- **Code boundary:** Deposit authorization originally built two complete property authorization
  queries and UNIONed them. `TenantAccountDepositAuthorization` now correctly uses the multi-
  capability `WhereAuthorized` overload, so one database predicate accepts either
  `money.deposits.manage` or `leasing.deposits.read`.
- **Refreshed result:** Removing the duplicated authorization graph was necessary but not sufficient.
  The count/page query still exceeds 20 seconds and all three browser attempts abort. The live build
  then incorrectly renders **No security deposit accounts yet**.
- **Additional source fix:** The deposits page now renders an explicit, retryable load error and says
  that no deposit records were changed, rather than presenting a timeout as an empty register.
- **Live proof:** after the slow request exhausted its retry window, `/deposits` rendered `Security
  deposits could not be loaded`, `Try again. No deposit records have been changed.`, and a **Try
  again** button. It no longer lies that the register is empty. The server-side query remains a
  release-performance blocker.
- **Remaining boundary:** The count/page statements still compose canonical access, lifecycle, and
  `vw_security_deposit_balances`. A bounded review did not prove a dominant plan node, so this result
  records the blocker rather than introducing an unproven query rewrite.
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
- **Fix made and refreshed proof:** The PostgreSQL path now subtracts the two timestamps and projects
  `TimeSpan.TotalHours`, which Npgsql translates to server-side interval/epoch SQL. The SQLite test
  provider retains its own DB-side ticks expression. The vendor page now renders an explicit
  scorecard error with **Try again** instead of displaying false empty data.
- **Refreshed result:** `GET /api/v1/vendors/1/scorecard` returns HTTP 200. Apex Plumbing renders its
  scorecard with zero ratings/jobs and an em dash for average response time, which is now a real empty
  scorecard rather than a hidden API failure.
- **Landlord impact:** Vendor selection depends on trustworthy response-time and rating history;
  silently hiding a broken scorecard can cause the wrong service provider to be assigned.

### OPS-006 — Team routing recipient preview returns 500 and disappears from the editor

- **Severity:** High for notification administration
- **Role/route:** Workspace Administrator at `/settings/notifications/team-routing`
- **Reproduction:** Open any saved topic, such as **Rent and money**, and choose **Edit**.
- **Expected:** The editor names the current recipients or the Workspace Administrator fallback,
  including each person's scope, enabled channels, destination, and saved routing reason.
- **Actual:** `GET /api/v1/team-routing/1/preview` returned HTTP 500 on all three retries. The live
  page silently omitted the promised preview, while the rest of the editor remained editable.
- **Exact root cause:** The API log reports `Unable to translate set operation after client
  projection has been applied` at `NotificationFoundationService.PreviewTeamRoutingAsync`. The query
  concatenated two `IQueryable` branches after directly constructing
  `TeamRoutingRecipientPreview` records.
- **Fix made in source:** Both branches now select the same server-side scalar shape, then perform
  `Concat`, `Distinct`, and ordering before the final DTO projection. The operation remains one
  database-side SQL statement. The web editor also renders an explicit preview failure with Retry
  instead of silently hiding who will receive the topic.
- **Live proof:** opening **Account and security** rendered `Rental Command Admin · Workspace`, the
  saved email destination, enabled In-app/Mobile push/Email channels, and the explicit Workspace
  Administrator fallback. The prior 500 is resolved.
- **Landlord impact:** Responsibility routing is only understandable if the administrator can verify
  the effective recipient and why they receive the alert before saving.

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

### Work order, recurring maintenance, and vendor journeys — PASS

- `/maintenance` loaded 15 seeded work orders with search, status, priority, and date filters.
- Six seeded inspection summaries and the three supplied checklist templates loaded on the same
  Work surface.
- **New Work Order** opened the intended six-step flow: Issue, Triage, Location, Schedule, People,
  and Budget. The tester cancelled before submission, so no sample work order was created.
- `/maintenance/recurring` loaded its valid empty state. **New Recurring Task** opened a focused
  editor for title, property/unit, vendor, cadence, next due date/time, expected cost, priority,
  category, notes, and Active state. The tester cancelled without creating a task.
- `/vendors` loaded six seeded vendors with contact and 1099/W-9 compliance data, search, add,
  edit, delete, and row-detail actions.
- The Apex Plumbing detail displayed the expected contact and tax/compliance information. Its
  scorecard now returns 200 and renders the legitimate empty-state values, verifying OPS-005.

### Banking and reconciliation — PARTIAL PASS

- Reconciliation and bank import controls loaded without connecting an external provider.
- The tester imported one synthetic JSON bank transaction and assigned it to Westview Four-Plex.
  This is the only banking mutation in the shared sample workspace.
- Plaid remains visibly unavailable because it is not configured in this preview. No real bank,
  payment processor, or external money movement was invoked.

### Messages — PASS

- `/messages` loaded an empty conversation list and a **New conversation** dialog containing the
  available tenant relationships.
- The tester created one deliberately portal-only sample conversation for Tyler Anderson at
  Eastland 8-Plex:
  - Subject: `QA-OPS-1033 portal conversation`
  - Body: `QA-OPS-1033 portal-only E2E message. No email or SMS should be sent.`
- The app persisted the conversation at `/messages/1`, displayed its Portal channel, and rendered
  the same thread from list/detail context.
- **Back to conversations** returned to canonical `/messages`, verifying OPS-002. No Email or SMS
  channel was selected and no provider-backed message was sent.

### Personal alerts, team routing, and tenant notice configuration — PASS with OPS-006 source fix

- **My alerts** clearly states that the settings apply only to the signed-in account. It names the
  current email/phone destinations and separately explains In-app, Mobile push (not browser push),
  Email, and SMS. Saving the unchanged values returned HTTP 200 and a success toast.
- **Team routing** loaded six distinct topics: Account and security, Applications and leasing,
  Morning Briefing, Owner statements and decisions, Rent and money, and Work orders. Each current
  rule explains its scope, named-recipient state, and explicit Workspace Administrator fallback.
- The retained Morning Briefing schedule is enabled for 08:00 America/New_York and explains that
  its delivery channels come from the routed person's My alerts. No schedule change was made.
- Opening Rent and money exposed OPS-006. The editor itself correctly separates named recipients,
  recipient reasons, fallback, scope, and tenant channels; the source fix restores the missing
  effective-recipient preview and adds a visible failure state.
- **Tenant notices** loaded exactly five supplied automation/template pairs: Past-due rent / late
  fee, Lease expiration / non-renewal, Lease renewal offer, Month-to-month offer, and Rent reminder.
  Every automation independently exposes Off, Draft for review, or Send automatically, timing,
  tenant delivery channels, relationship roles, and delivery-failure behavior.
- Legal notices disable automatic delivery until a jurisdiction-reviewed template version exists.
  Occupants are disabled for legal notices; guarantors require explicit eligibility. Courtesy and
  operational notices may include occupants.
- Expanding Rent reminder loaded the supplied subject/body and six documented merge fields. The UI
  explains that saving or restoring creates and binds a new immutable workspace version instead of
  overwriting template history. No template or automation was changed.
- `/notices` loaded the Draft view and **Generate drafts** completed with HTTP 200. No relationship
  currently met the generation conditions, so the list correctly remained empty. No notice was
  approved or delivered.

### Relationship-scoped shells — ADMINISTRATOR DENIAL PASS; PERSONA PROOF BLOCKED

- Direct `/owner` navigation as the Workspace Administrator returned HTTP 403 and the generic
  current-access denial. It did not render owner data.
- Direct `/portal` navigation as the Workspace Administrator returned to the authorized Management
  landing and did not render tenant navigation or tenant data.
- The shared refreshed preview has no activated Owner or Tenant credential fixture available to this
  lane. Creating a new legal relationship merely to manufacture credentials would add unrelated
  lifecycle data, so live Owner/Tenant positive-path proof remains blocked and is not overstated.
- Web and mobile policies were read before exploration: both select a relationship-specific shell
  before capability routes and fail closed for management destinations. This is source evidence,
  not a substitute for a future activated-persona browser/device run.

### Mobile parity boundary

- Mobile route policy, role shell, notification repository, and action surfaces were inspected for
  the same Management/Maintenance/Owner/Tenant boundaries. No physical-phone session was requested
  for this pass, so no mobile behavior is claimed as live proof.
- A focused future device pass should use activated Maintenance, Owner, and Tenant fixtures and
  verify assigned-work, relationship account, message, alert, and notice destinations.

## Fixes made during this scenario

- `web/src/routes/(protected)/messages/+page.svelte` — made **Back to conversations** use the
  canonical `/messages` destination instead of browser-history/nonexistent-route behavior.
- `web/src/routes/(protected)/accounting/+page.svelte` — fetch Reports only when its tab is opened,
  and show explicit retryable failures instead of silently converting timed-out money data to zero.
- `RentalCommand.Api/Services/Domain/AccountingService.cs` — combine the all-time accounting
  summary into two database-side statements instead of four sequential reads.
- `RentalCommand.Api.Tests/Domain/AccountingServiceTests.cs` — assert the revised two-statement SQL
  shape without allowing client-side aggregation.
- `RentalCommand.Api/Services/Domain/TenantAccountDepositAuthorization.cs` — authorize either
  deposit capability through one DB-side predicate instead of UNIONing two full access graphs.
- `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs` — require both accepted
  capability keys, no authorization UNION, and continued denial of balance-only access.
- `RentalCommand.Api/Services/Domain/VendorDispatchService.cs` — use Npgsql-translatable,
  database-side timestamp subtraction for average vendor response hours.
- `web/src/routes/(protected)/vendors/[id]/+page.svelte` — distinguish a scorecard request failure
  from a legitimate vendor with no scorecard and provide a retry action.
- `web/src/routes/(protected)/deposits/+page.svelte` — distinguish a timed-out deposit register from
  a legitimate empty workspace and provide Retry.
- `RentalCommand.Api/Services/Domain/NotificationFoundationService.cs` — keep the team-recipient
  set operation DB-translatable by projecting its DTO only after the set/order operations.
- `web/src/routes/(protected)/settings/notifications/team-routing/+page.svelte` — show preview load
  failures and Retry instead of silently hiding effective recipients.

## Screenshots and external effects

No screenshots, videos, traces, or retained Playwright artifacts were captured. The browser created
one synthetic imported bank transaction and one portal-only conversation in sample data. No real
bank was connected, no provider-backed payment was attempted, and no Email/SMS delivery was sent.
