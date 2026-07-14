using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Owner distributions are first-class owner payouts. They are portfolio-scoped, owner-scoped,
/// optional-property-linked, soft-deleted, and never represented as expenses.
/// </summary>
public sealed class OwnerDistributionServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int Year = 2026;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly OwnerDistributionService _sut;

    public OwnerDistributionServiceTests()
    {
        _ctx = new SqliteTestContext([new OwnerDistributionRecordingCommandInterceptor(_commands)]);
        _sut = new OwnerDistributionService(
            _ctx.Db, TimeProvider.System, Mock.Of<IAtomicUnitOfWork>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void MutationsExposeOnlyScopedReceiptBackedOverloads()
    {
        var mutationMethods = typeof(IOwnerDistributionService).GetMethods()
            .Where(method => method.Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync")
            .ToArray();

        mutationMethods.Should().HaveCount(3);
        mutationMethods.Should().OnlyContain(method =>
            method.GetParameters().First().ParameterType ==
                typeof(RentalCommand.Core.Authorization.WorkspaceReadScope) &&
            method.GetParameters().Any(parameter => parameter.Name == "idempotencyKey"));
    }

    [Fact]
    public async Task ListPageAsync_FiltersSortsAndPagesInSql()
    {
        var owner = SeedOwner("Acme Holdings");
        var otherOwner = SeedOwner("Beta Estates");
        SeedDistribution(owner.Id, 100m, new DateTime(Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedDistribution(owner.Id, 200m, new DateTime(Year, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedDistribution(owner.Id, 999m, new DateTime(Year - 1, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedDistribution(otherOwner.Id, 300m, new DateTime(Year, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        _commands.Clear();

        var page = await _sut.ListPageAsync(PortfolioId, new OwnerDistributionListQuery
        {
            OwnerEntityId = owner.Id,
            Year = Year,
            Sort = "-date",
            Skip = 0,
            Take = 1,
        });

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle();
        page.Items[0].Amount.Should().Be(200m);

        _commands.Should().Contain(command =>
            command.Contains("FROM \"OwnerDistributions\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("COUNT", StringComparison.OrdinalIgnoreCase),
            "paged lists must count in SQL");
        _commands.Should().Contain(command =>
            command.Contains("FROM \"OwnerDistributions\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             command.Contains("FETCH", StringComparison.OrdinalIgnoreCase)),
            "paged lists must sort and page in SQL");
    }

    [Fact]
    public async Task SumForOwnerYearAsync_SumsDbSideAndExcludesOtherYears()
    {
        var owner = SeedOwner("Acme Holdings");
        SeedDistribution(owner.Id, 100m, new DateTime(Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedDistribution(owner.Id, 250m, new DateTime(Year, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedDistribution(owner.Id, 999m, new DateTime(Year - 1, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        _commands.Clear();

        var total = await _sut.SumForOwnerYearAsync(PortfolioId, owner.Id, Year);

        total.Should().Be(350m);
        _commands.Should().Contain(command =>
            command.Contains("FROM \"OwnerDistributions\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("SUM", StringComparison.OrdinalIgnoreCase),
            "yearly owner distribution totals must be summed in SQL");
    }

    private void SeedPortfolio(int id)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = id,
            Name = $"Portfolio {id}",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private OwnerEntity SeedOwner(string name, int portfolioId = PortfolioId)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = portfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();
        return owner;
    }

    private Property SeedProperty(int ownerEntityId, string name, int portfolioId = PortfolioId)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = portfolioId,
            OwnerEntityId = ownerEntityId,
            Name = name,
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private OwnerDistribution SeedDistribution(
        int ownerEntityId,
        decimal amount,
        DateTime date,
        int? propertyId = null)
    {
        var now = DateTime.UtcNow;
        var distribution = new OwnerDistribution
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = ownerEntityId,
            PropertyId = propertyId,
            Date = date,
            Amount = amount,
            Method = DistributionMethod.Check,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerDistributions.Add(distribution);
        _ctx.Db.SaveChanges();
        return distribution;
    }

    private sealed class OwnerDistributionRecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
