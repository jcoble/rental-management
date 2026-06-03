# Rental Command dad-first product research

Date: 2026-06-02

## Short version

The product should not try to beat Buildium, AppFolio, DoorLoop, TurboTenant, Avail, RentRedi, Baselane, or Stessa by becoming a bigger property-management suite first. They already cover the standard checklist: listings, screening, e-sign leases, rent collection, maintenance, portals, accounting, reports, and now AI.

The wedge is narrower and stronger:

> A small landlord can run the business from his phone by taking pictures, forwarding scans, and replying to plain-English texts.

That means the highest-value work is not more screens. It is making the app forgiving, proactive, and paper-native:

1. Every scanned/AI-created record must be editable after save.
2. Scan intake must expand from receipts to checks, leases, bank deposits/statements, applications, IDs, W-9s, inspection photos, and "other document".
3. The app needs a daily "what needs me today?" briefing and an "ask anything" box before more dashboards.
4. SMS and email need to work for real; for Dad and tenants, messages are the UI.
5. Accounting should stay cash-basis and landlord-simple: rent roll, tenant ledger, property P&L, Schedule E categories, 1099/W-9 checklist, year-end accountant packet.

## What the code has now

Verified in this worktree:

- Scan upload, async extraction, review, and confirm-to-expense are implemented for receipt/invoice to expense. The worker always uses `ReceiptExtractionSchema`, so check-to-payment and lease-to-lease are not implemented yet.
- LLM providers exist for OpenAI and Anthropic, with deterministic no-key behavior.
- Expenses have richer scanned receipt fields: subtotal, tax amount, and receipt JSON. Before this pass, the update DTO and web form could not correct those fields after confirm.
- Payment CRUD exists, including `mark-paid`, method, external reference, paid date, and notes. Before this pass, the web form hid most of those and sent `type` instead of `paymentType`.
- Lease CRUD exists. Before this pass, the web form hid `LeaseNumber` and dropped required `PropertyId` before submit.
- Accounting is currently a summary only: Schedule E expense totals plus collected/outstanding/overdue payments. There is no rent roll report, tenant ledger page, property P&L, cash-flow report, CSV/PDF export, 1099 packet, or bank reconciliation.
- SMS/email have only a DB outbox and logging transport. Real Twilio/SendGrid providers are not wired.
- Bank setup/reconciliation is not working yet. `IPaymentProvider` is only an interface; I found no Plaid/Stripe/bank-account connection implementation.

## Live competitor signals

