# Accounting, Mobile, Messaging, and Auth Roadmap

Date: 2026-06-03

## Goal

Turn RentalCommand from separate operational lists into a capture-first property accounting system:

- Capture receipts, bills, checks, bank activity, and documents with the least typing possible.
- Reconcile those records against actual money movement.
- Produce owner/accountant-ready reports.
- Keep web and mobile behavior aligned.
- Make SMS/email and Google auth production capabilities, not stubs.

## Current Reality

- Web exists; mobile app does not exist yet.
- Accounting has payments, expenses, Schedule E categories, scan-created receipt details, and a basic summary.
- Scan creates expenses and now links confirmed scans to the created expense.
- Bank connection is not implemented. Plaid is referenced in specs only.
- SMS/email dispatch is not implemented. The engine has an outbox and `INotificationChannel`, but the current channel logs stub messages.
- Auth is password/JWT/refresh-cookie based. Google auth is not implemented.

## Product Lanes

### Lane A: Accounting Workspace

Purpose: make Accounting a place to close books, not just list payments and expenses.

Deliverables:

- Unified transaction ledger: payments, expenses, refunds, deposits, owner draws, bank feed items.
- Reports API and UI:
  - Profit and loss by property.
  - Cash flow.
  - Rent roll.
  - Tenant ledger.
  - Vendor ledger.
  - Transaction ledger.
  - Schedule E.
  - Owner statement.
  - Unpaid bills / A/P aging.
  - Delinquency / past-due rent.
  - Year-end accountant packet.
- Drill-down links from report totals to source records.
- CSV export first, PDF export second.
- Accounting period close checklist.

Acceptance:

- Every report uses one API contract shared by web and mobile.
- Every report total can be traced to source transactions.
- No report is web-only unless explicitly documented as desktop-only.

### Lane B: Bank Feed and Reconciliation

Purpose: reduce missed transactions and prevent scan/manual/bank duplicates.

Deliverables:

- Read-only bank connection using Plaid.
- Bank accounts and bank transactions tables.
- Bank inbox for unmatched transactions.
- Matching engine:
  - Bank charge to expense.
  - Bank deposit to payment.
  - Receipt scan to bank transaction.
  - Duplicate warnings for same vendor/date/amount/reference.
- Reconciliation screen:
  - Matched.
  - Needs review.
  - Missing receipt.
  - Missing ledger entry.
  - Ignored/personal transfer.
- Import fallback for CSV/QIF/OFX bank statements before Plaid is available.

Acceptance:

- The app remains useful without a bank connection.
- Bank connection is read-only by default.
- No bank transaction creates a ledger record without human confirmation or an explicit rule.

### Lane C: Scan Expansion

Purpose: make scan the universal intake layer.

Deliverables:

- Scan/import test fixture pack:
  - Paid receipt PDFs/images.
  - Unpaid bill PDFs.
  - Rent check images.
  - Deposit slips.
  - Bank statements.
  - W-9 style vendor documents.
  - Lease excerpts.
  - Insurance certificates.
  - Utility/property tax/mortgage documents.
- Receipt/invoice scan -> paid expense or unpaid bill.
- Check scan -> payment marked paid.
- Deposit slip scan -> bank/payment reconciliation draft.
- Bank statement scan/import -> reconciliation draft.
- W-9 scan -> vendor tax profile.
- Lease scan -> lease draft.
- Insurance/utility/tax/mortgage document scan -> structured record + reminder.
- Document routing classifier: identify the document type before field extraction.
- Rules and memory:
  - Vendor default category.
  - Vendor/property matching.
  - Card/account matching.
  - Recurring bill detection.

Acceptance:

- Fixture pack exists under `output/scan-fixtures/` with expected extraction fields and test purpose.
- Every scan result has a target record link.
- Confirmed scans are locked as audit/intake history.
- Edits happen on the created business record.
- Low-confidence fields and likely duplicates are first-class review items.

### Lane D: Mobile App

Purpose: make the capture/approval workflow natural on a phone.

Platform decision: Flutter, as already documented in the master spec.

Deliverables:

- `mobile/` Flutter app scaffold.
- Google sign-in.
- Secure token storage.
- Camera scan upload.
- Scan review/confirm screen with the same fields and labels as web.
- Accounting dashboard and report summaries.
- Push notifications for scan-ready, late rent, due bills, and daily briefing.
- Mobile parity checklist for every new web workflow.

Acceptance:

- Any web workflow that is naturally mobile, especially forms and scan review, gets a mobile parity task before being called complete.
- Mobile uses the same API contracts as web.
- Mobile-specific UX can differ, but data capability must not drift.

### Lane E: SMS and Email

Purpose: let tenants, vendors, and the owner act without logging into the app.

Deliverables:

- Real SendGrid email channel.
- Real Twilio SMS channel.
- Inbound Twilio webhook with signature validation.
- Outbox delivery status and retry visibility.
- Daily briefing over SMS/email.
- SMS maintenance intake:
  - Tenant texts issue/photo.
  - System creates work-order draft.
  - Owner/employee confirms.
- Vendor completion replies:
  - Reply DONE.
  - Attach photo.
  - Close/update work order after review.
- W-9 request workflow over SMS/email.

Acceptance:

- Logging channel remains available only for local development.
- Production config must fail closed if provider credentials are missing.
- Inbound messages are auditable and tied to tenant/vendor/contact records.

### Lane F: Google Auth

Purpose: replace the current password-first login with Google-first auth.

Deliverables:

- Google OAuth/OIDC provider config.
- API endpoints:
  - Start external login.
  - Callback/token exchange.
  - Link Google identity to existing user.
  - Provision first admin safely in development/setup.
- Web login page changed to "Continue with Google".
- Existing password auth kept temporarily behind a local/dev/admin fallback flag.
- Mobile Google sign-in path.
- Account linking and invite flow for employees/co-owners.

Acceptance:

- Google auth issues the same portfolio-scoped JWT/refresh session used today.
- Existing users can be linked without data loss.
- Password login is not the primary production path.

## Implementation Order

1. Reports/ledger foundation API and web UI.
2. Mobile parity checklist and `mobile/` scaffold.
3. Real email channel, then real SMS channel.
4. Google auth for web, then mobile Google sign-in.
5. Bank feed data model and reconciliation inbox.
6. Scan routing expansion and receipt/bank matching.
7. Year-end accountant packet and 1099 workflow.

## Active First Slice

Build `GET /api/v1/accounting/reports` with:

- Transaction ledger.
- Property financial summaries.
- Schedule E totals.
- Vendor 1099 checklist.

Then render those sections in the web Accounting page. Mobile must consume this same endpoint when the Flutter app is scaffolded.
