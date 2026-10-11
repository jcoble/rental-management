# TSK-750 Step 5 DEP-PERF-02 — authorization SQL execution contract

**Goal:** Replace stale generated-SQL assertions with the accepted opaque
authorization-function contract without changing production behavior.  
**Source brief:**
`Docs/superpowers/plans/2026-07-25-tsk-750-step-5-dep-perf-02-authorization-sql-discovery.md`  
**Active goal:** TSK-750 mobile UI rescue  
**Active boundary:** DEP-PERF-02 authorization SQL assertion replacement  
**Plan state:** Ready for execution  
**Planning retry:** 1 of 3

## Contract acceptance

1. **AUTH-SQL-01 — Tenant Account suite:** All 18 unchanged
   `TenantAccountQueryServiceSqlTests` pass.
2. **AUTH-SQL-02 — Opaque DB-side authority:** Every one of the 13 previously
   blocked generated statements contains exactly one
   `public.rc_api_effective_capability_scopes(` call.
3. **AUTH-SQL-03 — Balance capabilities:** Balance reads retain
   `money.balances.read` and the existing session coordinate passed to the
   function.
4. **AUTH-SQL-04 — Deposit capabilities:** Deposit reads retain
   `money.deposits.manage` and `leasing.deposits.read`, retain the existing
   session coordinate, exclude `money.balances.read`, and contain no `UNION`.
5. **AUTH-SQL-05 — No inline legacy graph:** Generated statements do not inline
   `AuthSessions`, `RoleProfileCapabilities`, `MembershipRoleAssignments`, or
   `MembershipRoleAssignmentProperties`.
6. **AUTH-SQL-06 — Existing PostgreSQL authority:** Unchanged focused tests for
   authorization-before-ordering/paging, money authorization, function
   semantics, fail-closed behavior, and API-only ACL pass.
7. **AUTH-SQL-07 — One-file scope:** The only changed file is
   `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`.
   Production authorization and queries remain untouched.

## Explicit non-goals

- No edit to `EffectiveCapabilityScopeQuery`, `WorkspaceAuthorizationQuery`,
  `TenantAccountQueryService`, foundation SQL, migrations, RLS, authorization
  roles, production code, integration tests, or fixtures.
- No edit to
  `RentalCommand.Data.Tests/FoundationBaselinePostgreSqlTests.cs`.
- No legacy inline-table or `AccessRevision` expectations.
- No new helper abstraction outside the two existing assertion helpers, broad
  test, compatibility lane, fallback, migration, hardening, or hypothetical
  edge-case requirement.
- No DEP-PERF-01 or DEP-PERF-03 through DEP-PERF-05 measurement, Azure/API/APK
  identity work, endpoint sampling, emulator work, performance optimization,
  Step 6, or Step 7.

## UI proof

- **UI impact:** No. This contract changes translation assertions only.
- **Supported flow:** Existing Tenant Account balance, ledger, charge, deposit,
  and move-out read SQL is translated with opaque DB-side authorization before
  ordering and paging.
- **Required target:** Focused API and PostgreSQL test suites only.
- **Proof artifacts:** Command output only; no proof file is created.

## Step 1 — Replace stale authorization SQL assertions

**Status:** Active  
**Acceptance:** AUTH-SQL-01 through AUTH-SQL-07

### Allowed files

- Modify only:
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`
  — `AssertAuthorized` and `AssertDepositAuthorized`.
- Exercise unchanged:
  `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs` and
  `RentalCommand.IntegrationTests/RlsResourceScopePlanRegressionTests.cs`.

No other file may be created or modified.

### Required actions

1. In `AssertAuthorized`, replace legacy table and `AccessRevision` text
   expectations with assertions that the generated statement contains exactly
   one `public.rc_api_effective_capability_scopes(` call, retains the
   endpoint-owned capability keys and existing session coordinate, and does not
   inline `AuthSessions`, `RoleProfileCapabilities`,
   `MembershipRoleAssignments`, or
   `MembershipRoleAssignmentProperties`.
2. In `AssertDepositAuthorized`, enforce the same exactly-one-function-call,
   session-coordinate, and no-inline-legacy-graph contract while retaining the
   existing deposit-specific requirements: both
   `money.deposits.manage` and `leasing.deposits.read`, no
   `money.balances.read`, and no `UNION`.
3. Do not modify production or integration code/tests. Run the two focused test
   commands serially, then shut down build servers.
4. Run a read-only relevance review of the complete implementation diff against
   AUTH-SQL-01 through AUTH-SQL-07. For AUTH-SQL-07, explicitly verify that only
   `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs` changed
   and that its changes are limited to `AssertAuthorized` and
   `AssertDepositAuthorized`; require `RELEVANCE PASS`.

### Targeted commands

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj \
  --filter 'FullyQualifiedName~TenantAccountQueryServiceSqlTests'

MSBUILDDISABLENODEREUSE=1 dotnet test \
  RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~WorkspaceAuthorizationKernelTests.AuthorizationExistsPrecedesOrderingAndPagingInGeneratedSql|FullyQualifiedName~WorkspaceAuthorizationKernelTests.MoneyAuthorizationRemainsDbSideBeforeOrderingAndPaging|FullyQualifiedName~RlsResourceScopePlanRegressionTests.EffectiveCapabilityScopes_'

dotnet build-server shutdown
```

- Command 1 proves AUTH-SQL-01 through AUTH-SQL-05.
- Command 2 proves AUTH-SQL-06.
- `dotnet build-server shutdown` is required serial resource cleanup only; it
  is not acceptance evidence.
- The read-only relevance review of the complete implementation diff proves
  AUTH-SQL-07.

### Relevance gate

The reviewer checks the complete implementation diff and verifies only:

- the diff in
  `RentalCommand.Api.Tests/Domain/TenantAccountQueryServiceSqlTests.cs`;
- changes limited to `AssertAuthorized` and `AssertDepositAuthorized`;
- AUTH-SQL-01 through AUTH-SQL-07 and both focused command results;
- absence of production, integration-test, foundation-test, or unrelated
  assertion changes.

Require `RELEVANCE PASS`. Any production change, additional changed path, or
weakening of the capability/session/no-inline/deposit-specific assertions is a
blocker.

## Completion gate

- [ ] Exactly one test file changed and only the two named helpers were edited.
- [ ] All 18 Tenant Account SQL tests pass.
- [ ] Every previously blocked statement has exactly one opaque function call.
- [ ] Capability keys and the existing session coordinate remain asserted.
- [ ] All four legacy authorization tables are absent from generated SQL.
- [ ] Deposit no-`UNION` and no-`money.balances.read` checks remain.
- [ ] The unchanged focused PostgreSQL suite passes.
- [ ] No production or integration test changed.
- [ ] Fresh relevance review returned `RELEVANCE PASS`.

## Deferred boundaries

- DEP-PERF-01 and DEP-PERF-03 through DEP-PERF-05 require a fresh Step 5
  measurement dispatch after this contract passes.
- Steps 6 and 7 remain deferred and unchanged.
