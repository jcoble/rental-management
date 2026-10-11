# Mobile scenario M05 — "Scan or add", Ask, leasing (rentals/applications/move-ins), owners

## Purpose
The flagship: the landlord taps "Scan or add", captures a document (or picks one), reviews the draft, and confirms. Also tries the Ask assistant and the leasing flows (listings, applications, move-ins), and the owners screen.

## Read the implementation first
- mobile/lib/features/scan/, ai/, voice/, leasing/, applications/, leases/, tenants/, owners/, owner_portal/, owner_reports/
- RentalCommand.Api scan/draft controllers, LeaseController.cs, application/listing controllers, OwnerEntityController.cs
- Note: the workbox has NO LLM extraction provider — a scan may stay "processing" or fail. Judge the UX of that failure (does it explain, can the user fall back to manual entry?), not the extraction itself.

## Rough exploration areas
- "Scan or add" sheet: every option; the manual "add" paths (tenant, expense, repair, lease…) — create one record each with the QA-20260827-m05 prefix.
- Scans list ('/scans') and a draft ('/scan/:draftId') if any exist in sample data.
- Ask: send "which tenants are behind on rent?" and one nonsense question; judge the response UX and errors.
- Leasing: rentals/listings, an application detail (approve/decline flow if a sample one is pending), a move-in detail, a lease detail.
- Owners screen and owner statements/reports.

## Edge cases worth trying
- Cancel mid-capture, deny a permission prompt, back out of a draft unconfirmed then return.

## What to verify visually
- The draft review reads as "confirm what the computer typed", confidence/uncertain fields obvious; long forms scroll with keyboard; nothing clipped.
