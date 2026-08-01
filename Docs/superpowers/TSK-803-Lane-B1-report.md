# TSK-803 Backend Lane B1 report

## Outcome

**Verified.** Source posting integration is implemented for the five approved families in the
existing atomic command paths. Source handlers build complete balanced proposals and attach them
to the caller-owned context; the posting service does not open a transaction or save on its own
(`RentalCommand.Data/Accounting/AccountingPostingService.cs:14-24`).

**Verified.** The final solution build completed with zero errors:

```text
MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln --no-restore -v:minimal
Build succeeded.
25 Warning(s)
0 Error(s)
```

**Verified.** Focused real-PostgreSQL checks passed:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter 'FullyQualifiedName~AccountingFoundationPostgreSqlTests' --no-restore -v:minimal
Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter 'FullyQualifiedName~AccountingSourcePostingPostgreSqlTests' --no-restore -v:minimal
Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter 'FullyQualifiedName~OwnerDistributionAuthorizationTests' --no-restore -v:minimal
Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests --filter 'FullyQualifiedName~WorkspaceAuthorizationKernelTests.CapitalizePaidExpenseReversesExpenseAndPostsOneCapitalPurchase|FullyQualifiedName~WorkspaceAuthorizationKernelTests.CapitalizeExpenseJournalFailureRollsBackAssetLinkJournalAndCompanions|FullyQualifiedName~WorkspaceAuthorizationKernelTests.CapitalAssetFactUpdateReversesAndRepostsWhileDescriptionOnlyUpdateDoesNotPost|FullyQualifiedName~WorkspaceAuthorizationKernelTests.CapitalAssetDateUpdateReversesAndRepostsCapitalPurchase|FullyQualifiedName~WorkspaceAuthorizationKernelTests.CapitalAssetDeleteReversesPostedCapitalPurchaseBeforeSoftDelete' --no-restore -v:minimal
Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests --filter 'FullyQualifiedName~BankingPersistenceAtomicCommandTests.Reconciliation_MatchingAlreadyPostedOwnerDistributionDoesNotReverseItOnClear|FullyQualifiedName~BankingPersistenceAtomicCommandTests.Reconciliation_MatchingUnpostedOwnerDistributionReversesOnlyMatchJournalOnClear|FullyQualifiedName~BankingPersistenceAtomicCommandTests.Reconciliation_ConcurrentDifferentBankLines_CannotClaimSameOwnerDistribution' --no-restore -v:minimal
Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```

The build server was stopped after the heavy verification batch with `dotnet build-server shutdown`
and both the MSBuild and compiler servers reported successful shutdown (**verified**).

## Commits

The branch contains these accounting commits (**verified** with `git log --oneline`):

- `84b50504` Add double-entry ledger persistence.
- `fcd11799` Add accounting posting and seed services.
- `280fff7e` Align ledger RLS with the foundation policy pattern.
- `782df98e` Enforce accounting portfolio boundaries.
- `5d14950b` Protect journal dimensions from destructive deletes.
- `9959fdee` Integrate tenant and deposit accounting postings.
- `a953d0e5` Integrate expense and bank source postings.
- `718fc10c` Integrate loan payment accounting postings.
- `eb8c4369` Integrate capital and owner accounting postings.
- `2a848880` Repost capital facts when dimensions change. This commit was already present when
  the dimension test was resumed; its behavior was explicitly approved by the controller.
- `2bbc16ae` Verify capital dimension reposts.

## Posting rule to code map

The following map uses the contract's debit and credit rules (**verified** by the cited source
lines).

| Contract source action | Debit | Credit | Code receipt |
| --- | --- | --- | --- |
| Rent, pet, parking, late fee, utility, other, and manual tenant charge | Tenant A/R | Mapped income | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:19-38`; command handlers call it at `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:587` and `RentalCommand.Data/Payments/ScheduledTenantChargeCommandHandler.cs:156`. |
| Tenant receipt | Operating Cash | Tenant A/R | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:57-85`; receipt handlers call it at `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:118,1641` and provider receipt integration at `RentalCommand.Data/Payments/ProviderPaymentCommandHandlers.cs:776`. |
| Provider receipt before settlement | Undeposited Funds | Tenant A/R | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:57-85` with the provider cash system mapping supplied by the source handler; provider receipt source call is `RentalCommand.Data/Payments/ProviderPaymentCommandHandlers.cs:776`. |
| Provider settlement | Destination bank cash | Undeposited Funds | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:826-855`; reconciliation handler call `RentalCommand.Data/Banking/BankingPersistenceCommandHandlers.cs:927-930`. |
| Tenant concession, charge reduction, and adjustment | Mapped income or contra-income | Tenant A/R | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:40-55,330-376`; command calls at `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:740,805`. |
| Receivable write-off | Mapped bad-debt expense | Tenant A/R | **Assumed.** No separate new write-off source command was found in the Lane B1 source map; existing adjustment/reversal paths remain the available source actions (`RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:740-805`). |
| Security deposit funding or receipt | Security Deposit Trust Cash | Security Deposits Payable | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:172-216`; opening recovery path `RentalCommand.Data/Payments/OpeningSecurityDepositRecoveryHandler.cs:55`. |
| Security deposit refund | Security Deposits Payable | Security Deposit Trust Cash | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:218-262`; handler call `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:1774`. |
| Deposit applied to an authorized tenant charge | Security Deposits Payable | Tenant A/R | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:264-308`; handler call `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:1723`. |
| Deposit and tenant reversals | Exact opposite of original lines, preserving source type and lineage | Exact opposite of original lines | `RentalCommand.Data/Payments/TenantAccountingPosting.cs:107-170,310-328`; command calls `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:668,926,1214,1398,1923`. |
| Expense paid immediately | Mapped expense or asset | Selected cash | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:27-49,194-227`; expense mutation call `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:322-335`. |
| Bill incurred but unpaid | Mapped expense or asset | Accounts Payable | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:902-944`; the same source occurrence call is `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:325-329`. |
| Bill payment | Accounts Payable | Selected cash | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:163-193`; payment transition call `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:328-335`. |
| Recurring expense generation | Mapped expense or asset | Accounts Payable or cash by state | `RentalCommand.Data/Automation/ScheduledFinanceCommandHandlers.cs:339`; it delegates to `MoneyAccountingPosting.PostExpenseOccurrenceAsync` at `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:27-49`. |
| Bank expense match and clear | Expense account and cash according to source state; clear is an exact reversal | Opposite cash or payable line | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:228-301`; reconciliation calls at `RentalCommand.Data/Banking/BankingPersistenceCommandHandlers.cs:932-938`. |
| Bank transfer | Destination Cash | Source Cash | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:857-900`; reconciliation call `RentalCommand.Data/Banking/BankingPersistenceCommandHandlers.cs:967-977`. |
| Mortgage payment | Mortgage Payable principal, Mortgage Interest, Escrow Asset | Cash for total payment | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:473-574`; loan command integration `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:521-530`. |
| Loan correction | Exact opposite, then a new correction-keyed loan entry | Exact opposite, then replacement components | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:516-573`; scan correction call `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs:1215`, and payment correction source key `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:521-530`. |
| Capital purchase | Buildings, Land, or selected asset account | Cash or Accounts Payable | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:607-666`; capitalization call `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:657-658,794-795`. Paid expense capitalization reverses the expense journal first at `MoneyAccountingPosting.cs:616-638`. |
| Capital asset cost, date, property, or unit correction | Exact opposite, then replacement capital lines | Exact opposite, then replacement capital lines | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:668-696`; mutation calls `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:749-801`; dimension behavior is covered by commit `2a848880` and test commit `2bbc16ae`. |
| Capital asset delete | Exact opposite of current capital purchase | Exact opposite of current capital purchase | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:698-717`; delete call `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:681-717`. |
| Depreciation | Depreciation Expense | Accumulated Depreciation | **Assumed.** `MoneyAccountingPosting.PostDepreciationAsync` is a skeleton at `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:719-751`, and no existing Lane B1 source command invokes it; the contract defers the full depreciation run. |
| Owner contribution | Cash | Owner contribution equity dimension | **Assumed.** No existing owner-contribution source command was found; omitted as approved by the family-five gate. |
| Owner distribution approval | Owner-specific distribution equity dimension | Cash | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:574-605`; approval call `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:963-967`. |
| Owner distribution bank match | Evidence-only when approval already posted; otherwise one bank-match journal | Evidence-only when approval already posted; otherwise one bank-match journal | `RentalCommand.Data/Accounting/MoneyAccountingPosting.cs:380-443`; match and clear calls `RentalCommand.Data/Banking/BankingPersistenceCommandHandlers.cs:948-985`. |

