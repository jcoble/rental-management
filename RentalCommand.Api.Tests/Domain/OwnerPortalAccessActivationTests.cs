using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class OwnerPortalAccessActivationTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 1;
    private static readonly AtomicJsonResultCodec<OwnerRelationshipAccessMutationResult> OwnerAccessCodec =
        new("owner-relationship-access.mutation.v1");
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private OwnerEntityService _sut = null!;
    private WorkspaceReadScope _scope;

    public OwnerPortalAccessActivationTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _services = BuildServices(_ctx.ConnectionString);
        _scope = SeedActor(RoleProfileKeys.WorkspaceAdministrator);
        _sut = Service();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task ActivationLookup_IsOneTranslatedJoinAgainstOwnerEmailUserAndAccessContext()
    {
        var owner = SeedOwner("owner-lookup@example.test");
        var target = SeedTargetUserAndContext("owner-lookup@example.test");
        await _ctx.Db.SaveChangesAsync();

        var sql = _sut.BuildOwnerPortalActivationTargetQuery(
                PortfolioId,
                owner.Id,
                "OWNER-LOOKUP@EXAMPLE.TEST")
            .ToQueryString();

        sql.Should().Contain("\"OwnerEntities\"");
        sql.Should().Contain("\"AspNetUsers\"");
        sql.Should().Contain("\"WorkspaceAccessContexts\"");
        sql.Should().Contain("\"OwnerUserAccesses\"");
        sql.Should().Contain("upper(");
        sql.Count(character => character == ';').Should().BeLessThanOrEqualTo(1);

        var resolved = await _sut.BuildOwnerPortalActivationTargetQuery(
                PortfolioId,
                owner.Id,
                "OWNER-LOOKUP@EXAMPLE.TEST")
            .SingleAsync();
        resolved.TargetAccessContextId.Should().Be(target.Id);
    }

    [Fact]
    public async Task ActivateOwnerPortalAccess_GrantsAccessAuditsAndAdvancesTargetRevision()
    {
        var owner = SeedOwner("owner-success@example.test");
        var target = SeedTargetUserAndContext("owner-success@example.test");
        await _ctx.Db.SaveChangesAsync();

        var result = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-success");

        result.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.Activated);
        result.TargetAccessContextId.Should().Be(target.Id);
        result.OwnerUserAccessId.Should().NotBeNull();
        result.AccessRevision.Should().Be(2);

        var access = await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .SingleAsync(row => row.OwnerEntityId == owner.Id);
        access.AccessContextId.Should().Be(target.Id);
        access.ApplicationUserId.Should().Be(target.UserId);
        access.GrantedByUserId.Should().Be(ActorUserId);
        access.RevokedAtUtc.Should().BeNull();

        var revised = await _ctx.Db.WorkspaceAccessContexts.AsNoTracking()
            .SingleAsync(row => row.Id == target.Id);
        revised.AccessRevision.Should().Be(2);

        (await _ctx.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.EntityType == nameof(OwnerUserAccess) &&
            row.EntityId == access.Id &&
            row.ChangeReason == "Owner portal relationship granted")).Should().Be(1);
        (await _ctx.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.PortfolioId == PortfolioId)).Should().Be(0);
    }

    [Fact]
    public async Task ActivateOwnerPortalAccess_InvitesMissingAccountAndCapturedEmailTokenCompletesActivation()
    {
        var owner = SeedOwner("missing-account@example.test");
        await _ctx.Db.SaveChangesAsync();

        var result = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-missing-account");

        result.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.InvitationPending);
        result.RequiresAccountActivation.Should().BeTrue();
        result.TargetAccessContextId.Should().NotBeNull();
        result.OwnerUserAccessId.Should().NotBeNull();
        result.InvitationExpiresAtUtc.Should().NotBeNull();

        var invitedUser = await _ctx.Db.Users.AsNoTracking()
            .SingleAsync(user => user.NormalizedEmail == "MISSING-ACCOUNT@EXAMPLE.TEST");
        invitedUser.PasswordHash.Should().BeNull();
        invitedUser.EmailConfirmed.Should().BeFalse();

        var context = await _ctx.Db.WorkspaceAccessContexts.AsNoTracking()
            .Include(row => row.Membership)
            .SingleAsync(row => row.Id == result.TargetAccessContextId);
        context.UserId.Should().Be(invitedUser.Id);
        context.LastAuthorizedExperience.Should().Be(WorkspaceExperience.Owner);
        context.AccessRevision.Should().Be(2);
        context.Membership.Should().NotBeNull();
        context.Membership!.DefaultExperience.Should().Be(WorkspaceExperience.Owner);

        var access = await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .SingleAsync(row => row.OwnerEntityId == owner.Id);
        access.AccessContextId.Should().Be(context.Id);
        access.ApplicationUserId.Should().Be(invitedUser.Id);

        var invitation = await _ctx.Db.WorkspaceInvitations.AsNoTracking()
            .SingleAsync(row => row.InvitedUserId == invitedUser.Id);
        invitation.WorkspaceMembershipId.Should().Be(context.Membership.Id);
        invitation.AcceptedAtUtc.Should().BeNull();

        var outbox = await _ctx.Db.OutboxMessages.AsNoTracking()
            .SingleAsync(row => row.IdempotencyKey.StartsWith($"owner-portal-invitation:{PortfolioId}:{owner.Id}:"));
        var token = ExtractActivationToken(outbox.Payload);
        HashInvitationToken(token).Should().Be(invitation.TokenHash);

        _ctx.Db.ChangeTracker.Clear();
        invitation = await _ctx.Db.WorkspaceInvitations.AsNoTracking()
            .SingleAsync(row => row.Id == invitation.Id);
        invitedUser = await _ctx.Db.Users.AsNoTracking()
            .SingleAsync(user => user.Id == invitedUser.Id);
        invitedUser.PasswordHash.Should().BeNull();

        var activation = await ActivateInvitationAsApiAsync(
            invitation.Id,
            invitedUser.Id,
            invitation.TokenHash,
            "hashed-first-password");

        activation.Should().NotBeNull();
        activation!.InvitedUserId.Should().Be(invitedUser.Id);
        activation.PortfolioId.Should().Be(PortfolioId);
        activation.AccessContextId.Should().Be(context.Id);
        var activatedUser = await _ctx.Db.Users.AsNoTracking()
            .SingleAsync(user => user.Id == invitedUser.Id);
        activatedUser.PasswordHash.Should().Be("hashed-first-password");
        activatedUser.EmailConfirmed.Should().BeTrue();
        (await _ctx.Db.WorkspaceInvitations.AsNoTracking()
            .SingleAsync(row => row.Id == invitation.Id)).AcceptedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task DuplicateActivation_ReturnsAlreadyActiveWithoutDuplicateAccess()
    {
        var owner = SeedOwner("owner-duplicate@example.test");
        SeedTargetUserAndContext("owner-duplicate@example.test");
        await _ctx.Db.SaveChangesAsync();

        var first = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-duplicate-1");
        var second = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-duplicate-2");

        first.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.Activated);
        second.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.AlreadyActive);
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .CountAsync(row => row.OwnerEntityId == owner.Id)).Should().Be(1);
    }

    [Fact]
    public async Task DuplicateActivation_ForPendingInvitationDoesNotDuplicateAccountAccessInvitationOrOutbox()
    {
        var owner = SeedOwner("pending-duplicate@example.test");
        await _ctx.Db.SaveChangesAsync();

        var first = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-pending-duplicate-1");
        var second = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-pending-duplicate-2");

        first.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.InvitationPending);
        second.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.InvitationPending);
        (await _ctx.Db.Users.AsNoTracking()
            .CountAsync(user => user.NormalizedEmail == "PENDING-DUPLICATE@EXAMPLE.TEST")).Should().Be(1);
        (await _ctx.Db.WorkspaceAccessContexts.AsNoTracking()
            .CountAsync(row => row.User!.NormalizedEmail == "PENDING-DUPLICATE@EXAMPLE.TEST")).Should().Be(1);
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .CountAsync(row => row.OwnerEntityId == owner.Id)).Should().Be(1);
        (await _ctx.Db.WorkspaceInvitations.AsNoTracking()
            .CountAsync(row => row.InvitedUser!.NormalizedEmail == "PENDING-DUPLICATE@EXAMPLE.TEST")).Should().Be(1);
        (await _ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.IdempotencyKey.StartsWith($"owner-portal-invitation:{PortfolioId}:{owner.Id}:"))).Should().Be(1);
    }

    [Fact]
    public async Task OutboxFailureRollsBackInvitedAccountContextAccessInvitationAndAudit()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();

        var failure = new ThrowOnOwnerPortalInvitationOutboxInterceptor();
        _ctx = await _fixture.CreateContextAsync();
        _services = BuildServices(_ctx.ConnectionString, failure);
        _scope = SeedActor(RoleProfileKeys.WorkspaceAdministrator);
        _sut = Service();
        var owner = SeedOwner("rollback-owner@example.test");
        await _ctx.Db.SaveChangesAsync();

        var act = () => _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-rollback");

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Injected owner-portal invitation outbox failure.");

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.Users.AsNoTracking()
            .CountAsync(user => user.NormalizedEmail == "ROLLBACK-OWNER@EXAMPLE.TEST")).Should().Be(0);
        (await _ctx.Db.WorkspaceAccessContexts.AsNoTracking()
            .CountAsync(row => row.PortfolioId == PortfolioId &&
                               row.User!.NormalizedEmail == "ROLLBACK-OWNER@EXAMPLE.TEST")).Should().Be(0);
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .CountAsync(row => row.OwnerEntityId == owner.Id)).Should().Be(0);
        (await _ctx.Db.WorkspaceInvitations.AsNoTracking()
            .CountAsync(row => row.PortfolioId == PortfolioId &&
                               row.InvitedUser!.NormalizedEmail == "ROLLBACK-OWNER@EXAMPLE.TEST")).Should().Be(0);
        (await _ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.PortfolioId == PortfolioId &&
                               row.IdempotencyKey.StartsWith($"owner-portal-invitation:{PortfolioId}:{owner.Id}:"))).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row => row.EntityType == nameof(OwnerUserAccess) &&
                               row.EntityId == owner.Id)).Should().Be(0);
    }

    [Fact]
    public async Task AdjacentRoleWithoutTeamManage_FailsClosedAndDoesNotGrantAccess()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();

        _ctx = await _fixture.CreateContextAsync();
        _services = BuildServices(_ctx.ConnectionString);
        _scope = SeedActor(RoleProfileKeys.LeasingAgent);
        _sut = Service();
        var owner = SeedOwner("owner-denied@example.test");
        SeedTargetUserAndContext("owner-denied@example.test");
        await _ctx.Db.SaveChangesAsync();

        var act = () => _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-denied");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .CountAsync(row => row.OwnerEntityId == owner.Id)).Should().Be(0);
    }

    [Fact]
    public void ActivationEndpoint_RequiresTeamManagePolicy()
    {
        var method = typeof(OwnerEntityController).GetMethod(nameof(OwnerEntityController.ActivatePortalAccess))
            ?? throw new InvalidOperationException("Missing owner portal activation endpoint.");
        var policy = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .OfType<AuthorizeAttribute>()
            .Single()
            .Policy;

        policy.Should().Be(CapabilityPolicy.Prefix + CapabilityKeys.TeamManage);
    }

    [Fact]
    public async Task GrantCommand_ReplaysWithoutWritingSecondAuditOrAccessRow()
    {
        var owner = SeedOwner("owner-replay@example.test");
        var target = SeedTargetUserAndContext("owner-replay@example.test");
        await _ctx.Db.SaveChangesAsync();
        var atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
        var command = new GrantOwnerUserAccessCommand(
            PortfolioId,
            owner.Id,
            target.Id,
            1,
            DateTime.UtcNow,
            null,
            "Replay proof",
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision);
        var identity = new AtomicCommandIdentity(
            "owner-entity.portal-access.activate",
            $"{PortfolioId}:{owner.Id}:{target.Id}:replay-proof");

        var first = await atomic.ExecuteAsync(identity, command, OwnerAccessCodec);
        var replay = await atomic.ExecuteAsync(identity, command, OwnerAccessCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.OwnerUserAccessId.Should().Be(first.Value.OwnerUserAccessId);
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .CountAsync(row => row.OwnerEntityId == owner.Id)).Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.EntityType == nameof(OwnerUserAccess) &&
            row.ChangeReason == "Owner portal relationship granted")).Should().Be(1);
    }

    private OwnerEntityService Service() => new(
        _ctx.Db,
        Mock.Of<IDataUpdateService>(),
        TimeProvider.System,
        _services.GetRequiredService<IAtomicUnitOfWork>());

    private static ServiceProvider BuildServices(
        string connectionString,
        DbCommandInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            GrantOwnerUserAccessCommand,
            OwnerRelationshipAccessMutationResult,
            GrantOwnerUserAccessHandler>();
        services.AddAtomicCommandHandler<
            ActivateOwnerPortalAccessCommand,
            ActivateOwnerPortalAccessMutationResult,
            ActivateOwnerPortalAccessHandler>();
        services.AddAtomicCommandHandler<
            ActivateWorkspaceInvitationCommand,
            ActivateWorkspaceInvitationResult,
            ActivateWorkspaceInvitationHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }
        });
        return services.BuildServiceProvider();
    }

    private WorkspaceReadScope SeedActor(string roleProfileKey)
    {
        var now = DateTime.UtcNow;
        var user = _ctx.Db.Users.Single(candidate => candidate.Id == ActorUserId);
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleProfileKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _ctx.Db.AddRange(assignment, session);
        _ctx.Db.SaveChanges();
        _ctx.Db.Entry(accessContext).Reload();
        return new WorkspaceReadScope(
            PortfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private OwnerEntity SeedOwner(string email)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = $"Owner {email}",
            Email = email,
            IsPrimary = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        return owner;
    }

    private WorkspaceAccessContext SeedTargetUserAndContext(string email)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            DisplayName = $"Owner {email}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            PasswordHash = "existing-owner-password-hash",
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Owner,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(context);
        return context;
    }

    private async Task<WorkspaceInvitationActivationRow?> ActivateInvitationAsApiAsync(
        long invitationId,
        int invitedUserId,
        string tokenHash,
        string passwordHash)
    {
        await _ctx.Db.Database.OpenConnectionAsync();
        try
        {
            await _ctx.Db.Database.ExecuteSqlRawAsync("SET SESSION AUTHORIZATION rentalcommand_api;");
            return await _ctx.Db.Database.SqlQuery<WorkspaceInvitationActivationRow>($"""
                    SELECT * FROM rc_activate_workspace_invitation(
                        {invitationId},
                        {invitedUserId},
                        {tokenHash},
                        {passwordHash},
                        {Guid.NewGuid().ToString("N")},
                        {Guid.NewGuid().ToString("N")})
                    """)
                .SingleOrDefaultAsync();
        }
        finally
        {
            await _ctx.Db.Database.ExecuteSqlRawAsync("RESET SESSION AUTHORIZATION;");
            await _ctx.Db.Database.CloseConnectionAsync();
        }
    }

    private static string ExtractActivationToken(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var body = document.RootElement.GetProperty("body").GetString()
            ?? throw new InvalidOperationException("Missing email body.");
        const string marker = "/activate-team?token=";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += marker.Length;
        var cr = body.IndexOf('\r', start);
        var lf = body.IndexOf('\n', start);
        var end = cr >= 0 && lf >= 0 ? Math.Min(cr, lf) : Math.Max(cr, lf);
        var encoded = end >= 0 ? body[start..end] : body[start..];
        return Uri.UnescapeDataString(encoded.Trim());
    }

    private static string HashInvitationToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed class ThrowOnOwnerPortalInvitationOutboxInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfTarget(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfTarget(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfTarget(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.OrdinalIgnoreCase) &&
                command.Parameters.Cast<DbParameter>().Any(parameter =>
                    parameter.Value?.ToString()?.Contains("owner-portal-invitation", StringComparison.Ordinal) == true))
            {
                throw new InvalidOperationException("Injected owner-portal invitation outbox failure.");
            }
        }
    }

    private sealed class WorkspaceInvitationActivationRow
    {
        public int PortfolioId { get; set; }
        public int WorkspaceMembershipId { get; set; }
        public int AccessContextId { get; set; }
        public int InvitedUserId { get; set; }
        public DateTime AcceptedAtUtc { get; set; }
    }
}
