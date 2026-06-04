# Phase 3-9 Completion Audit

Source spec: `Docs/superpowers/specs/2026-05-30-rental-command-master-spec.md`

This file tracks whether each feature is complete as a usable workflow across API,
web, engine, and Flutter. "Partial" means a model, endpoint, worker, or page exists,
but the landlord cannot use the complete workflow in a logical place.

## Phase 3 - Real AI Core

| Feature                                                                                                                                                                                                                                                                 | Status  | Notes                                                                                                                                                                    |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| DO- +1 priority.  The whole scanning thing needs to be brought together better.  Things route correctly, fill in right fields.  Makes theh system usable by someone like my dad who doesn't use laptops much.LLM-backed intake, notice generation, portfolio summary | Usable | Scan intake now routes Expense, Payment, and WorkOrder targets from web/Flutter; work-order scans use a maintenance-specific LLM schema and review/confirm into maintenance. |
| Daily Briefing                                                                                                                                                                                                                                                          | Usable  | API, engine delivery, web dashboard, and Flutter home are wired.                                                                                                         |
| DO- Needs to send out as sms and email if set to - Portfolio Q&A                                                                                                                                                                                                        | Usable  | API/web/Flutter assistant surfaces exist. Web also has the always-available assistant component.                                                                         |
| Voice capture to draft record                                                                                                                                                                                                                                           | Usable  | API, web scan voice capture, and Flutter microphone capture all create reviewable scan drafts for WorkOrder/Expense/Payment confirmation.                                |
| Tenant lease FAQ bot                                                                                                                                                                                                                                                    | Usable  | Lease detail now has grounded Q&A on web and Flutter; API falls back deterministically when no LLM is configured. Tenant portal placement is still a follow-up.          |
| AI maintenance triage from photo                                                                                                                                                                                                                                        | Usable  | Web/Flutter scan can target WorkOrder; the engine uses a maintenance photo schema for title, description, category, priority, property/unit/tenant/vendor hints, and estimate. |

## Phase 4 - Automation, Notifications, Lease Lifecycle

| Feature                                                                                                                      | Status  | Notes                                                                                                                                               |
| ---------------------------------------------------------------------------------------------------------------------------- | ------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| DO- put in settings like in EdiPlatform whether in-app, checkboxes rorRecurring rent auto-posting                            | Usable  | Engine worker/service exist with idempotent posting, and admin settings now control enablement, lead days, and tenant notices from the encrypted settings row. |
| DO- put in settings like in EdiPlatform whether in-app, checkboxes rorRecurring rent auto-postingAuto late fees              | Usable  | Engine worker/service exist; admin settings now control enablement, grace days, and tenant notices while retaining configured legal caps.             |
| DO- put in settings like in EdiPlatform whether in-app, checkboxes rorRecurring rent auto-postingLease expiry reminders      | Usable  | Engine worker creates reminders; admin settings control enablement and reminder window; web/mobile Notices queue can generate renewal and move-out drafts. |
| DO- put in settings like in EdiPlatform whether in-app, checkboxes rorRecurring rent auto-postingSMS/email communication     | Usable  | SignalWire and email routing are implemented through notification settings and conversations. Spec says Twilio, but current provider is SignalWire. |
| DO- put in settings like in EdiPlatform whether in-app, checkboxes rorRecurring rent auto-postingReply-YES rent confirmation | Usable  | Public `/api/v1/sms/inbound` SignalWire/Twilio-compatible webhook recognizes YES/Y from tenant phones and marks the oldest unpaid rent item paid.    |
| DO- put in settings like in EdiPlatform whether in-app, checkboxes rorRecurring rent auto-postingLease Lifecycle Autopilot   | Usable  | Notice drafts are persisted, editable, dismissible, and one-tap approved into tenant conversations with Portal/Email/SMS channels.                  |

## Phase 5 - Money

| Feature                        | Status  | Notes                                                                                                                            |
| ------------------------------ | ------- | -------------------------------------------------------------------------------------------------------------------------------- |
| Scan check to payment | Usable  | Scan service handles `RentCheck` and creates a paid Payment; web + Flutter review screens confirm. Now locked by a unit test (`ConfirmAndCreateAsync_ReviewingRentCheckDraft_CreatesPaidPayment`). |
| Read-only Plaid reconciliation | Usable | Plaid sandbox Link, encrypted access tokens, bank connections/transactions, sync, matching, accounting ledger/totals, and Flutter/web banking surfaces are wired. |
| Transparent tenant ledger      | Usable | `GET /leases/{id}/ledger` (was 404 — no backend; now built, tenant-ownership-scoped) returns each charge/payment with a deterministic plain-English "why" + running balance. Surfaced on web lease detail (Account History), accounting ledger tooltips, and the tenant portal; Flutter has a `LeaseLedgerView` + portal Account History. |
| Plain-English money snapshots  | Usable | `GET /accounting/snapshot` returns collected/spent/kept + past-due with ready-to-show plain-English sentences; "Your money" card on the web dashboard and Flutter home. |
| DO- Online card/ACH            | Partial | Stripe payment intent/webhook exists. ACH/Plaid payment path is not complete.                                                    |

