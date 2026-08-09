# Scenario 23 — Documents, files, scan-to-draft-confirm, and import

## Purpose

Exercise the flagship capture pipeline across lease, application, payment, expense, and work-order records: upload, extraction, confidence/review, correction, confirm, source-file retrieval, batch/import, retry, and abandonment.

## Preconditions and login

Use local development with sample documents from samples/ or the repository’s documented fixtures when extraction is available. In preview, use only additive QA documents and stop when the provider is unavailable; do not upload personal or sensitive real documents.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/scan/+page.svelte, web/src/routes/(protected)/scan/[draftId]/+page.svelte, web/src/routes/(protected)/scan/batch/+page.svelte, web/src/routes/(protected)/scan/batch/[id]/+page.svelte, and web/src/routes/(protected)/scan/new-rental/+page.svelte
- web/src/routes/(protected)/import/+page.svelte
- web/src/routes/scan-file/[id]/+server.ts, web/src/routes/document-file/[id]/+server.ts, web/src/routes/expense-file/[id]/+server.ts, web/src/routes/application-file/[id]/+server.ts, and web/src/routes/workorder-file/[id]/+server.ts
- web/src/lib/api/scan.ts, web/src/lib/api/endpoints/documents.ts, web/src/lib/api/endpoints/expenses.ts, web/src/lib/api/endpoints/applications.ts, web/src/lib/api/endpoints/lease-managements.ts, web/src/lib/api/endpoints/payments.ts, and web/src/lib/api/endpoints/workOrders.ts
- RentalCommand.Api/Controllers/ScanController.cs, RentalCommand.Api/Controllers/DocumentsController.cs, RentalCommand.Api/Controllers/ImportController.cs, RentalCommand.Api/Controllers/LeaseManagementController.cs, RentalCommand.Api/Controllers/ApplicationsController.cs, RentalCommand.Api/Controllers/ExpenseController.cs, RentalCommand.Api/Controllers/TenantAccountMoneyController.cs, RentalCommand.Api/Controllers/WorkOrderController.cs, and RentalCommand.Api/Controllers/WorkspaceExperienceController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Start capture globally and from property/unit/lease/application/work-order context; verify inherited context and target type before upload.
- Use at least three document types, inspect progress/pending/failure/retry/completed states, compare extracted names, dates, amounts, addresses, relationships, and confidence to the source, and correct fields before confirmation.
- Confirm once, deliberately retry/double-submit, reopen the canonical record, and retrieve the stored source through its file route; exactly one record and one source link should result.
- Reject/cancel/abandon one draft and verify no canonical record or orphan relationship remains; navigate away during extraction and return through the durable draft route.
- Exercise batch/import preview, malformed/mixed rows, duplicate file selection, partial validation, commit, and links to created records only where the UI exposes the feature.

## Specific edge cases worth trying

- Empty file, wrong MIME/extension, oversized file, corrupted PDF/image, multi-page document, Unicode filename, duplicate upload, and upload cancellation.
- Low/zero confidence, missing field, ambiguous amount/date, negative/large amount, invalid relationship, provider timeout, retry after partial success, and stale draft ID.
- Double confirm, browser reload during confirm, cross-portfolio draft/file ID, missing source file, incorrect content type, range/download failure, and unauthorized public file access.
- Import with blank headers, extra columns, duplicate rows, 0/negative numbers, invalid dates, long text, emoji, and mixed valid/invalid records.

## What to verify visually

- Upload/dropzone, progress, draft confidence/review, required-field errors, source preview, correction controls, and confirm/cancel actions are easy to find.
- Pending, blocked, failed, empty, and completed states are explicit; a spinner never replaces an error or a durable next action.
- Created-record return links preserve property/unit context; file preview/download and batch tables are usable at 390px.

## Data safety and evidence

Use QA-YYYYMMDD in all typed fields, filenames, import rows, and notes. Use synthetic sample documents only; never delete an existing uploaded file or draft in preview.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
