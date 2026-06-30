# Exploratory Test Report: Maintenance, Work Orders, Appointments, Inspections
Date: 2026-06-28
Tester: tester4
Duration: ~1h (code study + browser)

## Scenario
Exercise the operational workflows — work-order lifecycle/costs/timestamps, recurring maintenance,
appointments, and inspections — hunting for status that doesn't match the DB, timestamps that don't
stamp (or stamp wrong), costs that don't reconcile, and broken work-order ↔ unit ↔ tenant ↔ vendor ↔
expense links.

## Summary
The core flows mostly work and persist correctly: New→In progress→Completed stamps `CompletedAt`
(verified UI vs DB), inspection completion spawns exactly one work order per Fail item + stamps the
inspection's `CompletedAt` + generates the PDF, appointment links resolve, recurring tasks persist
with a UTC-midnight-normalized due date, and list filtering/search is genuinely server-side. The
prior "Completed but CompletedAt null" defect is fixed on the normal path.

However I found one **High** logic bug: a work order **scheduled for any future date/time cannot be
marked Completed** via the one-click status button — the server auto-stamps `CompletedAt = now`, which
then fails the "completion can't be before the scheduled visit" guard, returning a 400 with a
misleading message about a "completion date" the user never typed. I also found a **Medium**
misleading-UI bug (the "vendor has the job / will text back DONE" banner appears merely from assigning
a vendor, with no dispatch ever sent) and a **Low** data-fidelity bug (editing any field on a work
order truncates `RequestedAt`/`CompletedAt` to UTC-midnight).

Live recurring-maintenance *generation* could not be triggered in-session (the worker runs on a 24h
cadence with one cycle at Engine startup and has no manual-trigger endpoint); I verified its CRUD,
schedule, and normalization in the UI/DB and its generation logic by code reading.

## Bugs Found

### BUG-1: A future-scheduled work order can't be marked Completed (auto-stamped CompletedAt fails the "before scheduled visit" guard)
**Severity:** High
**Location:** Work-order detail → "Completed" status button (`/maintenance/{id}`); server `PATCH /api/v1/work-orders/{id}`
**Expected:** Marking a work order Completed should succeed and stamp a completion timestamp. If a job
is finished before its scheduled visit (vendor came early, or the visit was scheduled for later the
same day), the one-click Complete should still work — or at minimum give actionable guidance.
**Actual:** Creating a work order scheduled for the future and then clicking **Completed** is blocked.
The status never changes; a red toast says **"The completion date can't be before the scheduled
visit."** — even though the user never entered a completion date. The PATCH returns **400** and the
record stays New.
**Evidence:**
- Repro: WO #198 created with Scheduled start `2026-07-05 10:00` (stored `2026-07-05 14:00Z`), Status New.
- Clicked Completed → toast "The completion date can't be before the scheduled visit."; console `Failed to load resource: 400 … /api/v1/work-orders/198`.
- DB after attempt: `Status=0 (New)`, `CompletedAt=NULL`, only the initial status event exists (action fully rejected).
- Screenshot: `output/playwright/tester4-01-future-complete-blocked.png`
- Contrast: WO #199 (no schedule) completed cleanly — `Status=4`, `CompletedAt=2026-06-29 03:41:05`.
**Code Reference:** `RentalCommand.Api/Services/Domain/WorkOrderService.cs:454-461` (status→Completed
stamps `entity.CompletedAt = now`) and `:473-477` passing `completedProvided = … || completedAtStampedFromStatus`
into `EnsureCompletedAtInRange`, which throws at `:544-550` ("can't be before the scheduled visit").
**Suggested Fix:** Don't apply the "before scheduled visit" check to the **auto-stamped** completion
time. When `CompletedAt` was stamped from the status change (not user-supplied), either skip the
`scheduled`-comparison branch, or clamp the stamp to `max(now, scheduledFor)` so an early completion is
allowed. Concretely: only pass `completedProvided=true` for the `scheduled` comparison when the client
actually supplied `CompletedAt` (i.e. gate the `:544` branch on `request.CompletedAt.HasValue`, not on
the auto-stamp). The "completed before requested" intent for *typed* dates can stay.
**Why This Matters:** Scheduling a future vendor visit is the normal case. When the work gets done at
or before that scheduled time, the landlord taps Completed and hits a dead-end with a message blaming
a date they never entered, with no on-screen way out (they must first guess to edit/clear the Scheduled
date). It blocks the single most important lifecycle action and erodes trust in the status controls.

