# Surface saved scan detail on the Expense detail page (TSK-44)

**Goal:** Render the scan extraction already saved in `Expense.ReceiptData` (a JSONB
string) on the Expense detail page — a line-items table plus labeled fields for card
last 4, payment method, document kind, and vendor contact — instead of only showing
typed columns and a collapsed raw blob.

**Architecture:** Pure frontend. Parse `expense.receiptData` once (safe try/catch) in the
detail page, derive the presentable pieces, and render them inside the existing
"Receipt details" `DetailCard`, above the existing "Receipt details (raw)" expander
(kept as the full-view fallback). No backend change, no migration, no new endpoint.

**Tech Stack:** SvelteKit 5 (runes), TanStack Query, Tailwind, existing
`DetailCard` / InlineField styling tokens.

---

## Data shape

Built by `RentalCommand.Api/Scanning/ScanService.BuildReceiptDataJson` and stored as a
JSON **string** on `Expense.ReceiptData` (DTO field `expense.receiptData`):

```jsonc
{
  "documentKind": "Receipt",
  "dueDate": "2026-05-01",
  "vendor": { "address": "...", "phone": "...", "website": "...", "taxId": "..." },
  "receiptNumber": "...",
  "paymentMethod": "Visa credit",
  "cardLast4": "7529",
  "taxRate": 0.0825,
  "tip": 0, "discount": 0, "shipping": 0,
  "lineItems": [ { "description": "...", "quantity": 2, "unitPrice": 4.99, "amount": 9.98 } ],
  "extra": {}
}
```

Any value may be `null`, `0`, absent, or the whole string may be empty / unparseable.
Guard for all of that.

## File

- Modify: `web/src/routes/(protected)/accounting/expenses/[id]/+page.svelte`
  (the "Receipt details" `DetailCard`, lines ~201–220).

No other files change. Money formatting follows the established app pattern
(`new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })`) with
`font-mono tabular-nums`, matching `DataGrid` / accounting / payments pages.

## Implementation

### Task 1: Parse receiptData safely (derived state)

In the `<script>` block, add a `$derived` that parses `expense.receiptData` in a
try/catch and exposes a typed, normalized view. Return `null` when the string is
missing/empty/unparseable so the template can fall through to the raw expander only.

```ts
type ScanLineItem = {
  description?: string | null;
  quantity?: number | null;
  unitPrice?: number | null;
  amount?: number | null;
};
type ScanReceipt = {
  documentKind?: string | null;
  paymentMethod?: string | null;
  cardLast4?: string | null;
  vendor?: { address?: string | null; phone?: string | null; website?: string | null; taxId?: string | null } | null;
  lineItems?: ScanLineItem[] | null;
};

const scan = $derived.by<ScanReceipt | null>(() => {
  const raw = expense?.receiptData;
  if (!raw || typeof raw !== 'string') return null;
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? (parsed as ScanReceipt) : null;
  } catch {
    return null;
  }
});

const scanLineItems = $derived(
  (scan?.lineItems ?? []).filter(
    (li) => li && (li.description || li.amount != null || li.quantity != null || li.unitPrice != null)
  )
);
const scanLineItemsTotal = $derived(
  scanLineItems.reduce((sum, li) => sum + (typeof li.amount === 'number' ? li.amount : 0), 0)
);

const usd = (n: number | null | undefined) =>
  typeof n === 'number' && !Number.isNaN(n)
    ? new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n)
    : '';
```

Plus a `hasScanFields` derived so the labeled-fields block only renders when at least one
of card last4 / payment method / document kind / vendor phone / vendor address is present.

### Task 2: Render the labeled scan fields

Inside the "Receipt details" `DetailCard`, AFTER the existing subtotal/tax/notes fields
and BEFORE the raw expander, render read-only labeled rows (same markup as InlineField's
display mode: `<label class="mb-1 block text-xs font-medium text-muted-foreground">` +
`<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground">`). Each row
renders only when its value is present. Fields: Card Last 4 (prefix `•••• `),
Payment Method, Document Kind, Vendor Phone, Vendor Address. Add `data-testid` to each
new field (e.g. `expense-detail-scan-card-last4`, `-payment-method`, `-document-kind`,
`-vendor-phone`, `-vendor-address`). Only render the block when `hasScanFields`.

### Task 3: Render the line-items table

When `scanLineItems.length > 0`, render a table spanning both grid columns
(`sm:col-span-2`) with `data-testid="expense-detail-line-items"`. Columns:
Description · Qty · Unit price · Amount. Right-align the money/number columns with
`text-right font-mono tabular-nums`. Use a subtle bordered/`bg-background/40` container
matching the raw expander's calm styling. Show a footer total row using
`scanLineItemsTotal` and a small row count (e.g. "3 items"). Each money cell uses `usd()`;
omit empty cells gracefully (`-` or blank). Description falls back to `'-'` when missing.

### Task 4: Keep the raw expander

Leave the existing "Receipt details (raw)" `<details>` expander untouched as the full
fallback view. It stays editable in edit mode and is the only thing shown when `scan` is
null.

## Verification

- `cd web && npx svelte-check --threshold error` → 0 errors.
- `cd web && pnpm build` → succeeds.
- `dotnet build` (repo root) → succeeds (sanity; no backend change expected).
- If feasible, screenshot the expense detail page with line items rendered.

## Out of scope

- No edits to `ReceiptData` shape, the API, or any migration.
- No making line items editable (that's TSK-29) and no typed-column persistence (TSK-45).
- The raw JSON textarea stays the editing surface for receiptData.
