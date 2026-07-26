# TSK-747 Web UI Audit Checklist

For each route, complete all seven checks:

`V` visual hierarchy · `W` plain-English wording · `N` navigation/orientation · `C` component consistency · `P` progressive disclosure · `H` contextual help · `S` loading/empty/error states.

Evidence states:

- `[ ]` not reviewed
- `[x] browser` visually reviewed at the declared viewport
- `[x] source` reviewed in source only; do not treat as visual proof
- `[!] blocked` unavailable because of role, token, or missing seeded record

## Lane 1 — Rental spine and Command Center

- [x] browser `/` — V W N C P H S; Daily Briefing clean-reload proof completed after
  separating it from the portfolio-dashboard loading gate (2.34 seconds at 1710×1107)
- [x] browser `/get-started` — V W N C P H S
- [x] browser `/onboarding` — V W N C P H S
- [x] browser `/properties` — V W N C P H S
- [x] browser `/properties/[id]` — V W N C P H S
- [x] browser `/units` — V W N C P H S
- [x] browser `/units/[id]` — V W N C P H S
- [x] browser `/tenants` — V W N C P H S
- [x] browser `/tenants/[id]` — V W N C P H S
- [x] browser `/leases` — V W N C P H S
- [x] browser `/leases/[id]` — V W N C P H S
- [x] browser `/lease-templates` — V W N C P H S; upload is a numbered flow with a
  styled file action and the large signer-field catalog is collapsed by signer
- [x] browser `/applications` — V W N C P H S
- [!] blocked `/applications/[id]` — seeded preview has no applications; error state reviewed
- [x] source `/leasing` — authenticated management experience redirects to `/`
- [x] source `/leasing/[record]/[id]` — authenticated management experience redirects to `/`
- [x] source `/leasing/calendar` — authenticated management experience redirects to `/`
- [x] source `/leasing/inbox` — authenticated management experience redirects to `/`
- [x] source `/leasing/pipeline` — authenticated management experience redirects to `/`
- [x] source `/leasing/rentals` — authenticated management experience redirects to `/`
- [x] browser `/owners` — V W N C P H S
- [x] browser `/owners/[id]` — V W N C P H S
- [x] browser `/owners-report` — V W N C P H S
- [x] source `/owner` — management experience redirects to `/`
- [x] source `/owner/approvals` — management experience redirects to `/`
- [x] source `/owner/messages` — management experience redirects to `/`
- [x] source `/owner/properties` — management experience redirects to `/`
- [x] source `/owner/statements` — management experience redirects to `/`
- [x] source `/choose-setup` — authenticated session redirects to `/`
- [x] source `/setting-up` — authenticated session redirects to `/`
- [x] source `/welcome` — authenticated session redirects to `/`
- [x] browser `/features` — V W N C P H S
- [x] source `/login` — authenticated session redirects to `/`
- [x] browser `/register` — V W N C P H S
- [x] browser `/forgot-password` — V W N C P H S
- [x] browser `/reset-password` — missing-token state reviewed
- [x] browser `/verify-email` — missing-token state reviewed
- [x] browser `/activate-team` — invalid-token state reviewed

## Lane 2 — Work, communication, notifications, and tenant portal

