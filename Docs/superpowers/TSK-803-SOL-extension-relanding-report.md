# TSK-803 SOL extension re-landing report

## Outcome

**verified.** The four preserved SOL extension commits were reviewed and re-landed as four clean commits on the accounting branch. The broken journal-line transaction trigger was corrected, targeted deposit receipt overages are rejected before any row is written, scheduled tenant charges use bounded batch posting, and owner contributions use the approved additive API and posting flow.

**verified.** The final solution build reported zero errors, the four accounting PostgreSQL suites passed, the route-contract filter passed 29/29, and the focused owner-contribution test passed. Exact commands and outputs are recorded below.

**verified.** The preserved sol-accounting-hardening-attempt branch remains present. This lane did not delete it, touch web/ or mcp/, or touch the TSK-754 worktree or database.

## Commits

The following commits are **verified** with git log --oneline:

1. 9acdcaa6 — Harden accounting posting invariants.
2. b6611696 — Reject targeted deposit receipt overages.
3. b2160cf8 — Post batched tenant charge journals.
4. 2c646bd6 — Add owner contribution posting flow.
5. The report commit is the commit that adds this file; its hash is recorded in the final worker handoff because a report cannot contain its own hash before that commit exists.

The source commits preserved for review were 9fbe4d8d, 23cc1fb4, 5f4a3f83, and 441450cb. The branch was at 4d910828 before this re-landing. **verified** from commit headers and branch history.

## 1. Journal-line trigger diagnosis

### Failure and root cause

**verified.** The trigger first landed in preserved commit 9fbe4d8d at RentalCommand.Data/Accounting/AccountingLedgerPostgreSql.cs:293-296 with this predicate:

    IF parent_xmin <> pg_current_xact_id() THEN
        RAISE EXCEPTION 'Journal lines must be inserted in the journal entry transaction.';
    END IF;

The legitimate posting path inserts a journal entry and its lines through EF Core SaveChangesAsync. EF creates a savepoint for the save operation; PostgreSQL therefore stores the entry row's xmin as a subtransaction xid while pg_current_xact_id() returns the enclosing top-level transaction xid. Values captured during diagnosis included debug parent xmin 793, current xid 791, txid 791, with the same pattern repeated as 804/802 and 810/808. The values differed even though lines were inserted before the owning transaction committed.

**verified.** The diagnosis was reproduced in disposable worktree /Users/blackcolours/dev/work/worktrees/rental-management/tsk-803-trigger-diagnosis-23cc at preserved commit 23cc1fb4. This command failed 5 of 14 foundation tests with Npgsql.PostgresException 55000: Journal lines must be inserted in the journal entry transaction.

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~AccountingFoundationPostgreSqlTests" --no-restore -v:minimal
    Failed: 5, Passed: 9, Total: 14
    Npgsql.PostgresException 55000: Journal lines must be inserted in the journal entry transaction.

The disposable worktrees were removed with git worktree remove --force and git worktree prune after diagnosis. **verified** by the worktree cleanup commands; no user worktree was removed.

### Decision and corrected invariant

**verified.** The invariant is valuable and was retained. The deferred balance trigger protects balance at transaction commit, and the immutability triggers protect updates and deletes, but neither prevents a later transaction from adding a new balanced pair of lines to an already committed journal entry. A late insert would change historical accounting without changing the entry's immutable facts.

**verified.** The failing test was written before the corrected product code. At RentalCommand.Api.Tests/Domain/AccountingFoundationPostgreSqlTests.cs:244-270, CommittedJournal_CannotReceiveLateBalancedLines commits a complete entry, then bypasses the application service with a direct SQL insert of a balanced pair and expects the PostgreSQL exception. At base 4d910828, the focused test was red because the insert succeeded. After the trigger correction it was green:

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~CommittedJournal_CannotReceiveLateBalancedLines" --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1

The corrected function at RentalCommand.Data/Accounting/AccountingLedgerPostgreSql.cs:277-306 checks the parent row's transaction status instead:

    -- A journal row written through an EF savepoint can have a subtransaction xmin.
    -- Its transaction status stays in progress until the owning transaction commits.
    IF pg_xact_status(parent_xmin) IS DISTINCT FROM 'in progress' THEN
        RAISE EXCEPTION 'Journal lines must be inserted in the journal entry transaction.'
            USING ERRCODE = '55000';
    END IF;

This accepts the savepoint subtransaction used by the legitimate flow while rejecting a line insert after the entry transaction has committed. The migration and trigger replacement are in commit 9acdcaa6; its migration is RentalCommand.Data/Migrations/20260801110140_HardenAccountingPostingInvariants.cs.

