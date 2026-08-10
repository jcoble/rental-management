using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;
using RentalCommand.Data;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Authoritative provider proof for the Schedule E depreciation function and its composable query.
/// SQLite service tests exercise report behavior, but only PostgreSQL proves the deployed numeric
/// function executes beneath the authorized property/asset UNION and aggregate.
/// </summary>
public sealed class ScheduleEDepreciationPostgreSqlTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_schedule_e")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(ScheduleEDepreciationFunctionSql.Create);
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task AuthorizedPropertyAndAssetDepreciation_UnionsGroupsAndSumsInPostgreSql()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL Schedule E verification.");
        var commands = new List<string>();
        await using var db = NewContext([new QueryRecorder(commands)]);
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Portfolios.Add(new Portfolio
        {
            Id = 1,
            Name = "Schedule E",
            ManagementCompanyName = "Schedule E",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        var selected = Property("Selected", now, 300_000m, 60_000m, 2020);
        var selectedSecond = Property("Selected second", now, 275_000m, 50_000m, 2021);
        var decoy = Property("Decoy", now, 550_000m, 50_000m, 2019);
        db.Properties.AddRange(selected, selectedSecond, decoy);
        await db.SaveChangesAsync();
        db.CapitalAssets.AddRange(
            Asset(selected.Id, "Selected roof", now, 9_900m),
            Asset(selectedSecond.Id, "Selected HVAC", now, 5_500m),
            Asset(decoy.Id, "Decoy roof", now, 19_800m));
        await db.SaveChangesAsync();

        // The function is IMMUTABLE, so its date parts must not vary with the connection's zone.
        // This asset's midnight-UTC service date falls on the prior local day in Los Angeles.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'America/Los_Angeles';");

        var authorizedProperties = db.Properties
            .AsNoTracking()
            .Where(property => property.PortfolioId == 1 && property.Id != decoy.Id);
        var query = ScheduleEDepreciationQuery.Build(
            db, 1, 2025, authorizedProperties);
        var sql = query.ToQueryString();

        commands.Clear();
        var rows = await query.ToListAsync();

        commands.Should().ContainSingle();
        commands[0].Should().Contain(ScheduleEDepreciationDbFunction.Name);
        rows.Should().HaveCount(2);
        rows.Single(row => row.PropertyId == selected.Id).Amount.Should().Be(8_862.27m);
        rows.Single(row => row.PropertyId == selectedSecond.Id).Amount.Should().Be(8_256.82m);
        rows.Select(row => row.TotalAmount).Should().OnlyContain(amount => amount == 17_119.09m);
        sql.Should().Contain(ScheduleEDepreciationDbFunction.Name);
        sql.Should().Contain("UNION ALL");
        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("CapitalAssets");
        sql.Should().Contain("Properties");
        sql.Should().Contain("PortfolioId");
        sql.Should().Contain("InServiceDate");
        sql.Should().Contain("yearEndExclusive");
        sql.Should().Contain("DisposedOnDate");
        sql.Should().Contain("EXISTS");
    }

    private RentalCommandDbContext NewContext(IEnumerable<IInterceptor>? interceptors = null)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString);
        if (interceptors is not null)
            options.AddInterceptors(interceptors);
        return new RentalCommandDbContext(options.Options);
    }

    private static Property Property(
        string name,
        DateTime now,
        decimal purchasePrice,
        decimal landValue,
        int inServiceYear) => new()
    {
        PortfolioId = 1,
        Name = name,
        AddressLine1 = $"1 {name} St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        PurchasePrice = purchasePrice,
        LandValue = landValue,
        InServiceDate = new DateTime(inServiceYear, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static CapitalAsset Asset(
        int propertyId,
        string description,
        DateTime now,
        decimal costBasis) => new()
    {
        PortfolioId = 1,
        PropertyId = propertyId,
        Description = description,
        CostBasis = costBasis,
        InServiceDate = new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        Method = DepreciationMethod.StraightLine,
        RecoveryYears = RecoveryClass.ResidentialBuilding,
        Convention = DepreciationConvention.MidMonth,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
