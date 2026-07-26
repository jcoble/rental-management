# TSK-754 full-year execution run

## Run identity

- Run ID: `2026-07-26-001`
- Notion task: `TSK-754`
- Environment: `https://rental-command.chimp-map.ts.net`
- Initial source SHA: `ccac9ee24331bf42695772d745fb3a5a6992d10c`
- Current verified API source SHA: `c56902e973f045b1e4f7f8e4c7ef28bcd2725972`
- Current verified mobile source SHA: `f9e914dfacbe6e36d9fbd7103377e526c1cf8197`
- Planner: `Docs/Testing/YearSimulation2027/index.html`
- Schedule: `Docs/Testing/YearSimulation2027/schedule.csv`
- Corpus: `/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus`
- Financial oracle: `Docs/Testing/YearSimulation2027/rental-command-2027-simulation-oracle.xlsx`

## Safety boundary

- Use a new dedicated synthetic portfolio; preserve all unrelated preview data.
- Do not invoke preview `reset-data`.
- Do not use production.
- Notification delivery remains suppressed in preview; verify outbox/in-app state and delivery suppression evidence.
- Stop on unexplained financial variance, partial mutation, cross-portfolio access, or confidentiality failure.

## Queue

1. Restore the reusable `rental` verification runtime at the recorded source SHA.
2. Authenticate and create the dedicated simulation portfolio.
3. Execute the schedule in `run_id` order with the simulated clock.
4. Capture month-end financial comparisons and role/client evidence.
5. Capture, fix, and independently verify confirmed defects one at a time.
6. Complete year-end reconciliation and close the run.

## Defects

### TSK-754-D001 — Preview simulation clock disabled

- Status: Fixed; web and API verified, Engine date observation still required
- Severity: Blocking
- Evidence:
  - The real preview login screen did not render the `SIM CLOCK` panel.
  - `GET /api/v1/dev/clock` returned HTTP 404 on source SHA
    `ccac9ee24331bf42695772d745fb3a5a6992d10c`.
  - `deploy/docker-compose.preview.yml` did not enable the web, API, or Engine
    simulation-clock gates.
- Fix:
  - Enable `PUBLIC_SIMULATION_ENABLED` for web.
  - Enable `Simulation__Enabled` for API and Engine.
- Verification required:
  - The panel renders in a new browser session.
  - The authenticated clock route returns a clock state.
  - Setting a date updates the web clock and the Engine observes the same date.

### TSK-754-D002 — Guided Setup detection can remain pending forever

- Status: Fixed and independently verified in the real preview
- Severity: Blocking
- Reproduction:
  - Register and verify a new user.
  - Choose `Set up my real portfolio`.
  - Observe `Checking your existing setup…` indefinitely.
- Evidence:
  - All five detection endpoints returned HTTP 200 with valid JSON.
  - TanStack results for tenants and leases remained `pending/fetching`.
  - Calling each result's existing `refetch()` resolved immediately and advanced the UI.
- Root cause:
  - `detectionReady` joined the five `isSuccess` flags with a short-circuiting `&&` chain.
  - TanStack tracks only result properties that consumers read. Later queries could finish before
    their `isSuccess` property was observed, so the component never received their completed state.
- Fix:
  - Read every success and error flag into an array before reducing with `every` or `some`.
  - Add a regression assertion that rejects the short-circuiting readiness pattern.
- Verification required:
  - Passed: a fresh browser load advanced to the lease-first setup screen without a manual refetch.
  - Passed: focused onboarding tests and both web type-check lanes.
  - Evidence: `browser/d002-after-guided-setup-lease-first.png`.

### TSK-754-D003 — Scan extraction requires a workspace AI credential

- Status: Configuration prerequisite resolved for the pilot portfolio
- Severity: Blocking until configured
- Reproduction:
  - Upload `SCN-0001` through Guided Setup before configuring a workspace AI connection.
  - Draft 2 transitions from `Pending` to `Failed`.
- Evidence:
  - Upload returned HTTP 201 and created only the scan draft.
  - Failure reason was `AI extraction unavailable: configure a workspace OpenAI or Anthropic credential in Settings`.
  - Property, Unit, Tenant, LeaseManagement, LeaseAgreement, and TenantAccount counts for portfolio 3
    remained zero.
- Resolution:
  - Exercised Settings > AI connection through the real form.
  - Verified and saved the preview OpenAI integration as `gpt-4o`.
  - Re-uploaded the same PDF; extraction completed and opened the five-step review.
- Security note:
  - The credential remains encrypted in the application database and the saved form clears it.
  - The preview OpenAI key appeared once in diagnostic tool output during the shared-stack pilot.
    It must be rotated before this run is treated as security-complete; the key value is not
    reproduced in this evidence.

### TSK-754-D004 — Shared preview clock changes every portfolio's worker time

- Status: Shared-preview execution stopped before bulk worker execution; isolated runtime/database required
- Severity: Blocking safety boundary
- Evidence:
  - The simulation clock is a single global `SimulationClocks` row with no portfolio scope.
  - Both API and Engine register a clock-state refresher against that shared row.
  - Scheduled worker commands operate in the Engine's system/admin context across due portfolio data.
  - Therefore a year-long worker replay cannot be restricted to portfolio 3 merely by selecting that
    portfolio in the browser.
  - The shared clock was reset to `Real` immediately after discovery.
- Resolution required:
  - Stop only the shared `rental` application runtime while preserving its data.
  - Run the year under a distinct preview stack ID with a fresh persistent database and its own
    uploads/data-protection volumes.
  - Do not resume the schedule against the shared `rental` database.

### TSK-754-D005 — Tall camera JPEG can produce unrelated high-confidence lease data

- Status: Fixed and verified with the original camera JPEG in the isolated runtime
- Severity: Blocking scan-safety defect
- Reproduction:
  - Upload `SCN-0002`, a 1,360 by 13,210 camera JPEG containing nine vertically composited
    photographed lease pages.
  - Extraction opens the lease review but suggests the existing Arbor House and returns unrelated
    tenant, dates, rent, deposit, and fee values at 100% self-reported confidence.
