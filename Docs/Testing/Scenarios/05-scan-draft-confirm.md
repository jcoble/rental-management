# Scenario 05 — Scan → Draft → Confirm (the flagship capture flow)

**Domain:** Scan. **Suggested tester session:** `tester1`.

## Mission
Test the product's flagship: *"the computer does the typing for you."* Upload/scan a document →
the LLM extracts fields with confidence → the user reviews a **draft** → confirming creates the real
record. Exercise this across the record types it supports (Lease, Payment, Expense, Application,
WorkOrder). The key risks: extracted fields landing in the wrong place, confirming a draft that
doesn't actually persist, the stored source file not being retrievable, or a low-confidence field
being silently accepted.

## Get acquainted with the code first
- Frontend: `web/src/routes/(protected)/scan/` and the per-type file routes
  `web/src/routes/scan-file/[id]`, plus `lease-file/[id]`, `payment-file/[id]`, `expense-file/[id]`,
  `application-file/[id]`, `workorder-file/[id]`, `document-file/[id]` (the stored-source proxies).
- Web API client: relevant endpoints in `web/src/lib/api/endpoints/` (scan/documents + the target
  record type, e.g. `expenses.ts`, `payments.ts`, `applications.ts`, `leases.ts`, `workOrders.ts`).
- API: `ScanController.cs`, `DocumentsController.cs`, and the target record controllers.
- Services: the scan/extraction service + draft→confirm path. Entities: `StoredFile`/document
  entities and the target record entities.
- Note: in local dev the scan LLM may be a local Claude CLI path. Use the sample scans in
  `Docs/sample-scans/` or `samples/` if the UI needs a file to upload.

## Flows to exercise
For at least **three** of {Expense receipt, Rent payment, Lease, Rental application, Work order}:
1. Start a scan/upload from `/scan` (or the new-record scan affordance), provide a sample document.
2. Wait for extraction; review the **draft**. Compare extracted fields to the document — are amounts,
   dates, names, references, line items in the **right fields**? Is confidence surfaced? Can you
   correct a field before confirming?
3. **Confirm** the draft and verify the **real record** was created (open it, and if you can, confirm
   via the API/DB that it persisted with the reviewed values).
4. **Retrieve the source**: confirm the stored-file proxy (`/<type>-file/<id>`) returns the original
   document (HTTP 200, correct content type).
5. Try **canceling/abandoning** a draft — confirm no orphan record is created.

## Watch especially for
- Extracted value mapped to the wrong field (e.g. tax into subtotal, due date into paid date).
- Draft that shows values but, on confirm, creates a record missing them (persistence gap).
- Confirm succeeding in UI but no record actually created, or a duplicate created on double-confirm.
- Stored source file not retrievable, wrong content type, or wrong file linked to the record.
- Low/zero-confidence fields accepted silently with no prompt to verify.

## Data hygiene
Each scan creates a NEW record — that's inherently safe. Mark any free-text you type with
`QA-T1-<HHMMSS>`. Don't delete other testers' records.

## Output
Write your report to: `Docs/Testing/Results/2026-06-28-001/05-scan-draft-confirm.md`
