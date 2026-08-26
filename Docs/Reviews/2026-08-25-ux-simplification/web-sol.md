The web app already has the APIs and scan-first foundations needed for a much simpler landlord experience. The main problem is presentation: routine actions are exposed with management-company controls, technical record concepts, and long steppers even when only a few values are required.

# Web UX simplification — flow and plumbing — SOL medium

Counting method: a “screen” is a route, dialog, or stepper panel. Tap counts are minimum navigation/action taps and exclude typing, picker selections, external messaging, and conditional corrections. Static-only counts are labeled Assumed.

## The landlord's day (10 lines): what the app currently makes them do

1. They land on a “Command center” containing a briefing, setup card, money snapshot, portfolio pulse, four metric cards, messages, work orders, and activity—not one ranked list of today’s work.
2. They may face up to 26 navigation destinations, including Owners, Banking, Deposits, Reports, Lease Templates, Appointments, Vendors, Team routing, Team, and Activity history.
3. To see who owes rent, they move from the dashboard to a past-due page and then open a payment dialog.
4. To record a receipt manually, they cross seven form steps despite only three required values.
5. To report a repair themselves, they cross six steps covering triage, scheduling, people, and budget before the work order exists.
6. To reply to a tenant, they are asked on every message to understand and choose among Portal, Email, and Text delivery.
7. To advertise a vacancy, they manage listing copy, photos, publishing status, confirmations, URLs, external status, “Zillow Guided,” and “Zillow Connected,” then still publish in Zillow.
8. To screen an applicant, they must choose between Integrated and External workflows and may encounter provider-report and consumer-reporting-agency administration.
9. To create a lease, they meet templates, dynamic fields, render modes, revisions, signer roles, signing order, accounts, and rent-tracking start modes.
10. To move someone out, they separately plan the ending, return possession, decide every household and portal-access outcome, manage turnover, and close the tenant account.

Verified: product intent at `CLAUDE.md:3-8`; navigation at `web/src/lib/components/AppShell.svelte:111-194`; dashboard composition at `web/src/routes/(protected)/+page.svelte:21-36,121-179,225-375`.

## Workflow-by-workflow

### Onboard

- Today: 9 screen states / at least 9 navigation taps / approximately 24 validated values / concepts: portfolio, management company, legal owner record, owner type, property versus unit, lease import, alert channels. Verified: `/get-started`, `/onboarding`; seven wizard steps at `web/src/lib/onboarding/wizard-steps.ts:52-139`; the property step contains a second unit panel; property/unit/tenant/lease validations at `web/src/lib/schemas/index.ts:173-262`. Assumed: tap total follows the straight manual path and includes the initial real-versus-sandbox choice.
- Accidental complexity: the wizard separately saves portfolio, owner, property/units, tenants, lease, and notifications—up to six write round-trips (`web/src/routes/(protected)/onboarding/+page.svelte:369-378,504-515,775-800,931-940,1106-1119,1182-1189`). It asks a self-managing landlord to create a separate owner entity and optionally name a management company before adding the first rental. Meanwhile, the existing lease scanner can create the property, unit, tenant, and lease together (`web/src/lib/onboarding/wizard-steps.ts:79-88`).
- Proposal: make “Scan a signed lease” the first and dominant path. For manual setup, ask only “What should we call your rentals?” followed by property/unit and current tenant/lease; create the current user as the default owner server-side. Move legal owner type, tax ID, management-company name, and notification channels to later settings. Before→after: 9 states and 7 concepts → 3 review states and 3 concepts: rentals, address/unit, current lease. Effort M. Risk to management-company use: medium; preserve explicit owner/entity setup under “More ownership details.”

### Add property/unit

