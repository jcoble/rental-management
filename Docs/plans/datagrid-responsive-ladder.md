# DataGrid responsive ladder (TSK-32)

## Goal

Make the shared `DataGrid` degrade gracefully as available width shrinks, in three stages:

1. **Wide enough** → best-fit columns, no horizontal scrollbar.
2. **Too dense** (total intrinsic width > container) → horizontal scrollbar on the grid;
   columns keep readable widths instead of squishing/wrapping into an unreadable mess.
3. **Tablet / phone** → mobile cards (no half-broken cramped table in between).

Single shared component → applies app-wide (accounting ledger, leases, tenants, properties, …).

## Current state (verified)

- `web/src/lib/components/data-grid/DataGrid.svelte` renders a `<Table.Root>`.
- `Table.Root` (`web/src/lib/components/ui/table/table.svelte`) already wraps its `<table>` in
  `<div class="relative w-full overflow-x-auto">` **and** the `<table>` is `w-full`. Because the
  table is `w-full` with the default auto layout, it always sizes down to the container, so the
  overflow container never overflows — dense tables squish/wrap instead of scrolling.
- `ColumnDef` only supports an optional fixed `width` (inline style on th/td). No min/max width.
- Desktop table is `sm:block`, cards `sm:hidden` → cards only below 640px. Tablets (640–1024px)
  get the cramped 11-column ledger.

## Changes

### `web/src/lib/components/data-grid/types.ts`

Add two optional fields to `ColumnDef`:

- `minWidth?: string` — CSS min-width for the column (th + td). Lets the column keep a readable
  width; when the sum of min-widths exceeds the container the wrapper scrolls.
- `maxWidth?: string` — CSS max-width for the column. Pair with truncation so a long free-text
  column truncates instead of blowing out the layout.

### `web/src/lib/components/data-grid/DataGrid.svelte`

- Give the desktop table wrapper an explicit `overflow-x-auto` (defensive; the primitive already
  has it) and remove `w-full` stretch behavior by keeping the table at intrinsic width with a
  floor. Approach: keep `Table.Root` (still `w-full` so a sparse grid fills the container — no
  lonely scrollbar), but apply per-column `min-width` so a dense grid's intrinsic width exceeds
  the container and the existing `overflow-x-auto` kicks in.
- Apply `min-width` / `max-width` inline styles on `<th>` and `<td>` alongside the existing
  `width`.
- When a column has `maxWidth`, render its cell with `truncate` + `max-width` so overflowing text
  is ellipsised rather than forcing the table wider. (Header keeps `whitespace-nowrap`.)
- Cells already use `whitespace-nowrap` (from the primitive), which is what we want — content
  decides width, capped by `maxWidth`.

### Breakpoint

- Move the desktop/card split from `sm` (640px) up to `lg` (1024px). A dense grid (11-col
  accounting ledger) is unusable on a tablet-width table; cards read far better there. Verified by
  screenshotting the ledger at desktop / tablet / phone widths.
- `datagrid-desktop`: `hidden … lg:block`; `datagrid-mobile`: `space-y-3 lg:hidden`.

## Non-goals

- No backend / migration changes.
- No change to pagination, sorting, mobile card grouping, or existing per-column `width` overrides.

## Acceptance

- Dense grid scrolls horizontally on desktop instead of crushing; sparse grid still fills width
  (no lonely scrollbar).
- Tablet/phone → cards.
- Sticky/aligned header + existing per-column `width` overrides still work.
- `npx svelte-check --threshold error` clean; `pnpm build` succeeds; `dotnet build` sane.
- Spot-check accounting ledger + one sparse grid (e.g. owners/properties) at the three widths.
