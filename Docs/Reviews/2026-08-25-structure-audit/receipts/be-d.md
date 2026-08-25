# Lane receipt: be-d

- Start time: 2026-08-25T17:25:48-04:00
- Worktree: `/home/blackcolours/dev/work/worktrees/rental-management/simplify-be-d`
- Branch: `tsk-1018-be-d`
- Scope: one-implementation resolvers, controller error-shape stragglers, authorization method names

## Orientation note

The selected orientation skills reference the sister EdiPlatform paths, which are not present in this Rental Command worktree. Rental Command root `CLAUDE.md`, root `AGENTS.md`, and the source tree are authoritative for this lane.

## Completion log

- Item 1 — accounting provider resolver removal: commit `f467aab6`; static verification `git diff --cached --check` exited 0 and the scoped `rg` sweep found no `AccountingProviderResolver` references before commit.
- Item 2 — listing adapter resolver removal: commit `f0ed98cb`; static verification `git diff --cached --check` exited 0 and the scoped `rg` sweep found no listing resolver references before commit.
- Item 3 — controller error-shape normalization: commits `74b0bd4c`, `4e868b37`; the scoped controller `rg` sweep produced no `new { message =`, bare `BadRequest("`, `Problem(`, or `ValidationProblem(` matches before commit. The follow-up commit fixes the mapper return-type conversion found by the first solution build.
- Item 4 — authorization extension names: commit `cf8c2c2a`; `git diff --cached --check` exited 0, and `git diff --stat -- RentalCommand.Data/Authorization` showed only the seven definition-name replacements.

## Authorization rename receipt

- `WhereAccountingAuthorized` → `WhereAuthorized` — `T = JournalLine`.
- `WhereAuthorizedForReview` → `WhereAuthorized` — `T = ScanDraft`.
- `WhereMoneyAuthorized` → `WhereAuthorized` — `T = Expense`.
- `WhereMoneyAuthorized` → `WhereAuthorized` — `T = RecurringExpense`.
- `WhereMoneyAuthorized` → `WhereAuthorized` — `T = Loan`.
- `WhereMoneyAuthorized` → `WhereAuthorized` — `T = OwnerDistribution`.
- `WhereMoneyAuthorized` → `WhereAuthorized` — `T = OwnerContribution`.
- `WhereAuthorizedForReports` → `WhereAuthorized` — `T = AtomicAuditLog`.
- `WhereAuthorizedForScope` was not renamed for either overload — `T = Property`; both signatures collide with existing `WhereAuthorized` overloads (one `string` capability parameter and one `IReadOnlyCollection<string>` capability parameter).

## Verification log

- Required solution build first attempt: exit 1; compiler error was `RentalCommand.Api/Controllers/SignController.cs(54,20): error CS0029: Cannot implicitly convert type 'Microsoft.AspNetCore.Mvc.ActionResult<(System.IO.Stream Stream, string FileName, string ContentType)>' to 'Microsoft.AspNetCore.Mvc.IActionResult'`.
- Required solution build after `4e868b37`: exit 0; tail was `82 Warning(s)`, `0 Error(s)`, `Time Elapsed: 00:01:11.19`.
- Required API tests: exit 1; `Failed!  - Failed:    11, Passed:  1423, Skipped:     0, Total:  1434, Duration: 4 m 40 s - RentalCommand.Api.Tests.dll (net10.0)`.
- Additional diagnostic rerun of the first API failure: exit 1; `VendorDispatchServiceTests.cs(315,0)` reported expected `2026-08-25 21:45:44.7104199` but PostgreSQL returned `2026-08-25 21:45:44.710419`.
- Required Data tests: exit 0; `Failed:     0, Passed:   125, Skipped:     0, Total:   125, Duration: 6 s`.
- Required resolver/error sweep: exit 1 with no output, the expected `rg` no-match status.
- Required `dotnet build-server shutdown`: exit 0; MSBuild and VB/C# compiler servers shut down successfully.

## Tests changed

- `RentalCommand.Api.Tests/Auth/AuthControllerResendVerificationTests.cs` — reflection assertion follows the requested `error` response property.
- `RentalCommand.Api.Tests/Domain/ListingWorkspaceServiceTests.cs` — substitutes the adapter directly.
- `RentalCommand.IntegrationTests/ListingMetadataCrudWritePostgreSqlTests.cs` — registers the adapter directly.
- `RentalCommand.Api.Tests/Domain/AccountingConnectionServiceTests.cs`, `RentalCommand.Api.Tests/Domain/AccountingTokenServiceTests.cs`, `RentalCommand.IntegrationTests/AccountingConnectionClaimStoreTests.cs`, and `RentalCommand.IntegrationTests/AccountingWriteExecutorTests.cs` — construct/register `IAccountingProvider` directly.
- `RentalCommand.Api.Tests/Domain/OwnerEntityServiceListTests.cs`, `RentalCommand.Api.Tests/Scanning/ScanRetryControllerPostgreSqlTests.cs`, and `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs` — update renamed authorization extension calls only.