- [x] browser `/maintenance` — V W N C P H S
- [x] browser `/maintenance/[id]` — V W N C P H S
- [x] browser `/maintenance/inspections/[id]` — V W N C P H S
- [x] browser `/maintenance/recurring` — V W N C P H S
- [x] browser `/appointments` — V W N C P H S
- [x] browser `/appointments/[id]` — V W N C P H S
- [x] browser `/vendors` — V W N C P H S
- [x] browser `/vendors/[id]` — V W N C P H S
- [!] blocked `/my-work` — Maintenance experience unavailable; source reviewed
- [!] blocked `/my-work/[id]` — Maintenance experience unavailable; source reviewed
- [!] blocked `/my-schedule` — Maintenance experience unavailable; source reviewed
- [!] blocked `/assignment-inbox` — Maintenance experience unavailable; source reviewed
- [x] browser `/messages` — V W N C P H S
- [!] blocked `/messages/[id]` — no seeded conversation; source reviewed
- [x] browser `/notices` — V W N C P H S
- [x] browser `/settings/notifications/my-alerts` — V W N C P H S
- [x] browser `/settings/notifications/team-routing` — V W N C P H S
- [x] browser `/settings/notifications/tenant-notices` — V W N C P H S
- [!] blocked `/portal` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/account` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/appointments` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/lease` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/maintenance` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/messages` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/notifications` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/payments` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/profile` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/security` — Tenant experience unavailable; source reviewed
- [!] blocked `/portal/unlinked` — Tenant experience unavailable; source reviewed

## Lane 3 — Money, intake, settings, reporting, help, and admin

- [x] browser `/accounting` — V W N C P H S
- [x] browser `/accounting/expenses/[id]` — V W N C P H S
- [x] browser `/accounting/past-due` — V W N C P H S
- [x] browser `/accounting/year-end` — live loading state plus source reviewed
- [x] browser `/banking` — V W N C P H S
- [x] browser `/plaid/auth` — expired callback state; valid callback source-reviewed
- [x] browser `/deposits` — V W N C P H S
- [x] browser `/deposits/[id]` — V W N C P H S
- [x] browser `/tax` — V W N C P H S
- [x] browser `/reports` — V W N C P H S
- [x] browser `/reports/[report]` — Rent Roll plus source variants reviewed; General
  Ledger and Rent Ledger production query paths also exercised against real PostgreSQL
- [x] browser `/tenant-accounts/[tenantAccountId]/entries/[tenantLedgerEntryId]` — V W N C P H S
- [x] browser `/scan` — V W N C P H S
- [x] source `/scan/[draftId]` — no seeded draft
- [x] browser `/scan/batch` — V W N C P H S
- [x] source `/scan/batch/[id]` — no seeded batch
- [x] browser `/scan/new-rental` — V W N C P H S
- [x] browser `/import` — V W N C P H S
- [x] browser `/ai` — V W N C P H S
- [x] browser `/settings` — V W N C P H S
- [x] browser `/settings/accounting` — V W N C P H S
- [x] browser `/settings/integrations/ai` — V W N C P H S
- [x] browser `/settings/security` — V W N C P H S
- [x] source `/profile` — current experience redirected
- [x] source `/owner/security` — Owner experience unavailable
- [x] browser `/docs` — V W N C P H S
- [x] browser `/docs/[slug]` — Security Deposits article reviewed
- [x] browser `/privacy` — V W N C P H S
- [x] source `/sign/[token]` — valid token unavailable
- [x] source `/apply/[token]` — valid token unavailable
- [x] browser `/admin/users` — V W N C P H S
- [x] browser `/admin/audit` — explicit load-failure state plus source reviewed
- [x] browser `/audit` — V W N C P H S
- [x] source `/superadmin/engine` — Platform Admin role unavailable

## Cross-page product checks

- [x] Command Center is visible, named, searchable, and unit-centered.
- [x] Unit-owned list/detail links return to the correct Unit Command Center tab.
- [x] The six Unit destinations remain understandable and do not stack multiple full workflows into one wall.
- [x] Notification setup reads as one guided journey.
- [x] Changed selects, comboboxes, toggles, and date controls use the established design system.
- [x] Source sweep found no first-party native select or date input. The calendar's
  previously disguised native month/year selectors now open the shared Material-style menu;
  month switching was browser-proved at 1710×1107.
- [x] Upload inputs are hidden behind purposeful drop zones or app-styled actions; the
  technician photo log and lease-template upload no longer expose browser-default file controls.
- [x] Server-paged grids show their result range and paging controls on one-page results,
  while empty grids retain a purpose-specific next step.
- [x] Primary route changes show an opaque in-content skeleton immediately instead of
  freezing behind the removed top-bar loader; Unit-to-Tenants navigation was browser-proved.
- [x] Jargon and raw schema enums are absent from changed UI copy.
- [x] Contextual help links resolve to relevant written Help pages.
- [x] Default Portfolio is the first dashboard card; Daily Briefing follows it, retains its own
  loading state, stops after eight seconds, and exposes a manual retry instead of invisible
  minute-long retries.
- [x] Full-viewport desktop proof uses a measured 1710×1107 content viewport, matching the user's display.
- [x] A 1280×720 compatibility pass caught the cramped shared tab styling; the final fix was re-proved at 1710×1107.
- [x] A 414px-wide browser pass caught the Daily Briefing grid's min-content overflow;
  summary copy and all five action cards now wrap inside their containers with zero
  measured horizontal overflow, while the 1710×1107 layout remains unchanged.

## Independently found defects and proof boundaries

- [x] source General Ledger no longer repeats the complete authorized ledger union for
  every running-balance row; PostgreSQL computes rows, totals, and running balance in one
  DB-side statement. A real PostgreSQL integration test passed.
- [x] source Rent Ledger now consumes the canonical effective-capability-scope function
  instead of rebuilding the session, membership, assignment, role, and capability proof
  inside the report query. A real PostgreSQL integration test passed.
- [x] browser Rent Roll later completed in 2.5 seconds against the current Azure backend.
  The Azure deployment still contains the older General/Rent Ledger implementation, so the
  local browser cannot prove the new runtime until that backend is deployed.
- [x] source Owner Reports' all-zero sample result was traced to ownership beginning after
  the seeded financial history. Fresh ownership now begins before the earliest seeded
  ledger/expense activity, with a focused seed test.
- [x] source Expense descriptions that name a unit now seed the corresponding UnitId,
  eliminating the “Unit A” / “No unit” contradiction.
- [x] source `/admin/audit` is Platform Admin-only at both navigation and route boundaries;
  ordinary workspace admins are redirected to their permitted `/audit` activity page.
- [!] blocked The sample workspace has no lease template, application, message thread, scan
  draft, or scan batch with which to prove the corresponding valid-record states.
- [x] source Fresh sample seeding now reuses the canonical primary owner as Maple Ridge
  Properties LLC instead of adding a third “Rental Command Admin” owner; 10 focused API seed
  tests pass. The already-running Azure database has not been destructively reseeded.
