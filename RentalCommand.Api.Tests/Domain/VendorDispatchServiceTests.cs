using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
[Collection(MigratedPostgreSqlCollection.Name3)]
public class VendorDispatchServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly Guid FrozenSessionId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public VendorDispatchServiceTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _scope = _ctx.Db.SeedAdministratorScope(
            PortfolioId, nameof(VendorDispatchServiceTests), FrozenSessionId);
        await _ctx.ActivateApiScopeAsync(_scope);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_ctx.ConnectionString)
                .AddInterceptors(new RequestGucConnectionInterceptor(_scope))
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    private VendorDispatchService CreateDispatchSut() => new(
        _services.GetRequiredService<RentalCommandDbContext>(),
        Mock.Of<IDataUpdateService>(),
        _services.GetRequiredService<IWriteExecutor>(),
        Mock.Of<ILogger<VendorDispatchService>>(),
        TimeProvider.System);

    private static DispatchWorkOrderRequest Request(int vendorId) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        VendorId = vendorId,
    };

    private SmsInboundVendorDoneService CreateDoneSut() => new(
        _services.GetRequiredService<RentalCommandDbContext>(),
        Mock.Of<IDataUpdateService>(),
        _services.GetRequiredService<IWriteExecutor>(),
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
    public async Task FiveProviderContracts_ReplayFrozenLegacyReceipts()
    {
        var (property, vendor) = SeedPropertyAndVendor(vendorPhone: "+16145550199");
        var workOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();
        var access = new DispatchManagementAccess(
            _scope.SessionId, _scope.UserId, _scope.AccessContextId, _scope.AccessRevision);
        var dispatchKey = $"{PortfolioId}:{workOrder.Id}:frozen-dispatch";
        var dispatch = new DispatchWorkOrderToVendorCommand(
            PortfolioId, workOrder.Id, vendor.Id, "+16145550199",
            "New job at Maple Court: Leaky faucet. Kitchen sink drips Priority: High. Reply DONE when the job is complete.",
            5, DateTime.UtcNow, access);
        var cancelKey = $"{PortfolioId}:{workOrder.Id}:91:frozen-cancel";
        var cancel = new CancelVendorDispatchCommand(
            PortfolioId, workOrder.Id, 91, 5, "Frozen cancellation", DateTime.UtcNow,
            cancelKey, access);
        var recoveryDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("frozen-recovery"))).ToLowerInvariant();
        var recoveryKey =
            $"vendor-dispatch-chronology:{PortfolioId}:{workOrder.Id}:91:{recoveryDigest}";
        var recoveryRequest = new RecoverVendorDispatchChronologyRequest
        {
            ExpectedContaminatedDispatchedAtUtc = new DateTime(2098, 8, 21, 14, 0, 0, DateTimeKind.Utc),
            ExpectedStatusEventId = 92,
            ExpectedOutboxId = 93,
            ExpectedOutboxIdempotencyKey = "vendor-dispatch:91:sms",
            OriginalCommandIdempotencyKey = dispatchKey,
            CorrectDispatchedAtUtc = new DateTime(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc),
        };
        var recovery = new RecoverVendorDispatchChronologyCommand(
            PortfolioId, workOrder.Id, 91, recoveryRequest.ExpectedContaminatedDispatchedAtUtc,
            recoveryRequest.ExpectedStatusEventId, recoveryRequest.ExpectedOutboxId,
            recoveryRequest.ExpectedOutboxIdempotencyKey, recoveryRequest.OriginalCommandIdempotencyKey,
            recoveryRequest.CorrectDispatchedAtUtc, _scope.UserId, access, recoveryKey);
        var ratingKey = "frozen-rating";
        var rating = new CreateVendorRatingCommand(
            PortfolioId,
            new StaffOperationActor(_scope.UserId, _scope.SessionId,
                _scope.AccessContextId, _scope.AccessRevision),
            vendor.Id, null, 5, "Frozen rating", DateTime.UtcNow, ratingKey);
        var inboundEvent = "provider-frozen-event";
        var inboundKey = $"provider-event:{HashLower(inboundEvent)}";
        var inbound = new CompleteVendorDispatchFromInboundCommand(
            inboundEvent, "+16145550199", true,
            new DateTime(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc));

        new IAtomicCommandData[] { dispatch, cancel, recovery, rating, inbound }
            .Select(AtomicCommandFingerprint.Create).Should().Equal(
                "4140d2b9d30ceae74b2356f67f8c7ea80f3d621f3300fbdb956dca40b89e3589",
                "3b0ab036cf6e2a5822e87efe46221d7c27269ac30927790b85e524c86ee932ec",
                "2ad9380df0ba858a66925258460f270dec110b993182b5f09a7a89a0de136228",
                "b9960f7fa31960daf02ba32a1fac644bdeabef2c4297e18b69585526e12e6be3",
                "8db4410ca3ce0277821c8ff00604bd74884963b0d1a1720ac525082228393441");

        SeedReceipt(dispatchKey, DispatchWorkOrderToVendorRule.Write(dispatch, _ctx.Db),
            "vendor-dispatch.create", "4140d2b9d30ceae74b2356f67f8c7ea80f3d621f3300fbdb956dca40b89e3589", "vendor-dispatch.create.v1",
            $$"""{"Outcome":0,"DispatchId":90,"PortfolioId":1,"WorkOrderId":{{workOrder.Id}},"VendorId":{{vendor.Id}},"Status":0,"DispatchedAtUtc":"2099-08-21T12:00:00Z","Message":"Frozen dispatch"}""");
        SeedReceipt(cancelKey, CancelVendorDispatchRule.Write(cancel, _ctx.Db),
            "vendor-dispatch.cancel", "3b0ab036cf6e2a5822e87efe46221d7c27269ac30927790b85e524c86ee932ec", "vendor-dispatch.cancel.v1",
            $$"""{"Outcome":0,"DispatchId":91,"PortfolioId":1,"WorkOrderId":{{workOrder.Id}},"VendorId":{{vendor.Id}},"Status":3,"CancelledAtUtc":"2099-08-21T12:00:00Z","Reason":"Frozen cancellation"}""");
        SeedReceipt(recoveryKey, RecoverVendorDispatchChronologyRule.Write(recovery, _ctx.Db),
            "vendor-dispatch.recover-chronology", "2ad9380df0ba858a66925258460f270dec110b993182b5f09a7a89a0de136228", "vendor-dispatch.chronology-recovery.v1",
            $$"""{"WorkOrderId":{{workOrder.Id}},"DispatchId":91,"StatusEventId":92,"OutboxId":93,"DispatchedAtUtc":"2099-08-21T12:00:00Z","WorkOrderUpdatedAtRepaired":true}""");
        SeedReceipt(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ratingKey))),
            CreateVendorRatingRule.Write(rating, _ctx.Db),
            "vendor-rating.create", "b9960f7fa31960daf02ba32a1fac644bdeabef2c4297e18b69585526e12e6be3", "vendor-rating.create.v1",
            $$"""{"Outcome":0,"RatingId":94,"VendorId":{{vendor.Id}},"ResponseJson":"{\"Id\":94,\"VendorId\":{{vendor.Id}},\"WorkOrderId\":null,\"Stars\":5,\"Comment\":\"Frozen rating\",\"CreatedAtUtc\":\"2099-08-21T12:00:00Z\"}"}""");
        SeedReceipt(inboundKey, CompleteVendorDispatchFromInboundRule.Write(inbound, _ctx.Db),
            "sms.vendor-done", "8db4410ca3ce0277821c8ff00604bd74884963b0d1a1720ac525082228393441", "complete-vendor-dispatch-from-inbound-result.v1",
            """{"Outcome":1,"PortfolioId":0,"DispatchId":0,"WorkOrderId":0,"VendorId":0,"NotificationIds":[]}""");
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var service = CreateDispatchSut();
        var dispatchReplay = await service.DispatchAuthorizedAsync(
            _scope, workOrder.Id,
            new DispatchWorkOrderRequest { VendorId = vendor.Id, IdempotencyKey = "frozen-dispatch" }, 5);
        var cancelReplay = await service.CancelAuthorizedAsync(
            _scope, workOrder.Id, 91,
            new CancelVendorDispatchRequest
            {
                IdempotencyKey = "frozen-cancel",
                Reason = "Frozen cancellation",
            }, 5);
        var recoveryReplay = await service.RecoverChronologyAuthorizedAsync(
            _scope, workOrder.Id, 91, recoveryRequest, _scope.UserId, "frozen-recovery");
        var ratingReplay = await service.RateAsync(
            _scope, vendor.Id,
            new CreateVendorRatingRequest
            {
                WorkOrderId = null,
                Stars = 5,
                Comment = "Frozen rating",
            }, ratingKey);
        var inboundReplay = await CreateDoneSut().TryHandleAsync(
            inboundEvent, "+16145550199", "DONE", inbound.ReceivedAtUtc);

        dispatchReplay.Dispatch!.Id.Should().Be(90);
        cancelReplay.Dispatch!.Replayed.Should().BeTrue();
        recoveryReplay.Should().Match<RecoverVendorDispatchChronologyResponse>(item =>
            item.Replayed && item.StatusEventId == 92 && item.OutboxId == 93);
        ratingReplay!.Id.Should().Be(94);
        inboundReplay.Handled.Should().BeFalse();
        (await _ctx.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == "vendor-dispatch.create"
            || row.CommandType == "vendor-dispatch.cancel"
            || row.CommandType == "vendor-dispatch.recover-chronology"
            || row.CommandType == "vendor-rating.create"
            || row.CommandType == "sms.vendor-done")).Should().Be(5);
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

    private void SeedReceipt<TCommand, TResult>(
        string key,
        TransactionalWrite<TCommand, TResult> write,
        string operation,
        string frozenFingerprint,
        string resultContract,
        string literalResultJson)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull =>
        _ctx.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = operation,
            IdempotencyKey = key,
            RequestFingerprint = frozenFingerprint,
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = resultContract,
            ResultJson = literalResultJson,
            StartedAt = new DateTime(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc),
        });

    private static string HashLower(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RequestGucConnectionInterceptor(WorkspaceReadScope scope) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SET SESSION AUTHORIZATION rentalcommand_api;
                SELECT set_config('app.current_portfolio_id', @portfolio_id, false),
                       set_config('app.auth_session_id', @auth_session_id, false),
                       set_config('app.current_user_id', @user_id, false),
                       set_config('app.current_access_context_id', @access_context_id, false),
                       set_config('app.access_revision', @access_revision, false);
                """;
            AddParameter(command, "portfolio_id", scope.PortfolioId.ToString());
            AddParameter(command, "auth_session_id", scope.SessionId.ToString());
            AddParameter(command, "user_id", scope.UserId.ToString());
            AddParameter(command, "access_context_id", scope.AccessContextId.ToString());
            AddParameter(command, "access_revision", scope.AccessRevision.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void AddParameter(DbCommand command, string name, string value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{name}";
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
