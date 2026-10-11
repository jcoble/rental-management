# BACKEND structure audit — SOL medium

## Shape of the code

- Verified: 1,049 C# files across the five scoped projects, excluding `Migrations/` and `obj/` (`find … -name '*.cs' | wc -l`).
- Verified: Core has 360 files, Data 207, API 419 (75 controllers, 213 service files, 76 DTO files), Engine 63, and TestCommon 13.
- Verified: 71 controllers use 20 `AuthenticatedPortfolioControllerBase`, 42 `ManagementControllerBase`, and 11 direct `ControllerBase` inheritance sites (`rg -l ': …'`).
- Verified: the dominant write vocabulary is 195 `*Rule` classes and 215 command records; the shared executor appears in 76 files.
- Verified: controllers contain no direct `SaveChangesAsync` or explicit transaction calls (`rg -l 'SaveChangesAsync\\(' RentalCommand.Api/Controllers` and `rg -l 'BeginTransactionAsync|CreateExecutionStrategy' …`: both 0).
- Verified: the old atomic unit-of-work kernel was removed in commit `490a3c94`; commit `1c91775c` renamed write classes and entry points to `*Rule`/`ExecuteAsync`.
- Verified: Data configuration is consistently extracted: 31 `Configure*` calls and zero inline `modelBuilder.Entity<T>` calls in `RentalCommandDbContext.cs`.
- Verified: the code still exposes 166 public interfaces, including several provider/worker seams with only one production implementation.
- Verified: the largest behavior files are `FoundationBaselinePostgreSql.cs` (3,702 lines), `ReportsService.cs` (3,027), `ScanService.cs` (2,471), and `TenantMoneyCommandHandlers.cs` (2,434).
- Assumed: counts describe the checked-out source only; generated code, migrations, and excluded clients were intentionally omitted.

## Top changes, ranked by simplification-per-effort

### 1. Delete the unused `DateTimeNormalization` helper

- Where: `RentalCommand.Api/Services/Domain/DateTimeNormalization.cs:9`, `RentalCommand.Api/Services/Domain/AtomicMoneyMutationRule.cs:1694`, `RentalCommand.Api/Services/Domain/AtomicInspectionMutationRule.cs:1275`
- Evidence: Verified: `rg -n '\\bDateTimeNormalization\\b' RentalCommand.Core RentalCommand.Data RentalCommand.Api RentalCommand.Engine --glob '*.cs' --glob '!**/obj/**'` returns only its declaration: 1 reference. Four separate local `Utc` methods remain (`rg -n 'private (static )?DateTime Utc\\(' …`: 4).
- Why it is two-ways/over-built: The shared-looking extension API has no caller, while active code uses local normalization functions. It therefore adds a convention that the codebase does not actually follow.
- Change: Delete `DateTimeNormalization.cs`. Do not introduce another replacement abstraction as part of this cleanup.
- Size/risk: 1 file, about 25 deleted lines, low; one PR? yes.

### 2. Delete the speculative `ShowMojo` provider key

- Where: `RentalCommand.Core/Listings/ListingChannelContracts.cs:5`
- Evidence: Verified: `rg -n '\\bShowMojo\\b' . --glob '*.cs' --glob '!**/obj/**' --glob '!RentalCommand.Data/Migrations/**'` returns exactly 2 lines: the comment and declaration in the same file. The comment explicitly says no integration exists.
- Why it is two-ways/over-built: This is an extension point for a requirement that is not implemented. It gives readers and callers a provider identity that cannot do anything.
- Change: Delete `ListingProviderKeys.ShowMojo` and its future-facing comment.
- Size/risk: 1 file, 2 lines, low; one PR? yes.

### 3. Replace the two leftover `*Handler` forwarding shells with their `*Rule` targets

