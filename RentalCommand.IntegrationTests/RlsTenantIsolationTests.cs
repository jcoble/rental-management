using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// The load-bearing proof for audit M-1: PostgreSQL Row-Level Security genuinely isolates tenants at
/// the database layer, independent of the application-layer <c>.Where(x =&gt; x.PortfolioId == ...)</c>
/// filters. Modeled on EdiPlatform's <c>MultiTenant_RealRls_Tests</c> + <c>Tier2RlsBase</c>:
///
/// <list type="number">
///   <item>Spin a real Postgres, apply migrations as the owner/superuser (which creates the
///   <c>rentalcommand_api</c> role and the <c>tenant_isolation</c> policies), and seed two
///   portfolios' rows.</item>
///   <item>Connect directly as the non-superuser <c>rentalcommand_api</c> login and apply the
///   canonical durable session/access coordinates used by the request interceptor.</item>
///   <item>Query WITHOUT a <c>WHERE PortfolioId = ...</c> clause. If RLS were broken or the app
///   connected as a superuser, both portfolios' rows would return and the assertion would fail. That
///   is the whole point: RLS — not the app filter — does the work here.</item>
/// </list>
///
/// <para>Requires Docker. When Docker is unavailable the container fails to start and every test is
/// reported as <b>skipped</b> (via <c>[SkippableFact]</c> + <c>Skip.IfNot</c>) rather than failing,
/// so the suite stays green in Docker-less environments; the policy SQL + the SQLite EF-filter tests
/// remain the static proof there.</para>
/// </summary>
public sealed class RlsTenantIsolationTests : IAsyncLifetime
{
    private const string ApiRole = "rentalcommand_api";
    private const string EngineRole = "rentalcommand_engine";
    private const string ApiPassword = SharedPostgreSqlDatabase.ApiPassword;
    private const string EnginePassword = SharedPostgreSqlDatabase.EnginePassword;

    // Acquired inside InitializeAsync so a missing Docker daemon can be caught and skipped instead
    // of throwing in the test constructor.
    private SharedPostgreSqlDatabase? _pg;

    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;

