using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Data;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Atomic;
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

    public CanonicalRegistrationBootstrapTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int userId, int contextId, CancellationToken _) =>
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
            .ToListAsync();
        suppliedTemplates.Should().HaveCount(5);
        suppliedTemplates.Should().OnlyContain(template =>
            template.Version == 1 && template.BasedOnSystemTemplateVersionId > 0 &&
            template.CreatedByUserId == user.Id);

        var preLoginOptions = await ExecuteAsApiDatabaseIdentityAsync(() =>
            new EffectiveAccessContextSelectionQuery(_ctx.Db)
                .ListAsync(user.Id, DateTime.UtcNow));
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
                Mock.Of<IAtomicUnitOfWork>(),
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
                Mock.Of<IAtomicUnitOfWork>(),
                Options.Create(new AtomicAuthSessionCredentialOptions
                {
                    SigningKey = Convert.ToBase64String(new byte[32]),
                })),
            Mock.Of<RentalCommand.Core.Atomic.IAtomicUnitOfWork>(),
            NullLogger<AuthService>.Instance,
            TimeProvider.System);
    }

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
        await _ctx.DisposeAsync();
    }
}
