# Rental Command — Master Vision & Phased Spec

_Date: 2026-05-30 · Status: Direction-setting draft for review_

> Companion docs: competitive research in [`Docs/Research/2026-05-30-competitive-gap-analysis.md`](../../Research/2026-05-30-competitive-gap-analysis.md). This spec is the canonical **direction**; per-phase implementation plans are written separately (see §13).

---

## 1. North Star

**Rental Command is the rental-management app where the computer does the typing for you.**

You photograph or speak; the system reads it, fills in the right record, and you glance and confirm. You don't hunt through forms, you don't learn accounting, and you don't remember chores — the app remembers them and reaches out to you. It is built first for a real, non-computer-literate owner (the primary user's father, ~15–40 residential units, runs the business from his phone and a pile of paper), proven in his daily use, and then sellable to the millions of small landlords who feel exactly the same way about software.

The single sentence that has to be true for this product to win:

> _"A 65-year-old who hates computers can run his rental business from his phone, mostly by taking pictures and replying to texts."_

Everything in this spec serves that sentence — but it is a **layer, not a ceiling**. The product is **simple on the surface, a full management system underneath**: an easy proactive layer (mobile app, daily briefing, plain-English Q&A, scan-and-confirm, reply-to-confirm) sitting on top of a complete web-based management system. The owner uses the easy 20% from his phone; his co-owner spouse and employees use more; future SaaS customers get the whole thing. Channels: a **mobile app** for the family + employees (camera, push), the **web app** for the deeper features and the SaaS side, and **SMS** as the no-login channel for tenants and vendors who will never install anything.

---

## 2. Who it's for — the four value lenses

Every feature is judged against four questions. We label features by which lenses they serve.

1. **Runs the business better** — fewer dropped balls, faster rent, cleaner books, less risk.
2. **Makes the owner's (non-technical) life easier** — the #1 constraint. If it requires typing into a complex form, remembering a procedure, or understanding jargon, it has failed this lens.
3. **Makes tenants' lives better** — easy to pay, easy to report problems, transparent about money — which structurally removes the owner's phone-tag burden.
4. **Sellable beyond him** — would another small landlord switch from Buildium/TurboTenant for this? Is there a wedge and a moat?

### Personas

- **Frank (the owner / "Dad").** 60s, owns ~15–40 units. Lives on his phone. Distrusts complicated software, won't fill out long forms, forgets recurring tasks, keeps receipts in a shoebox and rent records in his head. Will happily take a photo or reply "YES" to a text. **He is the design target. When in doubt, optimize for Frank.**
- **Maria (the employee / part-time PM + handyman).** Works from a truck and a phone. Needs a dead-simple "what are my jobs today" list, snap-and-go photos, and voice notes. Will not use enterprise dispatch software.
- **Susan (the co-owner spouse).** Owns the properties alongside Frank, will use the **mobile app** and the web app, and is a bit more comfortable with a screen than Frank. A real secondary user — the app isn't single-user.
- **Tom (the tenant).** Wants to pay rent without friction, report a leak without a phone call, and see what he owes and why. ~80% of tenants will never install an app or log into a portal — but they will reply to a text.
- **Dana (future SaaS customer).** Another small landlord/PM evaluating the product. Switches only for a visible "wow" (the camera demos) and stays for sticky data (her categorized books, her imported leases).

---

## 3. Design principles (the product's soul — non-negotiable)

These are derived directly from "build for Frank." They override feature preferences when they conflict.

1. **Camera/voice replaces typing — everywhere.** Every capture surface (receipts, checks, leases, IDs, inspections, maintenance, expenses) is a photo or a spoken sentence that becomes a *draft* record. Typing is the fallback, not the default. _This is the spine, not a feature._
2. **Confirm, don't commit.** AI proposes a filled-in record with **per-field confidence**; nothing is written until a human taps Confirm. This is simultaneously the trust mechanism for a distrustful owner and the legal-safety control for money, late fees, screening, and Fair Housing.
3. **Two brains, not twenty buttons.** One **outbound brain** (a proactive daily briefing that reaches Frank — he never has to open the app) and one **inbound brain** (ask anything in plain English — "who's late?"). Most features feed these two surfaces rather than adding their own screen.
4. **A mobile app for the family; SMS for everyone who won't install one.** Frank and Susan get a **mobile app** (camera capture, push notifications, the daily briefing, the Q&A box) plus the full web app; employees too. **Tenants and vendors who will never log in** still participate fully over **SMS** — text a photo to report a leak, reply "DONE" to close a job. The app is the engine; the mobile app is the family's cockpit; SMS is the front door for the rest of humanity.
5. **Plain language, no jargon.** Never "overdue receivables ledger" — say "2 late payments." Never show a P&L to someone who wants "money in, money out, what's left."
6. **Proactive, not reactive.** The app remembers the recurring chores Frank forgets and nags gently. The value is the app reaching out, not Frank remembering to check.
7. **Forgiving.** Everything is editable; every action is undoable; mistakes are cheap. (Today the UI is create-only — that alone disqualifies it for a non-technical daily user.)
8. **Compliance is a quiet safety net, not a product.** State-aware late-fee caps, audit trails, FCRA notices, and Fair-Housing checks are baked *into* the features Frank already uses — never a separate "compliance suite" that intimidates him.
9. **Capture from wherever the paper already is.** Frank lives on a phone *and* a desktop scanner/printer. Capture must accept a **phone photo, a desktop-scanned PDF/image (drag-drop), and an email-to-inbox address** (scan-to-email → draft). Meet the document where it already enters his world; don't force the phone.
10. **Trust is earned, especially with money.** Anything touching his bank or his money is **opt-in, read-only where possible, locked down, and never required** — the app is fully useful without ever connecting a bank account.

