using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Conversations;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Conversations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public class ConversationNotificationTests : IAsyncLifetime
{
    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;

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
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<
            SendConversationMessageCommand,
            SendConversationMessageResult,
            SendConversationMessageHandler>();
        services.AddAtomicCommandHandler<
            AtomicNotificationMutationCommand,
            AtomicNotificationMutationResult,
            AtomicNotificationMutationHandler>();
        services.AddDbContext<RentalCommand.Data.RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_ctx.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    private ConversationService CreateSut(RecordingRealtimeInvalidationQueue? realtimeQueue = null) => new(
        _ctx.Db,
        realtimeQueue ?? new RecordingRealtimeInvalidationQueue(),
        new NoopFairHousingReviewService(),
        NullLogger<ConversationService>.Instance,
        TimeProvider.System,
        _services.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());

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
        _ctx.Db.TeamRoutingRules.Add(routingRule);
        _ctx.Db.SaveChanges();
        var realtimeQueue = new RecordingRealtimeInvalidationQueue();
        var sut = CreateSut(realtimeQueue);

        var result = await sut.TenantStartAsync(
            1, tenant.Id, "Sink leak", "Water under the cabinet", "tenant-start-sink-leak");

        result.Should().NotBeNull();
        var notification = _ctx.Db.Notifications.Should().ContainSingle().Subject;
        notification.Type.Should().Be("TenantMessage");
        notification.UserId.Should().Be(10);
        notification.Title.Should().Be("New message from Emily Chen");
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(result!.Id);
        notification.NavigationAccessContextId.Should().BePositive();
        notification.NavigationAccessRevision.Should().BePositive();
        _ctx.Db.Notifications.Should().NotContain(item => item.UserId == 30);
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
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var result = await CreateSut().TenantStartAsync(
            1, tenant.Id, "Question", "Who is responsible?", "tenant-no-responsibility");

        result.Should().NotBeNull();
        _ctx.Db.ConversationMessages.Should().ContainSingle();
        _ctx.Db.Notifications.Should().BeEmpty();
        _ctx.Db.AtomicCommandReceipts.Should().ContainSingle(receipt =>
            receipt.CommandType == "conversation.tenant-start");
        _ctx.Db.AtomicAuditLogs.Should().Contain(log => log.EntityType == nameof(ConversationMessage));
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
        var notification = _ctx.Db.Notifications.Should().ContainSingle(n => n.Type == "TenantNotice").Subject;
        notification.UserId.Should().Be(20);
        notification.NavigationExperience.Should().Be(NavigationExperience.Tenant);
        notification.NavigationDestination.Should().Be(NavigationDestination.Message);
        notification.NavigationResourceKind.Should().Be(nameof(Conversation));
        notification.NavigationResourceId.Should().Be(result!.Id);
        _ctx.Db.OutboxMessages.Should().NotContain(m => m.MessageType == "push");
    }

    [Fact]
    public async Task LandlordStartAsync_ReplayedOperation_CommitsOneMessageAndOneIntentPerDestination()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        tenant.Phone = "+15551234567";
        _ctx.Db.SaveChanges();
        var sut = CreateSut();
        const string operationKey = "conversation-retry-1";

        var first = await sut.StartAsync(
            1, tenant.Id, "Inspection", "Can we visit Friday?", ["Portal", "Email", "Sms"], operationKey);
        var replay = await sut.StartAsync(
            1, tenant.Id, "Inspection", "Can we visit Friday?", ["Portal", "Email", "Sms"], operationKey);

        replay!.Id.Should().Be(first!.Id);
        (await _ctx.Db.Conversations.CountAsync()).Should().Be(1);
        (await _ctx.Db.ConversationMessages.CountAsync()).Should().Be(1);
        (await _ctx.Db.Notifications.CountAsync(notification => notification.Type == "TenantNotice")).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(2);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
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
        (await _ctx.Db.ConversationMessages.CountAsync()).Should().Be(2);
        (await _ctx.Db.Conversations.AsNoTracking()
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
        var tenantContext = await _ctx.Db.WorkspaceAccessContexts.SingleAsync(context =>
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
        _ctx.Db.AuthSessions.Add(session);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var conversationId = await _ctx.Db.Conversations.AsNoTracking()
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

        (await _ctx.Db.Conversations.AsNoTracking()
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation => conversation.TenantUnreadCount)
            .SingleAsync()).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.AsNoTracking().CountAsync(log =>
            log.EntityType == nameof(Conversation)
            && log.EntityId == conversationId
            && log.ChangeReason == "Tenant conversation marked read")).Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_HidesTenantMessageNotificationsFromTenantOnlyUsers()
    {
        SeedTenantWithStaffAndTenantUsers();
        _ctx.Db.Notifications.AddRange(
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
                Type = "System",
                Title = "Pool closed",
                Message = "Shared system notice",
                CreatedAt = DateTime.UtcNow.AddMinutes(1),
            });
        _ctx.Db.SaveChanges();

        var sut = new NotificationService(
            _ctx.Db, TimeProvider.System, _services.GetRequiredService<IAtomicUnitOfWork>());

        var tenantItems = await sut.ListAsync(1, userId: 20);
        tenantItems.Select(n => n.Title).Should().Equal("Pool closed");
        var staffItems = await sut.ListAsync(1, userId: 10);
        staffItems.Select(n => n.Title).Should().Contain("New message from Emily Chen");
    }

    [Fact]
    public async Task CreateBroadcastAsync_NormalizesSeverityAndCountsAsUnreadForPortfolioUsers()
    {
        SeedTenantWithStaffAndTenantUsers();
        var context = await _ctx.Db.WorkspaceAccessContexts.SingleAsync(row =>
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
        _ctx.Db.AuthSessions.Add(session);
        await _ctx.Db.SaveChangesAsync();
        var scope = new WorkspaceReadScope(1, 10, session.Id, context.Id, context.AccessRevision);
        var sut = new NotificationService(
            _ctx.Db, TimeProvider.System, _services.GetRequiredService<IAtomicUnitOfWork>());

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
        _ctx.Db.Notifications.Add(new Notification
        {
            PortfolioId = 1,
            Type = "System",
            Title = "Water interruption",
            Message = "Water will be off from noon until two.",
            CreatedAt = now,
        });
        var contexts = await _ctx.Db.WorkspaceAccessContexts
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
        _ctx.Db.AuthSessions.AddRange(sessions.Values);
        await _ctx.Db.SaveChangesAsync();
        var notificationId = await _ctx.Db.Notifications.Select(notification => notification.Id).SingleAsync();
        var sut = new NotificationService(
            _ctx.Db, TimeProvider.System, _services.GetRequiredService<IAtomicUnitOfWork>());

        var staffContext = contexts[10];
        var staffScope = new WorkspaceReadScope(
            1, 10, sessions[10].Id, staffContext.Id, staffContext.AccessRevision);
        (await sut.MarkAsReadAsync(staffScope, notificationId, "read-water-interruption"))
            .Should().BeTrue();

        (await sut.ListAsync(1, 10)).Should().ContainSingle().Which.IsRead.Should().BeTrue();
        (await sut.ListAsync(1, 30)).Should().ContainSingle().Which.IsRead.Should().BeFalse();
        (await sut.ListAsync(1, 20)).Should().ContainSingle().Which.IsRead.Should().BeFalse();
        (await sut.GetUnreadCountAsync(1, 10)).Should().Be(0);
        (await sut.GetUnreadCountAsync(1, 30)).Should().Be(1);
        (await sut.GetUnreadCountAsync(1, 20)).Should().Be(1);

        var secondStaffContext = contexts[30];
        var secondStaffScope = new WorkspaceReadScope(
            1, 30, sessions[30].Id, secondStaffContext.Id, secondStaffContext.AccessRevision);
        await sut.MarkAllAsReadAsync(secondStaffScope, "read-all-water-interruption");

        (await sut.GetUnreadCountAsync(1, 30)).Should().Be(0);
        (await sut.GetUnreadCountAsync(1, 20)).Should().Be(1);
        (await _ctx.Db.NotificationReadStates.AsNoTracking()
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
        _ctx.Db.Conversations.Add(conversation);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();

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

        _ctx.Db.Conversations.Single(c => c.Id == conversation.Id).LandlordUnreadCount.Should().Be(2);
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
    public async Task GetUnreadCountAsync_SumsUnreadCountsInSql()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = 2,
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
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
        _ctx.Db.Tenants.Add(tenant);

        _ctx.Db.Users.AddRange(
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
        _ctx.Db.Properties.AddRange(tenantProperty, decoyProperty);
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();

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
        _ctx.Db.WorkspaceAccessContexts.AddRange(authorizedContext, decoyContext, tenantContext);
        _ctx.Db.SaveChanges();

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
        _ctx.Db.AddRange(relationship, party, account, agreement);
        _ctx.Db.SaveChanges();

        _ctx.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
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
        _ctx.Db.SaveChanges();

        IssueAgreement(agreement, now);

        _ctx.Db.TenantUserAccesses.Add(new TenantUserAccess
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
        _ctx.Db.SaveChanges();

        var authorizedMembership = NewMembership(authorizedContext.Id, now);
        var decoyMembership = NewMembership(decoyContext.Id, now);
        _ctx.Db.WorkspaceMemberships.AddRange(authorizedMembership, decoyMembership);
        _ctx.Db.SaveChanges();
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
        _ctx.Db.MembershipRoleAssignments.AddRange(authorizedAssignment, decoyAssignment);
        _ctx.Db.SaveChanges();
        _ctx.Db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = decoyAssignment.Id,
            PropertyId = decoyProperty.Id,
            PortfolioId = 1,
        });
        _ctx.Db.SaveChanges();

        return tenant;
    }

    private void IssueAgreement(LeaseAgreement agreement, DateTime now)
    {
        var issuedFile = AgreementFile($"agreement-{agreement.Id}-issued.pdf", now);
        var executedFile = AgreementFile($"agreement-{agreement.Id}-executed.pdf", now);
        _ctx.Db.StoredFiles.AddRange(issuedFile, executedFile);
        _ctx.Db.SaveChanges();

        var issuedArtifact = AgreementArtifact(
            issuedFile, LegalDocumentArtifactKind.IssuedAgreement, new string('a', 64), now);
        var executedArtifact = AgreementArtifact(
            executedFile, LegalDocumentArtifactKind.ExecutedAgreement, new string('b', 64), now);
        issuedArtifact.LegalIssuanceFingerprint = new string('c', 64);
        _ctx.Db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _ctx.Db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now;
        _ctx.Db.SaveChanges();
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
        _ctx.Db.Conversations.Add(new Conversation
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
        _ctx.Db.SaveChanges();
    }

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
