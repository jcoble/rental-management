# Scan persistence: typed columns per scan type + JSONB extras (line items as real data)

**Notion task:** TSK-45 (Rental Command)
**Status:** Spec — implemented on branch `feat/scan-persistence-typed-fields` (PR open, awaiting human review of the migration + backfill before merge).

## Problem

When a scan draft is confirmed, the parsed extraction (`ExtractedReceiptDto`) carries far
more than what we persist. Today only a few promoted columns (`Subtotal`, `TaxAmount`) plus
the whole receipt JSON (`Expense.ReceiptData`) survive for Expenses; line items live only as
a nested array inside that JSON (not queryable/reportable); and Payment/Lease/WorkOrder keep
no extras blob at all. We are dropping queryable, reportable signal that the LLM already
extracted.

## Goal

For **every** scan-created record:

1. Keep the **full** extraction in a JSONB "extras" column (nothing dropped), AND
2. Promote high-value fields to **typed columns**, AND
3. Make **line items a real child table** (queryable / reportable), not just nested JSON.

This is a DEFER-RISKY data-model change: it adds columns + a child table and backfills
existing rows from the JSON that's already stored.

## Entities (`RentalCommand.Core/Entities`)

### Expense (modify)
- Add `PaymentMethod string?`
- Add `CardLast4 string?`
- Add `DocumentKind string?`
- **Keep** existing `ReceiptData` (JSONB) as the Expense "extras" superset — the web already
  reads it; do not rename/remove.
- Add nav `ICollection<ExpenseLineItem> LineItems`.

### ExpenseLineItem (new)
- `Id int`
- `ExpenseId int` (FK → Expense, **cascade delete**)
- `Description string`
- `Quantity decimal?`
- `UnitPrice decimal?`
- `Amount decimal?`
- `LineNumber int` (1-based ordinal within the receipt)

### Payment (modify)
- Add `PayerName string?`
- Add `CheckNumber string?`
- Add `BankName string?`
- `Method string?` — **already exists**, reuse it (do NOT add a duplicate).
- Add `ExtractedData string?` (JSONB extras).

### Lease (modify)
- Add `ExtractedData string?` (JSONB extras).

### WorkOrder (modify)
- Add `ExtractedData string?` (JSONB extras).

### DbContext (`RentalCommand.Data/RentalCommandDbContext.cs`)
- Register `ExpenseLineItem` (`DbSet` + entity config + FK cascade, indexed on `ExpenseId`).
- Map the new JSONB columns with `.HasColumnType("jsonb")` exactly like `Expense.ReceiptData`.
- Map decimals with `.HasPrecision(18, 2)` (matches existing money columns).

## Migration

`dotnet ef migrations add ScanExtractionTypedFields --project RentalCommand.Data --startup-project RentalCommand.Api`

Adds the new columns + the `ExpenseLineItem` table, then **backfills existing rows in `Up()`**
via robust, guarded Postgres SQL (must never throw on missing/malformed JSON):

- **Expense scalars** — copy from the already-stored `ReceiptData` JSON, guarded so it only
  runs on rows whose `ReceiptData` is a JSON object.
- **Expense line items** — insert one `ExpenseLineItem` per element of
  `ReceiptData->'lineItems'`, guarded so it only runs when that path is a JSON array; numerics
  cast via `NULLIF(... ,'')::numeric`; `LineNumber` = the `WITH ORDINALITY` ordinal.
- **Payment / Lease / WorkOrder** `ExtractedData` — left null (no pre-existing source blob to
  copy from; nothing is dropped because nothing was stored).

The backfill is idempotent-safe (additive; re-running would only duplicate line items, which
is why it runs once inside the forward migration only) and never throws on weird data.

### Backfill SQL (verbatim, runs in `Up()`)

```sql
-- 1. Promote Expense scalar fields from the stored ReceiptData JSON.
UPDATE "Expenses"
SET "PaymentMethod" = NULLIF("ReceiptData"->>'paymentMethod',''),
    "CardLast4"     = NULLIF("ReceiptData"->>'cardLast4',''),
    "DocumentKind"  = NULLIF("ReceiptData"->>'documentKind','')
WHERE "ReceiptData" IS NOT NULL
  AND jsonb_typeof("ReceiptData") = 'object';

-- 2. Promote Expense line items into the new child table.
--    Numerics cast only when the text matches a numeric literal (non-numeric strings → NULL, never
--    a cast error); description truncated to the column's 1000-char limit.
INSERT INTO "ExpenseLineItems"
    ("ExpenseId", "Description", "Quantity", "UnitPrice", "Amount", "LineNumber")
SELECT e."Id",
       LEFT(COALESCE(NULLIF(li->>'description',''), ''), 1000),
       CASE WHEN li->>'quantity'  ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (li->>'quantity')::numeric  END,
       CASE WHEN li->>'unitPrice' ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (li->>'unitPrice')::numeric END,
       CASE WHEN li->>'amount'    ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (li->>'amount')::numeric    END,
       ord::int
FROM "Expenses" e
CROSS JOIN LATERAL
    jsonb_array_elements(e."ReceiptData"->'lineItems') WITH ORDINALITY AS t(li, ord)
WHERE e."ReceiptData" IS NOT NULL
  AND jsonb_typeof(e."ReceiptData") = 'object'
  AND jsonb_typeof(e."ReceiptData"->'lineItems') = 'array'
  AND jsonb_typeof(li) = 'object';
```

## Confirm/promote path (`RentalCommand.Api/Scanning/ScanService.cs`)

The confirm path goes through service abstractions (`IExpenseService` / `IPaymentService`),
so the typed fields + line items + extras are threaded through the create requests:

- **Expense** (`ConfirmAsExpenseAsync`): populate `CreateExpenseRequest` with the promoted
  scalars (`PaymentMethod`, `CardLast4`, `DocumentKind`), the `ReceiptData` extras (unchanged),
  and a list of line items built from `dto.LineItems` (1-based `LineNumber`). `ExpenseService`
  writes the `Expense` + child `ExpenseLineItem` rows in one `SaveChanges`.
- **Payment** (`ConfirmAsPaymentAsync`): populate `CreatePaymentRequest` with `PayerName`,
  `CheckNumber`, `BankName`, `Method` ("Check"), and an `ExtractedData` extras blob built from
  the DTO. `PaymentService` writes them onto the `Payment`.
- **Lease / WorkOrder**: populate `ExtractedData` from the draft's raw `ExtractedFields` JSON
  (the full superset) when creating the record.

The JSONB extras always stays the full superset of what was extracted.

## Verification

- `dotnet build` → 0 errors.
- `dotnet test` → all pass. New/extended tests assert: a confirmed Expense persists the typed
  columns + `ExpenseLineItem` rows + `ReceiptData`; a confirmed Payment persists
  payer/check/bank/method.
- The migration is **not** applied against the live dev DB during this work; it self-applies
  on the next boot under the shared advisory lock.
