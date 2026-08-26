# Rental Command web UX simplification — what the landlord sees and does — Opus 5

Verified against the running app at https://localhost:5667 with sample data, viewport 1710x990 (33 screenshots in scratchpad/ux/web-shots/).

## The landlord's day
1. Sign in and land on a hero band whose top third is the product's own tagline before any work appears.
2. Read a left rail of **26 destinations**: 3 pinned, 4 collapsible groups (Money 4, Rentals 6, Work 3, Inbox 2), Ask, Help, Settings group of 6 (`AppShell.svelte:113-194`).
3. Trust one of three different numbers for "who owes me": dashboard `$5,175`, Money overview `Who's behind $0.00`, Money header strip `Past due $0.00 (0)` (`/`, `/accounting`).
4. To log a receipt: Money → Activity → Add expense → **7-step, 26-field wizard** whose first step is four optional dropdowns all reading "No property / No unit / No vendor / No work order".
5. To log "the sink leaks": a **6-step wizard** Issue → Triage → Location → Schedule → People → Budget.
6. To add a property: "New Property" throws the landlord out of Properties into the **6-step onboarding wizard** "Let's set up your portfolio — Step 4 of 6", with 8 properties already on file.
7. To start a lease: "This creates one planned tenant relationship, account, and agreement draft together", then "Exact rental", "Household relationship begins", "Planned possession", during "this atomic move-in".
8. Choose between "Simple" and "Advanced" money — where Simple still shows six tabs, "Financial position", "Loan balance", "Cash after tenant deposits −$20,175.00".
9. Pick from **22 reports** across five sections, five of which warn "Opens another report page".
10. Meet management-company machinery: Owners, Team, Team routing, Lease Templates, Chart of Accounts, Trial Balance, Banking, 1099/W-9, Vendor ratings.

## Workflow-by-workflow

### Onboard
- Today: `/choose-setup` → `/setting-up` → `/onboarding`, 6 steps (Portfolio, Owner, Import a lease, Property, Tenants, Lease) + optional My alerts (`wizard-steps.ts:51-137`). The Owner step asks a solo landlord for an owner name **and tax ID** before the first property; `wizard-steps.ts:64-73` concedes "If you own them yourself, that's just you as a person".
- Proposal: default the owner to the signed-in user and drop the step; keep it in Settings → Owners. 6 steps + SSN prompt → 5 steps, no tax ID until a 1099 is needed. Effort S. Risk none. (The onboarding copy is the voice standard the rest of the app should follow.)

### Add property / unit
- Today: "New Property" calls `goto('/onboarding?step=property&from=properties')` (`properties/+page.svelte:230-233`); editing (`openEdit`, :235) is a plain inline form on the same page. Adding is harder than editing.
- Proposal: point `openCreate` at the inline form. Effort S. Risk none.

### Find a tenant
- Today: `/applications` is a filter, "Get application link", empty table; no listing surface and no screening step in the management shell. Listings live only in the Leasing-role shell at `/leasing/rentals`, gated to `experiences: ['Leasing']` (`experience-policy.ts:74-80`) — a solo landlord who is their own leasing agent cannot reach listings.
- Proposal: none from this lane — a missing path, not excess. Owner decision: should a one-person portfolio see the Leasing shell, or merge those screens into `/applications`? Effort L.

### Lease
- Today: `/leases` → Create lease → 3-step dialog; separate `/lease-templates` page with uploader and "Design fields". Densest jargon in the app (`PrepareMoveInDialog.svelte:377,436,511,648,662`): "one planned tenant relationship, account, and agreement draft together", "Exact rental", "atomic move-in", "Household relationship begins", "Planned possession … does not mark the unit occupied". `/leases` columns "Household / Rental / Status / Lease / Review"; 3 of 12 rows are empty records rendered as leases.
- Proposal: rewrite strings per the rename table; drop the subtitle; default "Household relationship begins" to today; hide "Planned possession" behind More. Effort S+S. Risk none. Not exercised: `/sign/[token]`, field placement.

### Move-in
- Today: folded into Create lease; deposits are a separate `/deposits` page whose help text warns against a mistake the UI allows ("do not create another holding").
- Proposal: "Deposit received" as a line on the lease dialog's Money & review step; `/deposits` stays for refunds/deductions. Effort M. Risk low.

### Collect rent / who owes
- Today: `/accounting/past-due` is **the best screen in the app** — "Who's behind", "5 rentals behind, owing $5,175", one card per tenant with **Record payment** and **Text**; Record payment has one required field (pre-filled) plus an allocation preview. But it is not in the nav (reached from a dashboard card), while `/accounting` (Money → Overview, in the nav) shows the same landlord `Who's behind $0.00`.
- Proposal: promote `/accounting/past-due` to the Money group's first item as "Who's behind"; demote Overview. Effort S. Risk none.
- **Trust issue (likely a bug):** dashboard "Who's behind $5,175 / Kept this month $15,046" vs Money overview "$0.00 / $0.00 / Cash available $0.00", same date range, same sample data. Fix before any cosmetic simplification.

