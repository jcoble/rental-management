using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// The load-bearing proof for the audit free-text search rewrite: <see
/// cref="AuditQueryService.ApplySearch"/> matches the visible audit-row fields with Postgres
/// <c>ILIKE</c> over the trgm-backed (<c>EntityType</c>/<c>ActorLabel</c>/<c>IpAddress</c>) columns
/// plus integer id-equality. SQLite has no <c>ILIKE</c> operator, so the case-insensitive / IP-substring
/// behavior can only be verified against a real Postgres — the SQLite unit twin asserted the same fields
/// but under a different operator (<c>LIKE</c>/no-op), which is why this lives here.
///
/// <para>Spins its OWN Postgres (Testcontainers, same pattern as <see cref="RlsTenantIsolationTests"/>),
/// applies migrations as the owner (which also creates the <c>AuditSearchTrgmIndexes</c>), seeds three
/// audit rows under one portfolio, and asserts the search hits. Queries run on the owner connection
/// (RLS bypassed); the service still scopes <c>WHERE PortfolioId == portfolioId</c>, which is what these
/// tests rely on.</para>
///
/// <para>Requires Docker. When Docker is unavailable the container fails to start and the test is
/// reported as <b>skipped</b> (via <c>[SkippableFact]</c> + <c>Skip.IfNot</c>) rather than failing.</para>
/// </summary>
public sealed class AuditSearchTests : IAsyncLifetime
{
    // Built inside InitializeAsync (not as a field initializer): PostgreSqlBuilder.Build() validates
    // the Docker endpoint eagerly, so building it here lets a missing daemon be caught and skipped.
    private PostgreSqlContainer? _pg;

    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;
    private int _portfolioId;

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

        // Apply all migrations as the owner (creates the pg_trgm GIN indexes ApplySearch relies on,
        // plus the RLS roles/policies and every table).
        await using var ctx = NewContext(_ownerConnString);
        await ctx.Database.MigrateAsync();

        // One portfolio (AuditLog.PortfolioId is a required FK → Portfolio), captured by id.
        var portfolio = new Portfolio
        {
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();
        _portfolioId = portfolio.Id;

        // Three audit rows whose EntityType / EntityId / Operation / ActorLabel / IpAddress we control,
        // so the search assertions target exactly the fields the audit page renders. No users needed
        // (each row carries its own ActorLabel, so no UserId FK to satisfy).
        ctx.AuditLogs.AddRange(
            new AuditLog
            {
                PortfolioId = _portfolioId, EntityType = "Expense", EntityId = 76,
                Operation = AuditLogOperation.Created, ActorLabel = "Jane Landlord",
                IpAddress = "203.0.113.5", Timestamp = DateTime.UtcNow.AddMinutes(-3),
            },
            new AuditLog
            {
                PortfolioId = _portfolioId, EntityType = "WorkOrder", EntityId = 11,
                Operation = AuditLogOperation.Updated, ActorLabel = "Bob Staff",
                IpAddress = "198.51.100.9", Timestamp = DateTime.UtcNow.AddMinutes(-2),
            },
            new AuditLog
            {
                PortfolioId = _portfolioId, EntityType = "Payment", EntityId = 7,
                Operation = AuditLogOperation.Created, ActorLabel = "Jane Landlord",
                IpAddress = "203.0.113.5", Timestamp = DateTime.UtcNow.AddMinutes(-1),
            });
        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Search_Matches_Visible_Fields_On_Postgres()
    {
        SkipIfNoDocker();

        await using var db = NewContext(_ownerConnString);
        var sut = new AuditQueryService(db, new AuditDescriber(), new AuditDiffBuilder());

        async Task<List<int>> SearchIds(string term)
        {
            var page = await sut.ListAsync(_portfolioId, null, null, null, new ListQuery { Search = term });
            return page.Select(e => e.EntityId).ToList();
        }

        // Bare numeric "76" → matches the Expense #76 row by integer id equality.
        (await SearchIds("76")).Should().Equal(76);
        // Compound entity label exactly as rendered → parsed id.
        (await SearchIds("Expense #76")).Should().Equal(76);
        (await SearchIds("WorkOrder 11")).Should().Equal(11);
        // Entity type via ILIKE.
        (await SearchIds("payment")).Should().Equal(7);
        // Actor label via ILIKE (lowercase term → case-insensitive match on "Bob Staff").
        (await SearchIds("bob")).Should().Equal(11);
        // IP address via ILIKE substring → both Jane rows share 203.0.113.5.
        (await SearchIds("203.0.113")).Should().BeEquivalentTo(new[] { 76, 7 });
        // Action verb → operation. "updated" narrows to the WorkOrder row.
        (await SearchIds("updated")).Should().Equal(11);
        // Friendly verb the describer renders for Created → both Created rows.
        (await SearchIds("recorded")).Should().BeEquivalentTo(new[] { 76, 7 });
    }

    // ───────────────────────────────── helpers ─────────────────────────────────

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; audit-search runtime verification skipped.");

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);
}
