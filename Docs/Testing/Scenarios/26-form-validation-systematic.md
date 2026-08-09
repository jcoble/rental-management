# Scenario 26 — Systematic form validation across the application

## Purpose

Hammer every major landlord, relationship, portal, public, scan, accounting, leasing, operations, settings, and communication form with invalid data before entering valid data. The expected contract is specific inline feedback, blocked submission, consistent server-side rejection, and cleared errors after correction.

## Preconditions and login

Run against local dev for mutation-heavy probes; use preview only for additive QA records and read-mostly validation. Start with the shared schema and route forms, then choose representative forms in every major area. Do not bypass security or mutate existing records.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/lib/schemas/index.ts
- web/src/lib/components/forms/ (all form components used by the routes below)
- web/src/routes/login/+page.svelte, web/src/routes/register/+page.svelte, web/src/routes/forgot-password/+page.svelte, web/src/routes/reset-password/+page.svelte
- web/src/routes/(protected)/onboarding/+page.svelte, web/src/routes/(protected)/properties/+page.svelte, web/src/routes/(protected)/units/+page.svelte, web/src/routes/(protected)/tenants/+page.svelte, web/src/routes/(protected)/vendors/+page.svelte, web/src/routes/(protected)/owners/+page.svelte
- web/src/routes/(protected)/leasing/pipeline/+page.svelte, web/src/routes/(protected)/applications/+page.svelte, web/src/routes/(protected)/leases/+page.svelte, web/src/routes/(protected)/accounting/+page.svelte, web/src/routes/(protected)/deposits/+page.svelte, web/src/routes/(protected)/maintenance/+page.svelte, web/src/routes/(protected)/appointments/+page.svelte, web/src/routes/(protected)/messages/+page.svelte, web/src/routes/(protected)/notices/+page.svelte, web/src/routes/(protected)/scan/+page.svelte, web/src/routes/(protected)/import/+page.svelte, web/src/routes/(protected)/settings/+page.svelte
- web/src/routes/(portal)/portal/maintenance/+page.svelte, web/src/routes/(portal)/portal/messages/+page.svelte, web/src/routes/(portal)/portal/payments/+page.svelte, web/src/routes/(portal)/portal/profile/+page.svelte
- RentalCommand.Api/Controllers/AuthController.cs, RentalCommand.Api/Controllers/PropertyController.cs, RentalCommand.Api/Controllers/UnitController.cs, RentalCommand.Api/Controllers/TenantController.cs, RentalCommand.Api/Controllers/VendorController.cs, RentalCommand.Api/Controllers/ApplicationsController.cs, RentalCommand.Api/Controllers/LeaseManagementController.cs, RentalCommand.Api/Controllers/AccountingController.cs, RentalCommand.Api/Controllers/TenantAccountMoneyController.cs, RentalCommand.Api/Controllers/WorkOrderController.cs, RentalCommand.Api/Controllers/AppointmentController.cs, RentalCommand.Api/Controllers/ConversationsController.cs, RentalCommand.Api/Controllers/NoticeDraftsController.cs, RentalCommand.Api/Controllers/ScanController.cs, RentalCommand.Api/Controllers/ImportController.cs, and RentalCommand.Api/Controllers/TeamController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- For each major form, test blank required fields, whitespace, max-length-minus-one/exact-max/max-plus-one, Unicode/emoji, pasted multiline text, invalid format, and a valid correction.
- Probe numeric fields with empty, zero, negative, decimal precision, maximum safe value, very large value, and locale-formatted input; probe dates with missing, reversed, leap-day, future/past boundary, and timezone midnight.
- Check select/combobox/search fields for no selection, stale selection, duplicate labels, keyboard selection, clear behavior, and a record outside the current portfolio.
- Blur each field before submit, submit with visible errors, fix one field at a time, reload during validation, and compare the frontend result with the server response/status.
- Exercise paste and keyboard navigation, native browser constraints, disabled/loading buttons, duplicate submit, and offline/API error for every form family.

## Specific edge cases worth trying

- Long names/addresses/notes/descriptions/references, invalid email/phone/postal codes, HTML/script-looking text, zero/negative money, excessive decimals, and Unicode normalization.
- Dates at month/year boundaries, leap years, DST transitions, start/end inversion, due day 31, and a future date beyond business rules.
- Missing relationship IDs, cross-portfolio IDs, duplicate operation keys, replayed uploads, provider unavailable, and server 400/409/422/500 distinctions.
- Form in a modal, drawer, wizard, table row, mobile sheet, public token page, and tenant portal; confirm error focus and scroll behavior in each.

## What to verify visually

- Error text appears next to the field and is associated with its label; it is specific, readable, and does not rely only on red borders.
- Submit controls explain disabled/submitting state, errors clear after correction, valid values persist on back/reload, and success cannot be mistaken for an error toast.
- Character counters, date/number formatting, select menus, upload previews, dialogs, and field focus remain usable at 390px.

## Data safety and evidence

Use local fixtures or additive QA-YYYYMMDD values. Never submit invalid values against a shared existing record merely to test server behavior; prefer create-only forms and local dev.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
