using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Conversations;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Conversations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class ConversationNotificationTests : IDisposable
{
    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly ServiceProvider _services;

    public ConversationNotificationTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<
            SendConversationMessageCommand,
            SendConversationMessageResult,
            SendConversationMessageHandler>();
        services.AddDbContext<RentalCommand.Data.RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(_ctx.Connection)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    private ConversationService CreateSut() => new(
        _ctx.Db,
        new NoopDataUpdateService(),
        new NoopFairHousingReviewService(),
        NullLogger<ConversationService>.Instance,
        TimeProvider.System,
        _services.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());

    [Fact]
    public async Task TenantStartAsync_NotifiesOnlyCapabilityAndLeasePropertyScopedStaff()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var sut = CreateSut();

        var result = await sut.TenantStartAsync(
            1, tenant.Id, "Sink leak", "Water under the cabinet", "tenant-start-sink-leak");

        result.Should().NotBeNull();
        var notification = _ctx.Db.Notifications.Should().ContainSingle().Subject;
        notification.Type.Should().Be("TenantMessage");
        notification.UserId.Should().Be(10);
        notification.Title.Should().Be("New message from Emily Chen");
        notification.ActionUrl.Should().Be($"/messages?conversationId={result!.Id}");
        _ctx.Db.Notifications.Should().NotContain(item => item.UserId == 30);
    }

    [Fact]
    public async Task TenantStartAsync_WithoutLeaseResponsibility_CommitsMessageButNoRecipientNotification()
    {
        var role = new IdentityRole<int>(nameof(UserRole.Admin))
        {
            Id = 11,
            NormalizedName = nameof(UserRole.Admin).ToUpperInvariant(),
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "No",
            LastName = "Relationship",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var legacyAdmin = new ApplicationUser
        {
            Id = 12,
            PortfolioId = 1,
            UserName = "legacy-admin@example.test",
            NormalizedUserName = "LEGACY-ADMIN@EXAMPLE.TEST",
            DisplayName = "Legacy admin",
            CreatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Roles.Add(role);
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.Users.Add(legacyAdmin);
        _ctx.Db.UserRoles.Add(new IdentityUserRole<int> { UserId = legacyAdmin.Id, RoleId = role.Id });
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
        notification.ActionUrl.Should().Be($"/portal/messages?conversation={result!.Id}");
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

        var sut = new NotificationService(_ctx.Db, TimeProvider.System, new NoopDataUpdateService());

        var tenantItems = await sut.ListAsync(1, userId: 20);
        tenantItems.Select(n => n.Title).Should().Equal("Pool closed");
        var staffItems = await sut.ListAsync(1, userId: 10);
        staffItems.Select(n => n.Title).Should().Contain("New message from Emily Chen");
    }

    [Fact]
    public async Task CreateBroadcastAsync_NormalizesSeverityAndCountsAsUnreadForPortfolioUsers()
    {
        var sut = new NotificationService(_ctx.Db, TimeProvider.System, new NoopDataUpdateService());

        var created = await sut.CreateBroadcastAsync(
            1,
            new CreateBroadcastNotificationRequest
            {
                Title = "Pool closed",
                Message = "The pool is closed for maintenance.",
                Severity = "critical",
            });

        created.Severity.Should().Be("Critical");
        created.IsRead.Should().BeFalse();

        var items = await sut.ListAsync(1, userId: 10);
        items.Should().ContainSingle(n => n.Id == created.Id && n.Severity == "Critical");

        var unreadCount = await sut.GetUnreadCountAsync(1, userId: 10);
        unreadCount.Should().Be(1);
    }

    [Fact]
    public async Task ListAndGetAsync_ProjectMessageCountsAndOrderedMessagesFromDatabase()
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
        detail.UnreadCount.Should().Be(0);
        detail.Messages.Select(m => m.Body).Should().Equal("First reply", "Second message");

        _ctx.Db.Conversations.Single(c => c.Id == conversation.Id).LandlordUnreadCount.Should().Be(0);
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
        var page = await sut.ListPageAsync(1, new ListQuery
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
        _ctx.Db.Roles.AddRange(
            new IdentityRole<int>(nameof(UserRole.Admin))
            {
                Id = 1,
                NormalizedName = nameof(UserRole.Admin).ToUpperInvariant(),
            },
            new IdentityRole<int>(nameof(UserRole.Tenant))
            {
                Id = 2,
                NormalizedName = nameof(UserRole.Tenant).ToUpperInvariant(),
            });

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
                PortfolioId = 1,
                UserName = "admin@example.test",
                NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                Email = "admin@example.test",
                NormalizedEmail = "ADMIN@EXAMPLE.TEST",
                DisplayName = "Admin",
            },
            new ApplicationUser
            {
                Id = 20,
                PortfolioId = 1,
                TenantId = tenant.Id,
                UserName = "emily@example.test",
                NormalizedUserName = "EMILY@EXAMPLE.TEST",
                Email = "emily@example.test",
                NormalizedEmail = "EMILY@EXAMPLE.TEST",
                DisplayName = "Emily Chen",
            },
            new ApplicationUser
            {
                Id = 30,
                PortfolioId = 1,
                UserName = "decoy@example.test",
                NormalizedUserName = "DECOY@EXAMPLE.TEST",
                Email = "decoy@example.test",
                NormalizedEmail = "DECOY@EXAMPLE.TEST",
                DisplayName = "Unrelated property manager",
            });
        _ctx.Db.UserRoles.AddRange(
            new IdentityUserRole<int> { UserId = 10, RoleId = 1 },
            new IdentityUserRole<int> { UserId = 20, RoleId = 2 },
            // A legacy Admin role is intentionally insufficient without an in-scope TSK-670 assignment.
            new IdentityUserRole<int> { UserId = 30, RoleId = 1 });
        var now = DateTime.UtcNow;
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

        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = 1,
            PropertyId = tenantProperty.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "ACTIVE-1",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddMonths(11),
            MonthlyRent = 1000,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var authorizedContext = NewAccessContext(10, now);
        var decoyContext = NewAccessContext(30, now);
        _ctx.Db.WorkspaceAccessContexts.AddRange(authorizedContext, decoyContext);
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
        int landlordUnreadCount = 0)
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
        });
        _ctx.Db.SaveChanges();
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
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
