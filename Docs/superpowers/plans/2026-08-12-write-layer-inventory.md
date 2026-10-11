# Write-layer static inventory

Date: 2026-08-12
Baseline: `8b9d0747c49c4c82f65214a23140d039e3a05934`

This is the Phase 0 measurement baseline for replacing command-handler ceremony without weakening the transaction kernel. Counts cover production C# under `RentalCommand.Api`, `RentalCommand.Data`, and `RentalCommand.Engine`; tests and migrations are excluded where noted.

## Atomic handlers

- 206 concrete `IAtomicCommandHandler<,>` implementations in 87 files.

```bash
rg -n --glob '*.cs' '^\s*: IAtomicCommandHandler<' RentalCommand.Api RentalCommand.Data RentalCommand.Engine | wc -l
rg -l --glob '*.cs' '^\s*: IAtomicCommandHandler<' RentalCommand.Api RentalCommand.Data RentalCommand.Engine | wc -l
```

The declaration pattern is anchored to the base-type line so generic registrations and handler resolution are not counted as implementations.

## Application dispatches

- 266 application `ExecuteAsync` dispatches in 83 files.

```bash
rg -n --glob '*.cs' '\.ExecuteAsync\(' RentalCommand.Api RentalCommand.Data RentalCommand.Engine | rg -v 'RentalCommand.Data/DependencyInjection/AtomicUnitOfWork.cs|RentalCommand.Api/Controllers/A[i]Controller.cs' | wc -l
rg -l --glob '*.cs' '\.ExecuteAsync\(' RentalCommand.Api RentalCommand.Data RentalCommand.Engine | rg -v 'RentalCommand.Data/DependencyInjection/AtomicUnitOfWork.cs|RentalCommand.Api/Controllers/A[i]Controller.cs' | wc -l
```

The exclusions are the unit-of-work's one internal delegation to the runner and one unrelated action dispatcher.

## Row-mutating raw and set-based surfaces

- 41 production files expose row-mutating raw or set-based SQL surfaces.
- 37 contain literal `INSERT`, `UPDATE`, or `DELETE` statements outside migrations.
- Three additional files use only EF `ExecuteUpdate` or `ExecuteDelete`.
- One additional payment file calls row-mutating database functions through `SELECT`.

```bash
{ rg -l --glob '*.cs' '^\s*(INSERT INTO|UPDATE |DELETE FROM)' RentalCommand.Api RentalCommand.Data RentalCommand.Engine | rg -v '/Migrations/'; rg -l --glob '*.cs' 'Execute(Update|Delete)(Async)?\(' RentalCommand.Api RentalCommand.Data RentalCommand.Engine; rg -l --glob '*.cs' 'rc_(claim_exact_tenant_payment_attempt|transition_tenant_payment_attempt|assert_provider_payment_fence|schedule_tenant_payment_reconciliation)' RentalCommand.Data/Payments; } | sort -u | wc -l
```

This is a file count, not a transaction-owner count: kernel helpers and handler-owned persistence children remain visible because every raw/set-based mutation surface must retain exact table-and-operation admission.

## Raw-write audit perimeter

- 47 production files form the broad audit perimeter.

The perimeter is the 41 row-mutating files plus six intentionally retained non-row candidates: API and Engine database-session setup, advisory locking, the exact-target raw mutation helper, accounting posting lock ownership, and runtime-role provisioning.

```bash
{ rg -l --glob '*.cs' '^\s*(INSERT INTO|UPDATE |DELETE FROM)' RentalCommand.Api RentalCommand.Data RentalCommand.Engine | rg -v '/Migrations/'; rg -l --glob '*.cs' 'Execute(Update|Delete)(Async)?\(' RentalCommand.Api RentalCommand.Data RentalCommand.Engine; rg -l --glob '*.cs' 'rc_(claim_exact_tenant_payment_attempt|transition_tenant_payment_attempt|assert_provider_payment_fence|schedule_tenant_payment_reconciliation)' RentalCommand.Data/Payments; rg --files RentalCommand.Api RentalCommand.Data RentalCommand.Engine | rg '^(RentalCommand.Api/Data/RlsConnectionInterceptor.cs|RentalCommand.Data/Accounting/AccountingPostingService.cs|RentalCommand.Data/Atomic/AtomicDbContextExtensions.cs|RentalCommand.Data/Atomic/AtomicLockingPersistence.cs|RentalCommand.Data/Security/RuntimeDatabaseRoleProvisioner.cs|RentalCommand.Engine/Data/EngineRlsInterceptor.cs)$'; } | sort -u | wc -l
```

The broader perimeter does not claim all 47 files mutate rows. It keeps infrastructure SQL visible during each migration sweep so a simplification cannot accidentally bypass transaction, session, role, or admission enforcement.

## Engine mutation initiators

- 19 Engine initiators can mutate business or queue state: 18 normal-host initiators plus the simulation-only `SimWorkerCommandWorker`.

```bash
{ rg -o 'AddHostedService<[^>]+>' RentalCommand.Engine/EngineHostedServiceRegistration.cs | rg -v 'AdvisoryLockWatcherService|WorkerWatchdogService'; rg -o 'AddSingleton<ScanProcessingWorker>' RentalCommand.Engine/EngineHostedServiceRegistration.cs; } | wc -l
```

`ScanProcessingWorker` uses the explicit singleton-plus-hosted-service registration shape and is therefore counted separately. `AdvisoryLockWatcherService` and `WorkerWatchdogService` monitor state but do not initiate business or queue mutations.