Payment allocations remain settlement records and do not post income a second time (**verified** by
the receipt and allocation handlers at `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:118`
and `TenantMoneyCommandHandlers.cs:1641-1723`).

## Foundation, persistence, and safety receipts

- **Verified:** unique source replay key and covering indexes are configured at
  `RentalCommand.Data/Accounting/AccountingFoundationModelConfiguration.cs:56-77`; account and
  journal line indexes are at lines 24-36 and 84-110.
- **Verified:** the four journal dimension foreign keys use `DeleteBehavior.Restrict` at
  `RentalCommand.Data/Accounting/AccountingFoundationModelConfiguration.cs:97-108`, with the
  follow-up migration at `RentalCommand.Data/Migrations/20260801020204_RestrictJournalDimensions.cs:8-114`.
- **Verified:** API and Engine register the posting, seed, and conversion services at
  `RentalCommand.Api/Program.cs:385-390` and `RentalCommand.Engine/Program.cs:99-104`.
- **Verified:** the conversion skeleton and reconciliation service exist at
  `RentalCommand.Data/Accounting/AccountingConversionFramework.cs:22-52`; no historical conversion
  run was added in this lane (**assumed by the contract's section-three deferral**).
- **Verified:** the foundation PostgreSQL suite covers idempotent seeding, application and database
  balance rejection, immutable posted rows, cross-portfolio denial, and atomic failure injection at
  `RentalCommand.Api.Tests/Domain/AccountingFoundationPostgreSqlTests.cs:20-365`.
- **Verified:** capital failure injection rolls back the asset, source expense link, journal, receipt,
  audit, and outbox rows at `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs:967-1040`.
- **Verified:** exact capital replay and lineage are covered at
  `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs:878-1158` and owner bank
  matching/clearing is covered at `RentalCommand.IntegrationTests/BankingPersistenceAtomicCommandTests.cs:1415-1640`.

The migration was exercised by the fresh real-PostgreSQL foundation fixture, which runs
`MigrateAsync` before the accounting tests (**verified** by
`RentalCommand.Api.Tests/Domain/AccountingFoundationPostgreSqlTests.cs:20-24` and the 12/12 result
above).

## Working tree

**Verified.** `git status --short` was clean after source commit `2bbc16ae`; this report was the
only subsequent uncommitted file at report creation, and no push was performed.
