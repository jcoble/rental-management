# Exploratory Test Report: Guided setup, onboarding wizard, and example-data choice
Date: 2026-08-27  
Tester: e2e-s11  
Duration: ~35 minutes

## Scenario

Verify that a landlord can choose an example-data or empty experience, use and resume Guided Setup safely, and return to a useful dashboard without partial portfolio state.

## Summary

I exercised the supplied administrator's committed SANDBOX workspace through the dashboard, Guided Setup, Getting Started, setup guards, scan entry points, and the spreadsheet-import page. Core completion, checklist wording, inline required-field validation, property sub-step navigation, typed Go Live protection, and dashboard handoff worked as designed. Three bugs were found: a long portfolio name reaches a server 400 without inline validation, the sandbox choice reappears in a new browser despite a persisted server choice, and the completion “Import more records” action opens the property step instead of the spreadsheet importer.

## Bugs Found

### BUG-1: Validate overlong portfolio names before sending the save request
**Severity:** Medium  
**Location:** Guided Setup portfolio step, `/onboarding?step=portfolio`  
**Expected:** The server contract limits `Portfolio.Name` to 200 characters, so the wizard should reject an overlong name inline and leave the user on the form with a clear field-level message before making a request.  
**Actual:** Filling the portfolio name with 201 `x` characters and clicking “Save & continue” produced no inline destructive-field error, sent `PATCH /api/v1/portfolios/1`, returned HTTP 400, and emitted `Failed to load resource: the server responded with a status of 400 ()`. The existing portfolio remained unchanged after reload, but the user receives no field-specific correction path.  
**Evidence:** Browser run `09-onboarding-validation.js`: `PORTFOLIO LONG ERRORS []`, `HTTP 400 https://localhost:5667/api/v1/portfolios/1`, and `CONSOLE ERROR: Failed to load resource: the server responded with a status of 400 ()`; reload returned `"Default Portfolio"`. The final read-only database check still showed `Default Portfolio`.  
**Code Reference:** `web/src/lib/schemas/index.ts:552-560` (`settingsSchema` has no max length); `web/src/routes/(protected)/onboarding/+page.svelte:381-400` (the wizard validates with that schema and sends the update); `RentalCommand.Api/DTOs/PortfolioDtos.cs:95-104` (`UpdatePortfolioRequest.Name` is `[MaxLength(200)]`).  
**Suggested Fix:** Add the server-matching `.max(200, 'Portfolio name must be 200 characters or fewer')` constraint to `settingsSchema.name` (and the matching 200-character constraint to `managementCompanyName`) so the wizard stops locally with an inline message.  
**Why This Matters:** A non-technical landlord who pastes a long business name sees a failed save instead of being told what to correct, which makes setup feel unreliable and can lead to repeated submissions.

