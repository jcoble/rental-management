# Scenario 15 — Lease creation, agreements, signing, corrections, and renewals

## Purpose

Validate the lease relationship lifecycle from creation through agreement draft, issue, e-sign, possession, correction/versioning, renewal, and month-to-month conversion while preserving immutable history and a continuous tenant account.

## Preconditions and login

Use a QA application/unit or a dedicated local fixture. Signing consumes durable state, so use only a marked additive relationship; never sign or mutate a shared preview lease owned by another tester. If provider/e-sign integration is unavailable, verify the explicit blocked state and artifact contract without claiming completion.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/leases/+page.svelte and web/src/routes/(protected)/leases/[id]/+page.svelte
- web/src/routes/(protected)/lease-templates/+page.svelte
- web/src/routes/(protected)/leasing/applications/[id]/+page.svelte and web/src/routes/(protected)/leasing/[record]/[id]/+page.svelte
- web/src/routes/(public)/sign/[token]/+page.svelte and web/src/routes/(public)/sign/[token]/+page.server.ts
- web/src/lib/api/endpoints/lease-managements.ts, web/src/lib/api/endpoints/lease-addendums.ts, and web/src/lib/api/endpoints/sign.ts
- RentalCommand.Api/Controllers/LeaseManagementController.cs, LeaseAgreementController.cs, LeaseAddendumController.cs, SignController.cs, DocumentTemplatesController.cs, TenantAccountLifecycleController.cs, TenantAccountsController.cs, and PublicApplicationsController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Create a lease from a QA unit/application and verify tenant, co-tenant/guarantor/occupant, term type, dates, rent, due day, deposit, late fee, and template relationships.
- Review and edit the initial agreement draft, prepare/issue it, inspect the generated artifact and signer order, and retry the same operation to test idempotent behavior.
- Open the public signing link in an unauthenticated context, complete the supported signature path, and confirm signed/executed artifacts and agreement history return to the staff lease.
- Give possession only when the governing agreement and tenant account requirements are met; verify unit occupancy and money surfaces update coherently.
- Correct an executed agreement with a reason, issue/sign the successor, and compare predecessor/successor history, governing dates, IDs, balances, and immutable PDFs.
- Renew a fixed-term agreement and create a month-to-month successor; verify current/upcoming governing behavior and addendum decisions.

## Specific edge cases worth trying

- End before start, overlapping terms, due day 31 in short months, negative/zero/large rent or deposit, missing signer, duplicate signer, and invalid email.
- Double issue/sign/possess, browser reload during save, expired/reused signing token, signer opens another tenant’s token, and provider timeout.
- Correction without reason, correction of a non-editable historical field, cancel abandoned successor, duplicate renewal, and renewal that starts too early.
- Direct lease/agreement ID from another portfolio, unauthorized owner/technician/tenant route, and a missing source document.

## What to verify visually

- Stepper progress, immutable/history language, governing/upcoming labels, signer identity, dates, money formatting, and artifact links are unmistakable.
- Signing page is focused and accessible on desktop/mobile; required fields and consent are visible, with recoverable errors.
- Unit/lease/tenant/account breadcrumbs return to the same relationship; no old PDF or status is silently overwritten.

## Data safety and evidence

Use QA-YYYYMMDD in parties, references, agreement reason, and test email addresses. Do not delete or void an existing agreement; abandon only marked local/preview drafts.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