- Where: `RentalCommand.Data/Operations/WorkOperationMutationHandlers.cs:14`, `RentalCommand.Data/Operations/WorkOperationMutationHandlers.cs:24`, `RentalCommand.Data/Operations/WorkOperationMutationHandlers.cs:116`
- Evidence: Verified: `CreateWorkOrderHandler` has 25 references, of which 23 call `CreateWorkOrderHandler.DataUpdate`; `CreateWorkOrderRule.DataUpdate` has 1 direct reference. `AddStaffWorkOrderCommentHandler` has 4 references, of which 2 call its forwarding `AddActivity`; `AddStaffWorkOrderCommentRule.AddActivity` has 1 direct reference. Commands: `rg -n '\\bCreateWorkOrderHandler\\b' …`, `rg -n 'CreateWorkOrderHandler\\.DataUpdate' …`, and corresponding `AddStaff…` searches.
- Why it is two-ways/over-built: Both handler classes have empty constructors and only forward static calls to the canonical rule classes. They are remnants of the vocabulary migration and preserve two names for the same owner.
- Change: Replace the 25 forwarding references with `CreateWorkOrderRule.DataUpdate` or `AddStaffWorkOrderCommentRule.AddActivity`, then delete both handler shells.
- Size/risk: about 10 files, roughly 35 changed/deleted lines, low; one PR? yes.

### 4. Delete the 17 authorization methods that only throw “retired path”

- Where: `RentalCommand.Data/Accounting/LedgerAccountCommandHandlers.cs:88`, `RentalCommand.Data/Accounting/ConfirmAccountingMappingHandler.cs:147`, `RentalCommand.Api/Services/Domain/AtomicRentalMutationRule.cs:71`
- Evidence: Verified: `rg -n '=> throw RetiredPath\\(\\)|throw RetiredPath\\(\\);' RentalCommand.Api RentalCommand.Data RentalCommand.Engine --glob '*.cs' --glob '!**/obj/**'` finds 17 methods in 14 files. `rg -l 'Legacy .* writes are retired; use the shared write executor' … | wc -l` finds retired-path machinery in 16 files.
- Why it is two-ways/over-built: The canonical rule methods coexist with public methods whose only behavior is to reject the old path. That is dead API surface, not useful compatibility.
- Change: Delete only the 17 always-throwing methods and their now-unused `RetiredPath` helpers; retain active `AuthorizeReplayAsync` methods that perform real authorization.
- Size/risk: 14 files, roughly 100–150 deleted lines plus test adjustments, medium because authorization signatures are shared; one PR? yes.

### 5. Rename the remaining `*Handler(s).cs` files that now contain only rules

- Where: `RentalCommand.Data/Accounting/LedgerAccountCommandHandlers.cs:10`, `RentalCommand.Data/Payments/ProviderPaymentCommandHandlers.cs:84`, `RentalCommand.Data/Screening/CreateAdverseActionNoticeHandler.cs:14`
- Evidence: Verified: `find RentalCommand.Data -type f -name '*CommandHandlers.cs' | wc -l` returns 25; `find … -name '*Handler.cs'` returns 4. Those 25 plural files contain 113 public `*Rule` classes and zero public `*Handler` classes (`rg '^public sealed class .*Rule\\b' … --glob '*CommandHandlers.cs'`: 113; corresponding Handler search: 0). There are already 32 canonically named `*Rule.cs` files.
- Why it is two-ways/over-built: The recent vocabulary migration changed the classes but left 29 filenames advertising the old convention. File discovery therefore requires knowing both names.
- Change: Rename the 29 files to `*Rules.cs` or the principal `*Rule.cs` name, without moving classes or refactoring behavior.
- Size/risk: 29 renames, near-zero textual diff, low; one PR? yes.

### 6. Remove the unreachable `Acknowledged` vendor-dispatch state

