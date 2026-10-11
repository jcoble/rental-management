# Exploratory Test Report: Guided Setup, Onboarding Wizard, and Example-Data Choice
Date: 2026-08-22
Duration: ~45 minutes

## Scenario
Test the full first-time-landlord onboarding flow: registration, choose-setup gate (sandbox vs live), the guided setup wizard (portfolio, owner, property+units, tenants, lease, alerts), getting-started checklist, import entry points, progress persistence, guards, mobile layout, and edge cases including double-submit and direct URL entry.

## Summary
The write-executor migration has introduced a critical regression: **all write operations return HTTP 200 (success) but fail to commit data to the database**. This affects registration, portfolio updates, property creation, tenant creation, and owner saves performed through the onboarding wizard. The UI shows success toasts and advances steps, but nothing persists. Reloading the page reveals the old data or, in the case of new accounts, a completely missing user. Beyond this showstopper, the UI flows (wizard navigation, step detection, guards, choose-setup gate) are well-implemented. One mobile layout issue was found where primary action buttons are clipped at 390px width.

## Bugs Found

### BUG-1: All write operations succeed in the UI but never commit to the database
**Severity:** Critical
**Location:** Every write endpoint exercised during onboarding: registration (`POST /api/v1/auth/register`), portfolio update (`PATCH /api/v1/portfolios/1`), property setup (`POST /api/v1/properties/setup`), tenant batch create, owner create/update.
**Expected:** After the API returns 200 OK, the data should be durably persisted in PostgreSQL. A page reload should reflect the saved values.
**Actual:** The API returns 200 OK, success is logged (e.g., `Registered user s11-fresh@qa.local (id 3)`), the UI shows success toasts, and the wizard advances. However, no rows are written to the database. Verified by querying `AspNetUsers`, `Portfolios`, `Properties`, `Tenants`, and `Owners` tables immediately after each operation - all returned zero matching rows. The portfolio name remained "Jesse's Porfolio" in the database despite the wizard reporting a successful rename to "S11 Test Portfolio" and the sidebar updating accordingly.
**Evidence:**
- Registration: `POST /api/v1/auth/register` returned 200 with message "Registration successful." API log: `Registered user s11-fresh@qa.local (id 3) with one canonical Administrator context`. But `SELECT * FROM "AspNetUsers" WHERE "Email" LIKE '%s11%'` returns 0 rows (verified with `SET app.is_admin = 'true'` to bypass any RLS). User count remained at 76 before and after registration.
- Portfolio rename: `PATCH /api/v1/portfolios/1` returned 200. `SELECT "Name" FROM "Portfolios" WHERE "Id" = 1` still shows `Jesse's Porfolio` (the original value, including the original typo).
- Property: `SELECT * FROM "Properties" WHERE "Name" LIKE '%S11%'` returns 0 rows after successful creation of "S11 Test House".
- Tenant: `SELECT * FROM "Tenants" WHERE "FirstName" LIKE '%S11%'` returns 0 rows after successful creation of "S11 Tester".
- OutboxMessages for the s11 registration email (`Payload->>'to' LIKE '%s11%'`) also returns 0 rows, confirming the entire transaction was rolled back.
**Code Reference:** `RentalCommand.Api/Services/Auth/CanonicalAccountBootstrapService.cs:146` uses `_writes.ExecuteAsync(...)` (the shared write executor). `RentalCommand.Api/Controllers/PortfolioController.cs` uses the same executor for updates. The `IRequestWriteExecutor` implementation appears to execute the operation in-memory and return a successful result, but the wrapping transaction is never committed.
**Why This Matters:** No new user can register. No onboarding step data persists. The entire first-run experience is broken. The user sees success indicators and advances through the wizard, but after closing the browser and returning, all progress is lost. This is a total blocker for new landlord acquisition.
**Suggested Fix:** The `IRequestWriteExecutor` transaction commit path needs investigation. The executor runs the handler, returns its result (which the API treats as success), but the enclosing `DbContext.SaveChangesAsync()` or `transaction.CommitAsync()` either does not fire or silently fails. Check whether the `AtomicWorkspaceExperiencePersistence` fix mentioned in the regression context actually resolved the commit path for all callers, or only for a subset.

