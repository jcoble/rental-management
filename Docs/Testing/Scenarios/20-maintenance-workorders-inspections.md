# Scenario 20 — Maintenance, work orders, recurring work, inspections, and turnover

## Purpose

Exercise the operational work loop: request/create, responsibility, vendor, status/timestamp transitions, costs/materials, recurring maintenance, inspections, and unit turnover context.

## Preconditions and login

Use a QA unit, vendor, and work order. A tenant portal request may be created only with a QA identity. Do not complete, cancel, or edit an existing shared preview work order; keep all new descriptions and references QA-prefixed.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/maintenance/+page.svelte and web/src/routes/(protected)/maintenance/[id]/+page.svelte
- web/src/routes/(protected)/maintenance/recurring/+page.svelte and web/src/routes/(protected)/maintenance/inspections/[id]/+page.svelte
- web/src/routes/(protected)/units/[id]/+page.svelte and web/src/routes/(portal)/portal/maintenance/+page.svelte
- web/src/lib/api/endpoints/workOrders.ts, web/src/lib/api/endpoints/recurring-maintenance.ts, web/src/lib/api/endpoints/inspections.ts, web/src/lib/api/endpoints/vendors.ts, and web/src/lib/api/endpoints/units.ts
- RentalCommand.Api/Controllers/WorkOrderController.cs, RentalCommand.Api/Controllers/WorkOrderResponsibilityController.cs, RentalCommand.Api/Controllers/RecurringMaintenanceController.cs, RentalCommand.Api/Controllers/InspectionController.cs, RentalCommand.Api/Controllers/VendorController.cs, RentalCommand.Api/Controllers/UnitTurnoverController.cs, and RentalCommand.Api/Controllers/ExpenseController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Create a work order globally and from a Unit Command Center; confirm property/unit/tenant context, priority, access instructions, photos, vendor, and responsibility are inherited correctly.
- Move a QA work order through the supported status lifecycle with notes and timestamps; reopen it from global, unit, technician, and portal views and compare the same timeline.
- Add labor/material costs and inspect totals, linked expenses, completion timestamp, and any owner/tenant notification behavior.
- Create or inspect recurring maintenance, generated occurrences, and retry behavior; run an inspection and follow unit/work-order links.
- Compare staff and tenant permissions for editing request fields, comments, photos, cancellation, status transitions, and contact/access details.

## Specific edge cases worth trying

- Blank title/description, invalid priority/status, zero/negative/large cost, long notes, Unicode/emoji, invalid date, and vendor outside the portfolio.
- Skip status transitions, complete twice, cancel after completion, stale concurrent edit, double-submit, retry after timeout, and duplicate recurring occurrence.
- Photo too large/wrong type, missing file, access instructions with sensitive text, tenant must-be-present toggles, and direct cross-portfolio work-order ID.
- No work orders, no vendors, no inspections, recurring schedule at DST/leap day, and 390px detail layout.

## What to verify visually

- Status, priority, assignee/vendor, cost total, timestamps, access rows, and unit identity are prominent and consistent across surfaces.
- Timeline and comments distinguish tenant/staff authors; loading, error, no-work, and retry states do not hide the next safe action.
- Forms/dialogs, photo previews, tables, badges, and mobile action bars remain aligned and keyboard reachable.

## Data safety and evidence

Use QA-YYYYMMDD in all new work-order titles, notes, vendor text, photos, and recurring descriptions. Do not modify/delete/void pre-existing preview work.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
