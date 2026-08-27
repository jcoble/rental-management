# Exploratory Test Report: Maintenance, Work Orders, Recurring Work, Inspections, and Turnover
Date: 2026-08-27
Tester: e2e-s20
Duration: ~55 minutes

## Scenario
Exercise the operational maintenance loop from request and assignment through status, cost, recurring work, inspections, and unit turnover context.

## Summary
I created QA-only property, unit, vendor, work orders, recurring maintenance, and inspections, then exercised the global maintenance page, work-order detail, unit command center, recurring-work views, inspection detail, and turnover view. The supported New → Scheduled → In progress → Completed path, notes/timestamps, comments, photo upload, cancellation, recurring pause/resume, inspection completion/PDF, failed-item repair generation, and turnover totals worked. Six defects were confirmed: a visible but rejected On hold transition, silent negative-cost conversion, broken recurring clear controls, an inspection completion guard mismatch, missing unit inspection linkage, and editing a cancelled order producing a 404 and stuck form.

## Bugs Found

### BUG-1: Make the visible On hold transition save successfully
**Severity:** Medium
**Location:** Global work-order detail, `/maintenance/16`, QA order `QA-20260827-s20 Repair lifecycle`
**Expected:** The manager capability graph advertises `OnHold` as a valid transition from `New`, and the detail page renders every allowed transition as an actionable button. Clicking the visible On hold action should record the transition and its note.
**Actual:** The detail page displayed an On hold button. Submitting `QA-20260827-s20 Put repair on hold pending part.` returned HTTP 400 from `/api/v1/work-orders/16`; the order remained `New` and no On hold timeline event was created. The same order subsequently transitioned successfully through Scheduled, InProgress, and Completed.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s20-onhold-error.png`. Browser response log: `HTTP 400 https://localhost:5666/api/v1/work-orders/16`. Read-only database verification showed work order 16 had only the New, Scheduled, InProgress, and Completed events.
**Code Reference:** `RentalCommand.Api/DTOs/WorkOrderDtos.cs:420-426` includes `OnHold` in manager transitions; `web/src/lib/components/records/WorkOrderDetail.svelte:623-633` renders the returned transitions; `RentalCommand.Data/Operations/WorkOperationMutationRules.cs:1252-1261` omits `OnHold` from user-assignable statuses and `:1360-1363` rejects it.
**Suggested Fix:** Add `WorkOrderStatus.OnHold` to `UserAssignableStatuses` so the server accepts the transition already advertised by the capability graph, with a regression test for manager New → OnHold.
**Why This Matters:** A landlord who uses “On hold” while waiting for a part cannot record the real operating state and instead sees a failed save after trusting the available action.

### BUG-2: Reject a negative cost instead of converting it to a positive cost
**Severity:** High
**Location:** Work-order detail cost editor, `/maintenance/16`, Actual Cost
**Expected:** Actual cost is defined as optional non-negative money. Entering `-1` should remain visibly invalid and produce the non-negative validation error; it must never be silently changed to a different amount.
**Actual:** Filling Actual Cost with `-1` changed the input value to `1` before submission. Save succeeded with no validation message and the detail page displayed `Actual Cost $1.00`. The QA order was then restored to the intended `$275.25` through the UI.
**Evidence:** Screenshot after the negative save: `/home/blackcolours/Workbox/screenshots/e2e-s20-negative-cost-silent-positive.png`. Browser-run output recorded `masked value before save 1` and `Actual Cost $1.00`; the final read-only database check after restoration showed work order 16 at `ActualCost=275.25`.
**Code Reference:** `web/src/lib/schemas/index.ts:360-365` declares `actualCost` as `optionalNonNegative('Actual cost')`; `web/src/lib/components/shared/InlineField.svelte:123-128` replaces the bound value with the masked value and `:171-183` routes numeric inputs through that mask; `web/src/lib/forms/input-masks.ts:46-55` strips every character other than digits and periods, including the minus sign.
**Suggested Fix:** Preserve a leading minus in the cost input until schema validation runs, so `optionalNonNegative` returns an inline error instead of turning `-1` into `1`.
**Why This Matters:** A landlord can type a refund, correction, or accidental negative value and unknowingly save a positive charge, corrupting maintenance cost history and downstream financial decisions.