    private int _portfolioA;
    private int _portfolioB;
    private int _isolatedTenantA;
    private int _isolatedTenantB;
    private RuntimeScopeSeed _scopeA = null!;
    private RuntimeScopeSeed _scopeB = null!;
    private ResourceScopeSeed _resourceScopes = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            // No Docker daemon (or image pull/build-validation failed) — tests report as skipped.
            _dockerAvailable = false;
            return;
        }

        _ownerConnString = _pg.GetConnectionString();

        // Apply all migrations as the owner. This creates the rentalcommand_api role + the
        // tenant_isolation policies + ENABLE/FORCE RLS on every portfolio-scoped table.
        await using (var ctx = NewContext(_ownerConnString))
        {
            await ctx.Database.MigrateAsync();
        }

        // Seed two portfolios' data as the owner/superuser (which bypasses RLS for the inserts).
        await using (var ctx = NewContext(_ownerConnString))
        {
            _scopeA = await SeedPortfolioWithOverdueChargeAsync(
                ctx, "Portfolio A", "DEMO-LM-PO-A");
            _scopeB = await SeedPortfolioWithOverdueChargeAsync(
                ctx, "Portfolio B", "DEMO-LM-PO-B");
            _portfolioA = _scopeA.PortfolioId;
            _portfolioB = _scopeB.PortfolioId;
            ctx.PlaidTokenExchangeAttempts.AddRange(
                PlaidAttempt(_portfolioA, "operation-a"),
                PlaidAttempt(_portfolioB, "operation-b"));
            var connectionA = NewAccountingConnection(_portfolioA);
            var connectionB = NewAccountingConnection(_portfolioB);
            ctx.AccountingConnections.AddRange(connectionA, connectionB);
            await ctx.SaveChangesAsync();
            var mappingA = AccountingMapping(_portfolioA, connectionA.Id, "customer-a");
            var mappingB = AccountingMapping(_portfolioB, connectionB.Id, "customer-b");
            ctx.AccountingEntityMappings.AddRange(mappingA, mappingB);
            await ctx.SaveChangesAsync();
            ctx.AccountingMappingPromotionJobs.AddRange(
                PromotionJob(_portfolioA, connectionA.Id, mappingA.Id),
                PromotionJob(_portfolioB, connectionB.Id, mappingB.Id));
            var isolatedA = IsolatedTenant(_portfolioA, "isolated-a");
            var isolatedB = IsolatedTenant(_portfolioB, "isolated-b");
            ctx.Tenants.AddRange(isolatedA, isolatedB);
            await ctx.SaveChangesAsync();
            _isolatedTenantA = isolatedA.Id;
            _isolatedTenantB = isolatedB.Id;
            _resourceScopes = await SeedResourceScopesAsync(ctx, _scopeA);
        }
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Rls_PortfolioA_SeesOnlyOwnLeaseManagements_WhenQueryingWithoutWhereClause()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await using var ctx = NewContext(conn);

        // CRITICAL: no WHERE clause. RLS must filter to portfolio A only.
        var relationships = await ctx.LeaseManagements
            .Select(relationship => new
            {
                relationship.Id,
                relationship.PortfolioId,
                relationship.RelationshipNumber,
            })
            .ToListAsync();

        relationships.Should().OnlyContain(relationship => relationship.PortfolioId == _portfolioA,
            "RLS must filter LeaseManagements to the current portfolio even with no app-layer predicate");
        relationships.Should().Contain(relationship =>
            relationship.RelationshipNumber == "DEMO-LM-PO-A");
        relationships.Should().NotContain(relationship =>
            relationship.RelationshipNumber == "DEMO-LM-PO-B",
            "portfolio A must never see portfolio B's lease relationships — RLS is the security boundary");
    }

    [SkippableFact]
    public async Task Rls_DemoSeedPortfolioDiscovery_IsCrossPortfolioForApiOnly()
    {
        SkipIfNoDocker();

        await using var api = await OpenDirectRoleAsync(ApiRole, ApiPassword);
        (await ExecScalarIntAsync(api, """
            SELECT count(DISTINCT "PortfolioId")
            FROM public."LeaseManagements"
            WHERE "RelationshipNumber" LIKE 'DEMO-LM-%'
            """))
            .Should().Be(0,
                "a blank-scope API connection must not bypass LeaseManagements RLS directly");

        var portfolioIds = new List<int>();
        await using (var command = api.CreateCommand())
        {
            command.CommandText = """
                SELECT "Value"
                FROM public.rc_demo_seed_portfolio_ids()
                ORDER BY "Value"
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                portfolioIds.Add(reader.GetInt32(0));
            }
        }

        portfolioIds.Should().Equal(_portfolioA, _portfolioB);

        await using var engine = await OpenDirectRoleAsync(EngineRole, EnginePassword);
        var engineCall = async () => await ExecScalarIntAsync(
            engine,
            "SELECT count(*) FROM public.rc_demo_seed_portfolio_ids()");
        (await engineCall.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [SkippableFact]
    public async Task Rls_PortfolioA_SeesOnlyOwnTenantLedgerEntries_WhenQueryingWithoutWhereClause()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await using var ctx = NewContext(conn);

        // Canonical tenant-money surfaces read the immutable ledger by scalar account FK. Prove the
        // database boundary isolates those entries even when the application supplies no predicate.
        var entries = await ctx.TenantLedgerEntries
            .Select(entry => new { entry.PortfolioId, entry.Amount })
            .ToListAsync();

        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(entry => entry.PortfolioId == _portfolioA,
            "RLS must filter TenantLedgerEntries to the current portfolio");
    }

    [SkippableFact]
    public async Task Rls_WriteForOtherPortfolio_IsRejectedByWithCheck()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await using var ctx = NewContext(conn);

        // Attempt to INSERT a row stamped with the OTHER portfolio's id while scoped to A. The
        // policy's WITH CHECK clause must reject it (RLS protects writes, not just reads).
        ctx.Tenants.Add(new Tenant
        {
            PortfolioId = _portfolioB,
            FirstName = "Cross",
            LastName = "Tenant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the WITH CHECK clause must block writing a row for a different portfolio");
    }

    [SkippableFact]
    public async Task Rls_CrossPortfolioSelectUpdateAndDeleteAreInvisible_AndOwnRowCannotMoveWorkspaces()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);

        (await ExecScalarIntAsync(conn,
            $"SELECT count(*) FROM \"Tenants\" WHERE \"Id\" = {_isolatedTenantB}"))
            .Should().Be(0, "another workspace's row must be invisible even when its exact ID is known");
        (await ExecAffectedAsync(conn,
            $"UPDATE \"Tenants\" SET \"FirstName\" = 'stolen' WHERE \"Id\" = {_isolatedTenantB}"))
            .Should().Be(0, "an exact-ID update must not reach another workspace's row");
        (await ExecAffectedAsync(conn,
            $"DELETE FROM \"Tenants\" WHERE \"Id\" = {_isolatedTenantB}"))
            .Should().Be(0, "an exact-ID delete must not reach another workspace's row");

        var moveAcrossWorkspace = async () => await ExecAffectedAsync(conn,
            $"UPDATE \"Tenants\" SET \"PortfolioId\" = {_portfolioB} WHERE \"Id\" = {_isolatedTenantA}");
        (await moveAcrossWorkspace.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "WITH CHECK must reject moving an authorized row into another workspace");
    }

    [SkippableFact]
    public async Task Rls_EveryClassifiedTableIsEnabledForcedAndHasAPolicy_InTheMigratedDatabase()
    {
        SkipIfNoDocker();

        var expected = FoundationBaselinePostgreSql.DirectPortfolioTables
            .Concat(FoundationBaselinePostgreSql.NullablePortfolioTables)
            .Concat(FoundationBaselinePostgreSql.ChildPortfolioTables.Select(policy => policy.Table))
            .Append("Portfolios")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await using var conn = new NpgsqlConnection(_ownerConnString);
        await conn.OpenAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = """
            WITH expected(table_name) AS (
              SELECT unnest($1::text[])
            )
            SELECT
              count(*) FILTER (WHERE relation.oid IS NULL) AS missing_table_count,
              count(*) FILTER (WHERE relation.oid IS NOT NULL AND NOT relation.relrowsecurity) AS rls_disabled_count,
              count(*) FILTER (WHERE relation.oid IS NOT NULL AND NOT relation.relforcerowsecurity) AS rls_not_forced_count,
              count(*) FILTER (WHERE relation.oid IS NOT NULL AND NOT EXISTS (
                SELECT 1
                FROM pg_policies policy
                WHERE policy.schemaname = 'public'
                  AND policy.tablename = expected.table_name
              )) AS missing_policy_count
            FROM expected
            LEFT JOIN pg_class relation
              ON relation.relname = expected.table_name
             AND relation.relnamespace = 'public'::regnamespace
             AND relation.relkind IN ('r', 'p')
            """;
        command.Parameters.AddWithValue(expected);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(0, "every classified table must exist after migration");
        reader.GetInt64(1).Should().Be(0, "RLS must be enabled on every classified table");
        reader.GetInt64(2).Should().Be(0, "RLS must be forced even for the table owner");
        reader.GetInt64(3).Should().Be(0, "every classified table must have an installed policy");
    }

    [SkippableFact]
    public async Task Rls_PlaidExchangeAdmission_SeesOwnAttempt_AndRejectsCrossPortfolioWrite()
    {
        SkipIfNoDocker();
        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await using (var roleCommand = conn.CreateCommand())
        {
            roleCommand.CommandText = "SELECT current_user";
            (await roleCommand.ExecuteScalarAsync()).Should().Be(ApiRole);
        }
        await using (var forceCommand = conn.CreateCommand())
        {
            forceCommand.CommandText = """
                SELECT count(*)
                FROM pg_class
                WHERE relname IN ('AccountingMappingPromotionJobs', 'PlaidTokenExchangeAttempts')
                  AND relrowsecurity
                  AND relforcerowsecurity
                """;
            Convert.ToInt32(await forceCommand.ExecuteScalarAsync()).Should().Be(2,
                "both durable recovery tables must run with ENABLE and FORCE ROW LEVEL SECURITY");
        }
        await using var ctx = NewContext(conn);

        var operations = await ctx.PlaidTokenExchangeAttempts
            .OrderBy(row => row.ClientOperationId)
            .Select(row => row.ClientOperationId)
            .ToListAsync();
        operations.Should().Equal("operation-a");
        var jobs = await ctx.AccountingMappingPromotionJobs
            .Select(row => row.PortfolioId)
            .ToListAsync();
        jobs.Should().Equal(_portfolioA);

        ctx.PlaidTokenExchangeAttempts.Add(PlaidAttempt(_portfolioB, "cross-write"));
        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task Rls_ForgedLegacySettings_DoNotWidenApiScope()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await ExecAsync(conn,
            "SET app.is_admin = 'true'; SET app.rls_bypass_reason = 'SandboxGraduation';");

        await using var ctx = NewContext(conn);
        var relationshipNumbers = await ctx.LeaseManagements
            .Select(relationship => relationship.RelationshipNumber)
            .ToListAsync();

        relationshipNumbers.Should().Contain("DEMO-LM-PO-A");
        relationshipNumbers.Should().NotContain("DEMO-LM-PO-B",
            "caller-controlled legacy settings are not an authorization mechanism");
    }

    [SkippableFact]
    public async Task Rls_RuntimeLoginsCannotAssumeEachOtherOrTheAuthorityRole()
    {
        SkipIfNoDocker();

        await using var api = await OpenAsApiRoleAsync(_portfolioA);
        await using var engine = await OpenDirectRoleAsync(EngineRole, EnginePassword);

        foreach (var (connection, targetRole) in new[]
                 {
                     (api, EngineRole),
                     (api, "rentalcommand_rls_authority"),
                     (engine, ApiRole),
                     (engine, "rentalcommand_rls_authority"),
                 })
        {
            var assume = async () => await ExecAsync(connection, $"SET ROLE {targetRole}");
            (await assume.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await ExecScalarIntAsync(engine, "SELECT count(*) FROM \"LeaseManagements\""))
            .Should().BeGreaterThanOrEqualTo(2,
                "the direct Engine identity has its own cross-workspace policy path");
    }

    [SkippableFact]
    public async Task Rls_EngineCanInsertOrdinaryAtomicAuditWithoutReceivingPreAuthAdmission()
    {
        SkipIfNoDocker();

        await using var engine = await OpenDirectRoleAsync(EngineRole, EnginePassword);
        var attemptId = Guid.NewGuid();
        var insertedId = await ExecScalarIntAsync(engine, $"""
            INSERT INTO "AtomicAuditLogs"
              ("AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
               "PortfolioId", "ActorLabel", "EntityType", "EntityId", "Operation",
               "ChangeReason", "Timestamp")
            VALUES
              ('{attemptId}', 'engine.atomic-audit.rls-regression', '{attemptId:N}', 1,
               {_portfolioA}, 'engine:rls-regression', 'EngineRegression', 1, 1,
               'Engine ordinary audit admission regression', CURRENT_TIMESTAMP)
            RETURNING "Id"
            """);

        insertedId.Should().BePositive();
        (await ExecScalarIntAsync(engine, $"""
            SELECT count(*)
            FROM "AtomicAuditLogs"
            WHERE "Id" = {insertedId}
              AND "CommandType" = 'engine.atomic-audit.rls-regression'
            """)).Should().Be(1);

        (await ExecScalarIntAsync(engine, """
            SELECT (
              NOT rc_pre_auth_email_audit_allows(
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
              AND NOT rc_pre_auth_account_security_audit_allows(
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
            )::integer
            """)).Should().Be(1,
                "the Engine EXECUTE grants permit policy evaluation but both API-only pre-auth branches stay false");
    }

    [SkippableFact]
    public async Task Rls_SameWorkspaceResourceScopes_BlockUnrelatedRowsAndWrites()
    {
        SkipIfNoDocker();

        await AssertResourceVisibilityAsync(
            _resourceScopes.SelectedProperty,
            expectedPropertyCount: 1,
            expectedWorkOrderId: _resourceScopes.InScopeWorkOrderId,
            expectedAccountId: _scopeA.TenantAccountId);
        await AssertResourceVisibilityAsync(
            _resourceScopes.Owner,
            expectedPropertyCount: 1,
            expectedWorkOrderId: _resourceScopes.InScopeWorkOrderId,
            expectedAccountId: _scopeA.TenantAccountId);
        await AssertResourceVisibilityAsync(
            _resourceScopes.Tenant,
            expectedPropertyCount: 1,
            expectedWorkOrderId: _resourceScopes.InScopeWorkOrderId,
            expectedAccountId: _scopeA.TenantAccountId);
        await AssertResourceVisibilityAsync(
            _resourceScopes.Technician,
            expectedPropertyCount: 0,
            expectedWorkOrderId: _resourceScopes.InScopeWorkOrderId,
            expectedAccountId: null);

        await using (var selected = await OpenAsApiRoleAsync(_resourceScopes.SelectedProperty))
        {
            (await ExecAffectedAsync(selected,
                $"UPDATE \"Properties\" SET \"Name\" = 'stolen' WHERE \"Id\" = {_resourceScopes.DecoyPropertyId}"))
                .Should().Be(0, "selected-property authority cannot update a same-workspace decoy");
            (await ExecAffectedAsync(selected,
                $"DELETE FROM \"Properties\" WHERE \"Id\" = {_resourceScopes.DecoyPropertyId}"))
                .Should().Be(0, "ordinary selected-property authority cannot delete a same-workspace decoy");

            var insertDecoy = async () => await ExecAffectedAsync(selected, $"""
                INSERT INTO "Properties"
                  ("PortfolioId", "Name", "PropertyType", "Status", "AddressLine1", "City", "State",
                   "PostalCode", "AccumulatedDepreciation", "CreatedAt", "UpdatedAt")
                VALUES
                  ({_scopeA.PortfolioId}, 'out of scope', 1, 1, '9 Other', 'Columbus', 'OH', '43219',
                   0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                """);
            (await insertDecoy.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "a selected-property assignment cannot create an unassigned Property");
        }

        await using (var owner = await OpenAsApiRoleAsync(_resourceScopes.Owner))
        {
            (await ExecAffectedAsync(owner,
                $"UPDATE \"Properties\" SET \"Name\" = 'owner mutation' WHERE \"Id\" = {_scopeA.PropertyId}"))
                .Should().Be(0, "an Owner relationship is read authority, not Team mutation authority");
        }

        await using (var tenant = await OpenAsApiRoleAsync(_resourceScopes.Tenant))
        {
            (await ExecAffectedAsync(tenant,
                $"UPDATE \"TenantAccounts\" SET \"CloseNote\" = 'tenant mutation' WHERE \"Id\" = {_scopeA.TenantAccountId}"))
                .Should().Be(0, "a Tenant relationship cannot rewrite its canonical account row");
        }

        await using (var technician = await OpenAsApiRoleAsync(_resourceScopes.Technician))
        {
            (await ExecAffectedAsync(technician,
                $"UPDATE \"WorkOrders\" SET \"Status\" = 2 WHERE \"Id\" = {_resourceScopes.InScopeWorkOrderId}"))
                .Should().Be(1, "assigned-work authority may update the assigned work order");
            (await ExecAffectedAsync(technician,
                $"UPDATE \"WorkOrders\" SET \"Status\" = 2 WHERE \"Id\" = {_resourceScopes.DecoyWorkOrderId}"))
                .Should().Be(0, "assigned-work authority cannot update a same-workspace decoy");
        }
    }

    [SkippableFact]
    public async Task Rls_TenantRelationship_CanInsertAndReturnOwnWorkOrder()
    {
        SkipIfNoDocker();

        await using var tenant = await OpenAsApiRoleAsync(_resourceScopes.Tenant);
        (await ExecScalarIntAsync(tenant,
            $"SELECT rc_api_scope_allows({_scopeA.PortfolioId})::int"))
            .Should().Be(1);
        (await ExecScalarIntAsync(tenant, $"""
            SELECT rc_api_resource_scope_allows(
              {_scopeA.PortfolioId}, {_scopeA.PropertyId}, {_scopeA.UnitId}, NULL,
              {_scopeA.LeaseManagementId}, NULL, {_scopeA.TenantId}, FALSE, TRUE, FALSE)::int
            """)).Should().Be(1);
        var insertedId = await ExecScalarIntAsync(tenant, $"""
            INSERT INTO "WorkOrders"
              ("PortfolioId", "PropertyId", "UnitId", "TenantId", "LeaseManagementId",
               "Title", "Description", "Category", "Priority", "Status", "RequestedAt",
               "CreatedBy", "UpdatedAt", "ActualCost", "EstimatedCost")
            VALUES
              ({_scopeA.PortfolioId}, {_scopeA.PropertyId}, {_scopeA.UnitId}, {_scopeA.TenantId},
               {_scopeA.LeaseManagementId}, 'Leaking kitchen faucet',
               'Water drips under sink after use', 'Resident Request', 1, 1,
               CURRENT_TIMESTAMP, 'Tenant', CURRENT_TIMESTAMP, 0, 0)
            RETURNING "Id"
            """);

        insertedId.Should().BePositive();
        (await ReadIdsAsync(tenant, "WorkOrders")).Should().Contain(insertedId);

        await using var unrelatedTenant = await OpenAsApiRoleAsync(_scopeB);
        (await ReadIdsAsync(unrelatedTenant, "WorkOrders")).Should().NotContain(insertedId);
    }

    // ----- helpers -----

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; RLS runtime verification skipped.");

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);

    private static RentalCommandDbContext NewContext(NpgsqlConnection conn) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(conn).Options);

    /// <summary>
    /// Open a fresh connection directly as the non-superuser API login and set the same canonical
    /// session/access coordinates as the request-time interceptor.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsApiRoleAsync(int portfolioId)
    {
        var scope = portfolioId == _portfolioA ? _scopeA : _scopeB;
        var conn = await OpenDirectRoleAsync(ApiRole, ApiPassword);
        await ExecAsync(conn,
            $"SET app.auth_session_id = '{scope.AuthSessionId}'; " +
            $"SET app.current_user_id = '{scope.UserId}'; " +
            $"SET app.current_access_context_id = '{scope.AccessContextId}'; " +
            $"SET app.access_revision = '{scope.AccessRevision}'; " +
            $"SET app.current_portfolio_id = '{portfolioId}';");
        return conn;
    }

    private async Task<NpgsqlConnection> OpenAsApiRoleAsync(RuntimeScopeSeed scope)
    {
        var conn = await OpenDirectRoleAsync(ApiRole, ApiPassword);
        await ExecAsync(conn,
            $"SET app.auth_session_id = '{scope.AuthSessionId}'; " +
            $"SET app.current_user_id = '{scope.UserId}'; " +
            $"SET app.current_access_context_id = '{scope.AccessContextId}'; " +
            $"SET app.access_revision = '{scope.AccessRevision}'; " +
            $"SET app.current_portfolio_id = '{scope.PortfolioId}';");
        return conn;
    }

    private async Task AssertResourceVisibilityAsync(
        RuntimeScopeSeed scope,
        int expectedPropertyCount,
        int expectedWorkOrderId,
        int? expectedAccountId)
    {
        await using var conn = await OpenAsApiRoleAsync(scope);
        (await ExecScalarIntAsync(conn, "SELECT count(*) FROM \"Properties\""))
            .Should().Be(expectedPropertyCount);
        (await ReadIdsAsync(conn, "WorkOrders"))
            .Should().Equal(expectedWorkOrderId);
        var accountIds = await ReadIdsAsync(conn, "TenantAccounts");
        accountIds.Should().Equal(expectedAccountId is null ? [] : [expectedAccountId.Value]);
    }

    private static async Task<int[]> ReadIdsAsync(NpgsqlConnection conn, string table)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = $"SELECT \"Id\" FROM \"{table}\" ORDER BY \"Id\"";
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        return ids.ToArray();
    }

    private async Task<NpgsqlConnection> OpenDirectRoleAsync(string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(_ownerConnString)
        {
            Username = username,
            Password = password,
            Pooling = false,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecAffectedAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecScalarIntAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static Tenant IsolatedTenant(int portfolioId, string tag) => new()
    {
        PortfolioId = portfolioId,
        FirstName = "RLS",
        LastName = tag,
        Email = $"{tag}@example.test",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static async Task<ResourceScopeSeed> SeedResourceScopesAsync(
        RentalCommandDbContext ctx,
        RuntimeScopeSeed primary)
    {
        var now = DateTime.UtcNow;
        var grantingUserId = primary.UserId;
        var ownerEntity = new OwnerEntity
        {
            PortfolioId = primary.PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "RLS scoped owner",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OwnerEntities.Add(ownerEntity);
        var primaryProperty = await ctx.Properties.SingleAsync(property => property.Id == primary.PropertyId);
        ctx.PropertyOwnerships.Add(new PropertyOwnership
        {
            PortfolioId = primary.PortfolioId,
            Property = primaryProperty,
            OwnerEntity = ownerEntity,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = now.AddDays(-1),
            StatementRecipientName = ownerEntity.Name,
            PayeeName = ownerEntity.Name,
        });

        var decoyProperty = new Property
        {
            PortfolioId = primary.PortfolioId,
            Name = "Same-workspace decoy",
            AddressLine1 = "2 Other",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var decoyUnit = new Unit
        {
            PortfolioId = primary.PortfolioId,
            Property = decoyProperty,
            UnitNumber = "2",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var decoyTenant = IsolatedTenant(primary.PortfolioId, "same-workspace-decoy");
        ctx.AddRange(decoyProperty, decoyUnit, decoyTenant);
        await ctx.SaveChangesAsync();

        var decoyRelationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = primary.PortfolioId,
            PropertyId = decoyProperty.Id,
            UnitId = decoyUnit.Id,
            RelationshipNumber = "RLS-DECOY",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = grantingUserId,
            RowVersion = Guid.NewGuid(),
        };
        ctx.LeaseManagements.Add(decoyRelationship);
        await ctx.SaveChangesAsync();
        var decoyAccount = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = primary.PortfolioId,
            LeaseManagementId = decoyRelationship.Id,
            AccountNumber = "TA-RLS-DECOY",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = grantingUserId,
        };
        var decoyParty = new LeaseManagementParty
        {
            PortfolioId = primary.PortfolioId,
            LeaseManagementId = decoyRelationship.Id,
            TenantId = decoyTenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "RLS same-workspace decoy",
            CreatedAtUtc = now,
            CreatedByUserId = grantingUserId,
        };
        var inScopeWorkOrder = WorkOrderFor(
            primary.PortfolioId, primary.PropertyId, primary.UnitId, primary.TenantId,
            primary.LeaseManagementId, "Assigned work");
        var decoyWorkOrder = WorkOrderFor(
            primary.PortfolioId, decoyProperty.Id, decoyUnit.Id, decoyTenant.Id,
            decoyRelationship.Id, "Same-workspace decoy work");
        ctx.AddRange(decoyAccount, decoyParty, inScopeWorkOrder, decoyWorkOrder);
        await ctx.SaveChangesAsync();

        var selected = await CreateTeamScopeAsync(
            ctx, primary, "selected", roleProfileId: 2,
            MembershipRoleAssignmentScopeKind.SelectedProperties, primary.PropertyId, now);
        var technician = await CreateTeamScopeAsync(
            ctx, primary, "technician", roleProfileId: 4,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders, selectedPropertyId: null, now);
        ctx.WorkOrderResponsibilities.Add(new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = primary.PortfolioId,
            PropertyId = primary.PropertyId,
            WorkOrderId = inScopeWorkOrder.Id,
            WorkspaceMembershipId = technician.WorkspaceMembershipId!.Value,
            MembershipRoleAssignmentId = technician.RoleAssignmentId!.Value,
            Kind = WorkOrderResponsibilityKind.Primary,
            EffectiveFromUtc = now,
            AssignedByUserId = grantingUserId,
            AssignedByAccessContextId = primary.AccessContextId,
            AssignedReason = "RLS assigned-work fixture",
            AssignedAtUtc = now,
        });

        var owner = await CreateRelationshipScopeAsync(ctx, primary, "owner", now);
        ctx.OwnerUserAccesses.Add(new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = primary.PortfolioId,
            AccessContextId = owner.Scope.AccessContextId,
            ApplicationUserId = owner.Scope.UserId,
            OwnerEntityId = ownerEntity.Id,
            EffectiveFromUtc = now,
            GrantedAtUtc = now,
            GrantedByUserId = grantingUserId,
            Reason = "RLS Owner fixture",
        });

        var tenant = await CreateRelationshipScopeAsync(ctx, primary, "tenant", now);
        var primaryPartyId = await ctx.LeaseManagementParties
            .Where(party => party.LeaseManagementId == primary.LeaseManagementId
                && party.TenantId == primary.TenantId)
            .Select(party => party.Id)
            .SingleAsync();
        ctx.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = primary.PortfolioId,
            AccessContextId = tenant.Scope.AccessContextId,
            ApplicationUserId = tenant.Scope.UserId,
            LeaseManagementPartyId = primaryPartyId,
            GrantedAtUtc = now,
            GrantedByUserId = grantingUserId,
            Reason = "RLS Tenant fixture",
        });
        await ctx.SaveChangesAsync();

        return new ResourceScopeSeed(
            selected.Scope,
            technician.Scope,
            owner.Scope,
            tenant.Scope,
            inScopeWorkOrder.Id,
            decoyWorkOrder.Id,
            decoyProperty.Id,
            decoyAccount.Id);
    }

    private static WorkOrder WorkOrderFor(
        int portfolioId,
        int propertyId,
        int unitId,
        int tenantId,
        int leaseManagementId,
        string title) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        UnitId = unitId,
        TenantId = tenantId,
        LeaseManagementId = leaseManagementId,
        Title = title,
        Description = title,
        RequestedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static async Task<CreatedScope> CreateTeamScopeAsync(
        RentalCommandDbContext ctx,
        RuntimeScopeSeed resource,
        string tag,
        int roleProfileId,
        MembershipRoleAssignmentScopeKind scopeKind,
        int? selectedPropertyId,
        DateTime now)
    {
        var created = await CreateScopeAsync(ctx, resource, tag, now, withMembership: true);
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = created.WorkspaceMembershipId!.Value,
            PortfolioId = resource.PortfolioId,
            RoleProfileId = roleProfileId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        ctx.MembershipRoleAssignments.Add(assignment);
        await ctx.SaveChangesAsync();
        if (selectedPropertyId is not null)
        {
            ctx.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignmentId = assignment.Id,
                PortfolioId = resource.PortfolioId,
                PropertyId = selectedPropertyId.Value,
            });
            await ctx.SaveChangesAsync();
        }
        return created with { RoleAssignmentId = assignment.Id };
    }

    private static Task<CreatedScope> CreateRelationshipScopeAsync(
        RentalCommandDbContext ctx,
        RuntimeScopeSeed resource,
        string tag,
        DateTime now) => CreateScopeAsync(ctx, resource, tag, now, withMembership: false);

    private static async Task<CreatedScope> CreateScopeAsync(
        RentalCommandDbContext ctx,
        RuntimeScopeSeed resource,
        string tag,
        DateTime now,
        bool withMembership)
    {
        var user = new ApplicationUser
        {
            UserName = $"rls-resource-{tag}@example.test",
            NormalizedUserName = $"RLS-RESOURCE-{tag.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"rls-resource-{tag}@example.test",
            NormalizedEmail = $"RLS-RESOURCE-{tag.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"RLS resource {tag}",
            CreatedAt = now,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = resource.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        ctx.WorkspaceAccessContexts.Add(context);
        await ctx.SaveChangesAsync();
        WorkspaceMembership? membership = null;
        if (withMembership)
        {
            membership = new WorkspaceMembership
            {
                AccessContextId = context.Id,
                PortfolioId = resource.PortfolioId,
                Status = WorkspaceMembershipStatus.Active,
                EffectiveFromUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.WorkspaceMemberships.Add(membership);
        }
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = context.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        ctx.AuthSessions.Add(session);
        await ctx.SaveChangesAsync();
        return new CreatedScope(
            new RuntimeScopeSeed(
                resource.PortfolioId, user.Id, context.Id, context.AccessRevision, session.Id,
                resource.PropertyId, resource.UnitId, resource.TenantId,
                resource.LeaseManagementId, resource.TenantAccountId),
            membership?.Id,
            RoleAssignmentId: null);
    }

    private static async Task<RuntimeScopeSeed> SeedPortfolioWithOverdueChargeAsync(
        RentalCommandDbContext ctx, string name, string tag)
    {
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = name,
            ManagementCompanyName = "Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();

        var actor = new ApplicationUser
        {
            UserName = $"rls-{tag.ToLowerInvariant()}@example.test",
            NormalizedUserName = $"RLS-{tag.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"rls-{tag.ToLowerInvariant()}@example.test",
            NormalizedEmail = $"RLS-{tag.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"RLS {tag}",
            CreatedAt = now,
        };
        ctx.Users.Add(actor);
        await ctx.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        ctx.WorkspaceAccessContexts.Add(accessContext);
        await ctx.SaveChangesAsync();
        var workspaceMembership = new WorkspaceMembership
        {
            AccessContextId = accessContext.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        ctx.WorkspaceMemberships.Add(workspaceMembership);
        var authSession = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        ctx.AuthSessions.Add(authSession);
        await ctx.SaveChangesAsync();
        ctx.MembershipRoleAssignments.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = workspaceMembership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await ctx.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = "Prop",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            Property = property,
            UnitNumber = "1",
            MarketRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id,
            FirstName = "T",
            LastName = tag,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.AddRange(property, unit, tenant);
        await ctx.SaveChangesAsync();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = tag,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        ctx.LeaseManagements.Add(relationship);
        await ctx.SaveChangesAsync();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{tag}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{tag}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 1000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                portfolio.Id, actor.Id, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "RLS isolation fixture",
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        ctx.AddRange(account, agreement, party);
        await ctx.SaveChangesAsync();

        ctx.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1000m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now.AddDays(-3)),
            DueOn = DateOnly.FromDateTime(now.AddDays(-3)),
            PostedAtUtc = now,
            Description = "Overdue rent",
            BusinessKey = $"rls-rent:{tag}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = actor.Id,
        });
        await ctx.SaveChangesAsync();

        return new RuntimeScopeSeed(
            portfolio.Id,
            actor.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            authSession.Id,
            property.Id,
            unit.Id,
            tenant.Id,
            relationship.Id,
            account.Id);
    }

    private static PlaidTokenExchangeAttempt PlaidAttempt(int portfolioId, string operationId) => new()
    {
        Id = Guid.NewGuid(),
        PortfolioId = portfolioId,
        ClientOperationId = operationId,
        RequestHash = new string('a', 64),
        PublicTokenHash = new string('b', 64),
        InstitutionName = "RLS bank",
        AccountName = "Operating",
        ExternalAccountIdCipherText = "protected",
        ExternalAccountIdHash = new string('c', 64),
        Status = "Prepared",
        PreparedAtUtc = DateTime.UtcNow,
    };

    private static AccountingConnection NewAccountingConnection(int portfolioId) => new()
    {
        PortfolioId = portfolioId,
        Provider = AccountingProvider.QuickBooks,
        Status = AccountingConnectionStatus.Connected,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static AccountingEntityMapping AccountingMapping(int portfolioId, int connectionId, string externalId) => new()
    {
        PortfolioId = portfolioId,
        AccountingConnectionId = connectionId,
        ExternalType = "Customer",
        ExternalId = externalId,
        LocalEntityType = "Tenant",
        Revision = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static AccountingMappingPromotionJob PromotionJob(int portfolioId, int connectionId, int mappingId) => new()
    {
        Id = Guid.NewGuid(),
        PortfolioId = portfolioId,
        AccountingConnectionId = connectionId,
        AccountingEntityMappingId = mappingId,
        MappingRevision = 1,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private sealed record RuntimeScopeSeed(
        int PortfolioId,
        int UserId,
        int AccessContextId,
        long AccessRevision,
        Guid AuthSessionId,
        int PropertyId,
        int UnitId,
        int TenantId,
        int LeaseManagementId,
        int TenantAccountId);

    private sealed record CreatedScope(
        RuntimeScopeSeed Scope,
        int? WorkspaceMembershipId,
        int? RoleAssignmentId);

    private sealed record ResourceScopeSeed(
        RuntimeScopeSeed SelectedProperty,
        RuntimeScopeSeed Technician,
        RuntimeScopeSeed Owner,
        RuntimeScopeSeed Tenant,
        int InScopeWorkOrderId,
        int DecoyWorkOrderId,
        int DecoyPropertyId,
        int DecoyTenantAccountId);
}
