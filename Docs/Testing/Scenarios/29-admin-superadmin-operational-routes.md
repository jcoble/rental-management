# Scenario 29 — Admin, super-admin, engine health, imports, assistant, and operational boundaries

## Purpose

Cover administrative and operational surfaces not reached from the ordinary landlord nav: admin users/audit, super-admin engine state, import/automation/provider boundaries, assistant entry, devices/webhooks, and API-only controller areas that must fail clearly when no UI route is exposed.

## Preconditions and login

Use the administrator for read-only admin/super-admin inspection. Do not stop workers, change clocks, invoke webhooks, connect providers, or modify operational settings on the shared preview. Use local development for any controlled dev-only action and record the required feature flag.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(admin)/admin/users/+page.svelte and web/src/routes/(admin)/admin/audit/+page.svelte
- web/src/routes/(superadmin)/superadmin/engine/+page.svelte
- web/src/routes/(protected)/ai/+page.svelte, web/src/routes/(protected)/import/+page.svelte, web/src/routes/(protected)/settings/integrations/ai/+page.svelte, and web/src/routes/(protected)/settings/+page.svelte
- web/src/routes/.well-known/apple-app-site-association/+server.ts and web/src/routes/.well-known/assetlinks.json/+server.ts
- web/src/routes/application-file/[id]/+server.ts, web/src/routes/document-file/[id]/+server.ts, web/src/routes/expense-file/[id]/+server.ts, web/src/routes/scan-file/[id]/+server.ts, and web/src/routes/workorder-file/[id]/+server.ts
- RentalCommand.Api/Controllers/AdminEngineStatusController.cs, RentalCommand.Api/Controllers/AdminAuditController.cs, RentalCommand.Api/Controllers/DevClockController.cs, RentalCommand.Api/Controllers/DevWorkersController.cs, RentalCommand.Api/Controllers/AnalyticsController.cs, RentalCommand.Api/Controllers/AiController.cs, RentalCommand.Api/Controllers/AiIntegrationsController.cs, RentalCommand.Api/Controllers/ImportController.cs, RentalCommand.Api/Controllers/AutomationSettingsController.cs, RentalCommand.Api/Controllers/DevicesController.cs, RentalCommand.Api/Controllers/VoiceController.cs, RentalCommand.Api/Controllers/CapitalAssetsController.cs, RentalCommand.Api/Controllers/LoanController.cs, RentalCommand.Api/Controllers/EvictionCasesController.cs, RentalCommand.Api/Controllers/PropertyDispositionsController.cs, RentalCommand.Api/Controllers/HistoricalRentRecoveryController.cs, and RentalCommand.Api/Controllers/SandboxController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Inspect admin team/user assignment screens and detailed audit; compare self-protection, role/scope editing, export/refresh, and capability gates.
- Open super-admin engine status/health and determine what is intentionally read-only; verify unhealthy/disabled/worker-unavailable states are actionable without exposing secrets.
- Explore import, automation, provider/integration, assistant, voice, and device entry points; record which are enabled, disabled, or intentionally absent and whether the copy explains the boundary.
- Probe direct routes for API-only operational areas (capital assets, loans, eviction, dispositions, historical recovery, sandbox) and verify they return a stable not-found/forbidden response rather than a generic 500.
- Inspect file proxy routes and app-association endpoints for content type, cache/auth headers, and cross-portfolio protection.

## Specific edge cases worth trying

- Admin identity with no capability, stale session after role change, direct super-admin URL as normal admin, and cross-portfolio user/property ID.
- Worker/provider offline, malformed webhook, duplicate webhook, disabled feature flag, dev clock missing/invalid, and operation attempted without explicit local configuration.
- Large import, invalid route segment, missing file, range request, wrong MIME, path traversal-looking ID, and an expired authenticated file request.
- Long engine/error text, secret/token redaction, 390px admin tables, and console errors from optional providers.

## What to verify visually

- Administrative labels distinguish read-only health from mutation controls; role/scope summaries and warnings are prominent.
- Unavailable/disabled features explain how to proceed; no spinner or blank page implies success, and operational errors do not expose credentials.
- Admin tables, file/error responses, mobile layouts, and direct-route fallback pages remain consistent with the rest of the app.

## Data safety and evidence

Use local dev for dev clock/worker/sandbox probes. On preview, remain read-only and never invoke a webhook, stop a worker, alter operational settings, or change existing files/records.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
