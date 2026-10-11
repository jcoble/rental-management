# Exploratory Test Report: Properties, units, CRUD, filters, health, and unit tabs
Date: 2026-08-27  
Tester: e2e-s13  
Duration: approximately 55 minutes

## Scenario
Exercise property and unit creation/editing, structure and occupancy behavior, list filters and health indicators, contextual links, all current Unit Command Center tabs, and URL/history state restoration.

## Summary
Created and edited additive QA records for both One rental and Multiple rentals properties, then exercised the property list, unit list, property workspace, and all current unit tabs. Core CRUD validation, duplicate-unit protection, counts, occupancy, ownership display, contextual scan connection, filters, and tab URL restoration worked. Three confirmed issues were found: an out-of-range property page displays a misleading empty portfolio, the global New unit flow loses its Add Unit guidance, and property mutation responses contain an invalid workspace entry even though subsequent reads are correct.

## Bugs Found

### BUG-1: Normalize an out-of-range properties page
**Severity:** Medium  
**Location:** `/properties?page=2` when the filtered result has one page  
**Expected:** An initial or bookmarked page beyond the server-reported page count should be normalized to a valid page, or otherwise show a valid bounded state. With 11 properties and a page size of 20, the page should be page 1 and show `1–11 of 11`; it should not present the portfolio as empty.  
**Actual:** Navigating directly to `https://localhost:5667/properties?page=2` returned zero rows while the page displayed `No rentals yet`, `21–11 of 11`, and `Page 2 of 1`. The URL stayed on `page=2`, so a stale bookmark or Back/Forward restoration can make existing properties appear to have disappeared.  
**Evidence:** Screenshot [e2e-s13-properties-invalid-page.png](/home/blackcolours/Workbox/screenshots/e2e-s13-properties-invalid-page.png); observed page text and URL above.  
**Code Reference:** `web/src/routes/(protected)/properties/+page.svelte:84-99` deliberately seeds `gridPage` from the initial URL without bounds checking, and `:119-138` sends `skip: (gridPage - 1) * PAGE_SIZE`; `web/src/lib/components/data-grid/DataGrid.svelte:296-314` computes the valid `pageCount` but does not clamp the initial `page` before calculating the range.  
**Suggested Fix:** When the properties response supplies `totalCount`, clamp `gridPage` to `Math.max(1, Math.ceil(totalCount / PAGE_SIZE))` and sync the corrected page back into the URL before rendering the grid.
**Why This Matters:** A non-technical landlord can reasonably interpret the empty state as lost or deleted rental data and may start recreating records.

### BUG-2: Carry the global New unit action through to Add Unit
**Severity:** Medium  
**Location:** `/units` → `New unit` → property selection  
**Expected:** Clicking `New unit` should guide the landlord through selecting a property and then open or spotlight the contextual `Add Unit` action. The property-list implementation explicitly intends to forward `coach=add-unit` to the property detail, where the Add Unit control is available.  
**Actual:** Clicking `New unit` navigated to `/properties?coach=open-property-for-units`, but after waiting for the list to load there was no coach overlay. Selecting QA property `QA-20260827-s13-Multi-252255` opened `/properties/11?area=summary` with no `coach=add-unit`; the Summary area was active and `data-testid="unit-add-button"` was not present because Add Unit is rendered only in the Rentals area. The landlord must discover and click Rentals before they can add a unit.  
**Evidence:** Screenshot [e2e-s13-new-unit-context.png](/home/blackcolours/Workbox/screenshots/e2e-s13-new-unit-context.png); observed destination URLs and zero visible coach/add-unit target counts after the selection.  
**Code Reference:** `web/src/routes/(protected)/units/+page.svelte:161-164` creates the global link; `web/src/routes/(protected)/properties/+page.svelte:69-81` relies on a second-hop `forwardUnitCoach` flag; `web/src/routes/(protected)/properties/[id]/+page.svelte:696-710,796-835` renders the Add Unit target only for the Rentals area; `web/src/lib/components/onboarding/CoachTrigger.svelte:18-40` globally consumes and removes the coach query, while `web/src/lib/onboarding/coach.svelte.ts:67-94` gives up after a short one-shot target lookup.  
**Suggested Fix:** Make the `open-property-for-units` handoff navigate the selected property directly to `?area=rentals&coach=add-unit`, preserving that intent before the global coach trigger consumes the first query value.
**Why This Matters:** The most obvious action for a landlord who needs to add a unit ends at a summary screen with no form and no visible next step, so the primary setup task stalls without an explanation.

