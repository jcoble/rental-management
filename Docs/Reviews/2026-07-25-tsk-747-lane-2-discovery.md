# TSK-747 Lane 2 Discovery — Work, Communication, Notifications, and Tenant Portal

**Status:** `DONE_WITH_CONCERNS`
**Owner boundary:** Lane 2 only
**Discovery date:** 2026-07-25
**Production edits:** None

## Evidence and limits

- The in-app Browser plugin was unavailable (`Browser is not available: iab`), so the audit used the user-approved Chrome plugin against `https://rental-command.chimp-map.ts.net/`.
- Every browser-reviewed desktop route was measured at an actual `window.innerWidth = 1920` and `window.innerHeight = 1080`, not merely a nominal browser window size.
- `/settings/notifications/tenant-notices` also received a focused `390 × 844` containment check. It had no horizontal overflow, but the repeated fully expanded forms still produced an excessive vertical wall and awkwardly wrapped mode choices.
- The signed-in preview identity was a staff administrator. Technician-only routes silently redirected to `/`; tenant-portal routes also redirected to `/` because the portal layout requires the Tenant experience. Those routes are marked **blocked + source**, never visually approved.
- `/messages/[id]` had no seeded conversation to prove the record-specific state. Its route wrapper and shared page were inspected in source, but the detail state is marked blocked.
- The route review covered visual hierarchy, wording, navigation, control consistency, progressive disclosure, help, and loading/error/empty states. It did not exercise destructive actions or save mutations.

## Checklist legend

- `[x] browser` — visually inspected in the live Azure preview at the stated viewport.
- `[x] source` — implementation and state branches inspected in source.
- `[!] blocked` — the required role/data state could not be reached; no visual claim is made.
- `V` — visual hierarchy and readability.
- `W` — plain-English wording.
- `N` — navigation and record context.
- `C` — control/component consistency.
- `P` — progressive disclosure.
- `H` — contextual help destination.
- `S` — loading, error, and empty states.

## Per-route audit checklist

