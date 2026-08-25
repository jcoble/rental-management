# Backend structure audit — Opus 5

## Shape of the code
Verified: ~206k lines of C# across five in-scope projects. `RentalCommand.Api` holds 75 controllers, 161 files under `Services/Domain`, 76 DTO files. Controllers inherit `ManagementControllerBase` (42), `AuthenticatedPortfolioControllerBase` (17, and the parent of the first), or bare `ControllerBase` (10) — one hierarchy, not two. Reads are scoped by `WorkspaceReadScope` (711 method signatures in `Services/Domain`) via `GetWorkspaceReadScope()` (226 controller call sites); a legacy `int portfolioId` scoping still exists (133 signatures, 80 `GetPortfolioId()` controller sites). Writes are uniform: `TransactionalWrite` + `*Rule` classes (172 `Rule`-suffixed classes in `RentalCommand.Data`) executed through `IRequestWriteExecutor.ExecuteAsync` (180 prod call sites). Mapping is inline `new XDto{…}` projection (37 files) — no AutoMapper/Mapster: one convention. Errors reach the client as `new { error = … }` (683 controller sites) plus 31 exception types funnelled through `GlobalExceptionHandler.cs`. `RentalCommand.Engine` is 19 workers over one `EngineWorkerBase` (330 lines), each worker 21–28 lines. Core carries 167 interface declarations, of which 141 have exactly one implementation.

## Top changes, ranked by simplification-per-effort

### 1. Delete the 60-odd dead `int portfolioId` read overloads from the domain services
- Where: `RentalCommand.Api/Services/Domain/ILoanService.cs:13,15,17,27`; `ITenantService.cs:29-31`; `IWorkOrderService.cs:32,33,39`; `IAppointmentService.cs:32-34`; `IExpenseService.cs:19,22,32`; `LoanService.cs:28,41,122,199`; 23 interface files in total.
- Evidence: Verified. `rg -c 'WorkspaceReadScope scope' RentalCommand.Api/Services/Domain` → 711; `rg -c '\(int portfolioId,|\(\s*int portfolioId\b'` on the same folder → 133. 23 interface files declare *both* shapes. `rg -n '(ListAsync|ListPageAsync|GetAsync)\(\s*GetPortfolioId\(\)' RentalCommand.Api/Controllers` → **2 hits only** (`PortfolioController.cs:41`, `UnitController.cs:135`). `LoanController.cs` read in full: all 8 service calls use `GetWorkspaceReadScope()`; the four `int portfolioId` members of `ILoanService` have no production caller.
- Why: Every list/get endpoint exists twice — once scoped by a bare portfolio id, once by the capability-bearing `WorkspaceReadScope` that produces the authorized SQL. The int overload skips `WhereAuthorized`, so a future caller picking the wrong one silently widens access.
- Change: Delete the `int portfolioId` `ListAsync`/`ListPageAsync`/`GetAsync` overloads from the 23 interfaces and their implementations. Convert the 2 remaining controller sites to the scope overload.
- Size/risk: ~46 files, ~700 lines removed; medium (narrows the read authorization contract). One PR: yes, large mechanical.

### 2. Delete `IRequestWriteExecutor` and `IJobStepWriteExecutor`; call `IWriteExecutor` directly
- Where: `RentalCommand.Api/Writes/RequestWriteExecutor.cs:1-51`, `RentalCommand.Engine/Writes/JobStepWriteExecutor.cs:1-36`, `RentalCommand.Data/Atomic/WriteExecutor.cs:11-29`, `RentalCommand.Core/Atomic/AtomicCommandContracts.cs:12-20`.
- Evidence: Verified — read all four. `RequestWriteExecutor.ExecuteAsync` (27-43) and `JobStepWriteExecutor.ExecuteAsync` (19-34) are byte-identical apart from the parameter name and error string: trim the key, reject empty or >200 chars, forward. `WriteExecutor` re-validates (line 20) before calling `AtomicTransactionRunner`.
- Why: Three interfaces and three classes wrap one call; two are the same class written twice for two hosts. The only real behaviour is one length check, duplicated.
- Change: Move the 200-char key check into `WriteExecutor.ExecuteAsync`. Delete both adapter files and their interfaces; consumers take `IWriteExecutor`.
- Size/risk: 2 files deleted, ~90 consumer files touched by a type rename, ~250 lines net removed; medium. One PR: yes.