- Safety evidence:
  - The source image was inspected and contains the planned `SCN-0002` Briar Cottage / Gray Lewis
    values; its SHA differs from `SCN-0001`.
  - The unconfirmed draft has a distinct stored-file hash and remains in Reviewing.
  - No second Property, Unit, Tenant, LeaseManagement, LeaseAgreement, or TenantAccount was saved.
- Root cause:
  - Non-PDF images were sent as one `image_url` using the configured `low` detail default.
  - A low-detail request reduces the entire 9.7:1 image to one small vision viewport, making each
    photographed page unreadable.
  - Local OCR is disabled in preview, so the model received no readable text fallback and guessed
    from the portfolio grounding context.
- Fix:
  - Detect images taller than three image widths.
  - Split them into overlapping page-sized JPEG tiles and send every tile at high detail.
  - Preserve the existing single-image behavior and configured detail for ordinary images.
  - Add a regression test proving a tall image produces multiple high-detail image parts.
- Verification:
  - Passed: all seven `OpenAiLlmProviderTests`.
  - Passed: deployed exact source SHA `5c428f3bd163b29fcbf7940c098e3609453e4d67`
    to `yearsim754`.
  - Passed: re-uploading the original `SCN-0002` JPEG extracted Briar Cottage, 134 Briar Street,
    Columbus, OH 43202; Gray Lewis; lease `SCN-0002`; and the 2026-03-01 through 2027-12-31 term.
  - The scan correctly left fields it could not read blank instead of fabricating values. Monthly
    rent, deposit, unit details, tenant contacts, late fee, due day, and notes were completed
    manually in the review.
  - Passed: the bad draft remains unconfirmed; the corrected draft was confirmed only after its
    visible values were checked against the planner.
  - Evidence: `browser/d005-scn-0002-fixed-confirmed.png`.

### TSK-754-D006 — One camera-image extraction failed transiently

- Status: Observed once; identical-file retry succeeded
- Severity: Non-blocking after controlled retry
- Reproduction:
  - Upload `SCN-0012` after the preceding nine opening-lease scans completed.
  - The draft moved to Failed and the UI displayed `We could not read that document. Try a clearer
    photo or the PDF.`
- Safety evidence:
  - The failed draft created no Property, Unit, Tenant, LeaseManagement, LeaseAgreement, or
    TenantAccount.
  - All six aggregate counts remained exactly 11.
- Retry:
  - Re-uploading the byte-identical source image immediately afterward extracted Lakeview Home,
    304 Lakeview Street, Columbus, OH 43212.
  - The corrected review was completed and confirmed; all six aggregate counts advanced to 12.
- Follow-up:
  - Preserve the failed and confirmed drafts for provider-failure-rate analysis during the
    remaining camera-image corpus.

### TSK-754-D007 — Native mobile dashboard ignores the simulation clock

- Status: Fixed and verified in the native Android app
- Severity: High for time-travel testing
- Reproduction:
  - Set the isolated environment clock to January 2, 2027.
  - Sign in to the native Android application as the simulation administrator.
  - Observe the date heading on the owner dashboard.
- Expected:
  - Every business-date surface uses the same simulated date as the API and Engine.
- Actual:
  - `GET /api/v1/dev/clock` reports `Offset` mode and `simNowUtc` on January 2, 2027.
  - The native dashboard renders `SUNDAY, JULY 26`, the emulator's wall-clock date.
- Root-cause evidence:
  - `mobile/lib/features/home/home_shell.dart` formats the dashboard heading from
    `DateTime.now()` rather than a server-provided simulation/business clock.
  - The same source sweep found additional mobile forms and report defaults using
    `DateTime.now()`; each must be verified against its intended wall-clock versus
    business-date semantics before the defect is treated as fixed.
- Safety impact:
  - A native user can create or filter records against the wrong date during a simulated run.
  - The year replay cannot claim mobile date-sensitive coverage until this boundary is corrected
    and retested.
- Evidence:
  - `mobile/android-admin-dashboard-wrong-simulation-date.png`
  - `mobile/d007-api-clock-state.json`
- Fix:
  - Add a mobile application-clock provider that reads the simulation-only `/dev/clock` endpoint.
  - Preserve the server's canonical UTC calendar date so a midnight simulation does not move to the
    previous day in a western device timezone.
  - Use device time only when production's simulation-only route returns 404; surface other clock
    failures rather than silently displaying a potentially incorrect date.
  - Refresh the application clock with the owner dashboard's pull-to-refresh action.
- Verification:
  - Passed: three focused provider tests covering simulated time, production 404 fallback, and
    non-404 failure propagation.
  - Passed: focused Flutter analysis for the provider, dashboard, and tests.
  - Passed: the correctly flavored dev APK built and installed on the Android emulator.
  - Passed: the native dashboard rendered `SATURDAY, JANUARY 2` while the API remained in `Offset`
    mode on January 2, 2027.
  - Passed on the required Azure VPS emulator: the exact committed dev APK rendered
    `SUNDAY, JANUARY 3` while connected to the isolated `yearsim754` stack.
  - Evidence: `mobile/d007-fixed-dashboard-simulation-date.png`.
  - Azure evidence: `mobile/azure/d007-simulated-date-jan03.png`.

### TSK-754-D008 — Mobile scan preview traps the review screen's vertical swipe

- Status: Fixed and verified on the Azure Android emulator
- Severity: High scan-review usability defect
- Reproduction:
  - Upload `SCN-0013` through Android's system file picker as a signed agreement.
  - Wait for extraction; draft 15 reports nine fields needing attention.
  - Start a vertical swipe on the 240-pixel document preview.
- Expected:
  - The review list scrolls to signing status, property/unit/tenant choices, and editable lease
    terms regardless of where the user starts the normal one-finger vertical gesture.
- Actual:
  - Repeated vertical gestures on the preview do not move the review list.
  - Starting the same gesture on the checkpoint card above the preview scrolls successfully.
