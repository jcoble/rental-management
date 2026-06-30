# Scenario 03 — Operations Core (properties, units, tenants, vendors)

**Domain:** Operations. **Suggested tester session:** `tester2`.

## Mission
Test the day-to-day record management a landlord does: create and relate **properties → units →
tenants**, manage **vendors**, and confirm the relationships, navigation, and status are correct and
portfolio-scoped. This is the structural backbone every other feature hangs off.

## Get acquainted with the code first
- Frontend: `web/src/routes/(protected)/properties/` (list + `[id]`), `units/` (list + `[id]` with
  its tabs), `tenants/` (list + `[id]`), `vendors/`.
- Web API client: `web/src/lib/api/endpoints/properties.ts`, `units.ts`, `tenants.ts`, `vendors.ts`.
- API: `PropertyController.cs`, `UnitController.cs`, `TenantController.cs`, `VendorController.cs`
  (all portfolio-scoped via `AuthenticatedPortfolioControllerBase` — inbound FK refs validated
  in-portfolio).
- Services: `Services/Domain/PropertyService.cs`, `UnitDashboardService.cs`, `TenantService.cs`,
  `VendorService.cs`. Entities under `Core/Entities/` (`Property.cs`, `Unit.cs`, `Tenant.cs`,
  `Vendor.cs`).

## Flows to exercise
1. **Create a property**, then **add units** to it, then **create a tenant** and associate it
   (via lease or directly per the UI). Confirm each appears in its list and detail, and that counts
   (e.g. "N units", occupancy) update correctly.
2. **Unit detail tabs**: walk the unit `[id]` tabs (overview, rent, lease, maintenance, etc.).
   Confirm each tab loads the right data for that unit and cross-links resolve (clicking a tenant,
   lease, or work order navigates to the right record).
3. **Edit + persist**: edit a property/unit/tenant field you created; re-open to confirm it saved.
4. **Vendors**: create a vendor, confirm it's selectable where vendors are used (e.g. work orders /
   expenses), and that contact fields validate.
5. **Edge cases**: required fields blank; very long names; duplicate unit numbers within a property;
   deleting/archiving a record that's referenced elsewhere (should be blocked or handled cleanly).

## Watch especially for
- List counts / occupancy / "N units" badges that don't match reality (possible in-memory
  aggregation — flag the service file:line).
- Broken cross-links (clicking a related record 404s or lands on the wrong id).
- Any picker or detail that exposes records from outside the current portfolio (IDOR).
- Save that appears to succeed in the UI but doesn't persist (re-open shows old value).

## Data hygiene
Marker `QA-T2-<HHMMSS>` on everything you create. Avoid editing money/lease records (the Money
tester owns those); stick to structural records.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/03-operations-core.md`
