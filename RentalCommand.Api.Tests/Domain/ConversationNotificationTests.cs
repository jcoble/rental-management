using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class ConversationNotificationTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task TenantStartAsync_CreatesTenantMessageNotificationsForStaffOnly()
    {
        var tenant = SeedTenantWithStaffAndTenantUsers();
        var sut = new ConversationService(_ctx.Db, new NoopDataUpdateService());

        var result = await sut.TenantStartAsync(1, tenant.Id, "Sink leak", "Water under the cabinet");

        result.Should().NotBeNull();
        var notification = _ctx.Db.Notifications.Should().ContainSingle().Subject;
        notification.Type.Should().Be("TenantMessage");
        notification.UserId.Should().Be(10);
        notification.Title.Should().Be("New message from Emily Chen");
        notification.ActionUrl.Should().Be($"/messages?conversationId={result!.Id}");
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

        var sut = new NotificationService(_ctx.Db);

        var tenantItems = await sut.ListAsync(1, userId: 20);
        tenantItems.Select(n => n.Title).Should().Equal("Pool closed");
        var staffItems = await sut.ListAsync(1, userId: 10);
        staffItems.Select(n => n.Title).Should().Contain("New message from Emily Chen");
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

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
