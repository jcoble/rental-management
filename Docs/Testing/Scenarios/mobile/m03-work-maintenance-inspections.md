# Mobile scenario M03 — Work: repairs, work orders, inspections, appointments

## Purpose
A tenant reported a leak; the landlord logs the repair from the phone, assigns a vendor, updates it, and closes it. Also walks an inspection checklist and views appointments.

## Read the implementation first
- mobile/lib/features/maintenance/, recurring_maintenance/, inspections/, appointments/, technician/, vendors/
- RentalCommand.Api/Controllers/WorkOrderController.cs, InspectionController.cs, AppointmentController.cs, VendorController.cs

## Rough exploration areas
- Work tab: lists, filters (open/done), sort; open a sample repair — status, unit, tenant, vendor, timeline, photos.
- Create a repair "QA-20260827-m03 leak under sink" for a sample unit; add a photo if the camera/gallery flow allows (skip if it needs a real camera), assign vendor, change status through its lifecycle, add a note, mark done.
- Inspections: open one, tick items, add a finding, complete (or leave incomplete and confirm state persists on reopen).
- Appointments: list, detail, create one for tomorrow.
- Real-time: after changing status, does the list update without manual refresh?

## Edge cases worth trying
- Status transitions the code should block (done → open?), blank title, very long description, vendor with no phone, same appointment time twice.

## What to verify visually
- Status colours/labels consistent between list and detail; dates in the landlord's local format; nothing jargon-y ("dispatch", "disposition").
