using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Testcontainers.PostgreSql;
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
    private const string ApiPassword = "rls-api-test-password";
    private const string EnginePassword = "rls-engine-test-password";

    // Built inside InitializeAsync (not as a field initializer): PostgreSqlBuilder.Build() validates
    // the Docker endpoint eagerly, so building it here lets a missing daemon be caught and skipped
    // instead of throwing in the test constructor.
    private PostgreSqlContainer? _pg;

    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;

    private int _portfolioA;
    private int _portfolioB;
    private int _isolatedTenantA;
    private int _isolatedTenantB;
    private RuntimeScopeSeed _scopeA = null!;
    private RuntimeScopeSeed _scopeB = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
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
            await ctx.Database.ExecuteSqlRawAsync(
                $"ALTER ROLE {ApiRole} PASSWORD '{ApiPassword}'; ALTER ROLE {EngineRole} PASSWORD '{EnginePassword}';");
        }

        // Seed two portfolios' data as the owner/superuser (which bypasses RLS for the inserts).
        await using (var ctx = NewContext(_ownerConnString))
        {
            _scopeA = await SeedPortfolioWithOverdueChargeAsync(ctx, "Portfolio A", "PO-A");
            _scopeB = await SeedPortfolioWithOverdueChargeAsync(ctx, "Portfolio B", "PO-B");
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
        relationships.Should().Contain(relationship => relationship.RelationshipNumber == "PO-A");
        relationships.Should().NotContain(relationship => relationship.RelationshipNumber == "PO-B",
            "portfolio A must never see portfolio B's lease relationships — RLS is the security boundary");
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

        relationshipNumbers.Should().Contain("PO-A");
        relationshipNumbers.Should().NotContain("PO-B",
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
        ctx.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            AccessContextId = accessContext.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
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
            authSession.Id);
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
        Guid AuthSessionId);
}
