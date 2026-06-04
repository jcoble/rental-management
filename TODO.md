# Rental Command — Rapid Round TODO

Captured 2026-06-04. Working against the live dev stack (web :5667 / API :5666).

## Inbox (fire away)

### 1. Group the sidebar/drawer nav into collapsible sections
The flat list is too long (esp. for admins). Break it into logical, collapsible
dropdown groups. Proposed grouping (refine later):

- **Overview** — Dashboard, Insights
- **Get Started** — Setup, Import Data, Scan
- **Portfolio** — Properties, Tenants, Leases, Applications, Notices
- **Operations** — Maintenance, Appointments, Messages
- **Money** — Accounting, Banking, Deposits, Tax, Owner Reports
- **Directory** — Owners & Vendors
- **AI** — AI Assistant
- (pinned bottom, ungrouped) Notifications, profile

Notes: sections collapsible w/ remembered open/closed state; admin-only items in
their own group/section; active-route should auto-expand its parent group.

### 2. App header bar with high-frequency quick actions (badges)
Pull the most-used items OUT of the drawer and surface them as quick-access icons
in a top app header (always visible). Candidates:

- **Scan** — fast access to the flagship capture flow
- **Messages** — icon + unread count badge
- **Appointments** — small icon + badge with # of upcoming
- (consider) Notifications bell here too instead of bottom of drawer

These live on the header so they're one tap from anywhere; they can still also
appear in their drawer group. Badges should live-update (SignalR) where we have
counts (unread messages, upcoming appts).

### 3. Reconcile Activity feed + Audit log (currently two broken halves)
Investigation findings:
- `/activity` page reads `ActivityLog` table. **NOTHING writes to it** → always
  "No activity found." Dead viewer.
- A real append-only `AuditLog` table DOES exist (`AuditTrailService.LogAsync`)
  with actor / IP / operation / **old→new values** / timestamp / reason. It's
  already populated for: Scan, Screening, Lease e-sign (sent/signed/declined),
  Vendor dispatch, W-9 request, inbound SMS (rent-confirm, vendor-done),
  Applications. But it has **NO UI / no controller** → invisible.

Plan:
- **Pick one store.** Promote `AuditLog` as the source of truth; either retire
  `ActivityLog` or make the `/activity` page read `AuditLog` (with a clean DTO).
- **Transaction log (every entity):** write an audit/activity row on create /
  edit / delete / status-change for Payments, Expenses, Leases, Tenants,
  WorkOrders, Deposits, etc. — at least actor + what changed.
- **Deep audit (important things):** ensure full old→new diffs on Leases
  (create / edit / terminate), **signatures**, deposits/refunds, payment
  reversals — the legally-sensitive trail.
- **Viewer:** make `/activity` show the unified trail w/ working type + entity
  filters; add a per-record "History" tab on detail pages (Lease, Payment, etc.)
  filtered to that entity. Admin-only deep view (IP / raw old→new JSON).

