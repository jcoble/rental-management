# Rental Command exploratory E2E scenarios

This directory is the complete exploratory guide set for the Rental Command web application. Each scenario is a rough exploration brief: read the named implementation first, navigate as a real user, compare actual behavior with business intent, and report confirmed bugs separately from potential issues and observations. These are not step-by-step regression scripts.

## Execution conventions

- Use local development from ./scripts/start-dev.sh or the approved preview https://rental-command.chimp-map.ts.net.
- Preview is a live shared simulation. Testers/verifiers are read-mostly; every additive write must be prefixed QA-YYYYMMDD, and no existing record may be deleted, voided, mutated, or reset.
- Use the seeded local administrator admin@rentalcommand.local / Admin123! or the role-specific QA identity named by the scenario.
- Read the scenario's source paths, project instructions, and relevant controller/service code before opening the browser.
- Verify desktop and 390px behavior where called out, including loading/empty/error states and console errors.
- Results belong under Docs/Testing/Results/{RUN_ID}/. New bugs are deduplicated and filed as Rental Command Notion Command Center tasks through capture-task with Source Bug.

## Scenario index

| # | File | Coverage |
|---:|---|---|
| 01 | 01-money-leases.md | Lease lifecycle, tenant-account ledger, accounting reconciliation, and money edge cases. |
| 02 | 02-deposits-owners-tax.md | Security deposits, owner reports/statements, tax, and year-end totals. |
| 03 | 03-operations-core.md | Properties, units, tenants, vendors, relationships, and portfolio scoping. |
| 04 | 04-maintenance-appointments.md | Work orders, recurring maintenance, appointments, inspections, and status timestamps. |
| 05 | 05-scan-draft-confirm.md | Scan/upload extraction, draft review, confirmation, and stored-source retrieval. |
| 06 | 06-portal-public.md | Tenant portal, public applications, and e-sign entry points. |
| 07 | 07-foundation-auth-onboarding-roles.md | Foundation authentication, onboarding, sample/live choice, and role/account setup. |
| 08 | 08-foundation-rentals-scan-leasing.md | Foundation rental structure, Unit Command Center, contextual capture, applications, and leasing. |
| 09 | 09-foundation-operations-communications.md | Foundation money, maintenance, communications, notifications, owner/tenant relationships, and mobile parity. |
| 10 | 10-auth-session-recovery.md | Login/logout, session refresh/expiry, registration, password recovery, verification, Google handoff, and invitations. |
| 11 | 11-guided-setup-onboarding.md | Guided setup wizard, fresh versus example-data mode, progress recovery, and first portfolio. |
| 12 | 12-dashboard-navigation-deeplinks.md | Dashboard metrics/activity, all navigation groups, breadcrumbs, deep links, and Back/Forward state. |
| 13 | 13-properties-units-command-center.md | Property/unit CRUD, structure invariants, filters/health, unit tabs, and contextual actions. |
| 14 | 14-leasing-listings-applications-screening.md | Leasing workspace, listings, applications, screening, public apply, and move-in handoff. |
| 15 | 15-lease-agreements-signing-renewals.md | Lease/agreement creation, issue/signing, possession, correction/versioning, renewal, and month-to-month. |
| 16 | 16-tenants-contacts-vendors.md | Tenant households, contacts, owners, vendors, pickers, cross-links, and scope. |
| 17 | 17-tenant-portal-public-signing.md | Tenant portal dashboard/account/payments/work/messages, unlinked state, public tokens, and files. |
| 18 | 18-accounting-ledger-charges-deposits.md | Transactions, tenant ledgers, one-time/recurring charges, receipts, deposits, past-due, and banking. |
| 19 | 19-reports-tax-owner-statements.md | Reports catalog, owner statements, rent ledger, financial statements, tax, and year-end drill-downs. |
| 20 | 20-maintenance-workorders-inspections.md | Work-order lifecycle, vendors/responsibility, costs, recurring work, inspections, turnover, and portal parity. |
| 21 | 21-appointments-scheduling-work.md | Appointments, leasing calendar/inbox, technician queues, schedules, assignments, and role scope. |
| 22 | 22-messages-notifications-notices.md | Conversations, unread state, notices/drafts, alerts, routing, policies, and delivery availability. |
| 23 | 23-scan-documents-import.md | Documents/files, scan-to-draft-confirm, batch/import, retries, idempotency, and source retrieval. |
| 24 | 24-settings-profile-team-permissions.md | Profile/security, portfolio settings, accounting/integrations, team roles/scopes, and billing boundaries. |
| 25 | 25-audit-history-activity.md | Activity/audit logs, record timelines, exports/filters, and append-only history. |
| 26 | 26-form-validation-systematic.md | Max lengths, numbers/dates, Unicode/emoji, paste, required gaps, server parity, and all major forms. |
| 27 | 27-error-handling-responsiveness.md | 404/403, offline/API errors, loading states, 390px responsiveness, accessibility, and console health. |
| 28 | 28-owner-experience-and-relationship-scope.md | Owner shell, statements/documents, approvals, messages, alerts, security, and relationship isolation. |
| 29 | 29-admin-superadmin-operational-routes.md | Admin users/audit, super-admin engine, imports, integrations, operational/API-only boundaries, and file endpoints. |
| 30 | 30-public-marketing-help-and-files.md | Features, blog, docs/help, privacy, public tokens, well-known files, and unauthenticated delivery. |

Scenarios 01–09 are preserved existing project guides. Scenarios 10–30 complete the route and controller coverage described in the receipt.