### BUG-3: Make recurring-task clear controls actually clear stored values
**Severity:** High
**Location:** Recurring maintenance edit dialog, `/maintenance/recurring`, task 1 `QA-20260827-s20 Monthly smoke alarm check`
**Expected:** Unit, vendor, description, and category are optional. The dialog explicitly labels the clear actions `Whole property` and `No vendor`, and the schema documents blank unit/vendor values as `null`; saving after those controls are used should remove the existing values.
**Actual:** I cleared the unit and vendor, blanked the QA description and category, and saved. The dialog closed as if successful, but the row and database still contained Unit A, the QA vendor, the original description, and the original category. The task therefore remained routed to the old unit/vendor and retained old text despite the user’s clear action.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s20-recurring-clear-result.png`. Read-only database verification after save: task 1 still had `UnitId=21`, `VendorId=8`, description `QA-20260827-s20 Check the smoke alarm and replace batteries if needed.`, and category `QA-20260827-s20 Preventive`.
**Code Reference:** `web/src/lib/components/maintenance/RecurringMaintenanceFormDialog.svelte:265-287` exposes the optional unit/vendor clear labels; `:211-225` sends the form values, including `null`, on update; `web/src/lib/schemas/index.ts:377-387` documents blank optional values as `null`; `RentalCommand.Api/Services/Domain/RecurringMaintenanceTaskService.cs:246-258` coalesces null to the old unit/vendor and only assigns nullable fields when `HasValue`/non-null.
**Suggested Fix:** Add explicit clear-presence fields to the recurring update command and set the entity fields to null when those clear controls or blank optional text fields are submitted.
**Why This Matters:** A landlord may believe a recurring job is property-wide or vendorless, while the system continues sending it to the old unit and vendor on every future occurrence.

### BUG-4: Block completion of an inspection while checklist items remain pending
**Severity:** Medium
**Location:** Inspection detail, QA inspection 8 at `/maintenance/inspections/8`
**Expected:** The backend requires at least one checklist item to be marked Pass, Fail, or N/A before completion. The UI should therefore prevent completion while items remain `To do`, or explain the validation before making a request.
**Actual:** I created one pending QA checklist item. The page showed `1 To do`, but Complete inspection remained enabled. The confirmation dialog repeated `1 still to do` and its confirmation button remained enabled. Clicking it produced HTTP 409 from `/api/v1/inspections/8`; the inspection stayed Scheduled with the pending item and only a generic request error was surfaced.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s20-inspection-pending-error.png`. Browser response log: `HTTP 409 https://localhost:5666/api/v1/inspections/8`. Read-only state after the failure: inspection 8 had `Status=0` (Scheduled), no completion time, and one pending item.
**Code Reference:** `RentalCommand.Api/Services/Domain/AtomicInspectionMutationRule.cs:690-709` rejects an all-pending checklist; `web/src/routes/(protected)/maintenance/inspections/[id]/+page.svelte:441-449` disables the primary button only when there are zero items; `:807-830` displays the pending count but does not disable the confirmation button.
**Suggested Fix:** Disable both Complete inspection buttons whenever `counts.pending > 0` and show the existing server rule as inline guidance.
**Why This Matters:** The landlord is invited to click an action the system knows cannot work, receives a technical conflict response, and is left unsure whether checklist data was lost.

### BUG-5: Allow an inspection to target a unit so its repair stays in the unit work loop
**Severity:** High
**Location:** Global inspection scheduler and unit 21 maintenance view, `/maintenance` and `/units/21?tab=maintenance&view=inspections`
**Expected:** Inspections and inspection-created repairs should retain unit context. The API and entity support an optional `UnitId`, and the unit view queries and displays inspections and work orders by unit. A landlord scheduling an inspection for a unit should be able to select that unit and later find the inspection and failed repair from the unit command center.
**Actual:** The global Schedule Inspection dialog offered a property but no unit selector. I scheduled and completed QA inspection 7 for QA property 9 with one failed smoke-alarm item. The inspection persisted with `UnitId=NULL`, and generated work order 18 also persisted with `UnitId=NULL`. The unit 21 Inspections view then showed `No inspections for this rental` and the generated repair did not appear in that unit’s Repairs list.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s20-unit-inspection-link-gap.png`. Read-only database verification: inspection 7 was Completed with `PropertyId=9, UnitId=NULL`; generated work order 18 had `PropertyId=9, UnitId=NULL`; the unit 21 page reported 0 inspections. The failed-item repair itself was correctly created with the QA title and note.
**Code Reference:** `RentalCommand.Api/DTOs/InspectionDtos.cs:299-306` accepts `UnitId`; `web/src/routes/(protected)/maintenance/+page.svelte:357-374` defines the create form without a unit field and `:1084-1111` renders only property/type in the first step; `:635-651` submits only the form fields; `RentalCommand.Api/Services/Domain/AtomicInspectionMutationRule.cs:926-932` copies `inspection.UnitId` to the generated repair; `web/src/routes/(protected)/units/[id]/+page.svelte:525-559` lists only unit-filtered inspections.
**Suggested Fix:** Add a unit selector filtered by the selected property to the global inspection scheduler and include its value as `unitId` in the existing create payload.
**Why This Matters:** A failed inspection repair can exist globally but disappear from the unit’s operational view, so a landlord can miss a safety repair while working from the unit command center.

### BUG-6: Hide editing for cancelled work orders instead of submitting a guaranteed 404
**Severity:** Medium
**Location:** Cancelled work-order detail, `/maintenance/17`, QA order `QA-20260827-s20 Unit command repair`
**Expected:** Cancelled is a terminal status for the update rule. The detail page should not offer an edit action that the API will reject, or it should leave the user in a recoverable read-only state.
**Actual:** I cancelled the QA unit-created order with a QA note. The detail page still displayed Edit. Clicking Edit and then Save without changing anything sent a request and returned HTTP 404 from `/api/v1/work-orders/17`; the page remained in edit mode with the error state instead of returning to a stable read-only view.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s20-cancelled-edit-error.png`. Browser response log: `HTTP 404 https://localhost:5666/api/v1/work-orders/17`. Read-only database verification showed work order 17 remained Cancelled.
**Code Reference:** `RentalCommand.Data/Operations/WorkOperationMutationRules.cs:266-272` returns NotFound for Cancelled or Archived work orders; `web/src/lib/components/records/WorkOrderDetail.svelte:238-247` only shows a toast on save failure; `:659-667` renders Edit whenever `caps.canEdit` without excluding terminal statuses.
**Suggested Fix:** Gate the detail Edit button on `wo.status !== 'Cancelled' && wo.status !== 'Archived'` so terminal records cannot enter the rejected edit path.
**Why This Matters:** A non-technical landlord sees an edit affordance, performs a normal save, and is left with a confusing error and a form that appears active even though the record cannot be changed.

