# Detail pages: grouped cards + edit-intent gate (TSK-28)

**Goal:** Make record detail pages calm and scannable (grouped cards with clear
hierarchy) and stop a stray label-click from silently flipping the whole form into
edit mode — the primary user is a non-technical landlord who can corrupt data by
accident.

**Architecture:** Two reusable pieces done once, then applied across detail pages.
1. A shared **`DetailCard`** wrapper (icon + title + optional description + tidy
   content slot) so every section looks the same with one component.
2. An **edit-intent gate** centralized in `InlineField`: in display mode the value is
   a calm, **non-editing** row by default. Click-to-edit is opt-in and, when enabled,
   routes through a single confirm ("Edit this record?") instead of silently entering
   edit mode. Every page already has an explicit page-level **Edit** button (with
   Save/Cancel) — that stays the primary, obvious way to edit.

**Tech stack:** SvelteKit 5 (runes), Tailwind, shadcn-svelte card/dialog,
`ConfirmDialog.svelte`, existing design tokens (`muted`, `card`, `border`).

---

## Design decisions

- **The footgun is `InlineField`'s `onedit` firing on a casual value click.** Today,
  clicking *any* value enters edit mode for the *entire* form. Fix at the source:
  - New `editTrigger` prop on `InlineField`: `'none'` (default — value is a plain,
    non-clickable row) or `'confirm'` (value is clickable but pops a confirm first).
  - Pages keep their existing page-level **Edit** button as the safe default path.
    We set the default behavior to `'none'` so accidental clicks do nothing; the Edit
    button is the obvious, intentional state.
  - Because the page-level snippets (`dateField`) and a couple of bespoke inline
    rows mirror InlineField, they get the same treatment (no silent `onclick`
    → `startEditing`).
- **Grouped cards:** introduce `DetailCard` so each section is `icon + title + rows`.
  Use existing `Card.*` under the hood for consistent surface/shadow. Group fields
  by meaning (e.g. Lease already does Term / Financials / Parties / Notes — extend
  the pattern to the flat pages: Payment, Expense, WorkOrder, Appointment, Property,
  Tenant).
- **No backend / migration.** Pure web. Enums still strings. Don't touch the
  scan→draft→confirm flow (it uses its own `<Input>` editing, not `InlineField`).

---

## Files

- Create: `web/src/lib/components/shared/DetailCard.svelte` — shared section wrapper.
- Modify: `web/src/lib/components/shared/InlineField.svelte` — add edit-intent gate.
- Modify (apply cards + gate): high-traffic first —
  `leases/[id]`, `tenants/[id]`, `properties/[id]`,
  `accounting/payments/[id]`, `accounting/expenses/[id]`,
  `maintenance/work-orders/[id]`, `appointments/[id]`.
- The pages that mirror InlineField with local `dateField`/value snippets
  (`leases`, `expenses`, `payments`, `properties`) must drop the silent
  `onclick={startEditing}` from those snippets too.
- Pages with no `InlineField` (`maintenance/inspections`, `deposits`,
  `owners/vendors`, `applications`, `maintenance/[id]`) already read-mostly /
  dialog-edit; leave layout, but adopt `DetailCard` only where it's a quick win.

---

## Tasks

### Task 1: Edit-intent gate in `InlineField`

- [ ] Add `editTrigger?: 'none' | 'confirm'` prop (default `'none'`).
- [ ] Add `oneditrequest?: () => void` callback the page wires to "open my confirm".
- [ ] Display mode:
  - `editTrigger === 'none'` (default): render the value as a plain non-interactive
    row (no button, no hover-as-editable affordance).
  - `editTrigger === 'confirm'`: render the value as a button that calls
    `oneditrequest` (page shows a ConfirmDialog) — NOT `onedit` directly.
  - Keep `onedit` for backward-compat: if a caller still passes `onedit` and no
    `editTrigger`, treat it as `'none'` (safe) — i.e. accidental clicks do nothing.
- [ ] Keep all existing `data-testid`s (`-field`, `-input`, `-value`, `-error`).
- [ ] `npx svelte-check --threshold error` passes.

### Task 2: Shared `DetailCard` wrapper

- [ ] Create `DetailCard.svelte`: props `title: string`, `icon?: Component`,
  `description?: string`, `accent?: 'primary'|'success'|'warning'|'muted'`,
  `testid?: string`, `children` snippet. Renders `Card.Root > Card.Header
  (icon + title + description) > Card.Content {children}`.
- [ ] `data-testid` = `${testid}` on root; default rows grid handled by caller.
- [ ] svelte-check passes.

### Task 3: Apply to high-traffic pages

For each of leases, tenants, properties, payments, expenses, work-orders,
appointments:
- [ ] Replace ad-hoc section `Card.Root` blocks with `DetailCard` (icon + grouped
  title). Split the previously-flat pages (payment/expense/work-order/appointment/
  tenant/property) into 2–4 meaningful groups.
- [ ] Remove the silent click-to-edit: InlineFields keep their page-level Edit
  button; drop `onedit` (or set `editTrigger="none"`). Update the local
  `dateField`/bespoke value snippets to not call `startEditing` on click.
- [ ] Add `data-testid` to any new interactive elements.

### Task 4: Verify

- [ ] `cd web && npx svelte-check --threshold error`
- [ ] `cd web && pnpm build`
- [ ] `dotnet build` sanity (no backend change, should be clean).
- [ ] Manually confirm scan→draft→confirm edit still works (it doesn't use
  InlineField — verify by reading and, if feasible, screenshot).
- [ ] Screenshot a redesigned page (lease) desktop + mobile if feasible.

### Task 5: Review, commit, PR, merge, cleanup

- [ ] requesting-code-review subagent; fix findings.
- [ ] Commit (subject + body, no AI attribution). Push branch.
- [ ] PR → main, squash-merge, delete remote branch.
- [ ] Remove worktree; attach this plan to the Notion task.
