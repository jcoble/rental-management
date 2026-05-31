# Phases 7–9 — Roadmap (2026-05-31)

Extends the product past Phase 6 (payments/tax). Everything here is buildable **without external keys**;
anything needing a provider (Stripe, LLM, SMS) is gated/config-driven and no-ops until configured —
same discipline as Phases 3/4. Each phase ships as verified, merged increments.

## Phase 6 — Money + tax (in progress)
- ✅ Schedule E tax report + CSV export (done, merged).
- **Security deposits:** `SecurityDepositHolding` (amount, status Held/PartiallyReturned/Returned, held-on-lease,
  itemized deductions JSON) + hold/deduct/return flow + move-out statement. Internal, fully functional.
- **Lease-renewal autopilot:** `LeaseRenewalDraft` (LLM-drafted renewal/late/move-out notices via `ChatAsync`,
  gated) for one-tap approval → send via the outbox. Draft/approval flow is internal; LLM call no-ops without a key.
- **Stripe rails (gated groundwork):** `PaymentTransaction`/`PaymentMethodToken` entities + a signed webhook
  controller (idempotent by event id) + a pay-intent service — all gated on a Stripe config (disabled w/o keys).
  Tenant pay-now + autopay (ties to the Phase 4 rent-charge worker) land when keys exist.

## Phase 7 — Tenant & owner experience + communications
**Goal:** make the people *around* the landlord first-class. All internal/functional.
- **Tenant portal (web + mobile):** view lease + balance, see/pay (Phase 6) rent, submit + track maintenance
  (work orders), see appointments, a message thread with management. (`PortalController` + `PortalMessage` exist —
  expand them.)
- **Owner statements/distributions:** per-owner, per-period income/expense/distribution statement (read-only
  aggregate, like Schedule E) + PDF/CSV export; an owner portal view.
- **In-app message center:** `Conversation`/`Message` (or extend `PortalMessage`) — threaded messages between
  landlord ↔ tenant ↔ owner, with SignalR realtime + the outbox for email/SMS fan-out (gated).
- **Notifications center:** surface briefing/alerts/automation events in-app (web bell + mobile) backed by a
  `Notification` entity; mark-read; ties Phase 4 outputs to the UI.

## Phase 8 — Teams, roles & access control
**Goal:** co-owners + employees with scoped access (the spec's second audience). Internal/functional.
- **Roles & permissions:** beyond the current Admin/Manager/Agent — granular, per-portfolio role assignment;
  a `Permission`/policy layer; enforce in `AuthenticatedPortfolioControllerBase` + UI gating.
- **User management:** invite/disable users, assign roles, per-user portfolio access (a user may belong to
  multiple portfolios) — extend `UserAccount`/Identity + the `(admin)` area.
- **Multi-portfolio membership:** a `PortfolioMembership` join (user × portfolio × role); the JWT/portfolio
  selector already exists — make membership real + switchable.
- **Audit trail UI:** the `AuditLog`/`ActivityLog` entities exist — a filterable activity view per portfolio.

## Phase 9 — Reporting, analytics & document hub
**Goal:** insight + records. Internal/functional (Q&A AI gated).
- **Dashboards/analytics:** occupancy, rent-collection trends, expense breakdowns, delinquency, lease-expiry
  pipeline — read-only aggregates + charts (web + mobile).
- **Owner/manager reports:** scheduled report generation (Engine) → outbox delivery (gated) + downloadable
  PDF/CSV (rent roll, P&L, owner statement, Schedule E).
- **Document hub:** the upload pipeline + `StoredFile` exist — a per-entity document library (leases,
  receipts, inspections, notices) with tags + search; polymorphic attachments already supported.
- **Portfolio Q&A moat (Phase 3 follow-on):** expand the tool-calling Q&A with more tools + a saved-insights /
  scheduled-briefing layer (the "repurpose the MCP" note); gated on the LLM key.

## Sequencing & method
Per phase: branch off `main` → sub-agent waves (one feature each) → `dotnet build` / `svelte-check` /
`flutter analyze` green → end-of-phase review → merge. Schema changes batched into one migration per wave to
avoid snapshot conflicts. External integrations (Stripe/LLM/SMS) gated OFF by default.