| Route | Proof | V / W / N / C / P / H / S |
|---|---|---|
| `/maintenance` | [x] browser; [x] source | **V:** Work orders, inspections, and checklist templates form three long peer sections. **W:** The subtitle is understandable, but the page mixes “requests,” “execution,” and “compliance checks” without first explaining the three jobs. **N:** Strong: selecting work order 18 opened `/units/21?tab=maintenance&view=work-orders&wo=18`, preserving Unit Command Center context. **C:** List filters use shared selects. **P:** Creation already uses steppers, but the page itself lacks Work orders / Inspections / Recurring task navigation. **H:** Add links to `/docs/maintenance-and-work-orders` and `/docs/inspections`. **S:** Loading and empty branches exist. |
| `/maintenance/[id]` | [x] browser at `/maintenance/18`; [x] source | **V:** Technician, request facts, six status actions, vendor contact, costs, timeline, files, and history are all expanded. The next action is not visually dominant. **W:** “Request” and “Costs & timing” are mostly clear; workflow terms need a one-line explanation. **N:** A Unit link exists, but the breadcrumb says only “Work Orders”; make “Back to Unit Command Center · Maintenance” explicit. **C:** “Choose a technician” is a native `<select>` unlike shared app controls. **P:** Keep overview and next action open; collapse assignment, vendor/cost, files, and history. **H:** Link to `/docs/maintenance-and-work-orders`. **S:** Loading and error states exist. |
| `/maintenance/inspections/[id]` | [x] browser at `/maintenance/inspections/7`; [x] source | **V:** A completed inspection exposes all 18 checklist rows and every Pass / Fail / N/A control at once. **W:** Raw `MoveIn inspection` is not humanized; the summary counts use “N/A” and “TO DO” without explanation. **N:** Property and Unit links are present but Unit Command Center return is not prominent. **C:** Completed rows still look editable even when read-only. **P:** Default to “Issues to review”; group rows by room and collapse passed items. **H:** Link to `/docs/inspections`. **S:** Loading/error branches exist; completed/read-only state needs clearer styling. |
| `/maintenance/recurring` | [x] browser; [x] source | **V:** The empty table is calm, but column labels compete before the user understands recurring work. **W:** The empty copy uses odd capitalization in “every Quarter”; headers such as “Context” and “Next” are vague. **N:** It reads as an isolated page rather than part of Work. **C:** Shared Select and remote record controls are used. **P:** Creation details should remain staged rather than exposed together. **H:** Link to `/docs/maintenance-and-work-orders`. **S:** Loading and empty states exist. |
| `/appointments` | [x] browser in Calendar and List views; [x] source | **V:** Calendar/list toggle and legend are readable; list view is easier to scan for beginners. **W:** “Showings, move-ins, inspections, and service visits” is clear. **N:** Rows open appointment details directly; Unit context should be retained where the appointment is unit-linked. **C:** Shared selects and remote record selects are used. **P:** Strong existing pattern: creation already uses Details / Schedule / People steps. **H:** Link to `/docs/appointments`. **S:** Loading and empty branches exist. |
| `/appointments/[id]` | [x] browser at `/appointments/21`; [x] source | **V:** Actions and “What & when” are clear; the “Record” card gives low-value metadata equal weight. **W:** Main labels are plain; all-caps CREATED / UPDATED feels database-oriented. **N:** Related property/unit context should return through Unit Command Center when available. **C:** Shared record controls are used. **P:** Move created/updated metadata into a collapsed Activity section. **H:** Link to `/docs/appointments`. **S:** Loading/error branches exist. |
| `/vendors` | [x] browser; [x] source | **V:** Compact list is easy to scan. **W:** “1099/W-9 compliance” assumes tax knowledge. **N:** Row navigation to vendor detail works. **C:** Creation already uses a stepper and shared address/state controls. **P:** Keep tax fields out of the primary list unless action is needed. **H:** Link “What is a W-9?” to `/docs/taxes-and-1099`. **S:** Loading/empty states exist. |
| `/vendors/[id]` | [x] browser at `/vendors/7`; [x] source | **V:** Scorecard and details are orderly, but tax fields are given equal weight to contact and service information. **W:** “From the text-out to their DONE reply” is internal workflow language; “1099 eligible,” “W-9 on file,” and “Tax ID” need plain explanations. **N:** Back-to-list works; related open work should be more discoverable. **C:** Detail presentation is consistent. **P:** Put tax paperwork in a collapsed “Tax paperwork” section unless action is required. **H:** Link to `/docs/taxes-and-1099`. **S:** Loading/error branches exist. |
| `/my-work` | [!] blocked by staff role; [x] source | **V:** Source uses repeated assignment cards. **W:** Status filters expose workflow names without a beginner summary. **N:** Silent redirect to `/` gives no explanation. **C:** Technician status filter uses a native `<select>`. **P:** Cards expose similar fields repeatedly. **H:** Use `/docs/maintenance-and-work-orders`. **S:** Shared technician loading/error/empty branches exist in source. |
| `/my-work/[id]` | [!] blocked by staff role; [x] source | **V:** Visit details, permitted contact, progress, field log, and conversation are stacked. **W:** “Permitted contact” and log entry types need examples. **N:** Silent redirect prevents a user from learning which role is required. **C:** Progress status and entry type use native `<select>` controls. **P:** Keep current assignment and next action open; collapse history/log. **H:** Use `/docs/maintenance-and-work-orders`. **S:** Loading/error branches exist in source. |
| `/my-schedule` | [!] blocked by staff role; [x] source | **V:** Shares the assignment-list presentation. **W:** Beginner guidance is absent from the wrapper. **N:** Silent redirect to `/`. **C:** Shared component contains a native status `<select>`. **P:** Source repeats card content rather than grouping by day/next visit. **H:** Use `/docs/appointments`. **S:** Shared loading/error/empty branches exist in source. |
| `/assignment-inbox` | [!] blocked by staff role; [x] source | **V:** Shares assignment cards. **W:** “Assignment inbox” does not explain that these are jobs waiting for acceptance/attention. **N:** Silent redirect to `/`. **C:** Native status `<select>` in shared component. **P:** Needs a default “needs your decision” view. **H:** Use `/docs/maintenance-and-work-orders`. **S:** Shared loading/error/empty branches exist in source. |
| `/messages` | [x] browser; [x] source | **V:** The split-pane empty state is visually clean. **W:** “Chat with your tenants” is clear, but it does not explain how a first conversation is started or who is eligible. **N:** New conversation is discoverable; no Unit context is shown until a thread exists. **C:** Compose uses shared Select. **P:** Appropriate for the empty state. **H:** Link to `/docs/tenant-portal-overview` for what tenants can see. **S:** Loading/error/empty branches exist. |
| `/messages/[id]` | [!] blocked by absent seeded conversation; [x] source | **V:** No record-specific state could be proved. **W:** The generic empty shell appeared rather than “Conversation not found.” **N:** The route is only a wrapper around the messages page. **C:** Shared message controls. **P:** Source reuses split-pane detail. **H:** Use `/docs/tenant-portal-overview`. **S:** Add explicit missing-conversation feedback and a Back to messages action. |
| `/notices` | [x] browser; [x] source | **V:** The empty page is orderly. **W:** “Lease lifecycle drafts” and “Generate drafts” are system-oriented. Prefer “Notices ready for review” and “Create notices that are due.” **N:** The relationship to notification automation is not explained. **C:** Shared Select and HelpPopover are used. **P:** Drafts appropriately remain a review queue. **H:** Existing help popover should link to `/docs/notices`. **S:** Loading/error/empty branches exist. |
| `/settings/notifications/my-alerts` | [x] browser; [x] source | **V:** Four equal channel cards are readable but feel like an orphan settings page. **W:** “In-app,” “Mobile push,” “SMS,” and “This is not browser web push” are technical. **N:** Back link works, but there is no guided continuation to team responsibilities. **C:** Shared cards/checkboxes. **P:** Small enough to remain open; it should be Step 1 of 3 with a human summary. **H:** Verified dialog links to `/docs/settings-and-notifications`. **S:** Loading/error and disabled-phone states exist. |
| `/settings/notifications/team-routing` | [x] browser; [x] source | **V:** Daily summary settings plus six equal routing cards create a settings matrix. Editing appends another large editor below the cards. **W:** “In-scope properties,” “named recipients,” and “active Workspace Administrators” are internal role language. **N:** No previous/next journey. **C:** Send hour is a numeric field with “0–23 in America/New_York,” not a human time control. **P:** Show responsibility rows with summaries; expand one editor at a time. **H:** Link to `/docs/settings-and-notifications` and `/docs/daily-briefing`. **S:** Loading/error branches exist. |
| `/settings/notifications/tenant-notices` | [x] browser at `1920 × 1080` and focused `390 × 844`; [x] source | **V:** Five fully expanded automation forms produce the lane’s worst wall of controls and copy. **W:** “Lead days,” “effective primary tenant,” “immutable workspace version,” “merge fields,” “durable delivery queue,” and provider retry terms are not customer language. **N:** No journey, completion state, or summary explains the relationship to My alerts and Team responsibilities. **C:** Shared Select is used, but hour is numeric and mode segments wrap awkwardly at phone width. **P:** Each automation must be a collapsed summary with one open editor; technical failure behavior belongs under Advanced. **H:** Link to `/docs/notices`, `/docs/delivery-channels`, and `/docs/settings-and-notifications`. **S:** Loading/error/empty delivery branches exist; recent delivery technical data overwhelms the primary task. |
| `/portal` | [!] blocked by Tenant experience guard; [x] source | **V:** Source stacks notifications, balance, payments, lease, maintenance request form, appointments, and messages on one dashboard. **W:** Most tenant copy is plain. **N:** Dashboard links to focused portal pages. **C:** Account chooser is a native `<select>`. **P:** The full maintenance request form should open from a primary action rather than remain in the dashboard wall. **H:** Link to `/docs/tenant-portal-overview`. **S:** Source includes loading/validation branches for major sections. |
| `/portal/account` | [!] blocked by Tenant experience guard; [x] source | **V:** Simple account hub. **W:** Labels are generally clear. **N:** Links to Profile and Security. **C:** Card links are consistent. **P:** Appropriate hub disclosure. **H:** Use `/docs/tenant-portal-overview`. **S:** No data-dependent state is central to this page. |
| `/portal/appointments` | [!] blocked by Tenant experience guard; [x] source | **V:** Source list is straightforward. **W:** Tenant-facing appointment language is understandable. **N:** Focused route exists from portal. **C:** Shared presentation. **P:** Appropriate list/detail density. **H:** Use `/docs/appointments`. **S:** Loading/error/empty branches exist in source. |
| `/portal/lease` | [!] blocked by Tenant experience guard; [x] source | **V:** Lease facts and documents are grouped. **W:** Mostly plain, though agreement/status terms need short explanations. **N:** “Ask about this lease” connects to communication. **C:** Shared cards/actions. **P:** Legal/document detail can remain behind sections. **H:** Link to `/docs/leases` and `/docs/lease-agreement-and-signing`. **S:** Loading/error/empty branches exist in source. |
| `/portal/maintenance` | [!] blocked by Tenant experience guard; [x] source | **V:** Requests and creation are focused compared with the dashboard. **W:** “Maintenance request” is clear. **N:** Focused route is discoverable from portal. **C:** Shared Select is used. **P:** Create form can be staged into Problem / Location / Photos / Review for first-time tenants. **H:** Use `/docs/maintenance-and-work-orders`. **S:** Loading/error/empty branches exist in source. |
| `/portal/messages` | [!] blocked by Tenant experience guard; [x] source | **V:** Familiar conversation layout. **W:** Tenant wording is generally clear. **N:** Focused route exists. **C:** Shared message controls. **P:** Appropriate split/list disclosure. **H:** Use `/docs/tenant-portal-overview`. **S:** Loading/error/empty branches exist in source. |
| `/portal/notifications` | [!] blocked by Tenant experience guard; [x] source | **V:** Simple list. **W:** Tenant-facing labels are concise. **N:** Focused route exists. **C:** Presentation is consistent. **P:** Appropriate list density. **H:** Use `/docs/delivery-channels`. **S:** Confirmed source defect: loading and failure are indistinguishable from “No notifications yet”; add explicit loading and error branches. |
| `/portal/payments` | [!] blocked by Tenant experience guard; [x] source | **V:** Payment information is grouped. **W:** Main labels are understandable. **N:** Focused route exists. **C:** Account chooser is a native `<select>` unlike shared app controls. **P:** Payment detail/actions should remain focused. **H:** Use `/docs/recording-payments`. **S:** Loading/error/empty branches exist in source. |
| `/portal/profile` | [!] blocked by Tenant experience guard; [x] source | **V:** Simple profile hub. **W:** Labels are clear. **N:** Links to personal details and security are explicit. **C:** Card links are consistent. **P:** Appropriate hub disclosure. **H:** Use `/docs/tenant-portal-overview`. **S:** No complex data state is central. |
| `/portal/security` | [!] blocked by Tenant experience guard; [x] source | **V:** Wrapper delegates to the account security component. **W:** Source follows account-security terminology. **N:** Return path should stay inside tenant Account. **C:** Shared security component. **P:** Security actions should remain separated by risk. **H:** Use `/docs/tenant-portal-overview` until a dedicated security article exists. **S:** Component states exist in shared implementation but were not visually proved here. |
| `/portal/unlinked` | [!] blocked by Tenant experience guard; [x] source | **V:** Plain explanatory state. **W:** Copy explains that no tenant account is connected. **N:** The signed-in staff role cannot prove this tenant edge state. **C:** Simple actions. **P:** Appropriate single-purpose state. **H:** Link to `/docs/tenant-portal-overview`. **S:** This route is itself the unlinked empty state. |