---

## 4. The spine: Capture → Draft → Confirm

The flagship, and the mechanic the whole product is organized around.

**Flow:** Frank (or Susan, or Maria, or a tenant) **captures** — snaps a phone photo, **drag-drops a desktop-scanned PDF/image, forwards a scan-to-email to a dedicated inbox address**, or speaks a sentence. The system **drafts** — Claude vision/LLM extracts structured fields with a calibrated 0–1 confidence per field and pre-fills the matching record, grounding against existing data (vendors, properties, tenants, units) so it can pre-select the right dropdowns. Frank **confirms** — the review screen shows the draft with low-confidence fields highlighted and focus-ordered first, the original scan beside it; he fixes anything wrong and taps Confirm, which creates the record via the normal endpoint and re-attaches the source document to it.

**Document types → records (each maps to an existing create-DTO):**

| Capture | Becomes | Notes |
|---|---|---|
| Receipt / invoice (photo/PDF) | **Expense** draft | First to ship — highest frequency, lowest risk. Later: dedupe against bank feed. |
| Check (photo) | **Payment** marked paid | Reuses existing `mark-paid` + `ExternalReference`. |
| Lease (PDF) | **Lease** draft (+ chained Tenant/Unit if missing) | The migration unlock — "import your PDF leases." |
| ID / pay stub (photo) | **Tenant** / application draft | Gated by screening compliance (Phase 6). |
| Spoken sentence ("water's coming through the ceiling in Unit 3") | **Work order** / Expense / Payment draft | Voice is greenfield — no competitor does operator voice-to-record. |

**Why it wins:** it removes the exact thing Frank refuses to do (typing into forms), it's the killer sales demo ("snap a receipt — it types itself"), and every serious competitor has shipped *parts* of it (AppFolio Smart Bill Entry with per-field confidence, Buildium AI Bill Scan, RentRedi AI lease-scan onboarding, Stessa/Baselane receipt capture) — so it's both differentiator and emerging table-stakes.

**Where it runs:** upload is synchronous (store file + create a `Pending` `ScanDraft`); the slow vision/LLM extraction runs in the **Engine** worker off the request path and notifies the review UI via SignalR when ready. Provenance (prompt, response, model, token usage) is stored on the draft for audit and cost tracking.

