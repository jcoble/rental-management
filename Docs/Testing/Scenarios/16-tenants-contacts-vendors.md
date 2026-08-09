# Scenario 16 — Tenants, contacts, owners, and vendors

## Purpose

Exercise the people and contact records that connect rentals to work and money: tenant CRUD and household relationships, contact details, owner/vendor records, pickers, scoping, and cross-links.

## Preconditions and login

Use QA-prefixed tenant, owner, and vendor records tied to a QA property/unit. Log in as administrator, then compare a scoped manager or relationship identity if available. Keep existing preview records read-only.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/tenants/+page.svelte and web/src/routes/(protected)/tenants/[id]/+page.svelte
- web/src/routes/(protected)/vendors/+page.svelte and web/src/routes/(protected)/vendors/[id]/+page.svelte
- web/src/routes/(protected)/owners/+page.svelte and web/src/routes/(protected)/owners/[id]/+page.svelte
- web/src/routes/(protected)/properties/[id]/+page.svelte and web/src/routes/(protected)/units/[id]/+page.svelte
- web/src/lib/api/endpoints/tenants.ts, web/src/lib/api/endpoints/vendors.ts, web/src/lib/api/endpoints/owners.ts, and web/src/lib/api/endpoints/properties.ts
- RentalCommand.Api/Controllers/TenantController.cs, RentalCommand.Api/Controllers/VendorController.cs, RentalCommand.Api/Controllers/OwnerEntityController.cs, RentalCommand.Api/Controllers/PropertyController.cs, RentalCommand.Api/Controllers/UnitController.cs, and RentalCommand.Api/Controllers/TeamController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Create, edit, search, and reopen tenants; inspect household/occupant/contact fields, portal-link affordances, lease/unit links, and activity/history.
- Create a vendor with contact and service details, select it from maintenance/expense flows, and verify the same vendor identity and scope in detail.
- Create or open an owner and follow property/statement/approval links; compare owner relationship visibility with staff management visibility.
- Test list and detail cross-links from property, unit, lease, work order, expense, owner, and portal surfaces; every link should preserve the correct record and portfolio.
- Compare role-scoped pickers and direct URLs for administrators, managers, owners, technicians, and tenants.

## Specific edge cases worth trying

- Blank/whitespace names, invalid/long email and phone, Unicode/emoji, duplicate contact, duplicate vendor, and a vendor with no service category.
- Multiple tenants/occupants in one unit, tenant with no lease, owner with multiple properties, inactive/archived contact, and a referenced record edit.
- Cross-portfolio IDs in list queries, picker search, and mutation payloads; no IDOR or confusing generic success.
- Long notes/addresses, no records, 1/2/many page boundaries, sort/filter persistence, and mobile table cards.

## What to verify visually

- Person/company identity, badges, contact actions, relationship counts, and breadcrumbs stay consistent between list/detail/contextual panels.
- Empty, loading, and error states explain what can be done next; long contact values truncate without hiding the full accessible value.
- Forms show required/error text at the field, save state, and success confirmation without losing the user’s return context.

## Data safety and evidence

Prefix all new names, emails, phone notes, vendor descriptions, and references with QA-YYYYMMDD. Do not deactivate/delete shared preview contacts.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