## Top five evidenced failures

### 1. Notification setup is three disconnected settings pages instead of one understandable journey

The product currently asks a beginner to infer the relationship among personal channels, team responsibility, and tenant notices. `/settings/notifications/team-routing` is a matrix of cards plus a second editor appended below it. `/settings/notifications/tenant-notices` is five near-identical forms expanded at once. There is no visible progress, summary, review, or “what happens next.”

**Repair:** Keep the three canonical routes and persistence contracts, but wrap them in one guided three-step shell:

1. **How should Rental Command reach you?**
2. **Who should handle each kind of work?**
3. **Which tenant messages should Rental Command prepare?**

Each route shows `Step n of 3`, Back/Next controls, a saved summary, and the same help affordance. A non-administrator sees Step 1 plus a plain explanation that an administrator manages team and tenant settings.

### 2. Internal implementation language has escaped into customer-facing copy

Confirmed examples include:

- `MoveIn inspection`, `AnnualSafety`, and `TO DO`
- “Lease lifecycle drafts”
- “1099 eligible” and “W-9 on file” without explanation
- “From the text-out to their DONE reply”
- “Lead days” and “Local send hour 0–23”
- “effective primary tenant,” “effective Lease Management parties,” and “in-scope properties”
- “immutable workspace version,” “merge fields,” “durable delivery queue,” and provider acceptance/retry/failure language