- Root cause:
  - `_DocumentPreview` used a default `InteractiveViewer`; its pan recognizer wins the gesture
    arena even at 1x and consumes the parent `ListView`'s vertical swipe.
- Safety evidence:
  - Draft 15 is Reviewing and no property, unit, tenant, lease, agreement, or account was created.
- Fix:
  - First attempt: disable panning and scaling in the embedded `InteractiveViewer`.
  - Failed retest: the same preview-origin swipe still did not move draft 16, proving the wrapper
    continued to participate in the gesture arena.
  - Revised fix: remove `InteractiveViewer` from the embedded preview entirely so the parent review
    owns the normal vertical gesture.
  - Add a focused contract regression test for the preview's gesture configuration.
- Verification:
  - Passed: after uploading `SCN-0014`, a vertical swipe beginning inside the rendered document
    preview moved the review list to the signing and property controls.
  - Evidence: `mobile/azure/d008-preview-origin-scroll-fixed.png`.
- Evidence:
  - `mobile/scn-0013-mobile-after-picker.png`
  - `mobile/scn-0013-review-scroll-fast.png`

### TSK-754-D009 — Scan review uses Flutter APIs scheduled for removal

- Status: Fixed; focused analyzer clean
- Severity: Low today, future-build compatibility risk
- Evidence:
  - Focused Flutter analysis reports four deprecated `Radio` properties (`groupValue` and
    `onChanged`) in the lease signing-status section.
  - The same analysis reports deprecated `DropdownButtonFormField.value` use in the template
    selector.
- Expected:
  - A core scan-review screen analyzes cleanly against the supported Flutter SDK.
- Actual:
  - The focused analyzer exits nonzero with five deprecation findings.
- Follow-up:
  - Migrate signing status to a `RadioGroup` ancestor and the template selector to
    `initialValue`, then rerun the scan-review tests and analyzer.
- Fix and verification:
  - Migrated the signing choices to a `RadioGroup` ancestor.
  - Replaced the deprecated template selector `value` with `initialValue`.
  - The clock and scan-review focused tests passed, and focused analysis reported no issues.

### TSK-754-D010 — Newly invited management users receive 403 on allowed screens

- Status: Fixed and verified through the API and Azure Android emulator
- Severity: Critical role-access defect
- Reproduction:
  - Invite a new Workspace Administrator, activate the invitation, and sign in through the native
    Android app.
  - The returned access envelope identifies `workspace-administrator`, reports Management as the
    active experience, and includes `rentals.read`.
  - Open Rentals / Properties, or send an authenticated `GET /api/v1/properties`.
- Expected:
  - The invited administrator can use the management screens allowed by its assignment.
- Actual:
  - The native screen reports `You do not have permission to perform this action.`
  - The equivalent direct API request returns HTTP 403 with an empty body.
- Root cause:
  - Team membership creation gave a new `WorkspaceAccessContext` no
    `LastAuthorizedExperience`.
  - The login access envelope fell back to the membership's Management default, but management
    controllers checked the persisted null value and denied the request.
  - Existing relationship contexts must retain their Owner or Tenant experience, so the default
    can only be initialized when the access context is newly created.
- Fix:
  - Initialize a brand-new Team access context's `LastAuthorizedExperience` from the selected
    role profile's default experience.
  - Add an integration assertion that a new Leasing Agent invitation persists Leasing while the
    existing relationship-context test continues to prove Owner is preserved.
- Verification:
  - Passed: the focused PostgreSQL invitation integration test.
  - Passed: a newly invited Workspace Administrator received HTTP 200 from
    `GET /api/v1/properties`.
  - Passed: the same invited administrator opened the native Properties screen and loaded Arbor
    House through Elm Haven on the Azure emulator.
  - Evidence: `mobile/azure/d010-invited-admin-properties-fixed.png`.

### TSK-754-D011 — Mobile scan review omits the original source filename

- Status: Confirmed
- Severity: High scan-safety usability defect
- Reproduction:
  - Select a lease PDF from Android DocumentsUI and open its completed review.
- Expected:
  - The review identifies the original filename clearly enough for the user to verify that the
    intended source was selected before creating business records.
- Actual:
  - The review showed only `Uploaded document #17`.
  - The PDF upload path persisted the generic filename `agreement-scan.pdf`.
  - The user therefore could not tell from the review that a different PDF had been selected.
- Safety evidence:
  - Draft 17's stored SHA-256 is
    `37176f2e86f88457730f25611c7875f7bdf59dc58a080770b22a41938b5c0d22`.
  - The planned `SCN-0014` camera JPEG's SHA-256 is
    `c29dafffe828d24a3526d148fe121d37907ace94efb0e9d87ff9c3ca30fb0001`.
  - The extracted Kingston House / `SCN-0011` values were consistent with the PDF actually
    selected; this was not an AI hallucination.
  - Draft 17 was rejected after D013 was fixed and created no rental aggregate.
- Follow-up:
  - Preserve and display the original source filename in the picker, upload metadata, and review.
  - Show source hash or another durable identifier in diagnostic details.

### TSK-754-D012 — Server and test projects restore packages with known vulnerabilities

