# TSK-702 receipt

## Fix round 2

This receipt proves that the seven RentalCommand.Data.Tests failures and three RentalCommand.Api.Tests failures pre-existed the remediation commit at merge-base `efda29099278cbd162e15dd393a62c78e9ce6614`.

### Reproducible setup

The clean baseline was created and later removed with the exact requested commands:

```text
git worktree add /Users/blackcolours/dev/work/worktrees/rental-management/tsk-702-baseline efda2909
git worktree remove /Users/blackcolours/dev/work/worktrees/rental-management/tsk-702-baseline
git worktree prune
```

Baseline HEAD was `efda29099278cbd162e15dd393a62c78e9ce6614` with clean status. The branch HEAD was `4d050c187d25ec9972297bdb3746f2a4484ef837`. Both used .NET SDK `10.0.302`, `DOTNET_CLI_USE_MSBUILD_SERVER=1`, and `MSBUILDDISABLENODEREUSE=1`; tests used `Tier!=3-Slow&Legacy!=PendingAudit&Legacy!=Archived`. Commands were run serially, never concurrently.

Baseline preparation:

```text
MSBUILDDISABLENODEREUSE=1 dotnet restore RentalCommand.sln                         # exit 0
MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln --no-restore              # exit 0; 66 warnings, 0 errors
```

The branch build used the same build command and exited 0 with 60 warnings and 0 errors. The warning-count difference is diagnostic-only; both builds succeeded.

### Exact test commands

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~EmailTransportSelectionTests' --logger 'console;verbosity=minimal'
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Core.Tests/RentalCommand.Core.Tests.csproj --no-build --no-restore --logger 'console;verbosity=minimal'
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Data.Tests/RentalCommand.Data.Tests.csproj --no-build --no-restore --logger 'console;verbosity=minimal'
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-build --no-restore --logger 'console;verbosity=minimal'
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --no-build --no-restore --logger 'console;verbosity=minimal'
```

### Merge-base totals and exact failing names

```text
Engine focused: exit 0 — Passed 12, Failed 0, Skipped 0, Total 12
Core:           exit 0 — Passed 80, Failed 0, Skipped 0, Total 80
Data:           exit 1 — Passed 174, Failed 7, Skipped 0, Total 181
API:            exit 1 — Passed 1345, Failed 3, Skipped 0, Total 1348
Engine full:    exit 0 — Passed 95, Failed 0, Skipped 0, Total 95
```

Data.Tests:

```text
RentalCommand.Data.Tests.FoundationBaselinePostgreSqlTests.RlsClassification_CoversEveryMappedBaseTableExactlyOnce
RentalCommand.Data.Tests.FoundationBaselinePostgreSqlTests.SandboxGraduation_ClassifiesEveryPortfolioScopedTableExactlyOnce
RentalCommand.Data.Tests.PortfolioVisibilityQueryFilterTests.Document_template_fields_are_directly_scoped_and_cannot_cross_portfolios
RentalCommand.Data.Tests.PortfolioVisibilityQueryFilterTests.Existing_soft_delete_filters_are_composed_with_portfolio_visibility
RentalCommand.Data.Tests.PortfolioVisibilityQueryFilterTests.Immutable_finance_and_legal_history_is_not_filtered_by_lifecycle_state
RentalCommand.Data.Tests.PortfolioVisibilityQueryFilterTests.Representative_filter_chains_translate_through_the_npgsql_provider
RentalCommand.Data.Tests.PortfolioVisibilityQueryFilterTests.Required_relationships_below_filtered_principals_have_transitive_filters
```

Api.Tests:

```text
RentalCommand.Api.Tests.Domain.ExpenseServiceTests.AtomicMoneyMutationRejectsAMissingBusinessClockBeforeStartingAnAttempt
RentalCommand.Api.Tests.Domain.LegalDocumentIssuancePreparationServiceTests.Stale_draft_is_rejected_before_render_or_upload_admission
RentalCommand.Api.Tests.Scanning.ScanBatchControllerTests.UploadBatch_WithInvalidTarget_ReturnsBadRequest
```

### Fix round 2 comparison

The branch returned the same totals: Engine focused 12/0/12, Core 80/0/80, Data 174/7/181, API 1345/3/1348, and Engine full 95/0/95. Its Data.Tests failure set is exactly the seven merge-base names above, and its Api.Tests failure set is exactly the three merge-base names above. Sorted exact-name comparisons for both suites returned exit 0. Only report order and runtime differed (API 4 m 30 s baseline versus 7 m 43 s branch; Data 4 s versus 5 s), with no identity or count difference. No baseline-versus-branch difference remains unexplained.

`dotnet build-server shutdown` exited 0 after each suite batch. The baseline worktree was removed as requested:

```text
Worktree cleanup: removed /Users/blackcolours/dev/work/worktrees/rental-management/tsk-702-baseline
```

No push, pull request, or merge was performed.