**Repair:** Make the first sentence answer what the setting does for the landlord. Keep legal or delivery diagnostics under named Help or Technical details disclosures.

### 3. Work pages stack independent jobs and historical detail with no default next action

`/maintenance` stacks work orders, inspections, and checklist templates. A completed inspection exposes all checklist rows and all result controls. Work-order detail gives request facts, actions, assignment, vendor contact, costs, timeline, documents, and history equal visual weight.

**Repair:** Make Work a small hub with **Requests / Inspections / Recurring tasks**. Put checklist templates inside Inspections. On detail pages, show identity, status, “What needs attention,” and the next safe action first; move assignment, vendor/cost, passed inspection items, documents, and history into labeled accordions.

### 4. Record context is inconsistent even though the correct Unit-centered pattern already exists

The maintenance list correctly sends work order 18 to `/units/21?tab=maintenance&view=work-orders&wo=18`. Direct work-order, inspection, and appointment detail routes do not make the same Unit Command Center return path prominent. Role-blocked staff and portal routes silently redirect to `/`, and a missing message detail falls back to a generic empty shell.

**Repair:** Preserve and extend the proven Unit folding. For unit-linked records, show **Back to [unit] Command Center · [tab]**. Role blocks need an explanatory state or permission-safe destination rather than a silent redirect. Missing records need a specific “not found or unavailable” state.