- Buildium now markets Lumina AI, including AI Bill Scan that can extract invoice data and generate draft bills from emailed or uploaded invoices. Source: [Buildium Lumina AI](https://www.buildium.com/features/ai-property-management-software/).
- AppFolio Smart Bill Entry reads PDF invoices, extracts fields, and uses confidence scoring for review/approval. AppFolio also markets Realm-X AI performers for leasing, maintenance, and resident communication. Sources: [AppFolio Smart Bill Entry / Document AI](https://appfolio-engineering.squarespace.com/appfolio-engineering/2022/11/11/understanding-invoices-with-document-ai), [AppFolio maintenance AI](https://www.appfolio.com/property-manager/maintenance), [Realm-X Performers](https://www.appfolio.com/articles/performers/).
- DoorLoop is positioning itself as AI-native, with an AI Assistant for leases, rent collection, maintenance, messaging, and reports. Sources: [DoorLoop AI software](https://www.doorloop.com/ai-property-management-software), [DoorLoop AI Assistant Help](https://support.doorloop.com/en/articles/12312822-doorloop-ai-assistant-agent-overview).
- RentRedi has AI onboarding that scans uploaded leases to create property, unit, tenant, rent, and lease-term setup data. It also advertises AI receipt capture, bank-feed expenses, Schedule E summaries, P&L, and export reports in its accounting suite. Sources: [RentRedi AI onboarding](https://rentredi.com/blog/ai-onboarding-for-landlords/), [RentRedi property add help](https://help.rentredi.com/en/articles/3219238-how-to-add-a-property), [RentRedi accounting FAQ](https://help.rentredi.com/en/articles/13513269-rentredi-accounting-suite-faq).
- Stessa has smart receipt scanning, transaction matching, and a tax package with income statement, net cash-flow report, and receipts backup. Sources: [Stessa receipt scanning](https://support.stessa.com/en/articles/4623262-stessa-mobile-smart-receipt-scanning), [Stessa tax center](https://www.stessa.com/resources/tax-center/).
- TurboTenant and Avail show the small-landlord table stakes: listings, screening, leases/e-sign, online rent, maintenance, tenant portal, messaging, and expense tracking. Sources: [TurboTenant](https://www.turbotenant.com/), [TurboTenant features](https://www.turbotenant.com/features/), [Avail](https://www.avail.co/).
- Baselane is strong on landlord banking/accounting: smart rules/AI categorization, receipt matching, consolidated ledger, tax package, and Schedule E reporting. Source: [Baselane](https://www.baselane.at/).

## What can set us apart

### 1. The "shoebox to accountant packet" promise

Competitors have receipt capture and reports, but the Dad-specific version should be simpler:

- He scans every receipt, invoice, check, bank deposit slip, W-9, lease, and notice into one inbox.
- The app routes it to the right draft.
- The app asks only the missing questions.
- At year end, Susan or the accountant gets one packet: property P&L, Schedule E category summary, rent roll, tenant balances, 1099/W-9 checklist, and receipts zip.

The UI copy should be "Get my tax stuff ready", not "financial reporting".

### 2. Text-message operations, not portal-first operations

Most competitors have tenant portals and apps. The easier wedge is:

- Dad receives one daily text: late rents, bills to approve, leases ending, maintenance due, inspections, missing receipts.
- Dad replies `YES`, `NO`, `PAID`, `DONE`, or a short sentence.
- Tenants can text photos of maintenance issues without logging in.
- Vendors can reply `DONE` with a photo.

This gives the system value even when nobody wants to learn software.

### 3. A single "paper inbox" that accepts everything

Buildium/AppFolio focus heavily on invoices/bills. RentRedi focuses on lease onboarding. Rental Command can win for small landlords by making one inbox handle all paper:

- Phone camera.
- Desktop scanner drag/drop.
- Forwarded scan-to-email.
- SMS photo.
- Later: mailbox address for physical paper if this becomes SaaS.

The route decision should be AI-assisted but review-gated: receipt, bill, check, lease, application, ID, W-9, bank statement/deposit, notice, inspection photo, other.

### 4. "Fix it later" as a first-class trust feature

For a non-technical owner, mistakes must be cheap. A scan-confirmed record cannot feel permanent. Every destination record needs:

- A document preview.
- The fields promoted into columns.
- The extra dynamic JSON fields in a friendly "Other details from scan" section.
- An audit trail of AI guess -> human correction.
- A "wrong document type" repair flow that reopens a confirmed record as a new draft and links the old record.

### 5. Plain-English AI that answers from the database

Competitor AI assistants are increasingly table stakes. The version here should be more concrete:

- "Who is late?"
- "What did I spend on Oak Street this year?"
- "Did I ever get a W-9 from Joe's Plumbing?"
- "What do I need to do today?"
- "Show me every receipt over $500 with no property."

The existing MCP surface is the technical advantage: use it to answer from live data instead of generating generic property-management advice.

## Immediate engineering backlog

### Finish the scan-to-record spine

1. Add document-type routing for scan drafts instead of assuming every scan is a receipt.
2. Implement check-to-payment. Extract payer, amount, check number, date, memo, and likely lease/tenant; confirm should call the existing mark-paid flow or create a payment.
3. Implement lease-to-lease. Extract lease number, property/unit, tenant, term, rent, due day, deposit, late fee, move-in/move-out, and notes. Allow chained drafts for missing tenant/unit.
4. Add "other document" attachment flow so an unrecognized scan still gets stored and attachable.
5. Build destination-record scan-detail editors for the dynamic JSON fields, not just the review screen before confirm.

### Make the current app trustworthy

1. Every CRUD screen should expose every backend field or intentionally hide it with a reason.
2. Make form property names match DTO names (`paymentType`, `leaseNumber`, `propertyId`, etc.).
3. Add empty-state and "what to do next" flows for Dad: "Scan a receipt", "Mark rent paid", "Add a lease".
4. Add undo/reopen for accidental scan confirmation.
5. Show source document thumbnails on destination records.

### Make money useful before bank integrations

1. Tenant ledger page: all charges/payments, running balance, plain-English "why".
2. Rent roll: unit, tenant, rent, due date, paid/late, balance.
3. Property P&L: money in, money out, net, by month/year.
4. Schedule E report: category totals and uncategorized/missing-property cleanup queue.
5. Accountant export: CSV first, PDF later.
6. 1099/W-9 checklist from existing vendor fields.

### Wire real communication

1. Twilio outbound SMS through the existing outbox.
2. Twilio inbound webhook with reply routing.
3. SendGrid/Postmark email transport for auth, notices, and daily briefing.
4. Daily briefing generator, rules-first before LLM prose.

### Keep bank connection optional

Dad should not need to connect a bank account for the app to be useful. When bank support lands, position it as read-only reconciliation:

- Match deposits to rent payments.
- Match charges to scanned expenses.
- Flag duplicates or unmatched transactions.
- Never require write access for MVP.

## Product sequence I would use

1. Fix forms and scan-confirm editability.
2. Implement check-to-payment and lease-to-lease.
3. Add tenant ledger, rent roll, and property P&L.
4. Add real SMS/email and the daily briefing.
5. Add Portfolio Q&A grounded in the DB/MCP tools.
6. Add read-only bank reconciliation.
7. Add onboarding import: upload all leases, scan and confirm one-by-one.

This keeps the product aimed at the real first user instead of drifting into a generic SaaS checklist.
