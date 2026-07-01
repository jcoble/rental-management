using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests the staff "send portal invite" endpoint (<see cref="TenantController.SendPortalInvite"/>):
/// it ensures the tenant's portal login exists and enqueues the invite email — the only place a portal
/// invite goes out. Uses a real provisioning service + UserManager + outbox sender over SQLite.
/// </summary>
public sealed class TenantPortalInviteEndpointTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _ctx = new();
    private readonly UserManager<ApplicationUser> _userManager;

    public TenantPortalInviteEndpointTests()
    {
        _userManager = CreateUserManager(_ctx.Db);
        _ctx.Db.Roles.Add(new IdentityRole<int>
        {
            Name = nameof(UserRole.Tenant),
            NormalizedName = nameof(UserRole.Tenant).ToUpperInvariant(),
        });
        _ctx.Db.SaveChanges();
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task SendPortalInvite_WithEmail_EnsuresLoginAndEnqueuesInviteEmail()
    {
        var tenant = SeedTenant("invitee@example.local");
        var controller = CreateController();

        var result = await controller.SendPortalInvite(tenant.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PortalInviteResponse>().Subject;
        response.Email.Should().Be("invitee@example.local");

        // The same call provisioned the portal login.
        (await _userManager.FindByEmailAsync("invitee@example.local")).Should().NotBeNull();

        // The invite email is enqueued on the outbox (MessageType "email"), carrying the sign-in email,
        // the shared tenant password, and the login link.
        var outbox = _ctx.Db.OutboxMessages.Should().ContainSingle().Subject;
        outbox.MessageType.Should().Be("email");
        outbox.Payload.Should().Contain("invitee@example.local");
        outbox.Payload.Should().Contain(new SeedSettings().TenantPassword);
        outbox.Payload.Should().Contain("/login");
    }

    [Fact]
    public async Task SendPortalInvite_TenantWithoutEmail_ReturnsBadRequestAndSendsNothing()
    {
        var tenant = SeedTenant(email: null);
        var controller = CreateController();

        var result = await controller.SendPortalInvite(tenant.Id, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _ctx.Db.OutboxMessages.Should().BeEmpty();
        _ctx.Db.Users.Should().BeEmpty();
    }

    private Tenant SeedTenant(string? email)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Portal",
            LastName = "Invitee",
            Email = email,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private TenantController CreateController()
    {
        var provisioning = new TenantPortalProvisioningService(
            _userManager, _ctx.Db, Options.Create(new SeedSettings()),
            TimeProvider.System,
            NullLogger<TenantPortalProvisioningService>.Instance);
        var emailSender = new OutboxAuthEmailSender(
            _ctx.Db, new ConfigurationBuilder().Build(), NullLogger<OutboxAuthEmailSender>.Instance);

        return new TenantController(
            Mock.Of<ITenantService>(),
            provisioning,
            emailSender,
            _userManager,
            Options.Create(new SeedSettings()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("portfolioId", PortfolioId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                        new Claim(ClaimTypes.Role, "Admin"),
                    ], "test")),
                },
            },
        };
    }

    private static UserManager<ApplicationUser> CreateUserManager(RentalCommandDbContext db)
    {
        var store = new UserStore<ApplicationUser, IdentityRole<int>, RentalCommandDbContext, int>(db);
        return new UserManager<ApplicationUser>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }
}
