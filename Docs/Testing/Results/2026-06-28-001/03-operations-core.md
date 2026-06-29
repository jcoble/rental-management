# Exploratory Test Report: Operations Core (properties, units, tenants, vendors)
Date: 2026-06-28
Tester: tester2
Duration: ~2h (deep code read + browser drive on Portfolio 1 / admin@rentalcommand.local)

## Scenario
Test the day-to-day structural records a landlord manages — properties → units → tenants,
plus vendors — confirming relationships, counts/occupancy, cross-links, persistence, and
portfolio-scoping (IDOR).

## Summary
Core CRUD is solid: creates/edits persist (DB-verified), list counts and occupancy reconcile
across list/detail (computed DB-side as correlated subqueries — no in-memory aggregation smell),
unit cross-links and all 8 unit-detail tabs load correctly, and validation fires for blank/invalid
fields. Direct-navigation IDOR is tight: a property/unit/tenant/vendor id from another portfolio
(Portfolio 7) all return "not found" while logged into Portfolio 1, and the work-order vendor picker
shows only in-portfolio vendors. The headline problem is **record deletion**: deleting a property
does **not** remove its units (it orphans them as live DB rows) and has **no guard at all** against
deleting a property that still has units or active leases — unlike the tenant and vendor delete paths,
which both guard. Two related unit-number issues (soft-deleted numbers can't be reused; duplicate
numbers surface an opaque conflict message) round out the findings.

## Bugs Found

### BUG-1: Deleting a property orphans its units (and has no active-lease/occupancy guard); the confirmation falsely claims it removes them
**Severity:** High
**Location:** Property detail + list → Delete property. `PropertyService.DeleteAsync`.
**Expected:** Deleting a property should either (a) be blocked while it still has live units / active
leases — the way `TenantService.DeleteAsync` blocks on an active lease and `VendorService.DeleteAsync`
blocks on open work orders — or (b) cascade the soft-delete to its child units so the confirmation's
promise ("This also removes its units") actually holds. No active lease/unit should be left live but
orphaned under a deleted parent.
**Actual:** `DeleteAsync` only sets `DeletedAt` on the Property row. Child units are left with
`DeletedAt = null`. After I deleted my property (id 135) which had 2 live units:
- DB: Property 135 `DeletedAt` set; Unit 223 (T2-101) and Unit 227 (T2-103) still `DeletedAt = null` (live).
- UI: both units vanish from the global Units list ("No units match your filters") and direct nav to
  `/units/223` shows "This unit could not be loaded" — because every unit read INNER-JOINs through the
  now-soft-deleted Property. So the rows are invisible/inaccessible from every UI surface yet persist live.
There is **no guard whatsoever** in `DeleteAsync`. The same code path would soft-delete a property
that has an **active lease** (with its tenant, rent schedule, deposit, and money) and silently orphan
it — the exact "orphaned-yet-Active, invisible in the UI" hazard that the tenant/vendor delete guards
were written to prevent.
**Evidence:**
- DB after delete: `property 135 | deleted=t` ; `unit 223 T2-101 | deleted=f` ; `unit 227 T2-103 | deleted=f`.
- `/units/223` → "This unit could not be loaded." Screenshot: `output/playwright/tester2-orphan-unit-223.png`.
- Units list search "T2-10" → "No units match your filters" (orphans invisible).
- Confirm dialog text: `Delete "QA-T2-0135 Test Property"? This also removes its units.`
**Code Reference:**
- `RentalCommand.Api/Services/Domain/PropertyService.cs:251` (`DeleteAsync` — soft-deletes Property only;
  no cascade, no guard). Contrast `TenantService.cs:253` (active-lease guard) and `VendorService.cs:168`
  (open-work-order guard).
- Misleading confirm copy: `web/src/routes/(protected)/properties/[id]/+page.svelte:686` and
  `web/src/routes/(protected)/properties/+page.svelte:423`.
**Suggested Fix:** In `PropertyService.DeleteAsync`, before soft-deleting, throw a
`DomainValidationException` when the property has any active lease (mirror the tenant guard:
`_db.Leases.AnyAsync(l => l.PropertyId == id && l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active)`),
and ideally when it still has live units (require the user to remove units first). For the
"also removes its units" promise, additionally set `DeletedAt` on the property's live units within the
same `SaveChangesAsync`. Recommended: ship the active-lease/unit guard now (data-safety), then add the
cascade so the confirmation copy is truthful.
**Why This Matters:** A landlord who deletes a property to tidy up can silently lose an **active lease and
its money** from the entire UI while it stays Active in the database — no warning, no way to see it again.
At minimum, units they were told would be "removed" linger as invisible junk that still occupy the unit-number
unique index. This is the structural backbone every other feature hangs off; orphaning it corrupts the model.