- Today: 2 creation panels / at least 2 primary action taps after opening the flow / 10 required fields / concepts: property, “underlying Unit,” single versus multiple rentals, owner entity. Verified: New Property redirects from `/properties` into `/onboarding?step=property` rather than a local form (`web/src/routes/(protected)/properties/+page.svelte:230-233`); six property and four residential unit fields are required (`web/src/lib/schemas/index.ts:173-229`). Editing later becomes a five-step form: Identity, Address, Setup, Operations, Tax basis (`web/src/routes/(protected)/properties/+page.svelte:168-181`).
- Accidental complexity: creating a house still requires understanding why its address and its “underlying Unit” are separate. Editing mixes ordinary identity/address work with owner assignment, management fee, purchase price, land value, in-service date, and manual depreciation (`web/src/routes/(protected)/properties/+page.svelte:531-613`).
- Proposal: present a house as one “Rental” form: address, beds, baths, rent. Internally continue creating the property and unit atomically—the API already accepts both in one setup command (`RentalCommand.Api/Controllers/PropertyController.cs:52-73`). Ask “Does this address have more than one rental?” only when adding a duplex/building. Move ownership, operations, and tax basis into collapsed sections on the finished property. Before→after: 2 creation panels/10 fields/4 concepts → 1 review screen/9 visible fields/1 concept. Effort M. Risk: low; no underlying data is removed.

### Find a tenant — listing

- Today: 1 large app screen plus Zillow / at least 7 taps from staff navigation through publication tracking / 3 fields required to save / 17 listing and publication controls, plus per-photo actions / concepts: listing workspace, content version, guided publication, connected publication, external status, republishing signals. Verified: `/properties` → property → unit → Listing; form model and minimum save validation at `web/src/lib/components/unit/tabs/ListingTab.svelte:15-40`; listing copy and photos at `:203-235`; Zillow Guided and Connected cards at `:239-297`.
- Accidental complexity: the landlord manually publishes in Zillow but must also reproduce Zillow’s workflow state inside Rental Command: five publication statuses, three confirmation checkboxes, three external identifiers/status fields, and “Save as published.” An unavailable Connected integration still receives its own full card (`ListingTab.svelte:239-292`).
- Proposal: after “Prepare listing,” show one review sheet containing headline, description, rent, deposit, and photos, then one primary “Copy listing and open Zillow” action. Hide publication bookkeeping, URLs, republish signals, and all unavailable connected-provider controls under “Posting details”; automatically infer “ready” after generation and let the landlord mark only “Posted” or “No longer available.” Before→after: 17 controls and two provider models → 5 main fields and one posting action. Effort M. Risk: medium; management companies retain provider tracking under “Posting details.”

### Find a tenant — application

- Today: 4 surfaces / at least 5 app taps across landlord and applicant / 5 applicant-required fields / concepts: reusable application link, listing context, application status. Verified: staff list and link dialog at `/applications` (`web/src/routes/(protected)/applications/+page.svelte:99-145,217-312`); public `/apply/[token]` has five required validations—name, email, phone, consent—at `web/src/routes/apply/[token]/+page.svelte:192-201`; approval occurs in the application detail.
- Accidental complexity: the generic “Get application link” is disconnected from the vacancy unless the route carries listing context. The public form exposes roughly 14 personal, address, employment, income, and preference inputs even though only five are required (`web/src/routes/apply/[token]/+page.svelte:49-63`). Scan autofill already supplies name, birth date, address, employer, and income (`:113-147`).
- Proposal: put “Share application” directly on each vacant unit and automatically preselect that unit. On the public form, show the five required values first and place employment, income, address history, and notes under “Add more information”; keep Scan ID prominent. Before→after: generic link then unit explanation → unit-specific link with one clear application. Effort S. Risk: low; portfolio-wide links can remain under Applications.

### Find a tenant — screening

- Today: 1 application-detail screen plus a provider / 2–6 action taps / 0 new required fields for the integrated invitation path versus at least provider/report details for external screening / concepts: Integrated versus External, screening service, provider reference, consumer report, reporting agency, adverse action. Verified: `web/src/lib/components/records/ApplicationDetail.svelte:769-925`; adverse-action handling at `:930-978`.
- Accidental complexity: the routine decision is framed first as a systems-integration choice. The External path shows service, reference, link, reporting-agency name, phone, and address on the applicant’s main workflow.
- Proposal: present one primary “Request screening” action. If a provider is connected, use it; otherwise say “Record a screening completed elsewhere.” Put provider references and reporting-agency contact in a collapsed “Report details” section, opening it automatically only when legally required for adverse action. Before→after: choose infrastructure, then screen → choose screening result; provider mechanics stay underneath. Effort M. Risk: medium; compliance details remain available and become mandatory when the outcome requires them.

