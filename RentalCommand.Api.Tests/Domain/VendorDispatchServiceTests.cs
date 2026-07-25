using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers vendor SMS dispatch (creates an open dispatch + enqueues the job SMS), a vendor DONE reply
/// closing the work order + dispatch, and ratings updating the scorecard aggregates.
/// </summary>
public class VendorDispatchServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly ServiceProvider _services;
    private readonly WorkspaceReadScope _scope;

    public VendorDispatchServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            DispatchWorkOrderToVendorCommand,
            DispatchWorkOrderToVendorResult,
            DispatchWorkOrderToVendorHandler>();
        services.AddAtomicCommandHandler<
            CompleteVendorDispatchFromInboundCommand,
            CompleteVendorDispatchFromInboundResult,
            CompleteVendorDispatchFromInboundHandler>();
        services.AddAtomicCommandHandler<
            CreateVendorRatingCommand,
            VendorRatingMutationResult,
            CreateVendorRatingHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(_ctx.ConnectionString)
                .AddInterceptors(SqliteDatabaseClockInterceptor.Instance)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(VendorDispatchServiceTests));
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    private VendorDispatchService CreateDispatchSut() => new(
        _ctx.Db,
        Mock.Of<IDataUpdateService>(),
        _services.GetRequiredService<IAtomicUnitOfWork>(),
        Mock.Of<ILogger<VendorDispatchService>>(),
        TimeProvider.System);

    private static DispatchWorkOrderRequest Request(int vendorId) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        VendorId = vendorId,
    };

    private SmsInboundVendorDoneService CreateDoneSut() => new(
        _ctx.Db,
        Mock.Of<IDataUpdateService>(),
        _services.GetRequiredService<IAtomicUnitOfWork>(),
        Mock.Of<ILogger<SmsInboundVendorDoneService>>());

    [Fact]
    public async Task DispatchAsync_CreatesOpenDispatch_AndEnqueuesSms()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "(614) 555-0199");
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateDispatchSut();

        var result = await sut.DispatchAsync(
            PortfolioId, workOrder.Id, new DispatchWorkOrderRequest
            {
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                VendorId = vendor.Id,
                Note = "Gate code 1234",
            }, changedByUserId: 5);

        result.Outcome.Should().Be(DispatchOutcome.Dispatched);
        result.Dispatch.Should().NotBeNull();
        result.Dispatch!.Status.Should().Be(VendorDispatchStatus.Dispatched);
        result.Dispatch.WorkOrderId.Should().Be(workOrder.Id);
        result.Dispatch.VendorId.Should().Be(vendor.Id);
        result.Dispatch.Message.Should().Contain("Reply DONE");
        result.Dispatch.Message.Should().Contain("Gate code 1234");

        // The work order was assigned to the vendor.
        _ctx.Db.ChangeTracker.Clear();
        var reloaded = await _ctx.Db.WorkOrders.FindAsync(workOrder.Id);
        reloaded!.VendorId.Should().Be(vendor.Id);

        // An open dispatch row exists.
        var dispatch = await _ctx.Db.VendorDispatches.SingleAsync();
        dispatch.Status.Should().Be(VendorDispatchStatus.Dispatched);

        // An SMS to the vendor's normalized phone was durably enqueued in the same command.
        var outbox = await _ctx.Db.OutboxMessages.SingleAsync();
        outbox.MessageType.Should().Be("sms");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("to").GetString().Should().Be("+16145550199");
    }

    [Fact]
    public async Task DispatchAsync_ReturnsNoPhone_WhenVendorHasNoPhone()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: null);
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateDispatchSut();

        var result = await sut.DispatchAsync(
            PortfolioId, workOrder.Id, Request(vendor.Id), changedByUserId: 5);

        result.Outcome.Should().Be(DispatchOutcome.VendorHasNoPhone);
        (await _ctx.Db.VendorDispatches.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_SameOperationKey_ReplaysOneDispatchAuditAndSmsIntent()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();
        var request = new DispatchWorkOrderRequest
        {
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            VendorId = vendor.Id,
        };
        var sut = CreateDispatchSut();

        var first = await sut.DispatchAsync(PortfolioId, workOrder.Id, request, changedByUserId: 5);
        var replay = await sut.DispatchAsync(PortfolioId, workOrder.Id, request, changedByUserId: 5);

        replay.Outcome.Should().Be(DispatchOutcome.Dispatched);
        replay.Dispatch!.Id.Should().Be(first.Dispatch!.Id);
        (await _ctx.Db.VendorDispatches.CountAsync()).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync(log => log.EntityType == nameof(VendorDispatch)))
            .Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_DifferentOperationKey_DoesNotCreateSecondOpenDispatch()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();
        var sut = CreateDispatchSut();

        var first = await sut.DispatchAsync(
            PortfolioId, workOrder.Id, Request(vendor.Id), changedByUserId: 5);
        var second = await sut.DispatchAsync(
            PortfolioId, workOrder.Id, Request(vendor.Id), changedByUserId: 5);

        first.Outcome.Should().Be(DispatchOutcome.Dispatched);
        second.Outcome.Should().Be(DispatchOutcome.AlreadyDispatched);
        (await _ctx.Db.VendorDispatches.CountAsync()).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task VendorDoneReply_CompletesWorkOrderAndDispatch()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();

        // Dispatch first.
        await CreateDispatchSut().DispatchAsync(
            PortfolioId, workOrder.Id, Request(vendor.Id), changedByUserId: 5);

        var doneSut = CreateDoneSut();
        var receivedAt = new DateTime(2026, 06, 03, 15, 0, 0, DateTimeKind.Utc);

        var result = await doneSut.TryHandleAsync(
            "SM-vendor-done-1", "+16145550199", "Done!", receivedAt);

        result.Handled.Should().BeTrue();
        result.WorkOrderId.Should().Be(workOrder.Id);

        _ctx.Db.ChangeTracker.Clear();
        var dispatch = await _ctx.Db.VendorDispatches.SingleAsync();
        dispatch.Status.Should().Be(VendorDispatchStatus.Completed);
        dispatch.RespondedAtUtc.Should().Be(receivedAt);

        var reloaded = await _ctx.Db.WorkOrders.FindAsync(workOrder.Id);
        reloaded!.Status.Should().Be(WorkOrderStatus.Completed);
        reloaded.CompletedAt.Should().Be(receivedAt);

        // A "Vendor" status event was written.
        var events = await _ctx.Db.WorkOrderStatusEvents
            .Where(e => e.WorkOrderId == workOrder.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();
        events.Last().ToStatus.Should().Be(WorkOrderStatus.Completed);
        events.Last().ChangedByLabel.Should().Be("Vendor");

        // The vendor's completed-jobs counter incremented.
        var vendorReloaded = await _ctx.Db.Vendors.FindAsync(vendor.Id);
        vendorReloaded!.JobsCompleted.Should().Be(1);
        _ctx.Db.Notifications.Should().BeEmpty(
            "legacy portfolio roles are not a recipient fallback when scoped responsibility is absent");
    }

    [Fact]
    public async Task VendorDone_CanHandle_FalseWhenNoOpenDispatch()
    {
        var (_, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        await _ctx.Db.SaveChangesAsync();

        // No dispatch created → cannot handle even with the right keyword/phone.
        (await CreateDoneSut().TryHandleAsync(
            "SM-vendor-no-dispatch", "+16145550199", "DONE", DateTime.UtcNow)).Handled.Should().BeFalse();
        vendor.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VendorDone_FailsClosed_WhenSameVendorHasMultipleOpenDispatches()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var first = SeedWorkOrder(property);
        var second = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.VendorDispatches.AddRange(
            OpenDispatch(first.Id, vendor.Id, DateTime.UtcNow.AddMinutes(-2)),
            OpenDispatch(second.Id, vendor.Id, DateTime.UtcNow.AddMinutes(-1)));
        await _ctx.Db.SaveChangesAsync();

        var result = await CreateDoneSut().TryHandleAsync(
            "SM-same-vendor-ambiguous", "+16145550199", "DONE", DateTime.UtcNow);

        result.Handled.Should().BeFalse();
        (await _ctx.Db.VendorDispatches.CountAsync(row =>
            row.Status == VendorDispatchStatus.Dispatched)).Should().Be(2);
        (await _ctx.Db.WorkOrders.CountAsync(row =>
            row.Status == WorkOrderStatus.Completed)).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sms.vendor-done")).Should().Be(1);
    }

    [Fact]
    public async Task VendorDone_ClosingSiblingDispatch_DoesNotIncrementJobsCompletedAgain()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        vendor.JobsCompleted = 1;
        var completedWorkOrder = SeedWorkOrder(property);
        completedWorkOrder.Status = WorkOrderStatus.Completed;
        var firstCompletionAt = DateTime.UtcNow.AddMinutes(-10);
        completedWorkOrder.CompletedAt = firstCompletionAt;
        await _ctx.Db.SaveChangesAsync();
        var completedDispatch = OpenDispatch(
            completedWorkOrder.Id, vendor.Id, DateTime.UtcNow.AddMinutes(-20));
        completedDispatch.Status = VendorDispatchStatus.Completed;
        completedDispatch.RespondedAtUtc = firstCompletionAt;
        var openSibling = OpenDispatch(
            completedWorkOrder.Id, vendor.Id, DateTime.UtcNow.AddMinutes(-5));
        _ctx.Db.VendorDispatches.AddRange(completedDispatch, openSibling);
        _ctx.Db.WorkOrderStatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = PortfolioId,
            WorkOrderId = completedWorkOrder.Id,
            FromStatus = WorkOrderStatus.InProgress,
            ToStatus = WorkOrderStatus.Completed,
            ChangedByLabel = "Vendor",
            CreatedAtUtc = firstCompletionAt,
        });
        SeedScopedMember(userId: 100, property.Id, roleProfileId: 2);
        await _ctx.Db.SaveChangesAsync();

        var siblingCompletionAt = DateTime.UtcNow;
        var result = await CreateDoneSut().TryHandleAsync(
            "SM-sibling-after-completion", "+16145550199", "DONE", siblingCompletionAt);
        var replay = await CreateDoneSut().TryHandleAsync(
            "SM-sibling-after-completion", "+16145550199", "DONE", siblingCompletionAt);

        result.Handled.Should().BeTrue();
        result.WorkOrderId.Should().Be(completedWorkOrder.Id);
        replay.Handled.Should().BeTrue();
        replay.WorkOrderId.Should().Be(completedWorkOrder.Id);
        _ctx.Db.ChangeTracker.Clear();
        var closedSibling = await _ctx.Db.VendorDispatches.SingleAsync(row => row.Id == openSibling.Id);
        closedSibling.Status.Should().Be(VendorDispatchStatus.Completed);
        closedSibling.RespondedAtUtc.Should().Be(siblingCompletionAt);
        (await _ctx.Db.VendorDispatches.CountAsync(row =>
            row.WorkOrderId == completedWorkOrder.Id
            && row.Status == VendorDispatchStatus.Completed)).Should().Be(2);
        (await _ctx.Db.Vendors.SingleAsync(row => row.Id == vendor.Id)).JobsCompleted.Should().Be(1);
        (await _ctx.Db.WorkOrderStatusEvents.CountAsync(row =>
            row.WorkOrderId == completedWorkOrder.Id)).Should().Be(1);
        (await _ctx.Db.Notifications.CountAsync(row =>
            row.RelatedEntityId == completedWorkOrder.Id)).Should().Be(0);
        var receipt = (await _ctx.Db.AtomicCommandReceipts
            .Where(row => row.CommandType == "sms.vendor-done")
            .ToListAsync()).Should().ContainSingle().Subject;
        receipt.Status.Should().Be(AtomicCommandReceiptStatus.Completed);
        receipt.CompletedAt.Should().NotBeNull();
        var audits = await _ctx.Db.AtomicAuditLogs
            .Where(row => row.CommandType == "sms.vendor-done")
            .ToListAsync();
        audits.Should().ContainSingle(row =>
            row.EntityType == nameof(VendorDispatch)
            && row.EntityId == openSibling.Id);
        audits.Should().NotContain(row =>
            row.EntityType == nameof(Vendor)
            || row.EntityType == nameof(WorkOrder)
            || row.EntityType == nameof(WorkOrderStatusEvent)
            || row.EntityType == nameof(Notification));
    }

    [Fact]
    public async Task VendorDone_NotifiesOnlyWorkCapabilityAndPropertyScopedRecipients()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var decoyProperty = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Unrelated property",
            AddressLine1 = "9 Other St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Properties.Add(decoyProperty);
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.VendorDispatches.Add(OpenDispatch(workOrder.Id, vendor.Id, DateTime.UtcNow.AddMinutes(-5)));
        SeedScopedMember(userId: 100, property.Id, roleProfileId: 2);
        SeedScopedMember(userId: 101, decoyProperty.Id, roleProfileId: 2);
        // Leasing is in property scope but does not supply work.read.
        SeedScopedMember(userId: 102, property.Id, roleProfileId: 3);
        var now = DateTime.UtcNow;
        var routingRule = new TeamRoutingRule
        {
            PortfolioId = PortfolioId,
            Topic = TeamRoutingTopic.WorkOrders,
            UseWorkspaceAdministratorFallback = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        routingRule.Recipients.Add(new TeamRoutingRuleRecipient
        {
            PortfolioId = PortfolioId,
            UserId = _scope.UserId,
            Reason = "Workspace administrator",
        });
        routingRule.Recipients.Add(new TeamRoutingRuleRecipient
        {
            PortfolioId = PortfolioId,
            UserId = 100,
            Reason = "Assigned maintenance staff",
        });
        routingRule.Recipients.Add(new TeamRoutingRuleRecipient
        {
            PortfolioId = PortfolioId,
            UserId = 101,
            Reason = "Unrelated property staff",
        });
        routingRule.Recipients.Add(new TeamRoutingRuleRecipient
        {
            PortfolioId = PortfolioId,
            UserId = 102,
            Reason = "Leasing staff without work access",
        });
        _ctx.Db.TeamRoutingRules.Add(routingRule);
        await _ctx.Db.SaveChangesAsync();

        var result = await CreateDoneSut().TryHandleAsync(
            "SM-scoped-recipient", "+16145550199", "DONE", DateTime.UtcNow);

        result.Handled.Should().BeTrue();
        var notifications = await _ctx.Db.Notifications
            .OrderBy(notification => notification.UserId)
            .ToListAsync();
        notifications.Select(notification => notification.UserId)
            .Should().Equal(_scope.UserId, 100);
        notifications.Should().OnlyContain(notification =>
            notification.RelatedEntityId == workOrder.Id);
        notifications.Should().NotContain(notification =>
            notification.UserId == 101 || notification.UserId == 102,
            "out-of-scope and capability-missing members are not recipients");
    }

    private static VendorDispatch OpenDispatch(
        int workOrderId,
        int vendorId,
        DateTime dispatchedAtUtc) => new()
    {
        PortfolioId = PortfolioId,
        WorkOrderId = workOrderId,
        VendorId = vendorId,
        Status = VendorDispatchStatus.Dispatched,
        DispatchedAtUtc = dispatchedAtUtc,
        Message = "Reply DONE when complete.",
    };

    private void SeedScopedMember(int userId, int propertyId, int roleProfileId)
    {
        var now = DateTime.UtcNow;
        var role = AccessCatalog.Roles.Single(candidate => candidate.Id == roleProfileId);
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = $"member-{userId}@example.test",
            NormalizedUserName = $"MEMBER-{userId}@EXAMPLE.TEST",
            Email = $"member-{userId}@example.test",
            NormalizedEmail = $"MEMBER-{userId}@EXAMPLE.TEST",
            DisplayName = $"Member {userId}",
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = role.DefaultExperience,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = roleProfileId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SelectedProperties =
            [
                new MembershipRoleAssignmentProperty
                {
                    PropertyId = propertyId,
                    PortfolioId = PortfolioId,
                },
            ],
        };
        _ctx.Db.Users.Add(user);
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        _ctx.Db.WorkspaceMemberships.Add(membership);
        _ctx.Db.MembershipRoleAssignments.Add(assignment);
    }

    [Fact]
    public async Task RateAsync_UpdatesScorecardAggregates()
    {
        var (_, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateDispatchSut();

        await sut.RateAsync(
            _scope, vendor.Id, new CreateVendorRatingRequest { Stars = 5, Comment = "Great" },
            Guid.NewGuid().ToString("N"));
        await sut.RateAsync(
            _scope, vendor.Id, new CreateVendorRatingRequest { Stars = 3 },
            Guid.NewGuid().ToString("N"));

        _ctx.Db.ChangeTracker.Clear();
        var card = await sut.GetScorecardAsync(_scope, vendor.Id);
        card.Should().NotBeNull();
        card!.RatingCount.Should().Be(2);
        card.AverageRating.Should().Be(4.00m);

        var vendorReloaded = await _ctx.Db.Vendors.FindAsync(vendor.Id);
        vendorReloaded!.RatingCount.Should().Be(2);
        vendorReloaded.AverageRating.Should().Be(4.00m);
    }

    [Fact]
    public async Task RateAsync_SameOperationKey_ReplaysOneRatingAuditAndBroadcastIntent()
    {
        var (_, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        await _ctx.Db.SaveChangesAsync();
        var sut = CreateDispatchSut();
        var key = Guid.NewGuid().ToString("N");
        var request = new CreateVendorRatingRequest { Stars = 5, Comment = "Great" };

        var first = await sut.RateAsync(_scope, vendor.Id, request, key);
        var replay = await sut.RateAsync(_scope, vendor.Id, request, key);

        replay.Should().BeEquivalentTo(first);
        (await _ctx.Db.VendorRatings.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync(log =>
            log.EntityType == nameof(VendorRating))).Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync(log =>
            log.EntityType == nameof(Vendor))).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetScorecard_IncludesAvgResponseHours_FromCompletedDispatch()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();

        await CreateDispatchSut().DispatchAsync(
            PortfolioId, workOrder.Id, Request(vendor.Id), changedByUserId: 5);

        // Force a known 2-hour gap between dispatch and response.
        var dispatch = await _ctx.Db.VendorDispatches.SingleAsync();
        dispatch.DispatchedAtUtc = new DateTime(2026, 06, 03, 12, 0, 0, DateTimeKind.Utc);
        await _ctx.Db.SaveChangesAsync();

        await CreateDoneSut().TryHandleAsync(
            "SM-scorecard-done",
            "+16145550199", "DONE", new DateTime(2026, 06, 03, 14, 0, 0, DateTimeKind.Utc));

        _ctx.Db.ChangeTracker.Clear();
        var card = await CreateDispatchSut().GetScorecardAsync(_scope, vendor.Id);
        card!.JobsCompleted.Should().Be(1);
        card.AvgResponseHours.Should().Be(2.00m);
    }

    private (Property property, Vendor vendor) SeedPropertyAndVendor(string? vendorPhone)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "12 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Ace Plumbing",
            ServiceType = "Plumbing",
            Phone = vendorPhone,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.Vendors.Add(vendor);
        _ctx.Db.SaveChanges();
        return (property, vendor);
    }

    private WorkOrder SeedWorkOrder(Property property)
    {
        var now = DateTime.UtcNow;
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Title = "Leaky faucet",
            Description = "Kitchen sink drips",
            Priority = WorkOrderPriority.High,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.WorkOrders.Add(workOrder);
        return workOrder;
    }
}
