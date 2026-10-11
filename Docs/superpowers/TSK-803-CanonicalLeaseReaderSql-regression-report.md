# Canonical lease reader SQL regression report

## Outcome

**Verified.** The failing test was a stale assertion that pre-dated the accounting lane. It was not an authorization regression and it was not a capture-index shift: the base commit `bbf0eea6` reproduces the failure, the test builds two SQL strings directly with `ToQueryString()`, and both executed statements contain the `MembershipRoleAssignments` authorization source.

**Verified.** The fix changes only `RentalCommand.Api.Tests/Domain/CanonicalLeaseReaderSqlTests.cs`. The scan-originated activity assertion now requires `MembershipRoleAssignments` for the dashboard query, while the existing helper default continues to reject that table for the other canonical-property readers that do not need the all-property assignment branch.

**Verified.** No production query construction changed. The current worktree has unrelated in-flight controller/test edits from the coordinator; those files were not staged or changed by this task.

## Root-cause evidence

**Verified — exact reproduction.** The required pre-fix command was run on the current branch:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~Scan_originated_tenant_money_activity" --no-restore -v:minimal
```

It failed at `AssertCanonicalPropertyAuthorization` because the old assertion called `NotContain("MembershipRoleAssignments")` while the generated dashboard SQL did contain `MembershipRoleAssignments`.

**Verified — base commit check.** The same focused test was run in an isolated worktree at `bbf0eea6` (`Correct accounting handoff branch point`) with the same command and failed with the same `NotContain("MembershipRoleAssignments")` assertion. Therefore no Lane A/B1/B2 accounting commit introduced this failure.

**Verified — commit/source history.** `git log -S'MembershipRoleAssignments' -- RentalCommand.Api.Tests/Domain/CanonicalLeaseReaderSqlTests.cs` identifies the assertion's original introduction in `5c7a1c60`, not an accounting-lane commit. `git diff bbf0eea6..HEAD` for the test, `DashboardService.cs`, `AuditQueryService.cs`, `AuditAuthorizationQuery.cs`, and `WorkspaceAuthorizationQuery.cs` has no query-construction change. The current test's direct query construction is at `RentalCommand.Api.Tests/Domain/CanonicalLeaseReaderSqlTests.cs:744-752`; there is no captured-statement collection or positional index.

**Verified — authorization construction.** `AuditAuthorizationQuery.WhereAuthorizedForReports` creates `allPropertiesAssignments` through `db.AuthorizedAllPropertyAssignments` at `RentalCommand.Api/Services/Domain/AuditAuthorizationQuery.cs:23-30` and applies it in the owner/global authorization predicate at `:32-49`. `WorkspaceAuthorizationQuery.AuthorizedWorkspaceAssignments` reads `db.MembershipRoleAssignments` and correlates it to the canonical effective-capability function at `RentalCommand.Data/Authorization/WorkspaceAuthorizationQuery.cs:55-75`, including the direct table source at `:68`. The generated table reference is therefore an expected cross-tenant authorization guard.

## Full SQL capture inventory

**Verified.** The test operation makes exactly two direct `ToQueryString()` calls and therefore captures exactly these two statements; no third statement is selected by index:

1. **`dashboardSql`** — constructed at `RentalCommand.Api.Tests/Domain/CanonicalLeaseReaderSqlTests.cs:744-745` from `BuildRecentActivityProjectionQuery(ReadScope())`. The captured SQL is one `SELECT` rooted at `AtomicAuditLogs`, 1,188 lines and 51,864 bytes, with `MembershipRoleAssignments` at generated line 591, `public.rc_api_effective_capability_scopes` calls, and final `ORDER BY a."Timestamp" DESC, a."Id" DESC` / `LIMIT @p790` at lines 1,188-1,189. SHA-256: `338c5b2a6e455bb00d25c601cc018876171ec5d407d5be87b68154af3024c1af`.
2. **`auditSql`** — constructed at `RentalCommand.Api.Tests/Domain/CanonicalLeaseReaderSqlTests.cs:746-752` from `BuildPageProjectionQuery(...)`. The captured SQL is one `SELECT` rooted at `AtomicAuditLogs`, 860 lines and 36,807 bytes, with `MembershipRoleAssignments` at generated line 263, `public.rc_api_effective_capability_scopes` calls, and final `ORDER BY a."Timestamp" DESC, a."Id" DESC` / `LIMIT @p810 OFFSET @p800` at lines 860-861. SHA-256: `82c2e5f5399273ebfbd19077880c17d2419eb3dbc16e28fefa43b4f776e8bec0`.

**Verified.** The complete captured statements were written temporarily to `/tmp/tsk803-dashboard-sql.sql` and `/tmp/tsk803-audit-sql.sql` while diagnosing, then the temporary test instrumentation was removed. The inventory and content checks were produced with:

```text
wc -l -c /tmp/tsk803-dashboard-sql.sql /tmp/tsk803-audit-sql.sql
  1188  51864 /tmp/tsk803-dashboard-sql.sql
   860  36807 /tmp/tsk803-audit-sql.sql

