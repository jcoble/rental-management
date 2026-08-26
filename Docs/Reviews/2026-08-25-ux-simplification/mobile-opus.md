# Rental Command mobile UX simplification — what the landlord sees and does on the phone — Opus 5

## The landlord's day (what the app currently makes them do)

1. Land on **Today**: greeting, briefing bullets, money snapshot, latest messages, work orders, a "Getting started" checklist (`mobile/lib/features/home/home_shell.dart:2222-2357`).
2. Any real task means leaving Today for one of four hubs, each a *horizontally scrolling* pill bar of up to six destinations — 21 destinations behind 5 tabs (`mobile_destination.dart:258-460`, `mobile_domain_hub.dart:395-404`).
3. The floating button is a **closed speed dial**: tapping it opens a stack (`Scan / Add`, the screen's action, `Record`, `Assistant`) before you can pick anything. **Every "Add X" costs two taps, not one** (`mobile_quick_action_fab.dart:400-495`).
4. Every multi-step sheet is a `TabbedFormSheet` that **clamps forward jumps to one step at a time** (`core/widgets/tabbed_form_sheet.dart:104-116`).
5. Tapping **Money** shows a pill row (`Portfolio | Insights | Ledger | Deposits | Banking | Reports`) whose middle four open the *same screen* with a *second* chip row (`Overview | Activity | Ledger | Reports`) and sometimes a *third* segmented button (`Payments | Expenses`) (`mobile_destination.dart:326-365`, `money_screen.dart:50,388-411,1758-1769`).
6. Tapping a unit opens a "command center": 6 more pills, 15 sub-views (`unit_command_center_screen.dart:254-366`).
7. Every payment, expense, deposit and property screen carries an **"Accounting impact"** card linking to `Dr`/`Cr`, `Balanced ✓`, `Posted`, `Reversal`, `Audit link` (`accounting_impact_card.dart:185,223-246`, `journal_detail_sheet.dart:299-395`).
8. Adding a property is **8 taps, 20 fields, 5 steps**, ending in a **Tax Basis** step asking for purchase price, land value, in-service date and *manual annual depreciation* (`property_form_sheet.dart:490-892`).
9. Logging a repair and assigning a vendor is **12 taps across 5 steps, 13 fields** (`create_work_order_sheet.dart:348-751`).
10. Ending a tenancy is called **"Return possession"** and asks for an explicit *disposition* per household member and per "tenant portal grant" (`return_possession_sheet.dart:124-247`).

## Workflow-by-workflow

### Onboarding
- Today: 2 screens, 1 decision, then a 7-task checklist — but the first real thing is the full 5-step, 20-field property sheet (`onboarding_live_setup_screen.dart:244` → `property_form_sheet.dart`).
- Three words for the same object: "unit" in forms, "rental space" in the checklist (`getting_started_tasks.dart:141-147`), "rental" in the property sheet. Checklist task #1 "Confirm who owns the properties" is always "me". The seeding screen says "Setting up your sandbox…" (`onboarding_seeding_screen.dart:206`).
- Proposal: drop the owner task when the workspace has one owner entity equal to the signed-in user; pick one word — **rental**; "Setting up your sample portfolio…". Effort S. Risk none.

### Add a property / unit
- Today: 8 taps, 20 fields (5 required), 5 steps `Identity | Rentals | Address | Operations | Tax Basis`. Two model-leak sentences (:536, :670). Property detail renders the raw enum `SingleRental`/`MultiRental` as "Rental setup" (`property_detail_screen.dart:1314-1316`). Unit sheet: 3 steps, 7 fields.
- Proposal: create sheet → 2 steps (`Identity`, `Address`); `Operations` and `Tax Basis` edit-only from property detail; delete the model-leak sentences; label the enum. 8 taps/20 fields → 4 taps/8 fields. Effort M. Risk none.

### Find a tenant — listing, application, screening
- Today: creating a listing is not reachable from the Leasing landing; only Rentals → Units → unit → Leasing → Listing → "Prepare listing". Two Zillow cards (Guided/Connected) with publication state, listing ID, confirmation checklist, "untrusted hints" queue, version-drift banner (`unit_command_center_screen.dart:1024-2069`). Application detail: 7 sections, 13 dialogs; list shows `Property #12 · Unit #34` (`applications_list_screen.dart:282-322`).
- Proposal: "List this rental" on the Leasing tile and unit summary; real names instead of ids; one Zillow card (Guided) with Connected/publication state/hints behind "Advanced publishing" until an adapter ships; one screening card. Effort M. Risk low (Connected already gated on `isConfigured`).

### Lease — template, e-signature
- Today: ~15 taps from approved application to issued agreement; "Prepare move-in" 4 steps, up to 21 fields, silent `validate()` gating (Next greys out with no reason); separate 3-step issue sheet; landlord cannot e-sign in-app (`agreement_draft_action_sheets.dart:537-539`). Lease detail offers 10 agreement verbs.
- Proposal: surface step-validation as field errors; lease detail → **Renew / Change the lease / End the lease** + "More…"; rename "Prepare move-in" → "Start a lease". Effort M. Risk low.

### Move-in
- Today: 2 taps — the cleanest flow. Only the confirmation copy leaks server internals (`lease_detail_screen.dart:236-257`).
- Proposal: "This marks the tenant as moved in." Effort S.

### Collect rent / see who owes
- Today: "Who's behind" is good (`overdue_screen.dart:66-339`) but only reachable when someone is past due (`money_snapshot_card.dart:47-56`); the "Rent still owed" tile is not tappable (`money_screen.dart:666-683`). Recording a payment for a current tenant: 7 taps + typed search; two competing record-payment sheets (7 and 8 fields).
- Proposal: make "Rent still owed" tappable → OverdueScreen; add "Rent came in" to the Today FAB; delete `money/record_payment_sheet.dart`, keep the tenant-receipt sheet. 7 taps/two forms → 3 taps/one form. Effort M. Risk low.

### Handle maintenance
- Today: 12 taps; "New Work Order" 5 steps, 13 fields, 3 required; vendor behind two "Next" presses and labelled optional; empty state says "Tap + to create one" but the button is a scanner glyph needing two taps (`create_work_order_sheet.dart:90-751`, `work_orders_screen.dart:556-560`). Detail carries a technician-responsibility sheet with "Assigned by property manager" reasons and a "Private management comment" checkbox (`work_order_detail_screen.dart:183-360,1103`).
- Proposal: one screen — Property/unit, What's wrong?, Details, Priority, **Who's fixing it** — with scheduling/cost/access under an expander; fix the empty-state copy. 12 taps/5 steps/13 fields → ~5 taps/1 screen/5 fields. Effort M. Risk none.

### Move-out
- Today: "Return possession", 3 steps, a disposition per household member and per portal grant, mandatory "Turnover reason", two server-internals sentences (`return_possession_sheet.dart:124,155,199,247,257-261`).
- Proposal: rename to **Move-out**; default every member to "End household membership" and every grant to "Revoke access now" under "Change what happens to their access"; reason optional; delete the server sentences. 3 steps/N+M+1 decisions → 1 screen/1 confirm. Effort M. Risk low.

### Daily loop — "what needs my attention": Today is well-built. Keep.

### Daily loop — "record this receipt"
- Flagship works; but the review screen shows a "Save command" row, uppercase headers, raw enum categories (`AutoTravel`, `CleaningMaintenance`, `MortgageInterest`) (`scan_review_screen.dart:2591-2611,514-529`); manual expense is 4 steps/24 fields with a `Document kind` of `ManualBill`.
- Proposal: delete the "Save command" row; map categories through `expense_models.dart:8-21`; title-case headers; manual expense → Property, Description, Amount, Date, Category + "More details"; drop Document kind. 24 fields → 5. Effort M.

### Daily loop — "reply to a tenant"
- Three channel chips on every reply; new conversation asks To/Subject/Message/Channels (`message_detail_screen.dart:466-490`).
- Proposal: remember the channel per conversation ("Sending by text — change"); drop Subject from replies. Effort S.

## The three worst offenders (ranked)

**1. Four stacked levels of tab navigation plus a speed-dial tax on every action.** Bottom nav (5) → hub pill bar (up to 6, scrolling) → screen chip row → segmented button. "Ledger" and "Reports" each mean two different things at two levels; Portfolio and Insights share an icon. Units repeat it with 6 pills + 15 sub-views. The FAB is closed by default and sheets forbid skipping steps. **Single change:** collapse the Money hub to two pills — "Money" and "Deposits" — folding Banking/Reports/Portfolio into the Money screen's Reports chip (an edit to `moneyHubDestinations`). Effort S. **Second:** fire the FAB's primary action directly when a screen registers exactly one. Effort S.

**2. Double-entry bookkeeping shipped to a landlord's phone.** Accounting-impact cards on five daily screens, a General ledger with Debit/Credit/Posted, a journal sheet with Dr/Cr/Balanced/Reversal/Audit link, a filter asking for a numeric Property ID, help articles "What is the general ledger?" and "Why every record has two sides", Section 1250 est., a depreciation Convention field, "The debt-service worker fills this in monthly". **Single change:** gate `AccountingImpactCard` on the existing `AccountingDetailMode.advanced` preference (`journal_detail_sheet.dart:19-78`) — one conditional at six call sites. Effort S. Risk none.

**3. Management-company machinery on the landlord's default screens.** Ten instances: ownership splits, Owners as a top-level pill with a 10-field form, Management fee % on every property, owner statements/distributions, a 1,449-line Team system, technician-responsibility sheet, Zillow Guided+Connected state machines, a notice-compliance engine with template versions and jurisdiction review, vendor 1099/W-9 fields, three separate notification screens. **Single change:** extend the existing capability gating (Owners/Team/Banking/Deposits already hide; dropdowns already hide with one option, `mobile_shell_actions.dart:170-172`) to Management fee %, the ownership-split card, the technician sheet and the Zillow Connected card: hide when the workspace has one owner entity and no team members. A condition, not a mode. Effort M.

## Jargon to rename

| Current term | Plain English | Where |
|---|---|---|
| Return possession | Move-out | `lease_detail_screen.dart:1540`; `return_possession_sheet.dart:124` |
| Disposition / access disposition | What happens to them / Their login | `return_possession_sheet.dart:177,227` |
| Lifecycle | Status | `leases_list_screen.dart:173` |
| Relationship / relationship number | Tenancy | `leases_list_screen.dart:270,300` |
| Governing agreement | Current lease | lease detail, agreement sheets |
| Successor treatment | What to do with this add-on | `successor_agreement_sheet.dart` |
| Ordered signer snapshot | Who signs, in order | `agreement_draft_action_sheets.dart:547` |
| Command center | (drop) | `properties_list_screen.dart:395`; `unit_command_center_screen.dart:729`; `mobile_destination.dart:281` |
| Stage (units filter) | Where it's at | `units_list_screen.dart:194` |
| Turnover / make-ready | Getting it ready | `unit_command_center_screen.dart:284,3078` |
| Workspace state / Publication state | Listing status | `unit_command_center_screen.dart:1068,1150` |
| `SingleRental` / `MultiRental` | One rental / Building with units | `property_detail_screen.dart:1314` |
| Sandbox | Sample portfolio | `onboarding_seeding_screen.dart:206` |
| Rental space / unit / rental | **rental** | `getting_started_tasks.dart:141` vs forms |
| Convention; Section 1250 est. | (behind Advanced) | capital-asset sheet; `property_detail_screen.dart:1806` |
| "the debt-service worker fills this in monthly" | "Payments appear here each month." | `property_detail_screen.dart:2020` |
| "the server will stop the delete" | "You'll need to clear those first." | `property_detail_screen.dart:216` |
| "Each server-shaped row contains its automation and currently bound immutable template" | rewrite | `tenant_notices_screen.dart:129` |
| Team routing | Who gets told what | `settings_screen.dart:124` |
| General ledger / Journal / Posted / Dr / Cr / Accounting impact | (hide in simple mode) | `general_ledger_view.dart`; `journal_detail_sheet.dart:299-375`; `accounting_impact_card.dart:185` |
| Ledger (Money hub pill) | delete — duplicates the chip | `mobile_destination.dart:350` |
| Portfolio vs Insights | Trends (merge) | `mobile_destination.dart:326-336` |
| Save command | delete the row | `scan_review_screen.dart:2611` |
| `AutoTravel`, `CleaningMaintenance`, `MortgageInterest` | Auto & travel, Cleaning & maintenance, Mortgage interest | `scan_review_screen.dart:514-529` |
| Property ID / Unit ID (typed numbers) | pickers | `general_ledger_view.dart:676-694` |
| Property #12 · Unit #34 | property name · unit number | `applications_list_screen.dart:282` |

## Keep — intrinsic complexity
Screening/FCRA/adverse action; e-signature outside the landlord's app; immutable signed PDFs and the correction/reversal model; security deposits as a separately held balance (`money_screen.dart:595-614`); depreciation and tax basis (off the create path, not deleted); 1099/W-9 tracking; the typed GO LIVE confirmation (`go_live_sheet.dart:143`); the capability system (extend, don't replace with a mode toggle).

## Not covered
- Deposits, banking reconciliation and owner reports were not walked screen by screen (the delegated money/scan read never returned).
- Tenant-portal and technician shells.
- Nothing was run; tap counts are minimum happy-path counts from code.
- Two incidental defects: `leases_list_screen.dart:311-315` — empty-state button reads "Open applications" but calls `onPrepareMoveIn`; `application_detail_screen.dart:108-123` — `_prepareMoveIn` dereferences `application.unitId!` with no guard, so an approved application with no unit crashes on tap.