### 3. Merge `ExecuteExactAsync` into `ExecuteAsync` and delete the throwing default interface method
- Where: `RentalCommand.Api/Writes/RequestWriteExecutor.cs:15-21` (default body throws `NotSupportedException`) and `:45-50`.
- Evidence: Verified. `rg -n '\.ExecuteExactAsync' RentalCommand.Api RentalCommand.Engine` → 50 production call sites (14 in `InspectionService.cs` alone). The sole production implementation forwards to `executor.ExecuteAsync`; only test doubles inherit the default.
- Why: Two methods reach the same executor, differing only in whether the key gets trimmed and length-checked. The "legacy exact key" distinction has no second implementation.
- Change: Delete `ExecuteExactAsync`; point the 50 sites at `ExecuteAsync`. First confirm no operation key exceeds 200 chars.
- Size/risk: ~25 files, ~60 lines; medium (idempotency keys are load-bearing). One PR: yes, with #2.

### 4. Rename the 35 `*Handler(s).cs` files in `RentalCommand.Data` to match the `*Rule` classes inside them
- Where: `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs` (13 `Rule` classes, 0 `Handler`), `Payments/ProviderPaymentCommandHandlers.cs` (10/0), `Banking/BankingPersistenceCommandHandlers.cs` (8/0), `Authorization/WorkspaceTeamAuthorityCommandHandlers.cs` (6/0), `Screening/ScreeningCommandHandlers.cs` (6/0), … 35 files total.
- Evidence: Verified. Class-name suffix histogram in `RentalCommand.Data`: `Rule` 172, `Persistence` 26, `Store` 12, `Writer` 3, `Handler` 2. 34 of 35 `*Handler*.cs` files contain zero Handler classes; the exception is `Operations/WorkOperationMutationHandlers.cs` (`CreateWorkOrderHandler` :14, `AddStaffWorkOrderCommentHandler` :24). Commit `1c91775c` renamed the classes and left the filenames.
- Why: Twelve file-name suffixes describe one concept. Grepping for `Rule` misses a third of the write kernel.
- Change: `git mv` each to `<Aggregate>Rules.cs`. Rename the two straggler `Handler` classes to `…Rule`.
- Size/risk: 35 renames + 2 class renames; low. One PR: yes — cheapest item.

### 5. Delete `LeaseManagementReadContext`; use `WorkspaceReadScope`
- Where: `RentalCommand.Api/Services/Domain/ILeaseManagementQueryService.cs:75-80`, `RentalCommand.Core/Authorization/WorkspaceReadScope.cs:8-13`, `RentalCommand.Api/Controllers/ManagementControllerBase.cs:60-72`.
- Evidence: Verified — field-for-field identical `(int PortfolioId, int UserId, Guid SessionId, int AccessContextId, long AccessRevision)` readonly record structs. 77 refs across 9 files.
- Why: One workspace-read coordinate type declared twice, so `ManagementControllerBase` needs two near-identical builders (`TryReadWorkspaceScope` :27 and `TryReadAccessContext` :60).
- Change: Delete `LeaseManagementReadContext` and `TryReadAccessContext`; replace all 77 references.
- Size/risk: 9 files, ~80 lines; low. One PR: yes.