### Handle maintenance
- Today: New Work Order = 6-step wizard (Issue · Triage · Location · Schedule · People · Budget); header offers three create buttons plus three filters. "Triage" is hospital vocabulary; steps 2, 4, 5, 6 are judgments made after a tenant reports, not while recording.
- Proposal: one form — title, property/unit, "More details" disclosure with priority/schedule/vendor/cost. 6 steps/10 fields/5 Next → 1 screen/3 visible fields/1 Save. Effort M. Risk low.

### Move-out
- Today: no dedicated route — only a scan type ("Move-out Notice") and an inspection type. Not exercised. Flag: the deposit-return step (legal deadlines) has no first-class screen while Trial Balance does.

### Daily: what needs attention
- Today: the dashboard "6 things need attention" list is well-designed, but sits below a ~380px hero band and beside an "AI · Today's Briefing" panel that restates it in one sentence; on first load it failed ("took too long to load / Action items unavailable").
- Proposal: hero band → one line; attention list full width at the top; briefing panel → one line or gone. Effort S.

### Daily: record a receipt/expense — the worst screen
- Today: Money → Activity → Add expense → 7 steps, 26 fields (`accounting/+page.svelte:318-335`): Source (4 optional links) · Details · Dates · Context · Vendor · Receipt · Adjustments (tax rate, tip, discount, shipping). Only 3 fields are required (description, amount, incurredAt; validation at :341-359) and none is on step 1. `/scan` ("Receipt / Bill — Becomes an expense record") already does the job one route away.
- Proposal: one screen — What did you spend on? / How much? / When? / Which property? — three pre-filled, "More details" disclosure for the rest, "Scan the receipt instead" at the top. 7 steps/26 fields → 1 screen/4 fields. Effort M. Risk low.

### Daily: reply to a tenant
- Today: `/messages` is genuinely simple. But three adjacent inboxes: `/messages`, `/notices`, and Settings → Notifications split into **three** pages (My alerts, Team routing, Tenant notices; `AppShell.svelte:186-193`); "Tenant notices" appears twice in the rail pointing at different pages; "Team routing" is offered to a one-person team.
- Proposal: one Settings notifications page with sections; hide Team routing until a second member exists. Effort M. Risk none.

### Money surface as a whole
- Today: Money group (Overview, Banking, Deposits, Reports) + Simple/Advanced toggle + 6 tabs on Overview + separate routes for P&L, balance sheet, trial balance, chart of accounts, year-end, past-due, plus `/tax`, `/owners-report`, `/reports` (22 reports). In Simple the landlord still sees the deposits-exceed-cash alert, "Financial position", "Loan balance", "Cash after tenant deposits −$20,175.00", and the General ledger tab (screenshot `accounting-main.png`). The toggle exists and hides nothing.
- Proposal: make Simple mean something — hide the General ledger tab, the Financial position card and the deposits-vs-cash alert; land on "Rent & payments". Effort M. Risk none (Advanced unchanged). Reports: group the five "opens another page" under "For your accountant"; lead with Rent roll, Who is behind, Income and expenses. Effort S.

## The three worst offenders
1. **Add expense — 7 steps, 26 fields, required fields on step 2.** One change: four fields on one screen + "More details" + "Scan the receipt instead".
2. **New Work Order — a 6-step wizard to record "the sink leaks".** One change: one screen — title, property, More details.
3. **"New Property" ejects the landlord into first-run onboarding** (`properties/+page.svelte:230-233`). One change: point `openCreate` at the inline edit form.
Runner-up, arguably first if real: **the same figure disagrees across dashboard, Money strip and Money overview.**

## Jargon to rename
| Current | Plain English | Where |
|---|---|---|
| atomic move-in | move-in | `PrepareMoveInDialog.svelte:511` |
| Household relationship begins | Move-in date | `PrepareMoveInDialog.svelte:648` |
| Planned possession | Keys handed over | `PrepareMoveInDialog.svelte:662`; `leasing/[record]/[id]/+page.svelte:146` |
| Exact rental | Which unit | `PrepareMoveInDialog.svelte:436,440,480` |
| "one planned tenant relationship, account, and agreement draft" | delete | Create lease subtitle |
| rent account | rent ledger | `leases/+page.svelte:123`; `PrepareMoveInDialog.svelte:377` |
| Household | Tenant | `/leases` column header |
| Triage | delete the step | New Work Order step 2 |
| Billable to owner | Charge this to the owner | `accounting/+page.svelte:1381`; `ExpenseDetail.svelte:557` |
| Financial position | What you own and owe | `/accounting` Overview |
| Trial Balance / General Ledger / Chart of Accounts | Advanced only | `/reports`, `/accounting` tabs |
| Portfolio | Your rentals | throughout; rail top item "Default Portfolio" |

## Keep
Deposits as separate non-income money; owner records and 1099/W-9 (hide until triggered); e-signature and the signed-lease artifact; double-entry accounting underneath (keep the ledger, keep it out of the landlord's face); "oldest open charges first" allocation preview — the model for the rest of the app.

## Not covered
Move-out end to end; e-signature and template field placement; tenant portal; Leasing and Maintenance role shells beyond their gates; `/import`; `/ai`; appointment and inspection detail; `/banking` with live Plaid; narrow viewports.

Cleanup: browser tree stopped, dev stack stopped (5665–5667 clear), `rc-ux-db` container removed; EdiPlatform containers untouched. Note: `/tmp/rentalcommand-api.log` held stale output from another lane's worktree, so the reviewer tracked its own PIDs.
