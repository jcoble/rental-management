# Bug-f fix receipt

- Lane: `bug-f`
- Started: `2026-08-25T20:36:52-04:00`

## Findings

- #14 — FIXED in `e05edcfafc0e1027041ddca509c60e5621ff817e`.
  - API test: `UpdateAsync_ClearCostFlags_NullExistingCosts` — passed.
  - Mobile test: `work order edit sends a flag when an existing cost is cleared` — passed.
- #8 — FIXED in `7fc5ff0dced0467d81f2b196eb2b4bdf52fc1f3d`.
  - API test: `UpdateAuthorizedAsync_ClearContactFlags_NullExistingContactFields` — passed.
  - Mobile test: `tenant edit sends flags for cleared contact fields` — passed.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln -c Debug --nologo -v q` — exit 0; 82 warnings, 0 errors.
- `dotnet test RentalCommand.Api.Tests --no-build --nologo --filter "FullyQualifiedName~WorkOrder|FullyQualifiedName~Tenant"` — exit 1; 262 passed, 2 known pre-existing failures:
  - `InspectionChecklistServiceTests.Complete_UsesBusinessClockForCompletionWorkOrdersAndReport`
  - `RecurringTenantChargeAtomicPostgreSqlTests.Create_DerivesTenantDimensions_AndWritesAuditAndOutbox`
- `dotnet build-server shutdown` — exit 0; both compiler servers stopped successfully.
- `flutter analyze` — exit 1; exactly 23 pre-existing issues, none in touched files.
- `flutter test` — exit 0; 530 tests passed.