### BUG-2: A soft-deleted unit number can't be reused — the unique index isn't filtered on `DeletedAt`
**Severity:** Medium
**Location:** Property detail → Add Unit (after a unit was deleted).
**Expected:** After removing unit "T2-102", the landlord should be able to add a new unit "T2-102" on the
same property — the old one is gone from their world. The soft-delete query filter hides it; the uniqueness
rule should only apply to live units.
**Actual:** Adding "T2-102" after deleting it returns 409 "The request conflicts with existing data or a
data-integrity rule." The unique index `(PropertyId, UnitNumber)` is a plain unique index, so the
soft-deleted row 224 still occupies the index slot and blocks the new insert — even though no "T2-102" is
visible anywhere in the UI.
**Evidence:** DB shows unit 224 T2-102 `deleted=t`; re-adding "T2-102" on property 135 → 409 conflict toast;
no new row created. (Re-adding a never-used number, "T2-103", succeeds — confirming it's the soft-deleted
collision, not a portfolio/property scoping problem.)
**Code Reference:** `RentalCommand.Data/RentalCommandDbContext.cs:829`
`entity.HasIndex(e => new { e.PropertyId, e.UnitNumber }).IsUnique();` — not filtered, while the soft-delete
query filter is at `:830`.
**Suggested Fix:** Make it a partial unique index that matches the soft-delete filter:
`entity.HasIndex(e => new { e.PropertyId, e.UnitNumber }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");`
and add a migration. (Npgsql supports filtered indexes.)
**Why This Matters:** A landlord who fat-fingers a unit, deletes it, and re-adds it with the same label hits
a wall they can't diagnose — the unit they're "duplicating" doesn't exist on screen. Numbers like "101"/"A"
are reused constantly across turnovers.

