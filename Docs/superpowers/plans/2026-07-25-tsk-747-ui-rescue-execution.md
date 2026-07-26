# TSK-747 Scope-Locked Execution Plan — Web UI Rescue

## Active goal

Make Rental Command understandable to a first-time landlord by restoring the Unit-centered Command Center, presenting one task at a time, replacing internal terminology with plain English, and preserving every existing business, permission, persistence, and deep-link contract.

This plan is grounded in:

- `Docs/Reviews/2026-07-25-tsk-747-lane-1-discovery.md`
- `Docs/Reviews/2026-07-25-tsk-747-lane-2-discovery.md`
- `Docs/Reviews/2026-07-25-tsk-747-lane-3-discovery.md`
- `Docs/Reviews/2026-07-25-tsk-747-ui-audit-checklist.md`

Production code remained frozen until the requested cross-lane review.

## Scope lock

### In scope

1. Restore the searchable **Command Center** navigation control using a server-paged query.
2. Make Unit tabs, secondary views, and folded records change instantly through shallow routing.
3. Render only one selected Unit sub-workflow instead of stacking sibling workflows.
4. Fold Unit-owned lease, payment, expense, and related rows back into their Unit destination.
5. Replace raw Unit/lease/payment terminology with plain English, including the user-provided Rent charge example.
6. Turn the three notification routes into one understandable, route-backed three-step journey and collapse tenant-notice policies.
7. Complete Lane 3A: money-language mapping, consistent controls, safe integration copy, current Help links, and corrected Security Deposits documentation.
8. Preserve loading, error, empty, access, mutation, idempotency, server-side query, and transaction behavior.

### Explicitly out of scope

- Backend schema, API, query, notification worker, or transaction changes.
- Replacing legally meaningful terms such as Schedule E, 1099, W-9, reconcile, or security deposit; these receive explanations instead.
- Rewriting the full lease-lifecycle mutation UI.
- Implementing visually unproved Tenant, Technician, Owner, token, or Superadmin states beyond safe source-backed copy/control changes.
- Deploying over the user-owned persistent Azure preview.
- Broad design-token, shared-primitive, or theme replacement.

## Ownership

### Lane 1 — primary agent

Owns shell/navigation, Unit Command Center, Unit Money surface, and rental-spine links:

- `web/src/lib/components/AppShell.svelte`
- `web/src/lib/components/CommandCenterNav.svelte`
- `web/src/lib/components/shell-hierarchy.test.ts`
- `web/src/routes/(protected)/units/**`
- `web/src/lib/components/unit/**`
- `web/src/routes/(protected)/leases/+page.svelte`
- `web/src/routes/(protected)/properties/[id]/+page.svelte`
- `web/src/routes/(protected)/tenants/[id]/+page.svelte`
- direct Lane 1 tests

### Lane 2 — work/notifications agent

Owns only:

- `web/src/routes/(protected)/settings/notifications/**`
- `web/src/lib/components/notifications/**`
- notification settings tests

This implementation slice does not absorb broader Work or Portal findings.

### Lane 3 — money/intake/settings/help agent

Owns the Lane 3A file list in its discovery document. `PaymentDetail.svelte` is Lane 3-owned; `RentTab.svelte` and `LedgerTab.svelte` are Lane 1-owned. Lane 3 does not change Unit routing or notification files.

All agents work in the same TSK-747 worktree, do not revert one another, and run no builds or test suites. The primary agent performs serialized verification after all edits.

## Step 1 — Restore the Unit-centered shell

### Files

- `web/src/lib/components/CommandCenterNav.svelte`
- `web/src/lib/components/AppShell.svelte`
- `web/src/lib/components/shell-hierarchy.test.ts`

### Work

1. Restore the searchable Command Center control from the safe shape in commit `0bfdd6a6`.
2. Adapt visibility to current experience/capability policy: Management plus access to `/units`.
3. Query only while open, with `listWithHealthPage({ search, sort: 'propertyName', take: 20 })`.
4. Keep search server-side; do not materialize all rentals and filter in the browser.
5. Show property plus unit so repeated unit numbers are unambiguous.
6. Keep Rentals as the home for global lists; do not duplicate the Units list inside the picker.
7. Update route title from generic Rentals to Command Center for `/units/[id]`.

### Acceptance

- Expanded and collapsed shell both expose Command Center.
- Closed picker does not query.
- Search and paging are server-side.
- Current Unit is identified.
- “Browse all rentals” opens `/units`.

## Step 2 — Make the Unit workspace instant and progressive

### Files

- `web/src/routes/(protected)/units/[id]/+page.svelte`
- `web/src/lib/components/unit/unit-tabs.ts`
- `web/src/lib/components/unit/unit-history-contract.test.ts`
- `web/src/lib/components/unit/unit-tenant-lease-surface.test.ts`
- other directly failing Unit contract tests

