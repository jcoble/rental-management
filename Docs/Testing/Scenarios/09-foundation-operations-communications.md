# Scenario 09 — Foundation Operations & Communications

**Run:** `2026-07-15-001`  
**Domain:** Money, maintenance, messages, notifications, and relationship-scoped experiences  
**Suggested tester session:** `foundation-ops`

## Mission

Exercise the operating loop that begins after a rental and household exist: collect and spend
money, complete work, communicate with the tenant and owner, and configure who is notified. Prove
that each role sees one coherent experience rather than a broad management UI with hidden or
non-functional controls.

This pass covers both web and mobile behavior. The web preview is the primary interactive surface;
mobile route/action parity must be checked against the Flutter implementation and, when a device is
available, repeated on the physical phone. Do not create screenshots for this run. Record observable
text, route, response, console, and persisted-state evidence instead.

## Read the implementation first

Follow each journey from navigation to persistence before opening the UI:

- Role and route policy:
  - `web/src/lib/components/AppShell.svelte`
  - `web/src/lib/auth/experience-policy.ts`
  - `mobile/lib/core/router/app_router.dart`
  - `mobile/lib/core/auth/mobile_access_policy.dart`
  - `mobile/lib/features/home/mobile_role_shell.dart`
- Money:
  - `web/src/routes/(protected)/accounting/`, `banking/`, `deposits/`, and `reports/`
  - `web/src/lib/api/endpoints/{accounting,banking,expenses,payments,securityDeposits}.ts`
  - `mobile/lib/features/money/`, `payments/`, `deposits/`, `banking/`, and `owner_reports/`
  - `TenantAccountMoneyController`, `ExpenseController`, `BankingController`, and the related
    atomic mutation/query services
- Work:
  - `web/src/routes/(protected)/maintenance/`, `appointments/`, and `vendors/`
  - `mobile/lib/features/maintenance/`, `vendors/`, and `inspections/`
  - `WorkOrderController`, `WorkOrderResponsibilityController`, `VendorController`, and their
    services
- Communications and notifications:
  - `web/src/routes/(protected)/messages/`, `notices/`, and `settings/notifications/`
  - `mobile/lib/features/messages/`, `notices/`, `notifications/`, and `settings/`
  - `NotificationsController`, `MyAlertsController`, `TenantNoticePoliciesController`,
    `NoticeDraftsController`, and `NotificationFoundationService`
- Relationship experiences:
  - `web/src/routes/(protected)/owner/` and `web/src/routes/(portal)/portal/`
  - `mobile/lib/features/home/owner_landing_screen.dart` and tenant portal screens
  - `OwnerPortalController` and `PortalController`

For every list, report, total, filter, sort, and page, confirm the API implementation remains a
translated DB-side query or database view. Do not accept materialize-then-filter/group, N+1, or
lazy loading.

## Test identities and data hygiene

- Use the seeded Workspace Administrator only to create any missing role assignments.
- Create or use one Property Manager assignment scoped to exactly one test property, one
  Maintenance Technician assigned to exactly one test work order, one relationship-scoped Owner,
  and one Tenant identity tied to the test household.
- Use marker `QA-OPS-<HHMMSS>` for references, vendors, work orders, message text, and descriptions.
- Prefer a new test unit/household or clearly marked seed data. Do not alter another tester's active
  lease, ledger, notice, or work order.
- Do not connect a real bank, send email/SMS, or invoke any provider-backed payment. Portal/in-app
  delivery is safe. Treat configured-provider absence as a product state that must be explained,
  not as permission to invent credentials.

## Journey A — Management money loop

1. From the global Money area, search/filter to the marked household and open the same tenant
   account from its Unit Command Center.
2. Record one receipt through the supported tenant-account flow. Confirm Unit, ledger entry detail,
   tenant account balance, and accounting summary agree without a manual repair or duplicate row.
3. Create one marked expense from the global list and one contextual Unit entry point. Confirm the
   contextual form retains property/unit and the global form requests them exactly once.
4. Open Security Deposits, then fund, deduct, and inspect the refundable balance without exceeding
   the held amount or refunding twice.
5. Open Banking/Reconciliation. Confirm disconnected-provider copy is clear and safe; if a sandbox
   connection exists, inspect reconciliation without destructive actions.
6. Open the owner report/statement for the marked property. Confirm income, expense, distribution,
   and net values reconcile and date/property filters affect rows and totals together.
7. Confirm the Property Manager can perform assigned-property payments, expenses, reports, and
   operational reconciliation, but cannot manage workspace billing, bank connections, payouts,
   integrations, or team/security administration.

Expected invariants:

- Every write uses one caller-owned operation key and reloading/retrying does not duplicate money.
- Currency, account identity, dates, and references agree across global and Unit entry points.
- Property scope is enforced by the API/query; changing an id or direct URL cannot expose another
  property.

