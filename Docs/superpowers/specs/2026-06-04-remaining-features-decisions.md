# Remaining Big Features — Decisions & Implementation Spec (2026-06-04)

Locked decisions for the remaining big TODO items so they can be implemented
autonomously. Source: TODO.md at repo root. Smaller no-decision items (Google auth
wiring #28, SMTP email #27, address-API key, inline explainers #22) are handled
separately and gated on keys the user provides.

Wave discipline (per project): branch off fresh `origin/main` per wave; ONE
migration-adding change per wave; parallel agents on disjoint files; orchestrator
runs the authoritative build/test/e2e + Playwright sweep; PR + merge when green;
NO Co-Authored-By trailer.

---

## 1. Reports Hub (#17) — NO migration
Build a real reports section. New `/reports` catalog (grouped cards) → pick a report
→ parameter bar → generate → export. Reuse existing engines (OwnerStatementService,
ScheduleEService, YearEndPacketPdfGenerator, AccountingService) and add new read-only
report queries (no schema changes).
- **Param bar:** Properties (multi-select), **date range via the new RangeDatePicker**,
  category/filter, Generate. Presets (This year / Last year / QTD / MTD).
- **Reports (v1):**
  - Accounting: Income/Expense Statement (P&L, monthly columns), P&L Summary, Account
    Transactions / General Ledger (running balance), Cash Flow, Schedule E (exists),
    Year-End Packet (exists).
  - Rent & Payments: Rent Roll (snapshot), Rent Ledger (due vs paid), Overdue /
    Delinquency aging, Rent Changes, Payment history.
  - Owners: Owner Statement (exists), Owner Distributions.
  - Operations: Work Orders / Maintenance, Vendor 1099 & payments, Security Deposit
    register, Lease Expirations / Renewals due, Occupancy / Vacancy.
- **Export/Actions:** PDF (QuestPDF), CSV, Print, Email.
- **Absorb** `/tax` + `/owners-report` + the accounting report bits under the hub;
  keep deep links working.
- **Custom reports CTA:** a card/banner "Need a report you don't see? Tell us" → a
  request form that records the ask (we implement later).
- Plan: round 1 = backend ReportsController + report query services + DTOs; round 2 =
  web `/reports` catalog + viewer + param bar + export UI (consumes the endpoints).

## 2. Bank Reconciliation (#4/#5) — DECISION: ALWAYS ONE-TAP CONFIRM — no migration
NEVER auto-match. Always surface a suggested match the user taps to confirm.
- Improve the match engine: currently scores amount (±$0.01) + date ≤7d only
  (`BankingService.ScorePayment/ScoreExpense/SuggestMatch`). Add **merchant ↔
  vendor/tenant name** to the score to make suggestions sharper.
- **Inline reconciliation badge on Accounting:** each Payment/Expense row shows green
  "✓ Cleared · {bank} · {date}" when matched, or a "Match?" chip with the suggested
  bank line (name/amount/date) → click to confirm (one tap). No silent auto-match.
- **Personal / Ignore action** (#5) on bank lines (Starbucks/Uber etc.): mark a line
  Ignored (MatchStatus already supports Dismissed/Removed) so it drops out of the
  unreconciled queue but stays visible under a filter. Remember the merchant so future
  lines from it suggest "ignore".
- **Clarify Deposits page** (#4): relabel Security Deposits ("money held in trust per
  lease, separate from rental income") — it stays its own page. Banking keeps the raw
  feed + connections + import; it is NOT a second ledger.

## 3. Docs + Chatbot KB (#13/#14) — DECISION: SIMPLE RETRIEVAL NOW — no migration
- **Public docs site (#13):** markdown in `web/src/lib/docs`, rendered at a public
  `(public)/docs` route AND an in-app Help entry. Searchable, categorized (Getting
  Started, Properties, Leases, Money, Scan/Intake, AI, etc.). I DRAFT the initial doc
  content from the codebase/features for the user to review.
- **Chatbot KB (#14):** extend the existing Portfolio Q&A (`PortfolioQaService` /
  `AiController`) so product/how-to questions ("how do I record a payment?") are
  answered. **Retrieval = simple keyword/section match over the markdown docs** (no
  embeddings, no pgvector, no new infra). Route: data questions → existing Q&A;
  how-to → KB retrieval over docs; answers can cite/link the relevant `/docs` page.
  KB is generated FROM the #13 docs (single source of truth). Upgrade to embeddings
  later only if recall is weak.

## 4. Sandbox Mode (#11) — DECISION: GRADUATE ONCE → WIPE DEMO — migration likely
Single account-wide Sandbox/Live state. On signup → **Sandbox**, seeded via the
existing `DemoDataSeeder`. "**Go Live**" is a ONE-WAY graduation: clear the demo data
and start the real portfolio clean (no revisiting the sandbox afterward — simplest
model). 
- **Account/user-level flag** (e.g. `IsSandbox`/onboarding state) — likely a small
  migration. While in Sandbox: persistent **"Sandbox" banner**; **HARD RULE** no real
  outbound — email/SMS/Stripe/e-sign no-op'd or clearly fake so demo play never
  contacts real tenants or moves real money (most externals already gated; add a
  sandbox short-circuit too).
- **Go Live** action: confirm dialog → wipe the portfolio's demo rows → flip flag →
  land on the empty real portfolio (or onboarding). Onboarding (#18) only runs in Live;
  at login offer "Explore in Sandbox" vs "Set up my real portfolio".
- Since graduate-once wipes rather than partitions, NO need to tag every row by
  sandbox/live — much less invasive than the toggle-back model.

## 5. Native E-signature (#23) — DECISION: BUILD NATIVE (RC) — migration likely
Build a native ESIGN/UETA-compliant e-sign for RC. Defer the standalone module (#26).
Reuse existing scaffolding: `IEsignProvider` / `LeaseEsignService` / gated provider
pattern, e-sign status UI on the lease page, `AuditLog`, QuestPDF lease PDF, and the
`apply/[token]` public-route pattern.
- **`NativeEsignProvider`** implementing `IEsignProvider`; selected when no Dropbox key
  (or always, per config). Keep DropboxSign behind the gate as optional.
- **Public `/sign/[token]`** route (tokenized, expiring, single-use per signer).
- **Consent + capture:** ESIGN disclosure + explicit consent checkbox; signature via
  typed-name-in-script-font and/or drawn-on-canvas.
- **Audit trail:** IP, timestamp, user-agent, consent, per-signer status
  (Sent/Viewed/Signed/Declined) — DB rows (new entities → migration).
- **Signed PDF:** stamp signatures into the lease PDF + append a **Certificate of
  Completion** (signers, times, IPs, document SHA-256 hash); store + lock final doc.
- **Status workflow:** Draft → Sent → Viewed → Signed → Executed / Declined; resend +
  decline.
- **v1 SKIPS** PKI/PAdES cryptographic signatures (not required for residential lease;
  hash + audit trail suffices). **#26** (multi-party routing, versioning,
  re-sign-on-change, sellable module) is a separate future project.

---

## Suggested wave order
1. **Reports Hub (#17)** — no migration, self-contained, reuses the new RangeDatePicker.
2. **Bank Reconciliation (#4/#5)** — no migration; banking/accounting.
3. **Docs + KB (#13/#14)** — no migration; web + light backend retrieval (I draft docs).
4. **Sandbox (#11)** — migration wave (account flag + go-live wipe + sandbox banner/gating).
5. **Native E-sign (#23)** — migration wave (envelope/signer/audit entities + signer UI + PDF cert).
(4 and 5 are the migration waves — one migration each, run separately.)

Plus, gated on user-provided secrets (build now, light up later): Google auth (#28),
SMTP email (#27), address autocomplete key (#19 sub-item), inline explainers (#22).
