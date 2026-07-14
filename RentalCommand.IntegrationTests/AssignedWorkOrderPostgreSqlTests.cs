using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class AssignedWorkOrderPostgreSqlTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<UpdateAssignedWorkOrderResult> UpdateCodec =
        new("assigned-work-order.update.v1");
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public AssignedWorkOrderPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<UpdateAssignedWorkOrderCommand, UpdateAssignedWorkOrderResult,
            UpdateAssignedWorkOrderHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task AssignedTechnician_UpdateReplaysOnce_AndUnassignedWorkIsRejected()
    {
        var scenario = await SeedScenarioAsync();
        var command = new UpdateAssignedWorkOrderCommand(
            scenario.PortfolioId,
            scenario.UserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            scenario.AssignedWorkOrderId,
            scenario.AssignedUpdatedAt,
            WorkOrderStatus.InProgress,
            "Parts confirmed on site.",
            null,
            null,
            null,
            "assigned-update-replay");
        var identity = new AtomicCommandIdentity(
            "assigned-work-order.update",
            $"{scenario.PortfolioId}:{scenario.AccessContextId}:{scenario.AssignedWorkOrderId}:assigned-update-replay");

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, UpdateCodec),
            Atomic.ExecuteAsync(identity, command, UpdateCodec));

        outcomes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes.Select(outcome => outcome.Value.Outcome)
            .Should().OnlyContain(outcome => outcome == UpdateAssignedWorkOrderOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        var assigned = await _context.Db.WorkOrders.AsNoTracking()
            .SingleAsync(workOrder => workOrder.Id == scenario.AssignedWorkOrderId);
        assigned.Status.Should().Be(WorkOrderStatus.InProgress);
        (await _context.Db.WorkOrderStatusEvents.CountAsync(statusEvent =>
            statusEvent.WorkOrderId == scenario.AssignedWorkOrderId)).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType && receipt.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);

        var unassigned = new UpdateAssignedWorkOrderCommand(
            scenario.PortfolioId,
            scenario.UserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            scenario.UnassignedWorkOrderId,
            scenario.UnassignedUpdatedAt,
            WorkOrderStatus.InProgress,
            "Must not be accepted.",
            null,
            null,
            null,
            "unassigned-update-denied");
        var denied = async () => await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "assigned-work-order.update",
                $"{scenario.PortfolioId}:{scenario.AccessContextId}:{scenario.UnassignedWorkOrderId}:unassigned-update-denied"),
            unassigned,
            UpdateCodec);

        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.Id == scenario.UnassignedWorkOrderId)
            .Select(workOrder => workOrder.Status)
            .SingleAsync()).Should().Be(WorkOrderStatus.New);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.IdempotencyKey.EndsWith(":unassigned-update-denied"))).Should().Be(0);
    }

    private IAtomicUnitOfWork Atomic => _services.GetRequiredService<IAtomicUnitOfWork>();

    private async Task<Scenario> SeedScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var user = new ApplicationUser
        {
            UserName = "assigned-technician@example.test",
            NormalizedUserName = "ASSIGNED-TECHNICIAN@EXAMPLE.TEST",
            Email = "assigned-technician@example.test",
            NormalizedEmail = "ASSIGNED-TECHNICIAN@EXAMPLE.TEST",
            DisplayName = "Assigned Technician",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Technician Property",
            AddressLine1 = "1 Repair Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(user, property);
        await db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Maintenance,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Maintenance,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.MaintenanceTechnician).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();

        var assigned = WorkOrder(property.Id, "Assigned repair", now);
        var unassigned = WorkOrder(property.Id, "Unassigned repair", now);
        db.AddRange(assigned, unassigned);
        await db.SaveChangesAsync();
        db.WorkOrderResponsibilities.Add(new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            WorkOrderId = assigned.Id,
            WorkspaceMembershipId = membership.Id,
            MembershipRoleAssignmentId = assignment.Id,
            Kind = WorkOrderResponsibilityKind.Primary,
            EffectiveFromUtc = now.AddSeconds(-1),
            AssignedByUserId = user.Id,
            AssignedByAccessContextId = context.Id,
            AssignedReason = "Integration proof assignment",
            AssignedAtUtc = now.AddSeconds(-1),
        });
        await db.SaveChangesAsync();
        return new Scenario(1, user.Id, session.Id, context.Id, context.AccessRevision,
            assigned.Id, assigned.UpdatedAt, unassigned.Id, unassigned.UpdatedAt);
    }

    private static WorkOrder WorkOrder(int propertyId, string title, DateTime now) => new()
    {
        PortfolioId = 1,
        PropertyId = propertyId,
        Title = title,
        Description = title,
        Category = "General",
        Status = WorkOrderStatus.New,
        RequestedAt = now,
        UpdatedAt = now,
    };

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:assigned-work-order";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int UserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        int AssignedWorkOrderId,
        DateTime AssignedUpdatedAt,
        int UnassignedWorkOrderId,
        DateTime UnassignedUpdatedAt);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoleAuthorityPostgreSqlCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Role authority PostgreSQL";
}