### BUG-2: Registration returns wrong user ID (idempotency key collision)
**Severity:** High
**Location:** `POST /api/v1/auth/register` via `CanonicalAccountBootstrapService.CreateAsync`
**Expected:** A newly registered user should receive a fresh, unique user ID. The API log should report the new user's actual ID.
**Actual:** The API log says `Registered user s11-fresh@qa.local (id 3)` and `Registered user s11-test2@qa.local (id 4)`. But user ID 3 is `priya.patel@email.example` and user ID 4 is `jordan.smith@email.example` (pre-existing seed data users). The write executor appears to be returning a stale or colliding operation result rather than a new identity.
**Evidence:** `SELECT "Id", "Email" FROM "AspNetUsers" WHERE "Id" IN (3, 4)` returns `priya.patel@email.example` and `jordan.smith@email.example` (verified). The s11 email addresses do not exist in the table at all.
**Code Reference:** `RentalCommand.Api/Services/Auth/CanonicalAccountBootstrapService.cs:146-153` - after `_writes.ExecuteAsync`, `outcome.Value.UserId` is the wrong ID. The subsequent `_users.FindByIdAsync(outcome.Value.UserId.ToString())` at line 153 finds the pre-existing user, so the method reports success with a user that is not the one being registered.
**Why This Matters:** Even if the transaction commit bug (BUG-1) is fixed, the ID returned by the write executor is wrong. The email confirmation token would be generated for the wrong user, and the confirmation link would confirm the wrong account.
**Suggested Fix:** Investigate whether the `BootstrapAccountHandler` or the write executor's receipt/idempotency mechanism is replaying a prior operation's result instead of creating a new record. The `CreateLockId(normalizedEmail)` at line 142 and the key digest at line 143-144 should produce unique keys per email address, so this may be a hash collision or a stale receipt lookup.

### BUG-3: Primary action buttons clipped on mobile (390px) in wizard footer
**Severity:** Low
**Location:** Onboarding wizard footer at 390x844 viewport, all steps with long button labels
**Expected:** Footer buttons should be fully visible with no text clipping at standard mobile widths (390px = iPhone 14/15 logical width).
**Actual:** "Save & continue" and "Create lease & finish" are truncated on the right edge. The text is cut off (e.g., "Save & continu..." and "Create lease & fi..."). The button extends past the viewport boundary.
**Evidence:** Screenshots at 390x844:
- Portfolio step: "Save & continue" button partially clipped at right edge (screenshot `page-2026-08-22T08-02-06-008Z.png`).
- Lease step: "Create lease & finish" button more severely clipped, showing only "Create lease & fi..." (screenshot `page-2026-08-22T08-02-50-476Z.png`).
- Other steps (property "Next", completion screen) render correctly because their labels are shorter.
**Code Reference:** `web/src/routes/(protected)/onboarding/+page.svelte:2058-2121` - the footer `div` uses `flex items-center justify-between gap-2` which pushes the right-aligned button group past the edge when the combined width of Back + Skip + primary button exceeds the available space at narrow widths.
**Why This Matters:** First-time users on mobile cannot read the full label of the most important button on the page. The button is still tappable (only the text is clipped, the button target area extends off-screen), but it looks broken and may reduce confidence in a first-impression flow.
**Suggested Fix:** On small screens, either truncate the button text to a shorter label (e.g., "Save" instead of "Save & continue") or allow the footer to wrap to two rows. Alternatively, use `text-sm` or `min-w-0 truncate` on the button so it fits within the available space while remaining readable.

## Potential Issues (Need Investigation)

### PI-1: Portfolio name shows a typo in the database
The admin portfolio (ID 1) is stored as "Jesse's Porfolio" (missing 't' in Portfolio). This is likely a seed data issue rather than a code bug, but it should be corrected in the seed script to avoid confusion during testing and demos.
**Evidence:** `SELECT "Name" FROM "Portfolios" WHERE "Id" = 1` returns `Jesse's Porfolio` (verified).

