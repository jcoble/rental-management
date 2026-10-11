# Scenario 28 — Owner portal, relationship-scoped navigation, statements, approvals, and messages

## Purpose

Prove that an owner sees a coherent relationship experience rather than the landlord management shell: overview, properties, statements/documents, approvals, messages, alerts, security, and strict property/data scope.

## Preconditions and login

Use an owner identity linked to a QA property, or local fixtures. Keep the owner browser context separate from administrator and tenant contexts. Do not approve/disburse/change a shared owner record; create additive QA approvals locally or in preview only when necessary.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/owner/+page.svelte, web/src/routes/(protected)/owner/properties/+page.svelte, web/src/routes/(protected)/owner/statements/+page.svelte, web/src/routes/(protected)/owner/approvals/+page.svelte, web/src/routes/(protected)/owner/messages/+page.svelte, and web/src/routes/(protected)/owner/security/+page.svelte
- web/src/lib/components/AppShell.svelte and web/src/lib/auth/experience-policy.ts
- web/src/lib/auth/owner-experience-contract.test.ts and web/src/lib/auth/relationship-navigation-contract.test.ts
- web/src/routes/(protected)/owners/+page.svelte and web/src/routes/(protected)/owners/[id]/+page.svelte
- RentalCommand.Api/Controllers/OwnerPortalController.cs, OwnerEntityController.cs, OwnerContributionController.cs, OwnerDistributionController.cs, ReportsController.cs, ConversationsController.cs, MyAlertsController.cs, and AuthController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Walk Overview, Properties, Statements & documents, Approvals, Messages, My alerts, and Security from the owner shell; compare headings, navigation, role copy, and available actions.
- Open a QA property statement and document, drill into income/expense/distribution values, and verify filters/periods do not expose unrelated owners or staff-only ledger detail.
- Inspect approval cards/actions and any owner contribution/distribution state without performing a real payout; verify pending/approved/rejected/unavailable states and audit/history links.
- Start/reply to a message and configure personal alerts; compare owner unread/read behavior with staff and tenant contexts.
- Paste direct management URLs and inspect denied/redirect behavior; refresh, use Back/Forward, and verify the owner shell does not silently become the management shell.

## Specific edge cases worth trying

- Owner with no properties, multiple owners on one property, duplicate property names, long statement rows, no documents, and a period with no data.
- Approval double-submit, stale approval, invalid amount/date, already processed distribution, provider unavailable, and cross-owner/property ID.
- Owner opens /accounting, /banking, /maintenance, /admin/users, /tenant-accounts, or another owner’s route; no partial page or leaked data.
- 390px statement table, long message, unread badge overflow, browser Back after a drill-down, and security form validation.

## What to verify visually

- Owner shell has its own navigation and relationship language; statements, documents, approvals, and property identity are immediately clear.
- Money values, period filters, document links, approval status, messages, loading/error/empty states, and mobile tables are legible.
- Forbidden routes show a consistent safe landing/access-denied state without flashing landlord content.

## Data safety and evidence

Prefix additive owner/property/approval/message references with QA-YYYYMMDD. Do not approve, distribute, or mutate an existing shared owner financial record.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
