# Scenario 13 — Properties, units, CRUD, filters, health, and unit tabs

## Purpose

Exercise the structural core of a portfolio: property and unit creation/editing, one-rental versus multiple-rental structure, filters and health summaries, contextual links, and every Unit Command Center tab with state restoration.

## Preconditions and login

Use a QA property and QA unit created locally or additively in preview. Prefer records created by this scenario; do not edit existing owner, tenant, lease, or maintenance records. Log in as an administrator or property-scoped manager to compare capability boundaries.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/properties/+page.svelte and web/src/routes/(protected)/properties/[id]/+page.svelte
- web/src/routes/(protected)/units/+page.svelte and web/src/routes/(protected)/units/[id]/+page.svelte
- web/src/lib/properties/property-labels.ts and web/src/lib/navigation/record-href.ts
- web/src/lib/components/data-grid/DataGrid.svelte and web/src/lib/utils/grid-url-state.svelte.ts
- RentalCommand.Api/Controllers/PropertyController.cs, UnitController.cs, OwnerEntityController.cs, UnitTurnoverController.cs, and PortfolioController.cs
- RentalCommand.Core/Entities/Property.cs, Unit.cs, and related property/unit configuration files

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Create both a One rental property and a Multiple rentals property; verify the intended unit invariant, address display, owner relationship, counts, vacancy/occupancy, and reopen persistence.
- Explore property and unit list search, status/type filters, sort, paging, health/attention indicators, empty states, and deep links from dashboard and owner surfaces.
- Traverse all available unit tabs or sections (overview, listing, applications, tenant/lease, ledger/money, turnover, work/maintenance, documents, timeline/history); confirm each remains scoped to the same unit.
- Edit safe fields, revisit the detail from the global list and property detail, and test browser Back/Forward while a tab/query state is active.
- Try contextual Add/Scan actions from a unit and global actions without context; the form should inherit property/unit exactly once and return to the originating record.

## Specific edge cases worth trying

- Blank required fields, duplicate unit labels within one property, duplicate addresses, invalid postal/state data, long names, emoji, and pasted multiline notes.
- Attempt an invalid RentalStructure change after units exist, delete/archive a referenced property or unit, and use a stale/deleted ID; expect a safe business error, not a partial mutation.
- Search case/diacritic differences, no-result filters, page-size boundary, sort direction, URL-encoded filter, and Back/Forward restoration.
- Probe a record ID from another portfolio through a direct route and picker; both read and write paths must deny it without leaking details.

## What to verify visually

- Property/unit identity, health badges, occupancy counts, tabs, and contextual breadcrumbs agree across list and detail views.
- Tables remain usable on 390px with horizontal overflow where needed; filters, clear buttons, pagination, skeletons, empty states, and errors are aligned.
- Tab selection and scroll/query state restore after reload and Back/Forward without stale data from a previous unit.

## Data safety and evidence

Name created property/unit/address/notes QA-YYYYMMDD. Never delete or archive preview records; use local cleanup only in a disposable local database.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
