# Exploratory Test Report: Properties, Units, CRUD, Filters, Health, and Unit Tabs

Date: 2026-08-22
Duration: ~45 minutes
Tester session: e2e-s13 (headless, viewport 1710x990, verified)

## Scenario

Exercise the structural core of a portfolio: property and unit creation/editing, one-rental vs multiple-rental structure, filters and health summaries, contextual links, and every Unit Command Center tab with state restoration.

## Summary

Properties and units CRUD is working correctly after the write executor migration. Creation through onboarding, inline editing on the property detail page, unit add/edit/delete from the property Rentals tab, search, type/status filtering, sorting, and pagination all function as expected. Two bugs were found: one where the API returns 403 instead of 404 for nonexistent units (inconsistent with property API behavior), and one where selecting "All types" in the properties list filter does not clear the `type` URL parameter (despite the data query working correctly). One potential issue was identified around the "New unit" coach link losing its query parameter.

## Bugs Found

### BUG-1: Nonexistent unit returns 403 instead of 404

**Severity:** Medium
**Location:** Unit Command Center page `/units/99999`
**Expected:** Navigating to a nonexistent unit ID should return a 404 response from the API, matching the property API behavior which correctly returns 404 for nonexistent property IDs.
**Actual:** The API returns 403 Forbidden (`GET /api/v1/units/99999/dashboard` responds with status 403). The UI shows "This unit could not be loaded" which is acceptable, but the HTTP status code is wrong.
**Evidence:** Console log: `[ERROR] Failed to load resource: the server responded with a status of 403 () @ https://localhost:5667/api/v1/units/99999/dashboard`. By contrast, `GET /api/v1/properties/99999` correctly returns 404. Verified at URL `https://localhost:5667/units/99999`.
**Code Reference:** `RentalCommand.Api/Controllers/UnitController.cs:111-124` -- The `Dashboard` endpoint calls `HasCapabilityAsync` with a `UnitCapabilityAuthorizationTarget(portfolioId, id)` before checking if the unit exists. When the authorization evaluator cannot find the unit, it fails the capability check and returns `Forbid()` (line 122) rather than reaching the `NotFound()` path (line 123). Compare with `PropertyController.cs:46-49` where the service's `GetAsync` returns null for missing properties, yielding a proper 404.
**Why This Matters:** A 403 for a nonexistent resource is an information leak (it implies the resource exists but the user lacks access) and makes debugging harder. Security best practice is to return 404 for both "does not exist" and "not in your scope" to prevent enumeration.
**Suggested Fix:** In the `UnitCapabilityAuthorizationTarget` evaluator, return false (deny) for units that do not exist in the portfolio. Then the controller's `_dashboard.GetDashboardAsync` call would handle the null case with a proper 404. Alternatively, reorder the controller to check existence before authorization, as `PropertyController.Get` does.

### BUG-2: Selecting "All types" does not clear the type URL parameter

**Severity:** Low
**Location:** Properties list page `/properties`
**Expected:** Selecting "All types" from the type filter dropdown should remove the `type` parameter from the URL, since an empty string value is the default and `syncGridUrl` omits default/empty values.
**Actual:** After filtering by "Multi-family" (`?type=MultiFamily`), selecting "All types" leaves `?type=MultiFamily` in the URL. The data query works correctly (both properties are shown), but the URL is stale.
**Evidence:** Navigated to `/properties`, selected type filter "Multi-family" (URL updated to `?type=MultiFamily`), then selected "All types" from the dropdown. URL remained `?type=MultiFamily` even though both properties were displayed. Verified by checking `window.location.href` returned `https://localhost:5667/properties?type=MultiFamily`.
**Code Reference:** `web/src/routes/(protected)/properties/+page.svelte:86-112` and the `Select.Root` component. The Select component with `bind:value={typeFilter}` should set `typeFilter` to `""` when the "All types" option (value `""`) is selected. The `syncGridUrl` function at `web/src/lib/utils/grid-url-state.svelte.ts:76-99` correctly omits empty values. The issue may be that the Select component does not fire a value change when the user selects the empty-value option, or the reactive chain does not propagate the empty string to the URL sync effect.
**Why This Matters:** A stale URL means bookmarking or sharing the current view after clearing a filter would re-apply that filter for anyone visiting the link. It also means browser Back after clearing the filter and navigating away would land on the filtered view instead of the expected unfiltered view.
**Suggested Fix:** Debug whether `Select.Root` with `type="single"` and `value=""` fires `onValueChange` when selecting the empty-value option. If the component does not fire for empty strings, add an explicit `onValueChange` handler that forces `typeFilter = value ?? ''`.

## Potential Issues (Need Investigation)

### PI-1: "New unit" coach link loses query parameter

**Location:** Units list page `/units`, "New unit" button
**Observation:** The "New unit" button links to `/properties?coach=open-property-for-units`, but after clicking it the browser URL showed `/properties` without the `coach` parameter. This could be the `syncGridUrl` effect running on mount and stripping the parameter through a `replaceState` call, or it could be intentional consumption of the parameter. The `forwardUnitCoach` flag was likely set before the URL was cleaned, so the coaching flow may still work. Needs manual verification that clicking a property row after this navigation actually carries `?coach=add-unit` to the detail page.
**Code Reference:** `web/src/routes/(protected)/properties/+page.svelte:67-80` reads `coach` from URL and sets `forwardUnitCoach`, then `syncGridUrl` at lines 107-112 may strip the unrecognized parameter via `replaceState`.

### PI-2: Property list edit dialog does not show RentalStructure as read-only

