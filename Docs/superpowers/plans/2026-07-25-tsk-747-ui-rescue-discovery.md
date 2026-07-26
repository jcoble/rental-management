# TSK-747 Web UI Rescue — Discovery Brief

**Goal:** Make Rental Command understandable to a non-technical landlord by restoring the Unit Command Center as the app's organizing hub, simplifying dense workflows, and using one consistent UI and plain-English product voice.

## Acceptance criteria

- Every `+page.svelte` route has an audit result covering visual hierarchy, wording, navigation, component consistency, progressive disclosure, help, and observable loading/empty/error states.
- The main navigation contains a clearly named Command Center doorway with a searchable unit picker, and unit-owned records return to the correct Unit Command Center tab and record.
- `/units` is an understandable way to choose a rental; `/units/[id]` is a calm hub rather than several full pages stacked into walls.
- Notification setup is a guided sequence with plain-English decisions, summaries, and contextual help instead of three unrelated walls of settings.
- Native browser form controls are replaced by the established app controls in changed surfaces.
- Existing business behavior, route contracts, authorization, API contracts, and DB-side query behavior remain unchanged.
- UI proof uses an explicit 1920×1080 browser viewport; critical responsive surfaces also receive a focused narrow-width check.

## Explicit non-goals

- No backend, database, authentication, billing, notification-delivery, or workflow-policy redesign.
- No speculative feature additions, new third-party dependencies, global rebrand, or broad mobile-app work.
- No implementation of unrelated findings discovered during the audit.
- No claim that a token-gated, role-gated, or unavailable-data state was visually reviewed when it was only inspected in source.

## Product decisions carried into discovery

1. A Unit is the durable context through vacancy, listing, application, occupancy, renewal, move-out, and turnover.
2. “Command Center” is a user-facing product term and must return as a top-level navigation doorway with a unit dropdown.
3. Cross-record navigation should fold a record back into its unit-owned tab when the unit relationship exists.
4. Labels must describe what the landlord is deciding or doing; internal model names and raw enum values are not acceptable product copy.
5. Long workflows use progressive disclosure: a short overview and next action first, then accordions, steppers, or detail drawers.
6. Contextual help links point to written Help pages; tooltips explain a field but do not replace documentation.

## Discovery questions

1. Which current routes, components, and link builders own the Unit Command Center and unit-folded navigation?
2. Which earlier Command Center implementation can be restored without bringing back unpaged or client-filtered data access?
3. Which page groups share form, select, disclosure, stepper, help, empty-state, and page-header components?
4. Where does unclear copy reflect real business distinctions that must not be simplified into an incorrect default?
5. Which live routes are reachable with the seeded admin, and which require a tenant, owner, technician, leasing, token, or super-admin context?
6. What is the smallest coherent set of changes that materially improves all three lanes without rewriting every screen?

## Bounded investigation

- Inspect: `web/src/routes`, `web/src/lib/components`, `web/src/lib/navigation`, relevant route-contract tests, current Unit Command Center specs, the July foundation blueprint, and the Azure preview.
- Commands allowed: `rg`, route/file inventory, read-only Git history, the AI-slop scanner, focused existing test listing, and real-browser navigation.
- Browser target: `https://rental-command.chimp-map.ts.net/` at an explicit 1920×1080 viewport.
- Do not: edit production code before the plan review, broaden into backend behavior, or run concurrent heavy builds/tests.

## Route ownership

### Lane 1 — Rental spine and Command Center (primary agent)

- Global shell and navigation.
- Dashboard, setup, property, unit, tenant, lease, application, leasing, owner, and lease-template surfaces.
- Cross-route link folding into unit tabs.
- Auth/public setup surfaces where the app establishes its mental model.

### Lane 2 — Work, communication, notification setup, and tenant portal (UI owner 2)

- Maintenance, inspections, recurring work, appointments, vendors, staff work/schedule/inbox.
- Messages, tenant notices, and the three notification-settings pages.
- Tenant portal pages and their relationship to staff terminology.

### Lane 3 — Money, intake, settings, reporting, help, and admin (UI owner 3)

- Accounting, banking, deposits, tax, reports, ledger entries.
- Scan/import/AI, profile/security/settings, Help/docs, admin/super-admin, and remaining public/token routes.

## Stop condition

Discovery ends when each lane returns:

- a checked route inventory with “browser reviewed”, “source-only”, or “blocked” evidence;
- the five highest-impact usability failures;
- exact source and test files for the smallest coherent fixes;
- proposed plain-English labels and help destinations;
- a no-overlap implementation boundary.

The primary agent then writes one scope-locked execution plan and sends all three lane plans to one read-only reviewer before any production-code change.

## Discovery evidence

- `web/src/lib/components/CommandCenterNav.svelte` currently renders one “Rentals” link and explicitly describes itself as legacy import compatibility, so the prior dropdown behavior is absent.
- `web/src/lib/components/AppShell.svelte` places Units inside the Rentals group and has no Command Center navigation entry.
- `web/src/routes/(protected)/units/[id]/+page.svelte` currently stacks multiple major sections inside the Leasing, Tenant & lease, Maintenance, and Documents & history tabs.
- The live 1920×1080 admin dashboard routes attention items to unit-aware URLs such as `/units/1?tab=money&tenantAccount=1`, proving the unit-folded routing convention still exists.
- The static slop scan inspected 782 frontend files and reported 920 leads across 17 groups; these are triage leads, not automatic changes.
