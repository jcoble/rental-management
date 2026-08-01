# TSK-754 Activity History State and Search Execution Plan

**Goal:** Restore Activity History forensic position on Back and find WorkOrder audit rows by target title without leaving PostgreSQL.
**Source brief:** `Docs/superpowers/plans/2026-07-27-tsk-754-audit-state-search-discovery.md`
**Active goal:** TSK-754 / YS-134 and YS-145
**Plan state:** Implementation verified; live UI proof pending

## Contract

### Acceptance criteria

- AC-1: Search, operation, entity, date range, and page are restored exactly after opening an audit target and using browser Back.
- AC-2: `Created` + `WorkOrder` + `Yard cleanup` returns the matching row in one translated, paged PostgreSQL reader command.

### Explicit non-goals

- Audit writes, descriptions, authorization rules, exports, admin-forensic UI, other entity-title searches, and unrelated list screens.

### Deferred items

- None.

### UI proof

- UI impact: Yes — Activity History navigation and search results change.
- Supported scenario: Filter Activity History, open a work-order target, use browser Back, then search its title.
- Required target: Existing Rental Command desktop browser session owned by the coordinator.
- Proof artifacts: Coordinator's TSK-754 evidence directory; this fixer will supply code/test evidence without opening a second browser.

## Task 1 — Persist Activity History forensic position

**Status:** Complete
**Allowed files:**
- Modify: `web/src/routes/(protected)/audit/+page.svelte`
- Test: `web/src/lib/audit/activity-history-url-state.test.ts`

**Acceptance:** AC-1

1. [x] Add a focused contract test for URL-seeded search/action/entity/date/page state and initial-page preservation.
2. [x] Reuse the established grid URL-state utility and convert page offset to a URL page number.
3. [x] Run: `node --test --experimental-strip-types src/lib/audit/activity-history-url-state.test.ts`; result: 3 passed.
4. [x] Relevance review: `RELEVANCE PASS`.

## Task 2 — Search WorkOrder audit targets by title in PostgreSQL

**Status:** Complete
**Allowed files:**
- Modify: `RentalCommand.Api/Services/Domain/AuditQueryService.cs`
- Test: `RentalCommand.IntegrationTests/AuditSearchTests.cs`

**Acceptance:** AC-2

1. [x] Extend the seeded WorkOrder title and assert a title-only search result under Created/WorkOrder filters.
2. [x] Add only the correlated, portfolio-scoped WorkOrder-title predicate to the existing `IQueryable` search.
3. [x] Run: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter FullyQualifiedName~AuditSearchTests --no-restore`; result: 3 passed, including generated SQL and one-reader-command proof.
4. [x] Relevance review: `RELEVANCE PASS`.

## Completion gate

- [x] Every changed file maps to a task above.
- [x] Every implementation acceptance criterion has fresh automated evidence.
- [x] No deferred item was implemented.
- [x] Final relevance review returned `RELEVANCE PASS`.
- [ ] Coordinator obtains `UI PROOF PASS` on the original browser reproduction before closing TSK-754 defects.
