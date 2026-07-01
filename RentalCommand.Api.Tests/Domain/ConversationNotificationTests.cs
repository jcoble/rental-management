using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class ConversationNotificationTests : IDisposable
{
    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;

    public ConversationNotificationTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task TenantStartAsync_CreatesTenantMessageNotificationsForStaffOnly()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var sut = new ConversationService(
            _ctx.Db, new NoopDataUpdateService(), new NoopFairHousingReviewService(),
            NullLogger<ConversationService>.Instance,
            TimeProvider.System);

        var result = await sut.TenantStartAsync(1, tenant.Id, "Sink leak", "Water under the cabinet");

        result.Should().NotBeNull();
        var notification = _ctx.Db.Notifications.Should().ContainSingle().Subject;
        notification.Type.Should().Be("TenantMessage");
        notification.UserId.Should().Be(10);
        notification.Title.Should().Be("New message from Emily Chen");
        notification.ActionUrl.Should().Be($"/messages?conversationId={result!.Id}");
    }

    [Fact]
    public async Task LandlordStartAsync_WithPortalChannel_CreatesTenantNotification()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var sut = new ConversationService(
            _ctx.Db, new NoopDataUpdateService(), new NoopFairHousingReviewService(),
            NullLogger<ConversationService>.Instance,
            TimeProvider.System);

        var result = await sut.StartAsync(
            1,
            tenant.Id,
            "Rent reminder",
            "Please check the payment portal.",
            ["Portal"]);

        result.Should().NotBeNull();
        var notification = _ctx.Db.Notifications.Should().ContainSingle(n => n.Type == "TenantNotice").Subject;
        notification.UserId.Should().Be(20);
        notification.ActionUrl.Should().Be($"/portal/messages?conversation={result!.Id}");
        _ctx.Db.OutboxMessages.Should().NotContain(m => m.MessageType == "push");
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

        var sut = new ConversationService(
            _ctx.Db, new NoopDataUpdateService(), new NoopFairHousingReviewService(),
            NullLogger<ConversationService>.Instance,
            TimeProvider.System);

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

        var sut = new ConversationService(
            _ctx.Db, new NoopDataUpdateService(), new NoopFairHousingReviewService(),
            NullLogger<ConversationService>.Instance,
            TimeProvider.System);

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

        var sut = new ConversationService(
            _ctx.Db, new NoopDataUpdateService(), new NoopFairHousingReviewService(),
            NullLogger<ConversationService>.Instance,
            TimeProvider.System);

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
            });
        _ctx.Db.UserRoles.AddRange(
            new IdentityUserRole<int> { UserId = 10, RoleId = 1 },
            new IdentityUserRole<int> { UserId = 20, RoleId = 2 });
        _ctx.Db.SaveChanges();

        return tenant;
    }

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