### Lease — template and e-sign

- Today: at least 3 surfaces / at least 7 action taps for first-time setup and sending / 2 template-upload requirements, then lease-term values plus name and email for every signer / concepts: template library, dynamic fields, field placement, render mode, default template, agreement revision, signer role, signing order. Verified: `/lease-templates` upload and activation at `web/src/routes/(protected)/lease-templates/+page.svelte:69-124,215-299`; designer, render metadata, and Dynamic Fields at `:365-455`; signer ordering and send confirmation at `web/src/lib/components/leases/AgreementDraftDialog.svelte:427-499`.
- Accidental complexity: first-time lease creation exposes the template-management system as a primary Rental navigation destination. The landlord must upload, name, place fields, activate a default, then encounter role/order controls for ordinary tenant signatures.
- Proposal: move Lease Templates out of primary Rentals navigation and into lease settings. First use becomes “Upload the lease form you already use”; suggest field placement and ask the landlord to confirm it once. Thereafter, “Create and send lease” should show dates, rent, deposit, tenant names/emails, and a document preview. Default signer role and order; reveal them only for multiple signers or explicit changes. Before→after: template administration plus draft administration → one lease review and one send confirmation. Effort L. Risk: medium; full designer and revision controls remain in settings.

### Move-in

- Today: 3 preparation panels plus a later possession-confirmation dialog / at least 5 action taps / about 12 core or conditional validations / concepts: planned tenant relationship, tenant account, agreement draft, rent-tracking start mode, deposit subledger, opening balance. Verified: three panels at `web/src/lib/components/applications/PrepareMoveInDialog.svelte:65-85`; single preparation mutation at `:299-315`; rent-start choices and ledger wording at `:758-800`; deposit account, opening balance, and atomic-preparation summary at `:881-960`. Actual possession is a separate atomic endpoint at `RentalCommand.Api/Controllers/LeaseManagementController.cs:747-810`.
- Accidental complexity: approved application, unit, market rent, and move date are already available, but the landlord still crosses three panels and sees migration/accounting controls such as Backfill, Custom cutoff date, deposit subledger, and opening balance.
- Proposal: collapse preparation into one “Review move-in” screen: tenant, rental, lease dates, rent, due day, deposit. Default rent charging to the current/start date as appropriate, create deposit tracking automatically when deposit is nonzero, and put backfill/opening balance under “Moving an existing lease into Rental Command?” Keep “Keys handed over” as a later one-tap confirmation because it is a separate real-world event. Before→after: 3 preparation steps and 6 system concepts → 1 review plus later handoff, using landlord concepts only. Effort M. Risk: medium; migration controls remain under the explicit existing-lease exception.

### Collect rent / see who owes

- Today: 3 surfaces / 3 primary taps / 3 required payment fields / concepts: past due, open charges, oldest-first versus specific-charge allocation. Verified: dashboard performs separate portfolio and accounting queries (`web/src/routes/(protected)/+page.svelte:21-28`) and links overdue money onward; Record Payment requires amount, date, and method (`web/src/lib/components/accounting/RecordPaymentSheet.svelte:91-99`) while showing eight controls plus allocation and attachment (`:125-207`).
- Accidental complexity: “Who owes me?” is a headline number before it becomes an actionable list. The ordinary payment case asks the landlord to understand charge allocation even though “oldest first” is already the default.
- Proposal: make the dashboard’s overdue section a short ranked tenant list with amount and “Record payment” on each row. In the dialog show amount, date, method; default oldest-first and hide reference, payer, note, specific-charge allocation, and attachment under “More details.” Before→after: dashboard → report → dialog → dashboard row → compact dialog. Effort S. Risk: low; accounting staff can expand allocation details.

