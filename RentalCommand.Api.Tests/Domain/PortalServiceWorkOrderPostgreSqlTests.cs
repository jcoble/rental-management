using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
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

    [Fact]
    public async Task CreateTenantWorkOrderAsync_UsesInjectedClockForAtomicRows()
    {
        var simulatedNow = new DateTimeOffset(2027, 1, 21, 5, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(simulatedNow);
        var securityNow = await _context.Db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync();
        var sessionExpiresAt = securityNow.AddDays(1);
        sessionExpiresAt.Should().BeBefore(simulatedNow.UtcDateTime);
        var scenario = await SeedScenarioAsync(sessionExpiresAt);
        await using var services = AtomicDomainTestKernel.CreateForWorkOrdersPostgreSql(
            _context.ConnectionString,
            clock);
        var service = new PortalService(
            _context.Db,
            Mock.Of<ILeaseQaService>(),
            clock,
            services.GetRequiredService<IAtomicUnitOfWork>());

        var created = await service.CreateTenantWorkOrderAsync(
            new ActiveAccessContext(
                scenario.AuthSessionId,
                UserId,
                scenario.Scope.AccessContextId,
                PortfolioId,
                scenario.Scope.AccessRevision,
                WorkspaceExperience.Tenant,
                null,
                WorkspaceExperience.Tenant),
            new CreateTenantWorkOrderRequest
            {
                Title = "Kitchen sink leak",
                Description = "Water is dripping under the cabinet.",
                Category = "Plumbing",
                Priority = WorkOrderPriority.High,
            },
            "tenant-work-order-sim-clock-proof");

        created.Should().NotBeNull();
        created!.RequestedAt.Should().Be(simulatedNow.UtcDateTime);
        created.UpdatedAt.Should().Be(simulatedNow.UtcDateTime);

        var persisted = await _context.Db.WorkOrders
            .AsNoTracking()
            .SingleAsync(workOrder => workOrder.Id == created.Id);
        persisted.RequestedAt.Should().Be(simulatedNow.UtcDateTime);
        persisted.UpdatedAt.Should().Be(simulatedNow.UtcDateTime);

        var statusEvent = await _context.Db.WorkOrderStatusEvents
            .AsNoTracking()
            .SingleAsync(item => item.WorkOrderId == created.Id);
        statusEvent.CreatedAtUtc.Should().Be(simulatedNow.UtcDateTime);

        var audit = await _context.Db.AtomicAuditLogs
            .AsNoTracking()
            .SingleAsync(item => item.EntityType == nameof(WorkOrder) && item.EntityId == created.Id);
        audit.Timestamp.Should().Be(simulatedNow.UtcDateTime);

        var outbox = await _context.Db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(item => item.IdempotencyKey == "tenant-work-order-create:tenant-work-order-sim-clock-proof");
        outbox.CreatedAtUtc.Should().Be(simulatedNow.UtcDateTime);
        outbox.NextAttemptAtUtc.Should().Be(simulatedNow.UtcDateTime);
    }

    private async Task<Scenario> SeedScenarioAsync(DateTime? sessionExpiresAtUtc = null)
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
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = Now,
            LastSeenAtUtc = Now,
            ExpiresAtUtc = sessionExpiresAtUtc ?? new DateTime(2027, 1, 22, 5, 0, 0, DateTimeKind.Utc),
        };
        _context.Db.AuthSessions.Add(session);

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
            session.Id,
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
        Guid AuthSessionId,
        int TenantId,
        int BeforeBoundaryId,
        int StartBoundaryId,
        int EndBoundaryId,
        int ExclusiveEndId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