### 6. Delete `WriteEntryPointAttribute` and `WriteEntryPointKind`
- Where: `RentalCommand.Core/Atomic/AtomicCommandContracts.cs:27-40`; 14 applications (e.g. `Data/Atomic/WriteExecutor.cs:10`, `Api/Writes/RequestWriteExecutor.cs:24`, `OwnerEntityService.cs:44,60,77`, `VendorService.cs:93,110,128`).
- Evidence: Verified. `rg -n 'WriteEntryPointAttribute|GetCustomAttribute.*WriteEntryPoint|typeof\(WriteEntryPoint'` → one hit, the declaration. Enum has one member. The doc comment describes distinguishing "the compatibility shell" removed in `490a3c94`.
- Why: A migration marker that outlived its migration; nothing reads it.
- Change: Delete attribute, enum, and all 14 applications.
- Size/risk: 8 files, ~30 lines; low. One PR: yes, with #7.

### 7. Delete the single-valued `WriteIdempotencyPolicy` enum
- Where: `RentalCommand.Core/Atomic/AtomicCommandContracts.cs:22-25`; enforced `RentalCommand.Data/Atomic/WriteExecutor.cs:22-25`; supplied at ~85 sites.
- Evidence: Verified. One member, `Required`. Every sampled site passes `.Required`. `WriteExecutor.cs:22` throws if anything else — unreachable.
- Why: A configuration point with one legal value threaded through every `TransactionalWrite`.
- Change: Remove the `IdempotencyPolicy` member from `TransactionalWrite`, delete enum and guard, drop the argument everywhere.
- Size/risk: ~50 files, ~85 lines removed; low (compile-checked). One PR: yes.

### 8. Make `IRequestWriteExecutor` a required constructor dependency and delete the 61 `RequireWrites()` calls
- Where: `UnitService.cs:26,34,1230`; `WorkOrderService.cs:27,36,581`; `EvictionCaseService.cs:23,26,175`; `NotificationService.cs:22,27,256`; 13 services.
- Evidence: Verified. `rg -c 'IRequestWriteExecutor\? '` → 27; `rg -c 'RequireWrites'` → 61. Twelve bodies are `_writes ?? throw`; `NotificationService.cs:256` is a no-op forwarder on a non-nullable field. All services are registered with the executor available.
- Why: A dependency that is always present is declared optional then re-required at each use with a runtime throw, 61 times.
- Change: Make the parameters and fields non-nullable; delete the 13 `RequireWrites()` methods; inline `_writes`. Tests pass a mock (pattern already in `LoanServiceTests.cs:30`).
- Size/risk: ~13 prod files plus tests, ~90 lines removed; medium. One PR: yes.

### 9. Delete `IPaymentProvider` and `PaymentResult`
- Where: `RentalCommand.Core/Interfaces/IPaymentProvider.cs:1-33`.
- Evidence: Verified. Exactly one reference in the repo — the declaration. Doc comment: "Phase 0 defines the contract only." The real integration is `StripePaymentService : IStripePaymentService`, not this.
- Change: Delete the file.
- Size/risk: 1 file, 33 lines; low. One PR: yes, with #10.

### 10. Delete the `NotificationsConfig.EnableNoticeAutopilot` flag
- Where: `RentalCommand.Core/Configuration/NotificationsConfig.cs:10`.
- Evidence: Verified. 4 hits: the declaration and three prose mentions in `Docs/`. `NoticeDraftWorker.cs` (27 lines) calls `GenerateAllAsync` unconditionally — despite two docs stating the worker is gated by this flag.
- Why: A false safety switch nothing reads.
- Change: Delete the property; correct the two doc lines.
- Size/risk: 1 code file, 4 lines; low.

### 11. Normalise the 22 stray controller error shapes onto `new { error = … }`
- Evidence: Verified via `rg -c` over Controllers: `new { error =` 683; `new { message =` 7; `BadRequest("` 14; `Problem(` 1; `ValidationProblem(` 1; bodyless `NotFound()` 29.
- Why: 683 endpoints agree on one envelope and ~22 disagree; a bare `BadRequest("…")` serialises as a JSON string.
- Change: Rewrite the 22 outliers as `new { error = "…" }`. Leave bodyless `NotFound()`.
- Size/risk: ~15 files, ~25 lines; low but client-visible — grep clients for `.message` first. One PR: yes.