### Handle maintenance

- Today: `/maintenance` plus 6 dialog panels / 7 primary taps / 4 required fields / 12 controls / concepts: work order, triage, arrival window, technician access, tenant assignment, vendor assignment, budget. Verified: six steps at `web/src/routes/(protected)/maintenance/+page.svelte:186-201`; required property/title/description/category at `web/src/lib/schemas/index.ts:334-350`; dialog and five Next actions at `web/src/routes/(protected)/maintenance/+page.svelte:930-1080`.
- Accidental complexity: a repair cannot exist until the landlord walks through scheduling, people, and budget—even when nobody has been assigned. Priority and category already default to Normal and General (`web/src/routes/(protected)/maintenance/+page.svelte:159-173`). The tenant portal proves a simpler capture works with title and description on one screen (`web/src/routes/(portal)/portal/maintenance/+page.svelte:37-40,303-353`).
- Proposal: use one “Report a repair” sheet: rental, “What’s wrong?”, details, optional photo. Default Normal/General. Save immediately, then offer “Schedule or assign someone” as the next action on the created repair; keep access instructions, arrival window, vendor, tenant, and estimate on its detail page. Before→after: 6 panels/12 controls → 1 sheet/3 entered values plus optional photo. Effort M. Risk: low; management-company triage becomes post-capture work rather than a creation gate.

### Move-out

- Today: approximately 4 surfaces / at least 6 action taps plus one selection per household member and portal login / required values: ending decision, turnover reason, every party outcome, every access outcome, and account-close reason / concepts: ending disposition, possession, household membership, access grants, turnover period, account closure. Verified: return request demands every party and access choice at `web/src/lib/components/leases/PossessionActions.svelte:173-213`; the dialog explains those decisions at `:344-429`; the API enforces every outcome and turnover reason (`RentalCommand.Api/Controllers/LeaseManagementController.cs:817-875`); closing the account is another dialog at `web/src/lib/components/unit/tabs/LeaseTab.svelte:308-314`.
- Accidental complexity: normal move-out has an obvious default—end the tenancy and tenant logins—but requires an explicit per-record decision. Turnover opens automatically after possession return, yet account closure remains a separate lease action.
- Proposal: retain two real-world moments: “Plan move-out” and “Keys returned.” On key return, preselect “End tenancy and portal access for everyone,” show a one-line summary, and reveal per-person exceptions only for guarantors or transfers. Put the final account-close checklist on the turnover screen after balance/deposit settlement. Before→after: four administrative surfaces → two event-driven surfaces. Effort M. Risk: medium; transfers, retained guarantors, and access exceptions remain expandable.

### Daily loop — what needs my attention today

- Today: 1 dashboard / 0 taps after login / 0 required fields / at least 4 server queries / concepts: Command center, portfolio pulse, AI briefing, getting started, money snapshot, occupancy, net income, recent activity. Verified: independent queries at `web/src/routes/(protected)/+page.svelte:21-36`; overlapping dashboard sections begin at `:121-179` and continue through messages, work orders, and activity.
- Accidental complexity: the screen repeats occupancy, overdue money, net, and work orders in several visual forms. Setup progress and recent activity compete with overdue rent, unread tenant messages, and urgent repairs.
- Proposal: make the top of the dashboard a single ranked “Today” list: overdue rent, new tenant messages, urgent repairs, applications awaiting a decision, leases needing signature, move-ins/outs due. Below it, keep one compact business summary. Collapse setup once the first rental exists; move recent activity to Activity history. Before→after: many equal-weight cards → one priority list plus one summary. Effort M. Risk: low; management-company users receive the same items, filtered by existing capabilities.

### Daily loop — record this receipt/expense

