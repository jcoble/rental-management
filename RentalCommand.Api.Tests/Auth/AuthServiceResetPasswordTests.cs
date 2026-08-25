using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Data;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

[Collection(MigratedPostgreSqlCollection.Name1)]
public sealed class AuthServiceResetPasswordTests : IAsyncLifetime
{
    private const string ResetToken = "reset-token";
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private UserManager<ApplicationUser> _userManager = null!;
    private ServiceProvider _services = null!;

    public AuthServiceResetPasswordTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _userManager = CreateUserManager(_ctx.Db);
        _services = AtomicDomainTestKernel.CreateForPasswordResetPostgreSql(_ctx.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _userManager.Dispose();
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
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
        SeedWorkspaceAuthority(user, DateTime.UtcNow);

        var result = await CreateService(
            _services.GetRequiredService<IWriteExecutor>(),
            _services.GetRequiredService<RentalCommandDbContext>()).ResetPasswordAsync(
            user.Id.ToString(), ResetToken, "NewPassword123!", "test-password-reset");

        result.Success.Should().BeTrue();
        var reloaded = await _userManager.FindByIdAsync(user.Id.ToString());
        reloaded.Should().NotBeNull();
        (await _userManager.IsLockedOutAsync(reloaded!)).Should().BeFalse(
            "the success page says the user can now sign in after resetting their password");
        (await _userManager.GetAccessFailedCountAsync(reloaded!)).Should().Be(0);
    }

    private void SeedWorkspaceAuthority(ApplicationUser user, DateTime now)
    {
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        membership.RoleAssignments.Add(new MembershipRoleAssignment
        {
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        _ctx.Db.WorkspaceMemberships.Add(membership);
        _ctx.Db.SaveChanges();
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

        var result = await CreateService(new SuccessfulPasswordWriteExecutor()).ChangePasswordAsync(
            new ActiveAccessContext(
                Guid.NewGuid(), user.Id, accessContext.Id, portfolio.Id, accessContext.AccessRevision,
                null, null, null),
            "OldPassword123!",
            "NewPassword123!",
            "test-password-change");

        result.Success.Should().BeTrue();
    }

    private AuthService CreateService(
        IWriteExecutor? writes = null,
        RentalCommandDbContext? writeDb = null) => new(
        writeDb ?? _ctx.Db,
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
        Mock.Of<ICanonicalAccountBootstrapService>(),
        writes ?? Mock.Of<IWriteExecutor>(),
        NullLogger<AuthService>.Instance,
        new SystemAuthSecurityClock());

    private sealed class SuccessfulPasswordWriteExecutor : IWriteExecutor
    {
        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            var password = write.Request.Should().BeOfType<ChangePasswordCommand>().Subject;
            var result = new ChangePasswordResult(
                ChangePasswordOutcome.Changed, password.UserId, password.AccessContextId);
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)(object)result,
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()));
        }
    }

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
