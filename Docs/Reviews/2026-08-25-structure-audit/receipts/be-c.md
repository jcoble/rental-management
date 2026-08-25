# Lane be-c receipt

- Lane: be-c — collapse the write-executor wrappers into IWriteExecutor
- Start time: 2026-08-25T17:01:00-04:00

## Item 2 audit — Verified before renaming

Command run: `rg -n '\\.ExecuteExactAsync' RentalCommand.Api RentalCommand.Engine --glob '*.cs'` returned 50 call sites. Every key argument was read. No call site requires exact semantics: request-derived keys are trimmed and bounded at 128 characters by `AuthenticatedPortfolioControllerBase.TryValidateIdempotencyKey` (RentalCommand.Api/Controllers/AuthenticatedPortfolioControllerBase.cs:20-23), and the composed keys remain below 200; Team keys are SHA-256 digests (RentalCommand.Api/Controllers/TeamController.cs:328-329); Workspace Experience keys are SHA-256 digests after a 200-character input check (RentalCommand.Api/Controllers/WorkspaceExperienceController.cs:51-62); Sandbox keys are SHA-256 digests (RentalCommand.Api/Services/Domain/SandboxService.cs:107-114); the LLM mutation identity trims and bounds client operation IDs at 160 (RentalCommand.Api/Services/Domain/WorkspaceLlmCredentialService.cs:342-343), and usage identities are generated from a fixed scan prefix, GUID, bounded feature, and invocation number (RentalCommand.Engine/Workers/ScanProcessingWorker.cs:206-212).

The 50 reviewed arguments, grouped exactly by source location, were:

- OwnerPortalController:143 — portfolio/notification IDs plus SHA-256 digest (144).
- WorkspaceInvitationsController:103 — invited-user ID plus SHA-256 token hash (104).
- DevWorkersController:105 and DevClockController:214 — `BuildIdentityKey` over the trimmed 128-character delivery key (106; 215).
- WorkspaceExperienceController:73 — portfolio/access-context IDs plus SHA-256 digest (74).
- StripePaymentService:365 — provider event ID prefixed with `stripe:` (365; provider-generated ID).
- TeamController:301 — portfolio ID plus SHA-256 key digest (302; digest built at 328-329).
- DemoDataSeeder:169 — startup portfolio ID plus generated startup key; :192 and :213 — fixed portfolio/agreement/template keys (170; 193; 214).
- OwnerEntityService:236 and :352 — portfolio/entity IDs plus SHA-256 digest (237; 353).
- PortfolioService:125 and :145 — `AtomicWorkspaceCoreMutation` identity from the controller-bounded operation key (126; 146).
- BankingService:505 and :992 — integer scope/entity components plus controller-bounded operation key (506; 993).
- TenantService:66 — `AtomicGuidedTenantSetup` identity from the controller-bounded operation key (67).
- ConversationService:544 and :685 — command identity from the controller-bounded operation key (545; 686); :611 — notification mutation identity (612).
- NotificationFoundationService:584 and :593 — notice/notification mutation identities from controller-bounded operation keys (585; 594).
- PortfolioQaService:524 — portfolio ID plus `RequiredDeliveryOperationId`, trimmed and bounded at 128 (525; 551-560).
- OwnerStatementEmailService:77 — command identity with controller-bounded delivery key (78; RentalCommand.Api/Services/Domain/OwnerStatementEmailService.cs:325-328).
- NoticeDraftService:110 and NotificationService:264 — mutation identities from controller-bounded operation keys (111; 265).
- WorkspaceLlmCredentialService:131, :159, and :179 — trimmed client operation identity bounded at 160 (132; 160; 180); :258 — portfolio ID plus generated scan usage identity (259; generation at RentalCommand.Engine/Workers/ScanProcessingWorker.cs:210-212).
- ApplicationService:149, :460, and :540 — mutation identities from controller-bounded operation keys (150; 461; 541).
- SandboxService:59 and :86 — portfolio ID plus SHA-256 digest (60; 87; digest at 107-114).
- ExpenseService:329 — money mutation identity from controller-bounded operation key (330).
- InspectionService:318, :333, :347, :361, :376, :392, :413, :429, :444, :459, :476, :491, :525, and :838 — inspection mutation identities from controller-bounded operation keys (319; 334; 348; 362; 377; 393; 414; 430; 445; 460; 477; 492; 526; 839).

Conclusion: no leading/trailing whitespace or over-200-character key is reachable at these 50 production call sites, so items 3-4 proceed for all call sites.

## Completion

### Item 1 — DONE (Verified)

- Commit `9e4afd5b` changed `RentalCommand.Data/Atomic/WriteExecutor.cs` by `+8/-2` lines.
- The shared executor trims the key, rejects null/empty or over-200-character values with the existing `ArgumentException` contract, and passes the normalized key to the runner. The deleted adapters contained the only duplicate adapter-level check (`git show 56c62ba3:RentalCommand.Api/Writes/RequestWriteExecutor.cs` and `git show 56c62ba3:RentalCommand.Engine/Writes/JobStepWriteExecutor.cs`).

### Item 2 — DONE (Verified)

- The audit above records all 50 `ExecuteExactAsync` production call sites and concludes that none needs exact whitespace or length semantics.

### Item 3 — DONE (Verified)

- Commit `73bc0539` deleted `RentalCommand.Api/Writes/RequestWriteExecutor.cs` and `RentalCommand.Engine/Writes/JobStepWriteExecutor.cs`, removed their API, Engine, and test DI registrations, changed consumers to `IWriteExecutor`, and renamed every exact call to `ExecuteAsync`.
- The grouped commit for items 3-5 changed 229 backend `.cs` files, `+650/-1068` lines (`git show --format= --stat 73bc0539`).

