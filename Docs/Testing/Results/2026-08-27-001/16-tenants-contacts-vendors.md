# Exploratory Test Report: Tenants, contacts, owners, and vendors
Date: 2026-08-27
Tester: e2e-s16
Duration: ~60 minutes

## Scenario
Scenario 16 — exercise tenant/owner/vendor CRUD, contact fields, search, pagination, pickers, cross-links, scoping and record history in the seeded SANDBOX portfolio.

## Summary
Created and edited QA-prefixed tenant, vendor and owner records through the real UI and proved persistence against the database. Creation, stepper validation, search (including Unicode/emoji), server-side paging, URL state persistence, delete blocking and the vendor picker in the repair form all behave correctly. Three real defects surfaced: **clearing any optional contact field is silently ignored across tenants, vendors and owners** (the UI reports success and the old value stays in the database); **a tenant who is not a party on a lease has a permanently empty "History" panel** because the audit authorization rule has no all-properties fallback for tenants (owners do have one); and **the owners form has no client-side length limits and no field-level mapping of server validation errors**, so a too-long Tax ID produces a raw ASP.NET framework message in a toast on a different step. A vendor's website is stored but never displayed anywhere.

## Bugs Found

### BUG-1: Clearing an optional contact field silently does nothing (tenant, vendor, owner)
**Severity:** High
**Location:** `/tenants` edit dialog, `/tenants/[id]` inline edit, `/vendors` edit dialog, `/owners` edit dialog
**Expected:** Blanking Email / Phone / Emergency contact / Website and saving should remove the value. The tenant update contract even has explicit clear flags (`UpdateTenantRequest.ClearEmail/ClearPhone/ClearEmergencyContact`, `RentalCommand.Api/DTOs/TenantDtos.cs:135,140,145`) precisely so a null can be distinguished from "leave unchanged".
**Actual:** The web forms send `"email": null` (Zod `optionalText`/`optionalEmail` transform `''` → `null`) and never send `clearEmail`. The server treats `null` as "no change", so the old value survives. The UI still shows "Tenant updated." / "Vendor updated." and the mutation succeeds, so the landlord believes the value was removed. `UpdatedAt` and an audit row are written, making it look like a real edit happened.
**Evidence:**
- Tenant 23 — PATCH body captured in-browser: `{"portfolioId":1,"firstName":"QA-20260827-s16 Zoë🏠","lastName":"O'Brien-Tester","email":null,"phone":null,"emergencyContact":null}` → toast "Tenant updated."; after reload the detail page still shows the email, phone and emergency contact. DB: `select "Email","Phone","EmergencyContact" from "Tenants" where "Id"=23;` → all three unchanged, `UpdatedAt` bumped to 2026-08-27 08:43:37Z.
- Vendor 7 — PATCH body `{... "email":null,"phone":null,"website":null ...}` → toast "Vendor updated."; DB: `select "Email","Website" from "Vendors" where "Id"=7;` → `qa.20260827.s16.vendor@example.com`, `https://qa-s16.example.com/very/long/path` (unchanged).
- Screenshot: `~/Workbox/screenshots/e2e-s16-tenant-detail-history-empty.png` (values still present after the "successful" clear).
**Code Reference:**
- `RentalCommand.Api/Services/Domain/PropertyTenantCrudRule.cs:692-697` (`if (update.ClearEmail) … else if (update.Email is not null) …`)
- `RentalCommand.Api/Services/Domain/VendorService.cs:181-183` and `RentalCommand.Api/Services/Domain/OwnerEntityService.cs:131-137` (no clear flags exist at all for vendors/owners)
- Client side: `web/src/routes/(protected)/tenants/[id]/+page.svelte:104` and `web/src/routes/(protected)/tenants/+page.svelte:210` (send `result.data` with nulls, no clear flags); `web/src/lib/schemas/index.ts:15-20` (`optionalText`: `'' → null`).
**Suggested Fix:** Make the update rules treat an explicit JSON `null` as "clear". Bind the update DTOs' optional strings through a tri-state that distinguishes *absent* from *null* (e.g. `JsonElement`/`Optional<T>` or `[JsonPropertyName]` + `ModelState` presence check) and assign `entity.X = update.X` whenever the property was present in the payload; then delete the now-redundant `Clear*` booleans. One rule change covers tenants, vendors and owners.
**Why This Matters:** A landlord who deletes a wrong phone number or a stale email is told it worked. Notices, W-9 texts and rent reminders keep going to the address they thought they removed — wrong-recipient communication and an audit trail that claims a change that never happened.