## Potential Issues (need investigation)

- Recurring occurrence generation and retry/quarantine were not run end to end. The QA task’s next due date was in the future, the worker is scheduled on a daily poll, and no manual run control was exposed in the tested UI. This is a coverage limitation, not a confirmed defect.
- Tenant-portal permissions and technician assignment/status behavior could not be compared because the seeded database exposed only the admin identity. `/portal/maintenance` and `/my-work` redirected the admin to `/`; no QA tenant or technician credentials were created, and no shared seed record was changed.
- The unit Repairs card rendered `$0.00` for the newly created QA work order whose estimated and actual costs were both null. This may be an intentional display default, but it should be checked against the intended meaning of “no cost recorded.”

## Observations

- Global repair creation successfully inherited QA property, unit, vendor, priority, category, estimated cost, and technician access instructions. Blank title/description/property validation stayed in the dialog and showed inline errors without sending a request.
- The QA global repair persisted the supported New → Scheduled → InProgress → Completed sequence, each QA status note, and `CompletedAt`. The completed detail intentionally had no reopen action; the source explicitly describes no reopen workflow.
- The unit command center created a QA repair with property/unit context. Public and private QA comments and a `QA-20260827-s20-photo.png` upload persisted after reload. Cancellation recorded the QA note and moved the unit turnover view to Rent-ready.
- Recurring task creation, property/unit/vendor display, unit-scoped recurring listing, and active pause/resume worked. The clear failure is limited to replacing existing optional values with blank/null values.
- Completing QA inspection 7 with one failed item generated a new QA repair, attached the failed-item link, set the inspection completion timestamp, and produced a downloadable PDF report. The unit-link defect is about missing source context, not failure of the generation transaction.
- Unit turnover reconciled the QA work orders and showed the completed/cancelled counts, actual cost, and receipt balance. No additional data-access or visibly in-memory aggregation defect was observed in the tested lists.

## What Was Tested

- Logged in at `https://localhost:5667/login` as the seeded admin in the isolated headless `e2e-s20` browser at `1710x990`.
- Created QA vendor 8 (`QA-20260827-s20 Vendor`), QA property 9, and QA units 21 and 22 through the UI.
- Created global QA work order 16 at `/maintenance`, verified property/unit/vendor/access/priority/cost, attempted On hold, then moved it through Scheduled, InProgress, and Completed with notes; checked timeline, completion timestamp, and cost persistence.
- Created unit QA work order 17 at `/units/21?tab=maintenance`, added public/private comments and a QA photo, reloaded to verify persistence, cancelled it, and tested the remaining Edit action.
- Created recurring task 1 at `/maintenance/recurring`, viewed it at `/units/21?tab=maintenance&view=recurring`, tested clearing optional fields, and paused/resumed it.
- Scheduled inspection 7 at `/maintenance`, added a QA failed checklist item, completed it, opened the generated repair/report, and followed the unit view at `/units/21?tab=maintenance&view=inspections`. Scheduled inspection 8 with one pending item and attempted completion to verify the guard.
- Opened `/units/21?tab=maintenance&view=turnover` and checked turnover status, open/completed counts, actual cost, and receipt balance.
- Used read-only PostgreSQL SELECT queries to verify QA records and relationships; no direct API or database writes were used to make assertions pass.
- Browser cleanup: stopped e2e-s20
