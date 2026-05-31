# Rental Command — Competitive Gap Analysis & Upgrade Roadmap

_Date: 2026-05-30_
_Method: multi-agent audit of this codebase + web research on competitor property-management systems, with an adversarial verification pass on competitor feature claims. Sources listed at the end._

---

## 1. Where Rental Command stands today

**What it genuinely is:** a residential property-management web app for small landlords. Stack: SvelteKit 5 + .NET 10 Minimal API + EF Core + SQLite, plus a (well-built) MCP server. Forked from a "Lifecycle" project-management template.

**What works (real strengths):**

- Solid core domain model — 13 rental entities: Portfolio → Property → Unit → Lease → Tenant, plus Payment, Expense, WorkOrder, Appointment, Inspection, Owner, Vendor, PortalMessage. Multi-tenant isolation via `PortfolioId`.
- Lease status state machine with Unit occupancy sync; Payment ledger with a 5-status model and a mark-paid workflow; WorkOrder priority + status machine with cost rollup into expenses.
- Role-based tenant/owner web portal (view lease, balance, recent payments, work orders, submit a maintenance request, message staff).
- Audit trail (13 auto-logged activity events) and real-time SSE invalidation.
- A clean, Zod-validated MCP server exposing rental operations to Claude agents — a genuine, under-leveraged differentiator.

**The honest problems:**

- **The "AI" is fake.** `api/Api/AiEndpoints.cs` is `string.Contains("leak")` keyword matching, a C# `switch` for notice templates, and deterministic LINQ for the "risk summary." There is **no LLM, no OCR, no document parsing** anywhere (the project has only EF Core + SQLite NuGet refs — zero AI deps).
- **No document/file pipeline at all.** The `Attachment` entity is orphaned (not in the DbContext, not in migrations), `AttachmentEndpoints.cs` actually contains `TenantEndpoints`, there are no upload endpoints, and the web client has an `upload()` helper that is never called. So today you can't even attach a receipt — let alone scan one.
- **Frontend is create-only.** No edit, no delete-confirm, no search, no filter, no pagination — even though `PATCH` endpoints already exist server-side. Onboarding 10 properties takes ~45–60 min of one-at-a-time typing (≈15–20 min in Buildium/QuickBooks).
- **Heavy legacy debt.** ~18 dead "Lifecycle" entities (Project/Milestone/Phase/Task/Test*/TeamMember…) pollute the schema, and rental endpoints are masqueraded under template filenames (`LeaseEndpoints` lives in `TaskEndpoints.cs`, `PaymentEndpoints` in `TestPlanEndpoints.cs`, etc.), which breaks IDE navigation.
- Missing operational logic: no recurring rent generation, no auto late fees, no lease renewals, no co-tenants, no real security-deposit/escrow accounting.

**Bottom line:** the data model is a decent skeleton, but the things that make a rental system *easy and valuable* — getting data in painlessly, collecting money, and real AI — are mostly missing or faked. The scan-it-in + LLM-auto-fill vision is essentially greenfield, which makes it the best differentiator to build.

---

## 2. Competitor landscape & the best ideas worth stealing

Surveyed: **Buildium, AppFolio, DoorLoop, Yardi (Breeze/Voyager), Rent Manager** (mid-market/enterprise) and **TenantCloud, Avail, Rentec Direct, Hemlane, RentRedi, TurboTenant, Baselane, Stessa, Azibo** (small-landlord/DIY). Verified, shipped features most relevant to your goals (ease-of-use, scanning, AI):

