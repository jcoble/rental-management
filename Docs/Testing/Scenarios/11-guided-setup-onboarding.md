# Scenario 11 — Guided setup, onboarding wizard, and example-data choice

## Purpose

Prove that a first-time landlord can choose an empty or example-data experience, complete the guided setup journey, recover from partial progress, and return to a useful dashboard without half-created portfolio state.

## Preconditions and login

Use a fresh local development identity where possible, or a QA-prefixed additive portfolio in preview. The administrator may open the wizard for comparison, but do not reset or rewrite the shared preview portfolio. Record which mode (fresh or example data) is selected and keep each mode in a separate local identity or additive portfolio.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/onboarding/+page.svelte
- web/src/routes/(protected)/get-started/+page.svelte
- web/src/routes/choose-setup/+page.svelte and web/src/routes/choose-setup/+page.server.ts
- web/src/routes/setting-up/+page.svelte and web/src/routes/setting-up/+page.server.ts
- web/src/routes/welcome/+page.svelte and web/src/routes/welcome/+page.server.ts
- web/src/routes/(protected)/import/+page.svelte
- web/src/lib/onboarding/wizard-steps.ts and web/src/lib/onboarding/getting-started-tasks.ts
- web/src/lib/components/AppShell.svelte and web/src/routes/(protected)/+layout.server.ts
- RentalCommand.Api/Controllers/WorkspaceExperienceController.cs, PortfolioController.cs, PropertyController.cs, UnitController.cs, OwnerEntityController.cs, and ImportController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Compare fresh portfolio setup and example-data mode: inspect the copy, choice persistence, seeded records, and the point at which the normal landlord shell becomes available.
- Explore every wizard step and the getting-started task list; test back, close, refresh, direct URL entry, and browser navigation to verify progress and context survive.
- Use the wizard to name the business, identify an owner, add a property and unit, add a tenant, create a lease, and configure personal alerts; confirm each completion indicator reflects durable server state.
- Start scan/import entry points from setup and from the dashboard; verify unresolved context is requested once and an imported record returns to the correct portfolio/property/unit.
- Check onboarding guard behavior for users who abandon midway, finish in another tab, or already have a portfolio; no duplicate portfolio or owner rows should appear.

## Specific edge cases worth trying

- Empty required names, whitespace, Unicode/emoji business names, long addresses, invalid postal codes, unsupported country/state combinations, and duplicate property/unit labels.
- Back/forward across each step, reload during a save, double-submit, network failure after submit, and a stale wizard URL after completion.
- Choose example mode twice, change mode after data exists, open /onboarding with an existing active portfolio, and revisit a completed task.
- Use a browser at 390px, keyboard-only input, paste multiline address text, and a date at a daylight-saving boundary.

## What to verify visually

- Wizard progress, step labels, back/next affordances, focus/error placement, loading states, and a clear final completion state.
- Example-data warning and fresh-mode empty states make the data boundary obvious; cards and task counts do not imply work is complete when it is not.
- Dashboard handoff, breadcrumbs, contextual labels, and mobile buttons remain visible without clipped text or horizontal overflow.

## Data safety and evidence

Use QA-YYYYMMDD in business/property/unit/tenant text. Example-data mode may create seed rows locally; in preview it must be an additive QA portfolio and must never trigger a reset.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
