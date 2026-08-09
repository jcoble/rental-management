# Scenario 25 — Audit log, activity history, and durable history views

## Purpose

Prove that important mutations leave an accurate, scoped, understandable history: landlord activity, record timelines, admin audit, exports/filters, and immutable money/agreement/document history.

## Preconditions and login

Use a QA record from another scenario and perform only safe additive mutations locally or in preview. Never edit/delete an audit row or use an existing user’s private activity as test data.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/audit/+page.svelte
- web/src/routes/(admin)/admin/audit/+page.svelte
- web/src/routes/(protected)/properties/[id]/+page.svelte, web/src/routes/(protected)/units/[id]/+page.svelte, web/src/routes/(protected)/tenants/[id]/+page.svelte, web/src/routes/(protected)/leases/[id]/+page.svelte, web/src/routes/(protected)/maintenance/[id]/+page.svelte, web/src/routes/(protected)/messages/[id]/+page.svelte
- web/src/routes/(protected)/tenant-accounts/[tenantAccountId]/entries/[tenantLedgerEntryId]/+page.svelte
- web/src/lib/api/endpoints/audit.ts and web/src/lib/utils/grid-url-state.svelte.ts
- RentalCommand.Api/Controllers/AuditController.cs, AdminAuditController.cs, DocumentsController.cs, TenantAccountsController.cs, LeaseAgreementController.cs, and PortfolioController.cs, plus RentalCommand.Api/Services/Domain/AuditQueryService.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Open activity history from Settings and from record detail; filter by date/user/entity/operation where exposed, page through results, and open the linked record.
- Perform one marked property/unit/work-order/message/ledger/agreement action and confirm the expected audit/activity row appears once with actor, timestamp, entity, operation, and useful before/after or reason context.
- Compare staff activity with admin audit; verify authorization, redacted sensitive values, CSV/export/refresh behavior, and no cross-portfolio leakage.
- Inspect agreement versions, ledger corrections, document source/history, and work-order timelines as append-only narratives rather than mutable status snapshots.
- Use notification/activity links and browser Back/Forward to confirm history filters and entity context are preserved.

## Specific edge cases worth trying

- No activity, many rows, same-timestamp events, timezone/day boundary, long JSON/value, Unicode actor/entity names, malformed filter, and export failure.
- Double-submit or retry of a safe action, failed transaction, rolled-back mutation, unauthorized attempt, and concurrent edits; audit should not claim a commit that did not happen.
- Direct audit/event ID from another portfolio, hidden/deleted entity, admin-only export as a normal user, and an audit row with a sensitive token/password.
- 390px table, horizontal overflow, sticky headers, loading/error/empty states, and copy/search/selectability of detail values.

## What to verify visually

- Timeline chronology, actor/action labels, status chips, changed-value presentation, filters, pagination, and export controls are readable.
- History views distinguish durable event history from current state; error and empty states explain scope and next action.
- Long values wrap/truncate safely, sensitive data is masked, and detail links land on the exact record.

## Data safety and evidence

Use QA-YYYYMMDD in actions/notes so test history is identifiable. Do not delete, edit, export, or redact existing shared audit records.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