### Scanning / document AI (the core of your vision — everyone has shipped this)
- **AppFolio Smart Bill Entry / Document AI** — drag-drop or *email-to-inbox* a PDF invoice; ML extracts amount, vendor, property, invoice # with a **calibrated 0–1 confidence score per field**, so the UI only flags the fields it's unsure about. (PDF-only; multi-bill PDFs process first page only.)
- **Buildium AI Bill Scan** — upload/scan multiple invoices at once or email to a per-account address; AI extracts vendor/date/total/line-items into a **draft bill** for review.
- **RentRedi AI lease-scan onboarding** (Oct 2025) — upload an existing lease PDF and ML auto-populates properties, tenants, rent, and terms — "hours to minutes."
- **Stessa / QuickBooks / Dext** — mobile camera-first receipt capture; OCR → structured record. Critically: **receipt-to-bank-transaction dedupe** so a scanned receipt merges with the imported charge instead of double-counting (called the #1 failure mode).
- **DoorLoop AI Inspections** (computer vision) — guided multi-photo capture; AI organizes photos, matches notes, generates a PDF report, and spawns work orders from findings.
- **Baselane** — AI + rules tag each transaction to property + Schedule E tax category on arrival; auto-receipt matching; one-click tax packages.

### Real AI assistants / agents
- **AppFolio Realm-X "Performers"** — agentic AI: Leasing Performer engages prospects/books tours; **Maintenance Performer analyzes request photos**, troubleshoots, creates prioritized work orders, dispatches vendors; Resident Messenger handles renewals.
- **AppFolio "Lisa"** — 24/7 AI leasing assistant answering prospects and scheduling showings (addresses that ~40% of leads go unanswered).
- **DoorLoop / Buildium / Yardi assistants** — conversational copilots resolving a large share of tenant questions, summarizing long threads (Buildium cites 83% time reduction), generating listing copy and tasks.

### Low-friction data capture & ease-of-use for non-technical users
- **AI-generated, Fair-Housing-safe listing descriptions** from basic attributes in <5s (TurboTenant, AppFolio, Hemlane) — a *safe* first AI win (no money/decisions involved).
- **Draft-not-commit** everywhere: AI proposes, human taps Confirm before anything posts (Buildium, QuickBooks, AppFolio). This is both a UX pattern and a compliance control.
- **Bank-feed auto-import + Schedule E categorization** (Baselane, Stessa, Azibo, RentRedi/REI Hub) — connect a bank once; transactions import and self-categorize into tax-ready buckets. Stessa deliberately uses *single-entry* bookkeeping to avoid intimidating non-accountants.
- **Self-service portals as data capture** — applications, payments, maintenance-with-photos-and-voice-memos flow in at the source (CondoCafe even takes voice memos).
- **Text-first / no-login intake** — Entrata Maintenance AI lets residents *text* an issue with no app login. Lowest possible friction for non-tech-savvy users.
- **Guided onboarding + CSV import + done-for-you migration** (DoorLoop migration specialist, "go live in days"; Yardi "onboard owners in minutes").
- **Mobile-first apps** with camera as primary input (Stessa iOS 4.8★); autopay with skip/edit + email/SMS/push reminders.
- **State-specific lease templates with autofill + e-sign** (Buildium 50+ autofill fields; Avail/TurboTenant/Hemlane/TenantCloud).
- **Rent reporting to credit bureaus** as a near-universal, cheap tenant-retention perk (TenantCloud, Avail CreditBoost, RentRedi, TurboTenant, Entrata Homebody).

---

## 3. Gap matrix (capability → competitor norm → our status)

| Capability | Competitor norm | Rental Command |
|---|---|---|
| Online rent payments | Universal (ACH/card, autopay, auto late fees) | **MISSING** — Payment is a manual bookkeeping record; no gateway, no tenant checkout |
| Recurring rent / late-fee automation | Auto-post on schedule, auto late fees | **MISSING** — `RentDueDay`/`LateFeeAmount` exist but nothing consumes them |
| Bank-sync bookkeeping | Plaid feeds, auto-categorize, reconcile, Schedule E | **MISSING** — manual expense entry only, no bank link, no tax output |
| **Receipt/invoice OCR ("snap a photo")** | Flagship — OCR/ML → draft expense | **MISSING** — no upload pipeline, no OCR, no LLM |
| Tenant screening (credit/criminal/eviction) | Integrated, applicant-initiated, FCRA flow | **MISSING** — Tenant has zero screening fields, no application object |
| E-signature leases | Templates + autofill + e-sign + stored docs | **MISSING** — Lease is metadata only; no document, no signing |
| Listing syndication / leasing funnel | Post once → many ILS sites + online apps | **MISSING** — no listing, no application, no prospect CRM |
| Maintenance triage & work orders | Photos/video, dispatch, SLA, AI triage | **PARTIAL** — solid core + tenant submit; no photos, no SLA, no AI |
| Resident mobile app & self-service | Native apps: pay, autopay, photos, docs | **PARTIAL** — responsive web portal, but view-only on money |
| Owner statements & distributions | Auto statements, payouts, 1099 e-file | **PARTIAL** — read-only dashboard; no statements/payouts/1099 |
| AI assistant / agents | Genuine LLM copilots + agents | **PARTIAL (faked)** — keyword/template logic; MCP server is real though |
| Inspections with photos | Templates, per-item photos, PDF, AI | **PARTIAL** — type + one text field; no photos/checklist |
| Document/lease extraction | OCR/LLM abstracts lease terms | **MISSING** |
| Onboarding / bulk import | Wizards, CSV import, migration teams | **MISSING** — create-only, one at a time, no edit UI |
| Lease lifecycle (renewals/escalations/co-tenants/deposits) | Full | **MISSING** — leases just expire; deposit is one number |
| Notifications (email/SMS/push) | Multi-channel reminders + broadcasts | **PARTIAL** — in-app messages only; nothing outbound |
| Reporting / exports | P&L, rent roll, Schedule E, CSV/PDF | **PARTIAL** — on-screen dashboard only |
| Integrations / open API | Marketplaces, webhooks | **PARTIAL** — internal REST + a strong MCP server |
| Codebase/data-model integrity | Clean, aligned, constrained | **PARTIAL** — legacy debt, misnamed files, missing DB constraints |

---

## 4. The flagship: "snap a photo → LLM fills the form → you confirm"

**Guiding principle (how the leaders actually ship it): _draft, not commit_.** The LLM proposes a filled-in record with per-field confidence; the human taps Confirm; only then does it hit the database. This is simultaneously the UX and the compliance control.

**End-to-end flow tailored to this stack:**

1. **Capture (SvelteKit)** — a new `/scan` route with a big drop zone / camera input (`<input type=file accept="image/*,application/pdf" capture="environment">` opens the phone camera). User picks a doc type (Receipt/Invoice, Lease, ID) or "Auto-detect." Reuses the existing-but-unused `api.upload()` helper.
2. **Upload + extract (API)** — new `api/Api/ScanEndpoints.cs` (`POST /api/scan`, multipart). Saves the file via the new Attachment pipeline, then calls a new `IDocumentExtractionService` that sends the image/PDF to Claude's **vision** API with a strict per-doc-type JSON output schema. Returns `{ docType, perFieldConfidence, draft }` — **no domain record is saved yet.**
3. **Per-doc-type schemas map to existing create-DTOs:**
   - Receipt/Invoice → `CreateExpenseRequest` (vendor, amount, date, category, line items) — and pass the portfolio's existing Vendors/Properties so Claude can pre-select the right dropdowns.
   - Lease → `CreateLeaseRequest` (tenant, property, unit, dates, rent, deposit, late fee, due day); propose creating missing tenant/unit as chained drafts.
   - ID/Application → `CreateTenantRequest`.
   - Check → propose marking the matching Scheduled rent Payment as Paid via the existing `mark-paid` endpoint.
4. **Review + confirm (SvelteKit)** — render the normal create form pre-filled, with **low-confidence fields highlighted and focus-ordered first** (AppFolio-style), and a thumbnail of the scan beside it. User fixes anything wrong and taps Confirm.
5. **Record created (API)** — Confirm calls the *existing* create endpoint (`POST /api/expenses`, etc.). On success, re-key the Attachment from a `ScanDraft` to the real record so the source document stays attached. Activity logged with `Actor="ai-scan"`; SSE broadcast so the list updates live.
6. **Dedupe guard (with bank feed)** — before creating an expense from a receipt, match against imported bank transactions on vendor+date+amount; if found, attach the receipt to that transaction instead of duplicating.
7. **MCP parity** — add a `scan_document` MCP tool so any Claude session can scan → then call the existing `create_*` tools to confirm.

**Non-negotiable requirements (from the critic):** per-field calibrated confidence; receipt↔transaction dedupe; a documented data-handling posture for sending images/PII to a third-party LLM (consent, retention, "not used for training"); human-in-the-loop as a stated control; a deterministic no-op fallback when no API key is set; and an eye on per-document API cost/latency.

---

## 5. Prioritized roadmap

Categories: **quickWin / tableStakes / differentiator**. Effort/impact noted.

**Foundation (do first — unblocks everything):**
- **File-upload + Attachment pipeline** (tableStakes, high impact, med effort) — rehabilitate the orphaned `Attachment` entity, add `DbSet` + migration + a correctly-named endpoint; reusable `<FileDrop>`. Prerequisite for scanning, lease docs, work-order/inspection photos, W-9 storage.
- **Edit / search / filter on every list** (tableStakes, high impact, med effort) — pure frontend; `PATCH` endpoints already exist. The difference between a prototype and a daily-use tool.
- **Codebase cleanup + DB constraints** (quickWin, low effort) — rename misnamed files, drop legacy entities; add `StartDate<EndDate`, unique `(PropertyId,UnitNumber)`, `RentDueDay 1–31`, missing enum states, `Portfolio.Currency`.

**Flagship & AI:**
- **Scan-to-Record** (differentiator, high impact, high effort) — start with Receipt → Expense (lowest compliance risk, highest frequency), then leases/IDs.
- **Replace keyword AI with real Claude** (differentiator, high impact, med effort) — lead with the *safest* generative win: **AI listing descriptions**, then upgrade intake to real entity extraction and notices to adaptive drafting.

**Operational automation:**
- **Recurring rent + late-fee + renewal automation** (tableStakes, high impact, med effort) — a daily `IHostedService`; no external dependency.
- **Email/SMS notifications + reminders** (quickWin, low effort) — wire the notice generator into actual delivery (SendGrid/Postmark/Twilio).

**Money & leasing (higher effort / regulatory weight):**
- **Online rent collection (Stripe/Plaid) + tenant pay-in** (tableStakes, high impact, high effort) — gated behind a trust/escrow accounting decision + adding tenant auth to the portal payment path. Bundle opt-in **rent reporting to credit bureaus** once payments flow.
- **Photo-based inspections with checklists + work-order spawning** (differentiator, med) — built on the Attachment pipeline; AI damage-flagging reuses the scan vision service.
- **Tenant screening + applications** (tableStakes, high effort) — FCRA-regulated; provider integration (TransUnion SmartMove etc.).
- **Bank-feed import + Schedule E auto-categorization (Plaid)** (differentiator, high effort) — the other half of the data-capture story; pairs with scan dedupe.
- **Guided onboarding wizard + CSV import** (differentiator, med) — note: even DoorLoop's import doesn't cover transactions; scope what migrates.

---

## 6. Key risks (from the adversarial critic — don't skip these)

1. **Compliance is under-weighted.** Handling rent money → PCI + money-transmitter + (for fee managers) state **trust/escrow accounting** with anti-commingling. Screening → **FCRA** (consent, adverse-action notices) + **Fair Housing** disparate-impact exposure. Any LLM touching screening/pricing/adverse decisions needs a documented **human-in-the-loop + audit trail** (cf. the RealPage algorithmic-pricing DOJ case). The single biggest blind spot.
2. **AI accuracy / data privacy for the flagship.** Need per-field confidence, a correction/feedback loop, dedupe, and a clear posture on sending tenant PII/IDs/financial docs to a third-party LLM (retention, "do not train"). A lease or "Paid" mark auto-created from a misread doc is a real financial-integrity risk.
3. **Sequencing.** The flagship depends on the Attachment pipeline AND existing records to ground against AND an edit UI to fix mis-extractions — so **foundation must land first**, not alongside.
4. **Cost/latency of an LLM-dependent core** — per-scan API cost, rate limits, latency; competitors bundle this into flat subscriptions.
5. **MCP is an under-leveraged strategic differentiator** — a Claude session that can ingest documents and operate the whole portfolio end-to-end is something competitors' closed assistants can't match.

---

## 7. Recommended sequence (the critic's synthesis)

1. **Foundation first** — Attachment/upload pipeline + edit/search/filter + cleanup/constraints.
2. **Flagship Scan-to-Record** — receipts/invoices first, draft-not-commit, per-field confidence, dedupe, documented data-handling.
3. **Real Claude AI** — lead with safe AI listing descriptions, then real intake/notice drafting.
4. **Recurring rent/late-fee/renewal automation + email/SMS** (paired) — hands-off the most repetitive chore.
5. **Online rent collection (Stripe/Plaid)** — gated behind a trust-accounting decision; bundle credit-bureau rent reporting.

---

## 8. Sources

Buildium (features, AI Bill Scan, Resident Center) · AppFolio (Property Manager, Smart Bill Entry / Document AI engineering blog, Realm-X / Lisa, Leasing Signals) · DoorLoop (features, AI Assistant, AI Inspections) · Yardi Breeze/Voyager (residential features, PayScan/Smart AP) · Rent Manager (Open Access/API) · TenantCloud, Avail, Rentec Direct, Hemlane, RentRedi (AI lease-scan onboarding, GlobeNewswire Oct 2025), TurboTenant, Baselane (landlord accounting), Stessa, Azibo · Snappt (document-fraud) · TransUnion SmartMove · Entrata (Maintenance AI, Homebody) · IRS Form 1099-NEC / Schedule E guidance · G2 / Capterra / Software Advice review aggregates. (Several marketing-stat claims were down-weighted or flagged during verification; see the workflow record.)