### 5. Native controls, weak help, and ambiguous states break the app’s design language

Native `<select>` controls remain in `WorkOrderDetail.svelte`, `TechnicianAssignmentList.svelte`, `my-work/[id]`, the portal dashboard, and portal payments. Most work pages provide no contextual Help link. Portal notifications render “No notifications yet” for loading and failure as well as a true empty result.

**Repair:** Use the existing shared `Select` or `RemoteRecordSelect` components, use published help articles, and give loading/error/empty states distinct copy and recovery actions.

## Proposed notification journey and exact copy direction

### Shared journey shell

| Current area | Journey step | Page summary |
|---|---|---|
| My alerts | **1. How should Rental Command reach you?** | “Choose where you want personal alerts. These choices do not change what your team or tenants receive.” |
| Team routing | **2. Who should handle each kind of work?** | “Choose the people responsible for rent, leasing, repairs, and account alerts.” |
| Tenant notices | **3. Which tenant messages should Rental Command prepare?** | “Choose which reminders Rental Command should prepare, when they are due, and who reviews them before delivery.” |

Preserve the three route-backed save operations. The shell is navigation and comprehension, not a new combined transaction or API.

### Step 1 — personal channels

| Current label | Plain-English label |
|---|---|
| In-app | **In Rental Command** |
| Mobile push | **Phone app** |
| Email | **Email** |
| SMS | **Text message** |
| This is not browser web push | Remove from primary UI; explain supported destinations in Help |