- Today: manual path is `/accounting` plus 7 form panels / 8 primary taps / 3 required fields / 26 controls / concepts: source links, incurred versus paid dates, billable to owner, vendor tax details, receipt proof, tax adjustments. Verified: seven steps and 26 grouped controls at `web/src/routes/(protected)/accounting/+page.svelte:318-335`; only description, positive amount, and incurred date are required (`web/src/lib/schemas/index.ts:273-307`). The scan alternative is `/scan` → `/scan/[draftId]` → Confirm & Create Expense; confirmation at `web/src/routes/(protected)/scan/[draftId]/+page.svelte:2831-2842`.
- Accidental complexity: “Add expense” launches the most exhaustive path even though the product’s flagship is scan → draft → confirm (`CLAUDE.md:5-8`). The scan review can expose record identifiers such as “Rental relationship #,” “Rental account #,” and “Ledger entry #” under linked references (`web/src/routes/(protected)/scan/[draftId]/+page.svelte:128-138,1499-1514`).
- Proposal: make the primary accounting action “Scan receipt”; keep “Enter manually” as a secondary compact sheet containing description, amount, date, and optional property. Move the remaining 22 controls under categorized “More details.” Replace linked numeric references in scan review with human labels such as property address and tenant name—or hide them when context is already clear. Before→after: 7 panels/26 controls → scan-and-confirm or 1 compact manual sheet. Effort S for manual simplification, M for resolving human context labels. Risk: low; advanced receipt metadata remains editable.

### Daily loop — reply to a tenant

- Today: 1 split-screen route / 2 taps—open thread and send / 1 required field / concepts: conversation and delivery channels. Verified: `/messages` loads list and selected thread separately (`web/src/routes/(protected)/messages/+page.svelte:40-118`); reply is one mutation (`:169-221`); Portal, Email, and Text checkboxes appear on every reply (`:473-514`).
- Accidental complexity: saved delivery defaults already exist, but every reply still displays three channel decisions. “Portal” is an implementation destination, not the landlord’s intent.
- Proposal: show the reply box and “Send.” Beneath it, display quiet confirmation such as “Delivered in the tenant app and by their saved contact method”; move channel overrides under a Delivery menu. Before→after: message plus three visible delivery choices → message plus one send action. Effort S. Risk: low; channel overrides remain available.

## The three worst offenders

1. **Manual expense entry — seven panels for three required values.** Single highest-impact change: make Scan receipt primary and replace the manual stepper with one compact fallback sheet. Verified: `web/src/routes/(protected)/accounting/+page.svelte:318-335`; `web/src/lib/schemas/index.ts:273-307`.
2. **Maintenance creation — six panels before a repair can exist.** Single highest-impact change: save a repair from rental/problem/details, then schedule and assign it afterward. Verified: `web/src/routes/(protected)/maintenance/+page.svelte:186-201,930-1080`.
3. **Vacancy listing — Rental Command duplicates the external publisher’s workflow.** Single highest-impact change: reduce the main path to review copy/photos and “Copy listing and open Zillow”; hide provider state tracking. Verified: `web/src/lib/components/unit/tabs/ListingTab.svelte:203-297`.

These are the strongest first approvals because they remove 16 stepper panels or provider decisions without changing the underlying accounting, maintenance, or listing models.

## Jargon to rename

