# Wave-1 Fix Verification — Rental Command (live)

- **Date:** 2026-06-28/29 (run by agent `verifier`)
- **Branch/commits verified:** `main` — fix commits `e29db53` (BUG-2), `8c713cb` (BUG-1),
  `6d2e2b0` (BUG-3), `133a537` (BUG-4), `700ee23` (BUG-5), `eebbce3` (OP-1), `5e5370c` (OP-3),
  `44ab5f4` (OP-4).
- **Live surfaces:** API `https://localhost:5666` (+ HTTP `:5665`); web (SvelteKit) on a private,
  throwaway instance pointed at the same API/DB (see Environment note).
- **Test data created in portfolio 1** (admin): Property **137** "Verifier Test Property" →
  Unit **233** "101" → Tenant **268** "Verify Tenant" → Lease **240** "L-VERIFY-001" (Active,
  future-start 2026-07-01 so it is a "current" lease but auto-generates zero rent charges) →
  partial Payment(s) **3545/3546**.

## Result summary

| Fix | Pri | Result | HTTP / key evidence |
|-----|-----|--------|---------------------|
| **BUG-2** signature-queue no 500 on envelope-less lease | TOP | **PASS** | `GET /leases/240/signature-queue` → **HTTP 200** `{"leaseId":240,"items":[]}` (was 500). UI signing tab → signature-queue **200**, no 500 console error. |
| **OP-1** property delete guarded vs orphaning units | HIGH | **PASS** | `DELETE /properties/137` → **HTTP 400**, property survives. UI dialog blocks + Delete disabled, no false "removes its units". |
| **BUG-1** unit outstanding reconciles a partial | HIGH | **PASS** | partial 1000/700 → unit **300**, ledger **300**, accounting **300** (all reconcile; was 1000). |
| **BUG-3** quick-post drops "Partial" status | MED | **PASS** | Unit Rent quick-post Status = Scheduled/Paid/Late/Waived/Failed/Refunded — no "Partial". |
| **BUG-4** ledger shows collected portion of partial | MED | **PASS** | ledger emits companion `Payment +AmountPaid` line beside the `−Amount` charge. |
| **OP-3** duplicate unit number → clear message | MED | **PASS** | `POST` dup unit "101" → **HTTP 409** `Unit number "101" already exists on this property.` |
| **BUG-5** "Total Collected" tooltip copy | LOW | **PASS** | Money page tooltip = `Rent and fees received (security deposits are tracked separately under Deposits).` |
| **OP-4** tenant delete blocked on NoticeGiven lease | opt | **PASS (live)** | lease→NoticeGiven, `DELETE /tenants/268` → **HTTP 400**, tenant survives. |

## Evidence detail

### BUG-2 — signature-queue no longer 500s (TOP) — PASS
- **API:** `GET /api/v1/leases/240/signature-queue` (lease has no e-sign envelope) →
  **HTTP 200**, body exactly `{"leaseId":240,"items":[]}`. Before the fix this was **HTTP 500**
  (Postgres "could not determine data type of parameter" on the untyped NULL `signatureRequestId`).
- **UI:** Lease 240 → **Agreement & Signing** tab. Network shows
  `GET …/leases/240/signature-queue => 200` and `…/signature-status => 200`; the surface renders
  ("E-signature — Not sent"). **No 500 console error on signature-queue.** Re-verified on canonical
  `https://localhost:5667` after the cert fix: signature-queue 200, signature-status 200, AND SignalR
  `…/hubs/updates/negotiate` **200** — confirming the negotiate-500 seen earlier was purely an artifact
  of routing SignalR through the private HTTP-API instance, not a real defect.
- Screenshot: `output/playwright/verifier-bug2-signing-no-500.png`

### OP-1 — property delete orphan guard (HIGH) — PASS
- **API:** `DELETE /api/v1/properties/137` (live unit 233 present) → **HTTP 400** ProblemDetails
  `{"title":"Validation failed","status":400,"detail":"This property still has 1 unit. Remove the unit before deleting this property."}`. Follow-up `GET /properties/137` → **HTTP 200** (NOT soft-deleted).
- **UI:** Property detail → Delete → dialog body
  "Verifier Test Property still has 1 unit. Remove the unit before deleting this property."; the
  **Delete confirm button is disabled** (Cancel enabled). Copy no longer promises "This also removes its units."
- Screenshot: `output/playwright/verifier-op1-property-delete-dialog.png`

### BUG-1 — unit Rent outstanding reconciles a partial (HIGH) — PASS
- Partial payment **3545** (Amount **1000**, AmountPaid **700**) recorded on lease 240. All three
  surfaces reconciled to the **remainder 300** (not the full 1000):
  - `units/233/dashboard` → `header.outstandingRentBalance = 300.0`
  - `leases/240/ledger` → `balance = 300.0`
  - `accounting/summary` → `payments.outstanding = 300.0`