### 4. Money pages: separation of concerns (Accounting / Deposits / Banking)
Findings: three money pages with confusing overlap.
- **Deposits = Security Deposits** (`SecurityDepositHolding`): money held IN TRUST
  per lease (held / deduct / return). Legally a liability, NOT income — often must
  be in a separate escrow acct + returned itemized within statutory days. It is a
  genuinely separate concern from Payments. **Problem is only that the UI doesn't
  explain that.** → Relabel + add a one-line explainer ("money held in trust per
  lease, separate from rental income"); keep it its own page. Consider grouping it
  visually near Leases since it's lease-scoped.
- **Banking** = raw bank feed (Plaid `BankTransaction`) whose ONLY unique job is
  **reconciliation** (does each recorded Payment/Expense actually clear the bank?).
  It should NOT read as a 2nd ledger. The recorded books live in Accounting.

Decision needed → **recommended approach (auto + assisted):**
1. Banking keeps the raw feed + connections + import only ("banking info stays").
2. Improve the match engine: currently scores on **amount (±$0.01) + date ≤7d**
   ONLY — add **merchant ↔ vendor/tenant name** into the score.
   (file: `BankingService.cs` `ScorePayment`/`ScoreExpense`/`SuggestMatch`.)
3. **Auto-match** when amount + name match + within N days + exactly ONE candidate
   + confidence ≥ ~0.95 → mark Matched with an `auto` flag (reversible + audited).
   Ambiguous / lower-confidence → suggestion chip for **one-tap confirm**. NEVER
   auto-match on a tie (multiple equal candidates) — that needs a human.
4. **Inline reconciliation badge on Accounting:** each Payment/Expense row shows
   green "✓ Cleared · {bank} · {date}" when matched, or a "Match?" chip w/ the
   suggested bank line (name/amount/date) → click to confirm. One place answers
   "what did I record?" AND "did the bank confirm it?".

### 5. Bank feed: "Personal / Ignore" action for non-business lines
Starbucks / Uber / McDonald's / FUN etc. are personal spend with no matching
expense, so they sit as "No suggestion yet" forever (clutters reconciliation).
Add a dismiss action: mark a bank line **Personal/Ignored** (MatchStatus already
supports "Dismissed"/"Removed") so it drops out of the unreconciled queue but is
still visible under a filter. Optional: remember the merchant so future lines from
the same merchant auto-ignore.

### 6. Accounting page redesign (tabs + visual hierarchy + color)
Problem: `/accounting` is one 1,100-line scroll — 4 KPI cards → Money Snapshot →
Reports (4 cards + ledger + P&L) → Transactions, ALL in the same muted gray
card-on-card treatment. Everything is the same color/weight so it reads like a
wall of form fields; no hierarchy; overwhelming on open.

Plan:
- **Tabs** to break the wall (best judgment on names):
  1. **Ledger** — the transactions list + recent ledger (the day-to-day view).
  2. **Reports** — net cash flow, property P&L, Schedule E, 1099 review, export
     buttons + links to Tax / Owner Reports / Banking.
  3. **Overview / Other** — Money Snapshot (plain-English) + the KPI summary
     cards; possibly the landing tab.
- **Visual hierarchy / liven up:**
  - Distinguish *display values* from *form inputs* — numbers/KPIs shouldn't look
    like editable fields. Bigger, confident figures; quieter labels.
  - Add **semantic color**: income/collected = green, overdue/negative = red/amber,
    neutral = muted. Use accent color (the blue already in the app) for headers /
    active tab / primary figures. Tint KPI cards subtly by meaning instead of all
    identical gray.
  - Tighten spacing & section rhythm; clear section headers; less card-in-card.
- Keep it consistent w/ EdiPlatform polish patterns (the design reference).
- Apply the same "display vs field" cleanup to the related money pages if quick.

### 7. Site-wide design makeover (professional polish, not a rebuild)
Goal: whole site feels a notch more professional. NOT big changes — Insights is
already "a little better" (has some color) → bring that energy everywhere.

Key insight: the theme tokens ALREADY exist and are just underused. `app.css`
defines `--success` (#22c55e green), `--warning` (#f59e0b amber),
`--destructive` (#ef4444 red), `--primary`/`--accent` blue, and chart-1..5.
Almost everything currently renders in `--muted` gray → flat & samey.

Pass (apply consistently across pages):
- **Use the existing semantic + chart colors** — status badges (Active/Overdue/
  Pending/Held), KPI figures, chart series, icons. Stop defaulting to gray.
- **Typographic hierarchy** — confident page titles, quieter labels, `tabular-nums`
  for money (already used some places — make it universal).
- **Cards/sections** — less card-in-card nesting; subtle borders + a touch of
  elevation/tint by meaning; consistent padding rhythm.
- **Accent usage** — primary blue for active nav, primary buttons, links, active
  tabs; don't let it disappear into gray.
- **Icons with subtle color** — lucide icons tinted to their section/meaning
  instead of all neutral.
- **Empty states** — friendlier (icon + one line + a CTA) instead of bare text.
- Reference: Insights page (good baseline) + EdiPlatform polish patterns.
- Treat as a sweep: hit the high-traffic pages first (Dashboard, Properties,
  Tenants, Leases, Accounting, Maintenance), then the rest.

### 8. Inspections can be "Completed" with zero checklist items
Investigation of `/maintenance/inspections/5`: it's a seeded MoveOut inspection
showing 0 PASS / 0 FAIL / 0 N/A / 0 TO DO, "Completed", "no template was selected".
Two causes:
- **Seeder gap (demo realism):** `DemoDataSeeder` creates Inspection rows directly
  with Status=Completed + outcome note but NEVER attaches a template or
  `InspectionItem`s → all seeded inspections are empty. Fix: seed each inspection
  with a template's checklist items + realistic pass/fail/N-A results so demo data
  looks real and the PASS/FAIL tiles populate.
- **Real logic gap:** `InspectionService.Schedule` treats `TemplateId` as optional
  → scheduling without a template creates an inspection with 0 items, which can
  then be marked **Completed** with nothing inspected (meaningless; bad for
  move-out disputes / owner reports). Fix: require a template at schedule time (or
  at least warn), and **block "Complete" when the inspection has 0 items** (or all
  Pending). A completed inspection should have actually inspected something.

### 9. Replace all native `<select>` with shadcn Select
Native OS dropdowns (e.g. the purple Draft/Approved/Dismissed/All on Notices)
break the dark theme + look unprofessional. shadcn `Select` is already in the repo
(`lib/components/ui/select`) → straight swap. Scope = 6 native `<select>` in 5 files:
- `lib/components/shared/InlineField.svelte`  ← shared inline-edit field; fixing
  this ripples to every detail-page inline edit. Do first.
- `routes/(protected)/applications/+page.svelte`
- `routes/(protected)/banking/+page.svelte`
- `routes/(protected)/notices/+page.svelte`
- `routes/apply/[token]/+page.svelte`  (×2) — public applicant form
Also: grep for any `<select` added later before calling it done; consider an ESLint
rule / convention note so new native selects don't creep back in.

### 10. Notices page typographic hierarchy (instance of #7)
Everything is the same font/size/weight — notice title, message subject, body,
trigger date all read flat; "nothing else to it." Give it hierarchy: stronger
notice title, distinct message-subject vs body, quieter meta (trigger date), a
real status pill (Draft/Approved/Dismissed in semantic color not gray), and group
the Portal/Email/SMS channel toggles visually. Fold into the #7 sweep but it's a
clear standalone example to fix.

### 11. Sandbox / trial mode (seeded demo data + promote-to-live)
Like Buildium's "Building your trial account" (seeds sample properties, residents,
bank accounts, even a leaky-faucet work order) and the user's EdiPlatform sandbox.
Goal: new users land in a **Sandbox** filled with demo data so they can learn the
system before entering real data; they can return to it, and **promote to live**
when ready.

**CLARIFIED MODEL (differs from EdiPlatform):** NOT granular like EdiPlatform
(where it's per-trading-partner / who's in practice mode). Here it's a **single
account-wide toggle** — the whole account is either in **Sandbox mode** or it's
**Live**. One switch flips the entire active dataset. No per-portfolio, no
per-record choosing.

Approach:
- **Account-level mode flag** (e.g. on the account/user or org), binary
  Sandbox ↔ Live. The toggle is global; the user never picks sandbox per entity.
- Sandbox and Live are **two separate datasets** that coexist so the user can flip
  back and forth ("go back to sandbox if they want"). The mode flag selects which
  dataset the whole app reads/writes. RC is already portfolio-scoped, so the
  natural separation is sandbox-portfolio(s) vs live-portfolio(s) selected by the
  mode flag (NOT by the normal portfolio switcher) — but the user only ever sees
  one switch: "Sandbox / Live."
- On signup → start in **Sandbox**, seeded via existing `DemoDataSeeder` (sample
  properties, tenants, leases, bank accts, a work order, etc.).
- **Promote to live** = graduate to the Live dataset (clean slate or import).
  Sandbox remains revisitable via the toggle.
- Persistent **"Sandbox" banner/badge** whenever in sandbox mode so it's never
  confused with real data.
- HARD RULE: in sandbox mode, **no real outbound** — emails / SMS / Stripe /
  e-sign must be no-op'd or clearly fake so demo play never contacts real tenants
  or moves real money. (Most externals are already gated; add a sandbox short-
  circuit too.)
- Onboarding "building your trial account" seeding animation is a nice touch,
  optional.

### 12. Detail / inline-edit pages: group fields into cards + a focal section
Inline forms are good (keep them), but detail pages (e.g. `/leases/19`) are a flat
3-col grid of ~13 label/value pairs — a wall of same-weight labels with no focal
point. Hard to land your eye anywhere. Reference site (img) isn't pretty but it
chunks data into sections so it's scannable.

Pattern to apply to ALL detail pages (and their inline-edit forms inherit it):
- **Hero / primary section** at top: the few things that matter most + a clear
  **call to action**. Bigger, confident figures; not the same weight as the rest.
- **Grouped cards** for the remaining fields, logically clustered (not one grid).
- Inline-edit keeps working — editing happens within each card's context.

Example for **Lease detail**:
- HERO: big Monthly Rent + Status pill + Tenant · Property/Unit, with the primary
  CTA by state (Active → Record payment; Expired → Renew / Set active).
- **Term** card: Start / End, Move-in / Move-out, Rent Due Day.
- **Financials** card: Monthly Rent, Security Deposit, Late Fee.
- **Parties** card: Property, Unit, Tenant (links).
- **Notes** card.
- **Lease Agreement** card (already separate — keep).

Do the same chunking for Tenant, Property, Unit, WorkOrder, Payment, Expense,
Vendor, etc. detail pages. Pairs with #7 (color/hierarchy) and the inline-edit
work — same components, just organized into cards with a focal CTA.

**Pages with many distinct sections → use TABS** (not just cards). The **Lease**
detail page is the prime example: it's a long scroll of separate concerns —
Lease Details, Lease Agreement + E-signature, Ask This Lease (Q&A), Account
History (ledger). Break into tabs, e.g.: **Overview** (hero + details), 
**Agreement & Signing**, **Ledger / Account History**, **Ask** (Q&A). Same
treatment for any other dense multi-section detail page (Property, Tenant).

### 13. Public-facing docs section (like EdiPlatform)
Always-available docs so users can look things up. Mirror EdiPlatform: markdown
files in `web/src/lib/docs`, rendered by a docs route at **`(public)/docs`** (no
auth, also good for SEO/marketing) AND an in-app **`(protected)/.../docs`** /
Help entry. Searchable, categorized (Getting Started, Properties, Leases, Money,
Scan/Intake, AI, etc.). Doubles as the source content for the chatbot KB (#14).
Content: write the how-to / feature docs as we go.

### 14. Knowledge base for the chat bot (product/how-to, not just data)
RC already HAS a chatbot: `PortfolioQaService` / `IPortfolioQaService` /
`AiController` / `QaDtos` (the blue bubble = Portfolio Q&A). Today it answers
questions about the user's **portfolio DATA** (the moat). Gap: it can't answer
**"how do I …"** product/how-to questions ("how do I record a payment?", "what's
a security deposit hold?").

Plan:
- Build a **knowledge base** the chatbot can retrieve from: the #13 docs +
  feature/how-to content + glossary + FAQ.
- Add retrieval (embeddings / vector search over the doc chunks, or simpler
  keyword+section retrieval to start) so answers can cite the right doc section.
- Route the chatbot: data questions → existing Portfolio Q&A path; product/how-to
  → KB retrieval. Possibly let it answer both and link to the relevant `/docs`
  page.
- Keep single source of truth: KB is generated FROM the #13 docs so they never
  drift. Build #13 first, then index it for #14.

### 15. Landing / marketing page (creative but minimal, animation-heavy)
Public marketing landing page (root `/` currently → login; landing should be the
unauthenticated home, with the public docs #13 as a sibling).
Vibe: **creative but minimal**, lots of tasteful animations, a strong hero.
- **Hero reference:** https://x.com/i/status/2062368755704033701 ("Google
  section" the user likes). REVIEW THE ACTUAL VIDEO WITH USER before building —
  may not be fetchable by me; treat as a design north-star to look at together.
- **Remotion videos:** user loves nicely-done creative Remotion videos. NOTE:
  Remotion is React + renders to MP4/WebM at build time (does NOT run in
  SvelteKit at runtime) → author hero/feature animations in a small Remotion
  project, **pre-render to video**, embed as `<video>` (poster + autoplay-muted-
  loop) on the Svelte landing page. Keep the Remotion project in-repo (e.g.
  `marketing/remotion/`) so videos can be re-rendered.
- Scroll-triggered reveal animations, subtle motion, the app's blue accent +
  semantic palette; performant (lazy-load video, prefers-reduced-motion respected).
- Sections: hero (the "computer does the typing for you" flagship), how-it-works
  (scan→draft→confirm), feature highlights, social proof/placeholder, CTA to
  sign up / try Sandbox (#11).
- Keep it minimal — whitespace, restraint; animation supports, doesn't clutter.

### 16. Scan: confirmed drafts route/label wrong (dead-end "Review")
Flow today: `/scan` list → both the **"Review"** button (line 155) AND row-click
(line 284) go to `/scan/{draftId}`. While Pending/Reviewing you edit fields +
Confirm (creates Payment/Expense/WorkOrder/Lease). Once **Confirmed** the page is
terminal (`isTerminal`): Confirm disabled, fields locked, and the ONLY way to the
real record is a tiny underlined link (lines 1076–1079) → roundabout.

Decision: **scan draft SHOULD stay immutable after confirm** — it's the capture;
once promoted, edits belong on the created record so the books stay the source of
truth. Don't make the draft editable. Fix the signposting instead:
- **Relabel the list action by status:** Confirmed → "View record" (or "Open
  Payment/Expense/Lease") linking **directly** to `linkedRecordHref`, NOT the
  read-only draft. Pending/Reviewing → keep "Review."
- **Row-click on a Confirmed draft** → go straight to the created record (or at
  least land on the draft with the CTA front-and-center).
- **Promote the tiny link** (1076–1079) to a prominent primary button matching the
  post-confirm success card's "View/Edit Record" (line 612). No misleading
  disabled Confirm button sitting there.
- Confirmed draft page = clean read-only summary ("Created this Payment on X →")
  + one obvious button to the editable record. Consider auto-redirect.

### 17. Real Reports hub (catalog + parameters + export) — replace single owner report
Today reporting is scattered & thin: Owner Statements at `/owners-report`, tax at
`/tax`, Schedule E CSV + P&L/ledger/1099 buried in `/accounting`. No unified hub,
no date-range params, no "pick → configure → generate → export" flow. Owner
reports = basically one report. Want a proper **Reports page** like Landlord Studio.

Engines that ALREADY exist (reuse, don't rebuild): `OwnerStatementService`,
`ScheduleEService`, `YearEndPacketPdfGenerator`, `AccountingService` (netCashFlow /
property P&L / ledger / 1099 review).

Build:
- **Reports catalog page** — grouped cards (icon + name + one-line description),
  by category, click to open a report. Categories/reports (proposed):
  - **Accounting:** Income/Expense Statement (P&L, monthly columns), P&L Summary
    (one line per property), Account Transactions / General Ledger (running
    balance), Cash Flow, Schedule E (exists), Year-End Packet (exists).
  - **Rent & Payments:** Rent Roll (current snapshot), Rent Ledger (due vs paid),
    Overdue / Delinquency aging, Rent Changes, Payment history.
  - **Owners:** Owner Statement (exists, date range, per owner), Owner
    Distributions.
  - **Operations:** Work Orders / Maintenance, Vendor 1099 & payments, Security
    Deposit register (held/deducted/returned), Lease Expirations / Renewals due,
    Occupancy / Vacancy.
- **Parameter bar** on each report: Properties (multi-select), **date range
  (from/to)** + presets (This year / Last year / QTD / MTD / custom), Categories,
  Filter. "Generate / Update" button.
- **Export / Actions:** PDF, CSV, Excel, Print, Email — reuse QuestPDF for PDFs.
- **Custom reports CTA:** make it KNOWN we do custom reports — a card/banner
  "Need a report you don't see? Tell us what you need and we'll build it" → a
  request form (captures the ask; we implement). 
- Pull `/tax`, `/owners-report`, and the accounting report bits UNDER this hub so
  reports live in one place (leave deep links working).
- Reference: Landlord Studio reports (good catalog/param pattern) + EdiPlatform
  report page (user says it also needs work — improve on it, don't copy 1:1).

### 18. Onboarding: skippable/auto-satisfied steps + gate to live mode + login choice
User likes the onboarding page. Refinements:
- **Smart skipping:** when a step is already satisfied, make it clearly skippable
  (or auto-mark complete). It already detects "You already have 3 owners on file"
  and shows "Skip this step" — extend that: if portfolio/owners/properties already
  exist, pre-complete those steps, let the user jump straight to what's missing.
  Don't force re-entering data they have.
- **Only onboard in LIVE mode:** don't run onboarding when in **Sandbox** (#11) —
  sandbox is already seeded with demo data to explore.
- **Login choice:** when they log in, give a choice — **"Explore in Sandbox"** vs
  **"Set up my real portfolio"** (→ onboarding). Ties directly to #11.

### 19. Consistent form inputs: State dropdown, Address autocomplete, Date pickers
Data we touch constantly deserves proper inputs (currently plain text fields):
- **US State → dropdown/combobox** everywhere (shadcn Select, ties to #9). State
  fields appear in ~10 files: properties (+[id]), owners, applications/[id],
  settings, onboarding, apply/[token], scan/[draftId], import, accounting.
- **Street address → autocomplete lookup** against an address API (type → suggest
  → autofill city/state/zip). COST: paid but ~$0 at this scale. RC has a Google
  Cloud project already (OAuth) but NO Maps/Places key → enable Places API + Maps
  key + billing to reuse it (~$2.83/1k sessions, monthly free credit covers a
  small landlord). Alternatives: USPS (free, weaker type-ahead), Mapbox/Smarty
  (free tiers). Recommend Google Places (reuse project) or Mapbox free tier.
  Gate it like other externals: no key → fall back to manual entry.
- **Dates → consistent date picker** component everywhere (we deal with dates a
  lot: lease start/end, move-in/out, due dates, payment/expense dates, report
  ranges). One shared date-picker + ensure UTC-Kind handling on the wire.

### 20. Tax page: "Paid" and "W-9" columns collide (no spacing)
`/tax` 1099 checklist table: columns ARE separate `<th>`/`<td>`
(`tax/+page.svelte` ~lines 197–225) but "Paid" is `text-right` butted directly
against the left-aligned "W-9" column → renders as `$965On file`, `$5,130Missing`
with no gap; header reads `PaidW-9`. Fix: add horizontal padding between columns
(e.g. `pr-6`/`px-4`), and make the W-9 status a proper **badge/pill** (green
"On file" / amber "Missing") instead of bare adjacent colored text. Small, fast.

### 21. Generate a pack of sample scan documents (images + PDFs) to load in
No sample scan docs exist in the repo. Make a set of realistic test documents the
user can drag into Scan to see the scan→draft→confirm flow work. Cover the doc
types the extractor handles:
- **Lease agreement** (PDF) → Lease draft
- **Rent receipt / payment confirmation** → Payment draft
- **Vendor invoice** (e.g. ComfortZone HVAC repair) → Expense draft
- **Utility bill / repair receipt** → Expense draft
- **W-9** → vendor tax ID
- **Rental application** → Application draft
- **Work order / maintenance request** → WorkOrder draft
Provide BOTH formats (PNG/JPG photos-of-docs AND clean PDFs) since the user wants
to test images and PDFs. Vary realism (some clean, some skewed/photographed) to
exercise the confidence/low-confidence UI. Put them in a `samples/scans/` folder.
Also seed a couple as a "Try a sample" button on the Scan page.

### 22. Inline feature explainers / "what's this?" help (docs gap is real)
User repeatedly can't tell what features do (Record audio note, Check for
fair-housing issues, etc.). Until #13/#14 land, add lightweight inline help:
short tooltips / "?" popovers on non-obvious feature buttons explaining what they
do + why. Cover first: Record audio note (voice intake), Check for fair-housing
issues, Generate drafts (Notices), Scan, Money Snapshot, the bank match badge.
Long-term these explainer blurbs become doc/KB content (#13/#14).
NOTE FOR DOCS: the two features above, explained —
- *Record audio note* = spoken scan: talk → Whisper transcribes → LLM extracts a
  draft (Expense/WorkOrder/etc.) you confirm. No form-filling.
- *Check for fair-housing issues* = LLM scans notice copy for Fair-Housing-risky
  language (protected-class / steering), returns flagged phrases + reason +
  compliant rewrite; fail-safe (no key/error → "unavailable", never false-clean).

### 23. E-signature: decide native (no 3rd party) vs Dropbox Sign
Today "Send for signature" is wired to **Dropbox Sign / HelloSign**
(`DropboxSignEsignProvider`, api.hellosign.com/v3), GATED — no key → no-op
("Not sent"). User wants to know if we can do legally-binding e-sign WITHOUT a 3rd
party.
Answer: **yes, legally** (federal ESIGN Act + state UETA) if a native flow
captures: signer **intent**, **consent** to sign electronically, **attribution**
(authenticated session + email/SMS OTP to prove who signed), a full **audit
trail** (IP, timestamps, what was shown, user agent), and a **tamper-evident**
locked final PDF (content hash + completion certificate page).
Trade-off vs Dropbox Sign: their court-tested certificate + identity verification
matter mainly if a signature is DISPUTED IN COURT; for a small landlord, native is
very doable and removes per-signature cost + 3rd-party dependency.
DECISION NEEDED (likely build native):
- Build a **native e-sign flow**: tokenized signer link → review PDF → typed/drawn
  signature + explicit consent checkbox → capture audit trail → lock PDF w/ hash +
  generate a "Certificate of Completion" page → store signed copy on the lease.
- Keep the Dropbox Sign provider behind the gate as an optional fallback.
- Add the ESIGN/UETA consent + disclosure language.

### 24. Lease agreement template: generic → state-specific + disclaimer
`LeaseAgreementPdfGenerator` produces a GENERIC standard residential lease from
the captured terms + 13 hard-coded boilerplate clauses; governing-law is a
placeholder ("the State of XX"). Code comment: "sensible defaults only, NOT legal
advice." Gaps for a real product:
- **State-specific templates / clauses** (deposit limits + return deadlines, entry
  notice, late-fee caps, mandatory disclosures like lead-paint/mold, etc. vary by
  state). Start with the user's operating state(s).
- **Required disclosures** (e.g. federal lead-paint for pre-1978).
- **Visible disclaimer** + ideally an attorney-reviewed base template.
- Let landlord upload / customize their own lease template (many have one).
- Pairs with #23 (the doc that gets signed).

### 25. "Ask This Lease" is on the wrong side → move to tenant portal
Confirmed an agent misplaced it: "Ask This Lease" (tenant-voiced prompts like
"Can I have a dog? When is rent due?") renders ONLY on the LANDLORD side
(`(protected)/leases/[id]`). The tenant **portal has a lease page**
(`(portal)/portal/lease/+page.svelte`) but **no Q&A**. The landlord doesn't need
to ask their own lease if they can have a cat.
- **Move** the lease Q&A to the portal lease page so TENANTS can self-serve
  ("can I have a pet?", "when's rent due?", "what's the late fee?") grounded in
  their lease. Reduces landlord questions.
- On the **landlord** side: either remove it, or reframe with landlord-appropriate
  prompts (e.g. "when does this lease expire?", "what's the deposit on file?").
- Reuse the existing grounded-Q&A service; just scope to the portal user's lease +
  guard access (tenant only sees their own lease).

### 26. (Vision / product) Legal e-sign + multi-party approval module — productize
User background: built ISO-standards software (WinForms) where every doc change
required sign-off from ALL managers, moving through multiple statuses, legally
binding. Wants to build that for the WEB as a **standalone, drop-in module/
service** — sellable to RC AND any other app that needs legal signatures /
approval workflows.
Scope of the module (superset of #23):
- **Multi-party signing & approval routing:** N required signers, ordered or
  parallel, with per-signer status (Pending / Viewed / Signed / Declined) and an
  overall document status workflow (Draft → Out for signature → Partially signed →
  Fully executed → Superseded).
- **Re-sign on change / versioning:** when a doc changes, invalidate prior
  signatures and re-route for sign-off (the ISO use case). Full version history.
- **Legal binding:** ESIGN/UETA compliant — intent, consent, attribution (auth +
  OTP), tamper-evident hash, immutable **audit trail**, Certificate of Completion.
- **Drop-in:** embeddable widget + REST API + webhooks; tenant-agnostic so it
  works for leases here and other domains (contracts, SOPs, change controls).
- Build #23 (native RC e-sign) FIRST as the v1 of this module, designed clean
  enough to extract into a standalone product later.
- NOTE: this is a business/strategic idea — flagged for later, not part of the
  current RC polish pass. Revisit as its own project.

### 27. Registration email: it DOES send (SendGrid 202) but deliverability unverified
Investigation: register → `OutboxAuthEmailSender` enqueues → Engine
`OutboxDispatchWorker` → SendGrid. Engine log: `[Email sent via SendGrid] ...
Status=202`. So the path WORKS; 202 = accepted, NOT delivered.
Likely why user "didn't get it":
- **SendGrid sender identity / domain authentication not verified** → 202 then
  suppressed / spam. ACTION: verify single-sender or (better) authenticate the
  sending domain (SPF/DKIM) in SendGrid; confirm `FromEmail` matches a verified
  sender.
- Possibly a typo'd recipient in the test (`ooble.jesse2@` vs `coble.jesse@`).
- Check spam folder.
Also harden UX: after register, show "check your email (and spam)"; add a
**resend confirmation** action; surface send failures (don't silently 202).
Separately noted: an SMS outbox msg permanently failed 5× in the same log —
check SignalWire/Twilio config (separate from email).

**ROOT CAUSE FOUND (email):** API key is VALID (starts `SG.`, 69 chars, send
returned 202 — a bad key = 401). The screenshot the user saw is the SendGrid API
Key **ID** (`WVY2…`), NOT the secret key (secret shown once at creation only).
Real issue = **domain auth mismatch**: From = `noreply@coblesolutions.com`, but
the user authenticated `coblesolutions.com` with **Zoho** (DKIM/SPF point to
Zoho), and the domain is **NOT authenticated in SendGrid**. So SendGrid-sent mail
fails SPF/DKIM alignment for the domain → with a strict DMARC policy Gmail
**silently drops it** (explains "not even in spam", despite 202).
FIX — pick one:
- **A) Authenticate domain in SendGrid** (Settings → Sender Authentication →
  Authenticate Your Domain): add ~3 CNAMEs to DNS + `include:sendgrid.net` to SPF.
  Coexists with Zoho. Standard/scalable fix.
- **B) Send via Zoho SMTP** (`smtp.zoho.com`) — reuse the already-authenticated
  Zoho domain, zero new DNS. Lower send limits but fine at this scale.
  RECOMMENDED for now (user already did the Zoho DKIM work). Add a config-selected
  email transport (SendGrid vs SMTP/Zoho) so it's swappable.
  [Supersedes the earlier project memory note "email=SendGrid"; Zoho is now an
  option since the user's domain is authenticated there.]

### 28. Google auth (OAuth sign-in) — wire up credentials, it's built
Code is ALREADY built end-to-end: backend `AuthController POST /auth/google`
(exchanges code, finds/creates user, issues our JWT; returns **501 when Google
creds not configured**), frontend routes `auth/google/+server.ts` +
`auth/google/callback/+server.ts`, `GoogleAuthService` + `GoogleAuthOptions`. 
Gap: **no Google client id/secret configured** (user-secrets only has SendGrid) →
501. ACTION:
- Add `Authentication:Google` ClientId/ClientSecret to user-secrets (Google
  creds reusable from EdiPlatform per project memory — add RC's redirect URIs to
  that OAuth client, or make a new one).
- Add RC redirect URI(s) (https://localhost:5667/auth/google/callback + prod).
- Add a "Sign in with Google" button on login/register (#29) + verify the round
  trip creates a user and logs in.

### 29. Login + Register cards need a facelift
Current login/register cards look plain. Give them a polished, professional
treatment (ties to #7 site makeover, EdiPlatform auth pages are the reference):
- Branded layout (logo, the blue accent, maybe a split hero / product imagery or a
  subtle gradient), confident typography, proper spacing.
- "Sign in with Google" button (#28) styled + a divider ("or").
- Friendly validation states, loading states, "check your email (+ spam)" post-
  register, resend link (#27).
- Consistent with the landing page (#15) vibe. Keep the DEV "Fill dev login"
  button in dev builds.

_(moved here as completed)_