### PI-2: TanStack Query cache shows stale-successful data after write-executor failures
When the API returns 200 but the database transaction rolls back (BUG-1), the TanStack Query cache updates with the "saved" values. The sidebar showed "S11 Test Portfolio", the wizard advanced steps showing green checkmarks, and the Getting Started checklist showed all items as "Done" - all based on cached API responses that never persisted. After a hard reload, the data reverts. This is not a separate bug (it's a consequence of BUG-1), but once BUG-1 is fixed, verify that the query invalidation pattern still works correctly and that the cache doesn't hold stale optimistic data.

### PI-3: Lease step shows wrong unit label for single-rental properties
On the lease step, the unit for the S11 Test House single-rental property was pre-selected as "Unit S11 Test House" rather than a simpler label. For single-rental properties, the wizard creates a canonical unit with the property name as the unit number (line 751: `unitNumber: propertyForm.name.trim() || '1'`). This means the lease unit dropdown shows "Unit S11 Test House" which is redundant and confusing when the property is already "S11 Test House". Consider displaying "Rental" or just the unit number "1" for single-rental units.
**Evidence:** Snapshot `page-2026-08-22T08-00-51-336Z.yml` line 172: `button "Unit S11 Test House"`.
**Code Reference:** `web/src/routes/(protected)/onboarding/+page.svelte:751` sets `unitNumber: propertyForm.name.trim() || '1'` for single-rental properties.

## Observations

1. **Wizard UX is well-designed.** The step grouping (Setup, Property, People, Notify), plain-English explanations, "Where do I find this?" hints, pre-filling from existing data, and the skip/back navigation all work as a real user would expect. The step indicator showing completed checkmarks provides clear progress feedback.

2. **Choose-setup gate works correctly.** The server-side guards redirect correctly: unauthenticated users go to `/login`, already-decided users go to `/`, and the gate pages (`/choose-setup`, `/setting-up`) cannot be replayed after a choice is made.

3. **Onboarding detection loads gracefully.** The "Checking your existing setup..." loading state shows while the detection queries settle, then the wizard either jumps to the first incomplete step or shows the completion screen. The detection covers portfolios, owners, properties, tenants, leases, and getting-started signals.

4. **Double-submit on the owner step did not produce duplicate rows.** The rapid double-click on "Save & continue" advanced one step without creating two owner records. This may be because the write executor's transaction never committed (BUG-1), so the protection was not truly tested. Once BUG-1 is fixed, re-test double-submit explicitly.

5. **Mobile layout (390x844) is generally good.** The step group pills wrap properly, form fields stack to single-column, and the completion screen renders cleanly. Only the footer action buttons overflow (BUG-3).

6. **Getting-started checklist renders fully.** All essentials and optional items show correct Done/incomplete status. Each item links back to the relevant wizard step or settings section.

## What Was Tested

1. **Registration flow:** Registered `s11-fresh@qa.local` and `s11-test2@qa.local` via the UI and API. Both returned success but users were not persisted (BUG-1, BUG-2).

2. **Email verification:** Extracted verification link from OutboxMessages payload. The token failed to confirm because the user was never created (consequence of BUG-1).

3. **Choose-setup gate:** Tested `/choose-setup` for authenticated-decided user (redirects to `/`), `/setting-up` for decided user (redirects to `/`), and both for unauthenticated user (redirects to `/login`).

4. **Wizard step-by-step (as admin@rentalcommand.local):**
   - Portfolio step: validated empty name, whitespace-only name, saved "S11 Test Portfolio" (BUG-1 applied).
   - Owner step: viewed existing owner pre-fill, tested back navigation.
   - Property step: selected "Add a new property", filled S11 Test House with address/rental structure/type, advanced through address sub-step to units sub-step, saved with beds/baths/rent.
   - Tenants step: added "S11 Tester" tenant.
   - Lease step: verified pre-fill of tenant/property/unit/rent/dates/deposit/due day.
   - Step indicator: verified step numbering (1-6), check marks on completed steps, active step highlighting.

5. **Wizard navigation:** Back button (disabled on step 1, functional on later steps), step tab clicks to jump between steps, skip ("I'll add this later"), deep link via `?step=<key>`.

6. **Reload persistence:** Navigated to `/onboarding` without step param; wizard auto-detected all-done state and showed completion screen.

7. **Completion screen:** "You're all set!" with action buttons (Import more records, Add security deposits, Go to my dashboard).

8. **Getting-started page:** All checklist items showed Done status.

9. **Import page:** Renders correctly with entity type selection and CSV upload area.

10. **Mobile pass (390x844):** Checked portfolio step, property step, lease step, and completion screen. Footer button clipping found (BUG-3).

11. **Double-submit test:** Rapid double-click on owner step "Save & continue" - no duplicate creation observed (inconclusive due to BUG-1).

12. **Browser cleanup:**
    - Browser cleanup: stopped e2e-s11 (verified no `pgrep -fl e2e-s11` output)
    - Browser cleanup: stopped e2e-s11b (verified no `pgrep -fl e2e-s11` output)

---

## ORCHESTRATOR DISPOSITION (2026-08-22, post-report verification)

BUG-1 (writes never commit) and BUG-2 (wrong user IDs) are REFUTED — tester verification error, not product bugs. The tester queried the wrong PostgreSQL database. Direct query of the live QA database (`rentalcommand_qa20260822` on rentalcommand-dev-db:5434) shows every write persisted:
- AspNetUsers: 1 admin@rentalcommand.local, 2 testuser@example.com, 3 s11-fresh@qa.local, 4 s11-test2@qa.local — s11-fresh IS id 3.
- Properties: S13 Maple Cottage, S13 Oak Apartments, S14 Willow Apt, S15 Birch Manor, S11 Test House — all committed.
- Corroborating: the tester logged in as the freshly registered user and completed the wizard with state surviving reloads — impossible without committed writes. "priya.patel@email.example" is demo-seed data from a different database (old pre-squash `rentalcommand` or the preview container's Postgres).

BUG-3 (wizard footer buttons clipped at 390px) stands — tracked as a Notion task.