Show the real destination next to each choice, such as `jesse@example.com`. When a phone is missing, say **Add a mobile number in Profile to receive text messages** with a direct Profile link. End with a sentence such as **We’ll notify Jesse in Rental Command, on signed-in phones, and by email.**

### Step 2 — team responsibilities

- Rename **Team routing** to **Team responsibilities**.
- Rename **Morning Briefing** to **Daily summary**.
- Replace **Local send hour: 8 (0–23 in America/New_York)** with a human time control: **Send at 8:00 AM · Eastern Time**.
- Rename **Send all-clear days** to **Send the summary even when there is nothing to do**.
- Replace six equal cards with six compact responsibility rows. Each row shows a human summary and expands one editor at a time.
- Replace **All in-scope properties** with **Every rental** or a named property summary.
- Replace **No named recipient is assigned; active Workspace Administrators receive this topic** with **No one assigned — administrators will receive these alerts.**

### Step 3 — tenant messages

Collapsed rows:

- **Rent is due soon**
- **Rent is late**
- **Offer a lease renewal**
- **Offer month-to-month**
- **Lease will end**

Each collapsed summary should read like:

> Prepare for review · 5 days before · 9:00 AM · Tenant portal + email · Primary tenant and other tenants on the lease

Only one automation opens at a time. Its editor uses these four sections:

1. **What should Rental Command do?** — Off / Prepare a draft / Send after required review
2. **When should it prepare the message?** — “5 days before rent is due” plus a human time selector
3. **Who should receive it?** — Primary tenant / Other tenants on the lease / Eligible guarantors / Other occupants
4. **Where should it be sent?** — Tenant portal / Phone app / Email / Text message
5. **Review the message** — Subject, body, supplied starting template, and legal-review warning

Move failure handling under **Advanced delivery settings**:

- **Pause and ask me**
- **Try again, then keep a draft**
- **Try again, then mark it failed**

Rename **Recent delivery** to **Recent tenant messages**. Provider IDs, retry counts, and immutable version information belong in **Technical details**, not in the primary setup flow.

### Published help destinations

All of these slugs are present under `RentalCommand.Api/KnowledgeBase`; no placeholder help URLs are needed:

- `/docs/settings-and-notifications`
- `/docs/delivery-channels`
- `/docs/daily-briefing`
- `/docs/notices`
- `/docs/maintenance-and-work-orders`
- `/docs/inspections`
- `/docs/appointments`
- `/docs/taxes-and-1099`
- `/docs/tenant-portal-overview`
- `/docs/leases`
- `/docs/lease-agreement-and-signing`
- `/docs/recording-payments`

## Proposed work and communication organization

### Work hub

- `/maintenance` becomes a stable Work hub with **Requests / Inspections / Recurring tasks**. The active segment is URL-backed.
- Requests default to **Needs attention**, with secondary views for Scheduled, In progress, and Completed.
- Inspections default to inspections that are due or contain issues. Checklist templates are a secondary action inside this segment.
- Recurring tasks keep their existing route and creation behavior but use plain labels: Task, Rental, Repeats, Next work order, Expected cost, Priority, On/Off.

### Record details

- Work order: overview and next action first; accordions for Schedule and assignment, Vendor and cost, Files, and History.
- Inspection: Issues first; rows grouped by area; passed items collapsed; completed controls visually read-only.
- Appointment: preserve the strong Calendar/List switch and existing creation stepper; collapse database metadata under Activity.
- Vendor: contact/service information first; “Tax paperwork” disclosed separately; replace response-time copy with **Average time from our text to the vendor’s reply**.
- Notices: title **Notices ready for review**; subtitle **Review each draft before anything is sent**; action **Create notices that are due**.
- Messages: first-use copy explains how to choose a tenant; invalid detail route shows **Conversation not found** and **Back to messages**.

### Portal, pending tenant-role proof

Source inspection supports a small follow-up slice after a tenant-role preview is available:

- Replace native account selectors on `/portal` and `/portal/payments`.
- Show dashboard summaries and primary actions before embedding a full maintenance form.
- Give `/portal/notifications` distinct loading, error, and true-empty states.
- Confirm all tenant-facing work status names match the staff-facing plain-language names.

