using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Conversations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Conversations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name1)]
public class ConversationNotificationTests : IAsyncLifetime
{
    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private RentalCommandDbContext _db = null!;

    public ConversationNotificationTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<NotificationService>();
        services.AddDbContext<RentalCommand.Data.RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_ctx.ConnectionString)
                .AddInterceptors(new RecordingCommandInterceptor(_commands))
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<RentalCommandDbContext>();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    private ConversationService CreateSut(RecordingRealtimeInvalidationQueue? realtimeQueue = null) => new(
        _db,
        realtimeQueue ?? new RecordingRealtimeInvalidationQueue(),
        new NoopFairHousingReviewService(),
        NullLogger<ConversationService>.Instance,
        TimeProvider.System,
        _services.GetRequiredService<IRequestWriteExecutor>());

    private async Task<AtomicCommandOutcome<AtomicNoticeDeliveryResult>> ExecuteNoticeDeliveryAsync(
        AtomicNoticeDeliveryCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>().ExecuteAsync(
            AtomicNoticeDelivery.Identity(command).IdempotencyKey,
            AtomicNoticeDelivery.Write(db, command));
    }

    [Fact]
    public async Task TenantStartAsync_NotifiesOnlyCapabilityAndLeasePropertyScopedStaff()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        var routingRule = new TeamRoutingRule
        {
            PortfolioId = 1,
            Topic = TeamRoutingTopic.ApplicationsAndLeasing,
            UseWorkspaceAdministratorFallback = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        routingRule.Recipients.Add(new TeamRoutingRuleRecipient
        {
            PortfolioId = 1,
            UserId = 10,
            Reason = "Assigned leasing staff",
        });
        routingRule.Recipients.Add(new TeamRoutingRuleRecipient
        {
            PortfolioId = 1,
            UserId = 30,
            Reason = "Out-of-scope staff",
        });
        _db.TeamRoutingRules.Add(routingRule);
        _db.SaveChanges();
        var realtimeQueue = new RecordingRealtimeInvalidationQueue();
        var sut = CreateSut(realtimeQueue);

        var result = await sut.TenantStartAsync(
            1, tenant.Id, "Sink leak", "Water under the cabinet", "tenant-start-sink-leak");

        result.Should().NotBeNull();
        var notification = _db.Notifications.Should().ContainSingle().Subject;
        notification.Type.Should().Be("TenantMessage");
        notification.UserId.Should().Be(10);
        notification.Title.Should().Be("New message from Emily Chen");
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(result!.Id);
        notification.NavigationAccessContextId.Should().BePositive();
        notification.NavigationAccessRevision.Should().BePositive();
        _db.Notifications.Should().NotContain(item => item.UserId == 30);
        realtimeQueue.Batches.Should().ContainSingle();
        realtimeQueue.Batches[0].Should().Contain(update =>
            update.EntityType == "Conversation" && update.EntityId == result!.Id);
        realtimeQueue.Batches[0].Should().Contain(update =>
            update.EntityType == "Notification" && update.EntityId == notification.Id);
    }

    [Fact]
    public async Task TenantStartAsync_WithoutLeaseResponsibility_CommitsMessageButNoRecipientNotification()
    {
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "No",
            LastName = "Relationship",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var result = await CreateSut().TenantStartAsync(
            1, tenant.Id, "Question", "Who is responsible?", "tenant-no-responsibility");

        result.Should().NotBeNull();
        _db.ConversationMessages.Should().ContainSingle();
        _db.Notifications.Should().BeEmpty();
        _db.AtomicCommandReceipts.Should().ContainSingle(receipt =>
            receipt.CommandType == "conversation.tenant-start");
        _db.AtomicAuditLogs.Should().Contain(log => log.EntityType == nameof(ConversationMessage));
    }

    [Fact]
    public async Task LandlordStartAsync_WithPortalChannel_CreatesTenantNotification()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var sut = CreateSut();

        var result = await sut.StartAsync(
            1,
            tenant.Id,
            "Rent reminder",
            "Please check the payment portal.",
            ["Portal"],
            Guid.NewGuid().ToString("N"));

        result.Should().NotBeNull();
        var notification = _db.Notifications.Should().ContainSingle(n => n.Type == "TenantNotice").Subject;
        notification.UserId.Should().Be(20);
        notification.NavigationExperience.Should().Be(NavigationExperience.Tenant);
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(result!.Id);
        _db.OutboxMessages.Should().NotContain(m => m.MessageType == "push");
    }

    [Fact]
    public async Task LandlordStartAsync_ReplayedOperation_CommitsOneMessageAndOneIntentPerDestination()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        tenant.Phone = "+15551234567";
        _db.SaveChanges();
        var sut = CreateSut();
        const string operationKey = "conversation-retry-1";

        var first = await sut.StartAsync(
            1, tenant.Id, "Inspection", "Can we visit Friday?", ["Portal", "Email", "Sms"], operationKey);
        var replay = await sut.StartAsync(
            1, tenant.Id, "Inspection", "Can we visit Friday?", ["Portal", "Email", "Sms"], operationKey);

        replay!.Id.Should().Be(first!.Id);
        (await _db.Conversations.CountAsync()).Should().Be(1);
        (await _db.ConversationMessages.CountAsync()).Should().Be(1);
        (await _db.Notifications.CountAsync(notification => notification.Type == "TenantNotice")).Should().Be(1);
        (await _db.OutboxMessages.CountAsync()).Should().Be(2);
        (await _db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "conversation.start")).Should().Be(1);
    }

    [Fact]
    public async Task LandlordPostAsync_ReplayedOperation_AppendsOnceAndIncrementsUnreadOnce()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var sut = CreateSut();
        var started = await sut.StartAsync(
            1, tenant.Id, "Keys", "Your replacement key is ready.", ["Portal"], "conversation-start-keys");

        var first = await sut.PostMessageAsync(
            1, started!.Id, "The office closes at five.", ["Portal"], "conversation-post-keys");
        var replay = await sut.PostMessageAsync(
            1, started.Id, "The office closes at five.", ["Portal"], "conversation-post-keys");

        replay!.Messages.Should().HaveCount(2);
        first!.Messages.Should().HaveCount(2);
        (await _db.ConversationMessages.CountAsync()).Should().Be(2);
        (await _db.Conversations.AsNoTracking()
            .Where(conversation => conversation.Id == started.Id)
            .Select(conversation => conversation.TenantUnreadCount)
            .SingleAsync()).Should().Be(2);
    }

    [Fact]
    public async Task TenantMarkReadAsync_WhenUnread_UpdatesOnceAndSecondReadIsNoOp()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        SeedConversation(
            tenant.Id,
            "HVAC appointment confirmed",
            now.AddMinutes(-1),
            tenantUnreadCount: 2);
        var tenantContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 20);
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = 20,
            ActiveAccessContextId = tenantContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _db.AuthSessions.Add(session);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var conversationId = await _db.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenant.Id
                && conversation.Subject == "HVAC appointment confirmed")
            .Select(conversation => conversation.Id)
            .SingleAsync();
        var scope = new WorkspaceReadScope(
            1, 20, session.Id, tenantContext.Id, tenantContext.AccessRevision);
        var sut = CreateSut();

        (await sut.MarkReadForTenantAsync(scope, tenant.Id, conversationId, "tenant-read-hvac-first"))
            .Should().BeTrue();
        (await sut.MarkReadForTenantAsync(scope, tenant.Id, conversationId, "tenant-read-hvac-second"))
            .Should().BeTrue();

        (await _db.Conversations.AsNoTracking()
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation => conversation.TenantUnreadCount)
            .SingleAsync()).Should().Be(0);
        (await _db.AtomicAuditLogs.AsNoTracking().CountAsync(log =>
            log.EntityType == nameof(Conversation)
            && log.EntityId == conversationId
            && log.ChangeReason == "Tenant conversation marked read")).Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_HidesTenantMessageNotificationsFromTenantOnlyUsers()
    {
        SeedTenantWithStaffAndTenantUsers();
        _db.Notifications.AddRange(
            new Notification
            {
                PortfolioId = 1,
                Type = "TenantMessage",
                Title = "New message from Emily Chen",
                Message = "Tenant-originated staff alert",
                CreatedAt = DateTime.UtcNow,
            },
            new Notification
            {
                PortfolioId = 1,
                UserId = 20,
                Type = "System",
                Title = "Pool closed",
                Message = "Shared system notice",
                CreatedAt = DateTime.UtcNow.AddMinutes(1),
            });
        _db.SaveChanges();

        var sut = new NotificationService(
            _db, TimeProvider.System, _services.GetRequiredService<IRequestWriteExecutor>());

        var tenantItems = await sut.ListAsync(1, userId: 20);
        tenantItems.Select(n => n.Title).Should().Equal("Pool closed");
        var staffItems = await sut.ListAsync(1, userId: 10);
        staffItems.Select(n => n.Title).Should().Contain("New message from Emily Chen");
    }

    [Fact]
    public async Task ListAsync_ProjectsTenantLedgerEntryIntentOnlyForOwnedTenantAccount()
    {
        SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        var tenantContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 20);
        var tenantAccountId = await _db.TenantAccounts
            .Where(account => account.PortfolioId == 1)
            .Select(account => account.Id)
            .SingleAsync();
        var ownedLedgerEntry = await SeedTenantLedgerEntryAsync(
            tenantAccountId,
            "owned-rent-charge",
            now);
        var foreignLedgerEntry = await SeedForeignTenantLedgerEntryAsync(now);
        _db.Notifications.AddRange(
            TenantLedgerNotification(
                userId: 20,
                title: "Pay January rent",
                tenantContext,
                tenantAccountId,
                ownedLedgerEntry.Id,
                now),
            TenantLedgerNotification(
                userId: 20,
                title: "Foreign account charge",
                tenantContext,
                foreignLedgerEntry.TenantAccountId,
                foreignLedgerEntry.Id,
                now.AddMinutes(1)));
        await _db.SaveChangesAsync();
        var scope = new WorkspaceReadScope(
            1, 20, Guid.NewGuid(), tenantContext.Id, tenantContext.AccessRevision);
        var sut = new NotificationService(
            _db, TimeProvider.System, _services.GetRequiredService<IRequestWriteExecutor>());

        _commands.Clear();
        var items = await sut.ListAsync(scope, NavigationExperience.Tenant);

        var owned = items.Should().ContainSingle(item => item.Title == "Pay January rent").Subject;
        owned.NavigationIntent.Should().NotBeNull();
        owned.NavigationIntent!.Destination.Should().Be(NavigationDestination.TenantLedgerEntry);
        owned.NavigationIntent.Resource.Should().BeEquivalentTo(new NavigationResourceDto
        {
            Kind = nameof(TenantLedgerEntry),
            Id = checked((int)ownedLedgerEntry.Id),
        });
        owned.NavigationIntent.ParentResource.Should().BeEquivalentTo(new NavigationResourceDto
        {
            Kind = nameof(TenantAccount),
            Id = tenantAccountId,
        });
        items.Should().ContainSingle(item => item.Title == "Foreign account charge")
            .Which.NavigationIntent.Should().BeNull();
        _commands.Should().Contain(command =>
            command.Contains("vw_effective_tenant_access", StringComparison.OrdinalIgnoreCase)
            && command.Contains("\"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("\"TenantAccountId\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListGetAndUnreadCount_HideFutureLedgerNotificationsButKeepAdvanceReminders()
    {
        SeedTenantWithStaffAndTenantUsers();
        var businessNow = new DateTime(2027, 1, 31, 17, 0, 0, DateTimeKind.Utc);
        var clock = await _db.SimulationClocks.SingleAsync(row => row.Id == 1);
        clock.Mode = RentalCommand.Core.Time.ClockMode.Frozen;
        clock.SimAnchorUtc = businessNow;
        clock.RealAnchorUtc = DateTime.UtcNow;
        clock.TimeZoneId = "America/New_York";
        var tenantContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 20);
        var tenantAccountId = await _db.TenantAccounts
            .Where(account => account.PortfolioId == 1)
            .Select(account => account.Id)
            .SingleAsync();
        var currentCharge = await SeedTenantLedgerEntryAsync(
            tenantAccountId,
            "current-rent-charge",
            businessNow,
            new DateOnly(2027, 1, 31),
            new DateOnly(2027, 1, 31));
        var futureCharge = await SeedTenantLedgerEntryAsync(
            tenantAccountId,
            "future-rent-charge",
            businessNow,
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 2, 1));
        var currentNotification = TenantLedgerNotification(
            userId: 20,
            title: "Current charge posted",
            tenantContext,
            tenantAccountId,
            currentCharge.Id,
            businessNow,
            type: "ScheduledRentCharge");
        var futureNotification = TenantLedgerNotification(
            userId: 20,
            title: "Future charge posted",
            tenantContext,
            tenantAccountId,
            futureCharge.Id,
            businessNow.AddMinutes(1),
            type: "ScheduledRentCharge");
        var reminderNotification = new Notification
        {
            PortfolioId = 1,
            UserId = 20,
            Type = "TenantNotice",
            Title = "Rent due soon",
            Message = "Rent of $1,650.00 is due Feb 1, 2027.",
            Severity = "Info",
            NavigationExperience = NavigationExperience.Tenant,
            NavigationDestination = NavigationDestination.TenantAccount,
            NavigationAccessContextId = tenantContext.Id,
            NavigationAccessRevision = tenantContext.AccessRevision,
            NavigationResourceKind = nameof(TenantAccount),
            NavigationResourceId = tenantAccountId,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = businessNow.AddDays(30),
            NavigationFallbackDestination = NavigationDestination.Home,
            RelatedEntityType = nameof(TenantLedgerEntry),
            RelatedEntityId = checked((int)futureCharge.Id),
            CreatedAt = businessNow.AddMinutes(2),
        };
        _db.Notifications.AddRange(currentNotification, futureNotification, reminderNotification);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var scope = new WorkspaceReadScope(
            1, 20, Guid.NewGuid(), tenantContext.Id, tenantContext.AccessRevision);
        var sut = new NotificationService(
            _db, TimeProvider.System, _services.GetRequiredService<IRequestWriteExecutor>());

        _commands.Clear();
        var items = await sut.ListAsync(scope, NavigationExperience.Tenant);

        items.Select(item => item.Title).Should().Equal("Rent due soon", "Current charge posted");
        (await sut.GetAsync(scope, NavigationExperience.Tenant, futureNotification.Id)).Should().BeNull();
        (await sut.GetUnreadCountAsync(1, 20)).Should().Be(2);
        _commands.Should().Contain(command =>
            command.Contains("rc_business_date", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("\"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListGetAndUnreadCount_HideWorkspaceBroadcastsFromTenantExperience()
    {
        SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        var adminContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 10);
        var tenantContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 20);
        var tenantAccountId = await _db.TenantAccounts
            .Where(account => account.PortfolioId == 1)
            .Select(account => account.Id)
            .SingleAsync();
        var bankBroadcast = new Notification
        {
            PortfolioId = 1,
            Type = "BankImportCompleted",
            Title = "Bank transactions imported",
            Message = "81 bank transactions are ready for review.",
            Severity = "Info",
            RelatedEntityType = nameof(BankConnection),
            RelatedEntityId = 42,
            CreatedAt = now,
        };
        var tenantNotice = new Notification
        {
            PortfolioId = 1,
            UserId = 20,
            Type = "TenantNotice",
            Title = "Rent due soon",
            Message = "Rent of $1,200.00 is due tomorrow.",
            Severity = "Info",
            NavigationExperience = NavigationExperience.Tenant,
            NavigationDestination = NavigationDestination.TenantAccount,
            NavigationAccessContextId = tenantContext.Id,
            NavigationAccessRevision = tenantContext.AccessRevision,
            NavigationResourceKind = nameof(TenantAccount),
            NavigationResourceId = tenantAccountId,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = now.AddDays(7),
            NavigationFallbackDestination = NavigationDestination.Home,
            CreatedAt = now.AddMinutes(1),
        };
        _db.Notifications.AddRange(bankBroadcast, tenantNotice);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var tenantScope = new WorkspaceReadScope(
            1, 20, Guid.NewGuid(), tenantContext.Id, tenantContext.AccessRevision);
        var adminScope = new WorkspaceReadScope(
            1, 10, Guid.NewGuid(), adminContext.Id, adminContext.AccessRevision);
        var sut = new NotificationService(
            _db, TimeProvider.System, _services.GetRequiredService<IRequestWriteExecutor>());

        _commands.Clear();
        var tenantItems = await sut.ListAsync(tenantScope, NavigationExperience.Tenant);

        tenantItems.Select(item => item.Title).Should().Equal("Rent due soon");
        (await sut.GetAsync(tenantScope, NavigationExperience.Tenant, bankBroadcast.Id)).Should().BeNull();
        (await sut.GetUnreadCountAsync(tenantScope, NavigationExperience.Tenant)).Should().Be(1);
        _commands.Should().Contain(command =>
            command.Contains("\"WorkspaceMemberships\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("\"Notifications\"", StringComparison.OrdinalIgnoreCase));

        var adminItems = await sut.ListAsync(adminScope, NavigationExperience.Management);

        adminItems.Select(item => item.Title).Should().Contain("Bank transactions imported");
        (await sut.GetUnreadCountAsync(adminScope, NavigationExperience.Management)).Should().Be(1);
    }

    [Fact]
    public async Task NoticeDeliveryApprovedReplay_ReconcilesStalePaymentNotificationWithoutDuplicatingGraph()
    {
        var graph = await SeedApprovedPaymentNoticeWithStaleMessageNotificationAsync("approved-payment-reconcile");
        var command = AtomicNoticeDelivery.Command(
            graph.ApprovalContext,
            graph.DraftId,
            [NoticeDeliveryChannel.TenantPortal],
            null,
            "approved-payment-reconcile-key");
        var first = await ExecuteNoticeDeliveryAsync(command);
        _services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>()
            .ChangeTracker.Clear();
        var replay = await ExecuteNoticeDeliveryAsync(command);
        _services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>()
            .ChangeTracker.Clear();
        var finalReplayCommand = AtomicNoticeDelivery.Command(
            graph.ApprovalContext,
            graph.DraftId,
            [NoticeDeliveryChannel.TenantPortal],
            null,
            "approved-payment-final-replay-key");
        var finalReplay = await ExecuteNoticeDeliveryAsync(finalReplayCommand);

        first.Value.RenderedNoticeId.Should().Be(graph.RenderedNoticeId);
        replay.Value.RenderedNoticeId.Should().Be(graph.RenderedNoticeId);
        finalReplay.Value.RenderedNoticeId.Should().Be(graph.RenderedNoticeId);
        _db.ChangeTracker.Clear();
        var notification = await _db.Notifications.AsNoTracking()
            .SingleAsync(item => item.Id == graph.NotificationId);
        notification.NavigationDestination.Should().Be(NavigationDestination.TenantLedgerEntry);
        notification.NavigationResourceKind.Should().Be(nameof(TenantLedgerEntry));
        notification.NavigationResourceId.Should().Be(checked((int)graph.TenantLedgerEntryId));
        notification.NavigationParentResourceKind.Should().Be(nameof(TenantAccount));
        notification.NavigationParentResourceId.Should().Be(graph.TenantAccountId);
        notification.RelatedEntityType.Should().Be(nameof(TenantLedgerEntry));
        notification.RelatedEntityId.Should().Be(checked((int)graph.TenantLedgerEntryId));
        (await _db.RenderedNotices.CountAsync()).Should().Be(1);
        (await _db.NoticeDeliveryEvidence.CountAsync()).Should().Be(1);
        (await _db.OutboxMessages.CountAsync()).Should().Be(1);
        (await _db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NoticeDeliveryApprovedReplay_WithWrongStaleNavigationFailsBeforeMutation()
    {
        var graph = await SeedApprovedPaymentNoticeWithStaleMessageNotificationAsync("approved-payment-wrong-stale");
        var notification = await _db.Notifications.SingleAsync(item => item.Id == graph.NotificationId);
        notification.NavigationResourceId = graph.ConversationId + 1000;
        notification.RelatedEntityId = graph.ConversationId + 1000;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var command = AtomicNoticeDelivery.Command(
            graph.ApprovalContext,
            graph.DraftId,
            [NoticeDeliveryChannel.TenantPortal],
            null,
            "approved-payment-wrong-stale-key");
        Func<Task> act = async () => await ExecuteNoticeDeliveryAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Approved tenant notice is not a recoverable delivery graph.");
        _db.ChangeTracker.Clear();
        notification = await _db.Notifications.AsNoTracking()
            .SingleAsync(item => item.Id == graph.NotificationId);
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(graph.ConversationId + 1000);
        notification.RelatedEntityType.Should().Be(nameof(Conversation));
        notification.RelatedEntityId.Should().Be(graph.ConversationId + 1000);
        (await _db.RenderedNotices.CountAsync()).Should().Be(1);
        (await _db.NoticeDeliveryEvidence.CountAsync()).Should().Be(1);
        (await _db.OutboxMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NoticeDeliveryApprovedReplay_WithExtraDeliveryEvidenceFailsBeforeMutation()
    {
        var graph = await SeedApprovedPaymentNoticeWithStaleMessageNotificationAsync("approved-payment-extra-evidence");
        var now = DateTime.UtcNow;
        var extraKey = DeliveryKey(graph.RenderedNoticeId, graph.LeaseManagementPartyId, NoticeDeliveryChannel.Email, "extra@example.test");
        var outbox = new OutboxMessage
        {
            PortfolioId = 1,
            MessageType = "email",
            Payload = "{}",
            IdempotencyKey = extraKey,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };
        _db.NoticeDeliveryEvidence.Add(new NoticeDeliveryEvidence
        {
            PortfolioId = 1,
            RenderedNoticeId = graph.RenderedNoticeId,
            RecipientLeaseManagementPartyId = graph.LeaseManagementPartyId,
            RecipientRole = NoticeRecipientRole.PrimaryTenant,
            Channel = NoticeDeliveryChannel.Email,
            Destination = "extra@example.test",
            OutboxMessage = outbox,
            IdempotencyKey = extraKey,
            CreatedAtUtc = now,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var command = AtomicNoticeDelivery.Command(
            graph.ApprovalContext,
            graph.DraftId,
            [NoticeDeliveryChannel.TenantPortal],
            null,
            "approved-payment-extra-evidence-key");
        Func<Task> act = async () => await ExecuteNoticeDeliveryAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Approved tenant notice is not a recoverable delivery graph.");
        _db.ChangeTracker.Clear();
        var notification = await _db.Notifications.AsNoTracking()
            .SingleAsync(item => item.Id == graph.NotificationId);
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(graph.ConversationId);
        (await _db.NoticeDeliveryEvidence.CountAsync()).Should().Be(2);
        (await _db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NoticeDeliveryApprovedReplay_WithDifferentChannelsFailsClosed()
    {
        var graph = await SeedApprovedPaymentNoticeWithStaleMessageNotificationAsync("approved-payment-channel-mismatch");
        var command = AtomicNoticeDelivery.Command(
            graph.ApprovalContext,
            graph.DraftId,
            [NoticeDeliveryChannel.Email],
            null,
            "approved-payment-wrong-channel-key");
        Func<Task> act = async () => await ExecuteNoticeDeliveryAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Existing approved notice does not match this approval request.");
        _db.ChangeTracker.Clear();
        var notification = await _db.Notifications.AsNoTracking()
            .SingleAsync(item => item.Id == graph.NotificationId);
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(graph.ConversationId);
        (await _db.RenderedNotices.CountAsync()).Should().Be(1);
        (await _db.NoticeDeliveryEvidence.CountAsync()).Should().Be(1);
        (await _db.OutboxMessages.CountAsync()).Should().Be(1);
        (await _db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateBroadcastAsync_NormalizesSeverityAndCountsAsUnreadForPortfolioUsers()
    {
        SeedTenantWithStaffAndTenantUsers();
        var context = await _db.WorkspaceAccessContexts.SingleAsync(row =>
            row.PortfolioId == 1 && row.UserId == 10);
        var now = DateTime.UtcNow;
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = 10,
            ActiveAccessContextId = context.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _db.AuthSessions.Add(session);
        await _db.SaveChangesAsync();
        var scope = new WorkspaceReadScope(1, 10, session.Id, context.Id, context.AccessRevision);
        var sut = new NotificationService(
            _db, TimeProvider.System, _services.GetRequiredService<IRequestWriteExecutor>());

        var created = await sut.CreateBroadcastAsync(
            scope,
            new CreateBroadcastNotificationRequest
            {
                Title = "Pool closed",
                Message = "The pool is closed for maintenance.",
                Severity = "critical",
            },
            "broadcast-pool-closed");

        created.Severity.Should().Be("Critical");
        created.IsRead.Should().BeFalse();

        var items = await sut.ListAsync(1, userId: 10);
        items.Should().ContainSingle(n => n.Id == created.Id && n.Severity == "Critical");

        var unreadCount = await sut.GetUnreadCountAsync(1, userId: 10);
        unreadCount.Should().Be(1);
    }

    [Fact]
    public async Task BroadcastReadState_IsIndependentForEachStaffAndTenantUser()
    {
        SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        _db.Notifications.Add(new Notification
        {
            PortfolioId = 1,
            Type = "System",
            Title = "Water interruption",
            Message = "Water will be off from noon until two.",
            CreatedAt = now,
        });
        var contexts = await _db.WorkspaceAccessContexts
            .Where(context => context.PortfolioId == 1)
            .ToDictionaryAsync(context => context.UserId);
        var sessions = contexts.Values.Select(context => new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = context.UserId,
            ActiveAccessContextId = context.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        }).ToDictionary(session => session.UserId);
        _db.AuthSessions.AddRange(sessions.Values);
        await _db.SaveChangesAsync();
        var notificationId = await _db.Notifications.Select(notification => notification.Id).SingleAsync();
        var sut = _services.GetRequiredService<NotificationService>();

        var staffContext = contexts[10];
        var staffScope = new WorkspaceReadScope(
            1, 10, sessions[10].Id, staffContext.Id, staffContext.AccessRevision);
        (await sut.MarkAsReadAsync(staffScope, notificationId, "read-water-interruption"))
            .Should().BeTrue();

        (await sut.ListAsync(1, 10)).Should().ContainSingle().Which.IsRead.Should().BeTrue();
        (await sut.ListAsync(1, 30)).Should().ContainSingle().Which.IsRead.Should().BeFalse();
        (await sut.ListAsync(1, 20)).Should().BeEmpty();
        (await sut.GetUnreadCountAsync(1, 10)).Should().Be(0);
        (await sut.GetUnreadCountAsync(1, 30)).Should().Be(1);
        (await sut.GetUnreadCountAsync(1, 20)).Should().Be(0);

        var secondStaffContext = contexts[30];
        var secondStaffScope = new WorkspaceReadScope(
            1, 30, sessions[30].Id, secondStaffContext.Id, secondStaffContext.AccessRevision);
        await sut.MarkAllAsReadAsync(secondStaffScope, "read-all-water-interruption");

        (await sut.GetUnreadCountAsync(1, 30)).Should().Be(0);
        (await sut.GetUnreadCountAsync(1, 20)).Should().Be(0);
        (await _db.NotificationReadStates.AsNoTracking()
            .OrderBy(readState => readState.UserId)
            .Select(readState => readState.UserId)
            .ToListAsync()).Should().Equal(10, 30);
    }

    [Fact]
    public async Task ListAndGetAsync_AreReadOnlyAndProjectMessageCountsAndOrderedMessagesFromDatabase()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            PortfolioId = 1,
            TenantId = tenant.Id,
            Subject = "Sink leak",
            StartedByLandlord = false,
            CreatedAt = now.AddMinutes(-10),
            LastMessageAt = now,
            LastMessagePreview = "Second message",
            LandlordUnreadCount = 2,
            TenantUnreadCount = 0,
            Messages =
            [
                new ConversationMessage
                {
                    SenderRole = ConversationSenderRole.Tenant,
                    Body = "Second message",
                    CreatedAt = now,
                },
                new ConversationMessage
                {
                    SenderRole = ConversationSenderRole.Landlord,
                    Body = "First reply",
                    Channels = "Portal",
                    CreatedAt = now.AddMinutes(-5),
                },
            ],
        };
        _db.Conversations.Add(conversation);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var sut = CreateSut();

        var list = await sut.ListAsync(1);
        var summary = list.Should().ContainSingle(c => c.Id == conversation.Id).Subject;
        summary.MessageCount.Should().Be(2);
        summary.UnreadCount.Should().Be(2);

        var detail = await sut.GetAsync(1, conversation.Id);
        detail.Should().NotBeNull();
        detail!.MessageCount.Should().Be(2);
        detail.UnreadCount.Should().Be(2);
        detail.Messages.Select(m => m.Body).Should().Equal("First reply", "Second message");

        _db.Conversations.Single(c => c.Id == conversation.Id).LandlordUnreadCount.Should().Be(2);
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        SeedConversation(tenant.Id, "Alpha", now.AddMinutes(-4));
        SeedConversation(tenant.Id, "Bravo", now.AddMinutes(-3));
        SeedConversation(tenant.Id, "Cedar", now.AddMinutes(-2));
        SeedConversation(tenant.Id, "Delta", now.AddMinutes(-1));

        var sut = CreateSut();

        _commands.Clear();
        var page = await sut.ListPageAsync(1, new ConversationListQuery
        {
            Sort = "subject",
            Skip = 1,
            Take = 2,
        });

        page.TotalCount.Should().Be(4);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(2);
        page.Items.Select(c => c.Subject).Should().Equal("Bravo", "Cedar");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Conversations\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_AppliesSearchAndUnreadFilterInTwoSqlQueries()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        SeedConversation(tenant.Id, "Routine update", now.AddMinutes(-2));
        SeedConversation(
            tenant.Id,
            "Leaking sink follow-up",
            now.AddMinutes(-1),
            landlordUnreadCount: 2);

        _commands.Clear();
        var page = await CreateSut().ListPageAsync(1, new ConversationListQuery
        {
            Search = "sink",
            UnreadOnly = true,
            Take = 20,
        });

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Subject.Should().Be("Leaking sink follow-up");
        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("FROM \"Conversations\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LandlordUnreadCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListPageForTenantAsync_AppliesTenantUnreadFilterAndWindowInSql()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        SeedConversation(tenant.Id, "Already read", now.AddMinutes(-2));
        SeedConversation(
            tenant.Id,
            "Needs tenant reply",
            now.AddMinutes(-1),
            tenantUnreadCount: 3);

        _commands.Clear();
        var page = await CreateSut().ListPageForTenantAsync(
            1,
            tenant.Id,
            new ConversationListQuery
            {
                UnreadOnly = true,
                Skip = 0,
                Take = 1,
            });

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Subject.Should().Be("Needs tenant reply");
        page.Items.Single().UnreadCount.Should().Be(3);
        _commands.Should().HaveCount(2);
        _commands.Should().Contain(sql =>
            sql.Contains("TenantId", StringComparison.Ordinal) &&
            sql.Contains("TenantUnreadCount", StringComparison.Ordinal) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListAndGetAsync_ProjectViewerAwareCounterpartyNames()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var portfolio = await _db.Portfolios.SingleAsync(portfolio => portfolio.Id == 1);
        portfolio.ManagementCompanyName = " Jordan QA Admin ";
        await _db.SaveChangesAsync();
        SeedConversation(tenant.Id, "Portal question", DateTime.UtcNow.AddMinutes(-1));
        var conversationId = await _db.Conversations
            .Where(conversation => conversation.Subject == "Portal question")
            .Select(conversation => conversation.Id)
            .SingleAsync();

        var sut = CreateSut();

        _commands.Clear();
        var tenantPage = await sut.ListPageForTenantAsync(
            1, tenant.Id, new ConversationListQuery { Take = 20 });
        var tenantSummary = tenantPage.Items.Should().ContainSingle().Which;
        tenantSummary.TenantName.Should().Be("Emily Chen");
        tenantSummary.CounterpartyName.Should().Be("Jordan QA Admin");
        var tenantDetail = await sut.GetForTenantAsync(1, tenant.Id, conversationId);
        tenantDetail.Should().NotBeNull();
        tenantDetail!.CounterpartyName.Should().Be("Jordan QA Admin");
        tenantDetail.TenantName.Should().Be("Emily Chen");
        _commands.Should().Contain(sql =>
            sql.Contains("ManagementCompanyName", StringComparison.Ordinal) &&
            sql.Contains("FROM \"Conversations\"", StringComparison.OrdinalIgnoreCase));

        var staffPage = await sut.ListPageAsync(1, new ConversationListQuery { Take = 20 });
        var staffSummary = staffPage.Items.Should().ContainSingle().Which;
        staffSummary.TenantName.Should().Be("Emily Chen");
        staffSummary.CounterpartyName.Should().Be("Emily Chen");
        var staffDetail = await sut.GetAsync(1, conversationId);
        staffDetail.Should().NotBeNull();
        staffDetail!.CounterpartyName.Should().Be("Emily Chen");
    }

    [Fact]
    public async Task ListPageForTenantAsync_FallsBackToGenericCounterpartyWhenManagementNameIsBlank()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var portfolio = await _db.Portfolios.SingleAsync(portfolio => portfolio.Id == 1);
        portfolio.ManagementCompanyName = "   ";
        await _db.SaveChangesAsync();
        SeedConversation(tenant.Id, "Office question", DateTime.UtcNow.AddMinutes(-1));

        var page = await CreateSut().ListPageForTenantAsync(
            1, tenant.Id, new ConversationListQuery { Take = 20 });

        var summary = page.Items.Should().ContainSingle().Which;
        summary.TenantName.Should().Be("Emily Chen");
        summary.CounterpartyName.Should().Be("Property management");
    }

    [Fact]
    public async Task GetUnreadCountAsync_SumsUnreadCountsInSql()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        _db.Portfolios.Add(new Portfolio
        {
            Id = 2,
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        SeedConversation(tenant.Id, "Alpha", DateTime.UtcNow.AddMinutes(-3), landlordUnreadCount: 2);
        SeedConversation(tenant.Id, "Bravo", DateTime.UtcNow.AddMinutes(-2), landlordUnreadCount: 5);
        SeedConversation(tenant.Id, "Other portfolio", DateTime.UtcNow.AddMinutes(-1), portfolioId: 2, landlordUnreadCount: 11);

        var sut = CreateSut();

        _commands.Clear();
        var unreadCount = await sut.GetUnreadCountAsync(1);

        unreadCount.Should().Be(7);
        _commands.Should().Contain(sql =>
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LandlordUnreadCount", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Conversations\"", StringComparison.OrdinalIgnoreCase));
    }

    private Tenant SeedTenantWithStaffAndTenantUsers()
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = 8,
            PortfolioId = 1,
            FirstName = "Emily",
            LastName = "Chen",
            Email = "emily@example.test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);

        _db.Users.AddRange(
            new ApplicationUser
            {
                Id = 10,
                UserName = "admin@example.test",
                NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                Email = "admin@example.test",
                NormalizedEmail = "ADMIN@EXAMPLE.TEST",
                DisplayName = "Admin",
                CreatedAt = now,
            },
            new ApplicationUser
            {
                Id = 20,
                UserName = "emily@example.test",
                NormalizedUserName = "EMILY@EXAMPLE.TEST",
                Email = "emily@example.test",
                NormalizedEmail = "EMILY@EXAMPLE.TEST",
                DisplayName = "Emily Chen",
                CreatedAt = now,
            },
            new ApplicationUser
            {
                Id = 30,
                UserName = "decoy@example.test",
                NormalizedUserName = "DECOY@EXAMPLE.TEST",
                Email = "decoy@example.test",
                NormalizedEmail = "DECOY@EXAMPLE.TEST",
                DisplayName = "Unrelated property manager",
                CreatedAt = now,
            });
        var tenantProperty = new Property
        {
            PortfolioId = 1,
            Name = "Tenant home",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var decoyProperty = new Property
        {
            PortfolioId = 1,
            Name = "Unrelated home",
            AddressLine1 = "2 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = tenantProperty,
            UnitNumber = "1A",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1000,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.AddRange(tenantProperty, decoyProperty);
        _db.Units.Add(unit);
        _db.SaveChanges();

        var authorizedContext = NewAccessContext(10, now);
        var decoyContext = NewAccessContext(30, now);
        var tenantContext = new WorkspaceAccessContext
        {
            UserId = 20,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.WorkspaceAccessContexts.AddRange(authorizedContext, decoyContext, tenantContext);
        _db.SaveChanges();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = tenantProperty.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-CONVERSATION-TENANT",
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = 10,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Conversation portal test",
            CreatedAtUtc = now,
            CreatedByUserId = 10,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = "TA-CONVERSATION-TENANT",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = 10,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = "AGR-CONVERSATION-TENANT",
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
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(1, 10, now),
            CreatedAtUtc = now,
            CreatedByUserId = 10,
            UpdatedAtUtc = now,
        };
        _db.AddRange(relationship, party, account, agreement);
        _db.SaveChanges();

        _db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = 1,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Emily Chen",
            EmailSnapshot = "emily@example.test",
            SigningOrder = 1,
            IsRequired = true,
        });
        _db.SaveChanges();

        IssueAgreement(agreement, now);

        _db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            AccessContext = tenantContext,
            ApplicationUserId = 20,
            LeaseManagementParty = party,
            GrantedAtUtc = now,
            GrantedByUserId = 10,
            Reason = "Conversation portal test",
        });
        _db.SaveChanges();

        var authorizedMembership = NewMembership(authorizedContext.Id, now);
        var decoyMembership = NewMembership(decoyContext.Id, now);
        _db.WorkspaceMemberships.AddRange(authorizedMembership, decoyMembership);
        _db.SaveChanges();
        var authorizedAssignment = NewAssignment(
            authorizedMembership.Id,
            1,
            MembershipRoleAssignmentScopeKind.AllProperties,
            now);
        var decoyAssignment = NewAssignment(
            decoyMembership.Id,
            2,
            MembershipRoleAssignmentScopeKind.SelectedProperties,
            now);
        _db.MembershipRoleAssignments.AddRange(authorizedAssignment, decoyAssignment);
        _db.SaveChanges();
        _db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = decoyAssignment.Id,
            PropertyId = decoyProperty.Id,
            PortfolioId = 1,
        });
        _db.SaveChanges();

        return tenant;
    }

    private void IssueAgreement(LeaseAgreement agreement, DateTime now)
    {
        var issuedFile = AgreementFile($"agreement-{agreement.Id}-issued.pdf", now);
        var executedFile = AgreementFile($"agreement-{agreement.Id}-executed.pdf", now);
        _db.StoredFiles.AddRange(issuedFile, executedFile);
        _db.SaveChanges();

        var issuedArtifact = AgreementArtifact(
            issuedFile, LegalDocumentArtifactKind.IssuedAgreement, new string('a', 64), now);
        var executedArtifact = AgreementArtifact(
            executedFile, LegalDocumentArtifactKind.ExecutedAgreement, new string('b', 64), now);
        issuedArtifact.LegalIssuanceFingerprint = new string('c', 64);
        _db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now;
        _db.SaveChanges();
    }

    private static StoredFile AgreementFile(string fileName, DateTime now) => new()
    {
        PortfolioId = 1,
        FileName = fileName,
        FilePath = $"legal/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact AgreementArtifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        string sha256,
        DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = file.FilePath,
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = sha256,
        CreatedAtUtc = now,
        CreatedByUserId = 10,
    };

    private static WorkspaceAccessContext NewAccessContext(int userId, DateTime now) => new()
    {
        UserId = userId,
        PortfolioId = 1,
        Status = WorkspaceAccessContextStatus.Active,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static WorkspaceMembership NewMembership(int accessContextId, DateTime now) => new()
    {
        AccessContextId = accessContextId,
        PortfolioId = 1,
        Status = WorkspaceMembershipStatus.Active,
        DefaultExperience = WorkspaceExperience.Management,
        EffectiveFromUtc = now.AddDays(-1),
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static MembershipRoleAssignment NewAssignment(
        int membershipId,
        int roleProfileId,
        MembershipRoleAssignmentScopeKind scopeKind,
        DateTime now) => new()
    {
        WorkspaceMembershipId = membershipId,
        PortfolioId = 1,
        RoleProfileId = roleProfileId,
        Status = MembershipRoleAssignmentStatus.Active,
        ScopeKind = scopeKind,
        EffectiveFromUtc = now.AddDays(-1),
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private void SeedConversation(
        int tenantId,
        string subject,
        DateTime lastMessageAt,
        int portfolioId = 1,
        int landlordUnreadCount = 0,
        int tenantUnreadCount = 0)
    {
        _db.Conversations.Add(new Conversation
        {
            PortfolioId = portfolioId,
            TenantId = tenantId,
            Subject = subject,
            StartedByLandlord = true,
            CreatedAt = lastMessageAt.AddMinutes(-1),
            LastMessageAt = lastMessageAt,
            LastMessagePreview = subject,
            LandlordUnreadCount = landlordUnreadCount,
            TenantUnreadCount = tenantUnreadCount,
        });
        _db.SaveChanges();
    }

    private async Task<ApprovedNoticeGraph> SeedApprovedPaymentNoticeWithStaleMessageNotificationAsync(
        string businessKey)
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var now = DateTime.UtcNow;
        var approvedAt = DateTime.SpecifyKind(now.AddMinutes(-10), DateTimeKind.Utc);
        var adminContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 10);
        var tenantContext = await _db.WorkspaceAccessContexts.SingleAsync(context =>
            context.PortfolioId == 1 && context.UserId == 20);
        var lease = await _db.LeaseManagements.SingleAsync(lease => lease.PortfolioId == 1);
        var party = await _db.LeaseManagementParties.SingleAsync(party => party.PortfolioId == 1);
        var tenantAccount = await _db.TenantAccounts.SingleAsync(account => account.PortfolioId == 1);
        var ledgerEntry = await SeedTenantLedgerEntryAsync(
            tenantAccount.Id,
            $"{businessKey}-charge",
            now);
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = 10,
            ActiveAccessContextId = adminContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _db.AuthSessions.Add(session);

        var systemTemplate = new SystemNoticeTemplateVersion
        {
            SystemKey = $"rent-reminder-{businessKey}",
            Version = 1,
            Classification = NoticeClassification.Operational,
            Subject = "Pay January rent",
            Body = "Please pay January rent from the tenant portal.",
            Provenance = "test",
            PublishedAtUtc = now,
        };
        var workspaceTemplate = new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = 1,
            SystemKey = systemTemplate.SystemKey,
            Version = 1,
            BasedOnSystemTemplateVersion = systemTemplate,
            Subject = systemTemplate.Subject,
            Body = systemTemplate.Body,
            CreatedByUserId = 10,
            CreatedAtUtc = now,
        };
        var policy = new TenantNoticePolicy
        {
            PortfolioId = 1,
            AutomationKey = $"policy-{businessKey}",
            Mode = TenantNoticeMode.Draft,
            Classification = NoticeClassification.Operational,
            SendTenantPortal = true,
            SendEmail = true,
            IncludePrimaryTenant = true,
            TemplateVersion = workspaceTemplate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _db.TenantNoticePolicies.Add(policy);
        await _db.SaveChangesAsync();
        var draft = new NoticeDraft
        {
            PortfolioId = 1,
            LeaseManagementId = lease.Id,
            TenantAccountId = tenantAccount.Id,
            RecipientLeaseManagementPartyId = party.Id,
            TenantLedgerEntryId = ledgerEntry.Id,
            PropertyId = lease.PropertyId,
            NoticeType = "rent-reminder",
            Status = "Approved",
            Subject = systemTemplate.Subject,
            Body = systemTemplate.Body,
            Reason = "Payment reminder",
            TriggerDate = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc),
            TenantNoticePolicyId = policy.Id,
            WorkspaceNoticeTemplateVersionId = workspaceTemplate.Id,
            ApprovedChannels = NoticeDeliveryChannel.TenantPortal.ToString(),
            CreatedAt = now,
            UpdatedAt = approvedAt,
            ApprovedAt = approvedAt,
        };
        _db.NoticeDrafts.Add(draft);
        await _db.SaveChangesAsync();

        var contentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(draft.Subject + "\n" + draft.Body)))
            .ToLowerInvariant();
        var rendered = new RenderedNotice
        {
            PortfolioId = 1,
            NoticeDraftId = draft.Id,
            WorkspaceNoticeTemplateVersionId = workspaceTemplate.Id,
            LeaseManagementId = lease.Id,
            Subject = draft.Subject,
            Body = draft.Body,
            ContentSha256 = contentHash,
            TemplateProvenance = $"{workspaceTemplate.SystemKey}:workspace-v1:system-v1",
            RenderedAtUtc = approvedAt,
            ApprovedByUserId = 10,
            ApprovedAtUtc = approvedAt,
        };
        _db.RenderedNotices.Add(rendered);
        await _db.SaveChangesAsync();

        var conversation = new Conversation
        {
            PortfolioId = 1,
            TenantId = tenant.Id,
            Subject = rendered.Subject,
            PropertyId = lease.PropertyId,
            StartedByLandlord = true,
            CreatedAt = approvedAt,
            LastMessageAt = approvedAt,
            LastMessagePreview = rendered.Body,
            TenantUnreadCount = 1,
        };
        var message = new ConversationMessage
        {
            Conversation = conversation,
            SenderRole = ConversationSenderRole.Landlord,
            Body = rendered.Body,
            Channels = "Portal",
            CreatedAt = approvedAt,
        };
        _db.ConversationMessages.Add(message);
        await _db.SaveChangesAsync();

        var deliveryKey = DeliveryKey(rendered.Id, party.Id, NoticeDeliveryChannel.TenantPortal, "20");
        var outbox = new OutboxMessage
        {
            PortfolioId = 1,
            MessageType = "data-update",
            Payload = "{}",
            IdempotencyKey = deliveryKey,
            CreatedAtUtc = approvedAt,
            NextAttemptAtUtc = approvedAt,
        };
        var evidence = new NoticeDeliveryEvidence
        {
            PortfolioId = 1,
            RenderedNoticeId = rendered.Id,
            RecipientLeaseManagementPartyId = party.Id,
            RecipientRole = NoticeRecipientRole.PrimaryTenant,
            Channel = NoticeDeliveryChannel.TenantPortal,
            Destination = "20",
            OutboxMessage = outbox,
            ConversationMessageId = message.Id,
            IdempotencyKey = deliveryKey,
            CreatedAtUtc = approvedAt,
        };
        var notification = new Notification
        {
            PortfolioId = 1,
            UserId = 20,
            Type = "TenantNotice",
            Title = rendered.Subject,
            Message = rendered.Body,
            Severity = "Info",
            NavigationExperience = NavigationExperience.Tenant,
            NavigationDestination = NavigationDestination.Message,
            NavigationAccessContextId = tenantContext.Id,
            NavigationAccessRevision = tenantContext.AccessRevision,
            NavigationResourceKind = nameof(Conversation),
            NavigationResourceId = conversation.Id,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = approvedAt.AddDays(7),
            NavigationFallbackDestination = NavigationDestination.Home,
            RelatedEntityType = nameof(Conversation),
            RelatedEntityId = conversation.Id,
            CreatedAt = approvedAt,
        };
        draft.RenderedNoticeId = rendered.Id;
        draft.ConversationId = conversation.Id;
        _db.AddRange(evidence, notification);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return new ApprovedNoticeGraph(
            draft.Id,
            rendered.Id,
            notification.Id,
            conversation.Id,
            party.Id,
            tenantAccount.Id,
            ledgerEntry.Id,
            new NoticeApprovalExecutionContext(1, 10, session.Id, adminContext.Id, adminContext.AccessRevision));
    }

    private async Task<TenantLedgerEntry> SeedTenantLedgerEntryAsync(
        int tenantAccountId,
        string businessKey,
        DateTime now,
        DateOnly? effectiveOn = null,
        DateOnly? dueOn = null)
    {
        var entry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            TenantAccountId = tenantAccountId,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1200m,
            Currency = "USD",
            EffectiveOn = effectiveOn ?? DateOnly.FromDateTime(now),
            DueOn = dueOn ?? DateOnly.FromDateTime(now.AddDays(5)),
            PostedAtUtc = now,
            Description = "January rent",
            BusinessKey = businessKey,
            CreatedByUserId = 10,
        };
        _db.TenantLedgerEntries.Add(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    private async Task<TenantLedgerEntry> SeedForeignTenantLedgerEntryAsync(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Foreign tenant home",
            AddressLine1 = "9 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "9A",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1250,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = "LM-FOREIGN-NOTIFICATION",
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = 10,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = "TA-FOREIGN-NOTIFICATION",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = 10,
        };
        _db.AddRange(property, unit, relationship, account);
        await _db.SaveChangesAsync();
        return await SeedTenantLedgerEntryAsync(account.Id, "foreign-rent-charge", now);
    }

    private static string DeliveryKey(
        long renderedNoticeId,
        int partyId,
        NoticeDeliveryChannel channel,
        string destination) =>
        $"notice:{renderedNoticeId}:party:{partyId}:{channel}:{DestinationHash(destination)}";

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))
            .ToLowerInvariant()[..16];

    private sealed record ApprovedNoticeGraph(
        int DraftId,
        long RenderedNoticeId,
        int NotificationId,
        int ConversationId,
        int LeaseManagementPartyId,
        int TenantAccountId,
        long TenantLedgerEntryId,
        NoticeApprovalExecutionContext ApprovalContext);

    private static Notification TenantLedgerNotification(
        int userId,
        string title,
        WorkspaceAccessContext tenantContext,
        int tenantAccountId,
        long tenantLedgerEntryId,
        DateTime now,
        string type = "TenantNotice") => new()
        {
            PortfolioId = 1,
            UserId = userId,
            Type = type,
            Title = title,
            Message = "A rent charge needs attention.",
            Severity = "Info",
            NavigationExperience = NavigationExperience.Tenant,
            NavigationDestination = NavigationDestination.TenantLedgerEntry,
            NavigationAccessContextId = tenantContext.Id,
            NavigationAccessRevision = tenantContext.AccessRevision,
            NavigationResourceKind = nameof(TenantLedgerEntry),
            NavigationResourceId = checked((int)tenantLedgerEntryId),
            NavigationParentResourceKind = nameof(TenantAccount),
            NavigationParentResourceId = tenantAccountId,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = now.AddDays(7),
            NavigationFallbackDestination = NavigationDestination.Home,
            RelatedEntityType = nameof(TenantLedgerEntry),
            RelatedEntityId = checked((int)tenantLedgerEntryId),
            CreatedAt = now,
        };

    private sealed class RecordingRealtimeInvalidationQueue : IRealtimeInvalidationQueue
    {
        public List<IReadOnlyList<EntityUpdateBroadcast>> Batches { get; } = [];

        public void EnqueueEntityUpdates(IReadOnlyList<EntityUpdateBroadcast> updates) =>
            Batches.Add(updates.ToArray());
    }

    // These tests exercise the TENANT send path (TenantStartAsync), which is not Fair-Housing gated,
    // so a no-op reviewer (review-unavailable shape) is sufficient and never blocks.
    private sealed class NoopFairHousingReviewService : IFairHousingReviewService
    {
        public Task<FairHousingReviewResult> ReviewAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new FairHousingReviewResult { Reviewed = false, Compliant = false });
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