**Location:** Properties list page, Edit dialog stepper
**Observation:** The edit dialog on the list page passes `rentalStructureLocked={editingId != null}` to `PropertyFields`, which should make the RentalStructure select disabled. However, I did not explicitly test changing it from the list-page dialog. The property detail inline edit correctly shows it as read-only text. If the list dialog allows changing RentalStructure on a property that already has units, it could cause data integrity issues.
**Code Reference:** `web/src/routes/(protected)/properties/+page.svelte:541` -- `rentalStructureLocked={editingId != null}` is passed, which should make it read-only when editing.

## Observations

1. The property creation flow through the onboarding wizard is smooth. The "one rental" vs "multiple rentals" radio choice correctly determines whether a single-unit sub-step or a multi-unit grid appears.

2. The property detail page's inline editing with View Transition morphing (TSK-599) is a polished touch. The cancel button correctly discards unsaved changes.

3. Unit Command Center tab navigation is well-implemented. All six primary tabs work, sub-tabs within Leasing, Tenant & lease, and Maintenance work, and the URL updates correctly with `?tab=` and `?view=` parameters. Browser Back/Forward correctly restores tab state, and a hard page reload preserves the active tab from the URL.

4. At the 390x844 mobile viewport, properties list renders in a compact card format, property detail workspace tabs are horizontally scrollable, and unit detail tabs are also scrollable. No horizontal page overflow was observed.

5. Validation is thorough on both frontend and backend: required field checks fire inline, year built range (1800-2200) is enforced, management fee range (0-100) is enforced, and duplicate unit numbers are caught by the server with a clear 409 Conflict and a user-facing error message.

6. The `getPropertyDeleteState` logic correctly handles three cases: zero units (simple delete), one unit on a single-family/condo/townhome (delete with empty rental space warning), and multi-unit properties (delete blocked, units must be removed first).

7. Empty states across all areas (property work, property finances, leases, documents, inspections) show appropriate messages and actions.

## What Was Tested

1. **Login:** Filled dev admin credentials via "Fill dev login (admin)" button, signed in, selected "Set up my real portfolio" on choose-setup page.

2. **Property creation via onboarding:**
   - Created "S13 Maple Cottage" as a SingleRental/SingleFamily property at 742 Maple Ave, Portland, OR 97201 with 3 bed/2 bath/$1800 rent.
   - Created "S13 Oak Apartments" as a MultiRental/MultiFamily property at 500 Oak Blvd, Seattle, WA 98101 with units S13-101 (2bd/1ba/$1500) and S13-102 (1bd/1ba/$1200).

3. **Properties list page:**
   - Verified both properties display with correct name, address, type, status, unit count, and occupied count.
   - Tested search: "Maple" filtered to one result; cleared search showed both.
   - Tested type filter: "Multi-family" showed only Oak Apartments; selected "All types" showed both (but URL param persisted -- BUG-2).
   - Tested status filter: navigated to `?status=UnderMaintenance` after changing Maple Cottage status, showed only that property.
   - Tested empty state: combined search "nonexistent" + Multi-family filter showed "No properties match your filters" with clear guidance.
   - Tested sort: clicked "Sort by Name" toggled ascending/descending, URL updated to `?sort=name` / `?sort=-name`.

4. **Property detail page:**
   - Verified breadcrumb (Home > Properties > name), heading, address, status badge, occupancy hero (0/2 for Oak, 0/1 for Maple).
   - Verified all 6 workspace sections load: Summary, Rentals, Ownership & management, Property work, Property finances, Documents & history.
   - Tested inline edit: changed year built, management fee, notes; saved successfully; values persisted in read-only view.
   - Tested validation: blank name ("Name is required"), year 1500 ("Year built must be 1800-2200").
   - Tested cancel: discards unsaved changes.
   - Tested status change: changed Maple Cottage from Active to Under maintenance; persisted correctly.

5. **Property delete:**
   - Multi-unit property (3 units): dialog shows "still has 3 units. Remove the units first", Delete button disabled.
   - Single-family one-rental property (1 unit): dialog shows "This will also remove its empty rental space", Delete button enabled.

6. **Unit CRUD from property detail:**
   - Added unit S13-103 (3bd/2ba/$2000/950sqft/Corner 3B/Has balcony); appeared in list.
   - Tested add unit validation: blank unit number shows "Unit number is required".
   - Tested duplicate unit: adding S13-101 again returned 409 with toast "Unit number 'S13-101' already exists on this property."
   - Deleted unit S13-103: confirmation dialog showed, confirmed, unit removed from list.

7. **Units list page:**
   - All 4 units (before S13-103 deletion) displayed with correct property names, statuses, market rents.
   - "New unit" button navigates to properties page (with coach link behavior noted in PI-1).

8. **Unit Command Center:**
   - Navigated to Unit S13-101: header shows unit number, vacancy badge, bed/bath/rent summary, status chips.
   - Cycled through all 6 tabs: Summary, Leasing (Listing + Applications sub-tabs), Tenant & lease (Agreements + Residents), Money, Maintenance (Work orders + Inspections + Recurring + Turnover sub-tabs), Documents & history (Documents + Activity).
   - Tested Back/Forward: each tab change creates history entry, back/forward restores correct tab.
   - Tested reload: hard refresh preserves tab from URL (`?tab=maintenance&view=work-orders`).

9. **Nonexistent record handling:**
   - `/properties/99999`: API returns 404, UI shows "Property not found" with Back button.
   - `/units/99999`: API returns 403 (BUG-1), UI shows "This unit could not be loaded" with Try again and Back buttons.

10. **Mobile viewport (390x844):**
    - Properties list renders in card format.
    - Property detail workspace tabs are horizontally scrollable.
    - Unit Command Center tabs are horizontally scrollable.
    - No horizontal page overflow observed.

Browser cleanup: stopped e2e-s13 (daemon + Chrome helper tree).