### BUG-2: A tenant who is not on a lease has a permanently empty History panel
**Severity:** Medium
**Location:** `/tenants/[id]` → "History" card
**Expected:** The per-record history reads `GET /api/v1/audit?entityType=Tenant&entityId=<id>` and should show at least "Added tenant" plus every edit, the same way `/owners/[id]` does for a brand-new owner with no properties.
**Actual:** For tenant 23 (created in the UI, then edited twice — audit rows 494 and 495 exist in `AtomicAuditLogs`), the API returns `[]` and the panel reads "No history recorded yet." Tenant 1 (a lease party) returns its audit row normally. The cause is the tenant branch of the audit authorization predicate: a Tenant audit row is visible only when a `LeaseManagementParties` row ties that tenant to an authorized property. The OwnerEntity branch has an `allPropertiesAssignments.Any()` fallback; the Tenant branch has none, so a prospective, applicant, or between-leases tenant has no visible history even for a portfolio administrator.
**Evidence:** In-browser response capture: `GET /audit?take=50&sort=-timestamp&entityType=Tenant&entityId=23 => []` versus `…entityId=1 => [{"id":95,…"description":"Added tenant"…}]`. DB: `select "Id","EntityType","EntityId","ChangeReason" from "AtomicAuditLogs" where "EntityType"='Tenant';` → rows 494/495 for entity 23 exist. Screenshot `~/Workbox/screenshots/e2e-s16-tenant-detail-history-empty.png`.
**Code Reference:** `RentalCommand.Api/Services/Domain/AuditAuthorizationQuery.cs:54-58` (Tenant branch) — compare `:38-49` (OwnerEntity branch, which includes `allPropertiesAssignments.Any() ||`).
**Suggested Fix:** Add the same `allPropertiesAssignments.Any() ||` fallback to the Tenant branch, guarded by `db.Tenants.Any(t => t.PortfolioId == scope.PortfolioId && t.Id == audit.EntityId)`, so a caller with portfolio-wide `reports.read` sees a lease-less tenant's own trail while property-scoped managers keep the lease-party restriction.
**Why This Matters:** "Who changed this tenant's phone number and when" is unanswerable for exactly the records most likely to be edited by hand — new residents, applicants, and former tenants. Combined with BUG-1 the landlord has neither the change nor a record of the attempt.

### BUG-3: Owner form has no length limits and shows raw framework validation text in a toast
**Severity:** Medium
**Location:** `/owners` → Add Owner (Identity step: Tax ID; also address/phone fields)
**Expected:** The client should bound Tax ID to the server's `[MaxLength(50)]` the way vendors/tenants do (`optionalTextMax`), and any server `ValidationProblemDetails` should be mapped to the offending field and the offending step, as `formErrorsFromApiError` already does on the tenants and vendors pages.
**Actual:** `ownerSchema` uses unbounded `optionalText` for `taxId`, `addressLine1/2`, `city`, `state`, `postalCode`, `phone`, so a 76-character Tax ID passes client validation. The POST returns 400 `{"errors":{"TaxId":["The field TaxId must be a string or array type with a maximum length of '50'."]}}` and the page surfaces that raw ASP.NET string as a toast while the user is on the *Contact* step, with no error next to the Tax ID field and no step navigation. The dialog stays open with no indication of what to fix.
**Evidence:** Captured request/response in-browser (POST `/api/v1/owner-entities`, 400, message above); dialog field-error nodes queried after the failure: `[]`.
**Code Reference:** `web/src/lib/schemas/index.ts:514-526` (`ownerSchema`, unbounded optional fields) and `web/src/routes/(protected)/owners/+page.svelte:103` (`onError: (err) => showError(apiErrorMessage(err))` — never calls `formErrorsFromApiError`, unlike `web/src/routes/(protected)/tenants/+page.svelte:100-112`).
**Suggested Fix:** Give `ownerSchema` the server's bounds via `optionalTextMax` (taxId 50, address 250, city 120, state 60, postalCode 20, phone 50) and wire the owners save `onError` through `formErrorsFromApiError` + `firstOwnerErrorStep` exactly as the vendors page does.
**Why This Matters:** The landlord sees a developer sentence about "string or array type" and cannot tell which of three steps holds the bad value; the natural next action is to abandon the owner record.

### BUG-4: A vendor's website is stored but never displayed
**Severity:** Low
**Location:** `/vendors` list and `/vendors/[id]` detail
**Expected:** A field the create/edit form collects and validates as a URL (`vendor-website-input`, `optionalUrlMax('Website', 500)`) should be visible on the record — ideally as a clickable link.
**Actual:** Website is saved (DB `Vendors.Website = https://qa-s16.example.com/very/long/path` for vendor 7) but the detail card renders only Service type, Phone, Email, Address, 1099, W-9 and Tax ID, and the list's Contact column shows only email · phone. There is no way to read back what was typed.
**Evidence:** Screenshot `~/Workbox/screenshots/e2e-s16-vendor-detail-no-website.png`; DB row above.
**Code Reference:** `web/src/routes/(protected)/vendors/[id]/+page.svelte` — the `vendor-detail-card` block (fields `vendor-detail-service-type`, `-phone`, `-email`, `-address`, `-1099`, `-w9`, `-taxid`; no website entry). Form field at `web/src/lib/components/forms/VendorFields.svelte:97`.
**Suggested Fix:** Add a "Website" row to the vendor detail card rendering `vendor.website` as an `<a href>` with `rel="noopener"`, alongside the existing phone/email rows.
**Why This Matters:** Small, but it is data entry the landlord did that the app then hides — the kind of thing that erodes trust in "the computer does the typing for you".