### Work

1. Replace state-only `goto(...)` tab/view navigation with SvelteKit shallow `pushState`/`replaceState`.
2. Preserve URL authority, back/forward behavior, deep links, and contextual query parameters.
3. Add a quiet secondary tab row for Leasing, Tenant & lease, Maintenance, and Documents & history.
4. Render only the selected secondary view.
5. Update focus without scrolling the whole page or showing the global navigation loader.
6. Initial Unit entry may issue only the existing `unit-dashboard` aggregate plus at most two bounded background list requests. The allowed background families in this slice are `unit-inspections` (`take: 10`) and `unit-recurring-maintenance` (`take: 10`), because those are already eagerly requested by the current page. Do not add another eager family without measured evidence and plan amendment.
7. All other workflow lists load on first visit to their selected view, use their existing server-side page size, and remain in the TanStack Query cache for instant return visits.
8. Never prefetch Unit history, document/file content or previews, full result sets, dialog-only lookups, or `take` values above the existing page contract. Do not filter, join, group, sort, page, count, or aggregate these results in the browser.
9. A first visit to an intentionally lazy view may use a local panel loader, but it must never blank the Unit page or trigger the global navigation loader.
10. Keep cached data across tab/view changes and do not refetch merely because the URL selection changed.
11. Keep all six primary destinations and all existing mutation/dialog behavior.

### Acceptance

- No Unit tab/view switch enters `$app/state.navigating`.
- Header and cached dashboard data remain stable.
- Only one child workflow is visible.
- Prefetched first-page data is reused; any intentionally lazy view loads only inside its panel.
- Network proof shows at most the dashboard plus the two allowed `take: 10` background families on initial entry, with no history/document-body request.
- Source/contract proof shows no client-side filtering, aggregation, sorting, or paging.
- Back/forward returns to the prior Unit destination.
- A focused narrow-width check shows usable, horizontally scrollable tab rows without page overflow.

## Step 3 — Restore unit-owned rental-spine links

### Files

- `web/src/routes/(protected)/leases/+page.svelte`
- `web/src/routes/(protected)/properties/[id]/+page.svelte`
- `web/src/routes/(protected)/tenants/[id]/+page.svelte`
- `web/src/lib/navigation/record-href.ts` only if the current contract is insufficient
- direct tests

### Work

1. Route Unit-owned lease rows through `recordHref('leaseManagement', ...)`.
2. Keep `/leases/[id]` only as the fallback when Unit context is genuinely absent.
3. Rewrite list headings, filters, columns, empty states, and descriptions in plain English.
4. Do not alter lifecycle values sent to the API.

### Acceptance

- Lease grid, Property lease rows, and Tenant lease rows open:
  `/units/:unitId?tab=tenant-lease&view=agreements&leaseManagement=:id`.
- Unitless records retain the generic detail fallback.
- No “tenant relationship,” “governing agreement,” or “account context” appears in the changed list UI.

## Step 4 — Repair Unit summary, header, and Money

### Files

- `web/src/lib/components/unit/UnitHeader.svelte`
- `web/src/lib/components/unit/tabs/OverviewTab.svelte`
- `web/src/lib/components/unit/tabs/LedgerTab.svelte`
- `web/src/lib/components/unit/tabs/RentTab.svelte`
- `web/src/lib/components/unit/tabs/ExpensesTab.svelte` only if needed for symmetric folded-detail routing
- focused display/routing tests

### Work

1. Reduce the gradient hero and health-pill pile to a calm identity header with essential rent, lease-date, repair, and document signals.
2. Use `formatStatusLabel` or explicit domain labels so raw enum tokens never render.
3. Rename Unit condition families in plain language.
4. In Money, show a secondary choice: **Rent & payments** | **Property expenses**, one at a time.
5. Use shallow routing for Money views and folded payment/expense detail.
6. Make PaymentReceipt rows in account activity and the receipt list open `PaymentDetail`.
7. Present receipts before lower-priority charge/deposit diagnostics.
8. Replace:
   - “Charge allocation and open amount” with **Rent and charges**
   - “allocated” with **paid**
   - “open” with **still owed** or **Paid in full**
9. Do not change financial calculations or allocation behavior.

Lane 1 owns the routing contract and may change `recordHref('payment')` so a payment with Unit context resolves to Unit Money before the generic tenant-account route. The generic tenant-account detail remains the fallback when Unit context is absent. Lane 3 changes only the ordinary-language presentation inside `PaymentDetail.svelte`; it does not change that component’s inputs, queries, correction command, or routing.

### Acceptance

- Payment and expense detail are both reachable inside Unit Money.
- User-provided example reads as paid/still owed with no accounting-engine terminology.
- No `TenantAccount`, `PaymentReceipt`, `LeaseAgreement`, `NotAvailable`, `NoticeOpen`, or raw `InProgress` appears in changed Unit UI.
- Financial amounts remain identical to the API values.