- Where: `RentalCommand.Core/Enums/VendorDispatchStatus.cs:12`, `RentalCommand.Data/Operations/DispatchWorkOrderToVendorRule.cs:21`, `RentalCommand.Api/Services/Domain/WorkOrderService.cs:408`
- Evidence: Verified: `rg -n 'VendorDispatchStatus\\.Acknowledged' RentalCommand.Core RentalCommand.Data RentalCommand.Api RentalCommand.Engine --glob '*.cs' --glob '!**/obj/**'` finds 6 references. All six merely classify it as open; no production assignment sets a dispatch to `Acknowledged`. The enum comment says it is reserved for a future “ON IT” reply.
- Why it is two-ways/over-built: Every open-state check must account for a branch that cannot occur. This is speculative workflow state embedded in persisted-domain vocabulary.
- Change: Delete the enum member and simplify the six open-status checks to `Dispatched`.
- Size/risk: about 7 files, roughly 15 lines, medium because it changes a persisted enum contract; one PR? yes, after confirming no stored rows use its numeric value. Assumed: no deployed row currently has this value; that requires a database check.

### 7. Replace `LeaseManagementReadContext` with `WorkspaceReadScope`

- Where: `RentalCommand.Core/Authorization/WorkspaceReadScope.cs:8`, `RentalCommand.Api/Services/Domain/ILeaseManagementQueryService.cs:75`, `RentalCommand.Api/Controllers/ManagementControllerBase.cs:60`
- Evidence: Verified: both records carry the same five coordinates: portfolio, user, session, access-context, and revision. `WorkspaceReadScope` appears 878 times across 172 files; `LeaseManagementReadContext` appears 77 times across 9 files (`rg -n` and `rg -l` for each symbol).
- Why it is two-ways/over-built: Lease queries renamed an otherwise identical authorization envelope. Controllers consequently expose both `GetWorkspaceReadScope` and `TryReadAccessContext`, and readers must learn that the two types carry the same authority.
- Change: Change the nine lease/report files to accept `WorkspaceReadScope`, delete `LeaseManagementReadContext`, and delete `TryReadAccessContext`.
- Size/risk: about 9 files, roughly 80–120 mostly mechanical lines, medium because it touches authorization-bearing method contracts; one PR? yes.

### 8. Remove obsolete fields and constructor overloads from ledger-account commands

- Where: `RentalCommand.Core/Accounting/LedgerAccountCommands.cs:8`, `RentalCommand.Core/Accounting/LedgerAccountCommands.cs:25`, `RentalCommand.Api/Controllers/AccountingController.cs:80`, `RentalCommand.Data/Accounting/LedgerAccountCommandHandlers.cs:52`
- Evidence: Verified: `RequestedNormalBalance` has exactly 1 reference—its declaration (`rg -n '\\bRequestedNormalBalance\\b' … | wc -l`: 1). The handler derives normal balance solely from `AccountType`. The only production `new CreateLedgerAccountCommand` call passes `null` for that field and `SystemKey`; the record then provides a second constructor described as a compatibility overload.
- Why it is two-ways/over-built: The primary constructor contains legacy inputs the active write path ignores, while the supposedly compatibility constructor more closely represents the actual command. Callers must understand both shapes and supply meaningless nulls.
- Change: Make the reduced constructor the sole record shape; delete `RequestedNormalBalance`, command-level `SystemKey`, and the overload. Apply the same treatment to any update overload whose only role is reconstructing fields already known by its sole caller.
- Size/risk: 3–6 files, roughly 40–80 lines, high because this is a money/accounting write contract; one PR? yes.

### 9. Let the global exception handler own all `DomainValidationException` responses