### BUG-2: Honor the persisted sandbox choice in every browser
**Severity:** High  
**Location:** Getting Started, `/get-started`  
**Expected:** Once the account's first-run choice is persisted as Sandbox, a later login from another browser or device should open the durable checklist. The destructive “Set up my real portfolio” choice should only be available while the server reports `onboardingChoicePending=true`.  
**Actual:** In the primary browser, clicking “Start exploring” changed the fork to the checklist and a reload kept the checklist. In a fresh second browser profile logged into the same account, `/get-started` showed the first-run “How do you want to start?” fork again, including “Set up my real portfolio,” even though the account was already a seeded Sandbox. The separate server-gated `/choose-setup` route correctly redirected to `/`, so this is specific to Getting Started's client-only choice check.  
**Evidence:** Secondary browser run `13b-get-started-secondary-browser-login.js`: `FORK ON NEW BROWSER 1 CHECKLIST 0`, with the visible “Clear the example data and start clean” copy. Read-only SQL returned `IsSandbox = t` and `onboarding_choice = sandbox` for portfolio 1. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s11-get-started-secondary-fork.png`.  
**Code Reference:** `web/src/routes/(protected)/get-started/+page.svelte:51-71` uses only the browser-local `rc.getStarted.choice` key when computing `showFork`; `web/src/routes/choose-setup/+page.server.ts:27-35` already demonstrates the server-side `onboardingChoicePending` guard that should be honored.  
**Suggested Fix:** Make `showFork` require `stateQuery.data?.onboardingChoicePending === true` in addition to the existing Sandbox and local-view conditions, defaulting to the durable checklist after the server reports a completed choice.  
**Why This Matters:** A landlord moving from a laptop to a phone can be shown an apparently fresh destructive choice and may intentionally confirm “Go Live” believing it is the normal next step, risking irreversible deletion of the example workspace and losing the expected checklist context.

### BUG-3: Send “Import more records” to the import center
**Severity:** Medium  
**Location:** Guided Setup completion screen, `/onboarding`  
**Expected:** The completion button labeled “Import more records,” with a spreadsheet icon, should open the spreadsheet import route at `/import` (or be labeled as a property-setup resume action if that is what it intends). The current setup metadata explicitly defines spreadsheet import as `/import`.  
**Actual:** With the seeded portfolio in the genuine “You're all set!” state, clicking “Import more records” navigated to `https://localhost:5667/onboarding?step=property` and rendered the property editor: “You already have 9 properties.” It did not open the spreadsheet importer.  
**Evidence:** Browser run `04-onboarding-finished-actions.js`: `BEFORE https://localhost:5667/onboarding You're all set!`; after the click, `AFTER IMPORT BUTTON URL https://localhost:5667/onboarding?step=property`. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s11-onboarding-import-button.png`.  
**Code Reference:** `web/src/routes/(protected)/onboarding/+page.svelte:1281-1287` (`openImportCenterFromFinished` selects the first incomplete core step or falls back to `property`); `web/src/routes/(protected)/onboarding/+page.svelte:1382-1385` gives that handler the “Import more records” label; `web/src/lib/onboarding/wizard-steps.ts:152-166` defines the spreadsheet import destination as `/import`.  
**Suggested Fix:** Change `openImportCenterFromFinished` to navigate directly to `/import` so the button opens the destination its label and icon promise.  
**Why This Matters:** The landlord has finished setup and is trying to bring in additional records; landing in a property editor is confusing and blocks the intended bulk-import workflow until they discover Settings or type the route another way.

## Potential Issues (need investigation)

- The safe CSV preview check did not reach `data-testid="import-preview"` within 15 seconds after selecting a QA-prefixed two-row tenant file. A second attempt through the visible drop-zone file-picker affordance did not produce a file chooser before the harness timeout. No HTTP error was captured and the database still had zero `ScanDrafts` and no QA tenant, so this is not classified as a confirmed bug; retest the `/import` upload path with backend request timing visible. Relevant client path: `web/src/routes/(protected)/import/+page.svelte:87-108`.
- A fresh live identity was not created because the supplied admin account was already a committed Sandbox and local registration requires email verification. No reset, Go Live action, or seeded data deletion was attempted.

## Observations

- The supplied account clearly identified itself as example data on both the dashboard and Guided Setup. The warning said configured email and text providers still work, which is an important safety notice for testing.
- The normal Guided Setup detection gate took about 2–3 seconds while loading the portfolio, owners, properties, tenants, leases, and alert signal queries, then correctly displayed “You're all set!” only when the property, tenant, and lease spine existed. No console errors appeared during the normal flows.
- In the same browser, “Start exploring” persisted the Getting Started choice across reload and showed a truthful “Exploring with sample data” heading rather than claiming the Sandbox was complete. All eight checklist tasks reflected the seeded server state as Done.
- The Go Live dialog was appropriately explicit. The confirm button was disabled with an empty or incorrect phrase, enabled only for `GO LIVE`, and cancel returned to the checklist. I did not click the destructive confirmation.
- Direct step URLs, property address/unit sub-step Back behavior, refresh, tab selection, and browser Back preserved a coherent wizard context. A 1710x990 completion screenshot had no horizontal overflow (`scrollWidth: 1710`), and the completion state was visually readable: `/home/blackcolours/Workbox/screenshots/e2e-s11-onboarding-complete-final.png`.
- Dashboard scan opened a global document-type chooser, `/scan` exposed both global capture and “New rental from your lease,” and `/scan/new-rental` rendered the lease capture entry point. The machine has no LLM extraction provider; no business record was confirmed from scan testing.
- Read-only end-of-run database snapshot: portfolio 1 remained `Default Portfolio`; counts were 3 owners, 9 properties, 24 tenants, 20 leases, and 0 scan drafts. These counts are a snapshot of the shared database, not a claim about other concurrent testers.

Browser cleanup: stopped e2e-s11

## What Was Tested

1. Logged in through the supplied local login flow with the seeded administrator credentials and verified the required `1710x990` headless viewport.
2. Inspected the dashboard banner, dashboard scan dialog, `/scan`, `/scan/new-rental`, and the new-rental capture UI.
3. Opened `/onboarding`, waited for existing-data detection, verified the genuine completion state, clicked the completion import action, and captured the resulting route.
4. Opened every wizard context by direct `step` URL, including portfolio, owner, property, lease, notifications, and the embedded lease-import step. Tested property sub-step Next/Back, direct tab navigation, reload, and browser Back.
5. Tested whitespace portfolio and owner names, a 201-character portfolio name, blank property name, and ZIP input masking without committing shared records.
6. Opened `/get-started`, selected “Start exploring,” verified the checklist and reload persistence, opened and canceled the typed Go Live dialog, and verified the fork reappears in a separate browser profile despite the server choice being Sandbox.
7. Verified `/choose-setup`, `/setting-up`, `/welcome`, and an invalid onboarding step URL do not replay the first-login choice for the existing account.
8. Attempted a QA-prefixed, dry-run spreadsheet tenant preview and a QA-prefixed scan upload; neither created a tenant, property, lease, or scan draft. No destructive action was taken.
9. Used SELECT-only PostgreSQL checks to confirm the portfolio choice and end-of-run record counts, then closed both headless browser profiles and verified no Chrome process remained for `e2e-s11`.
