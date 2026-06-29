# Scenario 04 — Maintenance, Work Orders, Appointments, Inspections

**Domain:** Operations (second slice). **Suggested tester session:** `tester2` (after 03).

## Mission
Test the operational workflows: **work orders** (create, status transitions, costs & timing),
**recurring maintenance**, **appointments**, and **inspections**. Status transitions that don't
stamp the right timestamps, or costs that don't total, are the high-value bugs here. (A prior pass
already fixed a "Completed but CompletedAt is null" defect — verify nothing similar lurks.)

## Get acquainted with the code first
- Frontend: `web/src/routes/(protected)/maintenance/` (list, `[id]`, `recurring/`),
  `appointments/`, and any inspections surface.
- Web API client: `web/src/lib/api/endpoints/workOrders.ts`, `recurring-maintenance.ts`,
  `appointments.ts`, `inspections.ts`.
- API: `WorkOrderController.cs`, `RecurringMaintenanceController.cs`, `AppointmentController.cs`,
  `InspectionController.cs`.
- Services: `Services/Domain/WorkOrderService.cs` (status + completion timestamp logic),
  recurring maintenance service, appointment/inspection services. Entities: `Core/Entities/
  WorkOrder.cs`, `RecurringMaintenanceTask.cs`, `Appointment.cs`, `Inspection.cs`.

## Flows to exercise
1. **Work order lifecycle**: create a work order for a unit/property, move it New → In progress →
   Completed via the status control/modal (with notes). Confirm the header status, the Costs &
   timing section, and the persisted record all agree — and that completing it stamps a completion
   timestamp (re-open the detail; if you can, verify the timestamp isn't null).
2. **Costs**: add labor/material costs to a work order; confirm the total is correct and reconciles
   wherever it's shown (and flows to an expense if the system links them).
3. **Recurring maintenance**: create/inspect a recurring task; confirm its schedule and any
   generated work-order/cost context is correct.
4. **Appointments / inspections**: create an appointment (and an inspection if available), confirm it
   shows on the right unit/tenant and any status/links resolve.
5. **Edge cases**: skip a status step; complete without required fields; past/future scheduled dates;
   assign a vendor that doesn't exist.

## Watch especially for
- Status shown in UI not matching the DB (e.g. Completed in header but underlying status/timestamp
  wrong) — re-open and, where possible, confirm persistence.
- Cost totals that don't sum, or that change wrongly under a filter.
- Recurring task generating duplicate or missing occurrences.
- Broken links between work order ↔ unit ↔ tenant ↔ vendor ↔ expense.

## Data hygiene
Marker `QA-T2-<HHMMSS>`. Create your own work orders/appointments rather than mutating seed ones.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/04-maintenance-appointments.md`