## 2. Targeted deposit receipt overages

**verified.** The preserved 23cc1fb4 guard was re-landed as b6611696. The guard is at RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:81-94: when a receipt targets a DepositCharge, the command amount must not exceed that charge's open amount. It raises the plain message A targeted deposit receipt cannot exceed the deposit target open amount. before payment, allocation, or journal rows are written. The deposit journal continues to debit trust cash and credit tenant receivable and the test asserts no income account is used at RentalCommand.Api.Tests/Domain/TenantReceiptSimulationClockTests.cs:548-562.

The failing test was staged before restoring the guard and ran red because the overage command completed; the restored implementation then passed the same test:

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~TargetedDepositReceipt_RejectsAmountAboveTheDepositOpenAmount" \
      --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1

The test also verifies that neither the tenant ledger row nor the journal row exists after rejection at TenantReceiptSimulationClockTests.cs:603-607.

## 3. Batched tenant-charge journals

**verified.** Commit b2160cf8 re-landed the bounded batch path from 5f4a3f83. The shared implementation is RentalCommand.Data/Payments/TenantAccountingPosting.cs:40-110: it performs one portfolio-scoped system-account query for receivable and income mappings, builds proposals in the shared deterministic line order, and calls AccountingPostingService.PostBatchAsync once. The scheduled command handler uses this helper; the initial native e-sign deposit-charge path uses the corresponding bounded helper at TenantAccountingPosting.cs:118-145.

The test-first proof is RentalCommand.Api.Tests/Domain/ScheduledTenantChargePostgreSqlTests.cs:284-313. With the production batch path temporarily restored to the base implementation, the test was red: the read counter reported ReadCount 80 for twenty occurrences while the bound is three. With the batch implementation restored, the focused test passed and the counter stayed within the bound:

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~RentBatch_PostsTwentyOccurrencesWithBoundedJournalPostingReads" \
      --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1

## 4. Owner contributions: approval gate and implementation

### Approval gate

**verified.** Before committing the additive controller and DTO surface, one orchestration approval question listed routes, response fields, request fields, authorization, paging, and string-enum requirements. The coordinator approved the schema with these binding corrections:

1. Writes use the existing MoneyDisbursementsManage capability-policy pattern used by owner distributions; reads use TryReadManagementScope.
2. The page route returns exactly AccountingPage<OwnerContributionResponse> with items, totalCount, skip, and take; no bespoke list envelope is used.

The approved posting rule is debit Operating Cash and credit owner-specific contribution equity with the owner dimension. No contribution amount is posted to an income account.

### Approved routes

All routes are portfolio-scoped through ManagementControllerBase and TryReadManagementScope; the URL contains no portfolio id. JSON enums use string names through the existing application serializer.

    GET    /api/v1/owner-contributions
    GET    /api/v1/owner-contributions/page
    GET    /api/v1/owner-contributions/{id}
    POST   /api/v1/owner-contributions
    PATCH  /api/v1/owner-contributions/{id}
    POST   /api/v1/owner-contributions/{id}/approve
    POST   /api/v1/owner-contributions/{id}/reject
    DELETE /api/v1/owner-contributions/{id}

The controller is at RentalCommand.Api/Controllers/OwnerContributionController.cs:11-170. Writes require Idempotency-Key and the approved existing capability policy at lines 53-55, 76-78, 100-102, 124-126, and 148-151. Reads call TryReadManagementScope at lines 27-28, 37-38, and 46-47.

### Approved DTO schema

**verified.** The implementation matches the approved schema at RentalCommand.Api/DTOs/OwnerContributionDtos.cs:8-110.

    OwnerContributionResponse
      id: int
      portfolioId: int
      ownerEntityId: int
      ownerName: string
      propertyId: int?
      propertyName: string?
      date: DateTime
      amount: decimal
      method: DistributionMethod (string enum)
      status: OwnerDistributionStatus (string enum)
      approvedAt: DateTime?
      approvedBusinessDate: DateTime?
      approvedByUserId: int?
      rejectedAt: DateTime?
      rejectedByUserId: int?
      rejectionReason: string?
      bankReference: string?
      exportReference: string?
      exportedAt: DateTime?
      memo: string?
      createdAt: DateTime
      updatedAt: DateTime

    OwnerContributionListQuery : ListQuery
      ownerEntityId: int?
      propertyId: int?
      year: int?
      status: OwnerDistributionStatus? (string enum)
      inherited ListQuery fields: skip, take, search, sort, from, to

    CreateOwnerContributionRequest
      ownerEntityId: int
      propertyId: int?
      date: DateTime
      amount: decimal
      method: DistributionMethod (string enum)
      memo: string?

    UpdateOwnerContributionRequest
      ownerEntityId: int?
      propertyId: int?
      clearProperty: bool?
      date: DateTime?
      amount: decimal?
      method: DistributionMethod? (string enum)
      memo: string?

    ApproveOwnerContributionRequest
      bankReference: string
      exportReference: string
      exportedAt: DateTime?

    RejectOwnerContributionRequest
      reason: string?

