using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
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
    public async Task MissingAccountPrerequisite_ReturnsClearRecoveryWithoutFabricatingAccess()
    {
        var owner = SeedOwner("missing-account@example.test");
        await _ctx.Db.SaveChangesAsync();

        var result = await _sut.ActivateOwnerPortalAccessAsync(
            _scope,
            owner.Id,
            new ActivateOwnerPortalAccessRequest(),
            "activate-owner-missing-account");

        result.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.MissingUserAccount);
        result.Message.Should().Contain("Create or invite the account first");
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .CountAsync(row => row.OwnerEntityId == owner.Id)).Should().Be(0);
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

        var result = await act();

        result.Outcome.Should().Be(ActivateOwnerPortalAccessOutcome.NotFound);
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

    private static ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            GrantOwnerUserAccessCommand,
            OwnerRelationshipAccessMutationResult,
            GrantOwnerUserAccessHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
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
}
