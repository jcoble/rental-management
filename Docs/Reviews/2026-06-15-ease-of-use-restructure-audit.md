# Rental Command — Ease-of-Use Restructure Plan

**Date:** 2026-06-15
**Audience:** The owner (a small landlord) building Rental Command for *other* non-technical small landlords.
**Goal as stated by the owner:** as easy as possible — hand-held onboarding, a real walkthrough of the system, maximum simplicity, willing to rip features out, mobile is the primary product, open to bold ideas.
**Method:** Synthesis of 6 specialist lens reports (onboarding, IA/nav, plain-language, simplify/rip-out, in-app guidance, bold redesign). Findings were deduped across lenses (most lenses independently hit the same screens), ranked by ease-of-use impact for a non-technical landlord, and grouped into a phased plan. Terms align with the existing `Docs/label-glossary.md` (canonical: **Money / Rentals / Work / Messages / Scan**, "Activity history", "Who owes me").

> Cross-reference: the engineering audit (`Docs/Reviews/AUDIT-2026-06-15.md`) independently flagged the same cross-surface drift theme (Scan/Add vs Capture, Work/Maintenance cluster, raw enum labels in money forms) and three real bugs that intersect this plan — the **mobile detail-screen "no back button"** issue (root-caused below in B5), the **401-retry dropping the photo/voice upload**, and the **dead-end "custom report request"**. Where a UX item and a known bug overlap, this plan notes it so they're fixed together.

---

## Executive summary — the 3 biggest levers

Every lens, independently, converged on the same three things. Fix these and the app stops feeling like accountant/property-manager software and starts feeling like the hand-held tool the owner promised.

### Lever 1 — Build the guided walkthrough that was promised (and put it on mobile)
**This is the #1 explicit ask and it does not exist.** The web app has a real spotlight/coach engine (`web/src/lib/onboarding/coach.svelte.ts` + `CoachOverlay.svelte`), but it *only* fires when a setup-checklist row deep-links to a single control — there are exactly ~10 anchors, all setup buttons. **There is no tour that says "here's Today, here's Money, here's the Scan button, here's Work."** Mobile — the owner's primary product — has *zero* teaching infrastructure: no coach, no tour, exactly one `Tooltip()` in the whole app (and tooltips don't even appear on touch). So a non-technical landlord is taught how to *fill the database* during setup and then dropped onto a dense dashboard with no idea how to *use* the app. The fix is one ~5-step welcome tour that auto-runs once on first dashboard load, reuses the existing web engine, gets a hand-rolled Flutter twin, fires at the high-intent "You're all set!" moment, and is replayable from a persistent "Show me around" button.

### Lever 2 — Collapse the app to ONE plain mental model, mirrored on web + mobile, and rip out the duplicates
Right now the two platforms expose *different* navigation, and "money" alone is spread across **5+ overlapping surfaces** (`/accounting` with its own Ledger/Reports/Overview tabs, *plus* a standalone `/reports` hub of ~16 reports, *plus* `/owners-report`, *plus* `/tax`, *plus* `/analytics`/Insights, *plus* `/banking`). Mobile hides ~17-19 destinations behind an unlabeled grid icon ("Browse"); web shows ~19-20 links in a collapsing accordion sidebar. There are duplicate AI entry points (Tell me / Ask AI / Assistant icon), duplicate capture entry points (on mobile, "Scan a document" and "Add expense" call the *identical* sheet), duplicate setup surfaces (choose-setup + a *second* explore-vs-live fork inside get-started), and duplicate routes (`/vendors` and `/owners/vendors`, `/activity` and `/audit`). The fix: **five landlord nouns on both platforms — Today / Money / Rentals / Work / Messages, with Scan as the center action** — and everything accountant-grade (Reports catalog, Owners, Team, Insights, Banking reconciliation, Inspections, CSV import, SMS-provider credentials) moved behind a single "More / Advanced" gate or hidden entirely for solo landlords.

### Lever 3 — Make the Scan/voice "the computer does the typing" magic the front door, and speak plain English everywhere
The single most differentiating, least-intimidating capability — **photograph a lease/bill/check and the AI fills everything in, and can create the property+unit+tenant+lease from one lease photo** (`scan_review_screen.dart` `leaseProposal` / `_CreatePropertyFields`) — is buried as one tile among four plus a FAB plus a Browse entry. Meanwhile the moment a user leaves the (genuinely excellent) onboarding copy, the app reverts to "Ledger / receivables / reconcile / counterparty / Schedule E / 1099 / P&L / Net / occupancy / Field queue / Work Order / portfolio / sandbox." The fix: lead with "Snap it or say it" (one-tap confirm cards, not 20-field forms), reframe every empty state as a camera/mic invitation, and enforce one plain-language glossary (`Docs/label-glossary.md`) across both clients so the friendly voice of the dashboard briefing carries through the whole product.

**Why these three:** Lever 1 is the literal unmet requirement. Lever 2 removes the "this app is huge and I'm lost" overwhelm. Lever 3 delivers the "I barely have to type, and I understand every word" feel. They're complementary: the tour (1) teaches a 5-noun app (2) whose front door is a photo and whose words are plain (3).

---

## Phase A — quick wins (copy, labels, hiding clutter)

Low-effort, high-payoff. Mostly relabeling, field-deferral, and hiding. No data-model changes. Most are S; a couple are M.

### A1. Fix the "you're done" lies (onboarding tells users they're set up when they aren't) — **both, S, HIGH**
Three places congratulate a user who has nothing:
- Mobile Live setup headline reads **"You're all set up, {firstName}"** on a deliberately empty account whose own body says "Your account is empty and ready for your real portfolio" (`mobile/lib/features/onboarding/onboarding_live_setup_screen.dart` ~line 70). → **"You're in — let's add your first property, {firstName}."**
- Web wizard shows **"You're all set!"** celebration even after the user tapped "Skip this step" through property/tenants/lease (`web/src/routes/(protected)/onboarding/+page.svelte`). → Only show the celebration if the core spine (≥1 property+unit, ≥1 tenant, ≥1 lease) actually exists; otherwise **"You can finish anytime"** and route to the checklist with remaining essentials highlighted.
- Sandbox lands the user on **"Getting started 5 of 5 — core setup complete"** because seeded demo records auto-satisfy all signals (see B1 for the structural fix; the copy half is quick).

### A2. De-emphasize "Skip" on core onboarding steps — **web, S, MEDIUM**
Every wizard step has an equally-weighted outlined **"Skip this step"** next to "Save & continue." A skimming user skips the essentials and lands on a broken-feeling app. → Make Skip a ghost/text link (not a same-size button) on core steps and relabel **"I'll add this later."** (`web/src/routes/(protected)/onboarding/+page.svelte`.)

### A3. Collapse the mobile quick-action row from 4 buttons to 2 — **mobile, S, HIGH**
"Tell me", "Scan a document", "Ask AI", "Add expense" — but **"Scan a document" and "Add expense" call the same handler** (`onOpenCapture`), and that capture sheet already contains "Tell me" and "Type it." Four tiles, two real destinations. → Two buttons: **"Scan or add"** (the capture sheet — covers receipts/bills/checks/leases/voice/type) and **"Ask AI."** Remove "Tell me" and "Add expense" tiles. (`mobile/lib/features/home/home_shell.dart` `_QuickActions` ~1957-1995, wired ~1360-1369.) Also fixes the cramped hard-wrapped labels ("Add\nexpense").