No portal visual acceptance should be granted until these routes are exercised with a real Tenant experience.

## Smallest coherent implementation slice

Implement the notification journey first. It is the clearest user-reported failure, affects only Lane 2, and can preserve all API/persistence behavior.

### Production files

- `web/src/routes/(protected)/settings/notifications/my-alerts/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/team-routing/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/tenant-notices/+page.svelte`
- `web/src/lib/components/notifications/NotificationHelpAction.svelte`
- **New:** `web/src/lib/components/notifications/NotificationSetupJourney.svelte`
- **New:** `web/src/lib/components/notifications/TenantNoticePolicyAccordion.svelte`

Reuse the existing shared `FormStepper`, `Select`, `Checkbox`, `Card`, and input components without changing their shared implementations in this slice.

### Test files

- `web/src/lib/components/notifications/notification-settings-contract.test.ts`
- `web/src/lib/settings/notification-settings-matrix.test.ts`
- `web/src/lib/components/settings/notice-templates-section.test.ts`
- **New:** `web/src/lib/components/notifications/notification-setup-journey.test.ts`
- **New:** `web/e2e/notification-settings.spec.ts`

### Required acceptance proof

- All three canonical routes still load and save through their current independent endpoints.
- Back/Next navigation preserves unsaved-change protection and shows saved summaries.
- Tenant policies render collapsed by default and only one policy editor is open at a time.
- A non-administrator sees the personal step and a clear locked explanation for administrator-managed steps.
- Keyboard order, focus return, field errors, and accordion semantics are verified.
- Real browser proof at `1920 × 1080` for all three routes and `390 × 844` for the tenant-policy editor.
- Existing `/docs/...` links resolve.

## Exact no-overlap boundary

Lane 2 may implement only:

- `web/src/routes/(protected)/maintenance/**`
- `web/src/routes/(protected)/appointments/**`
- `web/src/routes/(protected)/vendors/**`
- `web/src/routes/(protected)/my-work/**`
- `web/src/routes/(protected)/my-schedule/**`
- `web/src/routes/(protected)/assignment-inbox/**`
- `web/src/routes/(protected)/messages/**`
- `web/src/routes/(protected)/notices/**`
- `web/src/routes/(protected)/settings/notifications/**`
- `web/src/routes/(portal)/**`
- Lane-specific components under `web/src/lib/components/notifications/**`, `web/src/lib/components/technician/**`, and `web/src/lib/components/records/WorkOrderDetail.svelte`
- Directly corresponding Lane 2 tests

Lane 2 must not modify:

- `AppShell.svelte`, Command Center navigation, Units, Properties, Tenants, Applications, or Leases — Lane 1 ownership
- Settings landing, accounting, reports, admin, or documentation article bodies — Lane 3 ownership
- Shared primitives such as `FormStepper.svelte`, `Select`, `RemoteRecordSelect`, `PageHeader`, or global tokens without explicit primary-agent coordination
- Backend APIs, notification workers, delivery semantics, database queries, or transaction boundaries for this UI-only slice

Lane 2 may add links to already published Help articles. Changes to the article bodies remain Lane 3 ownership.

## Reviewer questions before implementation

1. Does the three-step notification journey preserve the existing permission model and independent save boundaries?
2. Is “Send after required review” accurate for every automation mode, or should the product use “Send automatically after safeguards pass” for a subset?
3. Are responsibility topics best shown as six rows, or should beginner-facing categories be consolidated without changing backend topics?
4. Does every proposed label describe the real behavior, especially guarantor/occupant eligibility and delivery failure fallback?
5. Can role guards show an explanatory page without leaking technician or tenant record existence?
6. Does the work hub tab model preserve the existing Unit Command Center deep-link contract?

## Concern requiring follow-up

`DONE_WITH_CONCERNS` is required because technician-only routes, all tenant-portal routes, and a real message-detail state could not be visually proved with the available role/data. Source inspection supports the proposed direction, but it is not a substitute for role-correct browser acceptance.
