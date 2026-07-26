# TSK-750 Step 5 DEP-PERF-02 — authorization SQL discovery brief

**Discovery verdict:** ROADMAP DISCOVERY READY  
**Active goal:** TSK-750 mobile UI rescue  
**Replacement boundary:** DEP-PERF-02 authorization SQL assertions  
**Prior checkpoint:** Step 5 measurement attempt 1 stopped fail-closed after
13 of 18 `TenantAccountQueryServiceSqlTests` failed at stale shared assertions;
5 passed and no later measurement ran.  
**Planning retry:** 1 of 3

## Goal

Align the Tenant Account generated-SQL assertions with the accepted opaque
PostgreSQL capability-scope function while continuing to prove authorization is
DB-side. All 18 existing Tenant Account SQL tests must pass without changing
production authorization or query behavior.

## Observable acceptance

1. **AUTH-SQL-01:** The unchanged 18-test
   `TenantAccountQueryServiceSqlTests` suite passes.
2. **AUTH-SQL-02:** Each of the 13 previously blocked generated statements
   contains exactly one
   `public.rc_api_effective_capability_scopes(` call.
3. **AUTH-SQL-03:** Balance reads retain `money.balances.read` and the existing
   session coordinate passed to the function.
4. **AUTH-SQL-04:** Deposit reads retain both
   `money.deposits.manage` and `leasing.deposits.read`, retain the existing
   session coordinate, exclude `money.balances.read`, and contain no `UNION`.
5. **AUTH-SQL-05:** No translated statement inlines `AuthSessions`,
   `RoleProfileCapabilities`, `MembershipRoleAssignments`, or
   `MembershipRoleAssignmentProperties`.
6. **AUTH-SQL-06:** The unchanged focused PostgreSQL tests for function
   semantics, fail-closed behavior, API-only ACL, and
   authorization-before-ordering/paging pass.
7. **AUTH-SQL-07:** The diff changes exactly
   `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`;
   production authorization, integration tests, and the currently dirty
   foundation test remain untouched.

## Explicit non-goals

- No edit to `EffectiveCapabilityScopeQuery`, `WorkspaceAuthorizationQuery`,
  `TenantAccountQueryService`, foundation SQL, migrations, RLS, authorization
  roles, production code, or integration fixtures/tests.
- No edit to the currently dirty
  `RentalCommand.Data.Tests/FoundationBaselinePostgreSqlTests.cs`.
- No restoration of legacy inline authorization tables or `AccessRevision`
  text assertions.
- No DEP-PERF-01 or DEP-PERF-03 through DEP-PERF-05 measurement, Azure/API/APK
  identity work, endpoint sampling, emulator proof, performance optimization,
  Step 6, Step 7, broad authorization redesign, or unrelated cleanup.

## Resolved discovery questions

1. **Why did DEP-PERF-02 fail?** Thirteen tests reached unchanged
   `AssertAuthorized` or `AssertDepositAuthorized` assertions that still expect
   the legacy inline authorization graph. Generated SQL now uses the accepted
   opaque function. Evidence: `DEP-PERF-02-BLOCKED`,
   `AUTH-HISTORY-533AB4EC`.
2. **Which authorization shape is current authority?** Commit history and
   accepted PostgreSQL coverage establish
   `public.rc_api_effective_capability_scopes(...)` as the current DB-side
   authorization boundary. Evidence: `AUTH-ACCEPTED-4AD5BE50`,
   `AUTH-PG-SEMANTICS`, `FOUNDATION-TERMINAL`.
3. **Who owns the repair?** Only the shared translation helpers
   `AssertAuthorized` and `AssertDepositAuthorized` in
   `TenantAccountQueryServiceSqlTests.cs` are stale. Existing canonical tests
   already require the opaque call, forbid inline legacy tables, and prove
   authorization precedes ordering and paging. Evidence:
   `AUTH-TRANSLATION-OWNER`.

## Exact execution map

- Modify only
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`,
  specifically `AssertAuthorized` and `AssertDepositAuthorized`.
- Exercise unchanged:
  `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs` and
  `RentalCommand.IntegrationTests/RlsResourceScopePlanRegressionTests.cs`.
- Create no production, test, proof, configuration, migration, or runtime file.

## Exact serial commands

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
  --filter 'FullyQualifiedName~TenantAccountQueryServiceSqlTests'

MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~WorkspaceAuthorizationKernelTests.AuthorizationExistsPrecedesOrderingAndPagingInGeneratedSql|FullyQualifiedName~WorkspaceAuthorizationKernelTests.MoneyAuthorizationRemainsDbSideBeforeOrderingAndPaging|FullyQualifiedName~RlsResourceScopePlanRegressionTests.EffectiveCapabilityScopes_'

dotnet build-server shutdown
```

- Command 1 maps to AUTH-SQL-01 through AUTH-SQL-05.
- Command 2 maps to AUTH-SQL-06.
- `dotnet build-server shutdown` is required serial resource cleanup only; it
  is not acceptance evidence.
- The read-only relevance review of the complete implementation diff maps to
  AUTH-SQL-07. It must explicitly verify that only
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs` changed
  and that its changes are limited to `AssertAuthorized` and
  `AssertDepositAuthorized`.

## Stop condition

Discovery is complete because the stale owner, exact helper symbols, accepted
replacement SQL shape, unchanged verification owners, commands, and non-goals
are resolved. No user decision remains.

## Evidence references

- `DEP-PERF-02-BLOCKED` — attempt 1 failure and stopped measurement.
- `AUTH-HISTORY-533AB4EC` — origin of legacy inline assertions.
- `AUTH-ACCEPTED-4AD5BE50` — accepted opaque-function authorization change.
- `AUTH-PG-SEMANTICS` — PostgreSQL semantics, fail-closed, and ACL acceptance.
- `AUTH-TRANSLATION-OWNER` — canonical generated-SQL assertion owner.
- `FOUNDATION-TERMINAL` — prior focused backend and deployed acceptance.