## Journey B — Work and vendor loop

1. Create a marked work order globally, selecting property/unit once. Create another from the Unit
   Command Center and confirm the property/unit context is already retained.
2. Create or select a marked vendor, assign responsibility, then move the order through the
   supported New/Open → In progress → Completed path with notes, labor/materials, and completion
   time.
3. Reopen the order from the global Work queue, Unit context, and technician My Work list. All
   entry points must open the same record and timeline.
4. Verify the assigned Maintenance Technician can see/update only the assigned work order, converse,
   and record permitted time/materials. Direct routes to money, tenants, leases, unassigned work,
   team, and notification administration must deny cleanly.
5. Create/inspect recurring maintenance and verify its generated work order is neither missing nor
   duplicated on retry.
6. Create or run an inspection only if the route is exposed for the active capability set. Confirm
   its Unit/work-order links resolve and any unavailable action explains why.

Expected invariants:

- Completion status and completion timestamp persist together.
- Totals reconcile without client-side summing or per-row follow-up queries.
- Vendor and responsibility pickers include only records allowed by the active property/assignment
  scope.

## Journey C — Messages, alerts, and tenant notices

1. From Messages, start a marked portal-only conversation with the test Tenant. Confirm the thread,
   unread state, search/filter behavior, and Unit/Tenant context persist after reload.
2. Reply as the Tenant and confirm Management sees the same conversation and unread transition.
3. In **My alerts**, change one channel for the current user. Confirm the page explicitly describes
   that these are personal preferences and does not imply team or tenant delivery changed.
4. In **Team routing**, inspect each event's recipient rule. Confirm the UI explains which role or
   assigned responsibility receives it; save a safe routing change and verify persistence.
5. In **Tenant notices**, verify every automation independently offers `Off`, `Create draft for
   review`, or `Send automatically` only where permitted. Change a single policy, reload, and confirm
   no unrelated automation changed.
6. Open the supplied template for that policy. Confirm provenance/default content is visible,
   content is editable, reset/restore behavior is explicit, and help links resolve.
7. Generate a notice draft. Edit, cancel, edit/save, choose Portal only, approve, and open the linked
   conversation. Dismiss a different draft and confirm filters reflect both states.
8. Inspect accepted/retry/failure delivery status. Missing Email/SMS provider credentials must
   produce a clear unavailable/failed state without pretending delivery succeeded.

Expected invariants:

- My alerts, Team routing, and Tenant notices are separate records and separate authorization
  surfaces.
- Staff/owner delivery is not silently coupled to tenant delivery.
- Owner/Tenant/Maintenance users may manage their own alerts, but relationship users cannot reach
  administrative routing or tenant-notice policy routes even with a stale capability in the client.

## Journey D — Owner and Tenant experiences

1. As the relationship-scoped Owner, walk Overview, Properties, Statements & documents, Approvals,
   Messages, My alerts, and Security. Confirm no management navigation, Unit command mutations,
   tenant PII beyond the intended projection, or unrelated owner's records appear.
2. As the Tenant, walk Home, Account & lease, Payments, Maintenance, Messages, Notifications,
   Appointments, Profile, My alerts, and Security. Confirm balance and lease agree with Management,
   a marked maintenance request reaches the global Work queue, and portal replies remain in the
   same conversation.
3. For both identities, paste direct management URLs (`/accounting`, `/banking`, `/maintenance`,
   `/settings/notifications/team-routing`, `/admin/users`) and confirm a safe landing/access-denied
   response rather than partial page rendering or leaked data.
4. Repeat the discoverability and denial checks in mobile. Owner and Tenant must land in their
   relationship shells; Maintenance must land in My Work; management-only global hubs and actions
   must not appear.

## Cross-surface and recovery checks

- Refresh every edited detail page and confirm persistence.
- Retry one safe write with the same operation key and confirm the existing receipt/result is
  returned rather than a duplicate.
- Temporarily navigate away during one save, then return and confirm the UI recovers to the durable
  result or an actionable error.
- Confirm search/filter controls are present on mobile list surfaces where the web equivalent has
  them; mobile may use a different layout but must retain the same action and outcome.
- Confirm global Scan/Add remains reachable from every management bottom-nav destination and
  preserves Unit/work-order/payment context when launched contextually.
- Record browser console errors and failed dynamic requests. Do not record screenshots.

## Report

Write the result to:

`Docs/Testing/Results/2026-07-15-001/09-foundation-operations-communications.md`

For every bug include severity, exact role/route, reproduction, expected/actual result, observable
evidence, a code reference with line number, one suggested fix, and landlord/business impact. Also
record the source SHA, preview URL, identities/records used, flows that passed, flows blocked by
environment, and any mobile checks deferred to the physical phone.
