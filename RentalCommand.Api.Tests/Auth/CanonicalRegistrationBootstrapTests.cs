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
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public sealed class CanonicalRegistrationBootstrapTests : IDisposable
{
    private readonly SqliteTestContext _sqlite = new();
    private readonly UserManager<ApplicationUser> _users;

    public CanonicalRegistrationBootstrapTests() => _users = CreateUserManager(_sqlite.Db);

    [Fact]
    public async Task Register_Verify_Login_ReturnsCanonicalAdministratorEnvelope()
    {
        var sessions = new Mock<IAtomicAuthSessionCredentialService>();
        sessions.Setup(service => service.StartAsync(
                It.IsAny<AtomicAuthSessionStartRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AtomicAuthSessionStartRequest request, CancellationToken _) =>
            {
                var context = _sqlite.Db.WorkspaceAccessContexts.AsNoTracking()
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
                var graph = _sqlite.Db.WorkspaceAccessContexts.AsNoTracking()
                    .Where(context => context.Id == contextId && context.UserId == userId)
                    .Select(context => new
                    {
                        Context = context,
                        MembershipId = context.Membership!.Id,
                        WorkspaceName = context.Portfolio!.Name,
                    })
                    .Single();
                var assignment = _sqlite.Db.MembershipRoleAssignments.AsNoTracking()
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

        var registered = await auth.RegisterAsync(new RegisterRequest
        {
            Email = "sole@example.test",
            Password = "Password123!",
            DisplayName = "Sole Landlord",
        });

        registered.Success.Should().BeTrue();
        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        (await _users.ConfirmEmailAsync(user!, registered.EmailConfirmationToken!)).Succeeded
            .Should().BeTrue();
        var context = await _sqlite.Db.WorkspaceAccessContexts
            .Include(row => row.Membership!)
            .ThenInclude(membership => membership.RoleAssignments)
            .SingleAsync(row => row.UserId == user!.Id);
        context.Membership!.RoleAssignments.Should().ContainSingle(assignment =>
            assignment.RoleProfileId == AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id &&
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        (await _sqlite.Db.OwnerEntities.SingleAsync(owner =>
            owner.PortfolioId == context.PortfolioId && owner.IsPrimary)).Id
            .Should().Be(user.OwnerEntityId);
        (await _users.GetRolesAsync(user)).Should().BeEmpty(
            "fresh registration must not recreate the removed Identity Admin role");
        (await _sqlite.Db.UserAccounts.CountAsync(account => account.Email == user.Email)).Should().Be(0);

        var loggedIn = await auth.LoginAsync(user.Email!, "Password123!");

        loggedIn.Success.Should().BeTrue();
        loggedIn.Response!.Access.SelectedContext.AccessContextId.Should().Be(context.Id);
        loggedIn.Response.Access.Assignments.Should().ContainSingle(assignment =>
            assignment.RoleProfileKey == RoleProfileKeys.WorkspaceAdministrator &&
            assignment.Scope.Kind == MembershipRoleAssignmentScopeKind.AllProperties);
        loggedIn.Tokens!.AccessToken.Should().Be("canonical-access");
    }

    private AuthService CreateService(
        IAtomicAuthSessionCredentialService sessions,
        IAccessEnvelopeQuery envelopes,
        ICanonicalAccessTokenService canonicalTokens)
    {
        var migration = new Mock<IUserMigrationService>();
        migration.Setup(service => service.RequiresPasswordResetAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(false);
        return new AuthService(
            _users,
            CreateSignInManager(_users),
            sessions,
            canonicalTokens,
            new EffectiveAccessContextSelectionQuery(_sqlite.Db),
            envelopes,
            Options.Create(new AtomicAuthSessionCredentialOptions
            {
                SigningKey = Convert.ToBase64String(new byte[32]),
                CredentialLifetimeDays = 7,
                FamilyAbsoluteLifetimeDays = 30,
                SessionLifetimeDays = 30,
            }),
            migration.Object,
            Mock.Of<IAuthEmailSender>(),
            _sqlite.Db,
            Mock.Of<IAuditTrailService>(),
            new CanonicalAccountBootstrapService(
                _users,
                _sqlite.Db,
                new RlsExecutionContext(),
                TimeProvider.System),
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
        var store = new UserStore<ApplicationUser, IdentityRole<int>, RentalCommand.Data.RentalCommandDbContext, int>(db);
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

    public void Dispose()
    {
        _users.Dispose();
        _sqlite.Dispose();
    }
}
