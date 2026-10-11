# TSK-747 Cross-Lane Plan Review

**Verdict:** `APPROVE_WITH_REQUIRED_CHANGES`

The plan is directionally sound and feasible, but implementation must not start until four boundaries are tightened.

## Required changes

1. **Inline the exact Lane 3A file boundary.** The execution plan must not point ambiguously at the discovery document’s broader Scan, Import, public application, admin, and settings inventory.
2. **Define payment-detail ownership.** Lane 1 owns Unit Money routing and `RentTab.svelte`/`LedgerTab.svelte`; Lane 3 owns `PaymentDetail.svelte` copy. The plan must decide whether `recordHref('payment')` changes.
3. **Make Unit prefetch limits testable.** Name the allowed prefetched query families, page sizes, and request budget. Prove there is no unbounded history/document fetch, client-side filtering, or client-side aggregation.
4. **Correct the Lane 3 shell path guard.** The actual files are `web/src/lib/components/AppShell.svelte` and `web/src/lib/components/CommandCenterNav.svelte`, not paths under a `layout` folder.

## Evidence

- `CommandCenterNav.svelte` is currently only a legacy Rentals link.
- Unit tabs and folded details call `goto(...)`.
- Unit destinations stack sibling workflows.
- Lease rows bypass `recordHref`.
- Unit Money exposes allocation/open-amount language and does not make account-activity payments interactive.
- The notification and Help findings are supported by the lane audits.

## Optional suggestions

- Keep browser performance acceptance focused on user-visible behavior: no global navigation loader, stable header, cached data reuse, and a local panel loader only for intentionally lazy work.
- Preserve legal/accounting distinctions in Help or Technical details while removing internal entity and enum names from ordinary UI.