### BUG-2: "Vendor has the job — they'll text back DONE" banner shows from merely *assigning* a vendor (no dispatch sent)
**Severity:** Medium
**Location:** Work-order detail (`/maintenance/{id}`) — blue dispatched-hint banner
**Expected:** The "{Vendor} has the job. When they text back DONE, this work order closes
automatically." banner should appear only after the job was actually **dispatched** (the "Text a
vendor" action that creates a dispatch + enqueues the SMS).
**Actual:** Simply selecting a vendor in the work-order form (no dispatch action) makes the detail page
assert the vendor "has the job" and will auto-close on a DONE reply. No SMS/dispatch was ever created.
**Evidence:**
- WO #199 created with vendor "Apex Plumbing Co." chosen in the form (never used "Text a vendor").
- Detail page showed: *"Apex Plumbing Co. has the job. When they text back DONE, this work order closes automatically."*
- DB: `SELECT COUNT(*) FROM "VendorDispatches" WHERE "WorkOrderId"=199` → **0**. No dispatch exists.
**Code Reference:** `web/src/lib/maintenance/work-order-dispatch.ts:41-48`
(`shouldShowActiveDispatchHint` returns true on `hasAssignedVendor` alone) consumed at
`web/src/lib/components/records/WorkOrderDetail.svelte:414` with `hasAssignedVendor = Boolean(wo.vendorName)`.
**Suggested Fix:** Drive the banner off real dispatch state, not vendor assignment — show it only when
`wasJustDispatched` is true or the work order has an **open VendorDispatch** (expose e.g.
`hasActiveDispatch` on the work-order detail response) rather than on `Boolean(wo.vendorName)`.
**Why This Matters:** A non-technical landlord reads "has the job … closes automatically," assumes the
vendor was notified, and never actually texts them. The job stalls and never auto-closes — the app
claimed an action it didn't take.

### BUG-3: Editing a work order silently truncates RequestedAt/CompletedAt to UTC-midnight
**Severity:** Low
**Location:** Work-order detail → Edit → Save (Costs & timing) (`/maintenance/{id}`)
**Expected:** Editing one field (e.g. adding Actual Cost) should not alter unrelated timestamps.
**Actual:** Any detail-page Save rewrites `RequestedAt` and `CompletedAt` to **00:00:00 UTC** of their
date, discarding the real time-of-day, because the edit form re-sends those fields as date-only
(`yyyy-MM-dd`). The status timeline keeps the true instants, and the (UTC-pinned date) display is
unchanged, so it's invisible — but the columns lose precision on every edit.
**Evidence:** WO #199 — after adding Actual Cost `175.50`:
`RequestedAt` 03:39:54 → `00:00:00`, `CompletedAt` 03:41:05 → `00:00:00`
(timeline events still read 03:39:54 / 03:40:32 / 03:41:05).
**Code Reference:** `web/src/lib/components/records/WorkOrderDetail.svelte:87-92` (seeds dates with
`.slice(0,10)`) + `web/src/lib/schemas/index.ts:332-338` (`workOrderDetailSchema` passes the date
fields straight through as `optionalText`); server stores them via `WorkOrderService.UpdateAsync`
`:450-453`.
**Suggested Fix:** On the detail edit, only send a timing field when the user actually changed it
(dirty-check against the seeded value), so an unchanged date isn't re-submitted and overwritten — or
preserve the existing time-of-day when only the date is edited.
**Why This Matters:** Low today (the app shows only dates), but any future age/SLA-in-hours metric, or
export, built on `CompletedAt`/`RequestedAt` would silently lose the real time the moment a landlord
touches any other field.

## Potential Issues (need investigation)

- **Completion-date off-by-one for evening completions (Low).** The one-click Complete stamps
  `CompletedAt = DateTime.UtcNow` (a real instant) and the detail renders it UTC-pinned via
  `formatDateOnly`. A WO completed at 23:41 EDT on **Jun 28** displayed **"Jun 29, 2026"** (its UTC
  day). Correct for genuine date-only fields (stored as UTC-midnight), but a real instant shown
  UTC-pinned reads a day ahead for behind-UTC evening completions. Ref: `WorkOrderService.cs:460`
  (stamp) + `web/src/lib/utils/date.ts:15-25` (`formatDateOnly`, `timeZone: 'UTC'`). Note this also
  collides with BUG-3: after the next edit, the stamp becomes UTC-midnight of that "Jun 29" anyway.

- **No property↔tenant/unit consistency check on links (by design?).** Creating an appointment let me
  attach tenant "Nancy Grace" to property "293 Mallard Point Dr" with no check that she's a
  tenant/lease there; work orders are the same. Scope is enforced (the FK must be in-portfolio) but
  not relational correctness. Ref: `AppointmentService.ReferencesInScopeAsync` (portfolio-only),
  `WorkOrderService.CreateAsync:221-243`. Likely intentional loose linking — flagging so it's a
  conscious choice.

## Observations (work as built; not bugs)

