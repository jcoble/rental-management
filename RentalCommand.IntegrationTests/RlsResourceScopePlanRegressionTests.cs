using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using RentalCommand.Data;
using RentalCommand.Data.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class RlsResourceScopePlanRegressionTests : IAsyncLifetime
{
    private const string ApiPassword = "rls-plan-test-password";
    private const string EnginePassword = "rls-plan-engine-test-password";
    private const string PublicSigningTokenHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AcceptedHistoricalAuthoritySourceSha = "77c69102bb440d7c47a69cdf62bb2848712f1d5f";
    private const string AcceptedHistoricalAuthorityFixtureFingerprint = "4988EEB6B34406A59B25088888681B13A762E1A45EA455262850F581BC842499";
    private const string AcceptedHistoricalLegacyOwnerPathSql = """
              JOIN public."OwnerUserAccesses" owner_access
                ON owner_access."OwnerEntityId" = property."OwnerEntityId"
        """;
    private const string CurrentSchemaOwnerPathSql = """
              JOIN public."PropertyOwnerships" ownership
                ON ownership."PropertyId" = property."Id"
               AND ownership."PortfolioId" = target_portfolio_id
               AND ownership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
               AND (ownership."EffectiveToUtc" IS NULL
                    OR ownership."EffectiveToUtc" > CURRENT_TIMESTAMP)
              JOIN public."OwnerUserAccesses" owner_access
                ON owner_access."OwnerEntityId" = ownership."OwnerEntityId"
        """;
    private const string FullResourcePolicyTableSqlList = """
        'Properties','Units','LeaseManagements','LeaseAgreements','LeaseAddenda',
        'LeaseManagementParties','LeaseRenewalAddendumDecisions','TenantAccounts',
        'SecurityDepositAccounts','TenantAccountConditionPeriods','TenantAutopayEnrollments',
        'TenantLedgerAllocations','TenantLedgerEntries','TenantPaymentAttempts','WorkOrders',
        'WorkOrderStatusEvents','TechnicianWorkEntries','WorkOrderResponsibilities',
        'VendorDispatches','Conversations'
        """;
    private static readonly string[] PublicHelperSignatures =
    [
        "rc_public_application_scope_allows(integer)",
        "rc_public_signing_scope_allows(integer)",
        "rc_public_signing_request_allows(integer,integer)",
        "rc_public_signing_artifact_allows(integer,integer)",
        "rc_public_signing_file_allows(integer,integer,text,bigint)",
    ];
    private const string UnitsSql = """
        SELECT count(*), min("Id"), max("Id")
        FROM "Units"
        WHERE "PortfolioId" = 9001 AND "DeletedAt" IS NULL
        """;
    private const string DepositsSql = """
        SELECT count(*), COALESCE(sum(agreement."SecurityDepositObligation"), 0)
        FROM "SecurityDepositAccounts" deposit
        JOIN "TenantAccounts" account
          ON account."Id" = deposit."TenantAccountId"
         AND account."PortfolioId" = deposit."PortfolioId"
        JOIN "LeaseAgreements" agreement
          ON agreement."Id" = deposit."OriginatingAgreementId"
         AND agreement."PortfolioId" = deposit."PortfolioId"
        WHERE deposit."PortfolioId" = 9001
        """;
    private const string CashFlowSql = """
        SELECT count(*), COALESCE(sum(allocation."Amount"), 0)
        FROM "TenantLedgerAllocations" allocation
        JOIN "TenantLedgerEntries" receipt
          ON receipt."PortfolioId" = allocation."PortfolioId"
         AND receipt."TenantAccountId" = allocation."TenantAccountId"
         AND receipt."Id" = allocation."CreditEntryId"
        JOIN "TenantLedgerEntries" charge
          ON charge."PortfolioId" = allocation."PortfolioId"
         AND charge."TenantAccountId" = allocation."TenantAccountId"
         AND charge."Id" = allocation."DebitEntryId"
        JOIN "TenantAccounts" account
          ON account."PortfolioId" = allocation."PortfolioId"
         AND account."Id" = allocation."TenantAccountId"
        JOIN "LeaseManagements" management
          ON management."PortfolioId" = account."PortfolioId"
         AND management."Id" = account."LeaseManagementId"
        JOIN "Properties" property
          ON property."PortfolioId" = management."PortfolioId"
         AND property."Id" = management."PropertyId"
        WHERE allocation."PortfolioId" = 9001
          AND receipt."EntryType" = 'PaymentReceipt'
          AND charge."EntryType" IN ('RentCharge', 'LateFeeCharge')
        """;
    private PostgreSqlContainer? _postgres;
    private string _ownerConnectionString = string.Empty;
    private string _apiConnectionString = string.Empty;
    private string _engineConnectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_rls_plan")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithCommand("-c", "track_functions=all")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        _ownerConnectionString = _postgres.GetConnectionString();
        await using (var db = new RentalCommandDbContext(
                         new DbContextOptionsBuilder<RentalCommandDbContext>()
                             .UseNpgsql(_ownerConnectionString)
                             .Options))
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER ROLE rentalcommand_api PASSWORD '{ApiPassword}';");
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER ROLE rentalcommand_engine PASSWORD '{EnginePassword}';");
        }

        _apiConnectionString = new NpgsqlConnectionStringBuilder(_ownerConnectionString)
        {
            Username = "rentalcommand_api",
            Password = ApiPassword,
            Pooling = false,
        }.ConnectionString;
        _engineConnectionString = new NpgsqlConnectionStringBuilder(_ownerConnectionString)
        {
            Username = "rentalcommand_engine",
            Password = EnginePassword,
            Pooling = false,
        }.ConnectionString;

        await using var owner = new NpgsqlConnection(_ownerConnectionString);
        await owner.OpenAsync();
        await using var seed = owner.CreateCommand();
        seed.CommandText = SeedSql;
        await seed.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task AllProperties_UnitsCountPlan_HasNoPerRowResourceScopeFilter()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL RLS plan proof.");
        using var plan = await CaptureExplainJsonAsync(UnitsSql);
        AssertNoPerRowResourceScopeFilter(plan, "Units");
        var capture = await CaptureRlsFunctionCallsAsync(UnitsSql);
        capture.Result.Should().Be("2000|910001|912000");
        capture.ResourceCalls.Should().Be(0, "the all-properties fallback cannot execute at Units row cardinality");
        capture.ScopeCalls.Should().BeGreaterThan(0);
        capture.AllPropertiesCalls.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task AllProperties_DepositAggregatePlan_HasNoPerRowResourceScopeFilter()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL RLS plan proof.");
        using var plan = await CaptureExplainJsonAsync(DepositsSql);
        AssertNoPerRowResourceScopeFilter(
            plan, "SecurityDepositAccounts", "TenantAccounts", "LeaseAgreements");
        var capture = await CaptureRlsFunctionCallsAsync(DepositsSql);
        capture.Result.Should().Be("1000|1500000.00");
        capture.ResourceCalls.Should().Be(0, "the all-properties fallback cannot execute at deposit-join row cardinality");
        capture.ScopeCalls.Should().BeGreaterThan(0);
        capture.AllPropertiesCalls.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task SameData_CurrentVersusOptimized_HasMaterialImprovement()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL RLS plan proof.");
        await InstallCurrentAuthorityFunctionsAsync();
        var current = new[]
        {
            await CaptureRlsFunctionCallsAsync(UnitsSql),
            await CaptureRlsFunctionCallsAsync(DepositsSql),
            await CaptureRlsFunctionCallsAsync(CashFlowSql),
        };
        await InstallOptimizedAuthorityFunctionsAsync();
        var optimized = new[]
        {
            await CaptureRlsFunctionCallsAsync(UnitsSql),
            await CaptureRlsFunctionCallsAsync(DepositsSql),
            await CaptureRlsFunctionCallsAsync(CashFlowSql),
        };

        await AssertSameRestrictedRowsAsync(current, optimized);
        current.Sum(item => item.ResourceCalls).Should().BeGreaterThan(optimized.Sum(item => item.ResourceCalls));
        optimized.Sum(item => item.ResourceCalls).Should().Be(0);
        current.Sum(item => item.Elapsed.TotalMilliseconds).Should().BeGreaterThan(
            optimized.Sum(item => item.Elapsed.TotalMilliseconds) * 2,
            "the deterministic same-data optimized authority must materially outperform the per-row current fixture");

        using var unitsPlan = await CaptureExplainJsonAsync(UnitsSql);
        using var depositsPlan = await CaptureExplainJsonAsync(DepositsSql);
        using var cashFlowPlan = await CaptureExplainJsonAsync(CashFlowSql);
        var detailedEvidence = await CaptureDetailedEvidenceAsync(unitsPlan, depositsPlan, cashFlowPlan);
        await WriteSqlArtifactAsync([UnitsSql, DepositsSql, CashFlowSql]);
        await WriteExplainArtifactAsync([unitsPlan, depositsPlan, cashFlowPlan]);
        await WritePerformanceEvidenceAsync(current, optimized, detailedEvidence);
    }

    [SkippableFact]
    public async Task OptimizedAuthority_PreservesLiveSecurityDecisionMatrix()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL RLS security proof.");
        await InstallCurrentAuthorityFunctionsAsync();
        (await CaptureCommandFingerprint(InstalledResourcePolicyInventorySql)).Sql.Should().Be("20|80");
        var current = await CaptureSecurityDecisionMatrixAsync();
        await InstallOptimizedAuthorityFunctionsAsync();
        (await CaptureCommandFingerprint(InstalledResourcePolicyInventorySql)).Sql.Should().Be("20|80");
        var optimized = await CaptureSecurityDecisionMatrixAsync();

        optimized.Should().BeEquivalentTo(current, options => options.WithStrictOrdering());
        optimized.Should().Contain("direct-role/active:2000");
        optimized.Should().Contain("cross-portfolio:0");
        optimized.Should().Contain("access-revision/stale:0");
        optimized.Where(item => item.Contains("expired", StringComparison.Ordinal)
            || item.Contains("revoked", StringComparison.Ordinal)
            || item.Contains("suspended", StringComparison.Ordinal)
            || item.Contains("future", StringComparison.Ordinal))
            .Should().OnlyContain(item => item.EndsWith(":0", StringComparison.Ordinal));

        var resourceFingerprint = await CaptureCommandFingerprint(
            "SELECT pg_get_functiondef('rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)'::regprocedure)");
        foreach (var boundary in new[]
                 {
                     "MembershipRoleAssignmentProperties", "OwnerUserAccesses",
                     "vw_effective_tenant_access", "WorkOrderResponsibilities",
                 })
        {
            resourceFingerprint.Sql.Should().Contain(boundary,
                $"the optimized authority must retain the {boundary} resource boundary");
        }
    }

    [SkippableFact]
    public async Task OptimizedAuthority_PreservesPublicPolicyDefinitions()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL public-policy proof.");
        await InstallAcceptedHistoricalPublicHelperAclsAsync();
        var historicalDefinitions = await CaptureCommandFingerprint(PublicDefinitionPolicyFingerprintSql);
        var historicalCombined = await CaptureCommandFingerprint(PublicAuthorityFingerprintSql);
        var historicalAcl = await CaptureCommandFingerprint(PublicHelperAclSql);
        AssertHistoricalPublicHelperAcls(historicalAcl.Sql);
        await InstallActualAuthorityVersionChainAsync();
        var optimizedDefinitions = await CaptureCommandFingerprint(PublicDefinitionPolicyFingerprintSql);
        var optimizedCombined = await CaptureCommandFingerprint(PublicAuthorityFingerprintSql);
        var optimizedAcl = await CaptureCommandFingerprint(PublicHelperAclSql);
        var apiSigningAuthorization = await CaptureApiPublicSigningAuthorizationAsync();
        var engineDenials = await CaptureEnginePublicDenialsAsync();
        optimizedDefinitions.Fingerprint.Should().Be(historicalDefinitions.Fingerprint);
        optimizedCombined.Fingerprint.Should().NotBe(historicalCombined.Fingerprint,
            "the reviewed Engine EXECUTE additions are the explicit historical-to-optimized ACL delta");
        optimizedAcl.Fingerprint.Should().NotBe(historicalAcl.Fingerprint);
        optimizedDefinitions.Sql.Should().Contain("public_application_select");
        optimizedDefinitions.Sql.Should().Contain("public_signing_select");
        AssertExactPublicHelperAcls(optimizedAcl.Sql);
        apiSigningAuthorization.Should().Equal(
            "functions:1|1|1|1",
            "signing-token-qualified-rows:1|1");
        engineDenials.Should().Equal(
            "functions:0|0|0|0|0",
            "application-token-qualified-rows:0",
            "signing-token-qualified-rows:0|0");
    }

    [SkippableFact]
    public async Task BaselineAndMigration_InstallIdenticalAuthorityFunctions()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL migration parity proof.");
        var baseline = await CaptureCommandFingerprint(InstalledOptimizedAuthorityFingerprintSql);
        await InstallCurrentAuthorityFunctionsAsync();
        var historical = await CaptureCommandFingerprint(InstalledAcceptedHistoricalAuthorityFingerprintSql);
        historical.Sql.Should().Contain("WHEN NOT public.rc_api_scope_allows(target_portfolio_id) THEN FALSE");
        historical.Fingerprint.Should().NotBe(baseline.Fingerprint);
        var migrationSql = await InstallActualAuthorityVersionChainAsync();
        var migrated = await CaptureCommandFingerprint(InstalledOptimizedAuthorityFingerprintSql);
        migrated.Fingerprint.Should().Be(baseline.Fingerprint);
        migrationSql.Should().Equal(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260719,
            FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719,
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260724,
            FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260725,
            FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727,
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260728,
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSql);
        FoundationBaselinePostgreSql.RlsAuthorityFunctionSql.Should()
            .BeSameAs(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260809);
        FoundationBaselinePostgreSql.ResourcePoliciesSql.Should()
            .BeSameAs(FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719);
    }

    [SkippableFact]
    public async Task EffectiveCapabilityScopes_UseFrozenBusinessClockForFutureEffectiveAuthority()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL capability-scope proof.");
        await ExecuteOwnerSqlAsync(ResetSecurityStateSql);
        await ExecuteOwnerSqlAsync("""
            UPDATE "SimulationClocks"
            SET "Mode" = 'Frozen',
                "SimAnchorUtc" = '2027-01-14T05:00:00Z',
                "RealAnchorUtc" = CURRENT_TIMESTAMP,
                "TimeZoneId" = 'America/New_York',
                "UpdatedAtRealUtc" = CURRENT_TIMESTAMP
            WHERE "Id" = 1;
            UPDATE "WorkspaceMemberships"
            SET "EffectiveFromUtc" = '2027-01-13T05:00:00Z'
            WHERE "Id" = 9001;
            UPDATE "MembershipRoleAssignments"
            SET "EffectiveFromUtc" = '2027-01-13T05:00:00Z'
            WHERE "Id" = 9001;
            """);

        var frozenScope = await ExecuteApiScalarTextAsync(
            EffectiveScopeSummarySql("ARRAY['rentals.read']", "Property"),
            ScopeSql);

        frozenScope.Should().Be("9001|9001|AllProperties|null");
    }

    [SkippableFact]
    public async Task EffectiveCapabilityScopes_ReturnTypedAllSelectedAndAssignedWorkRows()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL capability-scope proof.");
        await ExecuteOwnerSqlAsync(ResetSecurityStateSql);

        var allProperties = await ExecuteApiScalarTextAsync(
            EffectiveScopeSummarySql("ARRAY['rentals.read']", "Property"),
            ScopeSql);
        allProperties.Should().Be("9001|9001|AllProperties|null");

        await ExecuteOwnerSqlAsync(SelectedPropertyMutationSql);
        var selected = await ExecuteApiScalarTextAsync(
            EffectiveScopeSummarySql("ARRAY['rentals.read']", "Property"),
            ScopeSql);
        selected.Should().Be("9001|9001|SelectedProperties|900001");

        await ExecuteOwnerSqlAsync("""
            DELETE FROM "MembershipRoleAssignmentProperties"
            WHERE "MembershipRoleAssignmentId" = 9001;
            UPDATE "MembershipRoleAssignments"
            SET "RoleProfileId" = 4,
                "ScopeKind" = 'AssignedWorkOrders',
                "Status" = 'Active',
                "SuspendedAtUtc" = NULL,
                "RevokedAtUtc" = NULL,
                "EffectiveFromUtc" = now() - interval '1 day',
                "EffectiveToUtc" = NULL
            WHERE "Id" = 9001;
            """);
        var assignedWork = await ExecuteApiScalarTextAsync(
            EffectiveScopeSummarySql("ARRAY['maintenance.assigned-work.read']", "WorkOrder"),
            ScopeSql);
        assignedWork.Should().Be("9001|9001|AssignedWorkOrders|null");
    }

    [SkippableFact]
    public async Task EffectiveCapabilityScopes_FailClosedForWrongCoordinatesOrDeadAuthority()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL capability-scope proof.");
        var functionCountSql = """
            SELECT count(*)
            FROM rc_api_effective_capability_scopes(
              9001,
              '11111111-1111-1111-1111-111111111111',
              9001,
              9001,
              1,
              ARRAY['rentals.read'],
              'Property')
            """;
        var cases = new (string Mutation, string Scope, string Sql)[]
        {
            ("", ScopeSql, functionCountSql.Replace("\n  1,\n", "\n  999,\n", StringComparison.Ordinal)),
            ("", StaleRevisionScopeSql, functionCountSql),
            ("", ScopeSql, functionCountSql.Replace(
                "'11111111-1111-1111-1111-111111111111'",
                "'22222222-2222-2222-2222-222222222222'",
                StringComparison.Ordinal)),
            ("", ScopeSql, functionCountSql.Replace("ARRAY['rentals.read']", "ARRAY['team.manage']", StringComparison.Ordinal)),
            ("", ScopeSql, functionCountSql.Replace("'Property')", "'WorkOrder')", StringComparison.Ordinal)),
            ("UPDATE \"AuthSessions\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"='11111111-1111-1111-1111-111111111111'", ScopeSql, functionCountSql),
            ("UPDATE \"AuthSessions\" SET \"CreatedAtUtc\"=now()-interval '2 days',\"LastSeenAtUtc\"=now()-interval '1 day',\"ExpiresAtUtc\"=now()-interval '1 hour' WHERE \"Id\"='11111111-1111-1111-1111-111111111111'", ScopeSql, functionCountSql),
            ("UPDATE \"WorkspaceMemberships\" SET \"Status\"='Suspended',\"SuspendedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, functionCountSql),
            ("UPDATE \"MembershipRoleAssignments\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, functionCountSql),
            ("UPDATE \"MembershipRoleAssignments\" SET \"EffectiveToUtc\"=now()-interval '1 minute' WHERE \"Id\"=9001", ScopeSql, functionCountSql),
        };

        foreach (var item in cases)
        {
            await ExecuteOwnerSqlAsync(ResetSecurityStateSql);
            if (!string.IsNullOrEmpty(item.Mutation))
            {
                await ExecuteOwnerSqlAsync(item.Mutation);
            }

            (await ExecuteApiScalarTextAsync(item.Sql, item.Scope)).Should().Be("0");
        }

        await ExecuteOwnerSqlAsync(ResetSecurityStateSql);
        (await ExecuteApiScalarTextAsync(
            functionCountSql.Replace(
                "'11111111-1111-1111-1111-111111111111'",
                "NULL",
                StringComparison.Ordinal),
            ScopeSql)).Should().Be("0");
    }

    [SkippableFact]
    public async Task EffectiveCapabilityScopes_AclIsApiOnlyAndOwnedByNoLoginAuthority()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL capability-scope ACL proof.");
        var acl = await CaptureCommandFingerprint("""
            SELECT concat_ws('|',
              owner.rolname,
              owner.rolcanlogin,
              has_function_privilege(
                'rentalcommand_api',
                'rc_api_effective_capability_scopes(integer,uuid,integer,integer,bigint,text[],text)',
                'EXECUTE'),
              has_function_privilege(
                'rentalcommand_engine',
                'rc_api_effective_capability_scopes(integer,uuid,integer,integer,bigint,text[],text)',
                'EXECUTE'),
              COALESCE((
                SELECT bool_or(acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')
                FROM aclexplode(procedure.proacl) acl), false))
            FROM pg_proc procedure
            JOIN pg_roles owner ON owner.oid = procedure.proowner
            WHERE procedure.oid =
              'rc_api_effective_capability_scopes(integer,uuid,integer,integer,bigint,text[],text)'::regprocedure
            """);
        acl.Sql.Should().Be("rentalcommand_rls_authority|f|t|f|f");
    }

    private async Task<JsonDocument> CaptureExplainJsonAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_apiConnectionString);
        await connection.OpenAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.CommandText = ScopeSql;
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN (ANALYZE, VERBOSE, FORMAT JSON, COSTS OFF, TIMING OFF, SUMMARY OFF) {sql}";
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
    }

    private static void AssertNoPerRowResourceScopeFilter(JsonDocument explain, params string[] relations)
    {
        var root = explain.RootElement[0].GetProperty("Plan");
        var nodes = EnumeratePlanNodes(root).ToArray();
        var initPlans = nodes.Where(node =>
            node.TryGetProperty("Parent Relationship", out var relationship)
            && relationship.GetString() == "InitPlan").ToArray();

        foreach (var relation in relations)
        {
            var relationNodes = nodes.Where(node =>
                node.TryGetProperty("Relation Name", out var name)
                && name.GetString() == relation).ToArray();
            relationNodes.Should().NotBeEmpty($"the plan must contain expected affected relation {relation}");

            var filters = relationNodes.Where(node => node.TryGetProperty("Filter", out _))
                .Select(node => node.GetProperty("Filter").GetString()!).ToArray();
            filters.Should().NotBeEmpty($"{relation} must expose its RLS filter for structural proof");
            var filter = filters.Single(text => text.Contains("rc_api_resource_scope_allows", StringComparison.Ordinal));
            var fallbackIndex = filter.IndexOf("rc_api_resource_scope_allows", StringComparison.Ordinal);
            var parameters = System.Text.RegularExpressions.Regex.Matches(filter[..fallbackIndex], @"\$\d+")
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
            parameters.Should().HaveCountGreaterThanOrEqualTo(2,
                $"{relation} must be guarded by exact scope and all-properties InitPlan parameters");

            var matchingInitPlans = initPlans.Where(node =>
                node.TryGetProperty("Subplan Name", out var name)
                && parameters.Any(parameter => name.GetString()!.Contains($"returns {parameter}", StringComparison.Ordinal)))
                .ToArray();
            var initPlanText = string.Join("\n", matchingInitPlans.Select(node => node.ToString()));
            initPlanText.Should().Contain("rc_api_scope_allows");
            initPlanText.Should().Contain("rc_api_all_properties_scope_allows");
            filter.IndexOf(parameters[0], StringComparison.Ordinal).Should().BeLessThan(fallbackIndex);
        }
    }

    private static Task AssertSameRestrictedRowsAsync(
        IReadOnlyList<PerformanceCapture> current,
        IReadOnlyList<PerformanceCapture> optimized)
    {
        optimized.Select(item => item.Result).Should().Equal(current.Select(item => item.Result));
        optimized.Select(item => item.CommandFingerprint).Should().Equal(
            current.Select(item => item.CommandFingerprint),
            "current and optimized runs must execute identical DB-side statements");
        return Task.CompletedTask;
    }

    private async Task<(string Sql, string Fingerprint)> CaptureCommandFingerprint(string sql)
    {
        await using var connection = new NpgsqlConnection(_ownerConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return (value, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
    }

    private static void AssertExactPublicHelperAcls(string acl)
    {
        var lines = acl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(PublicHelperSignatures.Length * 3);
        lines.Should().NotContain(line => line.Contains("grantee=PUBLIC", StringComparison.Ordinal));
        foreach (var function in PublicHelperSignatures)
        {
            var grants = lines.Where(line => line.StartsWith($"{function}|", StringComparison.Ordinal)).ToArray();
            grants.Should().HaveCount(3);
            grants.Should().OnlyContain(line => line.Contains("owner=rentalcommand_rls_authority", StringComparison.Ordinal)
                                                && line.Contains("privilege=EXECUTE", StringComparison.Ordinal));
            grants.Select(line => line.Split("grantee=")[1].Split('|')[0]).Should().BeEquivalentTo(
                "rentalcommand_rls_authority", "rentalcommand_api", "rentalcommand_engine");
        }
    }

    private static void AssertHistoricalPublicHelperAcls(string acl)
    {
        var lines = acl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(10);
        lines.Should().NotContain(line => line.Contains("grantee=PUBLIC", StringComparison.Ordinal)
                                              || line.Contains("grantee=rentalcommand_engine", StringComparison.Ordinal));
        foreach (var function in PublicHelperSignatures)
        {
            var grants = lines.Where(line => line.StartsWith($"{function}|", StringComparison.Ordinal)).ToArray();
            grants.Should().HaveCount(2);
            grants.Should().OnlyContain(line => line.Contains("owner=rentalcommand_rls_authority", StringComparison.Ordinal)
                                                && line.Contains("privilege=EXECUTE", StringComparison.Ordinal));
            grants.Select(line => line.Split("grantee=")[1].Split('|')[0]).Should().BeEquivalentTo(
                "rentalcommand_rls_authority", "rentalcommand_api");
        }
    }

    private async Task<IReadOnlyList<string>> CaptureEnginePublicDenialsAsync()
    {
        await using var connection = new NpgsqlConnection(_engineConnectionString);
        await connection.OpenAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.CommandText = """
                SELECT set_config('app.public_application_token', 'accepted-public-application-token', false),
                       set_config('app.public_signing_token_hash', @signing_token_hash, false)
                """;
            scope.Parameters.AddWithValue("signing_token_hash", PublicSigningTokenHash);
            await scope.ExecuteNonQueryAsync();
        }

        var results = new List<string>(3);
        await using (var functions = connection.CreateCommand())
        {
            functions.CommandText = """
                SELECT concat_ws('|',
                  rc_public_application_scope_allows(9001)::int,
                  rc_public_signing_scope_allows(9001)::int,
                  rc_public_signing_request_allows(9001, 980001)::int,
                  rc_public_signing_artifact_allows(9001, 970002)::int,
                  rc_public_signing_file_allows(9001, 970003, 'SignatureSigner', 990001)::int)
                """;
            results.Add($"functions:{await functions.ExecuteScalarAsync()}");
        }
        await using (var applicationRows = connection.CreateCommand())
        {
            applicationRows.CommandText =
                "SELECT count(*) FROM \"Units\" WHERE rc_public_application_scope_allows(\"PortfolioId\")";
            results.Add($"application-token-qualified-rows:{await applicationRows.ExecuteScalarAsync()}");
        }
        await using (var signingRows = connection.CreateCommand())
        {
            signingRows.CommandText =
                """
                SELECT concat_ws('|',
                  (SELECT count(*) FROM "Portfolios" WHERE rc_public_signing_scope_allows("Id")),
                  (SELECT count(*) FROM "SignatureRequests"
                   WHERE "Id" = 980001
                     AND rc_public_signing_request_allows("PortfolioId", "Id")))
                """;
            results.Add($"signing-token-qualified-rows:{await signingRows.ExecuteScalarAsync()}");
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> CaptureApiPublicSigningAuthorizationAsync()
    {
        await using var connection = new NpgsqlConnection(_apiConnectionString);
        await connection.OpenAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.CommandText =
                "SELECT set_config('app.public_signing_token_hash', @signing_token_hash, false)";
            scope.Parameters.AddWithValue("signing_token_hash", PublicSigningTokenHash);
            await scope.ExecuteNonQueryAsync();
        }

        var results = new List<string>(2);
        await using (var functions = connection.CreateCommand())
        {
            functions.CommandText = """
                SELECT concat_ws('|',
                  rc_public_signing_scope_allows(9001)::int,
                  rc_public_signing_request_allows(9001, 980001)::int,
                  rc_public_signing_artifact_allows(9001, 970002)::int,
                  rc_public_signing_file_allows(9001, 970003, 'SignatureSigner', 990001)::int)
                """;
            results.Add($"functions:{await functions.ExecuteScalarAsync()}");
        }
        await using (var signingRows = connection.CreateCommand())
        {
            signingRows.CommandText = """
                SELECT concat_ws('|',
                  (SELECT count(*) FROM "Portfolios" WHERE "Id" = 9001),
                  (SELECT count(*) FROM "SignatureRequests"
                   WHERE "Id" = 980001
                     AND rc_public_signing_request_allows("PortfolioId", "Id")))
                """;
            results.Add($"signing-token-qualified-rows:{await signingRows.ExecuteScalarAsync()}");
        }
        return results;
    }

    private async Task<PerformanceCapture> CaptureRlsFunctionCallsAsync(string sql)
    {
        await using (var owner = new NpgsqlConnection(_ownerConnectionString))
        {
            await owner.OpenAsync();
            await using var reset = owner.CreateCommand();
            reset.CommandText = "SELECT pg_stat_reset()";
            await reset.ExecuteNonQueryAsync();
        }

        string result;
        var stopwatch = Stopwatch.StartNew();
        await using (var connection = new NpgsqlConnection(_apiConnectionString))
        {
            await connection.OpenAsync();
            await using (var scope = connection.CreateCommand())
            {
                scope.CommandText = ScopeSql;
                await scope.ExecuteNonQueryAsync();
            }
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            result = string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)));
        }
        stopwatch.Stop();

        await using var statsConnection = new NpgsqlConnection(_ownerConnectionString);
        await statsConnection.OpenAsync();
        await using (var flush = statsConnection.CreateCommand())
        {
            flush.CommandText = "SELECT pg_stat_force_next_flush()";
            await flush.ExecuteNonQueryAsync();
        }
        await using var stats = statsConnection.CreateCommand();
        stats.CommandText = """
            SELECT
              COALESCE(sum(calls) FILTER (WHERE funcname = 'rc_api_scope_allows'), 0),
              COALESCE(sum(calls) FILTER (WHERE funcname = 'rc_api_all_properties_scope_allows'), 0),
              COALESCE(sum(calls) FILTER (WHERE funcname = 'rc_api_resource_scope_allows'), 0)
            FROM pg_stat_user_functions
            """;
        await using var statsReader = await stats.ExecuteReaderAsync();
        (await statsReader.ReadAsync()).Should().BeTrue();
        return new PerformanceCapture(
            result,
            stopwatch.Elapsed,
            statsReader.GetInt64(0),
            statsReader.GetInt64(1),
            statsReader.GetInt64(2),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
    }

    private async Task InstallCurrentAuthorityFunctionsAsync()
    {
        FingerprintText(AcceptedHistoricalAuthoritySql).Should().Be(
            AcceptedHistoricalAuthorityFixtureFingerprint,
            $"the reviewed full historical authority fixture must remain byte-stable to source SHA {AcceptedHistoricalAuthoritySourceSha}");
        AcceptedHistoricalAuthoritySql.Should().Contain(
            "WHEN NOT public.rc_api_scope_allows(target_portfolio_id) THEN FALSE",
            "the immutable historical fixture must retain its portfolio scope guard");
        var legacyOwnerPathIndex = AcceptedHistoricalAuthoritySql.IndexOf(
            AcceptedHistoricalLegacyOwnerPathSql, StringComparison.Ordinal);
        legacyOwnerPathIndex.Should().BeGreaterThanOrEqualTo(
            0, "the immutable historical fixture must retain the legacy direct Property.OwnerEntityId owner path");
        AcceptedHistoricalAuthoritySql.LastIndexOf(
                AcceptedHistoricalLegacyOwnerPathSql, StringComparison.Ordinal)
            .Should().Be(
                legacyOwnerPathIndex,
                "the current-schema adapter must replace exactly one legacy owner path");
        AcceptedHistoricalAuthoritySql.Should().NotContain(
            CurrentSchemaOwnerPathSql,
            "current-schema compatibility must not mutate the immutable historical fixture");

        var currentSchemaExecutionSql = AcceptedHistoricalAuthoritySql.Replace(
            AcceptedHistoricalLegacyOwnerPathSql,
            CurrentSchemaOwnerPathSql,
            StringComparison.Ordinal);
        currentSchemaExecutionSql.Should().Contain(
            CurrentSchemaOwnerPathSql,
            "the execution adapter must use the canonical effective PropertyOwnerships owner path");
        currentSchemaExecutionSql.Should().NotContain(
            AcceptedHistoricalLegacyOwnerPathSql,
            "the legacy direct property owner column no longer exists in the current schema");
        currentSchemaExecutionSql.Replace(
                CurrentSchemaOwnerPathSql,
                AcceptedHistoricalLegacyOwnerPathSql,
                StringComparison.Ordinal)
            .Should().Be(
                AcceptedHistoricalAuthoritySql,
                "the execution adapter may change only the legacy owner-path fragment");

        await ExecuteOwnerSqlAsync(currentSchemaExecutionSql);
    }

    private async Task InstallOptimizedAuthorityFunctionsAsync()
    {
        await ExecuteOwnerSqlAsync(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260725);
        await ExecuteOwnerSqlAsync(FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719);
    }

    private async Task InstallAcceptedHistoricalPublicHelperAclsAsync() =>
        await ExecuteOwnerSqlAsync(AcceptedHistoricalPublicHelperAclSql);

    private async Task<IReadOnlyList<string>> InstallActualAuthorityVersionChainAsync()
    {
        var l15Migration = new OptimizeRlsRequestScope();
        var l15Builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var up = typeof(OptimizeRlsRequestScope).GetMethod(
            "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        up.Invoke(l15Migration, [l15Builder]);
        var l15Sql = l15Builder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        l15Builder.Operations.Should().HaveCount(l15Sql.Length).And.HaveCount(2,
            "historical L15 must contain exactly its immutable authority and policy operations");
        l15Sql.Should().Equal(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260719,
            FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719);

        var currentMigration = new AddSuppliedLegalNoticeTemplateV2();
        var currentBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddSuppliedLegalNoticeTemplateV2).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(currentMigration, [currentBuilder]);
        var laterSql = currentBuilder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        laterSql.Should().HaveCount(2,
            "the later migration must keep its versioned authority update separate from its legal-template helper");
        laterSql[0].Should().BeSameAs(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260724);
        laterSql[1].Should().Contain("CREATE OR REPLACE FUNCTION rc_delete_fresh_workspace_notice_templates");
        laterSql[1].Should().NotContain("CREATE OR REPLACE FUNCTION rc_api_scope_allows");
        currentBuilder.Operations.OfType<InsertDataOperation>().Should().ContainSingle(
            "parity must identify, but must not replay, unrelated legal-template seed DML");

        var optimizedMigration = new AddEffectiveCapabilityScopeAuthority();
        var optimizedBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddEffectiveCapabilityScopeAuthority).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(optimizedMigration, [optimizedBuilder]);
        var optimizedSql = optimizedBuilder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        optimizedSql.Should().ContainSingle()
            .Which.Should().BeSameAs(
                FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260725);

        var businessClockMigration = new UseBusinessClockForCapabilityScopes();
        var businessClockBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(UseBusinessClockForCapabilityScopes).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(businessClockMigration, [businessClockBuilder]);
        var businessClockSql = businessClockBuilder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        businessClockSql.Should().ContainSingle()
            .Which.Should().BeSameAs(
                FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727);

        var resourceClockMigration = new UseBusinessClockForResourceScopes();
        var resourceClockBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(UseBusinessClockForResourceScopes).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(resourceClockMigration, [resourceClockBuilder]);
        var resourceClockSql = resourceClockBuilder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        resourceClockSql.Should().ContainSingle()
            .Which.Should().BeSameAs(
                FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260728);

        var revokeMigration = new RestoreAuthSessionRevokeAudit();
        var revokeBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(RestoreAuthSessionRevokeAudit).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(revokeMigration, [revokeBuilder]);
        var revokeSql = revokeBuilder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        revokeSql.Should().ContainSingle()
            .Which.Should().BeSameAs(FoundationBaselinePostgreSql.RlsAuthorityFunctionSql);

        var sql = l15Sql.Append(laterSql[0])
            .Concat(optimizedSql)
            .Concat(businessClockSql)
            .Concat(resourceClockSql)
            .Concat(revokeSql)
            .ToArray();
        foreach (var statement in sql) await ExecuteOwnerSqlAsync(statement);
        return sql;
    }

    private static async Task WriteSqlArtifactAsync(IReadOnlyList<string> sql)
    {
        var path = Environment.GetEnvironmentVariable("FOUNDATION_PERF_SQL_OUTPUT");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = ResolveArtifactPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, string.Join("\n\n-- next operation --\n\n", sql));
    }

    private static async Task WriteExplainArtifactAsync(IReadOnlyList<JsonDocument> plans)
    {
        var path = Environment.GetEnvironmentVariable("FOUNDATION_PERF_EXPLAIN_OUTPUT");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = ResolveArtifactPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, string.Join("\n\n", plans.Select(plan => plan.RootElement.GetRawText())));
    }

    private static async Task WritePerformanceEvidenceAsync(
        IReadOnlyList<PerformanceCapture> current,
        IReadOnlyList<PerformanceCapture> optimized,
        IReadOnlyList<string> detailedEvidence)
    {
        var path = Environment.GetEnvironmentVariable("FOUNDATION_PERF_EVIDENCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(path)) return;
        path = ResolveArtifactPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var names = new[] { "Units count/list", "Security Deposit aggregate", "Cash Flow Reports" };
        var lines = new List<string>
        {
            "# L15 shared-path performance evidence",
            "",
            "Accepted immutable baseline: SHA `77c69102bb440d7c47a69cdf62bb2848712f1d5f`; Units 43.644s vs BYPASSRLS 1.300ms; Security Deposits 65.150s vs BYPASSRLS 5.000ms; 40,100 nested resource calls.",
            "",
            "| Operation | Current rows | Optimized rows | Current ms | Optimized ms | Current resource calls | Optimized resource calls | SQL fingerprint |",
            "|---|---:|---:|---:|---:|---:|---:|---|",
        };
        for (var index = 0; index < names.Length; index++)
        {
            lines.Add($"| {names[index]} | `{current[index].Result}` | `{optimized[index].Result}` | {current[index].Elapsed.TotalMilliseconds:F3} | {optimized[index].Elapsed.TotalMilliseconds:F3} | {current[index].ResourceCalls} | {optimized[index].ResourceCalls} | `{optimized[index].CommandFingerprint}` |");
        }
        lines.Add("");
        lines.Add("All filtering, joins, aggregation, sorting, paging, and authorization remained in each single PostgreSQL statement. Query count is informational and is appended by SharedRequestPathPerformanceTests when that focused suite runs.");
        lines.Add("");
        lines.AddRange(detailedEvidence);
        await File.WriteAllLinesAsync(path, lines);
    }

    private async Task<IReadOnlyList<string>> CaptureDetailedEvidenceAsync(
        JsonDocument unitsPlan,
        JsonDocument depositsPlan,
        JsonDocument cashFlowPlan)
    {
        var lines = new List<string>
        {
            "## Relation/InitPlan proof",
            "",
        };
        lines.AddRange(CaptureRelationInitPlanProof(unitsPlan, "Units"));
        lines.AddRange(CaptureRelationInitPlanProof(
            depositsPlan, "SecurityDepositAccounts", "TenantAccounts", "LeaseAgreements"));
        lines.AddRange(CaptureRelationInitPlanProof(
            cashFlowPlan, "TenantLedgerAllocations", "TenantLedgerEntries", "TenantAccounts", "LeaseManagements", "Properties"));

        await InstallCurrentAuthorityFunctionsAsync();
        var historicalAuthority = await CaptureCommandFingerprint(InstalledAcceptedHistoricalAuthorityFingerprintSql);
        var historicalInventory = await CaptureCommandFingerprint(InstalledResourcePolicyInventorySql);
        historicalInventory.Sql.Should().Be("20|80");
        var currentMatrix = await CaptureSecurityDecisionMatrixAsync();
        await InstallOptimizedAuthorityFunctionsAsync();
        var optimizedAuthority = await CaptureCommandFingerprint(InstalledOptimizedAuthorityFingerprintSql);
        var optimizedInventory = await CaptureCommandFingerprint(InstalledResourcePolicyInventorySql);
        optimizedInventory.Sql.Should().Be("20|80");
        var optimizedMatrix = await CaptureSecurityDecisionMatrixAsync();
        optimizedMatrix.Should().Equal(currentMatrix);
        lines.Add("");
        lines.Add("## Full decision matrix");
        lines.Add("");
        lines.Add($"- Accepted historical fixture source SHA: `{AcceptedHistoricalAuthoritySourceSha}`; byte-stable fixture fingerprint: `{AcceptedHistoricalAuthorityFixtureFingerprint}`.");
        lines.Add($"- Full historical resource bundle: `{historicalInventory.Sql}` tables/policies; installed fingerprint: `{historicalAuthority.Fingerprint}`.");
        lines.Add($"- Full optimized resource bundle: `{optimizedInventory.Sql}` tables/policies over the identical 20-table set; installed fingerprint: `{optimizedAuthority.Fingerprint}`.");
        lines.Add("- Positive and negative owner, tenant, and assigned-work authorization cases are bound by exact Command 3 PostgreSQL suites (`RlsTenantIsolationTests` and `RoleExperienceAuthorizationPostgreSqlTests`).");
        foreach (var result in currentMatrix)
            lines.Add($"- `{result}` (current = optimized)");

        await InstallAcceptedHistoricalPublicHelperAclsAsync();
        var historicalPublicDefinitions = await CaptureCommandFingerprint(PublicDefinitionPolicyFingerprintSql);
        var historicalPublicCombined = await CaptureCommandFingerprint(PublicAuthorityFingerprintSql);
        var historicalAcl = await CaptureCommandFingerprint(PublicHelperAclSql);
        AssertHistoricalPublicHelperAcls(historicalAcl.Sql);
        await InstallActualAuthorityVersionChainAsync();
        var optimizedPublicDefinitions = await CaptureCommandFingerprint(PublicDefinitionPolicyFingerprintSql);
        var optimizedPublicCombined = await CaptureCommandFingerprint(PublicAuthorityFingerprintSql);
        var optimizedAcl = await CaptureCommandFingerprint(PublicHelperAclSql);
        var apiSigningAuthorization = await CaptureApiPublicSigningAuthorizationAsync();
        var engineAfter = await CaptureEnginePublicDenialsAsync();
        optimizedPublicDefinitions.Fingerprint.Should().Be(historicalPublicDefinitions.Fingerprint);
        optimizedPublicCombined.Fingerprint.Should().NotBe(historicalPublicCombined.Fingerprint);
        optimizedAcl.Fingerprint.Should().NotBe(historicalAcl.Fingerprint);
        AssertExactPublicHelperAcls(optimizedAcl.Sql);
        apiSigningAuthorization.Should().Equal(
            "functions:1|1|1|1",
            "signing-token-qualified-rows:1|1");
        engineAfter.Should().Equal(
            "functions:0|0|0|0|0",
            "application-token-qualified-rows:0",
            "signing-token-qualified-rows:0|0");
        lines.Add("");
        lines.Add("## Public definitions and ACLs");
        lines.Add("");
        lines.Add($"- Public definition/policy fingerprint, historical and optimized unchanged: `{optimizedPublicDefinitions.Fingerprint}`");
        lines.Add($"- Historical definitions/policies/ACL combined fingerprint: `{historicalPublicCombined.Fingerprint}`");
        lines.Add($"- Optimized definitions/policies/ACL combined fingerprint: `{optimizedPublicCombined.Fingerprint}`");
        lines.Add($"- Historical exact ACL fingerprint: `{historicalAcl.Fingerprint}`; explicit grantees: owner plus `rentalcommand_api`.");
        lines.Add($"- Optimized exact ACL fingerprint: `{optimizedAcl.Fingerprint}`; only intended delta: `rentalcommand_engine` EXECUTE on five policy helpers.");
        lines.Add("- Both states: `PUBLIC` EXECUTE revoked and owner `rentalcommand_rls_authority` unchanged.");
        foreach (var result in apiSigningAuthorization) lines.Add($"- Matching-token API `{result}`");
        foreach (var result in engineAfter) lines.Add($"- Direct Engine `{result}`");

        var freshBaseline = await CaptureCommandFingerprint(InstalledOptimizedAuthorityFingerprintSql);
        await InstallCurrentAuthorityFunctionsAsync();
        var historicalBeforeMigration = await CaptureCommandFingerprint(InstalledAcceptedHistoricalAuthorityFingerprintSql);
        var migrationSql = await InstallActualAuthorityVersionChainAsync();
        var migrated = await CaptureCommandFingerprint(InstalledOptimizedAuthorityFingerprintSql);
        migrated.Fingerprint.Should().Be(freshBaseline.Fingerprint);
        lines.Add("");
        lines.Add("## Fresh-baseline/actual-migration parity");
        lines.Add("");
        lines.Add($"- Fresh optimized baseline fingerprint: `{freshBaseline.Fingerprint}`");
        lines.Add($"- Historical pre-migration fingerprint: `{historicalBeforeMigration.Fingerprint}`");
        lines.Add($"- Actual EF authority operations (historical L15 through V20260725): {migrationSql.Count}; SQL fingerprint: `{FingerprintText(string.Join("\n", migrationSql))}`.");
        lines.Add("- Unrelated legal-template seed and helper operations were identified from the later migration but deliberately excluded from parity replay.");
        lines.Add($"- Migrated installed fingerprint: `{migrated.Fingerprint}`; identical to fresh baseline: `true`.");
        lines.Add("- Historical L15 `Down`: explicitly irreversible because restoring per-row policies would restore the accepted production latency defect.");
        return lines;
    }

    private static IReadOnlyList<string> CaptureRelationInitPlanProof(JsonDocument explain, params string[] relations)
    {
        var nodes = EnumeratePlanNodes(explain.RootElement[0].GetProperty("Plan")).ToArray();
        var initPlans = nodes.Where(node =>
            node.TryGetProperty("Parent Relationship", out var relationship)
            && relationship.GetString() == "InitPlan").ToArray();
        var results = new List<string>(relations.Length);
        foreach (var relation in relations)
        {
            var relationNodes = nodes.Where(node =>
                node.TryGetProperty("Relation Name", out var name) && name.GetString() == relation).ToArray();
            relationNodes.Should().NotBeEmpty();
            var filters = relationNodes.SelectMany(node => node.TryGetProperty("Filter", out var value)
                    ? new[] { value.GetString()! }
                    : Array.Empty<string>())
                .Where(text => text.Contains("rc_api_resource_scope_allows", StringComparison.Ordinal))
                .ToArray();
            filters.Should().NotBeEmpty();
            var parameters = filters.SelectMany(filter =>
                {
                    var fallbackIndex = filter.IndexOf("rc_api_resource_scope_allows", StringComparison.Ordinal);
                    return System.Text.RegularExpressions.Regex.Matches(filter[..fallbackIndex], @"\$\d+")
                        .Select(match => match.Value);
                })
                .Distinct(StringComparer.Ordinal).ToArray();
            var names = initPlans.Where(node => node.TryGetProperty("Subplan Name", out var name)
                                               && parameters.Any(parameter => name.GetString()!.Contains($"returns {parameter}", StringComparison.Ordinal)))
                .Select(node => node.GetProperty("Subplan Name").GetString()!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            names.Should().HaveCountGreaterThanOrEqualTo(2);
            results.Add($"- `{relation}`: plan occurrences `{filters.Length}`; filter fingerprint `{FingerprintText(string.Join("\n", filters))}`; InitPlans `{string.Join(", ", names)}`; optimized nested resource calls verified as zero.");
        }
        return results;
    }

    private static string FingerprintText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string EffectiveScopeSummarySql(string capabilityKeysSql, string targetKind) => $"""
        SELECT string_agg(
          concat_ws(
            '|',
            scope."AssignmentId",
            scope."WorkspaceMembershipId",
            scope."ScopeKind",
            COALESCE(scope."PropertyId"::text, 'null')),
          ',' ORDER BY scope."AssignmentId", scope."PropertyId")
        FROM rc_api_effective_capability_scopes(
          9001,
          '11111111-1111-1111-1111-111111111111',
          9001,
          9001,
          1,
          {capabilityKeysSql},
          '{targetKind}') scope
        """;

    private static string ResolveArtifactPath(string path)
    {
        if (Path.IsPathFullyQualified(path)) return path;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            path);
    }

    private async Task<IReadOnlyList<string>> CaptureSecurityDecisionMatrixAsync()
    {
        var cases = new (string Name, string Mutation, string Scope, string Sql)[]
        {
            ("direct-role/active", "", ScopeSql, "SELECT count(*) FROM \"Units\" WHERE \"PortfolioId\" = 9001"),
            ("cross-portfolio", "", CrossPortfolioScopeSql, "SELECT count(*) FROM \"Units\" WHERE \"PortfolioId\" = 9001"),
            ("access-revision/stale", "", StaleRevisionScopeSql, "SELECT count(*) FROM \"Units\" WHERE \"PortfolioId\" = 9001"),
            ("session/status-revoked", "UPDATE \"AuthSessions\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"='11111111-1111-1111-1111-111111111111'", ScopeSql, UnitsCountSql),
            ("session/revoked", "UPDATE \"AuthSessions\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"='11111111-1111-1111-1111-111111111111'", ScopeSql, UnitsCountSql),
            ("session/expired", "UPDATE \"AuthSessions\" SET \"CreatedAtUtc\"=now()-interval '2 days',\"LastSeenAtUtc\"=now()-interval '1 day',\"ExpiresAtUtc\"=now()-interval '1 hour' WHERE \"Id\"='11111111-1111-1111-1111-111111111111'", ScopeSql, UnitsCountSql),
            ("context/status-revoked", "UPDATE \"WorkspaceAccessContexts\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("context/revoked", "UPDATE \"WorkspaceAccessContexts\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("context/suspended", "UPDATE \"WorkspaceAccessContexts\" SET \"Status\"='Suspended',\"SuspendedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("membership/status-revoked", "UPDATE \"WorkspaceMemberships\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("membership/revoked", "UPDATE \"WorkspaceMemberships\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("membership/suspended", "UPDATE \"WorkspaceMemberships\" SET \"Status\"='Suspended',\"SuspendedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("membership/effective-expired", "UPDATE \"WorkspaceMemberships\" SET \"EffectiveToUtc\"=now()-interval '1 minute' WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("membership/effective-future", "UPDATE \"WorkspaceMemberships\" SET \"EffectiveFromUtc\"=now()+interval '1 minute' WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("assignment/status-revoked", "UPDATE \"MembershipRoleAssignments\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("assignment/revoked", "UPDATE \"MembershipRoleAssignments\" SET \"Status\"='Revoked',\"RevokedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("assignment/suspended", "UPDATE \"MembershipRoleAssignments\" SET \"Status\"='Suspended',\"SuspendedAtUtc\"=now() WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("assignment/effective-expired", "UPDATE \"MembershipRoleAssignments\" SET \"EffectiveToUtc\"=now()-interval '1 minute' WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("assignment/effective-future", "UPDATE \"MembershipRoleAssignments\" SET \"EffectiveFromUtc\"=now()+interval '1 minute' WHERE \"Id\"=9001", ScopeSql, UnitsCountSql),
            ("all-properties", "", ScopeSql, UnitsCountSql),
            ("selected-properties/allowed", SelectedPropertyMutationSql, ScopeSql, "SELECT count(*) FROM \"Units\" WHERE \"PropertyId\"=900001"),
            ("selected-properties/denied", SelectedPropertyMutationSql, ScopeSql, "SELECT count(*) FROM \"Units\" WHERE \"PropertyId\"=900002"),
            ("owner-boundary/unrelated", "DELETE FROM \"MembershipRoleAssignments\" WHERE \"Id\"=9001", ScopeSql, "SELECT rc_api_resource_scope_allows(9001,900001,NULL,NULL,NULL,NULL,NULL,TRUE,FALSE,FALSE)::int"),
            ("tenant-boundary/unrelated", "DELETE FROM \"MembershipRoleAssignments\" WHERE \"Id\"=9001", ScopeSql, "SELECT rc_api_resource_scope_allows(9001,900001,NULL,NULL,NULL,NULL,NULL,FALSE,TRUE,FALSE)::int"),
            ("assigned-work-boundary/unrelated", "DELETE FROM \"MembershipRoleAssignments\" WHERE \"Id\"=9001", ScopeSql, "SELECT rc_api_resource_scope_allows(9001,900001,NULL,999999,NULL,NULL,NULL,FALSE,FALSE,TRUE)::int"),
        };
        var results = new List<string>(cases.Length + 1);
        foreach (var item in cases)
        {
            await ExecuteOwnerSqlAsync(ResetSecurityStateSql);
            if (!string.IsNullOrWhiteSpace(item.Mutation)) await ExecuteOwnerSqlAsync(item.Mutation);
            results.Add($"{item.Name}:{await ExecuteApiScalarTextAsync(item.Sql, item.Scope)}");
        }

        await ExecuteOwnerSqlAsync(ResetSecurityStateSql);
        await using (var owner = new NpgsqlConnection(_ownerConnectionString))
        {
            await owner.OpenAsync();
            await using var command = owner.CreateCommand();
            command.CommandText = $"SET ROLE rentalcommand_api; {UnitsCountSql}; RESET ROLE;";
            results.Add($"cross-role:{Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return results;
    }

    private async Task ExecuteOwnerSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_ownerConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ExecuteApiScalarTextAsync(string sql, string scopeSql)
    {
        await using var connection = new NpgsqlConnection(_apiConnectionString);
        await connection.OpenAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.CommandText = scopeSql;
            await scope.ExecuteNonQueryAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static IEnumerable<JsonElement> EnumeratePlanNodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in EnumeratePlanNodes(child))
                yield return descendant;
    }

    private const string ScopeSql = """
        SELECT set_config('app.current_portfolio_id', '9001', false),
               set_config('app.auth_session_id', '11111111-1111-1111-1111-111111111111', false),
               set_config('app.current_user_id', '9001', false),
               set_config('app.current_access_context_id', '9001', false),
               set_config('app.access_revision', '1', false)
        """;

    private const string CrossPortfolioScopeSql = """
        SELECT set_config('app.current_portfolio_id', '9002', false),
               set_config('app.auth_session_id', '11111111-1111-1111-1111-111111111111', false),
               set_config('app.current_user_id', '9001', false),
               set_config('app.current_access_context_id', '9002', false),
               set_config('app.access_revision', '1', false)
        """;

    private const string StaleRevisionScopeSql = """
        SELECT set_config('app.current_portfolio_id', '9001', false),
               set_config('app.auth_session_id', '11111111-1111-1111-1111-111111111111', false),
               set_config('app.current_user_id', '9001', false),
               set_config('app.current_access_context_id', '9001', false),
               set_config('app.access_revision', '999', false)
        """;

    private const string UnitsCountSql = "SELECT count(*) FROM \"Units\" WHERE \"PortfolioId\"=9001";

    private const string ResetSecurityStateSql = """
        UPDATE "AuthSessions" SET "Status"='Active', "RevokedAtUtc"=NULL, "CreatedAtUtc"=now(), "LastSeenAtUtc"=now(), "ExpiresAtUtc"=now()+interval '1 day'
          WHERE "Id"='11111111-1111-1111-1111-111111111111';
        UPDATE "WorkspaceAccessContexts" SET "Status"='Active', "SuspendedAtUtc"=NULL, "RevokedAtUtc"=NULL, "AccessRevision"=1
          WHERE "Id"=9001;
        UPDATE "WorkspaceMemberships" SET "Status"='Active', "SuspendedAtUtc"=NULL, "RevokedAtUtc"=NULL,
          "EffectiveFromUtc"=now()-interval '1 day', "EffectiveToUtc"=NULL WHERE "Id"=9001;
        DELETE FROM "MembershipRoleAssignmentProperties" WHERE "MembershipRoleAssignmentId"=9001;
        INSERT INTO "MembershipRoleAssignments" ("Id","WorkspaceMembershipId","PortfolioId","RoleProfileId","ScopeKind","Status","EffectiveFromUtc","CreatedAtUtc","UpdatedAtUtc")
          SELECT 9001,9001,9001,"Id",'AllProperties','Active',now()-interval '1 day',now(),now()
          FROM "RoleProfiles" ORDER BY "Id" LIMIT 1
          ON CONFLICT ("Id") DO UPDATE SET "ScopeKind"='AllProperties',"Status"='Active',"SuspendedAtUtc"=NULL,
            "RevokedAtUtc"=NULL,"EffectiveFromUtc"=now()-interval '1 day',"EffectiveToUtc"=NULL;
        """;

    private const string SelectedPropertyMutationSql = """
        UPDATE "MembershipRoleAssignments" SET "ScopeKind"='SelectedProperties' WHERE "Id"=9001;
        INSERT INTO "MembershipRoleAssignmentProperties" ("MembershipRoleAssignmentId","PortfolioId","PropertyId")
        VALUES (9001,9001,900001);
        """;

    private const string AcceptedHistoricalAuthoritySql = """
        CREATE OR REPLACE FUNCTION rc_api_resource_scope_allows(
          target_portfolio_id integer,
          target_property_id integer,
          target_unit_id integer,
          target_work_order_id integer,
          target_lease_management_id integer,
          target_tenant_account_id integer,
          target_tenant_id integer,
          allow_owner boolean,
          allow_tenant boolean,
          allow_assigned_work boolean)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          WITH resource AS (
            SELECT
              COALESCE(
                target_property_id,
                (SELECT unit."PropertyId"
                 FROM public."Units" unit
                 WHERE unit."Id" = target_unit_id AND unit."PortfolioId" = target_portfolio_id),
                (SELECT work_order."PropertyId"
                 FROM public."WorkOrders" work_order
                 WHERE work_order."Id" = target_work_order_id
                   AND work_order."PortfolioId" = target_portfolio_id),
                (SELECT relationship."PropertyId"
                 FROM public."LeaseManagements" relationship
                 WHERE relationship."Id" = target_lease_management_id
                   AND relationship."PortfolioId" = target_portfolio_id),
                (SELECT relationship."PropertyId"
                 FROM public."TenantAccounts" account
                 JOIN public."LeaseManagements" relationship
                   ON relationship."Id" = account."LeaseManagementId"
                  AND relationship."PortfolioId" = account."PortfolioId"
                 WHERE account."Id" = target_tenant_account_id
                   AND account."PortfolioId" = target_portfolio_id)) AS property_id,
              COALESCE(
                target_unit_id,
                (SELECT work_order."UnitId"
                 FROM public."WorkOrders" work_order
                 WHERE work_order."Id" = target_work_order_id
                   AND work_order."PortfolioId" = target_portfolio_id),
                (SELECT relationship."UnitId"
                 FROM public."LeaseManagements" relationship
                 WHERE relationship."Id" = target_lease_management_id
                   AND relationship."PortfolioId" = target_portfolio_id),
                (SELECT relationship."UnitId"
                 FROM public."TenantAccounts" account
                 JOIN public."LeaseManagements" relationship
                   ON relationship."Id" = account."LeaseManagementId"
                  AND relationship."PortfolioId" = account."PortfolioId"
                 WHERE account."Id" = target_tenant_account_id
                   AND account."PortfolioId" = target_portfolio_id)) AS unit_id,
              COALESCE(
                target_lease_management_id,
                (SELECT work_order."LeaseManagementId"
                 FROM public."WorkOrders" work_order
                 WHERE work_order."Id" = target_work_order_id
                   AND work_order."PortfolioId" = target_portfolio_id),
                (SELECT account."LeaseManagementId"
                 FROM public."TenantAccounts" account
                 WHERE account."Id" = target_tenant_account_id
                   AND account."PortfolioId" = target_portfolio_id)) AS lease_management_id
          )
          SELECT CASE
            WHEN session_user = 'rentalcommand_engine' THEN TRUE
            WHEN NOT public.rc_api_scope_allows(target_portfolio_id) THEN FALSE
            ELSE EXISTS (
              SELECT 1
              FROM resource
              JOIN public."WorkspaceMemberships" membership
                ON membership."AccessContextId" = NULLIF(
                     current_setting('app.current_access_context_id', true), '')::integer
               AND membership."PortfolioId" = target_portfolio_id
              JOIN public."MembershipRoleAssignments" assignment
                ON assignment."WorkspaceMembershipId" = membership."Id"
               AND assignment."PortfolioId" = target_portfolio_id
              WHERE membership."Status" = 'Active'
                AND membership."SuspendedAtUtc" IS NULL
                AND membership."RevokedAtUtc" IS NULL
                AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND assignment."Status" = 'Active'
                AND assignment."SuspendedAtUtc" IS NULL
                AND assignment."RevokedAtUtc" IS NULL
                AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND (assignment."ScopeKind" = 'AllProperties'
                  OR (assignment."ScopeKind" = 'SelectedProperties'
                    AND resource.property_id IS NOT NULL
                    AND EXISTS (
                      SELECT 1
                      FROM public."MembershipRoleAssignmentProperties" selected_property
                      WHERE selected_property."MembershipRoleAssignmentId" = assignment."Id"
                        AND selected_property."PortfolioId" = target_portfolio_id
                        AND selected_property."PropertyId" = resource.property_id)))
            ) OR (allow_owner AND EXISTS (
              SELECT 1
              FROM resource
              JOIN public."Properties" property
                ON property."Id" = resource.property_id
               AND property."PortfolioId" = target_portfolio_id
              JOIN public."OwnerUserAccesses" owner_access
                ON owner_access."OwnerEntityId" = property."OwnerEntityId"
               AND owner_access."PortfolioId" = target_portfolio_id
               AND owner_access."AccessContextId" = NULLIF(
                     current_setting('app.current_access_context_id', true), '')::integer
               AND owner_access."ApplicationUserId" = NULLIF(
                     current_setting('app.current_user_id', true), '')::integer
              WHERE owner_access."RevokedAtUtc" IS NULL
                AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (owner_access."EffectiveToUtc" IS NULL OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
            )) OR (allow_tenant AND EXISTS (
              SELECT 1
              FROM resource
              JOIN public."vw_effective_tenant_access" tenant_access
                ON tenant_access."AccessContextId" = NULLIF(
                     current_setting('app.current_access_context_id', true), '')::integer
               AND tenant_access."UserId" = NULLIF(
                     current_setting('app.current_user_id', true), '')::integer
               AND tenant_access."PortfolioId" = target_portfolio_id
               AND tenant_access."AccessRevision" = NULLIF(
                     current_setting('app.access_revision', true), '')::bigint
              WHERE (resource.property_id IS NULL OR tenant_access."PropertyId" = resource.property_id)
                AND (resource.unit_id IS NULL OR tenant_access."UnitId" = resource.unit_id)
                AND (resource.lease_management_id IS NULL
                     OR tenant_access."LeaseManagementId" = resource.lease_management_id)
                AND (target_tenant_account_id IS NULL
                     OR tenant_access."TenantAccountId" = target_tenant_account_id)
                AND (target_tenant_id IS NULL OR tenant_access."TenantId" = target_tenant_id)
                AND (target_work_order_id IS NULL OR EXISTS (
                  SELECT 1
                  FROM public."WorkOrders" work_order
                  WHERE work_order."Id" = target_work_order_id
                    AND work_order."PortfolioId" = target_portfolio_id
                    AND work_order."TenantId" = tenant_access."TenantId"
                    AND work_order."LeaseManagementId" = tenant_access."LeaseManagementId"))
            )) OR (allow_assigned_work AND target_work_order_id IS NOT NULL AND EXISTS (
              SELECT 1
              FROM public."WorkspaceMemberships" membership
              JOIN public."MembershipRoleAssignments" assignment
                ON assignment."WorkspaceMembershipId" = membership."Id"
               AND assignment."PortfolioId" = membership."PortfolioId"
              JOIN public."WorkOrderResponsibilities" responsibility
                ON responsibility."WorkspaceMembershipId" = membership."Id"
               AND responsibility."MembershipRoleAssignmentId" = assignment."Id"
               AND responsibility."PortfolioId" = target_portfolio_id
               AND responsibility."WorkOrderId" = target_work_order_id
              WHERE membership."AccessContextId" = NULLIF(
                      current_setting('app.current_access_context_id', true), '')::integer
                AND membership."PortfolioId" = target_portfolio_id
                AND membership."Status" = 'Active'
                AND membership."SuspendedAtUtc" IS NULL
                AND membership."RevokedAtUtc" IS NULL
                AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND assignment."ScopeKind" = 'AssignedWorkOrders'
                AND assignment."Status" = 'Active'
                AND assignment."SuspendedAtUtc" IS NULL
                AND assignment."RevokedAtUtc" IS NULL
                AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND responsibility."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (responsibility."EffectiveToUtc" IS NULL
                     OR responsibility."EffectiveToUtc" > CURRENT_TIMESTAMP)
            ))
          END;
        $function$;

        ALTER FUNCTION rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)
          TO rentalcommand_api, rentalcommand_engine;

        ALTER TABLE "Properties" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "Properties" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "Properties";
        DROP POLICY IF EXISTS tenant_select ON "Properties";
        DROP POLICY IF EXISTS tenant_insert ON "Properties";
        DROP POLICY IF EXISTS tenant_update ON "Properties";
        DROP POLICY IF EXISTS tenant_delete ON "Properties";
        CREATE POLICY tenant_select ON "Properties" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", "Id", NULL, NULL, NULL, NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "Properties" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "Id", NULL, NULL, NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "Properties" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", "Id", NULL, NULL, NULL, NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "Id", NULL, NULL, NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "Properties" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "Units" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "Units" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "Units";
        DROP POLICY IF EXISTS tenant_select ON "Units";
        DROP POLICY IF EXISTS tenant_insert ON "Units";
        DROP POLICY IF EXISTS tenant_update ON "Units";
        DROP POLICY IF EXISTS tenant_delete ON "Units";
        CREATE POLICY tenant_select ON "Units" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "Id", NULL, NULL, NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "Units" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "Id", NULL, NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "Units" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "Id", NULL, NULL, NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "Id", NULL, NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "Units" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "LeaseManagements" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "LeaseManagements" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "LeaseManagements";
        DROP POLICY IF EXISTS tenant_select ON "LeaseManagements";
        DROP POLICY IF EXISTS tenant_insert ON "LeaseManagements";
        DROP POLICY IF EXISTS tenant_update ON "LeaseManagements";
        DROP POLICY IF EXISTS tenant_delete ON "LeaseManagements";
        CREATE POLICY tenant_select ON "LeaseManagements" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", NULL, "Id", NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "LeaseManagements" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", NULL, "Id", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "LeaseManagements" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", NULL, "Id", NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", NULL, "Id", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "LeaseManagements" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "LeaseAgreements" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "LeaseAgreements" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "LeaseAgreements";
        DROP POLICY IF EXISTS tenant_select ON "LeaseAgreements";
        DROP POLICY IF EXISTS tenant_insert ON "LeaseAgreements";
        DROP POLICY IF EXISTS tenant_update ON "LeaseAgreements";
        DROP POLICY IF EXISTS tenant_delete ON "LeaseAgreements";
        CREATE POLICY tenant_select ON "LeaseAgreements" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "LeaseAgreements" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "LeaseAgreements" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "LeaseAgreements" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "LeaseAddenda" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "LeaseAddenda" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "LeaseAddenda";
        DROP POLICY IF EXISTS tenant_select ON "LeaseAddenda";
        DROP POLICY IF EXISTS tenant_insert ON "LeaseAddenda";
        DROP POLICY IF EXISTS tenant_update ON "LeaseAddenda";
        DROP POLICY IF EXISTS tenant_delete ON "LeaseAddenda";
        CREATE POLICY tenant_select ON "LeaseAddenda" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "LeaseAddenda" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "LeaseAddenda" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "LeaseAddenda" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "LeaseManagementParties" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "LeaseManagementParties" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "LeaseManagementParties";
        DROP POLICY IF EXISTS tenant_select ON "LeaseManagementParties";
        DROP POLICY IF EXISTS tenant_insert ON "LeaseManagementParties";
        DROP POLICY IF EXISTS tenant_update ON "LeaseManagementParties";
        DROP POLICY IF EXISTS tenant_delete ON "LeaseManagementParties";
        CREATE POLICY tenant_select ON "LeaseManagementParties" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "LeaseManagementParties" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "LeaseManagementParties" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "LeaseManagementParties" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "LeaseRenewalAddendumDecisions" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "LeaseRenewalAddendumDecisions" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "LeaseRenewalAddendumDecisions";
        DROP POLICY IF EXISTS tenant_select ON "LeaseRenewalAddendumDecisions";
        DROP POLICY IF EXISTS tenant_insert ON "LeaseRenewalAddendumDecisions";
        DROP POLICY IF EXISTS tenant_update ON "LeaseRenewalAddendumDecisions";
        DROP POLICY IF EXISTS tenant_delete ON "LeaseRenewalAddendumDecisions";
        CREATE POLICY tenant_select ON "LeaseRenewalAddendumDecisions" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "LeaseRenewalAddendumDecisions" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "LeaseRenewalAddendumDecisions" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "LeaseRenewalAddendumDecisions" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TenantAccounts" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TenantAccounts" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TenantAccounts";
        DROP POLICY IF EXISTS tenant_select ON "TenantAccounts";
        DROP POLICY IF EXISTS tenant_insert ON "TenantAccounts";
        DROP POLICY IF EXISTS tenant_update ON "TenantAccounts";
        DROP POLICY IF EXISTS tenant_delete ON "TenantAccounts";
        CREATE POLICY tenant_select ON "TenantAccounts" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", "Id", NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "TenantAccounts" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", "Id", NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "TenantAccounts" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", "Id", NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, "LeaseManagementId", "Id", NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "TenantAccounts" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "SecurityDepositAccounts" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "SecurityDepositAccounts" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "SecurityDepositAccounts";
        DROP POLICY IF EXISTS tenant_select ON "SecurityDepositAccounts";
        DROP POLICY IF EXISTS tenant_insert ON "SecurityDepositAccounts";
        DROP POLICY IF EXISTS tenant_update ON "SecurityDepositAccounts";
        DROP POLICY IF EXISTS tenant_delete ON "SecurityDepositAccounts";
        CREATE POLICY tenant_select ON "SecurityDepositAccounts" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "SecurityDepositAccounts" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "SecurityDepositAccounts" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "SecurityDepositAccounts" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TenantAccountConditionPeriods" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TenantAccountConditionPeriods" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TenantAccountConditionPeriods";
        DROP POLICY IF EXISTS tenant_select ON "TenantAccountConditionPeriods";
        DROP POLICY IF EXISTS tenant_insert ON "TenantAccountConditionPeriods";
        DROP POLICY IF EXISTS tenant_update ON "TenantAccountConditionPeriods";
        DROP POLICY IF EXISTS tenant_delete ON "TenantAccountConditionPeriods";
        CREATE POLICY tenant_select ON "TenantAccountConditionPeriods" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "TenantAccountConditionPeriods" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_update ON "TenantAccountConditionPeriods" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_delete ON "TenantAccountConditionPeriods" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TenantAutopayEnrollments" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TenantAutopayEnrollments" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TenantAutopayEnrollments";
        DROP POLICY IF EXISTS tenant_select ON "TenantAutopayEnrollments";
        DROP POLICY IF EXISTS tenant_insert ON "TenantAutopayEnrollments";
        DROP POLICY IF EXISTS tenant_update ON "TenantAutopayEnrollments";
        DROP POLICY IF EXISTS tenant_delete ON "TenantAutopayEnrollments";
        CREATE POLICY tenant_select ON "TenantAutopayEnrollments" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "TenantAutopayEnrollments" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_update ON "TenantAutopayEnrollments" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_delete ON "TenantAutopayEnrollments" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TenantLedgerAllocations" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TenantLedgerAllocations" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TenantLedgerAllocations";
        DROP POLICY IF EXISTS tenant_select ON "TenantLedgerAllocations";
        DROP POLICY IF EXISTS tenant_insert ON "TenantLedgerAllocations";
        DROP POLICY IF EXISTS tenant_update ON "TenantLedgerAllocations";
        DROP POLICY IF EXISTS tenant_delete ON "TenantLedgerAllocations";
        CREATE POLICY tenant_select ON "TenantLedgerAllocations" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "TenantLedgerAllocations" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_update ON "TenantLedgerAllocations" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_delete ON "TenantLedgerAllocations" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TenantLedgerEntries" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TenantLedgerEntries" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TenantLedgerEntries";
        DROP POLICY IF EXISTS tenant_select ON "TenantLedgerEntries";
        DROP POLICY IF EXISTS tenant_insert ON "TenantLedgerEntries";
        DROP POLICY IF EXISTS tenant_update ON "TenantLedgerEntries";
        DROP POLICY IF EXISTS tenant_delete ON "TenantLedgerEntries";
        CREATE POLICY tenant_select ON "TenantLedgerEntries" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, TRUE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "TenantLedgerEntries" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_update ON "TenantLedgerEntries" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_delete ON "TenantLedgerEntries" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TenantPaymentAttempts" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TenantPaymentAttempts" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TenantPaymentAttempts";
        DROP POLICY IF EXISTS tenant_select ON "TenantPaymentAttempts";
        DROP POLICY IF EXISTS tenant_insert ON "TenantPaymentAttempts";
        DROP POLICY IF EXISTS tenant_update ON "TenantPaymentAttempts";
        DROP POLICY IF EXISTS tenant_delete ON "TenantPaymentAttempts";
        CREATE POLICY tenant_select ON "TenantPaymentAttempts" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_insert ON "TenantPaymentAttempts" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_update ON "TenantPaymentAttempts" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, NULL, NULL, "TenantAccountId", NULL, FALSE, TRUE, FALSE));
        CREATE POLICY tenant_delete ON "TenantPaymentAttempts" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "WorkOrders" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "WorkOrders" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "WorkOrders";
        DROP POLICY IF EXISTS tenant_select ON "WorkOrders";
        DROP POLICY IF EXISTS tenant_insert ON "WorkOrders";
        DROP POLICY IF EXISTS tenant_update ON "WorkOrders";
        DROP POLICY IF EXISTS tenant_delete ON "WorkOrders";
        CREATE POLICY tenant_select ON "WorkOrders" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", "Id", "LeaseManagementId", NULL, "TenantId", TRUE, TRUE, TRUE));
        CREATE POLICY tenant_insert ON "WorkOrders" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", NULL, "LeaseManagementId", NULL, "TenantId", FALSE, TRUE, FALSE));
        CREATE POLICY tenant_update ON "WorkOrders" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", "Id", "LeaseManagementId", NULL, "TenantId", FALSE, FALSE, TRUE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", "UnitId", "Id", "LeaseManagementId", NULL, "TenantId", FALSE, FALSE, TRUE));
        CREATE POLICY tenant_delete ON "WorkOrders" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "WorkOrderStatusEvents" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "WorkOrderStatusEvents" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "WorkOrderStatusEvents";
        DROP POLICY IF EXISTS tenant_select ON "WorkOrderStatusEvents";
        DROP POLICY IF EXISTS tenant_insert ON "WorkOrderStatusEvents";
        DROP POLICY IF EXISTS tenant_update ON "WorkOrderStatusEvents";
        DROP POLICY IF EXISTS tenant_delete ON "WorkOrderStatusEvents";
        CREATE POLICY tenant_select ON "WorkOrderStatusEvents" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, TRUE, TRUE, TRUE));
        CREATE POLICY tenant_insert ON "WorkOrderStatusEvents" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, TRUE, TRUE));
        CREATE POLICY tenant_update ON "WorkOrderStatusEvents" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE));
        CREATE POLICY tenant_delete ON "WorkOrderStatusEvents" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "TechnicianWorkEntries" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "TechnicianWorkEntries" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "TechnicianWorkEntries";
        DROP POLICY IF EXISTS tenant_select ON "TechnicianWorkEntries";
        DROP POLICY IF EXISTS tenant_insert ON "TechnicianWorkEntries";
        DROP POLICY IF EXISTS tenant_update ON "TechnicianWorkEntries";
        DROP POLICY IF EXISTS tenant_delete ON "TechnicianWorkEntries";
        CREATE POLICY tenant_select ON "TechnicianWorkEntries" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE));
        CREATE POLICY tenant_insert ON "TechnicianWorkEntries" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE));
        CREATE POLICY tenant_update ON "TechnicianWorkEntries" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE));
        CREATE POLICY tenant_delete ON "TechnicianWorkEntries" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "WorkOrderResponsibilities" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "WorkOrderResponsibilities" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "WorkOrderResponsibilities";
        DROP POLICY IF EXISTS tenant_select ON "WorkOrderResponsibilities";
        DROP POLICY IF EXISTS tenant_insert ON "WorkOrderResponsibilities";
        DROP POLICY IF EXISTS tenant_update ON "WorkOrderResponsibilities";
        DROP POLICY IF EXISTS tenant_delete ON "WorkOrderResponsibilities";
        CREATE POLICY tenant_select ON "WorkOrderResponsibilities" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, TRUE));
        CREATE POLICY tenant_insert ON "WorkOrderResponsibilities" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "WorkOrderResponsibilities" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "WorkOrderResponsibilities" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "VendorDispatches" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "VendorDispatches" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "VendorDispatches";
        DROP POLICY IF EXISTS tenant_select ON "VendorDispatches";
        DROP POLICY IF EXISTS tenant_insert ON "VendorDispatches";
        DROP POLICY IF EXISTS tenant_update ON "VendorDispatches";
        DROP POLICY IF EXISTS tenant_delete ON "VendorDispatches";
        CREATE POLICY tenant_select ON "VendorDispatches" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_insert ON "VendorDispatches" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_update ON "VendorDispatches" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", NULL, NULL, "WorkOrderId", NULL, NULL, NULL, FALSE, FALSE, FALSE));
        CREATE POLICY tenant_delete ON "VendorDispatches" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        ALTER TABLE "Conversations" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "Conversations" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "Conversations";
        DROP POLICY IF EXISTS tenant_select ON "Conversations";
        DROP POLICY IF EXISTS tenant_insert ON "Conversations";
        DROP POLICY IF EXISTS tenant_update ON "Conversations";
        DROP POLICY IF EXISTS tenant_delete ON "Conversations";
        CREATE POLICY tenant_select ON "Conversations" FOR SELECT USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, "TenantId", FALSE, TRUE, TRUE));
        CREATE POLICY tenant_insert ON "Conversations" FOR INSERT WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, "TenantId", FALSE, TRUE, TRUE));
        CREATE POLICY tenant_update ON "Conversations" FOR UPDATE USING (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, "TenantId", FALSE, TRUE, TRUE)) WITH CHECK (rc_api_resource_scope_allows("PortfolioId", "PropertyId", NULL, "WorkOrderId", NULL, NULL, "TenantId", FALSE, TRUE, TRUE));
        CREATE POLICY tenant_delete ON "Conversations" FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

        """;

    private const string AcceptedHistoricalPublicHelperAclSql = """
        REVOKE EXECUTE ON FUNCTION rc_public_application_scope_allows(integer) FROM rentalcommand_engine;
        REVOKE EXECUTE ON FUNCTION rc_public_signing_scope_allows(integer) FROM rentalcommand_engine;
        REVOKE EXECUTE ON FUNCTION rc_public_signing_request_allows(integer, integer) FROM rentalcommand_engine;
        REVOKE EXECUTE ON FUNCTION rc_public_signing_artifact_allows(integer, integer) FROM rentalcommand_engine;
        REVOKE EXECUTE ON FUNCTION rc_public_signing_file_allows(integer, integer, text, bigint) FROM rentalcommand_engine;
        """;

    private const string PublicDefinitionPolicyFingerprintSql = """
        SELECT string_agg(definition, E'\n' ORDER BY definition)
        FROM (
          SELECT pg_get_functiondef(procedure.oid) AS definition
          FROM pg_proc procedure
          WHERE procedure.proname LIKE 'rc_public_%_scope_allows'
             OR procedure.proname LIKE 'rc_public_signing_%_allows'
          UNION ALL
          SELECT concat_ws('|', schemaname, tablename, policyname, cmd, qual, with_check)
          FROM pg_policies
          WHERE policyname LIKE 'public_application_%' OR policyname LIKE 'public_signing_%'
        ) definitions
        """;

    private const string PublicAuthorityFingerprintSql = """
        SELECT string_agg(definition, E'\n' ORDER BY definition)
        FROM (
          SELECT pg_get_functiondef(procedure.oid) AS definition
          FROM pg_proc procedure
          WHERE procedure.proname LIKE 'rc_public_%_scope_allows'
             OR procedure.proname LIKE 'rc_public_signing_%_allows'
          UNION ALL
          SELECT concat_ws('|', schemaname, tablename, policyname, cmd, qual, with_check)
          FROM pg_policies
          WHERE policyname LIKE 'public_application_%' OR policyname LIKE 'public_signing_%'
          UNION ALL
          SELECT concat_ws('|', 'acl', procedure.oid::regprocedure::text,
                    'owner=' || owner.rolname,
                    'grantee=' || COALESCE(grantee.rolname, 'PUBLIC'),
                    'privilege=' || acl.privilege_type,
                    'grantable=' || acl.is_grantable::text)
          FROM pg_proc procedure
          JOIN pg_roles owner ON owner.oid = procedure.proowner
          CROSS JOIN LATERAL aclexplode(procedure.proacl) acl
          LEFT JOIN pg_roles grantee ON grantee.oid = acl.grantee
          WHERE procedure.proname IN (
            'rc_public_application_scope_allows',
            'rc_public_signing_scope_allows',
            'rc_public_signing_request_allows',
            'rc_public_signing_artifact_allows',
            'rc_public_signing_file_allows')
        ) definitions
        """;

    private const string PublicHelperAclSql = """
        SELECT string_agg(
          concat_ws('|', procedure.oid::regprocedure::text,
                    'owner=' || owner.rolname,
                    'grantee=' || COALESCE(grantee.rolname, 'PUBLIC'),
                    'privilege=' || acl.privilege_type,
                    'grantable=' || acl.is_grantable::text),
          E'\n' ORDER BY procedure.oid::regprocedure::text, COALESCE(grantee.rolname, 'PUBLIC'))
        FROM pg_proc procedure
        JOIN pg_roles owner ON owner.oid = procedure.proowner
        CROSS JOIN LATERAL aclexplode(procedure.proacl) acl
        LEFT JOIN pg_roles grantee ON grantee.oid = acl.grantee
        WHERE procedure.proname IN (
          'rc_public_application_scope_allows',
          'rc_public_signing_scope_allows',
          'rc_public_signing_request_allows',
          'rc_public_signing_artifact_allows',
          'rc_public_signing_file_allows')
        """;

    private const string InstalledResourcePolicyInventorySql = $"""
        SELECT concat_ws('|', count(DISTINCT tablename), count(*))
        FROM pg_policies
        WHERE policyname IN ('tenant_select','tenant_insert','tenant_update','tenant_delete')
          AND tablename IN ({FullResourcePolicyTableSqlList})
        """;

    private const string InstalledAcceptedHistoricalAuthorityFingerprintSql = $"""
        SELECT string_agg(definition, E'\n' ORDER BY definition)
        FROM (
          SELECT pg_get_functiondef(
            'rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)'::regprocedure) AS definition
          UNION ALL
          SELECT concat_ws('|', schemaname, tablename, policyname, cmd, qual, with_check)
          FROM pg_policies
          WHERE policyname LIKE 'tenant_%'
            AND tablename IN ({FullResourcePolicyTableSqlList})
          UNION ALL
          SELECT concat_ws('|', 'acl', procedure.oid::regprocedure::text,
                    'owner=' || owner.rolname,
                    'grantee=' || COALESCE(grantee.rolname, 'PUBLIC'),
                    'privilege=' || acl.privilege_type,
                    'grantable=' || acl.is_grantable::text)
          FROM pg_proc procedure
          JOIN pg_roles owner ON owner.oid = procedure.proowner
          CROSS JOIN LATERAL aclexplode(procedure.proacl) acl
          LEFT JOIN pg_roles grantee ON grantee.oid = acl.grantee
          WHERE procedure.oid = 'rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)'::regprocedure
        ) definitions
        """;

    private const string InstalledOptimizedAuthorityFingerprintSql = $"""
        SELECT string_agg(definition, E'\n' ORDER BY definition)
        FROM (
          SELECT pg_get_functiondef(procedure.oid) AS definition
          FROM pg_proc procedure
          WHERE procedure.proname IN (
            'rc_api_scope_allows','rc_api_effective_capability_scopes',
            'rc_api_all_properties_scope_allows','rc_api_resource_scope_allows')
          UNION ALL
          SELECT concat_ws('|', schemaname, tablename, policyname, cmd, qual, with_check)
          FROM pg_policies
          WHERE policyname LIKE 'tenant_%'
            AND tablename IN ({FullResourcePolicyTableSqlList})
          UNION ALL
          SELECT concat_ws('|', 'acl', procedure.oid::regprocedure::text,
                    'owner=' || owner.rolname,
                    'grantee=' || COALESCE(grantee.rolname, 'PUBLIC'),
                    'privilege=' || acl.privilege_type,
                    'grantable=' || acl.is_grantable::text)
          FROM pg_proc procedure
          JOIN pg_roles owner ON owner.oid = procedure.proowner
          CROSS JOIN LATERAL aclexplode(procedure.proacl) acl
          LEFT JOIN pg_roles grantee ON grantee.oid = acl.grantee
          WHERE procedure.proname IN (
            'rc_api_scope_allows','rc_api_effective_capability_scopes',
            'rc_api_all_properties_scope_allows','rc_api_resource_scope_allows')
        ) definitions
        """;

    private sealed record PerformanceCapture(
        string Result,
        TimeSpan Elapsed,
        long ScopeCalls,
        long AllPropertiesCalls,
        long ResourceCalls,
        string CommandFingerprint);

    private const string SeedSql = """
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
        INSERT INTO "AspNetUsers" ("Id", "Email", "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "CreatedAt", "DisplayName")
        VALUES (9001, 'rls-plan@example.test', true, 'none', 's', 'c', false, false, false, 0, now(), 'RLS Plan');
        INSERT INTO "Portfolios" ("Id", "Name", "ManagementCompanyName", "TimeZone", "Status", "Currency", "IsSandbox", "CreatedAt", "UpdatedAt")
        VALUES (9001, 'RLS Plan', 'RLS Plan', 'America/New_York', 1, 'USD', false, now(), now());
        UPDATE "Portfolios" SET "PublicApplicationToken"='accepted-public-application-token' WHERE "Id"=9001;
        INSERT INTO "WorkspaceAccessContexts" ("Id", "UserId", "PortfolioId", "AccessRevision", "Status", "CreatedAtUtc", "UpdatedAtUtc")
        VALUES (9001, 9001, 9001, 1, 'Active', now(), now());
        INSERT INTO "WorkspaceMemberships" ("Id", "AccessContextId", "PortfolioId", "Status", "DefaultExperience", "EffectiveFromUtc", "CreatedAtUtc", "UpdatedAtUtc")
        VALUES (9001, 9001, 9001, 'Active', 'Admin', now() - interval '1 day', now(), now());
        INSERT INTO "MembershipRoleAssignments" ("Id", "WorkspaceMembershipId", "PortfolioId", "RoleProfileId", "ScopeKind", "Status", "EffectiveFromUtc", "CreatedAtUtc", "UpdatedAtUtc")
        SELECT 9001, 9001, 9001, "Id", 'AllProperties', 'Active', now() - interval '1 day', now(), now()
        FROM "RoleProfiles" ORDER BY "Id" LIMIT 1;
        INSERT INTO "AuthSessions" ("Id", "UserId", "ActiveAccessContextId", "Status", "CreatedAtUtc", "LastSeenAtUtc", "ExpiresAtUtc")
        VALUES ('11111111-1111-1111-1111-111111111111', 9001, 9001, 'Active', now(), now(), now() + interval '1 day');
        INSERT INTO "Properties" ("Id", "PortfolioId", "Name", "PropertyType", "RentalStructure", "Status", "AddressLine1", "City", "State", "PostalCode", "AccumulatedDepreciation", "CreatedAt", "UpdatedAt")
        SELECT 900000 + g, 9001, 'Property ' || g, 1, 'Unit', 1, g || ' Plan Way', 'Plan', 'NY', '10001', 0, now(), now()
        FROM generate_series(1, 10) g;
        INSERT INTO "Units" ("Id", "PortfolioId", "PropertyId", "UnitNumber", "Bedrooms", "Bathrooms", "MarketRent", "CreatedAt", "UpdatedAt")
        SELECT 910000 + g, 9001, 900000 + (((g - 1) % 10) + 1), 'U-' || g, 2, 1, 1500, now(), now()
        FROM generate_series(1, 2000) g;
        INSERT INTO "LegalDocumentSourceVersions" ("Id", "PublicId", "PortfolioId", "SourceKind", "BusinessKey", "RendererKey", "RendererVersion", "SnapshotPayload", "CreatedAtUtc", "CreatedByUserId")
        VALUES (970001, gen_random_uuid(), 9001, 'BuiltInRenderer', 'rls-plan', 'RlsPlan', 1, '{}'::jsonb, now(), 9001);
        INSERT INTO "LeaseManagements" ("Id", "PublicId", "PortfolioId", "PropertyId", "UnitId", "RelationshipNumber", "EndingDisposition", "CreatedAtUtc", "CreatedByUserId", "UpdatedAtUtc", "RowVersion")
        SELECT 930000 + g, gen_random_uuid(), 9001, 900000 + (((g - 1) % 10) + 1), 910000 + g, 'LM-' || g, 'Undecided', now(), 9001, now(), gen_random_uuid()
        FROM generate_series(1, 1000) g;
        INSERT INTO "TenantAccounts" ("Id", "PublicId", "PortfolioId", "LeaseManagementId", "AccountNumber", "Currency", "OpenedAtUtc", "CreatedAtUtc", "CreatedByUserId")
        SELECT 940000 + g, gen_random_uuid(), 9001, 930000 + g, 'TA-' || g, 'USD', now(), now(), 9001 FROM generate_series(1, 1000) g;
        INSERT INTO "LeaseAgreements" ("Id", "PublicId", "PortfolioId", "LeaseManagementId", "VersionNumber", "AgreementNumber", "ChangeType", "TermType", "TermStartOn", "TermEndOn", "GoverningFromOn", "BaseRentAmount", "RentDueDay", "SecurityDepositObligation", "LateFeeAmount", "GracePeriodDays", "Currency", "TermsSchemaVersion", "TermsPayload", "DocumentSourceVersionId", "CreatedAtUtc", "CreatedByUserId", "UpdatedAtUtc", "DraftRevision")
        SELECT 950000 + g, gen_random_uuid(), 9001, 930000 + g, 1, 'AGR-' || g, 'Initial', 'FixedTerm', current_date, current_date + 365, current_date, 1500, 1, 1500, 75, 5, 'USD', 1, '{}'::jsonb, 970001, now(), 9001, now(), 1
        FROM generate_series(1, 1000) g;
        INSERT INTO "StoredFiles" ("Id", "PortfolioId", "FileName", "FilePath", "ContentType", "FileSize", "EntityType", "EntityId", "UploadedAt")
        VALUES (970003, 9001, 'rls-signing-proof.pdf', 'rls/signing-proof.pdf', 'application/pdf', 1, 'SignatureSigner', 990001, now());
        INSERT INTO "LegalDocumentArtifacts" ("Id", "PublicId", "PortfolioId", "StoredFileId", "ArtifactKind", "StorageKey", "FileName", "ContentType", "ByteLength", "ContentSha256", "LegalIssuanceFingerprint", "CreatedAtUtc", "CreatedByUserId")
        VALUES (970002, gen_random_uuid(), 9001, 970003, 'IssuedAgreement', 'rls/signing-proof.pdf', 'rls-signing-proof.pdf', 'application/pdf', 1, repeat('b', 64), repeat('c', 64), now(), 9001);
        INSERT INTO "SignatureRequests" ("Id", "PublicId", "PortfolioId", "LeaseAgreementId", "Provider", "IdempotencyKey", "Status", "Subject", "IssuedArtifactId", "PreparedAtUtc", "ExecutionAttemptCount", "CreatedByUserId")
        VALUES (980001, gen_random_uuid(), 9001, 950001, 'native', 'rls-signing-proof', 'Prepared', 'RLS signing proof', 970002, now(), 0, 9001);
        INSERT INTO "SignatureSigners" ("Id", "PortfolioId", "SignatureRequestId", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired", "TokenHash", "TokenExpiresAtUtc", "Status", "SignatureType", "CreatedAtUtc", "UpdatedAtUtc")
        VALUES (990001, 9001, 980001, 'RLS Signer', 'rls-signer@example.test', 1, true, repeat('a', 64), now() + interval '1 day', 'Pending', 'None', now(), now());
        INSERT INTO "SecurityDepositAccounts" ("Id", "PortfolioId", "TenantAccountId", "OriginatingAgreementId", "Currency", "CreatedAtUtc", "CreatedByUserId")
        SELECT 960000 + g, 9001, 940000 + g, 950000 + g, 'USD', now(), 9001 FROM generate_series(1, 1000) g;
        INSERT INTO "TenantLedgerEntries" ("Id","PublicId","PortfolioId","TenantAccountId","EntryType","Direction","Amount","Currency","EffectiveOn","DueOn","PostedAtUtc","Description","BusinessKey","LeaseAgreementId","CreatedByUserId")
        SELECT 10000000+g,gen_random_uuid(),9001,940000+g,'RentCharge','Debit',1500,'USD',current_date,current_date,now(),'Rent charge','rls-charge-'||g,950000+g,9001
        FROM generate_series(1,1000) g;
        INSERT INTO "TenantLedgerEntries" ("Id","PublicId","PortfolioId","TenantAccountId","EntryType","Direction","Amount","Currency","EffectiveOn","PostedAtUtc","Description","BusinessKey","LeaseAgreementId","CreatedByUserId")
        SELECT 11000000+g,gen_random_uuid(),9001,940000+g,'PaymentReceipt','Credit',1500,'USD',current_date,now(),'Rent receipt','rls-receipt-'||g,950000+g,9001
        FROM generate_series(1,1000) g;
        INSERT INTO "TenantLedgerAllocations" ("Id","PortfolioId","TenantAccountId","DebitEntryId","CreditEntryId","Amount","AllocatedAtUtc","BusinessKey","CreatedByUserId")
        SELECT 12000000+g,9001,940000+g,10000000+g,11000000+g,1500,now(),'rls-allocation-'||g,9001
        FROM generate_series(1,1000) g;
        ANALYZE "Units"; ANALYZE "SecurityDepositAccounts"; ANALYZE "TenantAccounts"; ANALYZE "LeaseAgreements";
        ANALYZE "TenantLedgerEntries"; ANALYZE "TenantLedgerAllocations";
        """;
}
