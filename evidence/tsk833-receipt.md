# TSK-833 evidence receipt

## Fix round 2

The implementation for this round is committed as `9589538771fe05940206995b76c09b579a5ba796` (`Fix TSK-833 review blockers`). The five earlier item commits remain intact: `651beacdd853f8e811ebe2f951236f877a025249`, `091035a846169424a4dd3fdec6488bd3ace727aa`, `81be76c08110d8d58b24910c901912d5c4ccbd23`, `34fb0f89588e0d697380e6a6bdd451ed23b48e13`, and `f4e04e7e9aca697c8d94be8150dc88896c05c570` (`git log --format='%H %s' --reverse origin/main..HEAD`).

### Finding 1 — receipt

Original commit: `651beacdd853f8e811ebe2f951236f877a025249`. Fix-round implementation commit: `9589538771fe05940206995b76c09b579a5ba796`. Receipt introduction commit: `d919b71a9ae480aaeb2487639dbd2366f2a0c673`.

Exact satisfy-text: `Add evidence/tsk833-receipt.md with per-item commit SHA, production file:line proof, defect-catching test file:line proof, and explicit evidence for every item claimed already implemented.`

This file is the requested receipt at `evidence/tsk833-receipt.md:1`. The per-item commit map, production proof, defect-catching test proof, and retained-item proof are recorded below.

### Finding 2 — unit-creation ingress values

Original commit: `34fb0f89588e0d697380e6a6bdd451ed23b48e13`. Fix-round commit: `9589538771fe05940206995b76c09b579a5ba796`.

Exact satisfy-text: `Preserve missing bedrooms/bathrooms through every unit-creation ingress and, after resolving the target property in the server transaction, reject omitted values for SingleFamily, MultiFamily, Condo, and Townhome while accepting omissions for Storage, Parking, and Commercial; add defect-catching tests for CSV import and canonical scan confirmation.`

- CSV parsing keeps absent values nullable in `RentalCommand.Api/Services/Import/CsvImportService.cs:350-366`, and the atomic wire records carry nullable bedrooms and bathrooms in `RentalCommand.Core/Import/AtomicCsvImportContracts.cs:6-26`.
- The server-side validation query resolves the target property's type and classifies missing residential values in one SQL statement at `RentalCommand.Data/Import/AtomicUnitImportPersistence.cs:93-143`; the import insert consumes those classified nullable values at `RentalCommand.Data/Import/AtomicUnitImportPersistence.cs:146-166`.
- Canonical scan confirmation validates the resolved property's type before creating a new unit at `RentalCommand.Data/Scanning/CanonicalLeaseScanConfirmationWriter.cs:548-563`, with the residential rule at `RentalCommand.Data/Scanning/CanonicalLeaseScanConfirmationWriter.cs:587-598`.
- The service ingress regression test proves blank CSV cells remain null at `RentalCommand.Api.Tests/Import/CsvImportServiceTests.cs:156-169`.
- PostgreSQL validation covers all four residential types and Storage, Parking, and Commercial at `RentalCommand.IntegrationTests/AtomicCsvImportAliasPostgreSqlTests.cs:79-116`.
- Canonical scan tests cover missing residential values and non-residential acceptance at `RentalCommand.IntegrationTests/ProductionScanConfirmationTargetWriterTests.cs:464-529` and `RentalCommand.IntegrationTests/ProductionScanConfirmationTargetWriterTests.cs:422-462`.

### Finding 3 — self-owner race and reuse

Original commit: `81be76c08110d8d58b24910c901912d5c4ccbd23`. Fix-round commit: `9589538771fe05940206995b76c09b579a5ba796`.

Exact satisfy-text: `Make self-owner creation concurrency-safe inside the property setup transaction with a database-enforced at-most-one active primary owner invariant plus a serialized/retry-safe get-or-create path; add tests proving an existing self-owner is reused without duplication and concurrent first-property setup cannot create two active primary owners.`