- Status: Confirmed; remediation not yet applied
- Severity: High security maintenance finding
- Evidence:
  - `System.Security.Cryptography.Xml` 9.0.0 reports five high-severity advisories:
    `GHSA-23rf-6693-g89p`, `GHSA-8q5v-6pqq-x66h`, `GHSA-cvvh-rhrc-wg4q`,
    `GHSA-g8r8-53c2-pm3f`, and `GHSA-mmjf-rqrv-855v`.
  - `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 reports high-severity advisory
    `GHSA-2m69-gcr7-jv3q`.
  - `MailKit` 4.15.1 reports moderate-severity advisory `GHSA-9j88-vvj5-vhgr`.
- Follow-up:
  - Upgrade or remove the vulnerable dependency versions, then rerun restore, focused tests, and
    dependency auditing before security completion.

### TSK-754-D013 — Rejecting a reviewing scan returns HTTP 500

- Status: Fixed and verified through the live Azure API and Android emulator
- Severity: High scan-workflow defect
- Reproduction:
  - From the native review for draft 17, enter a rejection reason and confirm Reject.
  - `POST /api/v1/scans/17/reject` returns HTTP 500 and the draft remains Reviewing.
- Root cause:
  - `RejectAuthorizedAsync` changed a tracked `ScanDraft`, which is an `Updated` database
    mutation, but bound its semantic audit as `Rejected`.
  - The atomic audit boundary correctly rejected that mismatch with
    `Semantic audit does not match the exact pending tracked mutation.`
- Safety evidence:
  - The atomic transaction rolled back; the draft remained Reviewing and no rental aggregate was
    created.
- Fix:
  - Bind the rejection's exact tracked operation as `Updated` while retaining its rejected status,
    reviewer, reason, and semantic change description.
  - Add a focused regression contract guarding the exact operation.
- Verification:
  - Failed before the fix and passed after it on the Azure runner.
  - All six focused `AtomicScanConfirmationPersistenceTests` pass.
  - The deployed exact source returned HTTP 200 when rejecting draft 17.
  - Database status counts became 12 Confirmed, 1 Failed, 1 Rejected, and 3 Reviewing before the
    corrected `SCN-0014` confirmation.
  - The Azure Android detail screen showed Rejected and `This scan has been rejected.`
  - Evidence: `mobile/azure/d013-scan-rejection-fixed.png`.

### TSK-754-D014 — Mobile lease review cannot complete tenant and unit details

- Status: Fixed and verified through the live Azure Android emulator
- Severity: High scan-first data-completeness defect
- Reproduction:
  - Upload `SCN-0014` through Android DocumentsUI and choose Create new.
  - Review every field available before importing the signed lease.
- Expected:
  - The scan-first bootstrap lets the user verify or manually fill all tenant contact and unit
    physical fields accepted by the API before the aggregate is created.
- Actual:
  - The mobile review exposes only the tenant name and seven lease terms.
  - It does not expose tenant email, phone, emergency contact, unit number, bedrooms, bathrooms,
    or square footage even though the extraction schema and confirmation API accept those values.
  - Confirmed `SCN-0014` created Rina King with null/blank contacts and Unit Main with 0 bedrooms,
    0 bathrooms, and null square footage instead of the oracle's 4 bedrooms, 1 bathroom, and
    1,540 square feet.
- Evidence:
  - `mobile/azure/scn-0014-review-property-signing.png`
  - `mobile/azure/scn-0014-review-terms.png`
  - The native tenant form saved and reloaded `tenant.014@example.local`, `6145550114`, and
    `Morgan King 614-555-0199`; PostgreSQL independently returned the same three values for Rina
    King. Evidence: `mobile/azure/scn-0014-tenant-corrected.png`.
  - The native unit form exercised unit number, floor plan, beds, baths, square feet, market rent,
    and notes. It saved and reloaded Main, `4BR-1BA`, 4 beds, 1 bath, 1,540 square feet, $1,200,
    and the turnover note; PostgreSQL independently returned the same values.
  - After the save, the native Summary showed Occupied, Active, 4 beds, and 1 bath, while
    `vw_lease_management_lifecycle` returned business date `2027-01-03`, lifecycle `Occupied`,
    primary tenant Rina King, and no reconciliation exception.
    Evidence: `mobile/azure/scn-0014-unit-corrected.png`.
- Root cause:
  - The native review's fixed lease-field order rendered only tenant name and seven lease terms.
    The API contract and override builder already accepted the omitted tenant and unit facts, but
    no native inputs exposed them.
- Fix:
  - Render tenant email, phone, emergency contact, unit number, bedrooms, bathrooms, and square
    feet in every native lease review.
  - Use email, phone, and numeric keyboards for the corresponding inputs.
  - Add a regression contract requiring each supported field to remain recognized and rendered.
- Verification:
  - The regression failed against the old source, then all 12 focused mobile tests passed against
    exact source SHA `7fbd4455f192cf21eabefdd38ee74b39c0f0a824`.
  - The APK built from that exact SHA was installed over the existing app on Azure emulator
    `emulator-5554`.
  - The fixed review visibly rendered every added input. Evidence:
    `mobile/azure/d014-mobile-lease-review-fixed.png`.
  - `SCN-0013` was then confirmed through that review. PostgreSQL independently matched Nolan
    Flores, `tenant.013@example.local`, `6145550113`, `Morgan Flores 614-555-0198`, Unit Main,
    3 bedrooms, 1 bathroom, 1,425 square feet, lease `SCN-0013`, $1,575 rent, $1,575 deposit,
    and a $50 late fee.

### TSK-754-D015 — Historical signed-lease import creates contradictory lifecycle state

- Status: Fixed and verified through the live Azure API, PostgreSQL database, and Android emulator
- Severity: Blocking lifecycle and money defect
- Reproduction:
  - With the simulation in January 2027, import fully signed `SCN-0014` with a March 1, 2026 start.
- Expected:
  - An already-executed historical lease that governs the current business date creates or records
    current possession, or blocks confirmation and asks the user for the missing possession fact.
- Actual:
  - The Agreement is Active and governing, but LeaseManagement has only planned possession and no
    `PossessionGivenAtUtc`.
  - The native Unit screen simultaneously shows `Active`, an `Upcoming` badge, and
    `Needs review: the lifecycle facts do not agree.`
  - `vw_lease_reconciliation_exceptions` reports
    `GoverningAgreementWithoutPossession`.
- Evidence:
  - `mobile/azure/scn-0014-imported-lifecycle-conflict.png`
  - The cancel path on Give possession left the relationship unchanged.
  - Confirming the same action recorded possession, changed lifecycle to Occupied, and reduced
    reconciliation exceptions for LeaseManagement 13 from one to zero.
  - Evidence: `mobile/azure/scn-0014-possession-recorded.png`
- Root cause:
  - Signed-lease confirmation created the lease relationship and active agreement without accepting
    or validating the historical possession fact.
  - A historical agreement could therefore govern the business date while the lifecycle still
    projected as pre-possession.
- Fix:
  - Add `possession_given_at` to the reviewed scan contract and native lease review.
  - For a new historical, already-signed lease relationship, require a reviewed possession date no
    later than confirmation and persist it inside the same atomic confirmation transaction.
  - Preserve the existing behavior for future signed leases and imports linked to an existing
    relationship.
- Verification:
  - Passed: eight data-contract tests, two focused API lease-preparation tests, two PostgreSQL
    historical signed-lease integration tests, and all 12 focused native scan-override tests.
  - Exact source SHA `c56902e973f045b1e4f7f8e4c7ef28bcd2725972` was built under the Azure
    heavy-work lock and deployed to both the isolated API stack and Android emulator.
  - `SCN-0015` was imported through the native review with possession date `2026-04-01`.
    PostgreSQL independently returned that exact date, no possession exception, Active agreement
    `SCN-0015`, and the native Unit screen immediately reported Occupied.
  - The source SHA-256 matched the manifest:
    `bf226e792857c2a4c3a87db9c2868e078f5ca721da13e1c6f21aa593f54beaf5`.
  - All reviewed property, unit, tenant, and lease values matched the oracle, and all six aggregate
    counts advanced atomically from 14 to 15.
  - Evidence: `mobile/azure/d015-historical-lease-possession-fixed.png`.

### TSK-754-D016 — Date-only simulation clock anchors at UTC midnight

- Status: Fixed and verified against the live Azure API and PostgreSQL database
- Severity: Blocking time-travel correctness defect
- Reproduction:
  - Select January 3, 2027 in the simulation date picker.
  - Compare the stored clock instant and the portfolio's database business date.
- Expected:
  - Selecting January 3 anchors the clock at the start of January 3 in the configured business
    timezone.
- Actual:
  - The controller parsed the date as `2027-01-03T00:00:00Z`.
  - Portfolio 2 uses `America/New_York`, so the database business date was January 2 while the
    native dashboard displayed January 3.
- Root cause:
  - `DevClockController.Set` treated a semantic date-only value as a UTC instant.
- Fix:
  - Parse exact `yyyy-MM-dd` values as local midnight in the requested or configured business
    timezone, then convert that instant to UTC.
  - Preserve the existing `instantUtc` and date-time input behavior.
- Verification:
  - The focused integration test failed before the fix: expected `2027-01-03T05:00:00Z`, received
    `2027-01-03T00:00:00Z`.
  - Both `DevClockControllerTests` pass after the fix.
  - Exact source SHA `1a34244a2cfb7133d60557d9c130d5914d32c699` was built under the Azure
    heavy-work lock, labeled into `rc-preview-api:yearsim754`, and deployed to the isolated stack.
  - The recreated API container reported healthy and its image revision label matched the exact
    source SHA.
  - Posting date-only `2027-01-03` with timezone `America/New_York` and frozen mode returned
    `2027-01-03T05:00:00Z`; a separate anonymous clock read returned the same instant.
  - PostgreSQL independently returned `2027-01-03` from `rc_business_date(1)`.

### TSK-754-D017 — Native scan drafts had no history path and reopened in an obsolete review

- Status: Fixed and verified in the live Azure Android emulator
- Severity: Blocking scan-recovery defect
- Reproduction:
  - Upload a document through the native `Scan / Add` flow and leave it in Review.
  - Return to normal application navigation and attempt to resume that draft.
- Actual:
  - The native application implemented `ScanListScreen`, but no authorized management or leasing
    screen exposed a normal navigation path to it.
  - Opening a draft from the otherwise unreachable list used the obsolete guided rental flow,
    which could only create new records and omitted the canonical signing, link-existing,
    possession, and complete lease-review controls.
- Fix:
  - Add the authorized `/scans` route and a `View scan history` action to the global native
    capture sheet.
  - Allow scan-capable management and leasing experiences to enter the history while continuing
    to deny the owner-only experience.
  - Open every resumable draft in the canonical `ScanReviewScreen`; remove the obsolete guided
    rental import from scan-history navigation.
- Verification:
  - Passed: all 44 focused Flutter scan-history, capture-sheet, route, review, and source-contract
    tests.
  - Passed: focused Flutter analysis of all six changed or directly exercised files with no
    issues.
  - Exact source SHA `364ee3c11e63a23ac2b480c610e3d0f417fd8387` was built under the Azure
    heavy-work lock and installed as Android version code `75404` without clearing application
    data or authentication.
  - The live capture sheet exposed `View scan history`; the history showed Confirmed, Rejected,
    Failed, and Reviewing records; and tapping draft 15 opened the canonical full review before
    any save.
  - The resumed duplicate review linked Maple House / Unit Main, selected the executed-agreement
    disposition, required and accepted possession date `2026-02-01`, and confirmed successfully.
  - PostgreSQL independently proved drafts 15 and 16 have the identical source SHA-256
    `ad0ec3a383b95fcaa4e143ad021d525e8ff0e1382610df26e2856cd471f5d9d1`, both resolve to
    agreement 14, and all six aggregate counts remained exactly 15.
  - Evidence: `mobile/azure/d017-mobile-scan-history-fixed.png`.

### TSK-754-D018 — Native gallery selection downscaled tall scans before upload

- Status: Fixed and verified in the live Azure Android emulator
- Severity: Blocking scan-accuracy defect
- Reproduction:
  - Choose the planned `SCN-0016` tall camera JPEG from the native gallery.
  - Compare the selected source, stored upload, and extracted lease fields.
- Expected:
  - The native gallery path uploads the selected scan without discarding source pixels needed
    for extraction.
- Actual:
  - The planned source was a 1360×13210 JPEG with SHA-256
    `ecaf162120c302999fae6dbbfc6d27193450879ced7d27f14c84dd5f3ac90fb7`.
  - Native `image_picker` constrained gallery selections to 1600 pixels and JPEG quality 80.
  - Draft 20 stored a 165×1600 JPEG with SHA-256
    `ffd9f496ddecb83becfc83ead2d457584004d4b152e26e18b7d3f874eec8c211`.
  - Extraction returned unrelated existing Arbor House / Henry Cole details with high confidence.
  - Draft 20 was rejected with reason
    `Native gallery downscaled tall scan extraction unreliable`; no business aggregate was
    created.
- Fix:
  - Preserve the original selected file for gallery and multi-image imports without resizing or
    recompression.
  - Keep camera JPEG quality at 90 while removing the camera dimension cap.
  - Add a navigation/source contract test that rejects the former `maxHeight: 1600` path.
- Verification:
  - Passed: 43 focused Flutter scan tests.
  - Passed: focused analysis of the two changed Flutter files with no issues.
  - Exact source SHA `f9e914dfacbe6e36d9fbd7103377e526c1cf8197` was built under the Azure
    heavy-work lock and installed as Android version code `75405` without clearing application
    data or authentication.
  - Draft 21 preserved the exact original SHA-256 and 1360×13210 dimensions.
  - Extraction correctly identified Parkside Home, Avery Brooks, 372 Park Street, Columbus,
    `SCN-0016`, and both agreement dates; unreadable values remained blank for manual review
    instead of being hallucinated.
  - The live native review populated every property, unit, tenant, possession, and lease field.
  - Confirmation created Parkside Home / Unit Main / Avery Brooks / `SCN-0016`; direct
    PostgreSQL reads matched the 3-bedroom, 2-bath, 850-square-foot unit, $1,350 rent and deposit,
    $75 late fee, due day 1, and possession date `2026-05-01`.
  - Properties, Units, Tenants, LeaseManagements, LeaseAgreements, and TenantAccounts advanced
    atomically from 15 to 16, and draft 21 resolved to agreement 16.
  - Evidence: `mobile/azure/d018-native-gallery-resolution-fixed.png`.

### TSK-754-D019 — Existing-property scan search did not refresh while typing

- Status: Fixed and verified in the live Azure Android emulator
- Severity: Blocking mobile scan-review defect
- Reproduction:
  - Resume `SCN-0022`, choose Link existing, and type `Union` into
    `Search property or Unit`.
  - Wait for the expected search refresh without submitting the keyboard form.
- Expected:
  - The visible targets narrow to Union Duplex while the user types.
- Actual:
  - Arbor House and other unrelated targets remained visible after five seconds.
- Root cause:
  - The provider key read the search controller text, but the widget rebuilt only from
    `onSubmitted`; ordinary typing never changed provider state.
- Fix:
  - Debounce `onChanged` by 300 milliseconds, store the submitted query in widget state, reset
    paging, and retain immediate keyboard-submit behavior.
- Verification:
  - All 14 focused lease-scan override tests passed and focused analysis reported no issues at
    exact source SHA `83d3e1fb3dbd638b1fdd91bf1997d7d018bf9caf`.
  - Android version code `75406` was built under the Azure heavy-work lock and installed without
    clearing application data or authentication.
  - Typing `Union` without submitting narrowed the live results to Union Duplex alone.
- Evidence:
  - `mobile/azure/d019-link-existing-search-unfiltered.png`
  - `mobile/azure/d019-d020-existing-property-unit-fixed.png`

### TSK-754-D020 — Mobile lease scan could not create a Unit under an existing Property

- Status: Fixed and verified end to end in the live Azure stack
- Severity: Blocking multi-rental scan-import defect
- Reproduction:
  - Review `SCN-0022`, whose proposal correctly links Union Duplex and creates Unit B.
  - Inspect the mobile Property and Unit choices.
- Expected:
  - Select Union Duplex as the existing Property and create Unit B from the reviewed lease.
- Actual:
  - Create new would duplicate the Property; Link existing offered only existing Property-and-Unit
    pairs, which would incorrectly attach the lease to Unit A.
- Root cause:
  - The mobile readiness gate required both an existing Property and existing Unit even though
    the atomic server confirmation contract already supports `propertyId` without `unitId` and
    creates the reviewed Unit under that authorized Property.
- Fix:
  - Expose a `Create the Unit from this lease scan` choice for each matching Property.
  - Permit confirmation with an existing Property, no existing Unit, and a reviewed Unit number.
  - Continue sending no `unitId`, which invokes the server's existing atomic Unit-creation path.
- Verification:
  - The live review separately displayed `Create the Unit from this lease scan` for Union Duplex
    and the existing Unit A target.
  - Confirming `SCN-0022` created Unit B under Property 21 and did not alter or reuse Unit A.
  - PostgreSQL returned 21 Properties and 22 Units after confirmation, with all five dependent
    tenant, management, agreement, and account aggregates exactly 22.
- Evidence:
  - `mobile/azure/d019-d020-existing-property-unit-fixed.png`
  - `mobile/azure/scn-0022-opening-lease-confirmed.png`

## Tooling and maintenance observations

- The Azure Flutter build reports that Kotlin's current built-in version will be unsupported by a
  future Flutter release.
- One Android dependency reports use of a deprecated API during compilation.
- Dependency resolution reports 63 packages with newer versions outside the current constraints.
- The current iOS plugin set is not fully compatible with Swift Package Manager.
- Dev push registration remains unavailable because the dev Firebase configuration is absent;
  native push verification requires the separate configured-production lane.
- The first API image rebuild used the repository root instead of the published API directory,
  producing a container without `RentalCommand.Api.dll` and a temporary HTTP 502. The isolated
  database, uploads, credentials, keys, and volumes were untouched. Rebuilding from `publish/api`
  restored HTTP 200 health. This is a verification-harness incident, not a product defect.
- The .NET build also reports nullable-reference warnings across Data, API, Portal, Money, and
  Portfolio QA paths; `Program` symbol conflicts in Engine tests; and obsolete test APIs. These
  remain maintenance findings to triage without weakening the executed behavioral results.

## Disproved observations

### Mobile login 401 after automated field entry

- Status: Closed as test-input false positive; not a product defect
- Evidence:
  - The exact values read back from the native fields succeeded through a direct mobile-header API
    request.
  - Pressing `Sign In` again without changing either visible field returned HTTP 200 in the native
    application and opened the owner dashboard.
- Handling:
  - Retain the failed-attempt screenshots as run evidence, but do not count this as a product bug.

## Pilot scan proof

- `SCN-0001` was uploaded twice: the first attempt intentionally established the missing-credential
  failure boundary; the second extracted successfully after configuration.
- Before confirmation, portfolio 3 contained 0 Properties, 0 Units, 0 Tenants, 0 LeaseManagements,
  0 LeaseAgreements, and 0 TenantAccounts.
- The review populated Arbor House, 117 Arbor Street, Columbus, OH 43201; Unit Main; Dana Garcia;
  the 2026-02-01 through 2027-12-31 term; $1,575 monthly rent; $1,575 deposit; and rent due day 1.
- Manual completion also exercised bedrooms, bathrooms, tenant email, tenant phone, emergency
  contact, late fee, and lease notes.
- After `Confirm & create`, each of the six aggregate counts was exactly 1.
- This is pilot evidence only. The official schedule counter remains zero until the isolated run is
  initialized and `SCN-0001` is repeated there.

## Isolated official run proof

- The shared `rental` application runtime was stopped without deleting or resetting its PostgreSQL
  data, uploads, credentials, data-protection keys, or volumes.
- The official run uses stack ID `yearsim754`, with a distinct PostgreSQL bind mount and distinct
  uploads and data-protection volumes.
- A fresh synthetic administrator selected the real-portfolio path, creating portfolio 2 in the
  isolated database.
- Settings > AI connection was exercised through the real web form. The `gpt-4o` connection test
  passed, Save connection became available, the connection saved as Connected, and the API-key
  field cleared to zero characters.
- The authenticated simulation clock moved to `2027-01-02`; the existing session was invalidated
  as expected and the administrator signed in again at the simulated date.
- Before official confirmation, portfolio 2 contained 0 Properties, 0 Units, 0 Tenants,
  0 LeaseManagements, 0 LeaseAgreements, and 0 TenantAccounts.
- `SCN-0001` was uploaded from its rendered PDF and extracted into the five-step review. Extracted
  values were read back and every available field was completed, including bedrooms, bathrooms,
  tenant contact data, emergency contact, late fee, and lease notes.
- The executed-agreement choice was set to `Yes, everyone has signed`.
- After `Confirm & create`, portfolio 2 contained exactly 1 of each of the six aggregate records.
  No aggregate existed before confirmation, proving the official first scan did not partially
  save the rental graph.
- Evidence: `browser/isolated-official-scn-0001-confirmed.png`.
- `SCN-0002` exercised the rendered camera-JPEG path after D005 was fixed. Before confirmation,
  all six aggregate counts remained exactly 1. The review created Briar Cottage, Unit Main, Gray
  Lewis, and the planned active lease; after confirmation, all six counts advanced atomically to
  exactly 2.
- The failed D005 reproduction remains as draft 2 in Reviewing while corrected draft 3 is
  Confirmed. It did not create any business aggregate.
- Evidence: `browser/d005-scn-0002-fixed-confirmed.png`.
- `SCN-0003` through `SCN-0012` were then executed through the real browser upload, review, and
  confirmation screens. Every review field was populated, including rental structure, unit
  details, tenant contacts, emergency contact, lease dates, rent, deposit, late fee, due day,
  notes, and executed-agreement choice.
- The resulting property sequence exactly matches the planner: Arbor House, Briar Cottage, Cedar
  Bend, Dover House, Elm Haven, Franklin Place, Grove House, Hawthorne Home, Ivy House, Juniper
  Place, Kingston House, and Lakeview Home.
- After the final confirmation, portfolio 2 contained exactly 12 Properties, 12 Units, 12 Tenants,
  12 LeaseManagements, 12 LeaseAgreements, and 12 TenantAccounts.
- Evidence: `browser/run-20270102-01-opening-leases-complete.png`.
- The corrected `SCN-0014` camera JPEG was selected through Android DocumentsUI and its SHA-256
  matched the manifest. Every available review value was checked against the oracle before import.
- Confirmation advanced Properties, Units, Tenants, LeaseManagements, LeaseAgreements, and
  TenantAccounts atomically from 12 to 13.
- The save also established D014 and D015: omitted tenant/unit details persisted blank or zero,
  and the governing historical agreement lacks possession.
- The native tenant and unit edit flows were then used to correct every omitted SCN-0014 value
  represented in the planner plus the manual-only phone, emergency contact, floor-plan, and unit
  notes fields. The corrected values reloaded in the app and matched direct PostgreSQL reads.
- The corrected unit now reports Occupied/Active on the native Summary and has no lease
  reconciliation exception.
- The fixed native review then confirmed `SCN-0013` with every exposed tenant, unit, and lease
  field completed. All six aggregate counts advanced atomically from 13 to 14, and direct
  PostgreSQL reads matched every reviewed value.
- `SCN-0013` independently reproduced D015: its governing historical signed agreement was saved
  without possession and projected as Upcoming with a reconciliation exception.
- After the D015 fix was deployed, `SCN-0015` was uploaded through Android DocumentsUI and
  completed through the native review. Every property, unit, tenant, and lease field was populated;
  the new possession field was set to `2026-04-01`.
- Confirmation advanced Properties, Units, Tenants, LeaseManagements, LeaseAgreements, and
  TenantAccounts atomically from 14 to 15. Direct PostgreSQL reads matched Oak House, Unit Main,
  Uri Price and all reviewed contacts and lease terms, including the $1,725 rent and deposit,
  $75 late fee, and possession date. The Unit screen immediately reported Occupied and no
  possession reconciliation exception was stored.
- Evidence: `mobile/azure/d015-historical-lease-possession-fixed.png`.
- The byte-identical `SCN-0013` re-upload was then resumed from the newly reachable native scan
  history and reviewed through the canonical form. Drafts 15 and 16 share the same source
  SHA-256 and both resolve to agreement 14. Properties, Units, Tenants, LeaseManagements,
  LeaseAgreements, and TenantAccounts remained exactly 15, proving confirmation did not create
  a duplicate or a partial aggregate.
- Evidence: `mobile/azure/d017-mobile-scan-history-fixed.png`.
- The corrected native gallery path then uploaded the original 1360×13210 `SCN-0016` JPEG
  byte-for-byte. Its extracted identity and address fields matched the oracle, every omitted
  property, unit, tenant, possession, and lease value was entered in the native review, and the
  final Parkside Home unit screen showed Avery Brooks on active agreement `SCN-0016`.
- Direct PostgreSQL reads matched every reviewed value and proved all six aggregate counts
  advanced atomically from 15 to 16.
- Evidence: `mobile/azure/d018-native-gallery-resolution-fixed.png`.
- `SCN-0017` exercised a nine-page low-contrast photocopy PDF through the native Android document
  picker. Extraction correctly identified Quarry House, 389 Quarry Street, Dana Garcia, Unit Main,
  agreement `SCN-0017`, both agreement dates, rent, deposit, and due day. Every low-confidence or
  omitted field was manually completed in the canonical mobile review, including email, phone,
  emergency contact, four bedrooms, one bathroom, 965 square feet, possession date, and late fee.
- Draft 22 preserved the exact source and executed-artifact SHA-256
  `6866b049f3a4c037f54e5a44b3af24f21f5961a8c99ba0fcc2e660f56d904137`.
  Direct PostgreSQL reads matched all reviewed values and proved Properties, Units, Tenants,
  LeaseManagements, LeaseAgreements, and TenantAccounts advanced atomically from 16 to 17.
- Evidence: `mobile/azure/scn-0017-opening-lease-confirmed.png`.
- `SCN-0018` exercised another 13,210-pixel camera JPEG through the fixed native gallery path.
  Draft 23 preserved the exact planned SHA-256
  `45b09f4242bc5897408616c1588025d9473a2830ac9128f2bacda35af4f76156`.
  The canonical review manually completed all 13 low-confidence fields, including tenant contacts,
  Unit Main's two bedrooms, one bathroom, 1,080 square feet, possession, $1,500 rent and deposit,
  $75 late fee, and due day 1.
- Confirmation created River House / Unit Main / Gray Lewis / `SCN-0018`. The executed artifact
  retained the same source SHA, direct PostgreSQL reads matched every reviewed value, and all six
  aggregate counts advanced atomically from 17 to 18.
- Evidence: `mobile/azure/scn-0018-opening-lease-confirmed.png`.
- `SCN-0019` exercised the native Android PDF picker with a nine-page, slightly skewed phone-camera
  lease. Draft 24 and its executed artifact preserved the planned SHA-256
  `ddff9cc3fdc970e4d704ca5d323c989dbb6ee66c322e57486889a5da0d7e1aa2`.
  The mobile review completed every uncertain contact, unit, possession, and late-fee field.
- Confirmation created Summit Home / Unit Main / Jordan Reed / `SCN-0019`. PostgreSQL matched the
  three-bedroom, one-bath, 1,195-square-foot unit, $1,125 rent and deposit, due day 1, and all
  tenant contacts; all six aggregates advanced atomically from 18 to 19.
- Evidence: `mobile/azure/scn-0019-opening-lease-confirmed.png`.
- `SCN-0020` exercised a low-contrast 13,210-pixel camera JPEG through the fixed Android gallery
  path. Draft 25 and the executed artifact retained the planned SHA-256
  `f7386ea55d8854434500e8565fbadec74607bfc339fa56312dbee1572b9133ef`.
- Confirmation created Terrace House / Unit Main / Morgan Adams / `SCN-0020`. Direct database
  reads matched the corrected phone, all contacts, four bedrooms, two bathrooms, 1,310 square
  feet, possession, $1,650 rent and deposit, $75 late fee, and due day 1. All six aggregates
  advanced atomically from 19 to 20.
- Evidence: `mobile/azure/scn-0020-opening-lease-confirmed.png`.
- `SCN-0021` exercised the first MultiRental bootstrap through the native Android PDF picker.
  Draft 26 and the executed artifact retained the planned SHA-256
  `87c4df8dede8e2ae06f123f19b6e8d80b6ddee5092178bcc702ab505482c1ac9`.
- Confirmation created Union Duplex / Unit A / Parker Flores / `SCN-0021`. Direct PostgreSQL
  reads matched the reviewed 2-bedroom, 1-bath, 1,425-square-foot unit, $1,375 market rent,
  rent and deposit, possession date, due day, late fee, and all tenant contacts. All six
  aggregates advanced atomically from 20 to 21.
- Evidence: `mobile/azure/scn-0021-opening-lease-confirmed.png`.
- `SCN-0022` exercised a second Unit lease under the existing Union Duplex MultiRental Property.
  Draft 27 and the executed artifact retained the planned JPEG SHA-256
  `2ec5d61984b5a7990560ce60d71744ebc1659147b7f91a7ea2ad59a97dd61bd9`.
- The fixed live search selected Union Duplex while leaving Unit A untouched, and the fixed
  existing-Property path created Unit B from the scan. Direct PostgreSQL reads matched Sage King,
  every contact, three bedrooms, one bathroom, 1,540 square feet, possession date `2026-05-01`,
  $1,900 market rent/rent/deposit, $75 late fee, and due day 1.
- Properties correctly remained 21 while Units, Tenants, LeaseManagements, LeaseAgreements, and
  TenantAccounts advanced atomically from 21 to 22.
- Evidence: `mobile/azure/scn-0022-opening-lease-confirmed.png`.

## Checkpoint 2026-07-26

- Status: isolated run initialized; January 3 opening-lease batch in progress
- Completed run rows: 1 / 1,996
- Uploaded and confirmed scan assets: 22 / 953
- Pilot scan confirmations: 1
- Official scan confirmations: 22
- Findings and safety blockers: 20
- Current blockers: none for the January 3 opening-lease batch.
- Next action: complete `SCN-0023` and `SCN-0024`, then execute the January 3 rent receipts.