**Hard requirements (from adversarial review):** per-field calibrated confidence; a mandatory human-confirm gate; receipt↔bank-transaction dedupe once the bank feed exists (the #1 failure mode is double-counting); a documented data-handling posture for sending images/PII to the LLM (consent, retention, "not used for training"); a deterministic no-op fallback when no API key is configured; and the model id read from config, never hardcoded.

**Token & cost strategy.** PDFs/photos cost more tokens than text (each page ≈ image ~1,500–1,600 tokens + extracted text), but the absolute cost is small: ~$0.01–0.02 per receipt and ~$0.07–0.10 per multi-page lease on a mid-tier model — a few dollars/month at Frank's volume, ~$10–20/month for a heavy SaaS user. Keep it cheap by: (1) **OCR/text-first routing** — for born-digital PDFs (emailed invoices, digital leases) extract the text layer and send *text only, no image*; reserve vision for actual photos / poor scans / handwriting (the biggest lever); (2) **first-page / page-limited** extraction where the data lives up front; (3) **prompt-cache** the system prompt + tool schema + grounding context (vendor/property lists repeat every scan → ~90% off cached input); (4) **Batch API (50% off)** for non-urgent bulk jobs like onboarding-importing old leases; (5) a **cheap vision model tier** (Haiku/Sonnet, not Opus — model is config-driven), small structured JSON output, and downscaling photos to ~1,500px. Token usage is recorded per `ScanDraft` for per-account budgets/alerts. The real constraint is **latency**, not cost — hence async extraction in the Engine with a SignalR "ready" ping.

---

## 5. The two AI brains

**Outbound — the Daily Briefing.** Each morning Frank gets ONE message (SMS first, email/push optional): plain-English, priority-sorted bullets of what needs him today — rent due/late, new maintenance, today's appointments, auto-posted late fees to be aware of, recurring chores due. Each line is one tap to act. He never has to open the app; the briefing *is* the app for him most days. Builds on the existing `portfolio-summary` stub (ships rules-first, gets smarter once Real AI lands). **Biggest single retention lever.**

**Inbound — Portfolio Q&A.** Ask in plain English — "who's late?", "what did I spend on Oak St this year?", "is Unit 3's lease up soon?" — answered from the live database **through the already-built MCP server**. This is unusually cheap to build (the MCP tools exist and are unused) and is a genuine **moat**: competitors' systems aren't API/agent-native, so they can't easily copy a grounded portfolio agent. It also erases the UI learning curve for a non-technical owner — the interface becomes a question box.

Both brains are powered by the same real LLM core (Phase 3) and grounded by the same MCP surface. Build the core once; both brains and every other AI feature inherit it.

---

## 6. Target architecture (mirror EdiPlatform — kept brief)

You build these for a living, so this is the shape, not a tutorial. Full layer-by-layer current→target migration lives in the architecture-mapping workflow output; the essentials:

**Stack:** .NET 10 multi-project + SvelteKit (Svelte 5 runes) + **PostgreSQL** + EF Core + a separate background **Engine** process + **SignalR** + the existing TypeScript **MCP** server. External services: Claude/Anthropic (vision + chat), Stripe + Plaid, TransUnion, SendGrid + Twilio. **No message broker** — a DB-backed job/**outbox** queue (reliable SMS/email send with retry, even across restarts) polled by the single-instance Engine + SignalR for realtime covers our needs. (Texts go via the Twilio API and in-app messages via SignalR — neither needs a broker. An `IMessagePublisher` interface lets us swap in RabbitMQ later if volume ever demands; EdiPlatform uses RabbitMQ because it's a high-volume EDI pipeline — a different problem.)

**Mobile delivery (Flutter — confirmed):** the mobile app is a native-compiled **Flutter (Dart)** app — one codebase for iOS + Android (+ desktop/web if ever wanted), genuinely native feel/performance, strong camera/push/secure-storage plugins. It talks to the **same .NET API** as the SvelteKit web app; the web app remains the full SaaS / desktop-web surface. Deliberately two frontends over one clean API boundary (native-feeling mobile for Frank/Susan/field staff; rich web for deep management + SaaS admin). The Flutter app uses the same JWT + rotated-refresh auth, storing tokens in `flutter_secure_storage`. Scaffolded around Phase 1/2 when camera capture lands. Tradeoff accepted: a separate Dart codebase (no shared UI with web) in exchange for native quality and cross-device reach — chosen over a Capacitor WebView wrapper.

**Projects:**

| Project | Responsibility |
|---|---|
| `RentalCommand.Core` | Entities, enums, **all service interfaces** (`ILlmProvider`, `IScanService`, `IPaymentProvider`, `IScreeningProvider`, `IEsignProvider`, `INotificationChannel`…), config option classes. |
| `RentalCommand.Data` | `RentalCommandDbContext : IdentityDbContext`, Postgres EF Core migrations, soft-delete + enum `HasConversion` + JSON columns + central `PortfolioId` scoping, append-only `AuditTrailService`. |
| `RentalCommand.Api` | Thin controllers → Scoped feature services + DTOs; ASP.NET Identity + JWT access / rotated refresh (httpOnly cookies) + `ApiKeyAuthenticationHandler` (webhooks); SignalR hubs; real `AnthropicLlmProvider`. |
| `RentalCommand.Engine` | Background workers (rent posting, late fees, lease-expiry reminders, **scan/OCR extraction**, notification dispatch) + single-instance advisory lock so rent is never double-charged. |
| `RentalCommand.*.Tests` + `TestCommon` | xUnit + FluentAssertions + Moq, transaction-rollback DB fixtures, Playwright journeys; CI on GitHub Actions; multi-stage Dockerfiles; Traefik. |
| `web` | SvelteKit SSR auth (`hooks.server.ts`), `(protected)/(admin)/(portal)` route guards, fetch layer with single-flight token refresh, `data-testid` everywhere, SignalR→TanStack-Query invalidation bridge. The full SaaS / desktop-web surface. |
| `mobile` | **Flutter (Dart)** native iOS/Android app over the same .NET API — camera/scan capture, push, daily briefing, Q&A box, approvals, field/maintenance. JWT + refresh auth via `flutter_secure_storage`. Built fresh in Flutter (not shared with web); the family's + field staff's cockpit. |
| `mcp` | Kept and expanded; retargeted to the new versioned, authed REST contract; new `scan_document` + automation tools. |

**Highest-value reusable patterns from EdiPlatform:** the `ILlmProvider` abstraction (real `AnthropicLlmProvider`, model from config) directly powers scan/briefing/Q&A/notices; the file-handling stack (validator + Guid-prefixed filesystem storage + DB-commit-before-disk) is the storage half of the flagship; `EngineWorkerBase` + advisory lock + watchdog gives recurring rent and async scanning a safe home; JWT+rotated-refresh httpOnly-cookie auth replaces the XSS-exposed localStorage token; the per-feature module discipline (Interface in Core + Scoped Service + DTOs + thin Controller + Tier-tagged Tests) is how each feature below is structured.

**Concrete hooks already in the code** (so phases below are grounded): `Vendor` already has `Is1099Eligible` / `W9OnFile` / `TaxId`; `Payment` already has `ExternalReference` (the dedupe hook) and a `mark-paid` flow; `Lease` already has `RentDueDay` / `LateFeeAmount` / `SecurityDeposit`. **Blockers to clear first:** the `Attachment` entity is orphaned and tied to `TaskId` only (must be generalized to attach to Expense/Payment/Lease/Tenant/WorkOrder/Inspection/Vendor); ~9 endpoint files are misnamed (`LeaseEndpoints` lives in `TaskEndpoints.cs`); ~18 dead "Lifecycle" template entities and the `AiEndpoints` keyword stubs must go.

### 6.1 What gets captured, what gets saved, and how the books work

**Capture → record.** Every capture (phone photo, desktop-scanned PDF, email-to-inbox, or spoken sentence) lands as a `ScanDraft` — the raw file plus the LLM's extracted fields, each with a confidence score and provenance (model, prompt, token usage). On Confirm it becomes a real record and the source file stays attached:

| Document | Fields extracted | Record created (+ attachment) |
|---|---|---|
| Receipt / invoice | vendor, amount, date, category, line items, which property | **Expense** (+ receipt image) |
| Check | payer, amount, date, check #, memo | **Payment** marked paid (+ check image) |
| Bank deposit slip / statement (opt-in) | deposits, dates, amounts | matched to **Payments** (reconciliation) |
| Lease (PDF) | tenant(s), property, unit, term, rent, deposit, late fee, due day | **Lease** (+ chained Tenant/Unit; + lease PDF) |
| ID / pay stub / application | name, DOB, address, contact, income | **Tenant / Applicant** (+ doc) |
| Anything else | (summary only) | **Attachment** on any record |

**The data model (the spine of what's saved).**
- **Ownership:** `OwnerEntity` (Person · LLC · **Trust** — name, tax id, contact) → owns → `Property` (address, type) → `Unit` (number, beds/baths, market rent) → `Lease` (term, rent, deposit, due day, late-fee, status) ↔ `Tenant`(s).
- **Money in:** `Payment` (type Rent · Deposit · LateFee · Other; amount, due/paid dates, method, status, `ExternalReference`; linked to Lease/Tenant/Unit).
- **Money out:** `Expense` (amount, date, **Schedule-E category**, vendor, property/unit, description, billable-to-owner flag, attachment).
- **People/orgs:** `Vendor` (service type, 1099-eligible, W-9 on file, tax id), `Tenant`, `OwnerEntity`.
- **Operations:** `WorkOrder`, `Appointment`, `Inspection` (+ checklist items + photos).
- **Documents & safety:** `StoredFile` (polymorphic attachment), `ScanDraft`, `SecurityDepositHolding` (amount held per lease, status, itemized deductions), `AuditLog` (append-only), `ActivityLog` (lightweight UI feed).

**The accounting system — deliberately simple.**
- **Cash-basis, single-entry** (Stessa-style — no double-entry GL, no balance sheet, no journal entries). Two ledgers: **money in** (Payments) and **money out** (Expenses). What a non-accountant can actually understand, and enough for rental income/expense tracking.
- **Categories = IRS Schedule E lines** (Advertising, Auto/Travel, Cleaning & Maintenance, Commissions, Insurance, Legal/Professional, Management fees, Mortgage interest, Repairs, Supplies, Taxes, Utilities, Depreciation, Other) — replacing today's free-text category, so the year-end export maps straight to the form his accountant files.
- **Scoped by Property *and* OwnerEntity** — produces per-property income statements *and* per-entity (e.g., the **trust**) summaries.
- **Security deposits are a liability, never income** — tracked in `SecurityDepositHolding`, returned (minus photo-itemized deductions) at move-out.
- **Optional, opt-in, read-only bank reconciliation** (Plaid) flags entries that don't match a real deposit/charge — but the books are complete from scans + manual entry **without ever connecting a bank**.
- **Outputs:** per-property & portfolio income statement, **rent roll** (who owes what, who's behind), cash-flow, Frank's plain-English weekly snapshot, and the **year-end packet** (Schedule-E summary + 1099/W-9 checklist) as CSV/PDF for the accountant.
- **Explicitly NOT:** a double-entry general ledger, a tax-filing engine, or anything that *requires* a bank connection.

---

## 7. The phased plan

Ten phases. Each lists its goal, the features (with lenses, dad-now vs SaaS-later value, and competitor context), and what's explicitly **out of scope** for that phase. This is the direction; sequence can flex but the dependencies are real (foundation → spine → intelligence → automation → money → leasing → books → field → growth).

Lens key: 🧑‍🦳 owner · 🧰 employee · 🏠 tenant · 💼 business · 🏷️ sellable.

### Phase 0 — Re-platform foundation
**Goal:** stand on the same foundation as EdiPlatform so everything after is clean and the auth/conventions match your other project.
- 4-project split; `Lifecycle`→`RentalCommand` namespace; delete the ~18 dead template entities + `LegacyMigrations`; rename the misnamed endpoint files; delete the leftover `legacy-*` web dirs.
- SQLite → **PostgreSQL**; soft-delete, enum `HasConversion`, JSON columns, central `PortfolioId` scoping from the JWT claim (never a client query param), append-only audit trail.
- **Ownership/entity modeling baked in now (confirmed 2026-05-30):** introduce `OwnerEntity` (Person/LLC/**Trust**) so every property attaches to a legal owner from day one. UI lands in Phase 7, but the data shape is foundational and painful to retrofit.
- **Auth:** custom 14-day token → ASP.NET Identity + JWT access / rotated refresh in httpOnly cookies; email verification, password reset, lockout; `ApiKeyAuthenticationHandler` for webhooks; migrate existing users/roles (rehash-on-first-login). Portfolio comes from the user's claim, not the login body.
- **Frontend:** SSR auth + route guards, fetch layer with single-flight refresh, Zod form validators, toasts, `data-testid`; **edit/search/filter/pagination on every list** (the API already supports PATCH — this is the create-only fix and a Frank-critical usability gate, pulled forward into the foundation).
- **Realtime:** SSE → SignalR + TanStack invalidation bridge.
- Test harness (TestCommon + Tier-2 + Playwright) + GitHub Actions CI + multi-stage Dockerfiles + Traefik + Postgres compose. DB constraints (`StartDate<EndDate`, unique `(PropertyId,UnitNumber)`, `RentDueDay 1–31`), missing enum states, `Portfolio.Currency`.
- **Out:** any new user-facing feature. This phase changes structure, not surface (except edit/search, which is a usability prerequisite).

### Phase 1 — Upload pipeline + attachments
**Goal:** the plumbing the spine needs. 🧑‍🦳💼🏷️
- Generalize `Attachment` (off `TaskId`) into a polymorphic `StoredFile` that attaches to Expense/Payment/Lease/Tenant/WorkOrder/Inspection/Vendor; `FileUploadValidator` (whitelist, size, sanitize); `UploadSettings`; Guid-prefixed filesystem storage with DB-commit-before-disk; download with orphan logging.
- Reusable `<FileDrop>`/camera-input component (the capture surface reused everywhere after).
- **Out:** extraction (that's Phase 2). This phase just makes files first-class — finally letting Frank attach the lease to the lease and the receipt to the expense.

### Phase 2 — 🚩 Flagship: Scan-to-Record
**Goal:** the killer demo and Frank's favorite. 🧑‍🦳🧰💼🏷️ · dad **high** / saas **high**
- `ILlmProvider` in Core + real `AnthropicLlmProvider` (vision `ExtractAsync`), model from `AssistantConfig`.
- `ScanDraft` entity (JSON `ExtractedFields` = value + confidence + source-box per field; `Status` Pending→Reviewing→Confirmed/Rejected; `TargetEntityType`; `FilePath`; `PortfolioId`; LLM provenance + token usage).
- `ScanController` `POST /scan` (multipart) → store + Pending draft; Engine `ScanProcessingWorker` extracts → Reviewing → SignalR notify; review UI (per-field confidence, edit, Confirm) → existing create endpoint → re-attach source + audit.
- Ship order: **Receipt/Invoice→Expense first**, then Check→Payment, then Lease→Lease, then ID→Tenant (gated by Phase 6 compliance).
- Grounding against existing vendors/properties/tenants/units; MCP `scan_document` tool for parity.
- **Out:** bank-feed dedupe (Phase 7), screening on IDs (Phase 6).

### Phase 3 — Real AI core + the two brains
**Goal:** replace the keyword stubs once; everything inherits it. 🧑‍🦳💼🏷️ · dad **high** / saas **high**
- Real LLM-backed `/intake` (entity extraction → pre-filled records), notice generation (adaptive copy), and portfolio summary — same public contract, real brains.
- **Daily Briefing** (outbound brain) — rules-first, LLM-enhanced.
- **Portfolio Q&A** (inbound brain) — MCP-grounded, plain-English ask box. _Lowest-effort high-value AI win; the moat._
- **Voice capture → record** — Frank/Maria/tenant speaks; transcription + LLM → draft work order/expense/payment to confirm.
- **Tenant Lease & FAQ bot** — answers "can I have a dog?" from the actual lease; deflects ~40% of repetitive questions. (Reuses lease parsing from Phase 2.)
- AI **maintenance triage** — analyze a request photo to set category/priority and suggest a vendor.
- **Out:** anything requiring data we don't have yet (predictive maintenance — deferred).

### Phase 4 — Automation + notifications + lease lifecycle
**Goal:** the app does the recurring work and reaches out. 🧑‍🦳🏠💼🏷️ · dad **high**
- Engine workers: **recurring rent auto-posting** (idempotent per lease+period under the advisory lock — financial-correctness critical), **auto late fees** after grace (state-aware caps baked in), **lease-expiry/renewal reminders**.
- **SMS-first communication layer** (Twilio): notices/reminders go out as SMS with reply-back-to-thread; the connective tissue under briefing, rent confirmation, maintenance updates, vendor dispatch. Email via SendGrid.
- **Reply-YES rent confirmation** — Frank confirms cash/check rent by replying to a text; LLM interprets "yes/yep/ok".
- **Lease Lifecycle Autopilot** — AI watches dates, reads the lease, drafts renewal offers / escalating late notices / move-out reminders for one-tap approval ("notices as a service").
- **Out:** taking money online (Phase 5).

### Phase 5 — Money: match Frank's check reality, make money legible
**Goal:** capture rent the way it actually arrives (checks + direct deposits), reconcile it, and make money legible. 🧑‍🦳🏠💼🏷️ · dad **high** / saas **high**
> **Reframed for Frank's reality:** his tenants hand him checks or deposit straight into his account — so the core flow is **capture + reconcile**, not "tenants pay online."
- **Scan-the-check → Payment** (from the spine) and quick "mark paid" + **reply-YES-by-text** confirmation; rent recorded in seconds without typing.
- **Optional, opt-in, read-only bank connection (Plaid)** for **reconciliation only** — "your $1,200 deposit on the 3rd matches Unit 3's rent." Bank-grade locked down; **the app is fully functional without it** (Principle 10). This is the "some accounting hooked up to the account" Frank wants, minus the trust problem of write access.
- **Transparent tenant ledger** with plain-English "why" tooltips per charge (kills the #1 payment dispute).
- **Plain-English money snapshots** — weekly "collected X, spent Y, kept Z"; every KPI explained in one sentence. (Frank-delighter; low effort; feeds the briefing.)
- **Online card/ACH rent payment + autopay** (`IPaymentProvider`: Stripe + Plaid; PCI-safe hosted elements) — built but positioned as an **optional convenience for tech-savvy tenants and a SaaS table-stake**, not Frank's primary path.
- **Out:** full bookkeeping/Schedule E (Phase 7); credit-bureau rent reporting (deferred bolt-on — and distinct from screening; see §10). Property-management trust/escrow accounting gated on audience (§11).

### Phase 6 — Applications, screening & e-sign leases
**Goal:** digitize the screening + signing workflow Frank already does by hand. 🧑‍🦳💼🏷️ · dad **high** / saas **high**
> **Promoted for Frank:** he runs a *lot* of background/credit checks today — almost certainly on a separate screening site with paper applications. Digitizing this is a real daily win for him, not just a sell-to-others feature. Worth considering pulling the screening slice earlier than Phase 6 if it proves to be his second-favorite after scanning.
- **No-login online application** with **photo-ID autofill**; **TransUnion screening** (`IScreeningProvider`) returning credit/criminal/eviction with an **FCRA flow** — applicant consent, and an **auto-generated adverse-action notice + audit trail** on decline (compliance baked in). Stores the screening result on the applicant so his "lot of checks" become organized, searchable records instead of loose PDFs.
- **Lease scanner** (import existing PDF leases → structured records — the migration unlock) **+ 5-question lease generator** with **e-sign** (`IEsignProvider`) and state templates; `LeaseStatus.PendingSignature`; legally-stored signed docs.
- Fair-Housing-safe listing/notice copy.
- **Out:** listing syndication to Zillow/Apartments.com (deferred — integration-heavy, only matters at the rare vacancy; sequence after applications have somewhere to land).

### Phase 7 — Clean books for the tax guy + ownership/entity modeling
**Goal:** turn the shoebox into clean, exportable records his accountant can use. 🧑‍🦳💼🏷️ · dad **high** / saas **high**
> **Reframed:** Frank has a 40-year tax accountant and genuinely complex taxes — the app does **not** "do his taxes." It produces **clean, categorized, exportable records** his tax guy ingests (saving the accountant time and killing the shoebox). For SaaS customers *without* an accountant, the same export is closer to "taxes done."
- **Expense auto-categorization** to a Schedule-E-style taxonomy (replace free-text `Expense.Category`) + **receipt↔deposit dedupe/merge** against the opt-in bank reconciliation (closes the scan loop — uses `Payment.ExternalReference`).
- **Year-end export packet** — a Schedule-E-style summary + P&L / cash-flow / rent roll, **CSV + PDF**, ready to hand to the accountant — **plus a 1099/W-9 checklist** that flags vendors missing a W-9 and texts them a request (the `Vendor` `Is1099Eligible`/`W9OnFile`/`TaxId` fields already exist — unusually low-friction).
- **Ownership / entity modeling (first-class).** A property is *owned by* a person, an LLC, **or a trust**. This makes income, books, and the audit trail clean **per owning entity** — directly useful given Frank's estate-planning trust holds assets across the four kids (see §11). Note: this is **accurate ownership + clean per-entity records**, *not* estate/trust management (that stays with his attorney).
- **Security-deposit tracking** (held separately from operating cash; state-interest rules folded in) + **itemized, photo-backed move-out statements** (reuses inspection photos; deposit disputes are a top litigation source).
- **Out:** owner statements + ACH distributions to absentee owners (SaaS-later — targets fee-managers, not Frank, who owns his own units).

### Phase 8 — Inspections + maintenance depth (mobile field)
**Goal:** make field work fast and evidence-rich. 🧰🧑‍🦳🏠 · dad **high**
- **Smart inspection checklists** → photo/voice per item → **auto-PDF report** + auto-spawn work orders for flagged items (current Inspection is just date+note).
- **Photo/voice maintenance requests** + **live status stream** (Received→Assigned→Done) visible to tenant and owner.
- **Vendor SMS dispatch** (auto-text best-rated available vendor with photos; reply YES/DONE) + **performance scorecard** (ratings accrue automatically).
- **Mobile field work queue** for Maria — prioritized list, tap-to-open-map, status + before/after photos, simple status log.
- **Recurring maintenance tasks** (set "HVAC filter every 30 days" once; auto-created and surfaced in the briefing and the field queue).
- **Out:** GPS route optimization, offline-first sync, billable-hour timesheets (all cut — §10).

### Phase 9 — Onboarding & migration (the SaaS gate)
**Goal:** get a new landlord live in minutes — the #1 SaaS bounce point. 💼🏷️ · dad **medium (white-glove him)** / saas **high**
- **Guided 5-step setup wizard** (portfolio → owner → property+units → tenants → leases) + **CSV/bulk import** + **bulk lease-PDF scan** as the import path.
- Explicit migration scoping (note: like DoorLoop, transaction history needs separate handling; reconcile opening balances — don't silently drop financial history).
- **Out:** nothing major; this is the polish that makes it sellable.

---

## 8. Sequencing logic (why this order)

Foundation (0) must precede everything or we build the flagship on a foundation we're about to replace. The upload pipeline (1) is a hard prerequisite for scanning. The flagship (2) is the wedge and ships early for impact. The Real AI core (3) is built once so the briefing, Q&A, voice, FAQ bot, and triage all inherit it. Automation + SMS (4) makes the app proactive and is the connective tissue for money and maintenance. Money (5) is high-value but carries regulatory weight, so it follows the comms layer it depends on. Leasing/screening (6) and accounting (7) are the "sell to others" depth. Field/maintenance (8) and onboarding (9) round out daily ops and the SaaS on-ramp. **The three sales demos** — "snap a receipt, it types itself" (2), "ask it anything about your portfolio in plain English" (3), and "hand your accountant clean, categorized books at year-end" (7) — are deliberately spread so there's a wow in early, middle, and late phases.

---

## 9. Cross-cutting (woven through every phase, not a phase)

- **Confirm-don't-commit + human-in-the-loop** on every AI write path (the trust + legal-safety control).
- **Append-only audit trail** with payload hashing on all mutations (regulatory-grade, distinct from the lightweight activity feed).
- **Compliance baked into workflows**, never a separate suite: state-aware late-fee caps live in late-fee automation; FCRA adverse-action notices live in screening; Fair-Housing checks live in listing/notice generation; deposit-interest rules live in the escrow ledger.
- **LLM data-handling posture:** consent for sending images/PII, retention limits, "not used for training," provenance stored per draft, per-document cost/latency tracking, deterministic no-op fallback when no key is set.
- **Mobile-first + accessible:** big targets, plain language, high contrast — Frank and Tom are on phones.

---

## 10. Explicitly cut or deferred (scope discipline)

Recorded so we don't rebuild these without a reason. **Cut now:** tenant directory/neighbor social (no top-3 pain + privacy surface); offline-first mobile sync (engineering cost vs a part-time handyman rarely in dead zones); GPS route optimization (enterprise dispatch overkill — a tappable address is enough); billable-hour timesheets (payroll-adjacent, unused at this scale); standalone Fair-Housing/compliance dashboard, document auto-purge, receipt-forgery detector (enterprise theater that over-promises legal guarantees and intimidates a non-technical owner); standalone "lease↔ledger audit mode" and "maintenance cost forecasting" as screens (fold the useful signals into the ledger/tax review as inline flags). **Deferred (good, but later/bolt-on):** credit-bureau rent **reporting** (report a tenant's on-time payments *to* the bureaus so the tenant builds credit — a tenant perk; **distinct from applicant screening in Phase 6, which is a core Frank feature**), renters-insurance partner integration, listing syndication, owner statements + ACH distributions, predictive/seasonal maintenance analytics.

---

## 11. Open decisions

1. **Audience / compliance depth (currently "not sure yet").** If this stays Frank-and-Susan-only (self-managed), we keep deposit accounting lightweight and skip property-management trust/escrow. If it's ever sold to **fee managers** (who hold *other* owners' money), state **trust/escrow accounting** with anti-commingling + owner distributions become must-haves — a meaningfully larger Phase 5/7. **Recommendation:** design the deposit/escrow ledger cleanly from the start (cheap insurance), defer full trust-accounting + owner-distribution automation until a fee-manager customer is real. Revisit before Phase 5.
2. **Estate-trust tie-in — CONFIRMED (2026-05-30).** Frank's properties may legally be owned by his **estate-planning trust** (assets across the four kids, Medicaid-protection). Supported via **ownership/entity modeling**: a property is owned by a person, LLC, or trust, giving clean per-entity books and audit trail. Modeled in **Phase 0's data layer**; UI in Phase 7. Explicitly **not** an estate/trust-management or legal-advice tool.
3. **Mobile-app technology — CONFIRMED (2026-05-30): Flutter.** The mobile app is a native-compiled **Flutter (Dart)** app over the shared .NET API; the **SvelteKit web app** stays the full SaaS/desktop surface. Two frontends, one API boundary — chosen for native feel, performance, and cross-device reach over a Capacitor WebView wrapper. Same JWT/refresh auth via `flutter_secure_storage`; scaffolded around Phase 1/2 when camera capture lands.
4. **Screening sequencing.** Frank does *a lot* of background/credit checks today. Screening is in Phase 6, but consider pulling the **application + TransUnion screening** slice earlier (it may be his second-favorite feature after scanning). Decide after he trials the flagship.
5. **Integration order within "all services approved":** Claude (Phase 2/3) → Twilio/SendGrid (Phase 4) → check-scan + opt-in read-only Plaid reconciliation (Phase 5) → TransUnion (Phase 6) → optional online payments + bank-feed depth (Phase 5/7). Matches the phase plan.
6. **What to validate during Frank's trial** (before investing in the SaaS-only phases): does he actually use scan + voice daily? Does the mobile app + daily briefing become his habit? Does the reply-YES loop work? Do tenants adopt SMS maintenance? These answers gate how hard we push Phases 6/9 and the SaaS build-out.

---

## 12. How we'll know it's working

- **Frank metric:** % of records created by capture (photo/voice) vs typing — target the majority. Daily-briefing open/act rate.
- **Tenant metric:** % of maintenance requests via SMS/photo with no phone call; on-time payment rate after autopay.
- **Business metric:** time from "rent due" to "rent recorded"; late fees correctly applied; books tax-ready at year end with zero manual re-keying.
- **SaaS metric:** time-to-live for a new landlord (wizard + import); the three demos landing in a sales conversation.

---

## 13. Next steps

This spec is the **direction**. Implementation plans are written **per phase** (the program is too large for one plan), each via the writing-plans process and tracked like EdiPlatform's master plan. Proposed immediate order: write the **Phase 0 (re-platform)** plan first, then **Phase 1 + 2 (upload + flagship scan)** together since they're tightly coupled and constitute the first demoable slice. Everything after sequences per §7–8.

---

## 14. Provenance

Built from three multi-agent research passes (2026-05-30): a competitive audit + gap analysis of 9 competitor systems with adversarial verification; an architecture mapping of EdiPlatform with a layer-by-layer current→target migration; and a six-lens idea-generation pass (non-technical owner, tenant, employee, SaaS strategist, AI-possibility, accountant/compliance) filtered for fit to a small non-technical operator. Competitor and feature claims were verification-passed; marketing-stat claims were down-weighted. Detail in [`Docs/Research/2026-05-30-competitive-gap-analysis.md`](../../Research/2026-05-30-competitive-gap-analysis.md).
