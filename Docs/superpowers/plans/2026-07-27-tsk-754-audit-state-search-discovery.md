# TSK-754 Activity History State and Search Discovery Brief

**Goal:** Activity History preserves the user's exact forensic list position across record navigation, and search finds a work-order audit row by the work order title the user is investigating.

## Acceptance criteria

- Search, operation, entity, date range, and page survive opening an audit target and using browser Back.
- With `Created` and `WorkOrder` filters active, searching `Yard cleanup` returns the audit row for that titled work order.
- Filtering, searching, sorting, paging, actor resolution, authorization, and target-title matching remain in one PostgreSQL-translated statement.

## Explicit non-goals

- No audit write-path, audit description, authorization, export, admin-forensic UI, or unrelated grid changes.
- No broader entity-title search expansion beyond the reproduced WorkOrder defect.

## Discovery questions

1. Which existing route owns Activity History filter and navigation state?
2. Which existing query owns audit search and SQL projection?
3. Which focused tests prove URL restoration and one-command PostgreSQL title search?

## Bounded investigation

- Inspect: `web/src/routes/(protected)/audit`, `web/src/lib/utils/grid-url-state.svelte.ts`, `RentalCommand.Api/Services/Domain/AuditQueryService.cs`, and focused audit tests.
- Commands allowed: `rg`, `sed`, focused Node unit tests, focused PostgreSQL integration tests, `ToQueryString`.
- Do not: edit outside the named audit UI/query/test paths or fix adjacent findings.

## Stop condition

Discovery ends when the exact URL-state integration, SQL predicate, focused tests, and proof flow are known.

## Discovery evidence

- `web/src/routes/(protected)/audit/+page.svelte` initializes every filter and `skip` to empty/zero and resets `skip` on the initial effect, so browser Back cannot restore the prior forensic position.
- `web/src/lib/utils/grid-url-state.svelte.ts` is the established state-to-URL contract used by accounting, owners, leases, and maintenance grids.
- `RentalCommand.Api/Services/Domain/AuditQueryService.cs` applies search before `OrderBy`/`Skip`/`Take`, then projects actor and target route context in one `IQueryable`.
- Existing `ApplySearch` matches audit columns and operation aliases but does not search `WorkOrder.Title`.
- `RentalCommand.IntegrationTests/AuditSearchTests.cs` already provides real-PostgreSQL result and one-reader-command proof, making it the focused backend test owner.