### A4. Unify the AI to one name + one entry point — **both, S, HIGH**
Mobile shows "Tell me" + "Ask AI" + an AppBar Assistant sparkle icon + an inline briefing; web has `/ai` *and* a floating AssistantBubble on every page. → One assistant, one name ("**Ask**"), one entry (mic + type in the same sheet). Drop the duplicate AppBar icon; on web keep either `/ai` or the bubble, not both.

### A5. Rename the money jargon on the surfaces a newcomer hits day one — **both, S, HIGH**
Plain-English swaps (full before→after table in the last section). The highest-traffic ones:
- Money page subtitle "Rent ledger, receivables, expenses, and owner-facing books" → **"Everything you've collected and spent, all in one place."**
- "Ledger" tab → **"Transactions"** (the section heading already says Transactions).
- Dashboard KPIs: "Receivables past due" → **"Rent past due"**; "Occupancy" → **"Units filled"**; "Net This Month / Paid - expenses" → **"Kept this month / what's left after expenses."** (Match the big "Your money" snapshot, which already uses Collected / Spent / Kept.)
- "Counterparty" column → **"Who"**; "Reconcile" button → **"Match to bank"**; "Outstanding" → **"Still owed."**
(`web/src/routes/(protected)/accounting/+page.svelte`, `web/src/routes/(protected)/+page.svelte`.)

### A6. Rename the maintenance jargon — one word for "things to fix" — **both, S, MEDIUM**
The same concept is "Work" (tab), "Maintenance" (web link), "Work Order" (detail title), and "Field queue" (mobile dashboard header). → Standardize user-facing copy on **"Repairs"**: detail title "Repair", mobile section "Open repairs" / loading "Loading repairs..." / empty "No repairs open right now." Make the status label map total and friendly so raw `WaitingParts` can never leak: New→"Reported", Scheduled→"Scheduled", InProgress→"Being fixed", WaitingParts→"Waiting on parts", Completed→"Done", Cancelled→"Cancelled" (`mobile/lib/.../work_order_timeline.dart`). Keep "work order" in code/types only.

### A7. Collapse the manual expense form to 4 fields — **web, M, HIGH**
Logging one expense by hand shows ~25 fields (subtotal, tax rate, vendor tax ID, card last 4, tip, discount, shipping, due/paid dates, 14-category Schedule E dropdown, "billable to owner"...). → Show **Description, Amount, Date, Property**. Default Category to a best-guess/"Other" and Status to "Paid". Everything else behind a collapsed **"Add more details (optional)"** disclosure. (Scan already captures these when present.) (`web/src/routes/(protected)/accounting/+page.svelte` ~1246-1419.) Default the category picker to plain types (Repairs, Cleaning, Supplies, Insurance, Taxes, Mortgage, Other) mapped to Schedule E behind the scenes.