## Phase 6 - Applications, Screening, E-sign

| Feature                                           | Status  | Notes                                                                                                                          |
| ------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------ |
| No-login application + ID autofill            | Usable | Public `/apply/{token}` form (no login) with "scan your ID to autofill" (gated LLM), FCRA consent capture, and portfolio resolved by token (IDOR-safe, ID number never stored). Landlord reviews on web + Flutter (list/detail, approve→creates Tenant, decline/withdraw, shareable link). TransUnion screening is the next wave (consent seam in place). |
| DO- TransUnion screening with FCRA flow           | Missing | `IScreeningProvider` exists only as an interface. No application consent/adverse-action workflow.                            |
| DO- Lease scanner + 5-question generator + e-sign | Partial | Lease entity supports `PendingSignature` and `IEsignProvider` exists, but there is no complete generator/signing workflow. |
| DO- Fair-Housing-safe copy                        | Missing | No explicit fair-housing review/generation surface.                                                                            |

## Phase 7 - Clean Books

| Feature                           | Status  | Notes                                                                                                                           |
| --------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------- |
| DO- Expense Schedule E categorization | Usable  | Scan and expense forms use Schedule E-style categories. (Verify/polish the workflow.)                                       |
| DO- Receipt/deposit dedupe            | Partial | Bank transactions now suggest and store matches against payments/expenses. Needs a more explicit duplicate-review queue.        |
| DO- Year-end export packet            | Partial | Schedule E CSV, tax page, owner reports, P&L and 1099 review exist. Full PDF packet/cash-flow/rent-roll export is not complete. |
| DO- 1099/W-9 checklist and texts      | Partial | Vendor fields and accounting review exist. Text request flow is not implemented.                                                |
| DO- Ownership/entity modeling UI      | Usable  | Owners/entities pages exist and tie into reports. (Verify/polish the workflow.)                                             |
| DO- Security deposit tracking         | Partial | Deposits exist. Photo-backed move-out statements are not complete.                                                              |

## Phase 8 - Inspections and Maintenance Mobile

| Feature                                                    | Status  | Notes                                                                                            |
| ---------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------ |
| DO- Smart inspection checklists + PDF report + work orders | Missing | Inspections are still simple records.                                                            |
| DO- Photo/voice maintenance requests + live status stream  | Partial | Work orders and SignalR exist. Flutter staff and tenant maintenance creation can now attach a camera/gallery photo. Voice intake and richer status stream are still incomplete. |
| DO- Vendor SMS dispatch + scorecard                        | Missing | Vendors exist. Dispatch, reply DONE, ratings, and performance scoring are not implemented.       |
| DO- Mobile field queue                                     | Partial | Flutter Home now shows top open work orders; still needs map/action/photo workflow.              |
| DO-  Recurring maintenance tasks                          | Missing | No recurring maintenance model/worker.                                                           |

## Phase 9 - Onboarding and Migration

| Feature                              | Status  | Notes                                                              |
| ------------------------------------ | ------- | ------------------------------------------------------------------ |
| DO- Guided 5-step setup wizard       | Missing | No onboarding wizard.                                              |
| DO- CSV/bulk import                  | Missing | No bulk import workflow.                                           |
| DO- Bulk lease PDF scan              | Missing | Single-document scanning exists; no bulk lease migration workflow. |
| DO- Opening balances/history scoping | Missing | No migration scoping workflow.                                     |

## Cross-cutting — Web UX

| Feature | Status | Notes |
| ------- | ------ | ----- |
| Inline-edit on detail pages (replace modal "Edit" dialogs) | Usable | Detail pages edit the record **in place** (tap Edit → fields become inputs → Save/Cancel in the header) via the shared `web/src/lib/components/shared/InlineField.svelte`. DONE: expenses, payments, work-orders, **tenants, leases, properties, appointments, and `maintenance/[id]`** (modal Edit dialogs removed; reuse the existing `<entity>.update` mutation + Zod schema). N/A: `deposits/[id]` has no record-edit endpoint (only add-deduction / process-return actions), so nothing to inline; inspections have no detail route (simple records). Fixed a latent bug en route: the lease modal validated against a schema requiring `leaseNumber` it never supplied (save was broken) — now supplied. |

## Next Completion Lanes

1. **Mobile field operations:** add voice maintenance intake, field queue actions, before/after photos, and clearer tenant/owner status stream.
2. **Lease Lifecycle Autopilot:** add generated notice drafts with one-tap approve/send for renewal, late, and move-out workflows.
3. **Banking and reconciliation:** finish duplicate-review queue and tighter accounting review workflow.
4. **Onboarding/migration:** add setup wizard and CSV/bulk lease import path.
5. **Applications/screening/e-sign:** add public applications before wiring paid screening providers.
