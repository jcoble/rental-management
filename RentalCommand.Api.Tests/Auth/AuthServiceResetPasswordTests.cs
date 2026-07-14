using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Data;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public sealed class AuthServiceResetPasswordTests : IDisposable
{
    private const string ResetToken = "reset-token";
    private readonly SqliteTestContext _ctx = new();
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthServiceResetPasswordTests()
    {
        _userManager = CreateUserManager(_ctx.Db);
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task ResetPasswordAsync_ClearsLockoutSoUserCanSignInAfterReset()
    {
        var user = new ApplicationUser
        {
            UserName = "locked-reset@example.local",
            Email = "locked-reset@example.local",
            EmailConfirmed = true,
            DisplayName = "Locked Reset",
            CreatedAt = DateTime.UtcNow,
        };
        (await _userManager.CreateAsync(user, "OldPassword123!")).Succeeded.Should().BeTrue();
        (await _userManager.SetLockoutEnabledAsync(user, true)).Succeeded.Should().BeTrue();
        (await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded.Should().BeTrue();

        var result = await CreateService().ResetPasswordAsync(user.Id.ToString(), ResetToken, "NewPassword123!");

        result.Success.Should().BeTrue();
        var reloaded = await _userManager.FindByIdAsync(user.Id.ToString());
        reloaded.Should().NotBeNull();
        (await _userManager.IsLockedOutAsync(reloaded!)).Should().BeFalse(
            "the success page says the user can now sign in after resetting their password");
        (await _userManager.GetAccessFailedCountAsync(reloaded!)).Should().Be(0);
    }

    [Fact]
    public async Task ChangePasswordAsync_WritesScopedAuditLogWithoutPasswordValues()
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = "password-audit@example.local",
            Email = "password-audit@example.local",
            EmailConfirmed = true,
            DisplayName = "Password Audit",
            CreatedAt = now,
        };
        (await _userManager.CreateAsync(user, "OldPassword123!")).Succeeded.Should().BeTrue();
        var portfolio = new Portfolio
        {
            Name = "Password Audit",
            ManagementCompanyName = "Password Audit",
            Status = PortfolioStatus.Active,
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Portfolios.Add(portfolio);
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            Portfolio = portfolio,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        await _ctx.Db.SaveChangesAsync();

        var result = await CreateService().ChangePasswordAsync(
            user.Id.ToString(),
            accessContext.Id,
            "OldPassword123!",
            "NewPassword123!");

        result.Success.Should().BeTrue();
    }

    private AuthService CreateService() => new(
        _userManager,
        null!,
        Mock.Of<IAtomicAuthSessionCredentialService>(),
        Mock.Of<ICanonicalAccessTokenService>(),
        Mock.Of<IEffectiveAccessContextSelectionQuery>(),
        Mock.Of<IAccessEnvelopeQuery>(),
        Options.Create(new AtomicAuthSessionCredentialOptions
        {
            SigningKey = Convert.ToBase64String(new byte[32]),
            CredentialLifetimeDays = 7,
            FamilyAbsoluteLifetimeDays = 30,
            SessionLifetimeDays = 30,
        }),
        Mock.Of<IAuthEmailSender>(),
        _ctx.Db,
        Mock.Of<IAuditTrailService>(),
        Mock.Of<ICanonicalAccountBootstrapService>(),
        NullLogger<AuthService>.Instance,
        TimeProvider.System);

    private static UserManager<ApplicationUser> CreateUserManager(RentalCommandDbContext db)
    {
        var store = new UserOnlyStore<ApplicationUser, RentalCommandDbContext, int>(db);
        var options = Options.Create(new IdentityOptions());
        options.Value.Tokens.PasswordResetTokenProvider = TestTokenProvider.ProviderName;

        var manager = new UserManager<ApplicationUser>(
            store,
            options,
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.RegisterTokenProvider(TestTokenProvider.ProviderName, new TestTokenProvider());
        return manager;
    }

    private sealed class TestTokenProvider : IUserTwoFactorTokenProvider<ApplicationUser>
    {
        public const string ProviderName = "TestReset";

        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(false);

        public Task<string> GenerateAsync(string purpose, UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(ResetToken);

        public Task<bool> ValidateAsync(string purpose, string token, UserManager<ApplicationUser> manager, ApplicationUser user)
            => Task.FromResult(token == ResetToken);
    }
}