The page method returns the approved envelope at RentalCommand.Api/Controllers/OwnerContributionController.cs:31-39; the service constructs AccountingPage<OwnerContributionResponse> at RentalCommand.Api/Services/Domain/OwnerContributionService.cs:135-151. Filtering, count, sorting, and paging stay in EF-translated SQL at OwnerContributionService.cs:135-207; response projection is SQL-translated at lines 210-236.

### Entity, atomic flow, posting, and migration

**verified.** The entity is RentalCommand.Core/Entities/OwnerContribution.cs:6-37 and is portfolio-scoped with owner, optional property, amount, method, approval, rejection, export, audit, and deletion fields. The atomic approval path calls MoneyAccountingPosting.PostOwnerContributionAsync at RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:1113-1121.

**verified.** The posting implementation at RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:610-645 selects configured operating-cash and owner-contributions system mappings, emits exactly two ordered lines, and sets OwnerEntityId on both dimensions. The focused real-PostgreSQL test at RentalCommand.Api.Tests/Domain/AccountingSourcePostingPostgreSqlTests.cs:568-616 asserts:

    debit  operating-cash       2000 USD, OwnerEntityId = owner
    credit owner-contributions  2000 USD, OwnerEntityId = owner

**verified.** The migration command was:

    MSBUILDDISABLENODEREUSE=1 dotnet ef migrations add AddOwnerContributions \
      --project RentalCommand.Data --startup-project RentalCommand.Api
    Build succeeded.
    Done. To undo this action, use 'ef migrations remove'

The generated migration is RentalCommand.Data/Migrations/20260801111942_AddOwnerContributions.cs:10-135. It creates the table, indexes, PostgreSQL row-level security policies, and runtime grants. RLS is enabled and forced at lines 107-110; portfolio policies use rc_api_scope_allows at lines 111-117; the sandbox-graduation delete policy uses rc_sandbox_graduation_allows at lines 118-120; API and Engine grants are at lines 121-126. Existing baseline cleanup/grant inventories include the table at RentalCommand.Data/FoundationBaselinePostgreSql.cs:430-456, :488-513, and :556-577.

The table was deliberately not added to the mutable DirectPortfolioTables initial-create list: that list runs before this new migration during the fixture's baseline RLS setup. Adding it there caused 42P01 relation "OwnerContributions" does not exist; removing it and adding the explicit post-migration RLS/grants above made the fixture and source suite pass. **verified** from the real-PostgreSQL fixture failure and subsequent passing run.

Dependency injection registers the owner contribution service at RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs:68-70.

## 5. Verification receipts

The following results are **verified** from final committed product code at 2c646bd6. Each accounting suite was run serially with MSBUILDDISABLENODEREUSE=1.

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~AccountingFoundationPostgreSqlTests" --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 3 s

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~AccountingSourcePostingPostgreSqlTests" --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 2 s

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~AccountingConversionPostgreSqlTests" --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 s

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~AccountingReadModelPostgreSqlTests" --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 780 ms

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~OwnerContributionPostsCashAndOwnerSpecificContributionEquity" \
      --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 490 ms

    MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
      --filter "FullyQualifiedName~RouteContract" --no-restore -v:minimal
    Passed! - Failed: 0, Passed: 29, Skipped: 0, Total: 29, Duration: 1 s

    MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln --no-restore -v:minimal
    Build succeeded.
    66 Warning(s)
    0 Error(s)
    Time Elapsed: 00:00:24.95

    dotnet build-server shutdown
    MSBuild/C# server shutdown successfully.

**verified.** After each product commit, the four accounting suites were green before the next product commit was made. The intermediate counts were 15/15, 14/14, 2/2, and 3/3 after 9acdcaa6; the same after b6611696; the same after b2160cf8; and 15/15, 15/15, 2/2, and 3/3 after 2c646bd6 (the additional source test is the owner-contribution proof).

**assumed.** The report-only commit does not change product code, migrations, or tests, so the verification output above remains valid after that documentation commit. The final worker handoff records the report commit hash and clean-tree check.

