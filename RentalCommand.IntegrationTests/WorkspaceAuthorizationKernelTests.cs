using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Real-PostgreSQL proof for the non-shipping workspace authorization kernel. The tests use
/// EnsureCreated because this forward-only slice deliberately adds no compatibility migration; the
/// final destructive baseline will recreate the schema.
/// </summary>
public sealed class WorkspaceAuthorizationKernelTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<WorkspaceAccessMutationResult> MutationCodec =
        new("workspace-access-mutation-result.v1");
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private readonly DateTime _now = new(2026, 7, 10, 16, 0, 0, DateTimeKind.Utc);

    private int _userId;
    private int _accessContextId;
    private int _portfolioId;
    private int _membershipId;
    private Guid _sessionId;
    private int _leasingPropertyId;
    private int _managerPropertyId;
    private int _unscopedPropertyId;
    private int _otherWorkspacePropertyId;
    private int _managerWorkOrderId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_access")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await SeedKernelAsync(db);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, AccessTestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            ChangeWorkspaceAssignmentScopeCommand,
            WorkspaceAccessMutationResult,
            ChangeWorkspaceAssignmentScopeHandler>();
        services.AddAtomicCommandHandler<
            ChangeWorkspaceAssignmentEndCommand,
            WorkspaceAccessMutationResult,
            ChangeWorkspaceAssignmentEndHandler>();
        services.AddAtomicCommandHandler<
            UnsafeWorkspaceAssignmentMutationCommand,
            WorkspaceAccessMutationResult,
            UnsafeWorkspaceAssignmentMutationHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_connectionString)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SameAssignmentMustSupplyCapabilityAndScope_DecoyDoesNotLeak()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);

        var visible = await db.Properties
            .AsNoTracking()
            .WhereAuthorized(db, active, CapabilityKeys.LeasingListingsManage, _now)
            .OrderBy(property => property.Id)
            .Select(property => property.Id)
            .ToListAsync();

        visible.Should().Equal(_leasingPropertyId);
        visible.Should().NotContain(_managerPropertyId,
            "the Property Manager assignment has scope there but not the requested leasing capability");
    }

    [SkippableFact]
    public async Task MultipleAssignmentsRemainIndependentlyScoped()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);

        var leasing = await db.Properties
            .WhereAuthorized(db, active, CapabilityKeys.LeasingApplicationsManage, _now)
            .Select(property => property.Id)
            .ToListAsync();
        var money = await db.Properties
            .WhereAuthorized(db, active, CapabilityKeys.MoneyPaymentsManage, _now)
            .Select(property => property.Id)
            .ToListAsync();

        leasing.Should().Equal(_leasingPropertyId);
        money.Should().Equal(_managerPropertyId);
    }

    [SkippableFact]
    public async Task ContextSelectionFallsBackToFirstAvailableExperienceWhenMembershipDefaultIsUnavailable()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var managementAssignment = await db.MembershipRoleAssignments
            .SingleAsync(assignment =>
                assignment.WorkspaceMembershipId == _membershipId &&
                assignment.RoleProfileId == 2);
        managementAssignment.Status = MembershipRoleAssignmentStatus.Suspended;
        managementAssignment.SuspendedAtUtc = _now;
        managementAssignment.UpdatedAtUtc = _now;
        await db.SaveChangesAsync();

        var option = (await new EffectiveAccessContextSelectionQuery(db)
                .ListAsync(_userId, _now.AddMinutes(1)))
            .Single(item => item.AccessContextId == _accessContextId);

        option.DefaultExperience.Should().Be(
            WorkspaceExperience.Leasing,
            "the envelope SQL ordering chooses Leasing before Maintenance when Management is unavailable");
        await transaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task PropertyManagerHasOperationalMoneyButNotAdministrativeAuthority()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.MoneyPaymentsManage, PropertyTarget(_managerPropertyId), _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.MoneyReconciliationOperate, PropertyTarget(_managerPropertyId), _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.ResponsibilityAssignExistingMember, PropertyTarget(_managerPropertyId), _now)).Should().BeTrue();

        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.BankConnectionsManage, PropertyTarget(_managerPropertyId), _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.PayoutsManage, PropertyTarget(_managerPropertyId), _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.IntegrationsManage, PropertyTarget(_managerPropertyId), _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.TeamManage, PropertyTarget(_managerPropertyId), _now)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task SelectedPropertyTarget_MustMatchTheSameAssignmentScope()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.MoneyPaymentsManage,
            PropertyTarget(_managerPropertyId),
            _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.MoneyPaymentsManage,
            PropertyTarget(_leasingPropertyId),
            _now)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task MissingAuthorizationTarget_FailsClosed()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.MoneyPaymentsManage,
            target: null,
            utcNow: _now)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task PropertyTarget_RejectsCrossWorkspacePropertyIdInDatabaseDecision()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.MoneyPaymentsManage,
            PropertyTarget(_otherWorkspacePropertyId),
            _now)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task AllPropertiesAssignment_AuthorizesTypedPropertyAndWorkspaceTargetsOnly()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.MembershipRoleAssignments.Add(Assignment(
            _membershipId,
            _portfolioId,
            roleProfileId: 1,
            MembershipRoleAssignmentScopeKind.AllProperties));
        await db.SaveChangesAsync();

        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.RentalsRead,
            PropertyTarget(_unscopedPropertyId),
            _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.TeamManage,
            new WorkspaceCapabilityAuthorizationTarget(_portfolioId),
            _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.RentalsRead,
            new WorkspaceCapabilityAuthorizationTarget(_portfolioId),
            _now)).Should().BeFalse(
            "property-scoped capabilities cannot be widened by supplying a workspace target");

        await transaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task AssignedWorkOrdersScope_FailsClosedUntilResponsibilityJoinExists()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.AssignedWorkRead,
            new WorkOrderCapabilityAuthorizationTarget(_portfolioId, _managerWorkOrderId),
            _now)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task MisScopedMaintenanceAssignments_RemainUnauthorizedAndFailStoredValidation()
    {
        SkipIfNoDocker();
        foreach (var badScope in new[]
                 {
                     MembershipRoleAssignmentScopeKind.AllProperties,
                     MembershipRoleAssignmentScopeKind.SelectedProperties,
                 })
        {
            await using var db = NewContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var assignment = await db.MembershipRoleAssignments.SingleAsync(item =>
                item.WorkspaceMembershipId == _membershipId && item.RoleProfileId == 4);
            assignment.ScopeKind = badScope;
            if (badScope == MembershipRoleAssignmentScopeKind.SelectedProperties)
            {
                db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
                {
                    MembershipRoleAssignmentId = assignment.Id,
                    PropertyId = _managerPropertyId,
                    PortfolioId = _portfolioId,
                });
            }

            await db.SaveChangesAsync();
            var active = await ResolveAsync(db, presentedRevision: 7);
            var evaluator = new WorkspaceAuthorizationEvaluator(db);

            (await evaluator.HasCapabilityAsync(
                active,
                CapabilityKeys.AssignedWorkRead,
                new WorkOrderCapabilityAuthorizationTarget(_portfolioId, _managerWorkOrderId),
                _now)).Should().BeFalse(
                $"maintenance assigned-work capabilities must ignore bad {badScope} data");

            var validationAct = async () => await new MembershipAssignmentScopeValidator(db)
                .ValidateAsync([assignment.Id]);
            await validationAct.Should().ThrowAsync<DomainValidationException>();
            await transaction.RollbackAsync();
        }
    }

    [SkippableFact]
    public async Task StaleAccessRevisionIsRejectedWithCurrentRevision()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var resolver = new ActiveAccessContextResolver(db);

        var act = async () => await resolver.ResolveAsync(
            _sessionId, _userId, _accessContextId, presentedAccessRevision: 6, _now);

        var exception = await act.Should().ThrowAsync<StaleAccessRevisionException>();
        exception.Which.PresentedRevision.Should().Be(6);
        exception.Which.CurrentRevision.Should().Be(7);
    }

    [SkippableFact]
    public void AuthorizationExistsPrecedesOrderingAndPagingInGeneratedSql()
    {
        SkipIfNoDocker();
        using var db = NewContext();
        var active = ActiveContext();

        var sql = db.Properties
            .AsNoTracking()
            .WhereAuthorized(db, active, CapabilityKeys.RentalsRead, _now)
            .OrderBy(property => property.Name)
            .ThenBy(property => property.Id)
            .Skip(20)
            .Take(20)
            .Select(property => new { property.Id, property.Name })
            .ToQueryString();

        var existsAt = sql.IndexOf("EXISTS", StringComparison.OrdinalIgnoreCase);
        var orderAt = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        var limitAt = sql.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase);

        existsAt.Should().BeGreaterThanOrEqualTo(0);
        sql.Should().Contain("RoleProfileCapabilities");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        existsAt.Should().BeLessThan(orderAt,
            "authorization must be in the SQL WHERE clause before sort");
        orderAt.Should().BeLessThan(limitAt,
            "paging must apply only after the authorization predicate and stable ordering");
    }

    [SkippableFact]
    public async Task CompositeScopeForeignKeysRejectPropertyFromAnotherWorkspace()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var assignmentId = await db.MembershipRoleAssignments
            .Where(assignment => assignment.WorkspaceMembershipId == _membershipId &&
                                 assignment.RoleProfileId == 3)
            .Select(assignment => assignment.Id)
            .SingleAsync();

        db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = assignmentId,
            PropertyId = _otherWorkspacePropertyId,
            PortfolioId = await db.WorkspaceMemberships
                .Where(membership => membership.Id == _membershipId)
                .Select(membership => membership.PortfolioId)
                .SingleAsync(),
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task InvalidEffectivePeriodIsRejectedByDatabaseConstraint()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var portfolioId = await db.WorkspaceMemberships
            .Where(membership => membership.Id == _membershipId)
            .Select(membership => membership.PortfolioId)
            .SingleAsync();

        db.MembershipRoleAssignments.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = _membershipId,
            PortfolioId = portfolioId,
            RoleProfileId = 2,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = _now,
            EffectiveToUtc = _now.AddMinutes(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task SelectedPropertyCardinalityValidatorRejectsMismatchedScopeKinds()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var leasingAssignmentId = await db.MembershipRoleAssignments
            .Where(assignment => assignment.WorkspaceMembershipId == _membershipId &&
                                 assignment.RoleProfileId == 3)
            .Select(assignment => assignment.Id)
            .SingleAsync();
        var validator = new MembershipAssignmentScopeValidator(db);

        await validator.ValidateAsync([leasingAssignmentId]);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var assignment = await db.MembershipRoleAssignments.SingleAsync(item => item.Id == leasingAssignmentId);
        assignment.ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties;
        await db.SaveChangesAsync();

        var act = async () => await validator.ValidateAsync([leasingAssignmentId]);
        await act.Should().ThrowAsync<DomainValidationException>();
        await transaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task StoredSelectedPropertyScopeWithoutRows_IsRejected()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var assignment = Assignment(
            _membershipId,
            _portfolioId,
            roleProfileId: 3,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        db.MembershipRoleAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var validator = new MembershipAssignmentScopeValidator(db);
        var act = async () => await validator.ValidateAsync([assignment.Id]);

        await act.Should().ThrowAsync<DomainValidationException>();
        await transaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task StoredRoleScopeMatrix_RejectsEveryInvalidRoleCombination()
    {
        SkipIfNoDocker();
        var invalidCombinations = new[]
        {
            (RoleProfileId: 1, Scope: MembershipRoleAssignmentScopeKind.SelectedProperties),
            (RoleProfileId: 2, Scope: MembershipRoleAssignmentScopeKind.AssignedWorkOrders),
            (RoleProfileId: 3, Scope: MembershipRoleAssignmentScopeKind.AssignedWorkOrders),
            (RoleProfileId: 4, Scope: MembershipRoleAssignmentScopeKind.AllProperties),
            (RoleProfileId: 4, Scope: MembershipRoleAssignmentScopeKind.SelectedProperties),
        };

        foreach (var combination in invalidCombinations)
        {
            await using var db = NewContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var assignment = Assignment(
                _membershipId,
                _portfolioId,
                combination.RoleProfileId,
                combination.Scope);
            if (combination.Scope == MembershipRoleAssignmentScopeKind.SelectedProperties)
            {
                assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
                {
                    PropertyId = _managerPropertyId,
                    PortfolioId = _portfolioId,
                });
            }

            db.MembershipRoleAssignments.Add(assignment);
            await db.SaveChangesAsync();

            var validationAct = async () => await new MembershipAssignmentScopeValidator(db)
                .ValidateAsync([assignment.Id]);
            await validationAct.Should().ThrowAsync<DomainValidationException>();
            await transaction.RollbackAsync();
        }
    }

    [SkippableFact]
    public async Task RevisionGuard_RejectsAuthorityMutationWithoutExactSingleAdvance()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var accessContext = await db.WorkspaceAccessContexts.SingleAsync(context => context.Id == _accessContextId);
        var assignment = await db.MembershipRoleAssignments.FirstAsync(item =>
            item.WorkspaceMembershipId == _membershipId);
        assignment.UpdatedAtUtc = _now.AddMinutes(1);
        accessContext.AdvanceRevision(expectedRevision: 7);
        db.Entry(accessContext).Property(context => context.AccessRevision).CurrentValue = 9;

        var guard = new WorkspaceAccessRevisionGuard();
        var act = async () => await guard.ValidatePendingMutationAsync(
            db, _accessContextId, expectedRevision: 7);

        await act.Should().ThrowAsync<AccessAuthorityMutationException>();
    }

    [SkippableFact]
    public async Task AtomicBoundary_RejectsAuthorityHandlerThatForgetsRevisionAdvance()
    {
        SkipIfNoDocker();
        int assignmentId;
        DateTime originalUpdatedAt;
        await using (var db = NewContext())
        {
            var assignment = await db.MembershipRoleAssignments.FirstAsync(item =>
                item.WorkspaceMembershipId == _membershipId);
            assignmentId = assignment.Id;
            originalUpdatedAt = assignment.UpdatedAtUtc;
        }

        var act = async () => await AtomicUnitOfWork.ExecuteAsync(
            Identity(nameof(AtomicBoundary_RejectsAuthorityHandlerThatForgetsRevisionAdvance)),
            new UnsafeWorkspaceAssignmentMutationCommand(
                _accessContextId,
                ExpectedRevision: 7,
                assignmentId,
                _now.AddMinutes(2)),
            MutationCodec);

        await act.Should().ThrowAsync<AccessAuthorityMutationException>();

        await using var verificationDb = NewContext();
        (await verificationDb.WorkspaceAccessContexts
                .Where(context => context.Id == _accessContextId)
                .Select(context => context.AccessRevision)
                .SingleAsync())
            .Should().Be(7);
        (await verificationDb.MembershipRoleAssignments
                .Where(assignment => assignment.Id == assignmentId)
                .Select(assignment => assignment.UpdatedAtUtc)
                .SingleAsync())
            .Should().Be(originalUpdatedAt);
    }

    [SkippableFact]
    public async Task MutationBoundary_RollsBackRevisionAndAuthorityRowsWhenStoredScopeIsInvalid()
    {
        SkipIfNoDocker();
        int leasingAssignmentId;
        await using (var db = NewContext())
        {
            leasingAssignmentId = await db.MembershipRoleAssignments
                .Where(assignment => assignment.WorkspaceMembershipId == _membershipId &&
                                     assignment.RoleProfileId == 3)
                .Select(assignment => assignment.Id)
                .SingleAsync();
            var act = async () => await AtomicUnitOfWork.ExecuteAsync(
                Identity(nameof(MutationBoundary_RollsBackRevisionAndAuthorityRowsWhenStoredScopeIsInvalid)),
                new ChangeWorkspaceAssignmentScopeCommand(
                    _accessContextId,
                    ExpectedRevision: 7,
                    leasingAssignmentId,
                    MembershipRoleAssignmentScopeKind.AllProperties,
                    _now.AddMinutes(1)),
                MutationCodec);

            await act.Should().ThrowAsync<DomainValidationException>();
        }

        await using var verificationDb = NewContext();
        (await verificationDb.WorkspaceAccessContexts
                .Where(context => context.Id == _accessContextId)
                .Select(context => context.AccessRevision)
                .SingleAsync())
            .Should().Be(7);
        (await verificationDb.MembershipRoleAssignments
                .Where(assignment => assignment.Id == leasingAssignmentId)
                .Select(assignment => assignment.ScopeKind)
                .SingleAsync())
            .Should().Be(MembershipRoleAssignmentScopeKind.SelectedProperties);
    }

    [SkippableFact]
    public async Task MutationBoundary_RollsBackMisScopedMaintenanceAndRevisionForEveryBadScope()
    {
        SkipIfNoDocker();
        foreach (var badScope in new[]
                 {
                     MembershipRoleAssignmentScopeKind.AllProperties,
                     MembershipRoleAssignmentScopeKind.SelectedProperties,
                 })
        {
            int assignmentId;
            await using (var db = NewContext())
            {
                assignmentId = await db.MembershipRoleAssignments
                    .Where(assignment => assignment.WorkspaceMembershipId == _membershipId &&
                                         assignment.RoleProfileId == 4)
                    .Select(assignment => assignment.Id)
                    .SingleAsync();

                var mutationAct = async () => await AtomicUnitOfWork.ExecuteAsync(
                    Identity($"{nameof(MutationBoundary_RollsBackMisScopedMaintenanceAndRevisionForEveryBadScope)}-{badScope}"),
                    new ChangeWorkspaceAssignmentScopeCommand(
                        _accessContextId,
                        ExpectedRevision: 7,
                        assignmentId,
                        badScope,
                        _now.AddMinutes(1)),
                    MutationCodec);

                await mutationAct.Should().ThrowAsync<DomainValidationException>();
            }

            await using var verificationDb = NewContext();
            var persisted = await verificationDb.MembershipRoleAssignments
                .Where(assignment => assignment.Id == assignmentId)
                .Select(assignment => new
                {
                    assignment.ScopeKind,
                    SelectedCount = assignment.SelectedProperties.Count(),
                    Revision = assignment.WorkspaceMembership!.AccessContext!.AccessRevision,
                })
                .SingleAsync();
            persisted.ScopeKind.Should().Be(MembershipRoleAssignmentScopeKind.AssignedWorkOrders);
            persisted.SelectedCount.Should().Be(0);
            persisted.Revision.Should().Be(7);
        }
    }

    [SkippableFact]
    public async Task ConcurrentMutationWithStaleExpectedRevision_RollsBack()
    {
        SkipIfNoDocker();
        IsolatedAccessRoot isolated;
        await using (var seedDb = NewContext())
        {
            isolated = await SeedIsolatedAccessRootAsync(seedDb, Guid.NewGuid().ToString("N"));
        }

        try
        {
            await AtomicUnitOfWork.ExecuteAsync(
                Identity($"{nameof(ConcurrentMutationWithStaleExpectedRevision_RollsBack)}-winner"),
                new ChangeWorkspaceAssignmentEndCommand(
                    isolated.AccessContextId,
                    ExpectedRevision: 1,
                    isolated.AssignmentId,
                    _now.AddDays(30),
                    _now.AddMinutes(1)),
                MutationCodec);

            var staleAct = async () => await AtomicUnitOfWork.ExecuteAsync(
                Identity($"{nameof(ConcurrentMutationWithStaleExpectedRevision_RollsBack)}-stale"),
                new ChangeWorkspaceAssignmentEndCommand(
                    isolated.AccessContextId,
                    ExpectedRevision: 1,
                    isolated.AssignmentId,
                    _now.AddDays(60),
                    _now.AddMinutes(2)),
                MutationCodec);

            await staleAct.Should().ThrowAsync<StaleAccessRevisionException>();

            await using var verificationDb = NewContext();
            (await verificationDb.WorkspaceAccessContexts
                    .Where(context => context.Id == isolated.AccessContextId)
                    .Select(context => context.AccessRevision)
                    .SingleAsync())
                .Should().Be(2);
            (await verificationDb.MembershipRoleAssignments
                    .Where(assignment => assignment.Id == isolated.AssignmentId)
                    .Select(assignment => assignment.EffectiveToUtc)
                    .SingleAsync())
                .Should().Be(_now.AddDays(30));
        }
        finally
        {
            await DeleteIsolatedAccessRootAsync(isolated);
        }
    }

    [SkippableFact]
    public async Task AuthorityOwnershipCannotBeReparentedToAnotherAccessRoot()
    {
        SkipIfNoDocker();
        IsolatedAccessRoot source;
        IsolatedAccessRoot destination;
        await using (var seedDb = NewContext())
        {
            source = await SeedIsolatedAccessRootAsync(seedDb, $"source-{Guid.NewGuid():N}");
            destination = await SeedIsolatedAccessRootAsync(seedDb, $"destination-{Guid.NewGuid():N}");
        }

        try
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var assignment = await db.MembershipRoleAssignments
                .SingleAsync(item => item.Id == source.AssignmentId);
            var destinationMembershipId = await db.WorkspaceMemberships
                .Where(item => item.AccessContextId == destination.AccessContextId)
                .Select(item => item.Id)
                .SingleAsync();

            assignment.WorkspaceMembershipId = destinationMembershipId;
            var act = async () => await db.SaveChangesAsync();

            await act.Should().ThrowAsync<AccessAuthorityMutationException>()
                .WithMessage("*WorkspaceMembershipId*immutable*");
        }
        finally
        {
            await DeleteIsolatedAccessRootAsync(source);
            await DeleteIsolatedAccessRootAsync(destination);
        }
    }

    [SkippableFact]
    public async Task SuspendedAccessContext_IsNeverEffectiveAndActiveSuspensionFactIsRejected()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var context = await db.WorkspaceAccessContexts.SingleAsync(item => item.Id == _accessContextId);
        context.Status = WorkspaceAccessContextStatus.Suspended;
        context.SuspendedAtUtc = _now;
        await db.SaveChangesAsync();

        var resolver = new ActiveAccessContextResolver(db);
        var suspendedAct = async () => await resolver.ResolveAsync(
            _sessionId, _userId, _accessContextId, presentedAccessRevision: 7, _now);
        await suspendedAct.Should().ThrowAsync<AccessContextUnavailableException>();
        await transaction.RollbackAsync();

        await using var contradictionDb = NewContext();
        await using var contradictionTransaction = await contradictionDb.Database.BeginTransactionAsync();
        var active = await contradictionDb.WorkspaceAccessContexts
            .SingleAsync(item => item.Id == _accessContextId);
        active.SuspendedAtUtc = _now;
        var contradictionAct = async () => await contradictionDb.SaveChangesAsync();
        await contradictionAct.Should().ThrowAsync<DbUpdateException>();
        await contradictionTransaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task SessionRefreshCredential_RotatesOnceWithinItsOwnFamily()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var family = RefreshFamily();
        var first = RefreshCredential(family.Id, "hash-first");
        family.Credentials.Add(first);
        db.AuthSessionRefreshTokenFamilies.Add(family);
        await db.SaveChangesAsync();

        var replacement = RefreshCredential(family.Id, "hash-replacement");
        first.ConsumedAtUtc = _now.AddMinutes(1);
        first.ReplacedByCredential = replacement;
        family.Credentials.Add(replacement);
        await db.SaveChangesAsync();

        (await db.AuthSessionRefreshCredentials.CountAsync(credential =>
            credential.RefreshTokenFamilyId == family.Id)).Should().Be(2);
        await transaction.RollbackAsync();
    }

    [SkippableFact]
    public async Task SessionRefreshCredential_RejectsReplacementBeforeConsumption()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var family = RefreshFamily();
        var first = RefreshCredential(family.Id, "hash-invalid-first");
        var replacement = RefreshCredential(family.Id, "hash-invalid-replacement");
        first.ReplacedByCredential = replacement;
        family.Credentials.Add(first);
        family.Credentials.Add(replacement);
        db.AuthSessionRefreshTokenFamilies.Add(family);

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
        await transaction.RollbackAsync();
    }

    private async Task SeedKernelAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = "multi@example.test",
            NormalizedUserName = "MULTI@EXAMPLE.TEST",
            Email = "multi@example.test",
            NormalizedEmail = "MULTI@EXAMPLE.TEST",
            DisplayName = "Multi Assignment",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var workspace = Portfolio("Workspace A");
        var otherWorkspace = Portfolio("Workspace B");
        db.AddRange(user, workspace, otherWorkspace);
        await db.SaveChangesAsync();

        var leasingProperty = Property(workspace.Id, "Leasing Property");
        var managerProperty = Property(workspace.Id, "Manager Property");
        var unscopedProperty = Property(workspace.Id, "Unscoped Property");
        var outsideProperty = Property(otherWorkspace.Id, "Outside Property");
        db.AddRange(leasingProperty, managerProperty, unscopedProperty, outsideProperty);

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = workspace.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Leasing,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        for (var revision = 1L; revision < 7; revision++)
        {
            accessContext.AdvanceRevision(revision);
        }
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = workspace.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        db.Add(membership);
        await db.SaveChangesAsync();

        var leasingAssignment = Assignment(
            membership.Id, workspace.Id, roleProfileId: 3,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        leasingAssignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            Property = leasingProperty,
            PortfolioId = workspace.Id,
        });

        var managerAssignment = Assignment(
            membership.Id, workspace.Id, roleProfileId: 2,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        managerAssignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            Property = managerProperty,
            PortfolioId = workspace.Id,
        });

        var technicianAssignment = Assignment(
            membership.Id, workspace.Id, roleProfileId: 4,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders);

        var workOrder = new WorkOrder
        {
            PortfolioId = workspace.Id,
            Property = managerProperty,
            Title = "Assigned repair",
            Description = "Responsibility relation intentionally absent.",
            Category = "General",
            RequestedAt = _now.AddHours(-2),
            UpdatedAt = _now,
        };

        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now.AddHours(-1),
            LastSeenAtUtc = _now,
            ExpiresAtUtc = _now.AddDays(1),
        };
        db.AddRange(leasingAssignment, managerAssignment, technicianAssignment, workOrder, session);
        await db.SaveChangesAsync();

        _userId = user.Id;
        _accessContextId = accessContext.Id;
        _portfolioId = workspace.Id;
        _membershipId = membership.Id;
        _sessionId = session.Id;
        _leasingPropertyId = leasingProperty.Id;
        _managerPropertyId = managerProperty.Id;
        _unscopedPropertyId = unscopedProperty.Id;
        _otherWorkspacePropertyId = outsideProperty.Id;
        _managerWorkOrderId = workOrder.Id;
    }

    private MembershipRoleAssignment Assignment(
        int membershipId,
        int portfolioId,
        int roleProfileId,
        MembershipRoleAssignmentScopeKind scopeKind) => new()
    {
        WorkspaceMembershipId = membershipId,
        PortfolioId = portfolioId,
        RoleProfileId = roleProfileId,
        Status = MembershipRoleAssignmentStatus.Active,
        ScopeKind = scopeKind,
        EffectiveFromUtc = _now.AddHours(-1),
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private Portfolio Portfolio(string name) => new()
    {
        Name = name,
        ManagementCompanyName = name,
        TimeZone = "UTC",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private Property Property(int portfolioId, string name) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        AddressLine1 = "1 Main St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private ActiveAccessContext ActiveContext() => new(
        _sessionId,
        _userId,
        _accessContextId,
        PortfolioId: _portfolioId,
        AccessRevision: 7,
        LastAuthorizedExperience: WorkspaceExperience.Leasing,
        WorkspaceMembershipId: _membershipId,
        DefaultExperience: WorkspaceExperience.Management);

    private PropertyCapabilityAuthorizationTarget PropertyTarget(int propertyId) =>
        new(_portfolioId, propertyId);

    private IAtomicUnitOfWork AtomicUnitOfWork =>
        _services?.GetRequiredService<IAtomicUnitOfWork>()
        ?? throw new InvalidOperationException("Atomic access services are unavailable.");

    private sealed record UnsafeWorkspaceAssignmentMutationCommand(
        int AccessContextId,
        long ExpectedRevision,
        int AssignmentId,
        DateTime ChangedAtUtc) : IWorkspaceAccessMutationCommand;

    private sealed class UnsafeWorkspaceAssignmentMutationHandler
        : IAtomicCommandHandler<UnsafeWorkspaceAssignmentMutationCommand, WorkspaceAccessMutationResult>
    {
        public async Task<WorkspaceAccessMutationResult> HandleAsync(
            UnsafeWorkspaceAssignmentMutationCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var assignment = await attempt.Persistence.Query<MembershipRoleAssignment>()
                .SingleAsync(item => item.Id == command.AssignmentId &&
                                     item.WorkspaceMembership!.AccessContextId == command.AccessContextId, ct);
            assignment.UpdatedAtUtc = command.ChangedAtUtc;

            return new WorkspaceAccessMutationResult(
                command.AccessContextId,
                command.AssignmentId,
                command.ExpectedRevision);
        }
    }

    private IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Atomic access services are unavailable.");

    private static AtomicCommandIdentity Identity(string commandType) =>
        new(commandType, Guid.NewGuid().ToString("N"));

    private AuthSessionRefreshTokenFamily RefreshFamily() => new()
    {
        Id = Guid.NewGuid(),
        AuthSessionId = _sessionId,
        CreatedAtUtc = _now,
        AbsoluteExpiresAtUtc = _now.AddDays(30),
    };

    private AuthSessionRefreshCredential RefreshCredential(Guid familyId, string tokenHash) => new()
    {
        Id = Guid.NewGuid(),
        RefreshTokenFamilyId = familyId,
        TokenHash = tokenHash + Guid.NewGuid().ToString("N"),
        IssuedAtUtc = _now,
        ExpiresAtUtc = _now.AddDays(7),
    };

    private async Task<IsolatedAccessRoot> SeedIsolatedAccessRootAsync(
        RentalCommandDbContext db,
        string suffix)
    {
        var user = new ApplicationUser
        {
            UserName = $"guard-{suffix}@example.test",
            NormalizedUserName = $"GUARD-{suffix}@EXAMPLE.TEST",
            Email = $"guard-{suffix}@example.test",
            NormalizedEmail = $"GUARD-{suffix}@EXAMPLE.TEST",
            DisplayName = "Guard Test",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var portfolio = Portfolio($"Guard {suffix}");
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var assignment = Assignment(
            membershipId: 0,
            portfolio.Id,
            roleProfileId: 1,
            MembershipRoleAssignmentScopeKind.AllProperties);
        assignment.WorkspaceMembership = membership;
        db.Add(assignment);
        await db.SaveChangesAsync();

        return new IsolatedAccessRoot(
            user.Id,
            portfolio.Id,
            context.Id,
            assignment.Id);
    }

    private async Task DeleteIsolatedAccessRootAsync(IsolatedAccessRoot isolated)
    {
        await using var db = NewContext();
        await db.WorkspaceAccessContexts
            .Where(context => context.Id == isolated.AccessContextId)
            .ExecuteDeleteAsync();
        await db.Users
            .Where(user => user.Id == isolated.UserId)
            .ExecuteDeleteAsync();
        await db.Portfolios
            .Where(portfolio => portfolio.Id == isolated.PortfolioId)
            .ExecuteDeleteAsync();
    }

    private sealed record IsolatedAccessRoot(
        int UserId,
        int PortfolioId,
        int AccessContextId,
        int AssignmentId);

    private async Task<ActiveAccessContext> ResolveAsync(
        RentalCommandDbContext db,
        long presentedRevision) =>
        await new ActiveAccessContextResolver(db).ResolveAsync(
            _sessionId, _userId, _accessContextId, presentedRevision, _now);

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; workspace authorization kernel test skipped.");

    private sealed class AccessTestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:workspace-access";
        public string? IpAddress => "127.0.0.1";
    }
}
