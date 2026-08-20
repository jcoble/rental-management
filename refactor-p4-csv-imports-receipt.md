# refactor-p4-csv-imports receipt

## Fix round 1

Scope: test-only fixes for the two adversarial-review findings. Production code was not changed.

The Core and Unit row expectations were taken from the legacy `CsvImportService` result mapping and the untouched PostgreSQL persistence result shapes, then exercised against the live persistence path. Persisted IDs from the test database are used to freeze the created and related identifier contracts without inventing sequence values.

### Verification receipts

`MSBUILDDISABLENODEREUSE=1 dotnet build --no-restore`

```text
Build succeeded.
    26 Warning(s)
    0 Error(s)
Time Elapsed 00:00:02.94
```

`MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~AtomicUnitOfWorkTests|FullyQualifiedName~WriteExecutorLockOrder|FullyQualifiedName~Phase1WriteExecutorParityCanary|FullyQualifiedName~AtomicCsvImportAlias|FullyQualifiedName~CsvImportWriteExecutorPostgreSql'`

```text
Passed!  - Failed:     0, Passed:    37, Skipped:     0, Total:    37, Duration: 1 m 59 s - RentalCommand.IntegrationTests.dll (net10.0)
```

`MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-build`

```text
Passed!  - Failed:     0, Passed:  1427, Skipped:     0, Total:  1427, Duration: 15 m 4 s - RentalCommand.Api.Tests.dll (net10.0)
```

`MSBUILDDISABLENODEREUSE=1 dotnet build-server shutdown`

```text
Shutting down MSBuild server...
Shutting down VB/C# compiler server...
VB/C# compiler server shut down successfully.
MSBuild server shut down successfully.
```

`git diff --check`: exit 0 with no output.