rg -n 'FROM "MembershipRoleAssignments"|rc_api_effective_capability_scopes|^SELECT |ORDER BY|LIMIT .*OFFSET|LIMIT ' /tmp/tsk803-dashboard-sql.sql /tmp/tsk803-audit-sql.sql
```

**Verified.** The actual failure text described the old negative assertion failing because the table was present; it did not show a missing table. The controller's hypothesis wording (“no longer contains”) does not match the observed assertion/output, and the base reproduction confirms the test expectation was stale.

## Fix

**Verified.** `CanonicalLeaseReaderSqlTests` now documents and asserts the expected all-property guard for this scan-originated activity query at `:764-768`:

```csharp
// This reader also carries the all-property assignment guard for unsupported/global audit rows.
AssertCanonicalPropertyAuthorization(
    dashboardSql,
    expectedCallCount: null,
    expectMembershipRoleAssignments: true);
```

**Verified.** The helper option is defined at `:784-787`; when enabled it asserts `MembershipRoleAssignments` at `:830-833`, and otherwise retains the prior `NotContain` assertion at `:834-837`. The existing `MembershipRoleAssignmentProperties`, client-evaluation, canonical-function, and single-statement checks remain unchanged.

## Verification receipts

**Verified.** In an isolated clean validation worktree based on `e91b4b55`, after applying this test-only patch and restoring its untracked NuGet assets, the required class passed:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~CanonicalLeaseReaderSqlTests" --no-restore -v:minimal
Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27
```

**Verified.** Focused accounting suites ran serially in that same clean validation worktree and passed:

```text
FullyQualifiedName~AccountingConversionPostgreSqlTests       Passed:  2, Failed: 0
FullyQualifiedName~AccountingReadModelPostgreSqlTests        Passed:  3, Failed: 0
FullyQualifiedName~AccountingSourcePostingPostgreSqlTests    Passed: 14, Failed: 0
FullyQualifiedName~RecurringTenantChargeWorkerPostgreSqlTests Passed: 1, Failed: 0
FullyQualifiedName~AccountingApiContractFreezeTests           Passed:  3, Failed: 0
FullyQualifiedName~AccountingFoundationPostgreSqlTests        Passed: 12, Failed: 0
```

**Verified.** Each command used `MSBUILDDISABLENODEREUSE=1`, `--no-restore`, and a single filter; no parallel build or test ran. The temporary validation worktree was removed after these checks and was not part of the branch deliverable.

## Commit and remaining state

**Verified.** This task's commit is recorded in the worker handoff after committing the two task files. No push was performed.

**Verified.** The coordinator's concurrent edits remain outside this commit: `RentalCommand.Api.Tests/Domain/AccountingSourcePostingPostgreSqlTests.cs`, `RentalCommand.Api.Tests/Domain/ScheduledTenantChargePostgreSqlTests.cs`, `RentalCommand.Api.Tests/Domain/TenantReceiptSimulationClockTests.cs`, `RentalCommand.Data/Accounting/AccountingConversionService.cs`, `RentalCommand.Data/Accounting/AccountingPostingService.cs`, `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs`, `RentalCommand.Data/Payments/ScheduledTenantChargeCommandHandler.cs`, `RentalCommand.Data/Payments/TenantAccountingPosting.cs`, and `RentalCommand.IntegrationTests/NativeEsignDepositChargePostgreSqlTests.cs`. They were preserved and are not part of this regression fix.

**Assumed.** The coordinator will continue those concurrent edits and run the full branch suite after their product changes are complete; this dispatch's isolated validation proves the regression fix and the focused accounting suites against the approved pre-existing source state.
