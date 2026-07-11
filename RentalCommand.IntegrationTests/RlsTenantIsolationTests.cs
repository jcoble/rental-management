using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
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
///   <item>Switch the connection into the non-superuser <c>rentalcommand_api</c> role and set
///   <c>app.current_portfolio_id</c> for portfolio A (the exact contract the request-time connection
///   interceptor applies) — so RLS fires the way it does in production.</item>
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

    // Built inside InitializeAsync (not as a field initializer): PostgreSqlBuilder.Build() validates
    // the Docker endpoint eagerly, so building it here lets a missing daemon be caught and skipped
    // instead of throwing in the test constructor.
    private PostgreSqlContainer? _pg;

    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;

    private int _portfolioA;
    private int _portfolioB;

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
        }

        // Seed two portfolios' data as the owner/superuser (which bypasses RLS for the inserts).
        await using (var ctx = NewContext(_ownerConnString))
        {
            _portfolioA = await SeedPortfolioWithOverduePaymentAsync(ctx, "Portfolio A", "PO-A");
            _portfolioB = await SeedPortfolioWithOverduePaymentAsync(ctx, "Portfolio B", "PO-B");
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
            await ctx.SaveChangesAsync();
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
    public async Task Rls_PortfolioA_SeesOnlyOwnLeases_WhenQueryingWithoutWhereClause()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await using var ctx = NewContext(conn);

        // CRITICAL: no WHERE clause. RLS must filter to portfolio A only.
        var leases = await ctx.Leases.Select(l => new { l.Id, l.PortfolioId, l.LeaseNumber }).ToListAsync();

        leases.Should().OnlyContain(l => l.PortfolioId == _portfolioA,
            "RLS must filter Leases to the current portfolio even with no app-layer predicate");
        leases.Should().Contain(l => l.LeaseNumber == "PO-A");
        leases.Should().NotContain(l => l.LeaseNumber == "PO-B",
            "portfolio A must never see portfolio B's leases — RLS is the security boundary");
    }

    [SkippableFact]
    public async Task Rls_PortfolioA_SeesOnlyOwnPayments_WhenQueryingWithoutWhereClause()
    {
        SkipIfNoDocker();

        await using var conn = await OpenAsApiRoleAsync(_portfolioA);
        await using var ctx = NewContext(conn);

        // The H-4 surface (payment aggregates) read Payments by scalar FK — prove the DB layer also
        // isolates them by portfolio with no app-layer filter.
        var payments = await ctx.Payments.Select(p => new { p.PortfolioId, p.Amount }).ToListAsync();

        payments.Should().NotBeEmpty();
        payments.Should().OnlyContain(p => p.PortfolioId == _portfolioA,
            "RLS must filter Payments to the current portfolio");
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
    public async Task Rls_AdminContext_SeesBothPortfolios()
    {
        SkipIfNoDocker();

        // app.is_admin = true (the background-worker / platform-admin path) bypasses the portfolio
        // predicate — both portfolios visible.
        await using var conn = new NpgsqlConnection(_ownerConnString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"SET ROLE {ApiRole}; SET app.current_portfolio_id = '0'; SET app.is_admin = 'true';");

        await using var ctx = NewContext(conn);
        var leases = await ctx.Leases.Select(l => l.LeaseNumber).ToListAsync();

        leases.Should().Contain("PO-A").And.Contain("PO-B",
            "an admin context bypasses RLS via app.is_admin=true; both portfolios visible");
    }

    // ----- helpers -----

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; RLS runtime verification skipped.");

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);

    private static RentalCommandDbContext NewContext(NpgsqlConnection conn) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(conn).Options);

    /// <summary>
    /// Open a fresh connection, switch into the non-superuser rentalcommand_api role and set the
    /// portfolio GUC — the exact session state the request-time RlsConnectionInterceptor produces.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsApiRoleAsync(int portfolioId)
    {
        var conn = new NpgsqlConnection(_ownerConnString);
        await conn.OpenAsync();
        await ExecAsync(conn,
            $"SET ROLE {ApiRole}; SET app.current_portfolio_id = '{portfolioId}'; SET app.is_admin = 'false';");
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> SeedPortfolioWithOverduePaymentAsync(
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
        var unit = new Unit { Property = property, UnitNumber = "1", MarketRent = 1000m, CreatedAt = now, UpdatedAt = now };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id,
            FirstName = "T",
            LastName = tag,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = portfolio.Id,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = tag,
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1000m,
            SecurityDeposit = 1000m,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Leases.Add(lease);
        await ctx.SaveChangesAsync();

        ctx.Payments.Add(new Payment
        {
            PortfolioId = portfolio.Id,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1000m,
            DueDate = now.AddDays(-3),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();

        return portfolio.Id;
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
}
