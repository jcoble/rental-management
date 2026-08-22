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
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
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
    private IRequestWriteExecutor _writes = null!;
    private RentalCommandDbContext _writeDb = null!;

    public CanonicalRegistrationBootstrapTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _services = AtomicDomainTestKernel.CreateForAccountBootstrapPostgreSql(
            (NpgsqlConnection)_ctx.Db.Database.GetDbConnection());
        _writes = _services.GetRequiredService<IRequestWriteExecutor>();
        _writeDb = _services.GetRequiredService<RentalCommandDbContext>();
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
                TermsPrivacyAccepted = true,
            }, "test-register"));

        registered.Success.Should().BeTrue();
        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        user!.TermsPrivacyAccepted.Should().BeTrue();
        user.TermsPrivacyAcceptedAtUtc.Should().NotBeNull();
        user.TermsPrivacyVersion.Should().Be(ApplicationUser.RegistrationTermsPrivacyVersion);
        (await _users.ConfirmEmailAsync(user, registered.EmailConfirmationToken!)).Succeeded
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
    public async Task Register_RequiresTermsPrivacyConsent()
    {
        var email = $"consent-required-{Guid.NewGuid():N}@example.test";
        var auth = CreateService(
            CreateAtomicCredentials(),
            new AccessEnvelopeQuery(_ctx.Db),
            CreateCanonicalTokens());

        var result = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = "Password123!",
                DisplayName = "Consent Required",
            }, $"consent-required-{Guid.NewGuid():N}"));

        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(AuthErrorType.BadRequest);
        result.ValidationErrors.Should().ContainSingle(
            "You must agree to the Terms of Service and Privacy Policy.");
        (await _ctx.Db.Users.AnyAsync(user => user.NormalizedEmail == email.ToUpperInvariant()))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Register_EnablesLockout_AndLocksAfterFifthFailureUntilExpiry()
    {
        var email = $"lockout-{Guid.NewGuid():N}@example.test";
        var auth = CreateService(
            CreateAtomicCredentials(),
            new AccessEnvelopeQuery(_ctx.Db),
            CreateCanonicalTokens());

        var registered = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = "Password123!",
                DisplayName = "Lockout Regression",
                TermsPrivacyAccepted = true,
            }, $"lockout-register-{Guid.NewGuid():N}"));

        registered.Success.Should().BeTrue();
        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        user!.LockoutEnabled.Should().BeTrue();
        (await _users.ConfirmEmailAsync(user, registered.EmailConfirmationToken!)).Succeeded
            .Should().BeTrue();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failed = await ExecuteAsApiDatabaseIdentityAsync(() =>
                auth.LoginAsync(email, "WrongPassword123!"));

            failed.Success.Should().BeFalse();
            failed.Error.Should().Be("Invalid email or password");
        }

        var fifth = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.LoginAsync(email, "WrongPassword123!"));
        fifth.Success.Should().BeFalse();
        fifth.Error.Should().Be("Account is locked. Try again later.");

        var sixth = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.LoginAsync(email, "WrongPassword123!"));
        sixth.Success.Should().BeFalse();
        sixth.Error.Should().Be("Account is locked. Try again later.");

        var lockedUser = await _users.FindByIdAsync(user.Id.ToString());
        lockedUser!.LockoutEnd.Should().NotBeNull();
        (await _users.SetLockoutEndDateAsync(
            lockedUser,
            DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded.Should().BeTrue();

        var afterExpiry = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.LoginAsync(email, "Password123!"));
        afterExpiry.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmEmail_InvalidTokenForConfirmedAndUnknownUsersIsNeutral()
    {
        var email = $"verification-{Guid.NewGuid():N}@example.test";
        var auth = CreateService(
            new Mock<IAtomicAuthSessionCredentialService>().Object,
            new AccessEnvelopeQuery(_ctx.Db),
            CreateCanonicalTokens());

        var registered = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = "Password123!",
                DisplayName = "Verification Regression",
                TermsPrivacyAccepted = true,
            }, $"verification-register-{Guid.NewGuid():N}"));

        registered.Success.Should().BeTrue();
        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        user!.TermsPrivacyAccepted.Should().BeTrue();
        user.TermsPrivacyAcceptedAtUtc.Should().NotBeNull();
        user.TermsPrivacyVersion.Should().Be(ApplicationUser.RegistrationTermsPrivacyVersion);
        (await _users.ConfirmEmailAsync(user, registered.EmailConfirmationToken!)).Succeeded
            .Should().BeTrue();

        var confirmedUserWithBogusToken = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.ConfirmEmailAsync(
                user!.Id.ToString(),
                "bogus-verification-token",
                $"verification-invalid-confirmed-{Guid.NewGuid():N}"));
        var unknownUserWithBogusToken = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.ConfirmEmailAsync(
                "999999",
                "bogus-verification-token",
                $"verification-invalid-unknown-{Guid.NewGuid():N}"));

        confirmedUserWithBogusToken.Success.Should().BeFalse();
        unknownUserWithBogusToken.Success.Should().BeFalse();
        confirmedUserWithBogusToken.Error.Should().Be(unknownUserWithBogusToken.Error);
        confirmedUserWithBogusToken.ErrorType.Should().Be(unknownUserWithBogusToken.ErrorType);
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
                TermsPrivacyAccepted = true,
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
        var operationId = Guid.NewGuid();

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
                operationId));

        revoked.Revoked.Should().BeTrue();
        (await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RefreshAsync(refreshed.Tokens.RefreshToken))).Success.Should().BeFalse();

        var revokedSession = await _ctx.Db.AuthSessions
            .AsNoTracking()
            .Include(row => row.RefreshTokenFamilies)
            .ThenInclude(row => row.Credentials)
            .SingleAsync(row => row.Id == session.Id);
        revokedSession.Status.Should().Be(AuthSessionStatus.Revoked);
        revokedSession.RefreshTokenFamilies.Should().OnlyContain(row => row.RevokedAtUtc != null);
        revokedSession.RefreshTokenFamilies.SelectMany(row => row.Credentials)
            .Should().OnlyContain(row => row.RevokedAtUtc != null);
        var credentialsBeforeReplay = await _ctx.Db.AuthSessionRefreshCredentials
            .AsNoTracking()
            .Where(row => row.RefreshTokenFamily!.AuthSessionId == session.Id)
            .OrderBy(row => row.Id)
            .ToListAsync();
        var sessionBeforeReplay = await _ctx.Db.AuthSessions
            .AsNoTracking()
            .SingleAsync(row => row.Id == session.Id);

        Func<Task> replay = async () =>
            await ExecuteAsApiDatabaseIdentityAsync(
                session.Id,
                user.Id,
                accessContext.Id,
                accessContext.AccessRevision,
                () => credentials.RevokeSessionAsync(
                    new RevokeAuthSessionCommand(
                        session.Id,
                        user.Id,
                        accessContext.Id,
                        accessContext.AccessRevision,
                        DateTime.UtcNow.AddMinutes(1),
                        "User signed out"),
                    operationId));

        await replay.Should().ThrowAsync<UnauthorizedAccessException>();
        var credentialsAfterReplay = await _ctx.Db.AuthSessionRefreshCredentials
            .AsNoTracking()
            .Where(row => row.RefreshTokenFamily!.AuthSessionId == session.Id)
            .OrderBy(row => row.Id)
            .ToListAsync();
        credentialsAfterReplay.Should().BeEquivalentTo(credentialsBeforeReplay);
        var sessionAfterReplay = await _ctx.Db.AuthSessions
            .AsNoTracking()
            .SingleAsync(row => row.Id == session.Id);
        sessionAfterReplay.Status.Should().Be(sessionBeforeReplay.Status);
        sessionAfterReplay.RevokedAtUtc.Should().Be(sessionBeforeReplay.RevokedAtUtc);
        sessionAfterReplay.RevocationReason.Should().Be(sessionBeforeReplay.RevocationReason);
        (await _ctx.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == "auth-session:revoke" &&
            row.EntityType == nameof(AuthSession) &&
            row.ActorLabel == "authentication:logout" &&
            row.UserId == user!.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RevokeAuthority_RejectsNonApiIdentityMismatchedCoordinatesAndCrossPortfolio()
    {
        var scenario = await CreateRevokeAuthorityScenarioAsync();

        var nonApi = await ExecuteNonApiRevokeAuthorityProbeAsync(scenario);
        nonApi.SessionUser.Should().NotBe("rentalcommand_api");
        nonApi.Allows.Should().BeFalse();

        var rejected = await ExecuteMismatchedRevokeAuthorityProbesAsync(scenario);
        rejected.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["session"] = false,
            ["user"] = false,
            ["context"] = false,
            ["access-revision"] = false,
            ["portfolio"] = false,
        });
        (await ExecuteCrossPortfolioAuditProbeAsync(scenario)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAuthority_RejectsPreExistingReceiptAndSessionRows()
    {
        var scenario = await CreateRevokeAuthorityScenarioAsync();

        (await ExecutePreExistingReceiptRevokeAuthorityProbeAsync(scenario)).Should().BeFalse();
        (await ExecutePreExistingSessionRevokeAuthorityProbeAsync(scenario)).Should().BeFalse();
    }

    private async Task<RevokeAuthorityScenario> CreateRevokeAuthorityScenarioAsync()
    {
        var credentials = CreateAtomicCredentials();
        var auth = CreateService(credentials, new AccessEnvelopeQuery(_ctx.Db), CreateCanonicalTokens());
        var email = $"revoke-authority-{Guid.NewGuid():N}@example.test";
        var registered = await ExecuteAsApiDatabaseIdentityAsync(() =>
            auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = "Password123!",
                DisplayName = "Revoke Authority Regression",
                TermsPrivacyAccepted = true,
            }, $"revoke-authority-register-{Guid.NewGuid():N}"));
        registered.Success.Should().BeTrue();

        var user = await _users.FindByIdAsync(registered.UserId!.Value.ToString());
        user.Should().NotBeNull();
        (await _users.ConfirmEmailAsync(user!, registered.EmailConfirmationToken!)).Succeeded
            .Should().BeTrue();
        (await ExecuteAsApiDatabaseIdentityAsync(() => auth.LoginAsync(email, "Password123!")))
            .Success.Should().BeTrue();

        var session = await _ctx.Db.AuthSessions
            .AsNoTracking()
            .SingleAsync(row => row.UserId == user!.Id);
        var accessContext = await _ctx.Db.WorkspaceAccessContexts
            .AsNoTracking()
            .SingleAsync(row => row.Id == session.ActiveAccessContextId);
        return new RevokeAuthorityScenario(
            session.Id,
            user.Id,
            accessContext.Id,
            accessContext.PortfolioId,
            accessContext.AccessRevision,
            _ctx.ConnectionString);
    }

    private async Task<(string SessionUser, bool Allows)> ExecuteNonApiRevokeAuthorityProbeAsync(
        RevokeAuthorityScenario scenario)
    {
        await using var connection = new NpgsqlConnection(_ctx.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var operation = NewRevokeOperation();
            var sessionUser = await ExecuteScalarAsync<string>(
                connection,
                transaction,
                "SELECT session_user");
            var allows = await ExecuteRevokeAuthorityFunctionAsync(
                connection,
                transaction,
                scenario,
                operation,
                scenario.PortfolioId,
                scenario.UserId,
                scenario.AccessContextId,
                scenario.AccessRevision,
                scenario.SessionId);
            await transaction.RollbackAsync();
            return (sessionUser, allows);
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, null, "RESET SESSION AUTHORIZATION;");
        }
    }

    private async Task<IReadOnlyDictionary<string, bool>> ExecuteMismatchedRevokeAuthorityProbesAsync(
        RevokeAuthorityScenario scenario)
    {
        await using var connection = new NpgsqlConnection(_ctx.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, null, "SET SESSION AUTHORIZATION rentalcommand_api;");
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var operation = NewRevokeOperation();
            await SetRevokeScopeAsync(connection, transaction, scenario, scenario.AccessRevision);
            await InsertReceiptAsync(connection, transaction, operation);
            await RevokeSessionRowAsync(connection, transaction, scenario.SessionId);

            var results = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["session"] = await ExecuteRevokeAuthorityFunctionAsync(
                    connection,
                    transaction,
                    scenario,
                    operation,
                    scenario.PortfolioId,
                    scenario.UserId,
                    scenario.AccessContextId,
                    scenario.AccessRevision,
                    Guid.NewGuid()),
                ["user"] = await ExecuteRevokeAuthorityFunctionAsync(
                    connection,
                    transaction,
                    scenario,
                    operation,
                    scenario.PortfolioId,
                    scenario.UserId + 1000000,
                    scenario.AccessContextId,
                    scenario.AccessRevision,
                    scenario.SessionId),
                ["context"] = await ExecuteRevokeAuthorityFunctionAsync(
                    connection,
                    transaction,
                    scenario,
                    operation,
                    scenario.PortfolioId,
                    scenario.UserId,
                    scenario.AccessContextId + 1000000,
                    scenario.AccessRevision,
                    scenario.SessionId),
            };

            await SetRevokeScopeAsync(connection, transaction, scenario, scenario.AccessRevision + 1);
            results["access-revision"] = await ExecuteRevokeAuthorityFunctionAsync(
                connection,
                transaction,
                scenario,
                operation,
                scenario.PortfolioId,
                scenario.UserId,
                scenario.AccessContextId,
                scenario.AccessRevision + 1,
                scenario.SessionId);

            await SetRevokeScopeAsync(connection, transaction, scenario, scenario.AccessRevision);
            results["portfolio"] = await ExecuteRevokeAuthorityFunctionAsync(
                connection,
                transaction,
                scenario,
                operation,
                scenario.PortfolioId + 1000000,
                scenario.UserId,
                scenario.AccessContextId,
                scenario.AccessRevision,
                scenario.SessionId);
            await transaction.RollbackAsync();
            return results;
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, null, "RESET app.access_revision; RESET app.current_access_context_id; RESET app.current_user_id; RESET app.auth_session_id; RESET SESSION AUTHORIZATION;");
        }
    }

    private async Task<bool> ExecutePreExistingReceiptRevokeAuthorityProbeAsync(
        RevokeAuthorityScenario scenario)
    {
        await using var connection = new NpgsqlConnection(_ctx.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, null, "SET SESSION AUTHORIZATION rentalcommand_api;");
        var operation = NewRevokeOperation();
        try
        {
            await using (var priorTransaction = await connection.BeginTransactionAsync())
            {
                await SetRevokeScopeAsync(connection, priorTransaction, scenario, scenario.AccessRevision);
                await InsertReceiptAsync(connection, priorTransaction, operation);
                await priorTransaction.CommitAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await SetRevokeScopeAsync(connection, transaction, scenario, scenario.AccessRevision);
            await RevokeSessionRowAsync(connection, transaction, scenario.SessionId);
            var allows = await ExecuteRevokeAuthorityFunctionAsync(
                connection,
                transaction,
                scenario,
                operation,
                scenario.PortfolioId,
                scenario.UserId,
                scenario.AccessContextId,
                scenario.AccessRevision,
                scenario.SessionId);
            await transaction.RollbackAsync();
            return allows;
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, null, "RESET app.access_revision; RESET app.current_access_context_id; RESET app.current_user_id; RESET app.auth_session_id; RESET SESSION AUTHORIZATION;");
        }
    }

    private static async Task<bool> ExecuteCrossPortfolioAuditProbeAsync(
        RevokeAuthorityScenario scenario)
    {
        await using var connection = new NpgsqlConnection(scenario.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, null, "SET SESSION AUTHORIZATION rentalcommand_api;");
        var operation = NewRevokeOperation();
        var rejected = false;
        try
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await SetRevokeScopeAsync(connection, transaction, scenario, scenario.AccessRevision);
            await InsertReceiptAsync(connection, transaction, operation);
            await RevokeSessionRowAsync(connection, transaction, scenario.SessionId);
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO "AtomicAuditLogs"
                        ("AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
                         "PortfolioId", "UserId", "ActorLabel", "EntityType", "EntityId", "Operation",
                         "NewValues", "ChangeReason", "Timestamp")
                    VALUES
                        (@attempt_id, 'auth-session:revoke', @idempotency_key, 1,
                         @portfolio_id, @user_id, 'authentication:logout', 'AuthSession',
                         @access_context_id, 1,
                         jsonb_build_object('AuthSessionId', @session_id),
                         'Authentication session revoked', CURRENT_TIMESTAMP)
                    """,
                    new("attempt_id", operation.AttemptId),
                    new("idempotency_key", operation.IdempotencyKey),
                    new("portfolio_id", scenario.PortfolioId + 1000000),
                    new("user_id", scenario.UserId),
                    new("access_context_id", scenario.AccessContextId),
                    new("session_id", scenario.SessionId.ToString("D")));
            }
            catch (PostgresException exception) when (exception.SqlState == "42501")
            {
                rejected = true;
            }

            await transaction.RollbackAsync();
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connection,
                null,
                "RESET app.access_revision; RESET app.current_access_context_id; RESET app.current_user_id; RESET app.auth_session_id; RESET SESSION AUTHORIZATION;");
        }

        var writtenRows = await ExecuteScalarAsync<long>(
            connection,
            null,
            "SELECT COUNT(*) FROM \"AtomicAuditLogs\" WHERE \"AttemptId\" = @attempt_id",
            new NpgsqlParameter("attempt_id", operation.AttemptId));
        return rejected && writtenRows == 0;
    }

    private async Task<bool> ExecutePreExistingSessionRevokeAuthorityProbeAsync(
        RevokeAuthorityScenario scenario)
    {
        await using var connection = new NpgsqlConnection(_ctx.ConnectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, null, "SET SESSION AUTHORIZATION rentalcommand_api;");
        try
        {
            await using (var priorTransaction = await connection.BeginTransactionAsync())
            {
                var priorOperation = NewRevokeOperation();
                await SetRevokeScopeAsync(connection, priorTransaction, scenario, scenario.AccessRevision);
                await InsertReceiptAsync(connection, priorTransaction, priorOperation);
                await RevokeSessionRowAsync(connection, priorTransaction, scenario.SessionId);
                await priorTransaction.CommitAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync();
            var currentOperation = NewRevokeOperation();
            await SetRevokeScopeAsync(connection, transaction, scenario, scenario.AccessRevision);
            await InsertReceiptAsync(connection, transaction, currentOperation);
            var allows = await ExecuteRevokeAuthorityFunctionAsync(
                connection,
                transaction,
                scenario,
                currentOperation,
                scenario.PortfolioId,
                scenario.UserId,
                scenario.AccessContextId,
                scenario.AccessRevision,
                scenario.SessionId);
            await transaction.RollbackAsync();
            return allows;
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, null, "RESET app.access_revision; RESET app.current_access_context_id; RESET app.current_user_id; RESET app.auth_session_id; RESET SESSION AUTHORIZATION;");
        }
    }

    private static async Task SetRevokeScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RevokeAuthorityScenario scenario,
        long accessRevision)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            SELECT set_config('app.auth_session_id', @auth_session_id, false),
                   set_config('app.current_user_id', @current_user_id, false),
                   set_config('app.current_access_context_id', @current_access_context_id, false),
                   set_config('app.access_revision', @access_revision, false)
            """,
            new("auth_session_id", scenario.SessionId.ToString("D")),
            new("current_user_id", scenario.UserId.ToString()),
            new("current_access_context_id", scenario.AccessContextId.ToString()),
            new("access_revision", accessRevision.ToString()));
    }

    private static async Task RevokeSessionRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            UPDATE "AuthSessions"
            SET "Status" = 'Revoked',
                "RevokedAtUtc" = CURRENT_TIMESTAMP,
                "RevocationReason" = 'authority probe'
            WHERE "Id" = @session_id
            """,
            new NpgsqlParameter("session_id", sessionId));
    }

    private static async Task InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RevokeOperation operation)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO "AtomicCommandReceipts"
                ("Id", "AttemptId", "CommandType", "IdempotencyKey", "RequestFingerprint",
                 "Status", "ResultContract", "StartedAt")
            VALUES
                (@id, @attempt_id, 'auth-session:revoke', @idempotency_key, @request_fingerprint,
                 0, 'RevokeAuthSessionResult', CURRENT_TIMESTAMP)
            """,
            new("id", Guid.NewGuid()),
            new("attempt_id", operation.AttemptId),
            new("idempotency_key", operation.IdempotencyKey),
            new("request_fingerprint", new string('a', 64)));
    }

    private static async Task<bool> ExecuteRevokeAuthorityFunctionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RevokeAuthorityScenario scenario,
        RevokeOperation operation,
        int targetPortfolioId,
        int targetUserId,
        int targetAccessContextId,
        long accessRevision,
        Guid targetSessionId)
    {
        await SetRevokeScopeAsync(connection, transaction, scenario, accessRevision);
        return await ExecuteScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT public.rc_pre_auth_account_security_audit_allows(
                @target_portfolio_id,
                @target_attempt_id,
                'auth-session:revoke',
                @target_idempotency_key,
                1,
                @target_user_id,
                'AuthSession',
                @target_access_context_id,
                1,
                'authentication:logout',
                'Authentication session revoked',
                jsonb_build_object('AuthSessionId', @target_session_id))
            """,
            new("target_portfolio_id", targetPortfolioId),
            new("target_attempt_id", operation.AttemptId),
            new("target_idempotency_key", operation.IdempotencyKey),
            new("target_user_id", targetUserId),
            new("target_access_context_id", targetAccessContextId),
            new("target_session_id", targetSessionId.ToString("D")));
    }

    private static RevokeOperation NewRevokeOperation()
    {
        var operationId = Guid.NewGuid();
        return new RevokeOperation(operationId, $"operation:{operationId:N}");
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record RevokeAuthorityScenario(
        Guid SessionId,
        int UserId,
        int AccessContextId,
        int PortfolioId,
        long AccessRevision,
        string ConnectionString);

    private sealed record RevokeOperation(Guid AttemptId, string IdempotencyKey);

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
                _writeDb,
                _users,
                _writes,
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
            _writeDb,
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
                _writeDb,
                _users,
                _writes,
                Options.Create(new AtomicAuthSessionCredentialOptions
                {
                    SigningKey = Convert.ToBase64String(new byte[32]),
                })),
            _writes,
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
            _writeDb,
            _writes,
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