### BUG-3: Duplicate unit number (and every DB-constraint hit) surfaces as an opaque "conflicts with existing data" toast
**Severity:** Medium
**Location:** Property detail → Add Unit (duplicate number on the same property).
**Expected:** A clear, specific message — e.g. "Unit number "T2-101" already exists on this property" —
ideally inline on the unit-number field, the way blank/invalid fields are surfaced.
**Actual:** Adding a second "T2-101" returns a toast: "The request conflicts with existing data or a
data-integrity rule." The dialog stays open (good) but gives no hint *which* field or *why*. A
non-technical landlord cannot act on "data-integrity rule."
**Evidence:** Snapshot of the toast after the second "T2-101" submit; DB confirms the duplicate was rejected
(still 2 units on property 135). Screenshot attempt captured the state in-session.
**Code Reference:** `RentalCommand.Api/Services/Domain/UnitService.cs:264` (`CreateAsync` does no
duplicate pre-check; relies on the DB unique index) → generic 409 from
`RentalCommand.Api/GlobalExceptionHandler.cs:85` (`isConstraintViolation` → "...conflicts with existing
data or a data-integrity rule.").
**Suggested Fix:** In `UnitService.CreateAsync` / `UpdateAsync`, pre-check for a live unit with the same
`UnitNumber` on the property and throw `new DomainValidationException("Unit number \"{n}\" already exists
on this property.", StatusCodes.Status409Conflict)` — mirroring the named messages the tenant/vendor delete
guards already use. (This also cleans up BUG-2's UX, since the pre-check only sees live units.)
**Why This Matters:** "The computer does the typing for you" is the product's promise; bouncing the user off
an unexplained data-integrity error is the opposite of that.

### BUG-4: Tenant delete is blocked only by `Active` leases, not `NoticeGiven` — a still-occupying tenant can be deleted and orphan their lease
**Severity:** Medium
**Location:** Tenant detail/list → Delete. `TenantService.DeleteAsync` + `getTenantDeleteState`.
(Code-derived — not browser-reproduced, to avoid mutating lease records owned by the Money tester.)
**Expected:** A `NoticeGiven` lease still occupies its unit (the unit health badge / dashboard treat it as
the *current* lease and label it "Move-Out"). Deleting that tenant should be blocked exactly like an `Active`
lease — the guard's own comment says its job is to stop a lease being "orphaned... invisible yet stays
Active/occupying."
**Actual:** The server guard checks only `l.Status == LeaseStatus.Active`, and the UI's `confirmDisabled`
uses `activeLeaseCount`, which also counts only `Active`. So a tenant whose sole lease is `NoticeGiven` shows
`activeLeaseCount = 0`, the delete is enabled, the server allows it, and the still-occupying lease is left
pointing at a soft-deleted tenant (it then inner-joins out of the UI through the tenant filter).
**Evidence:** Code paths:
- Guard predicate: `RentalCommand.Api/Services/Domain/TenantService.cs:253` (`l.Status == LeaseStatus.Active`).
- Active count (drives the UI block): `TenantService.cs:94` (`t.Leases.Count(l => l.Status == LeaseStatus.Active)`).
- Current-lease occupancy treats `NoticeGiven` as current: `UnitService.cs:132` (CurrentLeaseStatus selects
  `Active || NoticeGiven`) and `ComputeSimpleStage` → "Move-Out" at `UnitService.cs:232`.
**Code Reference:** `RentalCommand.Api/Services/Domain/TenantService.cs:253`.
**Suggested Fix:** Block on occupancy, not just `Active`:
`l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven` in the delete guard, and align
`ActiveLeaseCount` / `AvailableForLease` semantics if "active occupancy" is the intended meaning.
**Why This Matters:** A tenant who has given notice but hasn't moved out is exactly the kind of record a
landlord might "clean up" early — and doing so silently orphans a lease that still owes rent and holds a deposit.

## Potential Issues (need investigation)
- **Orphaned test rows left behind (from BUG-1):** Units 223 (T2-101) and 227 (T2-103) under deleted property
  135 are now live-but-inaccessible. They cannot be removed via the UI/API (the unit delete endpoint scopes
  through the deleted property and 404s), so they need a DB cleanup. I left them in place as BUG-1 evidence
  rather than hand-editing the DB.
- **BUG-4** is code-derived; a browser repro requires creating a `NoticeGiven` lease (Money tester's domain).

## Observations (work as built; could be better)
- **Occupancy is unit-status-driven, but there's no UI to set unit status.** "Occupied" everywhere counts
  `Unit.Status == Occupied` (consistent across list/detail, computed DB-side). But the structural Add/Edit
  Unit form (`UnitFields.svelte`) exposes only unit number / beds / baths / rent — no Status field. So a
  landlord doing pure structural setup sees every unit as "Vacant" and occupancy `0/N` until a lease move-in
  flips it. Yet the property Details help text says occupancy updates "as you... change the status of
  individual units" — a capability the structural UI doesn't offer. (`web/src/lib/components/forms/UnitFields.svelte`;
  help copy in `properties/[id]/+page.svelte:550`.)
- **Client/server validation parity.** `propertySchema` / `unitSchema`
  (`web/src/lib/schemas/index.ts`) enforce required + non-negative but NOT max-length or upper numeric
  bounds. A 251-char property name passes client validation then 400s server-side with a raw .NET message:
  "The field Name must be a string or array type with a maximum length of '200'." Handled cleanly (no 500,
  no record created) but less polished than the inline messages used elsewhere; same applies to unit number
  > 50, beds > 99, rent > 99,999,999.
- **The generic 409 copy** (BUG-3) is reused for every constraint conflict, so it will read the same for a
  duplicate unit number, a duplicate lease-tenant, etc.

## What Was Tested (repro steps)
1. **IDOR / portfolio scope (PASS):** Logged in as admin (Portfolio 1), navigated directly to Portfolio-7
   records — `/properties/43`, `/units/35`, `/tenants/55`, `/vendors/8` — all returned "not found / could not
   load." Work-order vendor picker listed exactly Portfolio 1's 8 active vendors (DB-cross-checked), no leakage.
2. **Property create + persist (PASS):** Created "QA-T2-0135 Test Property" (Columbus, OH 43215). DB row 135
   correct.
3. **Add units + counts (PASS):** Added T2-101 then T2-102 → property showed 0/1 then 0/2, "N units · 0%
   occupied"; counts reconcile with DB (correlated-subquery SQL, no N+1).
4. **Duplicate unit number (BUG-3):** Re-added "T2-101" → opaque 409 conflict toast; DB unchanged (2 units).
5. **Unit edit + persist (PASS):** Edited T2-101 to 2 beds / $1,650 → DB row 223 updated; survived reload.
6. **Unit cross-links + tabs (PASS):** "Open unit" → correct Unit T2-101; walked Overview/Lease/Applications/
   Rent/Maintenance/Documents/Expenses/Timeline — all loaded with correct empty states; Timeline showed the
   "Added unit" audit event. Only console error was the expected 409 from step 4.
7. **Unit delete (PASS) + soft-delete reuse (BUG-2):** Deleted T2-102 (DB `deleted=t`, removed from grid;
   property back to 0/1). Re-adding "T2-102" → 409; re-adding fresh "T2-103" → succeeds.
8. **Property delete (BUG-1):** With property 135 holding 2 live units, deleted it. DB: property `deleted=t`,
   units 223 & 227 still `deleted=f`; both invisible in Units list and 404 on direct nav.
9. **Tenant create + validation + persist (PASS):** Blank save → "First name/Last name is required";
   "not-an-email" → "Enter a valid email"; valid save created tenant 265; edited phone 0135→2222 and verified
   persistence via reload + DB.
10. **Vendor create + selectable + scoped (PASS):** Created vendor "QA-T2-0135 Plumbing Co" (id 64); appears in
    the work-order vendor picker; picker contents == Portfolio 1 vendors only.
11. **Edge — very long name (Observation):** 251-char property name → clean server 400 toast; no record created.

### Data created (all marked QA-T2-0135, Portfolio 1)
- Property 135 (deleted in BUG-1 test). Units 223 (T2-101) & 227 (T2-103) — **orphaned live rows, need DB cleanup**.
  Unit 224 (T2-102) — soft-deleted. Tenant 265 (QA-T2-0135 Renter) — live. Vendor 64 (QA-T2-0135 Plumbing Co) — live.
