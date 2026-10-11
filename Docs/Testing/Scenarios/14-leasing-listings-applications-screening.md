# Scenario 14 — Leasing workspace, listings, applications, and screening

## Purpose

Cover the lead-to-approval surface: rental/listing presentation, leasing pipeline, showings/inbox entry points, application creation and review, screening status, and the transition into move-in preparation.

## Preconditions and login

Use a vacant QA unit and QA applicant. If screening provider access is unavailable, inspect the explicit unavailable state and do not invent provider credentials. Public application links must be additive and token-scoped.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/leasing/+page.svelte
- web/src/routes/(protected)/leasing/rentals/+page.svelte and web/src/routes/(protected)/leasing/[record]/[id]/+page.svelte
- web/src/routes/(protected)/leasing/pipeline/+page.svelte, web/src/routes/(protected)/leasing/calendar/+page.svelte, web/src/routes/(protected)/leasing/inbox/+page.svelte
- web/src/routes/(protected)/applications/+page.svelte and web/src/routes/(protected)/applications/[id]/+page.svelte
- web/src/routes/apply/[token]/+page.svelte
- web/src/lib/applications/application-unit-options.ts and web/src/lib/api/endpoints/applications.ts
- RentalCommand.Api/Controllers/LeasingWorkspaceController.cs, ApplicationsController.cs, PublicApplicationsController.cs, ScreeningWebhooksController.cs, PlacesController.cs, PropertyController.cs, and UnitController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Compare the leasing Today, rentals/listings, pipeline, calendar/showings, and inbox surfaces; verify filters, counts, status labels, and links all point to the same property/unit/application.
- Create or open a QA listing and inspect public-facing fields, availability, inquiry/application entry points, and any copy that distinguishes a draft listing from a published one.
- Create an application, reopen it globally and from its unit, review editable fields, run the supported screening request/status path, and inspect approval/decline/withdraw transitions.
- Use the application-to-prepare-move-in handoff; confirm unit, applicant, parties, dates, rent, deposit, and signer context are inherited and the unit is not marked occupied prematurely.
- Open a tokenized public apply link without a staff session and compare valid, expired, reused, and wrong-unit tokens.

## Specific edge cases worth trying

- Required applicant fields blank, invalid/long email and phone, Unicode names, duplicate application for one unit, concurrent submissions, and an application for an unavailable unit.
- Screening provider unavailable, webhook replay/out-of-order status, timeout, partial result, and manual status refresh.
- Illegal status transition, double approve/decline/withdraw, stale application after the unit is leased, and direct cross-portfolio application ID.
- Listing with no photos/description, long rent/availability text, date boundary at midnight, and mobile viewport with the apply CTA.

## What to verify visually

- Pipeline stages and badges are mutually understandable; counts, vacancy, screening status, and action availability agree.
- Application forms place inline errors next to fields, preserve values on back, and do not hide critical applicant or unit identity.
- Public listing/apply pages have clear landlord/applicant separation, loading/error/empty states, and no staff navigation leak.

## Data safety and evidence

Use QA-YYYYMMDD in applicant names, email aliases, listing copy, and screening references. Never submit a real applicant or call a paid screening provider in preview.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