### Item 4 — DONE (Verified)

- The 13 nullable API domain services now require non-null `IWriteExecutor` fields and constructor parameters, with guard methods removed and uses inlined. Direct test constructions receive mocks or the shared test executor; `OwnerEntityService` keeps its configuration parameter optional after the required executor parameter (`RentalCommand.Api/Services/Domain/OwnerEntityService.cs:30-38`).
- `RecurringMaintenanceCrudWritePostgreSqlTests.MutationWithoutSharedExecutor_RequiresWriteExecutor` was deleted because it asserted the nullable-executor guard that item 4 removes. No behavioral test was weakened.

### Item 5 — DONE (Verified)

- Commit `73bc0539` changed all test doubles from the deleted interfaces to `IWriteExecutor` and removed forwarding methods that became duplicate `ExecuteAsync` implementations. Representative changes are in `RentalCommand.Api.Tests/Domain/PublicApplicationsControllerTests.cs:173-191`, `RentalCommand.Api.Tests/Domain/PortfolioQaServiceTests.cs:961-985`, and `RentalCommand.IntegrationTests/ProviderPaymentAtomicCommandTests.cs:2254-2310`.

## Verification (Verified)

Commands were run one at a time in the required order after implementation:

1. `MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln -c Debug --nologo -v q` — exit `0`. Last five lines: the existing `ProductionScanConfirmationTargetWriterTests.cs(1763,13)` warning, `45 Warning(s)`, `0 Error(s)`, a blank line, and `Time Elapsed: 00:00:10.22`.
2. `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --no-build --nologo -v q` — exit `1`; `Failed!  - Failed:     9, Passed:  1425, Skipped:     0, Total:  1434, Duration: 4 m 13 s - RentalCommand.Api.Tests.dll (net10.0)`.
3. `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests --no-build --nologo -v q` — exit `0`; `Passed!  - Failed:     0, Passed:   109, Skipped:     0, Total:   109, Duration: 14 s - RentalCommand.Engine.Tests.dll (net10.0)`.
4. `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Data.Tests --no-build --nologo -v q` — exit `0`; `Passed!  - Failed:     0, Passed:   125, Skipped:     0, Total:   125, Duration: 8 s - RentalCommand.Data.Tests.dll (net10.0)`.
5. `rg -n 'IRequestWriteExecutor|IJobStepWriteExecutor|ExecuteExactAsync|RequireWrites' --glob '*.cs' --glob '!**/obj/**'` — exit `1` with empty output, meaning zero matches.
6. `dotnet build-server shutdown` — exit `0`; last four lines were `Shutting down MSBuild server...`, `Shutting down VB/C# compiler server...`, `VB/C# compiler server shut down successfully.`, and `MSBuild server shut down successfully.`
7. Final post-cleanup rerun of `MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln -c Debug --nologo -v q` — exit `0`; last output lines were `36 Warning(s)`, `0 Error(s)`, a blank line, and `Time Elapsed: 00:00:04.04`.
8. Final `dotnet build-server shutdown` — exit `0`; last four lines were `Shutting down MSBuild server...`, `Shutting down VB/C# compiler server...`, `VB/C# compiler server shut down successfully.`, and `MSBuild server shut down successfully.`

The nine API failures were timestamp-precision failures in the existing PostgreSQL/SQLite timing tests. Filtered runs verified, for example, `VendorDispatchServiceTests` expected `2026-08-25 21:26:30.4779554` but stored `2026-08-25 21:26:30.477955`, and `InspectionChecklistServiceTests` expected `2026-08-25 21:38:13.5655888` but stored `2026-08-25 21:38:13.565588`; no changed executor behavior appeared in those stacks.

## Test-file change receipt (Verified)

The complete changed-test-file set is the 140 paths produced by:

`git show --format= --name-only 73bc0539 | rg '^RentalCommand\.(Api\.Tests|Engine\.Tests|IntegrationTests)/'`

All 140 changes are mechanical interface/method renames or removal of deleted wrapper registrations. The direct-construction files additionally pass the required mock/shared executor: `AppointmentScheduleSummarySqlTests.cs`, `AppointmentServiceListTests.cs`, `CanonicalLeaseReaderSqlTests.cs`, `FinancialReportPostgreSqlTests.cs`, `OwnerCutoverPostgreSqlTests.cs`, `OwnerEntityServiceListTests.cs`, `PortalServiceAppointmentTests.cs`, `PortalServiceBalanceTests.cs`, `PortalServiceLeaseTests.cs`, `PortalServiceWorkOrderPostgreSqlTests.cs`, `PropertyServiceTests.cs`, `PropertyWorkspacePostgreSqlTests.cs`, `RecurringMaintenanceServiceTests.cs`, `RecurringMaintenanceTaskServiceTests.cs`, `ReportsServicePostgreSqlTests.cs`, `ReportsServiceTests.cs`, `RemoteSelectorPagingPostgreSqlTests.cs`, `UnitConditionPostgreSqlTests.cs`, `WorkOrderServiceListTests.cs`, and `WorkOrderStatusTimelineTests.cs`. `AtomicDomainTestKernel.cs` removes wrapper registration plumbing; the forwarding-double files and the obsolete recurring-maintenance guard test are described under items 4-5.

No web or mobile files were changed (Verified: `git diff --name-only | rg '^(web|mobile)/'` returned no output). Existing warnings and the nine timing failures above were not changed.