### 12. Collapse `WhereMoneyAuthorized` / `WhereAccountingAuthorized` / `WhereManagementAuthorized` into `WhereAuthorized` overloads
- Where: `RentalCommand.Data/Authorization/WorkspaceMoneyAuthorizationQuery.cs:10,32,49,58,72`; `AccountingAuthorizationQuery.cs:14`; `WorkspaceAuthorizationQuery.cs`; `ScanDraftAuthorizationQuery.cs`.
- Evidence: Verified. Name histogram: `WhereAuthorized` 8, `WhereMoneyAuthorized` 5, `WhereManagementAuthorized` 2, `WhereAuthorizedForScope` 2, `WhereAccountingAuthorized` 1, `…ForReview` 1, `…ForReports` 1 — 20 methods under 7 names. Every one opens with the same two lines then differs only by entity fallback chain.
- Why: All 20 answer "restrict this entity to the properties this scope can read under capability X"; distinguished by caller's domain, not contract.
- Change: Rename every variant to `WhereAuthorized` (overload resolution by `IQueryable<T>`); keep the differently-shaped ones only if their parameter lists actually differ.
- Size/risk: 5 definition files + ~40 call sites; medium (tenant-isolation boundary — read the diff line by line). One PR: yes.

### 13. Keep one SMS provider; delete the other three
- Where: `RentalCommand.Api/Services/Sms/{Twilio,SignalWire,Telnyx,Vonage}SmsProvider.cs`; `Extensions/SmsProviderRegistration.cs:17-20`; four `*Options` classes in `NotificationsConfig.cs`; `deploy/docker-compose.prod.yml:197-208`.
- Evidence: Verified — 458 lines in `Services/Sms`; four `AddHttpClient<ISmsProvider, …>` registrations; only SignalWire and Twilio blocks exist in appsettings and both are empty; 13 compose env vars.
- Why: Four interchangeable vendors for a product with one landlord. Only one can be active.
- Change: Pick one (SignalWire is named first in compose and appsettings). Delete the other three. `SmsDispatcher` and `ISmsProvider` survive.
- Size/risk: ~8 files, ~200 lines; medium (deployment config; owner must confirm which vendor is in use). One PR: yes, once confirmed.

## Patterns worth a single rule
1. When a write-path vocabulary is unified, rename the files in the same PR.
2. A migration marker is deleted by the PR that finishes the migration.
3. No enum, attribute, or config property ships with one legal value or zero readers.
4. Adding an overload that widens or narrows authorization means deleting the other one.
5. No `IFoo? foo = null` constructor parameters for services that are always registered.

## Looked at and left alone
- `FoundationBaselinePostgreSql.cs` (3,702 lines) — raw-SQL baseline assembled from named per-feature constants. Long by necessity.
- `EngineWorkerBase` (330 lines) with 19 subclasses averaging 25 lines — earning its keep.
- The 13 `Worker` + `Service` + `IService` trios in Engine — real test seams.
- `Simulation` + `Sandbox` (~1,800 lines) — a shipped capability.
- Per-entity `ApplySort` methods (9) — each names its own entity's columns; `ListQuery` already carries the shared parsing.

## Not covered
- The 106 `*Service.cs` files in `Services/Domain` surveyed by size/shape, not read — `ReportsService.cs` (3,027), `ScanService.cs` (2,471), `DemoDataSeeder.cs` (1,853), `TenantAccountQueryService.cs` (1,809), `AccountingLedgerReadModelService.cs` (1,784) may hold internal copy-paste.
- `RentalCommand.Data`'s `Persistence`/`Store`/`Writer`/`Projections` split (26/12/3/6 files) — fork confirmed, dominant form not determined.
- The 31 exception types and how `GlobalExceptionHandler.cs` maps them.
- `Api/Scanning/ScanService.cs` vs `Engine/Workers/ScanProcessingWorker.cs` (3,400 lines combined) — flagged, not opened.