- Property setup still uses the requested ownership list as authority and only defaults when it is empty at `RentalCommand.Api/Services/Domain/AtomicCoreCrudMutation.cs:238-247`.
- The get-or-create path takes a transaction-scoped `PortfolioPrimaryOwner` advisory lock, reads one active primary owner, and reuses it before inserting at `RentalCommand.Api/Services/Domain/AtomicCoreCrudMutation.cs:491-509`; the owner insert remains inside the same atomic transaction at `RentalCommand.Api/Services/Domain/AtomicCoreCrudMutation.cs:520-549`.
- The database invariant is the filtered unique index at `RentalCommand.Data/RentalCommandDbContext.cs:340-345`, with the PostgreSQL migration at `RentalCommand.Data/Migrations/20260810135320_EnforceActivePrimaryOwner.cs:11-23`.
- The PostgreSQL test helper uses Npgsql-backed core services at `RentalCommand.Api.Tests/Domain/AtomicDomainTestKernel.cs:175-188`.
- Existing-owner reuse and no-duplication are proved at `RentalCommand.Api.Tests/Domain/PropertySetupAtomicCommandTests.cs:91-117`; concurrent first-property setup and the active-owner count are proved against PostgreSQL at `RentalCommand.Api.Tests/Domain/PropertySetupAtomicCommandTests.cs:148-191`.

### Finding 4 — form and setup regression coverage

Original commit: `34fb0f89588e0d697380e6a6bdd451ed23b48e13`. Fix-round commit: `9589538771fe05940206995b76c09b579a5ba796`.

Exact satisfy-text: `Add a form-level test proving beds/baths are shown and required for residential dwelling types and hidden for Storage, Parking, and Commercial, plus a property-setup server test proving omitted residential beds/baths are rejected without writes.`

- The shared form renders beds and baths only when `showBedBath` is true and marks both inputs required at `web/src/lib/components/forms/UnitFields.svelte:37-69`.
- The form-level test proves visibility and requiredness for SingleFamily, MultiFamily, Condo, and Townhome, and absence for Storage, Parking, and Commercial at `web/component-tests/unit-fields.test.ts:19-65`.
- The property-setup server test submits omitted residential values, expects the residential validation error, and proves zero properties, units, and setup receipts at `RentalCommand.Api.Tests/Domain/PropertySetupAtomicCommandTests.cs:213-229`.
- Direct create/update validation remains covered at `RentalCommand.Api/Services/Domain/AtomicRentalMutation.cs:121-160` and `RentalCommand.Api/Services/Domain/AtomicRentalMutation.cs:202-211`, with tests at `RentalCommand.Api.Tests/Domain/UnitServiceCreateTests.cs:80-127`.

### Finding 5 — web registration wire contract

Original commit: `651beacdd853f8e811ebe2f951236f877a025249`. Fix-round commit: `9589538771fe05940206995b76c09b579a5ba796`.

Exact satisfy-text: `Add required termsPrivacyAccepted: boolean to the web RegisterRequest contract and add or extend a contract test so the web registration wire type cannot drift from the API request again.`

- The web request now requires `termsPrivacyAccepted: boolean` at `web/src/lib/types/user.ts:76-82`.
- The API request remains required and nullable only for model binding at `RentalCommand.Api/DTOs/AuthDtos.cs:24-42`.
- The contract test constructs the typed request and checks both source declarations at `web/src/lib/types/user-contract.test.ts:12-29`.

## Retained evidence for items already implemented

Every item marked satisfactory by the review remains evidenced here:

- Registration rejection, durable consent fields, the registration-only checkbox, and persistence/rejection tests are at `RentalCommand.Api/Services/Auth/AuthService.cs:270-289`, `RentalCommand.Data/Auth/AccountSecurityCommandHandlers.cs:32-54`, `web/src/routes/register/+page.svelte:113-127`, and `RentalCommand.Api.Tests/Auth/CanonicalRegistrationBootstrapTests.cs:114-128` plus `RentalCommand.Api.Tests/Auth/CanonicalRegistrationBootstrapTests.cs:201-223`.
- Appointment DTO and command validation reject equal or reversed ranges at `RentalCommand.Api/DTOs/AppointmentDtos.cs:147-154`, `RentalCommand.Api/DTOs/AppointmentDtos.cs:203-210`, `RentalCommand.Data/Operations/AppointmentOperationMutationHandlers.cs:32-37`, `RentalCommand.Data/Operations/AppointmentOperationMutationHandlers.cs:144-145`, and `RentalCommand.Data/Operations/AppointmentOperationMutationHandlers.cs:504`; shared form copy is at `web/src/lib/schemas/index.ts:501-511`; API/command tests are at `RentalCommand.Api.Tests/Domain/AppointmentDtoValidationTests.cs:9-75` and `RentalCommand.Api.Tests/Domain/AppointmentMutationAuthorizationTests.cs:126-155`.
- Explicit property ownership remains authoritative at `RentalCommand.Api/Services/Domain/AtomicCoreCrudMutation.cs:241-247`; new-owner and explicit-owner behavior is covered at `RentalCommand.Api.Tests/Domain/PropertySetupAtomicCommandTests.cs:74-88` and `RentalCommand.Api.Tests/Domain/PropertySetupAtomicCommandTests.cs:119-145`.
- Direct unit create/update validation and the non-residential setup acceptance branch are at `RentalCommand.Api/Services/Domain/AtomicRentalMutation.cs:121-160`, `RentalCommand.Api/Services/Domain/AtomicRentalMutation.cs:202-211`, and `RentalCommand.Api.Tests/Domain/PropertySetupAtomicCommandTests.cs:194-211`.
- Dashboard lease-agreement facts carry the owning lease-management and unit identifiers at `RentalCommand.Api/Services/Domain/DashboardService.cs:833-850`; destinations use the existing record-link contract at `web/src/lib/navigation/dashboard-activity-href.ts:17-29`; both required destinations and URL resolution are tested at `web/src/routes/(protected)/dashboard-activity-deeplinks.test.ts:18-46`.

## Verification commands

- `pnpm --dir web test:unit` — 886 passed, 0 failed (`ℹ tests 886`, `ℹ pass 886`).
- `pnpm --dir web test:components` — 11 files, 43 tests passed, 0 failed.
- `pnpm --dir web check:native` — exit 0.
- `pnpm --dir web check` — 5,630 files, 0 errors, 18 warnings.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~PropertySetupAtomicCommandTests|FullyQualifiedName~CsvImportServiceTests" --no-restore --logger "console;verbosity=minimal"` — 29 passed, 0 failed.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter "FullyQualifiedName~AtomicCsvImportAliasPostgreSqlTests.UnitPreview_PreservesMissingBedsAndBathsThroughPropertyTypeValidation|FullyQualifiedName~ProductionScanConfirmationTargetWriterTests.GuidedSetupManualLease_AllowsMissingBedsAndBathsForNonResidentialHomes|FullyQualifiedName~ProductionScanConfirmationTargetWriterTests.GuidedSetupManualLease_InvalidInput_IsRejectedBeforeAnyBusinessFlush" --no-restore --logger "console;verbosity=minimal"` — 27 passed, 0 failed against PostgreSQL.
- `dotnet build-server shutdown` — MSBuild and compiler servers shut down successfully.
- `git diff --check` — no whitespace errors before the implementation commit; the staged check also passed before commit (`git diff --cached --check`).

The class-wide scan-writer exploratory command was not used as acceptance: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter "FullyQualifiedName~AtomicCsvImportAliasPostgreSqlTests|FullyQualifiedName~ProductionScanConfirmationTargetWriterTests" --no-restore --logger "console;verbosity=minimal"` reported 63 passed and 13 failures in pre-existing unrelated paths, including the out-of-scope helper update at `RentalCommand.IntegrationTests/ProductionScanConfirmationTargetWriterTests.cs:3234` and the existing payment view dependency at `RentalCommand.Data/Payments/TenantMoneyCommandHandlers.cs:182`. The exact new PostgreSQL cases above are green.