| Current term | Plain English | Where it appears |
|---|---|---|
| Command center | Today | Dashboard, `web/src/routes/(protected)/+page.svelte:129` |
| Portfolio pulse | Today’s summary | Dashboard, `web/src/routes/(protected)/+page.svelte:153-169` |
| Portfolio | My rentals / rental business | Onboarding, reports, dashboard; `web/src/lib/onboarding/wizard-steps.ts:52-64` |
| Guided Setup | Add your rentals | Navigation and dashboard; `web/src/lib/components/AppShell.svelte:114-117` |
| Work Order | Repair | Navigation and maintenance; `web/src/lib/components/AppShell.svelte:149-157` |
| Listing workspace | Rental listing | Unit Listing tab, `web/src/lib/components/unit/tabs/ListingTab.svelte:160-167` |
| Zillow Guided | Post on Zillow yourself | Listing tab, `web/src/lib/components/unit/tabs/ListingTab.svelte:239-267` |
| Zillow Connected | Automatic Zillow posting | Listing tab; hide when unavailable, `web/src/lib/components/unit/tabs/ListingTab.svelte:269-292` |
| Tenant & lease relationship | Tenant and lease | Move-in dialog, `web/src/lib/components/applications/PrepareMoveInDialog.svelte:351-357` |
| Begin rent charges | When should rent tracking start? | Move-in, `web/src/lib/components/applications/PrepareMoveInDialog.svelte:758-780` |
| Tenant ledger | Charges and payments | Payment and move-in copy; `web/src/lib/components/accounting/RecordPaymentSheet.svelte:167-195` |
| Open charges | Unpaid charges | Record Payment, `web/src/lib/components/accounting/RecordPaymentSheet.svelte:167-195` |
| Security-deposit account / subledger | Track this deposit separately | Move-in, `web/src/lib/components/applications/PrepareMoveInDialog.svelte:881-892` |
| Opening balance | Amount already owed or credited | Move-in, `web/src/lib/components/applications/PrepareMoveInDialog.svelte:894-944` |
| Dynamic Fields | Information Rental Command fills in | Lease Templates, `web/src/routes/(protected)/lease-templates/+page.svelte:435-440` |
| Render mode | Hide; this is template plumbing | Lease Templates, `web/src/routes/(protected)/lease-templates/+page.svelte:404-416` |
| Rental relationship # | Tenant/lease name | Scan linked references, `web/src/routes/(protected)/scan/[draftId]/+page.svelte:128-138` |
| Rental account # | Tenant balance | Scan linked references, same location |
| Ledger entry # | Charge or payment description | Scan linked references, same location |
| Active portal grant | Tenant app access | Move-out, `web/src/lib/components/leases/PossessionActions.svelte:394-429` |

“Operation key,” access context, capability scopes, and idempotency keys are currently plumbing rather than visible form labels. Keep them invisible; they should never become landlord concepts.

## Keep — intrinsic complexity that must stay, and why

- Property address, unit identity, beds/baths, and rent: required to identify what is being managed and advertised.
- Application consent and verified applicant contact: necessary for a valid application and follow-up.
- Screening consent, report provenance, and adverse-action information: consumer-reporting obligations cannot be replaced with a casual approve/decline control.
- Lease dates, rent, deposit, parties, document preview, and signature identity: these define the legal agreement.
- A separate “keys handed over” event: possession may occur after signing and affects occupancy, deposit, and rent accounting.
- Payment amount, received date, and method: essential financial evidence. Specific-charge allocation should remain available for exceptions.
- Maintenance location, problem description, urgency, and safe-entry instructions: required for safety and correct dispatch, though not all must block initial capture.
- Planned move-out versus actual return of keys: they occur at different times and have different consequences.
- Deposit settlement, unpaid balance review, tenant access termination, and account closure: necessary safeguards at move-out.
- Scan draft review: extraction can be wrong; “computer types, human confirms” is the correct simple surface.
- Atomic API commands and idempotency keys: essential correctness underneath the surface. The property/unit setup and move-in endpoints already support consolidating UI steps without weakening transaction safety (`RentalCommand.Api/Controllers/PropertyController.cs:52-73`; `RentalCommand.Api/Controllers/LeaseManagementController.cs:170-273`).

## Not covered

- Runtime rendering, mobile ergonomics, focus order, visual density, and actual touch-target reachability; the app and browser were not run.
- Measured network latency or production query timing.
- The admin, super-admin, owner-reporting, and full tenant portal experiences, except where the portal provided a direct simplicity comparison.
- The behavior of Zillow, screening, e-signature, SMS, email, or other external providers.
- Jurisdiction-specific lease, screening, deposit, notice, or move-out legal review.
- Usage analytics showing which features landlords actually use.
- A complete API contract audit; endpoints were inspected only where they demonstrated that a simpler UI can reuse existing atomic operations.
- No repository files, branches, worktrees, builds, app processes, or browser sessions were created or changed.