- Independently re-confirmed after a concurrent reseed churned 3545: partial **3546**
  (1200/800) → unit **400.0**, ledger **400.0**, accounting **400.0** (all equal).
- **UI:** Unit 233 Command Center header chip "Due · $400.00" and Rent tab "Outstanding balance **$400.00**".
- Screenshot: `output/playwright/verifier-bug1-unit-outstanding.png`

### BUG-3 — quick "Post payment" form drops "Partial" (MED) — PASS
- **UI:** Unit 233 → Rent tab → "Post payment" form → Status dropdown options:
  `Scheduled, Paid, Late, Waived, Failed, Refunded`. **"Partial" is absent.**
- Screenshot: `output/playwright/verifier-bug3-quickpost-no-partial.png`

### BUG-4 — lease ledger shows the collected portion of a partial (MED) — PASS
- **API** `leases/240/ledger` for the partial returns the −charge AND a companion +payment line:
  - `Charge / Partial / -1000` — "Rent for July 2026 — $1,000 due Jul 1 (partially paid, balance still owed)."
  - `Payment / Partial / +700` — **"Payment of $700 received by cash on Jul 1 — $300 still owed."**
- **UI** ledger (second partial 1200/800): "−$1,200.00" charge + separate
  "Payment of $800 received by check on Aug 1 — $400 still owed." **$800.00** line; headline Paid
  $1,800 / Balance $400.
- Screenshot: `output/playwright/verifier-bug4-ledger-collected-line.png`

### OP-3 — duplicate unit number clear message (MED) — PASS
- **API:** `POST /api/v1/units` with `unitNumber:"101"` on property 137 (already has unit "101") →
  **HTTP 409** ProblemDetails `{"title":"Conflict","status":409,"detail":"Unit number \"101\" already exists on this property."}`.
  This is the new actionable message, NOT the opaque "conflicts with existing data or a data-integrity rule".

### BUG-5 — "Total Collected" tooltip copy (LOW) — PASS
- **UI:** Money page (`/accounting`) → "Total Collected" KPI `title` attribute =
  `Rent and fees received (security deposits are tracked separately under Deposits).`
  No longer claims it includes "deposits".
- Screenshot: `output/playwright/verifier-bug5-total-collected-tooltip.png`

### OP-4 — tenant delete blocked on a NoticeGiven lease (optional) — PASS (verified live)
- Set lease 240 → **NoticeGiven**, then `DELETE /api/v1/tenants/268` (its sole lease) →
  **HTTP 400** `{"detail":"This tenant has an active lease; end or reassign it first."}`; tenant
  survives (`GET /tenants/268` → 200). Confirms the guard now covers NoticeGiven occupancy, not just Active.

## Environment note (not a fix defect)
During the run the shared dev stack was restarted/reseeded more than once (API pid 24699→44418,
web pid 44618→46501; a momentary API outage; partial payment 3545 was externally flipped to Paid mid-run).
For a window the **restarted API on :5666 served a self-signed `CN=localhost` cert (the .NET dev cert),
not the mkcert cert**, so the SvelteKit server's Node `fetch` (`DEPTH_ZERO_SELF_SIGNED_CERT`) failed and
**web login/SSR was broken for every browser session** (login action returned "Unable to connect to the
API server"). The API's plain-HTTP port :5665 was unaffected, so UI checks were performed against a
private throwaway web instance (`vite dev --port 5699`, `API_URL=http://localhost:5665`), which was shut
down afterward; no shared service was modified. A later restart **resolved** it — the API now serves the
mkcert cert and the main web app at `https://localhost:5667` logs in normally. Flagging in case the
API restart procedure should be pinned to the mkcert cert so the web app isn't intermittently unusable.

## Re-verification on canonical web (`https://localhost:5667`)
After the orchestrator restored the API's mkcert cert, **all browser-dependent checks were re-run on
the canonical `:5667` instance** (logged in via the UI "Fill dev login" + Sign In — no /login bounce):
BUG-5 tooltip, BUG-1 Rent-tab "Outstanding balance $400.00" (unit/ledger/accounting all = 400),
BUG-3 quick-post Status (Scheduled/Paid/Late/Waived/Failed/Refunded, no "Partial"), BUG-4 ledger
companion +$800 collected line, OP-1 delete dialog (correct copy + disabled Delete). All **PASS**;
all six screenshots in `output/playwright/verifier-*.png` are from `:5667`. BUG-2 re-confirmed on
`:5667` (signature-queue 200, signature-status 200, SignalR negotiate 200) and via API.

## Overall: 8/8 PASS. No fix re-opened. (Re-verified on canonical :5667.)