### A8. Default the payment form to "Paid", relabel "Select lease" → "Tenant / unit" — **web, S, MEDIUM**
Recording rent forces "Select lease" (users think *which tenant/unit*) and a 5-status dropdown (Scheduled/Partial/Late/Waived). → Label the selector **"Tenant / unit"** (show "Tenant — Unit", lease # secondary), default Status to **"Paid"**, hide the status dropdown behind "More options." If shown: "Waived"→"Forgiven", "Partial"→"Part-paid." (`.../accounting/+page.svelte` ~1120-1166.)

### A9. Cut the 20-second seeding theater — **both, S, MEDIUM**
The "Setting up your sandbox" screen is *intentionally* ~20s of fake staged progress while the real seed finishes in a couple seconds (code comment is explicit). A non-technical user reads a 20s spinner as "this is slow/stuck." → Cut to the real seed time (~3-5s) with one honest line, **or** start the welcome tour intro during the wait so the time teaches instead of stalls. (`web/src/routes/setting-up/+page.svelte` ~14-22; `mobile/.../onboarding_seeding_screen.dart`.)

### A10. Hide single-owner-irrelevant fields/labels — **both, S, MEDIUM**
For a landlord who owns their own properties:
- Hide the **"Billable to owner"** checkbox entirely (relabel "Charge this back to the property owner — only if you manage for someone else" where shown).
- Hide the **Portfolio selector** when there's exactly one portfolio; show the business name as a label. Replace the word "portfolio" with "your rentals" in user-facing copy. (`web/.../AppShell.svelte` ~527-531; threaded `getCurrentPortfolioId()` stays in code.)
- Gate **Owners / owner-statement** surfaces behind Advanced for single-owner accounts.

### A11. Rename Sandbox/Live to outcomes, make the banner actionable — **both, S, MEDIUM**
"Sandbox" + "Live" + a flask icon are dev terms. → **"Try it with example data"** vs **"Set up my real rentals."** Replace the persistent flask banner with one quiet, actionable chip: **"Example data — tap to start fresh with your own."** Mobile's `_SandboxIndicator` is currently informational-only ("Nothing here is real") with no action — give it the go-live affordance web already has. (`web/.../SandboxBanner`, `mobile/.../home_shell.dart` ~330-371.)

### A12. Replace dead help links OR hide them until articles exist — **web, S→M, MEDIUM**
"Read the step-by-step guide" / "See how it works" link to `/docs/<slug>` where the slugs are explicitly placeholders that render an error page. A help link that dead-ends erodes trust faster than no link. → Author the handful of getting-started articles (portfolio, owner, property, tenants, lease) **or** gate the link on a `published` flag the registry already knows. (`web/.../WizardStepScaffold.svelte`, `wizard-steps.ts`.)

### A13. Pull SignalWire texting out of the newcomer checklist — **both, S, MEDIUM**
The checklist's "Connect texting" step asks for SignalWire Project ID / Space URL / API Token / a purchased From number — by far the most technical thing in the app, and on mobile it **can't be dismissed** (no mark-done), so it permanently blocks "all done." → Move it to Settings as an opt-in **"Send tenants text messages (advanced)"**; if kept on the checklist, mark it clearly optional AND dismissible on both platforms. (`getting-started-tasks.ts`, `getting_started_tasks.dart`.)

### A14. Make mobile detail screens reachable from notifications keep a back button — **mobile, S, HIGH (also a logged bug)**
Notification taps and push deep-links use `context.go(...)` which *replaces* the stack, so the detail screen renders with no back affordance — the user is stranded (the engineering audit's mobile "no back button" finding, root-caused). → Use `context.push` for these (one-line change per call site); reserve `context.go` for true one-way gates (choice→seeding→dashboard). (`mobile/.../notifications_inbox_screen.dart:51`, `home_shell.dart` `_handlePushLink:188`; same for the 9 detail `context.go` call sites.)

### A15. Friendly empty states instead of "No properties found." — **both, M, MEDIUM**
First-run Properties/Tenants/Leases show a flat "No X found." → Per-screen first-run empty state: icon + one plain sentence (reuse the eli5 copy already written in `getting-started-tasks.ts`, e.g. "A property is one building or address. Add your first to get started.") + a big primary "Add your first property" button. Extend `EmptyState` to take description + action. (`web/src/lib/components/data-grid/DataGrid.svelte`.)

---

## Phase B — structure & rip-outs (IA changes, renames, what to remove)

Each item: **what / why it's easier / what it replaces or removes / impact / effort / platform.**

### B1. Sandbox teaches by doing, not by pre-checking the checklist
- **What:** In Sandbox, stop treating seeded demo records as completed learning steps. Replace the data-derived checklist card with an **"Explore — try these 3 things"** card ("Scan a receipt", "See who's behind on rent", "Open a work order") that teaches by doing on the fake data. Keep the real data-derived checklist for Live accounts only.
- **Why it's easier:** Today the app steers everyone to Sandbox, then the seed auto-satisfies all 5 core signals, so the new user lands on "core setup complete" having learned nothing and with nothing to do — the hand-holding silently evaporates the instant they arrive.
- **Replaces/removes:** The pre-checked "5 of 5" checklist state for sandbox users.
- **Impact:** HIGH · **Effort:** M · **Platform:** both. (`use-getting-started.svelte.ts`, `getting_started_provider.dart`, `GettingStartedCard.svelte`, `_GettingStartedCard`.)

### B2. Five landlord nouns, mirrored on both platforms
- **What:** Collapse navigation to **Today / Money / Rentals (properties+tenants+leases+applications) / Work / Messages**, with **Scan** as the center action. Everything currently top-level — `/reports`, `/owners-report`, `/tax`, `/analytics`, `/deposits`, `/banking`, `/vendors`, `/appointments`, `/notices`, `/applications`, `/audit` — becomes a sub-view inside one of the five or moves under "More / Advanced." Align labels exactly across clients (Today=Home, Messages=Inbox — pick one word each per `label-glossary.md`). Properties/Tenants must be reachable at the same depth on both.
- **Why it's easier:** This single move fixes findability, the web/mobile split, money fragmentation, and jargon at once. A landlord who learns the phone already knows the web.
- **Replaces/removes:** The mobile "Browse" grid as a parallel nav system; the web 5-group collapsing accordion sidebar; the ~26 top-level web routes as nav entries.
- **Impact:** HIGH · **Effort:** L · **Platform:** both. (`web/.../AppShell.svelte`, `mobile/.../home_shell.dart`, `more_tab.dart`.)

### B3. One Money hub with two tabs
- **What:** Collapse to a single **Money** destination: **"Activity"** (the ledger of payments + expenses) and **"Summary"** (Collected / Spent / Kept + the 2-3 charts a small landlord cares about). Fold the 16-report catalog, Insights, and Owner Reports under one **"More reports"** link inside Money (or Advanced). Make Tax a once-a-year **"Tax export"** button, not a top-level destination.
- **Why it's easier:** A landlord thinks "money in / money out / what do I owe at tax time," not "Ledger vs Reports vs Insights vs Owner reports vs Overview." Five+ front doors to overlapping numbers is the single biggest IA tax in the app.
- **Replaces/removes:** Standalone `/reports`, `/owners-report`, `/tax`, `/analytics` as nav entries; the `/accounting` Ledger/Reports/Overview three-tab split.
- **Impact:** HIGH · **Effort:** M · **Platform:** both. (`/accounting`, `/reports`, `/owners-report`, `/tax`, `/analytics`, `/banking`.)

### B4. One setup spine (kill the duplicate forks and surfaces)
- **What:** Pick ONE path: **Choose Setup → wizard → done.** The dashboard "Getting started" card becomes the single ongoing checklist (auto-hides when done). **Delete the standalone `/get-started` route and its duplicate explore-vs-live fork.** The wizard and checklist read from one shared task list (they already share `wizard-steps.ts`) so the user never sees two versions of "add a property."
- **Why it's easier:** A new user currently hits the explore-vs-live choice in *two* places, then a 7-step wizard, then a separate checklist, then a dashboard card — overlapping surfaces that make setup feel like it never ends and leave it unclear which is authoritative.
- **Replaces/removes:** `/get-started` route + its second fork; the parallel "wizard AND a full checklist of the same items."
- **Impact:** HIGH · **Effort:** M · **Platform:** both.

### B5. Mobile Live setup becomes a real wizard that reaches a first lease (+ fix back-button nav)
- **What:** After the first property is saved on mobile, *continue guiding* instead of bouncing to the dashboard: chain **Add a unit → Add a tenant → Create the first lease** (a lightweight stepper reusing existing add-sheets), or offer "skip — I'll scan my lease instead" routing into the create-from-document path. Mirror the web wizard's spine and its plain-English per-step copy. End on a celebratory "You're set — here's your first rent due." Use `context.push` (not `.go`) for anything the user should back out of.
- **Why it's easier:** On the *primary* product, the real-setup user currently adds at most one property via a single screen and is abandoned mid-setup — the hardest, most error-prone path gets the least hand-holding, and `context.go` strands them with no back button.
- **Replaces/removes:** The single-screen `onboarding_live_setup_screen.dart`; web's "all set up after one property" cliff.
- **Impact:** HIGH · **Effort:** M-L · **Platform:** mobile. (`mobile/lib/features/onboarding/onboarding_live_setup_screen.dart`, `app_router.dart`.)

### B6. Calmer first-run dashboard for brand-new accounts
- **What:** For not-yet-set-up / low-data accounts (and the first week), show a calmer dashboard variant: lead with a big friendly **"Welcome — let's get your rentals in"** card (the checklist/tour), and collapse the analytics-heavy modules (severity briefing, leasing mix, KPI strip, recent activity, appointments) until there's real data and the user has been oriented. Reveal the full command-center once setup is done. Cap any "needs attention" list to ~3 with a "see all."
- **Why it's easier:** Post-setup the dashboard stacks 8-9 competing modules (AI briefing + "14 things need attention" + money snapshot + 4 KPIs + messages + field queue + leases + activity + appointments). For a brand-new (especially Sandbox/fake-data) user this is overwhelming, jargon-heavy, and buries the actual "start here."
- **Replaces/removes:** The dense full dashboard *as the first impression* (it stays — just deferred until there's data).
- **Impact:** HIGH · **Effort:** M · **Platform:** both. (`web/src/routes/(protected)/+page.svelte`, `mobile/.../home_shell.dart` `_HomeTab`.)

### B7. Lease scan-review: one confirm card, hide the "Create vs Link" toggle
- **What:** Default to the AI's proposal and **hide** the `SegmentedButton('Create new'/'Link existing')` and the property→unit→tenant dropdowns. Show one plain summary card: **"I'll add 123 Main St, Unit 4B, and a lease for Jane Smith at $1,400/mo"** with a single **"Looks right — create it"** button and a quiet **"Change something"** link that only then reveals the pickers/fields. Same treatment for receipt review: lead with "$54.20 to Home Depot on Jun 12, filed under Repairs. Right?" + "Yes, save it"; collapse the ~20 extraction fields behind "See all details."
- **Why it's easier:** The most powerful path (create the whole property+lease from a photo) currently asks a Facebook-comfortable landlord to reason about a data-modeling concept ("link vs create") and confronts them with ~20 editable fields — turning "snap it and done" back into data entry and undercutting the whole value prop.
- **Replaces/removes:** The create/link toggle and the full field grid *as the default surface* (kept behind "Change something").
- **Impact:** HIGH · **Effort:** M · **Platform:** both. (`mobile/lib/features/scan/scan_review_screen.dart` `_LeasePickers` / `_fieldGroups`; web review parity.)

### B8. Trim the mobile "Browse" grid (or hide it behind Advanced)
- **What:** Reduce Browse to the handful a small landlord opens occasionally (Properties, Tenants, Leases, Applications, Expenses, Deposits, Settings) and move Recurring maintenance, Inspections, Banking reconciliation, Owner reports, Insights, Team into an "Advanced" subsection collapsed by default — so Browse opens to ~7 obvious things, not 17-19. If kept as an entry point, **label the icon "More" with text**, never a bare grid glyph. Better: hide Browse behind Advanced entirely and let the AI/search reach the long tail ("where do I record a deposit?").
- **Why it's easier:** Beyond the 5 tabs there's a second, hidden navigation surface of 17-19 tiles reached via an unlabeled grid icon — an app-within-the-app the target user will never explore, full of PM features they don't need.
- **Replaces/removes:** The 18-item flat Browse grid as a co-equal nav system.
- **Impact:** MEDIUM-HIGH · **Effort:** M · **Platform:** mobile. (`mobile/lib/features/home/more_tab.dart`.)

### B9. Web sidebar → "Simple" default of ~5 entries
- **What:** Ship a Simple default sidebar (Today, Money, Rentals, Work, Ask AI + Scan) and tuck Reports/Owners/Vendors/Notices/Insights/Team/Activity behind a single "More / Advanced" expander. Drop the **header quick-action bar** that *repeats* Scan/Messages/Appointments/Help already in the nav (keep only Notifications + Scan in the header). Drop the **exclusive accordion** behavior (opening one group closes another) — with ~16 links, show flat always-visible sections so nothing disappears. Rename so nothing collides ("Money" group containing a "Money" page; "Reports" vs "Owner reports" vs "Insights" vs "Activity history").
- **Why it's easier:** The expanded sidebar reads as an enterprise admin console (~19-20 links across 5 groups + a redundant header icon bar + a portfolio selector), and the accordion makes links the user just saw vanish.
- **Replaces/removes:** The header quick-action duplicates; the accordion collapse; the full sidebar as the *default* (available via Advanced toggle for power users).
- **Impact:** MEDIUM-HIGH · **Effort:** M-L · **Platform:** web. (`web/src/lib/components/AppShell.svelte`.)

### B10. Fold Recurring + Inspections into "Work" as optional actions
- **What:** Make "Work" a single list of jobs (open/done). Fold Recurring schedules and Inspections in as optional actions ("Set up a repeating task", "Do a walk-through") behind a "+" / Advanced affordance rather than first-class sibling destinations. Hide Inspections until the user has more than N units, or behind Advanced.
- **Why it's easier:** "Maintenance" is one mental bucket for a small landlord. Splitting it into Work Orders + Recurring + Inspections (+ a Maintenance landing) is PM-grade structure with concepts most small owners won't use.
- **Replaces/removes:** `/maintenance/recurring` and `/maintenance/inspections` as sibling nav destinations.
- **Impact:** MEDIUM · **Effort:** M · **Platform:** both. (`web /maintenance/*`, mobile WorkOrders/RecurringMaintenance/Inspections.)

### B11. Default to "Simple" mode; reveal Pro surfaces only on signal
- **What:** Auto-detect single-landlord mode and physically remove enterprise surfaces: no Portfolio selector, no Team tab, no raw SMS-provider credential fields, no Owners management (self-owner is implicit), no billable-to-owner, no 1099-review/P&L-by-property as standing destinations. A Settings toggle ("I manage properties for other people" / "I want full accounting") reveals the heavier surfaces. Gate Owners/Team on the user actually adding a second owner or inviting a teammate.
- **Why it's easier:** Most users would then never meet "Account SID", "Schedule E", "Reconciliation", or "Portfolio" at all. The data model already supports everything — this is purely a visibility gate.
- **Replaces/removes:** Owners, Team, raw Messaging-provider credentials, Applications/Notices/Banking/Deposit-register *from the default surface* (opt-in via Advanced/usage).
- **Impact:** MEDIUM-HIGH · **Effort:** M · **Platform:** both.

### B12. Slim Settings to "Basics" + an Advanced toggle
- **What:** Default Settings to a short Basics view (your name/business name, where reminders go, turn reminders on/off). Move Team, raw Messaging-provider credentials, Setup & import, and Activity history behind "Advanced." Hide "Owners" for single-owner accounts. Aim for 2-3 visible sections.
- **Why it's easier:** Settings currently exposes 8 technical tabs (including SignalWire/Twilio Account SID/Auth Token) — not "simple," and a non-technical user shouldn't meet "Account SID" on day one.
- **Replaces/removes:** The 8-tab control panel as the default.
- **Impact:** MEDIUM · **Effort:** M · **Platform:** both. (`settings/+page.svelte` SETTINGS_SECTIONS.)

### B13. Move CSV import behind Advanced
- **What:** Make scanning/typing the only surfaced intake. Move `/import` (entity-type templates, spreadsheet upload) into Advanced/Settings as "Switching from another tool? Import a spreadsheet." Don't reference templates/entity-types in the main flow.
- **Why it's easier:** CSV templates with entity types are decidedly technical; a non-technical landlord won't have CSVs and shouldn't be shown a spreadsheet-import workflow as a primary path.
- **Replaces/removes:** `/import` from the main intake paths.
- **Impact:** LOW-MEDIUM · **Effort:** S · **Platform:** web.

### B14. Plain-English summary on top of every data screen
- **What:** Lead Money/Reports/Owner reports/Insights with one AI sentence before the table ("You collected $4,200 and spent $900 in June; Jane still owes $1,400."). Keep the table below for those who want it.
- **Why it's easier:** The dashboard briefing proves the team can speak plainly, but the moment the user taps into a second-level screen they hit ledgers/tabs/charts — the friendly voice stops at the home screen and they bounce.
- **Replaces/removes:** Nothing (additive); makes tables the *second* thing, not the first.
- **Impact:** MEDIUM · **Effort:** M · **Platform:** both.

### B15. One canonical onboarding task list + one tour-copy registry, shared by web + mobile
- **What:** Define ONE canonical task list and completion rule shared by both clients (mobile already cites the web list as canonical but silently drops the "portfolio" step and lacks mark-done). Same tasks, labels, order, "done" math everywhere. Extend the shared `wizard-steps.ts` / `label-glossary.md` pattern to a single source of truth for tour steps + jargon definitions consumed by both the Svelte coach overlay and the new mobile coach overlay. A meta-test fails the build if a destination is top-level on one platform and buried on the other, or if a banned jargon term appears in a `.svelte`/`.dart` string.
- **Why it's easier:** Today the checklists differ across platforms (different counts, locked items, mark-done availability), so a user who sets up on the phone then opens web sees a different checklist — "I thought I finished this?" The hand-holding must compound, not reset.
- **Replaces/removes:** The web/mobile checklist divergence; future drift.
- **Impact:** MEDIUM · **Effort:** S-M · **Platform:** both.

### B16. Kill duplicate routes
- **What:** One vendors route (`/vendors`, delete/redirect `/owners/vendors`); one history surface (`/activity` vs `/audit` — keep one, mirror the `label-glossary.md` "Activity history" label). Audit the ~26 top-level protected routes and demote anything not a daily/weekly task into Settings/Advanced so the primary surface is ~8-10.
- **Why it's easier:** Duplicate paths to the same concept make the app feel bigger and raise "are these different?" doubt.
- **Replaces/removes:** `/owners/vendors`, one of `/activity`//`/audit`.
- **Impact:** MEDIUM · **Effort:** S · **Platform:** web.

### B17. Cut the mobile AppBar to (at most) the notification bell
- **What:** The Today AppBar has four unlabeled icons (bell, grid/Browse, sparkle/Assistant, logout). → Keep at most the bell. Move Assistant into the body "Ask AI" button, fold Browse into "More", relocate logout into Settings (so it's not one mis-tap from the bell).
- **Why it's easier:** Two of the icons are entire navigation surfaces behind ambiguous glyphs, and logout sits one tap from the bell (accidental sign-outs). Icon-only affordances assume software literacy the target user lacks.
- **Replaces/removes:** The grid, sparkle, and logout AppBar icons.
- **Impact:** MEDIUM · **Effort:** S · **Platform:** mobile. (`home_shell.dart` `_HomeTab` AppBar ~1312-1333.)

---

## What to rip out

A clean cut-list. "Rip out" = delete from the default user-facing surface (some move behind Advanced; most "delete from nav," not "delete the capability"). Ordered by confidence/payoff.

| # | Cut | Where | Disposition |
|---|---|---|---|
| 1 | **Duplicate capture tiles** — "Scan a document" + "Add expense" both open the same sheet; plus "Tell me" | mobile `home_shell.dart` `_QuickActions` | Delete; replace with one "Scan or add" + "Ask AI" (A3) |
| 2 | **Standalone money routes** as nav entries: `/reports`, `/owners-report`, `/tax`, `/analytics` | web nav | Fold into Money → "More reports" / "Tax export" (B3) |
| 3 | **`/get-started` route + its second explore-vs-live fork** | web | Delete; one setup spine (B4) |
| 4 | **20-second seeding theater** (artificial delay) | `setting-up/+page.svelte`, mobile seeding | Cut to real seed time or fill with tour intro (A9) |
| 5 | **Duplicate AI entry points** — keep one ("Ask"): Tell me / Ask AI / AppBar sparkle / floating bubble + `/ai` | both | Collapse to one (A4) |
| 6 | **Pre-checked "5 of 5" sandbox checklist** | getting-started signals | Replace with "try these 3 things" explore card (B1) |
| 7 | **Mobile "Browse" 17-19-tile grid** as a parallel nav system | `more_tab.dart` | Trim to ~7 / hide behind Advanced (B8) |
| 8 | **Web header quick-action bar** (repeats Scan/Messages/Appointments/Help) | `AppShell.svelte` | Delete; keep Notifications + Scan only (B9) |
| 9 | **Web sidebar exclusive accordion** behavior | `AppShell.svelte` | Flat always-visible sections (B9) |
| 10 | **Portfolio selector** for single-portfolio accounts + the word "portfolio" in UI | both | Hide; show business name (A10/B11) |
| 11 | **"Billable to owner" checkbox** for single-owner accounts | expense form | Hide (A10) |
| 12 | **Owners / Owner statements / Team / SMS-provider credentials** from default surface | both | Behind Advanced / usage-gated (B11/B12) |
| 13 | **`SegmentedButton` Create-vs-Link toggle + 20-field grid** as the default scan-review surface | `scan_review_screen.dart` | Hide behind "Change something" (B7) |
| 14 | **~21 fields** in the manual expense form (down to 4 visible) | `accounting/+page.svelte` | Collapse behind "Add more details" (A7) |
| 15 | **5-status dropdown** on the payment form (default "Paid") | `accounting/+page.svelte` | Behind "More options" (A8) |
| 16 | **Duplicate routes** `/owners/vendors`, one of `/activity`//`/audit` | web | Delete/redirect (B16) |
| 17 | **CSV `/import`** from main intake | web | Behind Advanced (B13) |
| 18 | **Recurring + Inspections** as first-class nav destinations | both | Fold into Work as optional actions (B10) |
| 19 | **SignalWire texting** from the newcomer checklist | both | Move to Settings (advanced, dismissible) (A13) |
| 20 | **Dead `/docs/<slug>` help links** (placeholder slugs → error page) | web wizard | Author articles or gate on `published` (A12) |
| 21 | **Dense dashboard modules** (severity briefing / leasing mix / KPI strip / activity / appointments) *as the first impression* | both | Deferred until there's data (B6) |

---

## Bold ideas to prototype (Phase C)

Higher-effort restructures worth a prototype before committing. Ranked by intent match to the owner's vision.

### C1. The first 10 minutes is "set up ONE rental together," not a dashboard
A single warm, full-screen conversational stepper (same on web + mobile) right after the choice screen: "What do you call your rentals?" → "What's the address of your first place?" (address autocomplete) → "How much is rent?" → "Who lives there?" → "When does rent start?" One question per screen, big type, plain words, a friendly progress dot, ending "That's it — Maple St is set up and I'll remind you when rent is due." The current multi-field wizard already has all the pieces; this is a re-skin into a hand-held narrative that finishes with a *real working rental*, not an empty dashboard. **This is onboarding = the tour = setup, one flow.**

### C2. "Set it up for me" from one lease photo
Promote the existing create-from-document path (`scan_review_screen.dart` `leaseProposal` / `_CreatePropertyFields`) to *be* onboarding: "Take a picture of your lease and I'll create the property, the unit, the tenant, and the lease for you." The hardest part of setup becomes one photo + one "Looks right" tap. Setup-by-form becomes the fallback, not the front door.

### C3. The home screen is ONE question, not a dashboard
Replace the mobile Today feed (8 stacked sections) and the 4 ambiguous tiles with one plain hero: "Hi {name} — 2 things need you today" + a "Show me" button, and below it one giant "Snap or say something." Messages and field queue move off the home scroll into their existing tabs. The home screen's only job: *what needs me, and what do I want to do.* Pairs with a "one card at a time" stack ("Rent from 12 Oak is 3 days late — send a reminder?" → next → "You're all caught up") instead of a 14-item list.

### C4. The AI assistant becomes the primary navigation
An always-present "What do you need?" bar where the user types or says "add the rent I just got from 12 Oak", "who's late", "show me my taxes", "log this $80 plumber bill" — and the app does it or takes them there. For someone who texts and uses Facebook, conversational intent beats hunting tabs; the structured nav becomes the fallback, and the long-tail Browse grid / 19-link sidebar stop being the way to *find* things. (Infra exists: Tell-me intake + briefing deep-links.)

### C5. Port the spotlight/coach to Flutter and make a 60-second narrated tour the first thing a mobile user sees
Reuse the proven `coach.svelte.ts` pattern (dim + cutout + caption bubble + Next/Skip) as one reusable Flutter widget. Narrate 3-5 steps: add a property, scan a lease, where rent shows up, ask the AI. Fire it at the high-intent "You're all set!" moment and make it replayable from a persistent "Show me around" button. **This is the direct fulfillment of Lever 1 on the primary product** — today only a single-step deep-link coachmark exists on web and nothing on mobile.

### C6. "Get my tax packet" replaces the entire Reports/Schedule E/1099/P&L surface
One outcome-named button generates the PDF an accountant needs plus a plain checklist ("You still owe a 1099 to Joe's Plumbing — tap to text for their info"). Hide all standalone accounting tabs and IRS form names behind that single action, so the user never has to know "Schedule E" to do the right thing.

### C7. Two-persona app, default to the simplest
Ship a "Just my rentals" mode (default) showing only Today / Money / Rentals / Work / Messages + Scan, and a "Pro" toggle that reveals Owners, Team, Applications, Notices, Banking reconciliation, Inspections, Recurring, the full report catalog, and Insights. ~80% of the current surface area becomes opt-in. Purely a navigation/visibility gate over the existing data model. (This is B11 productized as an explicit mode.)

### C8. Reframe every empty state as a camera/mic invitation
"No expenses yet" → "Got a receipt? Take a picture and I'll log it." "No leases yet" → "Snap your lease — I'll set everything up." The magic path is offered exactly where the user gets stuck, app-wide.

### C9. One shared "tourSeen/help" state + step-copy registry across web + mobile
Author the walkthrough and jargon definitions *once*; both clients consume them. The owner's "make everything make sense" goal becomes a content task, not a per-platform engineering task each time. (B15 taken to its conclusion.)

---

## Per-screen plain-language fixes (before → after)

One canonical word per concept; enforce via `Docs/label-glossary.md` + a build-failing lint on banned terms in `.svelte`/`.dart` strings. (Schema/enum names like `Accounting`, `pastDueCount`, `WorkOrder` stay in code — just never surface them.)

| Screen / location | Before | After |
|---|---|---|
| Money page subtitle (`accounting/+page.svelte`) | "Rent ledger, receivables, expenses, and owner-facing books." | "Everything you've collected and spent, all in one place." |
| Money tab | "Ledger" | "Transactions" |
| Money transactions column | "Counterparty" | "Who" (or "Tenant / Vendor") |
| Money ledger button | "Reconcile" | "Match to bank" |
| Money KPI | "Outstanding" | "Still owed" |
| Banking page subtitle | "Read-only bank reconciliation…" | "See your bank deposits and withdrawals, and match them to the rent and bills you recorded — so nothing's counted twice." |
| Reports subtitle | "Ledger, property profit and loss, Schedule E totals, and 1099 review." | "Year-end summaries to hand your accountant, plus who still needs a tax form." |
| Reports card | "Net cash flow" | "Money kept (income minus expenses)" |
| Reports card | "Ledger rows" | "Recent transactions" |
| Reports card | "Property-level P&L" | "Profit by property" |
| Schedule E gloss (keep the term) | "Schedule E" | "Schedule E — the rental page of your tax return" |
| Dashboard KPI | "Receivables past due" | "Rent past due" |
| Dashboard KPI | "Occupancy" | "Units filled" (keep X/Y count) |
| Dashboard KPI | "Net This Month / Paid - expenses" | "Kept this month / what's left after expenses" |
| Dashboard hero | "Command center" / "Portfolio pulse" | "Today" |
| Dashboard module | "Leasing mix" | "Leases by status" |
| Mobile home section | "Field queue" / "No open field work." | "Open repairs" / "No repairs open right now." |
| Repair detail title | "Work Order" | "Repair" |
| Repair status (mobile, total map) | `New` / `WaitingParts` (raw) | "Reported" / "Waiting on parts" |
| Expense checkbox | "Billable to owner" | "Charge this back to the property owner" (hidden for single-owner) |
| Payment form selector | "Select lease" | "Tenant / unit" |
| Payment status | "Waived" / "Partial" | "Forgiven" / "Part-paid" (hidden behind "More options") |
| Mobile Live setup headline | "You're all set up, {firstName}" | "You're in — let's add your first property, {firstName}" |
| Wizard finish (when spine missing) | "You're all set!" | "You can finish anytime" |
| Wizard core-step button | "Skip this step" | "I'll add this later" (ghost link, de-emphasized) |
| Choose-setup option | "Sandbox · sample data" | "Try it with example data" |
| Choose-setup option | "Live · your real data" | "Set up my real rentals" |
| Sandbox banner (mobile) | "Sandbox mode — exploring with sample data. Nothing here is real." | "Example data — tap to start fresh with your own →" (actionable) |
| Go-live confirm | type "GO LIVE" to confirm + red "permanently deletes" | Two-button: "Clear the demo and start with my real properties" / "Cancel" + "This removes the sample data — it was never real." |
| Portfolio selector / copy | "Portfolio" | "Your rentals" (selector hidden when only one) |
| Nav landing screen | "Dashboard" (web) / "Rental Command" (mobile app-bar title) | "Today" / "Home" (consistent both) — reserve product name for branding |
| Mobile landlord tab | "Messages" | "Inbox" *(or keep "Messages" — pick one per `label-glossary.md`; don't run both)* |
| Empty states | "No properties found." / "No tenants found." | "A property is one building or address. Add your first to get started." + big "Add your first property" button |
| Tenant 1099/owner framing | "Owner statement — for reference, not a tax document…" / bare "1099 / W-9" | "If you paid any repair person more than $600 this year, the IRS wants you to send them a 1099. First you need their W-9 (their tax info). We'll text them a request." |

---

## Sequencing recommendation

1. **Phase A first (a single sprint).** Pure copy/label/hide + the two one-line nav-bug fixes (A14 back-button, A3 dedup) — these remove the loudest "this is jargon / I'm lost / two buttons do the same thing" friction with near-zero risk, and several are also engineering-audit bugs.
2. **Lever 1 (the tour) immediately after / in parallel** — C5 + B6 + the "fire at You're all set!" hook. It's the literal unmet ask; build the web chained tour from the existing engine, then the Flutter twin, gated on a shared `tourSeen` flag (B15/C9).
3. **Phase B structural** — lead with B2 (5 nouns) + B3 (one Money hub) + B4 (one setup spine), then B7 (one-tap scan confirm) and B11/B12 (Simple-mode gating). These are the "make it feel half the size" wins.
4. **Phase C prototypes** — C1/C2/C3/C4 are the boldest re-inversions (setup-as-conversation, photo-as-front-door, home-as-one-question, AI-as-nav). Prototype on mobile first since it's the primary product.

**One enforcement mechanism ties it together:** a single shared glossary + nav manifest + tour-copy registry (`label-glossary.md` extended) consumed by both clients, with a build-failing meta-test on banned jargon and on cross-platform reachability parity (B15). That's what keeps web and mobile from drifting apart again after this work lands.

---

## Risks, tradeoffs & devil's-advocate

A skeptical counter-read of the plan above. The plan is strong where it kills *duplicate entry points* and *jargon* — those are nearly free wins and most of the rip-out list survives scrutiny. The risk concentrates in a smaller set of items that touch **money accuracy, tax/legal records, destructive-action guards, and irreversible deletion** — plus the two places where "make it simple" quietly becomes "make a decision *for* the user that's sometimes wrong." For each, the question is the same: does this remove friction, or does it remove a *capability the landlord will need within their first year* and now can't find?

**Cross-cutting principle for the whole plan:** the safe version of almost every rip-out is **"default-hidden behind a discoverable `Advanced` / `More` gate," not "deleted."** "Hidden" is reversible by the user in one tap; "deleted" requires a developer and a redeploy. For a product whose users range from "Facebook-comfortable" to "runs a 30-unit portfolio and knows what Schedule E is," the escape hatch is what keeps hand-*holding* from becoming hand-*cuffing*. The plan says this in places (B8, B11, B13) but the cut-list table and a few Phase-C ideas slip into literal deletion or into hiding things with **no escape hatch at all**. Those are the items below.

A second cross-cutting risk: **the meta-test that bans jargon terms in `.svelte`/`.dart` strings (B15) can itself cause harm.** "Schedule E", "1099", "W-9", "Reconcile", "Escrow", "Security deposit" are not jargon to be purged — several are *legal terms of art the user must eventually match against an external document* (their tax return, an IRS form, a bank statement, a state deposit-accounting statute). A lint that forbids the literal string "1099" will push developers to *rename the concept* and leave the user unable to map the app's word to the IRS form in their hand. **Allowlist legally-load-bearing terms** (keep them, gloss them — exactly as the plan already does for "Schedule E — the rental page of your tax return") rather than banning the string outright.

### R1 — A8: defaulting the payment form to "Paid" and hiding status is the single most dangerous money change in the plan
**Challenge.** Status on a payment is not cosmetic — it's the field that drives "Who owes me," past-due totals, late-fee logic, and the tenant ledger. Defaulting to **"Paid"** and hiding the dropdown behind "More options" optimizes for the one case (recording a payment that already arrived) at the cost of silently corrupting the others. A landlord who is *scheduling* expected rent, recording a **partial** payment, or marking rent **late** will — because the friendly default did the thinking for them — record money as collected that wasn't. That doesn't just mislabel a row; it makes "Rent past due" read **$0 when a tenant is actually behind**, which is the exact number this whole app exists to get right, and it can suppress a late fee the lease entitles the landlord to charge. "A correct-looking number that's wrong is worse than an obviously-missing one" applies directly: the user trusts the dashboard and stops chasing rent that's owed.
**Does it just move complexity?** Partly worse — it *hides* a decision the user still has to get right, so the error surfaces later (at "why does Jane show paid-up when she paid half?") where it's far harder to trace back to a defaulted dropdown.
**Safer alternative.** Keep status visible but make it a **3-chip plain-English toggle, not a 5-item dropdown**: **"Got it all" (Paid) · "Got part" (Partial) · "Still waiting" (Scheduled/Late)**. Default the chip by **context, not blanket "Paid"**: if the amount entered equals the rent due → preselect "Got it all"; if less → "Got part"; if the due date is future → "Scheduled." Keep "Forgiven/Waived" under "More options" (it's genuinely rare). This preserves one-tap speed for the common case without ever silently overstating collection. If the team insists on a flat default, default to **"Scheduled/Still owed," not "Paid"** — under-counting collection is a recoverable annoyance; over-counting it hides money the landlord is owed.

### R2 — A7: collapsing the expense form to 4 fields can quietly degrade tax deductions and reimbursement accuracy
**Challenge.** The instinct (don't show 25 fields for a coffee receipt) is right; the execution has two traps. (a) **Defaulting Category to "Other"** is a tax landmine: expense category *is* the Schedule E line. An expense filed under "Other" instead of "Repairs/Cleaning/Insurance/Mortgage interest" is a **mis- or under-claimed deduction** — real money lost at tax time, and the user never sees the error because the form felt easy. (b) Burying **tax rate / vendor tax ID / billable-to-owner** behind a disclosure is fine for a solo owner buying a $12 part, but the **vendor's tax ID (for 1099s)** and **billable-to-owner** are exactly the fields a property-manager persona needs, and the plan elsewhere (C6) wants to *auto-generate 1099s* — which is impossible if the data was hidden at entry and never captured.
**Does it over-simplify an inherently-complex domain?** Yes — rental accounting is mildly complex *because the IRS makes it so*; the app can hide the complexity but cannot make the categories not matter.
**Safer alternative.** Keep the 4-field default, but (1) **never default Category to "Other"** — when the description or scanned vendor gives a signal ("Home Depot," "State Farm," "plumber"), map to the right Schedule E category and **show the guess as an editable chip** ("Filed under *Repairs* — change?"). Only fall back to "Other" when there's genuinely no signal, and surface a gentle once-a-year nudge ("12 expenses are filed under Other — want to sort them for taxes?"). (2) Keep the **"Add more details"** disclosure, but ensure the 1099-relevant fields (vendor tax ID, billable-to-owner) live there for the *Pro* persona rather than being removed — the disclosure is the right home for them, deletion is not.

### R3 — B3 + C6: burying Tax/Reports behind "More reports" / replacing the whole accounting surface with one "Get my tax packet" button risks orphaning records the user is legally required to produce
**Challenge.** Demoting `/tax`, `/reports`, `/owners-report` from nav to a year-end button is mostly defensible **for a solo landlord** — but two things bite. (a) **Discoverability at the moment of need.** Tax artifacts are needed under deadline pressure (accountant emails in March: "send me your P&L and Schedule E by Friday"). If the only door is one button labeled "Get my tax packet," a user who thinks in the accountant's words ("where's my P&L? my 1099 report?") may not recognize it and concludes *the app can't do it* — then exports to a spreadsheet by hand, the precise pain this product removes. (b) **Audit-trail / owner-statement obligations.** For the property-manager persona, the **owner statement** isn't a convenience report — it's the document they're contractually (sometimes by state law) required to give the owner each month. Folding it under "More reports → Advanced" for *everyone* is fine; folding it for a PM who manages others' property removes a deliverable they're obligated to produce.
**Does it just move complexity?** For the common case, genuinely removes it. For the deadline/PM case, it *hides* it where the user can't find it under stress — which feels like removal.
**Safer alternative.** Ship **C6's "Get my tax packet" button as the hero** *and* keep a discoverable **"All reports & tax forms"** link inside Money → so the outcome-named path leads, but the accountant-literate user (or the PM with an obligation) can still find the named artifact. Inside that catalog, keep the IRS/accounting names **with glosses** (the plan already does this well in the language table). Gate the *owner statement* behind the "I manage for other people" signal — but when that signal is on, treat it as **first-class, not Advanced**, because for that persona it's a core monthly deliverable.

### R4 — "Go-live" confirmation: replacing the typed "GO LIVE" + red warning with two soft buttons weakens a destructive-data guard
**Challenge** (language table, "Go-live confirm" row). The typed-confirmation pattern ("type GO LIVE") exists precisely because the action **permanently deletes the sandbox data and is irreversible**. Swapping it for two ordinary buttons ("Clear the demo and start with my real properties" / "Cancel") plus a reassuring "it was never real" line lowers the friction on an *unrecoverable* action. The stated reassurance is only safe if the data truly is *only* demo seed — but the plan's own C1/C2 push users to **set up a real rental during the sandbox/explore flow** (B5 "skip — I'll scan my lease instead," C1 "set up ONE rental together"). If any real data can exist in the explore state by the time this button is reachable, a one-tap "Clear the demo" **destroys real work** with a single mis-tap.
**Where hand-holding becomes hand-cuffing — inverted.** Here the plan *removes* a guard rather than adds one; the risk is the opposite of rigidity (data loss), but it stems from the same "make it frictionless" reflex.
**Safer alternative.** Keep the friction **proportional to reversibility**. Two buttons are fine *if* the destructive button is the **non-default / secondary-styled** one and the dialog **states what will be lost in specifics** ("This deletes the 3 example properties and 12 example payments. Your real data stays."). Critically, **detect whether any user-created (non-seed) records exist**; if they do, do **not** offer "clear everything" — offer "keep what I've added, remove only the examples," or block and explain. Never let a single tap delete data the system can't prove is disposable.

### R5 — B11 / C7: auto-detecting "single-landlord mode" and *physically removing* Owners/Team/Portfolio/Banking is a correctness bet that fails closed in the wrong direction
**Challenge.** "Auto-detect single-landlord mode and physically remove enterprise surfaces" (B11) is the riskiest *structural* idea because **the detection will be wrong for real users**, and the failure mode is *the feature isn't there and can't be turned on from where the user is looking*. A landlord who (a) co-owns a duplex with a sibling (needs Owners/owner-split), (b) just took on managing one unit for a friend (needs billable-to-owner + owner statement), or (c) wants bank reconciliation because they're meticulous — gets an app that has **silently amputated** the capability based on a heuristic. "The data model already supports everything — this is purely a visibility gate" is true and is exactly why **deletion is the wrong verb**: if it's purely visibility, it must be **one obvious toggle away**, not gated behind "the user added a second owner" (chicken-and-egg: they can't add the second owner if Owners is hidden until they have a second owner).
**Does it over-simplify?** It risks defining the product as single-owner when a meaningful slice of small landlords are exactly the co-own / manage-a-friend's-place / "house-hack" cases that *need* the multi-party features.
**Safer alternative.** Default to Simple, but make Pro a **single always-visible switch** ("I manage property for other people / I want full accounting" in Settings → Basics, not buried in Advanced) — the plan proposes this toggle; the fix is to ensure it is **never circularly gated** and never requires data the hidden surface is needed to create. **Hide, don't delete; reveal by toggle, not only by usage-signal.** And surface a breadcrumb at the point of need: when a user types/asks the AI "split this with the other owner" or "send my co-owner a statement," the app should *offer to turn on Pro mode* rather than answer "not available."

### R6 — B4 + B16: "delete the route" (vs redirect) strands bookmarks, deep-links, and the savvy user; deletion is not reversible by the user
**Challenge.** B4 says **"Delete the standalone `/get-started` route"**; B16 says **"delete/redirect `/owners/vendors`"** and "keep one of `/activity`//`/audit`." Hard-deleting a route 404s any **bookmark, emailed deep-link, push-notification target, or muscle-memory URL** pointing at it, and removes a surface a power user may have relied on. The *consolidation* is right; the *deletion* is the avoidable part. (`/audit` vs `/activity` specifically: if these are not byte-identical — e.g. one is an immutable security/audit log and the other a friendly activity feed — collapsing them could **destroy an audit trail's separateness**, which matters for disputes/compliance. Verify they're truly the same surface before merging; the plan assumes duplication without confirming semantic identity.)
**Safer alternative.** **Redirect, never delete.** Every retired route 301/permanent-redirects to its replacement (`/get-started` → the unified setup; `/owners/vendors` → `/vendors`; the demoted money routes → `Money` with the right tab/anchor). Redirects cost almost nothing, preserve every link, and let you *measure* what's still hit before truly removing anything. For `/activity` vs `/audit`: **confirm they're the same data** first; if one is an audit log, keep it (renamed/グlossed) and only merge the *friendly* views.

### R7 — C3 + C4: "home is ONE question" and "AI becomes primary navigation" trade deterministic findability for probabilistic intent — the long-tail user can get *stuck*
**Challenge.** These are the boldest inversions and the plan correctly files them as *prototype-first*. The devil's-advocate point is about the **failure mode when the AI is wrong or the answer isn't a card.** (a) **"2 things need you today" + a card stack** is delightful when the AI's triage is right and dangerous when it's not: a landlord whose real #1 concern (a lease expiring in 30 days, a deposit deadline mandated by state law within X days of move-out) **isn't in the AI's top-2** now has *no list to scan* — the plan moves field queue/messages off the home scroll, so the safety net of "I can see everything and judge for myself" is gone. (b) **AI-as-nav** assumes the user can *articulate* what they want; the non-technical user who doesn't know the app *can* do a thing won't ask for it, and conversational nav has **no peripheral vision** — you can't stumble onto a feature you didn't know to name. Structured nav teaches the app's capabilities just by existing; a chat box doesn't.
**Where hand-holding becomes hand-cuffing.** A single-question home is maximally hand-held — and maximally rigid: there's exactly one thing the app thinks you should do, and if it's wrong you're stuck with no map.
**Safer alternative.** Keep the AI hero **on top of**, not **instead of**, a scannable structure. "2 things need you today" should sit above a still-present (collapsed-but-one-tap) "see everything" list, and the 5-noun nav must remain the **deterministic backstop** so the AI is an *accelerator*, never the *only* door. Cap-to-3 with "see all" (B6) is the right pattern; the danger is only when "see all" disappears. Treat C3/C4 as an **additive surface gated behind the structured app**, and instrument the prototype for the specific question "what % of sessions did the user need something the AI didn't surface, and could they still reach it?"

### R8 — B6 + B1: deferring dashboard modules and hiding the real checklist "until there's data" can hide time-sensitive obligations during the highest-stakes early window
**Challenge.** Calming the first-run dashboard is good. But "**collapse the severity briefing / KPI strip / needs-attention list until there's real data**" assumes the early period is low-stakes. It often isn't: a landlord who onboards *because* a tenant just went 30 days late, or whose new lease has a **deposit-return clock** ticking, needs the "needs attention" surface **on day one** — that's *why they signed up*. Hiding it behind "once you've been oriented" could bury a legally time-boxed task (deposit accounting, notice-to-cure windows) under a welcome card.
**Safer alternative.** Defer the **analytics/vanity** modules (leasing mix, occupancy charts, P&L strip) aggressively — those genuinely need data to mean anything. But **never defer a non-empty "needs attention" list**: if there are 0 real items, hide it (clean first-run); the moment there's ≥1 genuinely time-sensitive item, show it **above** the welcome card, in plain language. "Calm" should mean *hide what's empty or vanity*, not *hide what's urgent*.

### R9 — B7: hiding the "Create vs Link" toggle and field grid behind "Change something" is right — with one guardrail
**Challenge (smaller).** One-tap confirm of an AI lease/receipt proposal is the best idea in the plan. The only risk: the AI's **default choice between "create new" and "link to existing"** is now invisible, and a wrong default writes bad data confidently — e.g. it **creates a duplicate property** when the address already exists, or **links to the wrong existing tenant** with a similar name. A duplicate property silently splits a landlord's money/occupancy across two records (wrong totals); a mis-link attaches a lease/payment to the wrong person.
**Safer alternative.** Keep the one-tap confirm, but make the *summary card state the consequential choice in plain words* so it's reviewable without opening the editor: "**I'll add a *new* property at 123 Main St**" vs "**I'll add this lease to your *existing* 123 Main St**." Run a **duplicate-address / similar-name check** before defaulting to "create," and if there's a near-match, *don't* hide the decision — that's the one case to surface the picker by default. The escape hatch ("Change something") stays for everything else.

### R10 — A10 / A11 / "rip out the word 'portfolio'": hiding the portfolio selector when there's one portfolio is safe; hardcoding "one portfolio" assumptions is not
**Challenge (smaller).** Hiding the selector for single-portfolio accounts and saying "your rentals" is good. The trap is if "single landlord = exactly one portfolio" gets baked in such that a user who legitimately keeps **two portfolios** (e.g. personal rentals vs an LLC's rentals, often a *tax-motivated* separation) loses the ability to switch, or worse has new records silently land in the wrong one. The separation is frequently deliberate and tax-relevant.
**Safer alternative.** Drive the selector's visibility off **`count > 1`, dynamically** (the plan implies this — make it explicit and tested), never off a "simple mode" flag. The instant a second portfolio exists, the selector returns automatically. Keep `getCurrentPortfolioId()` threaded (the plan does). Don't let "hide the chrome for the 1-portfolio case" become "assume nobody has two."

### Summary: which rip-outs are safe as-is vs need the guardrail

- **Safe to delete/collapse as written** (pure duplication or pure jargon, no record/accuracy/legal stakes): cut-list #1 (duplicate capture tiles), #4 (seeding theater), #5 (duplicate AI entry points), #8 (header quick-action duplicates), #9 (accordion behavior), #19 (SignalWire off the *checklist* — keep it in Settings), #20 (dead help links), plus the entire plain-language relabel table **except** the legally-load-bearing terms (keep+gloss "Schedule E / 1099 / W-9 / Reconcile / Security deposit").
- **Safe only as HIDE-behind-discoverable-Advanced, never DELETE, and never as an irreversible default** (re-verb the cut-list from "delete" to "default-hidden + redirect"): #2 (money routes → keep a findable catalog, redirect old URLs), #3 (`/get-started` → redirect), #7 (Browse → trim, keep escape hatch), #12 (Owners/Team/credentials → toggle-revealable, owner-statement first-class for PMs), #16 (duplicate routes → **redirect** + verify `/audit` isn't a distinct audit log), #17 (CSV import → Advanced), #18 (Recurring/Inspections → Advanced, still reachable).
- **Needs the accuracy guardrail before shipping** (can corrupt money/tax/deposit records if defaulted wrong): A7 (don't default Category to "Other"), A8 (context-aware status, never blanket "Paid"), B7 (duplicate-detection before "create"), C6 (keep a findable named-report catalog under the outcome button), the go-live confirmation (proportional friction + detect real data), and B6/B1 (never defer a *non-empty urgent* list).
- **Prototype-and-instrument, keep the deterministic backstop** (don't replace structure with probability): C3, C4 — AI hero *on top of* scannable nav, not instead of it.
