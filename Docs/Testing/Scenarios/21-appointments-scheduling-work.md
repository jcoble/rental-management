# Scenario 21 — Appointments, scheduling, leasing calendar, and role-specific work queues

## Purpose

Test scheduled visits and the people who act on them: appointments, leasing showings/calendar/inbox, technician My Work/My Schedule/assignment inbox, status transitions, notifications, and contextual links.

## Preconditions and login

Use QA appointments and a QA work order/unit. Compare administrator, leasing, technician, and tenant contexts where fixtures permit. Do not assign or reschedule another tester’s live appointment in preview.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/appointments/+page.svelte and web/src/routes/(protected)/appointments/[id]/+page.svelte
- web/src/routes/(protected)/leasing/calendar/+page.svelte and web/src/routes/(protected)/leasing/inbox/+page.svelte
- web/src/routes/(protected)/my-schedule/+page.svelte, web/src/routes/(protected)/my-work/+page.svelte, web/src/routes/(protected)/my-work/[id]/+page.svelte, and web/src/routes/(protected)/assignment-inbox/+page.svelte
- web/src/routes/(portal)/portal/appointments/+page.svelte
- web/src/lib/components/AppShell.svelte and web/src/lib/api/endpoints/appointments.ts
- RentalCommand.Api/Controllers/AppointmentController.cs, TechnicianController.cs, WorkOrderResponsibilityController.cs, TeamRoutingController.cs, LeasingWorkspaceController.cs, NotificationsController.cs, and PortalController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Create an appointment from a work order, unit, leasing calendar, and tenant portal where supported; verify the same time window, type, location, contact, and status everywhere.
- Explore day/week/month or equivalent calendar filters, search, paging, detail links, and empty/loading/error states; compare scheduled and completed/cancelled states.
- Open technician My Work, My Schedule, and Assignment Inbox as a scoped technician; confirm only allowed work appears and actions are explicit.
- Open leasing Today, calendar, and inbox as a leasing user; verify showing/application context and tenant contact boundaries.
- Use browser Back/Forward and notification links to return to a specific appointment/work item without losing the date/filter context.

## Specific edge cases worth trying

- Start/end same time, end before start, DST boundary, all-day/invalid timezone, past appointment, duplicate appointment, and conflicting resource/assignee.
- Cancel/reschedule/complete twice, stale appointment after work-order close, missing unit/tenant, invalid contact, and direct ID from another portfolio.
- Technician with no assignment, reassignment to forbidden property, tenant sees another appointment, and provider/notification failure.
- Long titles/location/notes, many appointments on one day, 390px calendar, horizontal overflow, and keyboard focus.

## What to verify visually

- Calendar date/time, timezone, status, assignee, unit/tenant identity, and action availability are consistent between list, detail, and portal.
- Busy-day density, empty states, skeletons, errors, filters, and mobile cards remain readable; no clipped appointment text.
- Role-specific shells expose only intended navigation and show an actionable forbidden/empty state for direct links.

## Data safety and evidence

Use QA-YYYYMMDD in appointment titles, locations, notes, and participant text. Never reschedule or cancel an existing shared preview appointment.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