## Potential Issues (need investigation)
- **Duplicate tenants are created silently.** Saving a second tenant with the identical first name, last name and email produced a second record (ids 23 and 24, same email) with a plain "Tenant created." No warning, no near-duplicate check. The client idempotency key is a fresh UUID per successful mutation (`web/src/lib/api/idempotency.ts:8`), so this is by design at the write layer; whether the *product* should warn on an exact name+email match is a product decision. For a landlord this is how one resident ends up with two ledgers.
- **Vendor service type is pre-filled with "Plumbing"** (`web/src/routes/(protected)/vendors/+page.svelte:66`). "A vendor with no service category" is unreachable through the UI, and a landlord who tabs past step 1 saves a roofer as a plumber. The required-field validation only fires if the value is manually cleared.
- **`/vendors/999999` renders "Failed to load vendor."** with no Retry and no way back, while `/tenants/999999` and `/owners/999999` render "Could not load … / not found" with a Retry action and `/tenants/abc` offers "Back to Tenants" (`web/src/routes/(protected)/vendors/[id]/+page.svelte:164`). Inconsistent dead end rather than a functional defect. The page also fires `GET /vendors/999999/scorecard` for a vendor it already knows is missing.

## Observations
- Tenant search is solid: server-side, token-based, case-insensitive, and correct for Unicode and emoji (`Zoë`, `zoë🏠`, `O'BRIEN`, a full email address all matched tenant 23). Punctuation is stripped into tokens, which incidentally neutralizes `%`/`_` LIKE injection. A symbols-only query (`@@@`) correctly yields the "No tenants match your search" empty state with an "Add tenant" action.
- Paging is genuinely server-side (`/tenants/page?skip&take`), the row count reconciles (`21–23 of 23` after creating two tenants), and `?q=`, `?sort=`, `?page=` survive a full reload.
- Delete blocking is correct and well worded: for tenant 1 the confirm button is `disabled` with "This tenant is a current resident in an occupied rental; return possession or change the household first."
- The W-9 text request on a phone-less vendor returns a 400 with a genuinely helpful sentence ("This vendor has no phone number on file. Add a phone number, then request the W-9."). It would be better as a disabled button with a hint than as an error after the click.
- The vendor picker in the repair form lists every vendor with its service type, including the newly created QA vendor, and preserves identity.
- Owner detail (`/owners/[id]`) has no properties, statements, or approvals section — an owner that holds properties (Maple Ridge Properties LLC, id 1) shows only Details / Address / Record / Documents / History, so there is no path from the owner to the properties they own or to their statement. Reported as an observation rather than a bug because no code path claims to render one.
- No console errors during any successful flow; the only console errors observed were the expected 400/404 fetch failures.

## What Was Tested
1. Logged in at `https://localhost:5667/login` as `admin@rentalcommand.local`, viewport verified at 1710x990.
2. `/tenants`: New Tenant stepper — empty Next (both required errors fire), whitespace-only names (correctly rejected), Unicode/emoji name accepted, invalid email rejected at the field on Save, then created tenant "QA-20260827-s16 Zoë🏠 O'Brien-Tester" (id 23) with email, punctuated phone and a long emergency contact; verified the row in Postgres.
3. Search: `Zoë`, `QA-20260827-s16`, `zoë🏠`, full email, `O'BRIEN`, `@@@`; checked `?q=` in the URL and the filtered empty state.
4. `/tenants/23`: verified detail fields, Active Leases = 0, "No leases found for this tenant", Documents empty state, and the empty History panel; compared with `/tenants/1` whose History renders.
5. Inline edit on `/tenants/23`: blanked email, phone and emergency contact, captured the PATCH body, confirmed the success toast, reloaded, and confirmed in Postgres that nothing was cleared.
6. `/vendors`: create stepper — blank Basics blocked, invalid Website rejected, created vendor "QA-20260827-s16 Vendor Co. ☂" (id 7); `/vendors/7` detail and scorecard; "Text W-9 request" with no phone on file (400 + message); edit dialog blanking email and website (silently ignored, verified in Postgres).
7. `/owners`: create with a 76-character Tax ID (server 400 with raw framework text in a toast, no field error), then created "QA-20260827-s16 Holdings LLC" (id 3); checked `/owners/3` and `/owners/1` detail and History.
8. Cross-links and error states: `/owners/1`, `/tenants/999999`, `/vendors/999999`, `/owners/999999`, `/tenants/abc`.
9. Paging and state: tenants page 2 via Next, full reload with `?page=2`, sort by Name (`?sort=name`).
10. Delete blocking: `/tenants/1` delete confirm (button disabled, explanatory message).
11. Picker check: `/maintenance` → Report a repair → More details → vendor picker lists all seven vendors with service types.

Records created (all QA-prefixed, none deleted, no shared preview record deactivated): Tenants 23 and 24, Vendor 7, Owner 3.

Note on tooling: `playwright-cli` is not installed on this Linux workbox, so the session was driven with the repository's own Playwright (`playwright-core` 1.60.0) through a persistent headless Chromium profile named `udd-e2e-s16`, viewport pinned and verified at 1710x990. TLS verification stayed on — the two mkcert leaf certificates were pinned by SPKI hash rather than disabling certificate checks.