- Where: `RentalCommand.Api/GlobalExceptionHandler.cs:59`, `RentalCommand.Api/Controllers/OwnerContributionController.cs:73`, `RentalCommand.Api/Controllers/LeaseAgreementController.cs:388`, `RentalCommand.Api/Controllers/OwnerDistributionController.cs:76`
- Evidence: Verified: the global handler maps `DomainValidationException` to its carried 400/409 status and `ProblemDetails`. Separately, `rg -n 'catch \\(DomainValidationException' RentalCommand.Api/Controllers --glob '*.cs'` finds 16 controller catches that always convert it to `Conflict(new { error = … })`.
- Why it is two-ways/over-built: The same exception reaches clients through two response formats and two status-selection rules. Sixteen repeated catches bypass the central policy already intended for this exception.
- Change: Delete those 16 catches and allow the existing global handler to map them. Do not add another controller helper.
- Size/risk: 4 controller files, about 20 deleted lines, medium because the error-body contract changes from `{error}` to `ProblemDetails`; one PR? yes.

### 10. Replace local property-address formatting with `AddressComposer`

- Where: `RentalCommand.Api/Services/Domain/AddressComposer.cs:9`, `RentalCommand.Api/Services/Domain/TechnicianExperienceService.cs:234`, `RentalCommand.Api/Services/Domain/LeasingWorkspaceService.cs:227`, `RentalCommand.Api/Services/Domain/InspectionService.cs:750`
- Evidence: Verified: `AddressComposer.Compose` has 3 call sites. `rg -n 'string\\.Join\\(\", \", new\\[\\]' RentalCommand.Api --glob '*.cs'` finds 5 competing joins; four more address strings use interpolation or concatenation in `InspectionService`, `TenantAccountMoveOutStatementService`, and `LeasingWorkspaceService`.
- Why it is two-ways/over-built: Nine active display paths independently decide how to omit null parts, include line two, and combine state/postal code, despite an existing formatter for exactly those inputs. This produces repeated logic and subtly different output.
- Change: Replace those nine local address constructions with `AddressComposer.Compose`; retain prefixes such as the property name outside the helper.
- Size/risk: about 8 files, roughly 25–40 lines, low; one PR? yes.

### 11. Remove the permanently-true `EnableRentCharges` switch

- Where: `RentalCommand.Core/Entities/AutomationSettings.cs:10`, `RentalCommand.Data/NotificationAutomationModelConfiguration.cs:128`, `RentalCommand.Api/Services/Domain/AtomicNotificationMutationRule.cs:446`
- Evidence: Verified: `rg -n '\\bEnableRentCharges\\b' … --glob '!RentalCommand.Data/Migrations/**'` finds 7 references. The entity says it is retained only for schema compatibility; the model adds a check constraint forcing it true, and the mutation path writes `true`.
- Why it is two-ways/over-built: The application models core rent posting as configurable even though the database and write path permit only one branch. It adds a setting, DTO data, mutation handling, and query conditions without a real choice.
- Change: Remove the property from the entity, read/write DTOs, mutation code, and scheduling predicates; drop the constrained column in one forward migration.
- Size/risk: about 8 source files plus one migration, roughly 80–140 lines, high because it changes schema and scheduled money behavior; one PR? yes. Assumed: no external client still displays this read-only field.

### 12. Delete `AccountingProviderResolver` and inject the sole provider directly

- Where: `RentalCommand.Core/Interfaces/IAccountingProvider.cs:7`, `RentalCommand.Core/Enums/AccountingProvider.cs:7`, `RentalCommand.Api/Services/Domain/AccountingProviderResolver.cs:18`, `RentalCommand.Api/Services/Domain/QuickBooksAccountingProvider.cs:33`
- Evidence: Verified: `rg -n ': IAccountingProvider\\b'` across production finds exactly 1 implementation. The enum has exactly one value, QuickBooks. `IAccountingProvider` has 18 production references and `AccountingProviderResolver` 11; three services consume the resolver. The comments explicitly justify the dictionary using future Xero/FreshBooks/Wave implementations.
- Why it is two-ways/over-built: The interface remains useful as an external-provider/test seam, but placing a dictionary resolver on top of a one-value enum and one implementation is speculative dispatch. Every call takes two indirections to select something that cannot vary.
- Change: Keep `IAccountingProvider`, inject it directly into `AccountingConnectionService`, `AccountingTokenService`, and `AccountingImportService`, and delete `AccountingProviderResolver`. Retain the enum only where persisted connection data requires it.
- Size/risk: about 7 files, roughly 70–110 deleted/changed lines, medium because it touches an external integration contract; one PR? yes.