- **Recurring-maintenance generation not triggerable in-session.** The worker polls every 24h with one
  cycle at Engine startup (`RecurringMaintenanceWorker.cs:14` `PollInterval = 24h`; first cycle runs
  before the first delay in `EngineWorkerBase.cs:60-86`) and there is **no manual-generate endpoint**
  (`RecurringMaintenanceController` is CRUD-only). So I could not observe a live New work order appear.
  I did verify: create persists with `NextDueDate` normalized to UTC-midnight (`2026-06-28 00:00:00`),
  the list shows "Runs every Quarter · Next: Jun 28, 2026," and the active/pause switch flips
  `IsActive` in the DB. The generation logic reads correct by inspection — one work order per due task
  per run (single catch-up even if periods were missed), `NextDueDate` advanced past today, the
  insert+advance wrapped in one transaction (`RecurringMaintenanceService.GenerateAsync:54-171`). The
  master gate `cfg.EnableRecurringMaintenance` defaults **true** (`NotificationsConfig.cs:22`).
  Recommend a dev-only "Run now" hook (or a short dev poll interval) so this path is testable.

- **Work orders have no line-item costs.** Only single `EstimatedCost`/`ActualCost` fields; there is no
  cost breakdown to sum and no auto-creation of an Expense from a work-order cost. So "costs that don't
  total" has nothing to mis-total here — `$150` est / `$175.50` actual persisted and displayed
  correctly. (The `WorkOrder.Expenses` navigation exists but nothing in the maintenance flow populates
  it.)

- **Inspection "Complete" guards are solid.** Completing with **all items Pending** (or zero items) is
  blocked server-side ("Mark at least one checklist item Pass, Fail, or N/A …"); the inspection stayed
  `Scheduled`/`CompletedAt NULL`. Completing with marks spawned exactly **one** work order for the
  single Fail item (#203, Category "Inspection", New), linked via `InspectionItem.SpawnedWorkOrderId`,
  stamped the inspection `CompletedAt`, set `ReportStoredFileId`, and the UI showed "Created 1 work
  order for the failed items" + a Download report button. The generic-PATCH→Completed block
  (`InspectionService.cs:212-217`) is correctly enforced server-side (no UI path reaches it).

- **Appointment time validation works both ways.** End ≤ start is rejected ("The appointment end time
  must be after its start time."), and a valid appointment converted local→UTC correctly
  (10:00 EDT → `14:00Z`) and rendered local time + property/tenant/status in the list.

- **"Vendor has the job" hint aside (BUG-2):** appointments/work-order status badges in the list always
  matched the DB (WO #199 Completed, #198 New; server-side status filter narrowed correctly).

## What Was Tested
1. **Work-order lifecycle.** Created WO #198 (293 Mallard Point Dr, Scheduled `2026-07-05 10:00`) →
   clicked Completed → **400 / blocked** (BUG-1; screenshot saved); verified DB unchanged.
2. Created WO #199 (293 Mallard, Unit 201, vendor Apex Plumbing, est cost 150, **no schedule**) →
   New→In progress→Completed via the status dialogs with notes → verified `Status=4`,
   `CompletedAt=03:41:05`, and the 3-row timeline (null→New→InProgress→Completed) in the DB. Confirmed
   unit/vendor links render on the detail page (BUG-2 noticed here).
3. **Costs.** Added Actual Cost `175.50` via Edit; verified `$150.00`/`$175.50` display + DB; noticed
   the timestamp truncation (BUG-3). Then set Completed `06/20/2026` < Requested `06/29/2026` → correctly
   blocked ("completion date can't be before the work order was requested"), DB unchanged.
4. **Recurring maintenance.** Created RMT #1 "QA-T4-234400 HVAC filter quarterly" (Quarterly, Next due
   06/28/2026, category HVAC) → verified `NextDueDate=2026-06-28 00:00:00`, list display, and the
   pause/resume toggle flipping `IsActive` in the DB. (Live generation not triggerable — see Observations.)
5. **Appointments.** Created Appt #125 (Showing, 06/30 10:00–11:00, 293 Mallard, tenant Nancy Grace) →
   verified UTC conversion (`14:00Z`/`15:00Z`), property+tenant+status in List view. Attempted an
   inverted-window appointment (end 13:00 < start 14:00) → rejected, not persisted (count 0).
6. **Inspections.** Created Insp #64 (MoveOut, Move-Out built-in checklist, 16 items, inspector "QA-T4
   Inspector"). Tried Complete with all Pending → blocked. Marked Sink=Pass, Appliances=Fail,
   Cabinets=Pass, Floor=N/A → Complete → `Status=Completed`, `CompletedAt=03:52:24`,
   `ReportStoredFileId=579`, spawned WO #203 for the Fail item (item 884 → `SpawnedWorkOrderId=203`),
   UI "Created 1 work order …" + Download report.
7. **List/data-access.** Search `q=QA-T4` and `status=Completed` filters narrowed server-side; list
   status badges matched the DB.

### Test data left behind (portfolio 1, all marked QA-T4)
WO #198 (New, future-sched), WO #199 (Completed), WO #203 (spawned, New), Appt #125 (Scheduled),
RMT #1 (active), Insp #64 (Completed). Left in place as evidence; none are seed records.
