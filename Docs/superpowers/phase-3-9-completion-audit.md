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
| DO- Lease scanner + 5-question generator + e-sign | Partial | DONE: **scan a lease PDF -> Lease** (LeaseExtractionSchema, chained Tenant/Unit, IDOR-safe) and **generate a lease-agreement PDF** (QuestPDF, `POST /leases/{id}/generate-document`) on web + Flutter. REMAINING: the **e-sign** send/track workflow (gated IEsignProvider + envelope fields + signed webhook) — next wave. |
| Fair-Housing-safe copy | Usable | `POST /ai/fair-housing-check` (gated LLM) flags discriminatory language (protected classes, steering, "no kids") and suggests a compliant rewrite; surfaced on the web notices composer ("Check for fair-housing issues" → flagged phrases + one-tap rewrite). Never reports a false "compliant": no key / errors / unparseable all return reviewed:false. |

## Phase 7 - Clean Books

| Feature                           | Status  | Notes                                                                                                                           |
| --------------------------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------- |
| DO- Expense Schedule E categorization | Usable  | Scan and expense forms use Schedule E-style categories. (Verify/polish the workflow.)                                       |
| Receipt/deposit dedupe | Usable | Explicit **duplicate-review queue** (`GET /banking/review-queue`) lists bank lines with a suggested payment/expense match side-by-side; **Confirm match** links them (excluded from totals so money isn't double-counted) and **Not a match** dismisses. On the web Banking page + Flutter Banking screen. Reused the existing `MatchStatus` (no migration). |
| Year-end export packet | Usable | `GET /accounting/year-end-packet?year=` streams a single **QuestPDF** to hand the accountant: cover, Schedule-E summary (reconciles with the CSV), per-property P&L, monthly cash-flow, and rent roll. Downloadable from the web Tax page and the Flutter Owner Reports screen (year selector, default prior year). Schedule-E CSV/P&L/1099 review already existed. |
| 1099/W-9 checklist and texts | Usable | `POST /vendors/{id}/request-w9` texts the vendor a plain-language W-9 request; the 1099 checklist (`accounting/reports.vendors1099`, flags `Is1099Eligible && !W9OnFile`) is surfaced on the web Tax page with a per-vendor text action, and W-9-on-file is togglable on the vendor (web + Flutter). |
| DO- Ownership/entity modeling UI      | Usable  | Owners/entities pages exist and tie into reports. (Verify/polish the workflow.)                                             |
| Security deposit tracking | Usable | Deposits + itemized deductions, plus a **photo-backed move-out statement PDF** (`GET /security-deposits/{id}/move-out-statement`): deposit held, itemized deductions, attached photos (entityType=SecurityDeposit), net refund/owed. Downloadable on web deposit detail (with photo attach) + Flutter deposit detail. |

## Phase 8 - Inspections and Maintenance Mobile

| Feature                                                    | Status  | Notes                                                                                            |
| ---------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------------------ |
| Smart inspection checklists + PDF report + work orders | Usable | Templated checklists (built-in Move-In/Move-Out/Annual-Safety) materialize per-item Pass/Fail/N-A with notes + photos; Complete generates a **QuestPDF report** (items by area + embedded photos) and **auto-creates a Work Order for each failed item** (linked + status event). Web run page (`/maintenance/inspections/{id}`) + Flutter on-site tool (big toggles, camera per item, report). |
| Photo/voice maintenance requests + live status stream  | Usable | **Live status timeline** (`WorkOrderStatusEvent`, From→To, who, when, notes) on staff + tenant detail (web + Flutter), tenant-ownership-scoped. **Photo** capture on tenant create (web + mobile) and add-photo on the staff detail + photo strip. Status changes can carry a note. (Voice intake on a maintenance request is the one remaining bit — route through the existing VoiceController; low priority follow-up.) |
| Vendor SMS dispatch + scorecard                        | Usable | `POST /work-orders/{id}/dispatch` texts the vendor the job + "reply DONE"; the inbound SMS router handles vendor **DONE** (auto-completes the work order + writes a "Vendor" timeline event) before falling back to tenant rent-YES. Ratings (`POST /vendors/{id}/ratings`) accrue into a **scorecard** (`GET /vendors/{id}/scorecard`: avg rating, jobs completed, avg response hours). Web (Owners&Vendors list rating column + scorecard detail, dispatch/rate from work order) + Flutter (vendors feature + dispatch/rate). |
| Mobile field queue                                     | Usable | Flutter staff work-order detail now has the status timeline, add-photo (before/after), **tap-to-navigate (Open in Maps)**, and status-note updates, on top of the prioritized Home field queue. |
| Recurring maintenance tasks | Usable | `RecurringMaintenanceTask` (Weekly/Monthly/Quarterly/SemiAnnually/Annually) + Engine `RecurringMaintenanceWorker` (advisory-locked, local-tz, idempotent, catches up without flooding) auto-creates a Work Order each period. Web `/maintenance/recurring` + Flutter management screen with property/unit/vendor pickers, interval/priority, next-due, active toggle. |

## Phase 9 - Onboarding and Migration

| Feature                              | Status  | Notes                                                              |
| ------------------------------------ | ------- | ------------------------------------------------------------------ |
| Guided 5-step setup wizard | Usable | `/onboarding` full-page stepped flow (Portfolio -> Owner -> Property+Units -> Tenants -> Lease) over the existing create endpoints + Zod schemas; remembers what it created so later steps prefill, marks already-done steps, and is safe to re-enter (no duplicates). Empty-portfolio banner on the dashboard + a Setup nav link. (Web; Frank is white-gloved, web is the SaaS on-ramp.) |
| DO- CSV/bulk import                  | Missing | No bulk import workflow.                                           |
| Bulk lease PDF scan | Usable | `POST /scans/batch` (many PDFs, field `files`) creates a `ScanBatch` of Lease drafts the existing Engine worker extracts; web bulk-import page (drag many PDFs) + a polling **batch review queue** (progress, per-draft Review -> scan->Lease confirm, links to created leases). Wired from the scan page + the onboarding wizard. |
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
