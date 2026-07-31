using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using RentalCommand.Api.Data;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class CanonicalRegistrationBootstrapTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private UserManager<ApplicationUser> _users = null!;
    private ServiceProvider _services = null!;
    private IAtomicUnitOfWork _atomic = null!;

    public CanonicalRegistrationBootstrapTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _services = AtomicDomainTestKernel.CreateForAccountBootstrapPostgreSql(
            (NpgsqlConnection)_ctx.Db.Database.GetDbConnection());
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
        _users = CreateUserManager(_ctx.Db);
    }

    [Fact]
    public async Task Register_Verify_Login_ReturnsCanonicalAdministratorEnvelope()
    {
        var sessions = new Mock<IAtomicAuthSessionCredentialService>();
        sessions.Setup(service => service.StartAsync(
                It.IsAny<AtomicAuthSessionStartRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AtomicAuthSessionStartRequest request, CancellationToken _) =>
            {
                var context = _ctx.Db.WorkspaceAccessContexts.AsNoTracking()
                    .Single(row => row.Id == request.SelectedAccessContextId);
                return new AtomicAuthSessionStartOutcome(
                    true, Guid.NewGuid(), request.UserId, context.Id, context.PortfolioId,
                    context.AccessRevision, "refresh-bearer", AtomicCommandDisposition.Executed);
            });
        var envelopes = new Mock<IAccessEnvelopeQuery>();
        envelopes.Setup(query => query.GetAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid sessionId, int userId, int contextId, long accessRevision,
                DateTime effectiveAtUtc, CancellationToken cancellationToken) =>
            {
                var graph = _ctx.Db.WorkspaceAccessContexts.AsNoTracking()
                    .Where(context => context.Id == contextId && context.UserId == userId)
                    .Select(context => new
                    {
                        Context = context,
                        MembershipId = context.Membership!.Id,
                        WorkspaceName = context.Portfolio!.Name,
                    })
                    .Single();
                var assignment = _ctx.Db.MembershipRoleAssignments.AsNoTracking()
                    .Where(row => row.WorkspaceMembershipId == graph.MembershipId)
                    .Select(row => new
                    {
                        row.Id,
                        row.RoleProfile!.Key,
                        row.RoleProfile.DisplayName,
                        row.Status,
                        row.ScopeKind,
                    })
                    .Single();
                return new AccessEnvelope(
                    new AccessIdentitySummary(userId, "Sole Landlord", "sole@example.test"),
                    new SelectedAccessContextSummary(
                        contextId, graph.Context.PortfolioId, graph.WorkspaceName,
                        graph.Context.AccessRevision, WorkspaceExperience.Management),
                    WorkspaceExperience.Management,
                    [WorkspaceExperience.Management],
                    [new AssignmentSummary(
                        assignment.Id,
                        assignment.Key,
                        assignment.DisplayName,
                        assignment.Status,
                        new AssignmentScopeSummary(assignment.ScopeKind, 0, []))],
                    []);
            });
        var tokens = new Mock<ICanonicalAccessTokenService>();
        tokens.Setup(service => service.Issue(It.IsAny<CanonicalAccessCoordinates>()))
            .Returns(("canonical-access", DateTime.UtcNow.AddMinutes(15)));
        var auth = CreateService(sessions.Object, envelopes.Object, tokens.Object);

        var registered = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RegisterAsync(new RegisterRequest
            {
                Email = "sole@example.test",
                Password = "Password123!",
                DisplayName = "Sole Landlord",
            }, "test-register"));

        registered.Success.Should().BeTrue();
        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        (await _users.ConfirmEmailAsync(user!, registered.EmailConfirmationToken!)).Succeeded
            .Should().BeTrue();
        var context = await _ctx.Db.WorkspaceAccessContexts
            .Include(row => row.Membership!)
            .ThenInclude(membership => membership.RoleAssignments)
            .SingleAsync(row => row.UserId == user!.Id);
        context.Membership!.RoleAssignments.Should().ContainSingle(assignment =>
            assignment.RoleProfileId == AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id &&
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        var owner = await _ctx.Db.OwnerEntities.SingleAsync(owner =>
            owner.PortfolioId == context.PortfolioId && owner.IsPrimary);
        (await _ctx.Db.OwnerUserAccesses.SingleAsync(access =>
            access.AccessContextId == context.Id && access.ApplicationUserId == user.Id))
            .OwnerEntityId.Should().Be(owner.Id);
        var suppliedTemplates = await _ctx.Db.WorkspaceNoticeTemplateVersions
            .Where(template => template.PortfolioId == context.PortfolioId)
            .OrderBy(template => template.SystemKey)
            .ToListAsync();
        suppliedTemplates.Should().HaveCount(5);
        suppliedTemplates.Should().OnlyContain(template =>
            template.BasedOnSystemTemplateVersionId > 0 &&
            template.CreatedByUserId == user.Id);
        suppliedTemplates.Single(template => template.SystemKey == "lease-non-renewal")
            .Version.Should().Be(3);
        suppliedTemplates.Single(template => template.SystemKey == "late-rent-late-fee")
            .Version.Should().Be(3);
        suppliedTemplates.Where(template =>
                template.SystemKey != "lease-non-renewal" &&
                template.SystemKey != "late-rent-late-fee")
            .Should().OnlyContain(template => template.Version == 1);

        var policies = await _ctx.Db.TenantNoticePolicies.AsNoTracking()
            .Where(policy => policy.PortfolioId == context.PortfolioId)
            .OrderBy(policy => policy.AutomationKey)
            .ToListAsync();
        policies.Should().HaveCount(5);
        policies.Should().OnlyContain(policy => policy.Mode == TenantNoticeMode.Draft);
        policies.Should().OnlyContain(policy => suppliedTemplates.Any(template =>
            template.Id == policy.WorkspaceNoticeTemplateVersionId &&
            template.SystemKey == policy.AutomationKey));

        var bootstrapAudits = await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .Where(audit => audit.PortfolioId == context.PortfolioId &&
                audit.ActorLabel == "authentication:registration")
            .ToListAsync();
        bootstrapAudits.Count(audit =>
                audit.EntityType == nameof(WorkspaceNoticeTemplateVersion) &&
                audit.ChangeReason == "Fresh workspace supplied legal template replaced with latest immutable version")
            .Should().Be(2);
        bootstrapAudits.Count(audit =>
                audit.EntityType == nameof(TenantNoticePolicy) &&
                audit.ChangeReason == "Fresh workspace Draft policy rebound to latest supplied legal template")
            .Should().Be(2);

        var preLoginOptions = await ExecuteAsApiDatabaseIdentityAsync(() =>
            new EffectiveAccessContextSelectionQuery(_ctx.Db)
                .ListAsync(user.Id, null));
        preLoginOptions.Should().ContainSingle(option =>
            option.AccessContextId == context.Id &&
            option.AccessRevision == context.AccessRevision);

        var loggedIn = await auth.LoginAsync(user.Email!, "Password123!");

        loggedIn.Success.Should().BeTrue();
        loggedIn.Response!.Access.SelectedContext.AccessContextId.Should().Be(context.Id);
        loggedIn.Response.Access.Assignments.Should().ContainSingle(assignment =>
            assignment.RoleProfileKey == RoleProfileKeys.WorkspaceAdministrator &&
            assignment.Scope.Kind == MembershipRoleAssignmentScopeKind.AllProperties);
        loggedIn.Tokens!.AccessToken.Should().Be("canonical-access");
    }

    [Fact]
    public async Task Logout_RevokesSessionAndRefreshCredentials_AfterRefreshRotation()
    {
        var credentials = CreateAtomicCredentials();
        var auth = CreateService(credentials, new AccessEnvelopeQuery(_ctx.Db), CreateCanonicalTokens());
        var email = $"logout-{Guid.NewGuid():N}@example.test";

        var registered = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = "Password123!",
                DisplayName = "Logout Regression",
            }, $"logout-register-{Guid.NewGuid():N}"));
        registered.Success.Should().BeTrue();
        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        (await _users.ConfirmEmailAsync(user!, registered.EmailConfirmationToken!)).Succeeded
            .Should().BeTrue();

        var loggedIn = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.LoginAsync(email, "Password123!"));
        loggedIn.Success.Should().BeTrue();
        loggedIn.Tokens!.RefreshToken.Should().NotBeNullOrWhiteSpace();

        var refreshed = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RefreshAsync(loggedIn.Tokens.RefreshToken));
        refreshed.Success.Should().BeTrue();
        refreshed.Tokens!.RefreshToken.Should().NotBe(loggedIn.Tokens.RefreshToken);

        var session = await _ctx.Db.AuthSessions
            .AsNoTracking()
            .SingleAsync(row => row.UserId == user!.Id);
        var accessContext = await _ctx.Db.WorkspaceAccessContexts
            .AsNoTracking()
            .SingleAsync(row => row.Id == session.ActiveAccessContextId);

        var revoked = await ExecuteAsApiDatabaseIdentityAsync(
            session.Id,
            user!.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            () =>
            credentials.RevokeSessionAsync(
                new RevokeAuthSessionCommand(
                    session.Id,
                    user.Id,
                    accessContext.Id,
                    accessContext.AccessRevision,
                    DateTime.UtcNow,
                    "User signed out"),
                Guid.NewGuid()));

        revoked.Revoked.Should().BeTrue();
        (await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RefreshAsync(refreshed.Tokens.RefreshToken))).Success.Should().BeFalse();

        var revokedSession = await _ctx.Db.AuthSessions
            .Include(row => row.RefreshTokenFamilies)
            .ThenInclude(row => row.Credentials)
            .SingleAsync(row => row.Id == session.Id);
        revokedSession.Status.Should().Be(AuthSessionStatus.Revoked);
        revokedSession.RefreshTokenFamilies.Should().OnlyContain(row => row.RevokedAtUtc != null);
        revokedSession.RefreshTokenFamilies.SelectMany(row => row.Credentials)
            .Should().OnlyContain(row => row.RevokedAtUtc != null);
        (await _ctx.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == "auth-session:revoke" &&
            row.EntityType == nameof(AuthSession) &&
            row.ActorLabel == "authentication:logout" &&
            row.UserId == user!.Id)).Should().Be(1);
    }

    [Fact]
    public async Task IdentitySeed_FirstCleanStartup_CreatesConfiguredWorkspaceOnce()
    {
        await _ctx.Db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"Portfolios\", \"AspNetUsers\" RESTART IDENTITY CASCADE;");
        var settings = new SeedSettings
        {
            Enabled = true,
            AdminEmail = "admin@seed.test",
            AdminPassword = "Admin123!",
            AdminDisplayName = "Seed Administrator",
            PortfolioName = "Configured Portfolio",
            ManagementCompanyName = "Configured Management Company",
        };
        var seeder = new IdentitySeeder(
            _users,
            Options.Create(settings),
            new CanonicalAccountBootstrapService(
                _users,
                _atomic,
                Options.Create(new AtomicAuthSessionCredentialOptions
                {
                    SigningKey = Convert.ToBase64String(new byte[32]),
                })),
            NullLogger<IdentitySeeder>.Instance);

        await ExecuteAsApiDatabaseIdentityAsync(async () =>
        {
            await seeder.SeedAsync();
            return true;
        });
        await ExecuteAsApiDatabaseIdentityAsync(async () =>
        {
            await seeder.SeedAsync();
            return true;
        });

        (await _ctx.Db.Users.AsNoTracking().CountAsync()).Should().Be(1);
        var workspace = await _ctx.Db.Portfolios.AsNoTracking()
            .Select(portfolio => new
            {
                portfolio.Name,
                portfolio.ManagementCompanyName,
            })
            .SingleAsync();
        workspace.Name.Should().Be(settings.PortfolioName);
        workspace.ManagementCompanyName.Should().Be(settings.ManagementCompanyName);
    }

    private async Task<T> ExecuteAsApiDatabaseIdentityAsync<T>(Func<Task<T>> action)
    {
        await _ctx.Db.Database.OpenConnectionAsync();
        try
        {
            await _ctx.Db.Database.ExecuteSqlRawAsync(
                "SET SESSION AUTHORIZATION rentalcommand_api;");
            return await action();
        }
        finally
        {
            await _ctx.Db.Database.ExecuteSqlRawAsync("RESET SESSION AUTHORIZATION;");
            await _ctx.Db.Database.CloseConnectionAsync();
        }
    }

    private async Task<T> ExecuteAsApiDatabaseIdentityAsync<T>(
        Guid authSessionId,
        int userId,
        int accessContextId,
        long accessRevision,
        Func<Task<T>> action)
    {
        await _ctx.Db.Database.OpenConnectionAsync();
        try
        {
            await _ctx.Db.Database.ExecuteSqlRawAsync(
                "SET SESSION AUTHORIZATION rentalcommand_api;");
            await _ctx.Db.Database.ExecuteSqlRawAsync(
                $"""
                SET app.auth_session_id = '{authSessionId:D}';
                SET app.current_user_id = '{userId}';
                SET app.current_access_context_id = '{accessContextId}';
                SET app.access_revision = '{accessRevision}';
                """);
            return await action();
        }
        finally
        {
            await _ctx.Db.Database.ExecuteSqlRawAsync(
                """
                RESET app.access_revision;
                RESET app.current_access_context_id;
                RESET app.current_user_id;
                RESET app.auth_session_id;
                RESET SESSION AUTHORIZATION;
                """);
            await _ctx.Db.Database.CloseConnectionAsync();
        }
    }

    private AuthService CreateService(
        IAtomicAuthSessionCredentialService sessions,
        IAccessEnvelopeQuery envelopes,
        ICanonicalAccessTokenService canonicalTokens)
    {
        return new AuthService(
            _users,
            CreateSignInManager(_users),
            sessions,
            canonicalTokens,
            new EffectiveAccessContextSelectionQuery(_ctx.Db),
            envelopes,
            Options.Create(new AtomicAuthSessionCredentialOptions
            {
                SigningKey = Convert.ToBase64String(new byte[32]),
                CredentialLifetimeDays = 7,
                FamilyAbsoluteLifetimeDays = 30,
                SessionLifetimeDays = 30,
            }),
            Mock.Of<IAuthEmailSender>(),
            new CanonicalAccountBootstrapService(
                _users,
                _atomic,
                Options.Create(new AtomicAuthSessionCredentialOptions
                {
                    SigningKey = Convert.ToBase64String(new byte[32]),
                })),
            _atomic,
            NullLogger<AuthService>.Instance,
            new SystemAuthSecurityClock());
    }

    private IAtomicAuthSessionCredentialService CreateAtomicCredentials()
    {
        var signingKey = Convert.ToBase64String(
            Enumerable.Range(1, RefreshCredentialTokenFactory.MinimumSigningKeyBytes)
                .Select(value => (byte)value)
                .ToArray());
        return new AtomicAuthSessionCredentialService(
            _atomic,
            new RefreshCredentialTokenFactory(signingKey),
            Options.Create(new AtomicAuthSessionCredentialOptions
            {
                SigningKey = signingKey,
                CredentialLifetimeDays = 7,
                FamilyAbsoluteLifetimeDays = 30,
                SessionLifetimeDays = 30,
            }),
            new SystemAuthSecurityClock());
    }

    private static ICanonicalAccessTokenService CreateCanonicalTokens() =>
        new CanonicalAccessTokenService(
            Options.Create(new JwtSettings
            {
                SecretKey = "dev_only_super_secret_signing_key_at_least_64_chars_long_0123456789",
                Issuer = "RentalCommand",
                Audience = "RentalCommandWeb",
                AccessTokenExpirationMinutes = 15,
            }),
            new SystemAuthSecurityClock());

    private static SignInManager<ApplicationUser> CreateSignInManager(
        UserManager<ApplicationUser> userManager) => new(
        userManager,
        Mock.Of<IHttpContextAccessor>(),
        Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
        Options.Create(new IdentityOptions()),
        NullLogger<SignInManager<ApplicationUser>>.Instance,
        Mock.Of<IAuthenticationSchemeProvider>(),
        Mock.Of<IUserConfirmation<ApplicationUser>>());

    private static UserManager<ApplicationUser> CreateUserManager(RentalCommand.Data.RentalCommandDbContext db)
    {
        var store = new UserOnlyStore<ApplicationUser, RentalCommand.Data.RentalCommandDbContext, int>(db);
        var manager = new UserManager<ApplicationUser>(
            store,
            Options.Create(new IdentityOptions { User = { RequireUniqueEmail = true } }),
            new PasswordHasher<ApplicationUser>(),
            [new UserValidator<ApplicationUser>()],
            [new PasswordValidator<ApplicationUser>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.RegisterTokenProvider(
            TokenOptions.DefaultProvider,
            new EmailTokenProvider<ApplicationUser>());
        manager.RegisterTokenProvider(
            TokenOptions.DefaultEmailProvider,
            new EmailTokenProvider<ApplicationUser>());
        return manager;
    }

    public async Task DisposeAsync()
    {
        _users.Dispose();
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }
}