## Step 5 — Create the notification setup journey

### Files

- `web/src/routes/(protected)/settings/notifications/my-alerts/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/team-routing/+page.svelte`
- `web/src/routes/(protected)/settings/notifications/tenant-notices/+page.svelte`
- `web/src/lib/components/notifications/NotificationHelpAction.svelte`
- new Lane 2 notification journey/accordion components
- direct notification tests

### Work

1. Preserve the three canonical routes and independent save endpoints.
2. Add one shared route-backed journey:
   - Step 1: **How should Rental Command reach you?**
   - Step 2: **Who should handle each kind of work?**
   - Step 3: **Which tenant messages should Rental Command prepare?**
3. Give each step Back/Next actions, saved summary, and the same Help action.
4. Rename personal channels in plain language.
5. Collapse team responsibilities and tenant policies; only one editor opens at a time.
6. Replace numeric send-hour copy with a human time label/control.
7. Move provider/retry/version details under Advanced or Technical details.
8. Keep non-admin access truthful and safe.

### Acceptance

- Current independent persistence and permission behavior is unchanged.
- Tenant policies are collapsed by default and only one is open.
- Help slugs resolve.
- Keyboard, focus return, unsaved-change protection, and `390 × 844` containment are proved.

## Step 6 — Implement Lane 3A

### Production files

- `web/src/lib/accounting/money-display.ts` (new)
- `web/src/routes/(protected)/accounting/+page.svelte`
- `web/src/routes/(protected)/accounting/year-end/+page.svelte`
- `web/src/routes/(protected)/banking/+page.svelte`
- `web/src/routes/(protected)/deposits/+page.svelte`
- `web/src/routes/(protected)/deposits/[id]/+page.svelte`
- `web/src/routes/(protected)/tax/+page.svelte`
- `web/src/routes/(protected)/reports/+page.svelte`
- `web/src/routes/(protected)/reports/[report]/+page.svelte`
- `web/src/lib/components/records/ExpenseDetail.svelte`
- `web/src/lib/components/records/PaymentDetail.svelte`
- `web/src/routes/(protected)/settings/integrations/ai/+page.svelte`
- `RentalCommand.Api/KnowledgeBase/security-deposits.md`

### Test files

- `web/src/lib/accounting/money-display.test.ts` (new)
- `web/src/lib/accounting/expense-receipt-display.test.ts`
- `web/src/lib/accounting/report-ledger-preview.test.ts`
- direct existing Year End, AI provider, and Knowledge Base contract tests

Lane 3A explicitly excludes Scan, Import, public application/signing, Admin, the Settings landing page, notification settings, shell/navigation, and Unit components.

### Work

1. Add presentation-only money labels and apply them on the audited money surfaces.
2. Move genuinely needed identifiers under **Technical details**.
3. Replace native selects on Year End and AI Provider with the existing Select component.
4. Replace Plaid environment/secret copy with an availability state and next action.
5. Add route-level Help links.
6. Correct the Security Deposits article to match the current live workflow.
7. In `PaymentDetail.svelte`, replace append-only/allocation/refund jargon in ordinary copy while preserving correction semantics and audit data.

### Acceptance

- No raw demo IDs or raw money-entry enums on changed customer-facing screens.
- Banking exposes no environment or secret-key names.
- Year End and AI Provider have no native select.
- Help text matches live buttons and route sequence.
- Loading/error/mutation behavior is unchanged.

## Step 7 — Review and serialized verification

### Static and focused tests

Run one process at a time:

1. `git diff --check`
2. Focused Node tests for shell, Unit routing/history, Unit Money, notification settings, Lane 3 money labels, and Help content.
3. `pnpm --dir web check:native`
4. `pnpm --dir web check`

If resource use makes the full checks impractical locally, use the canonical remote-verification handoff. Do not run parallel builds/tests.

### Browser proof

Use a temporary local or approved verification stack, not the persistent Azure preview:

1. Confirm actual `1920 × 1080`.
2. Command Center: open, search, choose a rental, browse all.
3. Unit: switch every primary and secondary view; confirm no global navigation loader/full-page refresh.
4. Lease grid → Unit lease view.
5. Money: payment row → folded detail; expense row → folded detail; paid/still-owed copy.
6. Notification Steps 1–3, save summaries, one policy editor.
7. Lane 3A representative money, Banking, Year End, AI Provider, and Security Deposits Help.
8. Focused `390 × 844` proof for tab containment and tenant-notice policy editor.

### Completion rules

- Do not call the task complete on route rendering alone.
- Any role/token/data state not proved remains explicitly marked source-only or blocked.
- Do not deploy or publish without separate authorization.
- Keep the dirty/unmerged worktree and report its exact state; never remove it silently.