### BUG-3: Return a populated workspace entry from property mutations
**Severity:** Low  
**Location:** Property setup and update responses (`POST /api/v1/properties/setup`, `PATCH /api/v1/properties/{id}`)  
**Expected:** A mutation response should satisfy the same `PropertyResponse.WorkspaceEntry` contract as a property list/detail response. For MultiRental property 11 it should contain `Destination: Property`, `PropertyId: 11`, and the six approved areas. For SingleRental property 10 it should contain `Destination: Unit`, `PropertyId: 10`, and its canonical unit ID.  
**Actual:** UI-originated setup responses for both QA properties, and the UI-originated property edit response for property 11, contained `workspaceEntry: {"destination":"Property","propertyId":0,"unitId":null,"areas":[]}`. A subsequent list/detail GET returned the correct property ID and workspace areas, so the current page's invalidation/refetch masks the bad mutation payload.  
**Evidence:** Captured the HTTP 201 setup responses after creating property IDs 10 and 11 and the HTTP 200 PATCH response after editing property 11 through the UI; compared them with the subsequent GET/list payloads. The QA records themselves remained persisted and correct after reload.  
**Code Reference:** `RentalCommand.Api/DTOs/PropertyDtos.cs:29-64` defines the server-owned workspace contract and `:123-127` documents it on `PropertyResponse`; `:132-157` maps `FromEntity` without setting `WorkspaceEntry`, leaving its default `propertyId` and empty areas; `RentalCommand.Api/Services/Domain/PropertyTenantCrudRule.cs:821-855` builds mutation snapshots with `PropertyResponse.FromEntity` and never populates the workspace entry; `RentalCommand.Api/Controllers/PropertyController.cs:56-89` exposes the affected setup/update responses.  
**Suggested Fix:** Populate `response.WorkspaceEntry` inside `SnapshotPropertyAsync` using the persisted rental structure, property ID, and the scoped canonical SingleRental unit ID before serializing the setup and update receipts.
**Why This Matters:** Any client that routes or restores context from the mutation receipt can receive property ID 0, the wrong destination, and no available areas immediately after saving. The current UI hides the defect only because it refetches.

## Potential Issues (need investigation)

- The first full traversal of unit 26's Money area emitted transient HTTP 500 responses for `/api/v1/expenses/page?...&unitId=26` and `/api/v1/properties/11`. A fresh traversal of the same paths, waiting five seconds, completed without errors and showed the correct empty states. This was not counted as a confirmed bug because it did not reproduce; investigate if the concurrent activity or HMR timing can be reproduced.
- The occupied seeded unit took approximately 7–8 seconds to reach its populated dashboard on a cold navigation, and the edited QA unit took roughly 1.6 seconds to show its new rent after saving. Both eventually reconciled correctly, so this is recorded as a performance observation rather than a correctness bug.

## Observations

- Blank property submission showed client-side required-field messages. Blank unit submission showed `Unit number is required`. Duplicate unit creation was rejected with HTTP 409 and did not add a second row.
- Property 10 was created as One rental with one unit; property 11 was created as Multiple rentals with two units and then safely expanded to three units. Property and unit detail views showed the expected addresses, owner, unit counts, vacancy, and status. Multiline notes and an emoji persisted after reopening.
- Property search, case-insensitive QA marker search, type/status filters, no-result empty state, sort ascending/descending/cleared, and unit search/property picker/paging worked. Unit paging showed `1–20 of 25` and `21–25 of 25` correctly.
- The current Unit Command Center's Summary, Leasing (Listing and Applications), Tenant & lease (Lease & move-in and Residents), Money (Rent & payments and Property expenses), Maintenance (Repairs, Inspections, Recurring work, and Move-out & turnover), and Documents & history (Documents and Activity/history) routes all remained scoped to unit 26. Browser Back/Forward and reload restored the active money subview URL.
- The contextual unit Scan action showed one connected source for property 11 and unit 26. The property delete confirmation correctly explained that property 11 still had three units and kept Delete unavailable; no destructive delete or archive was performed.
- Nonexistent property/unit routes did not leak data: the property request returned 404 and the unit page ended on `This unit could not be loaded.` The database contained only portfolio 1 records in the read-only inventory available during this run, so a cross-portfolio IDOR comparison was not possible.
- The current August UI opens a SingleRental property in the property detail workspace rather than directly opening its unit. This matches the active routes and was treated as an intentional current-design observation, not a bug from the older scenario wording.

## What Was Tested

- Logged into the seeded SANDBOX as `admin@rentalcommand.local` in a headless Chromium context at `1710x990`.
- Created QA property `QA-20260827-s13-One-083965` with unit 23 and QA property `QA-20260827-s13-Multi-252255` with units 24–25; added unit 26 as `QA-20260827-s13-Added-426060`.
- Exercised property setup validation, property status change and restoration, property notes edit, unit edit (rent, notes, and residential fields), duplicate-unit protection, and persistence after close/reload.
- Exercised property list filters/search/sort/no-result/page boundary and unit list search/sort/property filter/paging. Captured the invalid-page screenshot and the New unit handoff screenshot.
- Opened the property workspace and traversed every current Unit Command Center tab and nested view, including contextual scan, empty money/maintenance/document states, Back/Forward, and reload restoration.
- Checked stale property/unit IDs and opened then cancelled the live-unit property delete guard. No seed owner, tenant, lease, maintenance, property, or unit was deleted; only additive QA records were changed.
- Browser cleanup: stopped e2e-s13.
