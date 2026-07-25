using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class PortalServiceWorkOrderPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int UserId = 1;
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public PortalServiceWorkOrderPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkOrderPage_ThisMonthUsesUtcHalfOpenBoundariesInPostgreSql()
    {
        var scenario = await SeedScenarioAsync();
        _commands.Clear();

        var page = await new PortalService(
                _context.Db,
                Mock.Of<ILeaseQaService>(),
                TimeProvider.System)
            .ListWorkOrdersPageAsync(
                scenario.Scope,
                scenario.TenantId,
                new PortalTenantWorkOrderListQuery
                {
                    From = new DateTime(2026, 7, 1),
                    To = new DateTime(2026, 7, 31),
                    OpenOnly = false,
                    Sort = "status",
                    Skip = 0,
                    Take = 20,
                });

        page.TotalCount.Should().Be(2);
        page.Items.Select(item => item.Id).Should()
            .BeEquivalentTo([scenario.StartBoundaryId, scenario.EndBoundaryId]);
        page.Items.Should().NotContain(item =>
            item.Id == scenario.BeforeBoundaryId || item.Id == scenario.ExclusiveEndId);

        _commands.Should().HaveCount(2, "count and bounded page stay server-side");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("\"RequestedAt\" >=", StringComparison.Ordinal)
            && sql.Contains("\"RequestedAt\" <", StringComparison.Ordinal));
        _commands.Should().ContainSingle(sql =>
            sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase));
        _commands.Should().ContainSingle(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"Status\"", StringComparison.Ordinal)
            && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Scenario> SeedScenarioAsync()
    {
        var accessContext = new WorkspaceAccessContext
        {
            UserId = UserId,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Tenant maintenance property",
            AddressLine1 = "1 Repair Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Tenant",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.AddRange(accessContext, property, tenant);
        await _context.Db.SaveChangesAsync();

        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "1",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Units.Add(unit);
        await _context.Db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-TENANT-MAINTENANCE",
            PossessionGivenAtUtc = Now.AddMonths(-1),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            CreatedByUserId = UserId,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.LeaseManagements.Add(management);
        await _context.Db.SaveChangesAsync();

        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2026, 6, 1),
            ChangeReason = "tenant maintenance PostgreSQL proof",
            CreatedAtUtc = Now,
            CreatedByUserId = UserId,
        };
        _context.Db.LeaseManagementParties.Add(party);
        await _context.Db.SaveChangesAsync();

        _context.Db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            AccessContextId = accessContext.Id,
            ApplicationUserId = UserId,
            LeaseManagementPartyId = party.Id,
            GrantedAtUtc = Now,
            GrantedByUserId = UserId,
            Reason = "tenant maintenance PostgreSQL proof",
        });

        var workOrders = new[]
        {
            WorkOrder("Before boundary", new DateTime(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc)),
            WorkOrder("Start boundary", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
            WorkOrder("End boundary", new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc)),
            WorkOrder("Exclusive end", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
        };
        _context.Db.WorkOrders.AddRange(workOrders);
        await _context.Db.SaveChangesAsync();

        return new Scenario(
            new PortalTenantReadScope(
                PortfolioId,
                UserId,
                accessContext.Id,
                accessContext.AccessRevision),
            tenant.Id,
            workOrders[0].Id,
            workOrders[1].Id,
            workOrders[2].Id,
            workOrders[3].Id);

        WorkOrder WorkOrder(string title, DateTime requestedAt) => new()
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseManagementId = management.Id,
            Title = title,
            Description = $"{title} repair",
            Category = "General",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = requestedAt,
            UpdatedAt = requestedAt,
        };
    }

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record Scenario(
        PortalTenantReadScope Scope,
        int TenantId,
        int BeforeBoundaryId,
        int StartBoundaryId,
        int EndBoundaryId,
        int ExclusiveEndId);
}