### 13. Delete the listing-adapter resolver layer

- Where: `RentalCommand.Core/Listings/ListingChannelContracts.cs:15`, `RentalCommand.Core/Listings/ListingChannelContracts.cs:27`, `RentalCommand.Api/Services/Domain/ListingChannelAdapters.cs:5`, `RentalCommand.Api/Services/Domain/ListingWorkspaceService.cs:666`
- Evidence: Verified: the combined adapter/resolver symbols have 14 production references (`rg -n '\\bListingChannelAdapterResolver\\b|\\bIListingChannelAdapter\\b|\\bIListingChannelAdapterResolver\\b|\\bDisabledZillowListingChannelAdapter\\b' …`). There is one production adapter, `DisabledZillowListingChannelAdapter`, and all six operations return failed tasks. The resolver adds a dictionary around that sole disabled implementation.
- Why it is two-ways/over-built: The resolver models multiple connected listing providers before even one usable provider exists. The availability check can be made against the sole adapter without a second interface and lookup layer.
- Change: Inject `IListingChannelAdapter` directly into `ListingWorkspaceService`, delete `IListingChannelAdapterResolver` and `ListingChannelAdapterResolver`, and keep the existing unavailable behavior unchanged.
- Size/risk: 4–6 files, roughly 50–80 lines, medium because tests substitute adapters and listing publication is a workflow boundary; one PR? yes.

## Patterns worth a single rule (not a code change)

- After a vocabulary migration, rename files and delete rejecting compatibility entry points in the same lane; do not leave `Handler` filenames around `Rule` classes.
- Do not add enum members, provider keys, or resolver dictionaries until a second executable branch exists.
- A configuration value constrained to one value is not configuration; remove the switch and its branches.
- Use `WorkspaceReadScope` as the one canonical five-coordinate workspace authority envelope.
- Exceptions assigned to the global handler must not be remapped in individual controllers unless an endpoint has a documented different wire contract.

## Looked at and left alone

- Verified: `AuthenticatedPortfolioControllerBase` and `ManagementControllerBase` are justified; 20 and 42 controllers respectively use distinct authenticated-versus-management admission policies.
- Verified: Engine service interfaces such as `IRentChargeService` have one production implementation, but workers and `SimWorkerRegistry` consume them and tests substitute them; deleting them would couple orchestration to implementation without a meaningful simplification.
- Verified: API `DataUpdateService` and Engine `NotifyDataUpdateService` both implement `IDataUpdateService`, but they serve genuinely different process boundaries: SignalR in API versus PostgreSQL `NOTIFY` in Engine.
- Verified: the three upload validators share filename/size checks, but scan, general-document, and lease-template uploads intentionally accept different file sets; merging them would require a new policy abstraction.
- Verified: `ReportsService` and `ScanService` are very large, but their inspected sections contain many distinct SQL-backed report/scan workflows; splitting files alone would move code without deleting a concept.

## Not covered

- `web/`, `mobile/`, `mcp/`, `deploy/`, `Docs/`, and `RentalCommand.Data/Migrations/` were excluded as directed.
- Tests were searched only to distinguish dead production code from seams actively used by test substitutes.
- No compile, test, install, branch, worktree, or file-changing command was run.
- The audit did not manually inspect every one of the 166 public interfaces or all 1,049 scoped files; breadth-first counts were followed by inspection of write-kernel remnants, controller scoping, mapping/error conventions, provider seams, configuration flags, large files, and low-reference symbols.
- Deleting persisted enum members or columns requires deployed-data checks and migration review that were outside this read-only, migrations-excluded pass.
tokens used
