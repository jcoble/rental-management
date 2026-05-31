# Phase 6 — Money movement + tax + deposits — Plan (outline, 2026-05-31)

> Planning outline to get ready for Phase 6. Builds on Phases 1–4 (Payment/Expense entities, outbox,
> notifications) and Phase 5 (mobile). Detailed task breakdown to follow when it's picked up; written so
> Sonnet agents can execute once expanded.

**Goal:** Close the financial loop — let tenants actually *pay* online, give the landlord real
books (Schedule E export), and handle security deposits properly through move-out. This turns recorded
intentions (Scheduled payments) into real money + tax-ready records.

## Themes / epics

### 1. Online rent payments (the big one)
- **Provider:** Stripe (Connect for multi-landlord SaaS later; standard account first). ACH (low fee) +
  card. Reuse the existing `Payment` rows — a payment intent settles a `Scheduled` Payment → `Paid`.
- **Entities:** `PaymentMethodToken` (tenant's saved method), `PaymentIntent`/`Transaction` (Stripe ref,
  status, fees), link to `Payment`. Webhook controller (`POST /api/v1/stripe/webhook`, `ApiKeyAuth`/signature)
  → on `payment_intent.succeeded` mark the Payment Paid + record the transaction (idempotent by Stripe id).
- **Tenant portal/app:** pay now + **autopay** (scheduled charge on rent due) — ties to Phase 4
  RentChargeWorker (charge the saved method when a Scheduled rent payment comes due, with the same
  idempotency + human-friendly receipts via the outbox).
- **Payouts:** landlord payout/distribution tracking (owner statements).

### 2. Schedule E tax reporting & export
- The `ScheduleECategory` enum already classifies expenses. Add a **tax report** service +
  `GET /api/v1/accounting/schedule-e?year=YYYY` → per-property, per-category totals (income vs each
  Schedule E line) + CSV/PDF export. Year-end owner packet. This is high landlord value and mostly
  read-only aggregation over existing data.

### 3. Security deposits (deferred from Phase 4)
- `SecurityDepositHolding` entity (amount, status Held/PartiallyReturned/Returned, held-on-lease,
  itemized deductions JSON with photos). Move-out flow: itemize deductions (reuse scan/vision for
  receipts), compute return, generate the statement notice (LLM draft + approval), enqueue to tenant.

### 4. Lease Lifecycle Autopilot (deferred from Phase 4)
- LLM-drafted renewal / late / move-out notices (`LeaseRenewalDraft`) for one-tap approval, then send via
  the outbox. Uses `ChatAsync`; safe (drafts only). Good tie-in between the AI core and automation.

### 5. Owner/co-owner financials & SaaS groundwork
- Owner statements (income, expenses, distributions per property/period).
- Multi-landlord SaaS: per-tenant (org) billing, plan limits, Stripe Connect onboarding — the path the
  spec hints at ("potential SaaS for other small landlords").

## Dependencies / sequencing
1. Schedule E export (read-only, fast win) → 2. Stripe rails + webhook + tenant pay-now → 3. Autopay tied
to RentChargeWorker → 4. Security deposits + move-out → 5. Autopilot notices → 6. SaaS billing.

## Cross-cutting
- **Compliance:** never store raw card data (Stripe tokenization); PCI scope minimized; deposit handling
  is state-regulated (interest, timelines) — encode rules like the Phase 4 state late-fee caps.
- **Idempotency everywhere** (Stripe event ids, payment intents) — same discipline as Phase 4's PeriodKey.
- **Mobile (Phase 5)** surfaces all of this: tenant pay-now/autopay, owner statements, deposit itemization
  via camera.
