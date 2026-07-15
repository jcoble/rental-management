using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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
    private static readonly AtomicJsonResultCodec<CreateWorkspaceMembershipResult> TeamCreateCodec =
        new("workspace-team.membership.create.v1");
    private static readonly AtomicJsonResultCodec<WorkspaceTeamMutationResult> TeamMutationCodec =
        new("workspace-team.mutation.v1");
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
    private int _leasingUnitId;
    private int _managerUnitId;
    private int _leasingApplicationId;
    private int _managerApplicationId;

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
        await db.Database.MigrateAsync();
        await SeedKernelAsync(db);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, AccessTestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicMoneyMutationCommand,
            AtomicMoneyMutationResult,
            AtomicMoneyMutationHandler>();
        services.AddAtomicCommandHandler<
            ChangeWorkspaceAssignmentScopeCommand,
            WorkspaceAccessMutationResult,
            ChangeWorkspaceAssignmentScopeHandler>();
        services.AddAtomicCommandHandler<
            ChangeWorkspaceAssignmentEndCommand,
            WorkspaceAccessMutationResult,
            ChangeWorkspaceAssignmentEndHandler>();
        services.AddAtomicCommandHandler<
            CreateWorkspaceMembershipCommand,
            CreateWorkspaceMembershipResult,
            CreateWorkspaceMembershipHandler>();
        services.AddAtomicCommandHandler<
            AddWorkspaceRoleAssignmentCommand,
            WorkspaceTeamMutationResult,
            AddWorkspaceRoleAssignmentHandler>();
        services.AddAtomicCommandHandler<
            EndWorkspaceRoleAssignmentCommand,
            WorkspaceTeamMutationResult,
            EndWorkspaceRoleAssignmentHandler>();
        services.AddAtomicCommandHandler<
            ReplaceWorkspaceAssignmentPropertyScopeCommand,
            WorkspaceTeamMutationResult,
            ReplaceWorkspaceAssignmentPropertyScopeHandler>();
        services.AddAtomicCommandHandler<
            ChangeWorkspaceMembershipStatusCommand,
            WorkspaceTeamMutationResult,
            ChangeWorkspaceMembershipStatusHandler>();
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
                .ListAsync(_userId, null, _now.AddMinutes(1)))
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
        var propertyTarget = PropertyTarget(_managerPropertyId);
        var workspaceTarget = new WorkspaceCapabilityAuthorizationTarget(_portfolioId);

        var operationalCapabilities = new[]
        {
            CapabilityKeys.RentalsRead,
            CapabilityKeys.RentalsManage,
            CapabilityKeys.WorkRead,
            CapabilityKeys.WorkManage,
            CapabilityKeys.ReportsRead,
            CapabilityKeys.MoneyBalancesRead,
            CapabilityKeys.MoneyChargesManage,
            CapabilityKeys.MoneyPaymentsManage,
            CapabilityKeys.MoneyExpensesManage,
            CapabilityKeys.MoneyDepositsManage,
            CapabilityKeys.MoneyOwnerReportsRead,
            CapabilityKeys.MoneyReconciliationOperate,
            CapabilityKeys.ResponsibilityAssignExistingMember,
        };
        foreach (var capability in operationalCapabilities)
        {
            (await evaluator.HasCapabilityAsync(active, capability, propertyTarget, _now))
                .Should().BeTrue($"Property Managers need {capability} for assigned-property operations");
        }

        var administratorCapabilities = new[]
        {
            CapabilityKeys.TeamRead,
            CapabilityKeys.TeamManage,
            CapabilityKeys.SecurityManage,
            CapabilityKeys.BillingManage,
            CapabilityKeys.IntegrationsManage,
            CapabilityKeys.DataExport,
            CapabilityKeys.BankConnectionsManage,
            CapabilityKeys.PayoutsManage,
            CapabilityKeys.MoneyDisbursementsManage,
            CapabilityKeys.MoneyReconciliationDestructive,
            CapabilityKeys.AccountDestructiveActions,
            CapabilityKeys.NotificationsManage,
        };
        foreach (var capability in administratorCapabilities)
        {
            (await evaluator.HasCapabilityAsync(active, capability, workspaceTarget, _now))
                .Should().BeFalse($"Property Managers must not receive workspace authority through {capability}");
        }

        foreach (var capability in new[]
                 {
                     CapabilityKeys.ReportsRead,
                     CapabilityKeys.MoneyBalancesRead,
                     CapabilityKeys.MoneyPaymentsManage,
                     CapabilityKeys.MoneyOwnerReportsRead,
                 })
        {
            (await evaluator.HasCapabilityAsync(active, capability, PropertyTarget(_leasingPropertyId), _now))
                .Should().BeFalse(
                    $"the independently scoped Leasing Agent assignment must not leak {capability}");
        }
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
    public async Task MarketplaceTargets_RequireExactCapabilityAndOwningPropertyScope()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingListingsManage,
            new UnitCapabilityAuthorizationTarget(_portfolioId, _leasingUnitId),
            _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingListingsManage,
            new UnitCapabilityAuthorizationTarget(_portfolioId, _managerUnitId),
            _now)).Should().BeFalse(
            "a different assignment's property scope cannot supply the leasing capability");

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingApplicationsManage,
            new RentalApplicationCapabilityAuthorizationTarget(_portfolioId, _leasingApplicationId),
            _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingApplicationsManage,
            new RentalApplicationCapabilityAuthorizationTarget(_portfolioId, _managerApplicationId),
            _now)).Should().BeFalse(
            "portfolio membership and an unrelated scoped assignment are not application authority");
    }

    [SkippableFact]
    public async Task RelationshipOnlyContext_CannotUseMarketplaceTeamCapabilities()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = (await ResolveAsync(db, presentedRevision: 7)) with
        {
            WorkspaceMembershipId = null,
            DefaultExperience = null,
        };
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingListingsManage,
            new UnitCapabilityAuthorizationTarget(_portfolioId, _leasingUnitId),
            _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingApplicationsManage,
            new RentalApplicationCapabilityAuthorizationTarget(_portfolioId, _leasingApplicationId),
            _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.TeamRead,
            new WorkspaceCapabilityAuthorizationTarget(_portfolioId),
            _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.TeamManage,
            new WorkspaceCapabilityAuthorizationTarget(_portfolioId),
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
        var workspaceTarget = new WorkspaceCapabilityAuthorizationTarget(_portfolioId);
        foreach (var capability in new[]
                 {
                     CapabilityKeys.TeamRead,
                     CapabilityKeys.TeamManage,
                     CapabilityKeys.SecurityManage,
                     CapabilityKeys.BillingManage,
                     CapabilityKeys.IntegrationsManage,
                     CapabilityKeys.DataExport,
                     CapabilityKeys.BankConnectionsManage,
                     CapabilityKeys.PayoutsManage,
                     CapabilityKeys.MoneyDisbursementsManage,
                     CapabilityKeys.MoneyReconciliationDestructive,
                     CapabilityKeys.AccountDestructiveActions,
                     CapabilityKeys.NotificationsManage,
                 })
        {
            (await evaluator.HasCapabilityAsync(active, capability, workspaceTarget, _now))
                .Should().BeTrue($"Workspace Administrators retain {capability}");
        }
        (await evaluator.HasCapabilityAsync(
            active,
            CapabilityKeys.RentalsRead,
            workspaceTarget,
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
    public void MoneyAuthorizationRemainsDbSideBeforeOrderingAndPaging()
    {
        SkipIfNoDocker();
        using var db = NewContext();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);

        var queries = new[]
        {
            db.Expenses.AsNoTracking()
                .WhereMoneyAuthorized(db, scope, CapabilityKeys.MoneyBalancesRead, _now)
                .OrderBy(row => row.Id).Skip(20).Take(20).Select(row => row.Id).ToQueryString(),
            db.RecurringExpenses.AsNoTracking()
                .WhereMoneyAuthorized(db, scope, CapabilityKeys.MoneyBalancesRead, _now)
                .OrderBy(row => row.Id).Skip(20).Take(20).Select(row => row.Id).ToQueryString(),
            db.Loans.AsNoTracking()
                .WhereMoneyAuthorized(db, scope, CapabilityKeys.MoneyBalancesRead, _now)
                .OrderBy(row => row.Id).Skip(20).Take(20).Select(row => row.Id).ToQueryString(),
            db.OwnerDistributions.AsNoTracking()
                .WhereMoneyAuthorized(db, scope, CapabilityKeys.MoneyOwnerReportsRead, _now)
                .OrderBy(row => row.Id).Skip(20).Take(20).Select(row => row.Id).ToQueryString(),
        };

        foreach (var sql in queries)
        {
            var existsAt = sql.IndexOf("EXISTS", StringComparison.OrdinalIgnoreCase);
            var orderAt = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
            var limitAt = sql.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase);

            existsAt.Should().BeGreaterThanOrEqualTo(0);
            sql.Should().Contain("RoleProfileCapabilities");
            existsAt.Should().BeLessThan(orderAt,
                "money authorization must remain in the SQL WHERE clause before sort");
            orderAt.Should().BeLessThan(limitAt,
                "money paging must apply only after authorization and stable ordering");
        }
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_StaleRevisionRollsBackBusinessRowAndReceipt()
    {
        SkipIfNoDocker();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 6);
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "stale-test", ValidLoanRequest(_managerPropertyId));
        var identity = AtomicMoneyMutation.Identity(command);

        var act = () => AtomicUnitOfWork.ExecuteAsync(identity, command, AtomicMoneyMutation.Codec);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        await using var verify = NewContext();
        (await verify.Loans.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_SelectedScopeReturnsMissingForUnassignedProperty()
    {
        SkipIfNoDocker();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "cross-scope-test", ValidLoanRequest(_unscopedPropertyId));
        var identity = AtomicMoneyMutation.Identity(command);

        var outcome = await AtomicUnitOfWork.ExecuteAsync(identity, command, AtomicMoneyMutation.Codec);
        outcome.Value.Should().Be(new AtomicMoneyMutationResult(
            Found: false, Applied: false, EntityId: 0));

        await using var verify = NewContext();
        (await verify.Loans.CountAsync(row => row.PropertyId == _unscopedPropertyId)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_FailedBusinessFlushRollsBackReceipt()
    {
        SkipIfNoDocker();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var request = ValidLoanRequest(_managerPropertyId);
        request.Lender = null!;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "rollback-test", request);
        var identity = AtomicMoneyMutation.Identity(command);

        var act = () => AtomicUnitOfWork.ExecuteAsync(identity, command, AtomicMoneyMutation.Codec);
        await act.Should().ThrowAsync<DbUpdateException>();

        await using var verify = NewContext();
        (await verify.Loans.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_SameCallerKeyReplaysAndChangedPayloadConflicts()
    {
        SkipIfNoDocker();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var request = ValidLoanRequest(_managerPropertyId);
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "stable-loan-create", request);
        var identity = AtomicMoneyMutation.Identity(command);

        var first = await AtomicUnitOfWork.ExecuteAsync(identity, command, AtomicMoneyMutation.Codec);
        var replay = await AtomicUnitOfWork.ExecuteAsync(identity, command, AtomicMoneyMutation.Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        var changedRequest = ValidLoanRequest(_managerPropertyId);
        changedRequest.Lender = "Different lender";
        var changedCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "stable-loan-create", changedRequest);
        var conflictAct = async () => await AtomicUnitOfWork.ExecuteAsync(
            AtomicMoneyMutation.Identity(changedCommand), changedCommand, AtomicMoneyMutation.Codec);

        await conflictAct.Should().ThrowAsync<AtomicIdempotencyConflictException>();

        await using var verify = NewContext();
        (await verify.Loans.AsNoTracking().CountAsync(row =>
            row.Id == first.Value.EntityId && row.PortfolioId == _portfolioId)).Should().Be(1);
        (await verify.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_DeleteReplaysAfterTheBusinessRowIsSoftDeleted()
    {
        SkipIfNoDocker();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var createCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "loan-before-delete", ValidLoanRequest(_managerPropertyId));
        var created = await AtomicUnitOfWork.ExecuteAsync(
            AtomicMoneyMutation.Identity(createCommand), createCommand, AtomicMoneyMutation.Codec);
        var deleteCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Delete, created.Value.EntityId, "stable-loan-delete", new object());
        var deleteIdentity = AtomicMoneyMutation.Identity(deleteCommand);

        var firstDelete = await AtomicUnitOfWork.ExecuteAsync(
            deleteIdentity, deleteCommand, AtomicMoneyMutation.Codec);
        var replayedDelete = await AtomicUnitOfWork.ExecuteAsync(
            deleteIdentity, deleteCommand, AtomicMoneyMutation.Codec);

        firstDelete.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replayedDelete.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayedDelete.Value.Should().Be(firstDelete.Value);
        replayedDelete.Value.Should().Be(new AtomicMoneyMutationResult(
            Found: true, Applied: true, EntityId: created.Value.EntityId));

        await using var verify = NewContext();
        var persisted = await verify.Loans.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.Id == created.Value.EntityId && row.PortfolioId == _portfolioId)
            .Select(row => new { row.Id, row.DeletedAt })
            .SingleAsync();
        persisted.DeletedAt.Should().NotBeNull();
        (await verify.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == deleteIdentity.CommandType && row.IdempotencyKey == deleteIdentity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_ExpensePatchRejectsIncompatibleEffectivePropertyUnitAndWorkOrder()
    {
        SkipIfNoDocker();
        int unitWorkOrderId;
        await using (var seed = NewContext())
        {
            var workOrder = new WorkOrder
            {
                PortfolioId = _portfolioId,
                PropertyId = _managerPropertyId,
                UnitId = _managerUnitId,
                Title = "Unit-scoped repair",
                Description = "Expense effective-tuple test",
                Category = "General",
                RequestedAt = _now,
                UpdatedAt = _now,
            };
            seed.WorkOrders.Add(workOrder);
            await seed.SaveChangesAsync();
            unitWorkOrderId = workOrder.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var createRequest = new CreateExpenseRequest
        {
            PropertyId = _managerPropertyId,
            UnitId = _managerUnitId,
            WorkOrderId = unitWorkOrderId,
            Category = ScheduleECategory.Repairs,
            Description = "Scoped repair expense",
            Status = ExpenseStatus.Pending,
            Amount = 125m,
            IncurredAt = _now,
        };
        var createCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Create, 0, "effective-expense-create", createRequest);
        var created = await AtomicUnitOfWork.ExecuteAsync(
            AtomicMoneyMutation.Identity(createCommand), createCommand, AtomicMoneyMutation.Codec);

        var patchCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, created.Value.EntityId,
            "effective-expense-invalid-patch", new UpdateExpenseRequest { PropertyId = _leasingPropertyId });
        var patchOutcome = await AtomicUnitOfWork.ExecuteAsync(
            AtomicMoneyMutation.Identity(patchCommand), patchCommand, AtomicMoneyMutation.Codec);

        patchOutcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        patchOutcome.Value.Should().Be(new AtomicMoneyMutationResult(
            Found: false, Applied: false, EntityId: 0));

        await using var verify = NewContext();
        var unchanged = await verify.Expenses.AsNoTracking()
            .Where(row => row.Id == created.Value.EntityId && row.PortfolioId == _portfolioId)
            .Select(row => new { row.PropertyId, row.UnitId, row.WorkOrderId })
            .SingleAsync();
        unchanged.PropertyId.Should().Be(_managerPropertyId);
        unchanged.UnitId.Should().Be(_managerUnitId);
        unchanged.WorkOrderId.Should().Be(unitWorkOrderId);
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
    public async Task TeamInvite_ReplayReturnsSameMembershipWithoutDuplicateAuthorityRows()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"invite-{Guid.NewGuid():N}");
        var identity = new AtomicCommandIdentity("test.team.membership.create", Guid.NewGuid().ToString("N"));
        var command = new CreateWorkspaceMembershipCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            $"invite-{Guid.NewGuid():N}@example.test", "Invited Member",
            RoleProfileKeys.LeasingAgent, MembershipRoleAssignmentScopeKind.SelectedProperties,
            [_leasingPropertyId], _now, "https://localhost:5667");

        var first = await AtomicUnitOfWork.ExecuteAsync(identity, command, TeamCreateCodec);
        var replay = await AtomicUnitOfWork.ExecuteAsync(identity, command with
        {
            ActorAuthSessionId = pair.ActorSessionId,
            ActorAccessRevision = 1,
        }, TeamCreateCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var db = NewContext();
        (await db.WorkspaceMemberships.CountAsync(item =>
            item.AccessContextId == first.Value.AccessContextId)).Should().Be(1);
        (await db.MembershipRoleAssignments.CountAsync(item =>
            item.WorkspaceMembershipId == first.Value.WorkspaceMembershipId)).Should().Be(1);
        var invitation = await db.WorkspaceInvitations.SingleAsync(item =>
            item.WorkspaceMembershipId == first.Value.WorkspaceMembershipId);
        invitation.TokenHash.Should().HaveLength(64);
        invitation.AcceptedAtUtc.Should().BeNull();
        invitation.ExpiresAtUtc.Should().BeAfter(invitation.CreatedAtUtc);
        (await db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey ==
            $"workspace-invitation:{first.Value.WorkspaceMembershipId}:activation-v1"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task TeamInvite_ReusesExistingRelationshipContextWithoutCreatingSecondRoot()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"relationship-invite-{Guid.NewGuid():N}");
        var email = $"owner-team-{Guid.NewGuid():N}@example.test";
        int relationshipUserId;
        int relationshipContextId;
        await using (var seedDb = NewContext())
        {
            var user = new ApplicationUser
            {
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = "Owner Becoming Manager",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = _now,
            };
            var context = new WorkspaceAccessContext
            {
                User = user,
                PortfolioId = pair.PortfolioId,
                Status = WorkspaceAccessContextStatus.Active,
                LastAuthorizedExperience = WorkspaceExperience.Owner,
                CreatedAtUtc = _now,
                UpdatedAtUtc = _now,
            };
            var owner = new OwnerEntity
            {
                PortfolioId = pair.PortfolioId,
                Name = "Relationship Owner",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var access = new OwnerUserAccess
            {
                PortfolioId = pair.PortfolioId,
                AccessContext = context,
                ApplicationUser = user,
                OwnerEntity = owner,
                EffectiveFromUtc = _now.AddDays(-1),
                GrantedAtUtc = _now,
                GrantedByUserId = pair.ActorUserId,
                Reason = "Existing owner portal access",
            };
            seedDb.Add(access);
            await seedDb.SaveChangesAsync();
            relationshipUserId = user.Id;
            relationshipContextId = context.Id;
        }

        var result = await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.relationship-context-reuse"),
            new CreateWorkspaceMembershipCommand(
                pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
                email, "Owner Becoming Manager", RoleProfileKeys.PropertyManager,
                MembershipRoleAssignmentScopeKind.SelectedProperties, [_managerPropertyId], _now,
                "https://localhost:5667"),
            TeamCreateCodec);

        result.Value.UserId.Should().Be(relationshipUserId);
        result.Value.AccessContextId.Should().Be(relationshipContextId);
        result.Value.AccessRevision.Should().Be(2);
        await using var verification = NewContext();
        (await verification.WorkspaceAccessContexts.CountAsync(context =>
            context.UserId == relationshipUserId && context.PortfolioId == pair.PortfolioId)).Should().Be(1);
        (await verification.WorkspaceMemberships.CountAsync(membership =>
            membership.AccessContextId == relationshipContextId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task TeamInvite_RejectsExistingTeamMembershipInsteadOfAddingDuplicateAuthority()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"duplicate-invite-{Guid.NewGuid():N}");
        string targetEmail;
        await using (var db = NewContext())
        {
            targetEmail = await db.Users.Where(user => user.Id == pair.TargetUserId)
                .Select(user => user.Email!)
                .SingleAsync();
        }

        var act = async () => await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.duplicate-membership"),
            new CreateWorkspaceMembershipCommand(
                pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
                targetEmail, "Duplicate Team Member", RoleProfileKeys.PropertyManager,
                MembershipRoleAssignmentScopeKind.SelectedProperties, [_managerPropertyId], _now,
                "https://localhost:5667"),
            TeamCreateCodec);

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*already a Team member*");
        await using var verification = NewContext();
        (await verification.WorkspaceMemberships.CountAsync(membership =>
            membership.AccessContextId == pair.TargetContextId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task TeamAssignment_ReplayPreservesExistingAssignmentsAndReturnsStableRevision()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"assignment-{Guid.NewGuid():N}");
        var identity = new AtomicCommandIdentity("test.team.assignment.add", Guid.NewGuid().ToString("N"));
        var command = TeamAddCommand(pair, expectedTargetRevision: 1, _managerPropertyId);

        var first = await AtomicUnitOfWork.ExecuteAsync(identity, command, TeamMutationCodec);
        var replay = await AtomicUnitOfWork.ExecuteAsync(identity, command, TeamMutationCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        replay.Value.AccessRevision.Should().Be(2);
        await using var db = NewContext();
        (await db.MembershipRoleAssignments.CountAsync(item =>
            item.WorkspaceMembershipId == pair.TargetMembershipId)).Should().Be(2,
            "adding one job must not replace the member's existing independently scoped job");
    }

    [SkippableFact]
    public async Task TeamMutation_StaleActorRevisionCannotChangeCurrentTarget()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"stale-actor-{Guid.NewGuid():N}");
        await using (var db = NewContext())
        {
            var actor = await db.WorkspaceAccessContexts.SingleAsync(item => item.Id == pair.ActorContextId);
            actor.AdvanceRevision(1);
            actor.UpdatedAtUtc = _now.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        var act = async () => await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.stale-actor"), TeamAddCommand(pair, 1, _managerPropertyId), TeamMutationCodec);
        await act.Should().ThrowAsync<StaleAccessRevisionException>();

        await using var verification = NewContext();
        (await verification.WorkspaceAccessContexts.Where(item => item.Id == pair.TargetContextId)
            .Select(item => item.AccessRevision).SingleAsync()).Should().Be(1);
        (await verification.MembershipRoleAssignments.CountAsync(item =>
            item.WorkspaceMembershipId == pair.TargetMembershipId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task TeamAdministrator_CannotAddAssignmentToOwnAccessContext()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"self-expand-{Guid.NewGuid():N}");
        var command = new AddWorkspaceRoleAssignmentCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            pair.ActorContextId, 1, RoleProfileKeys.PropertyManager,
            MembershipRoleAssignmentScopeKind.SelectedProperties, [_managerPropertyId], _now);

        var act = async () => await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.self-expand"), command, TeamMutationCodec);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*cannot change their own role, scope, or membership status*");
        await using var verification = NewContext();
        (await verification.WorkspaceAccessContexts
            .Where(context => context.Id == pair.ActorContextId)
            .Select(context => context.AccessRevision)
            .SingleAsync()).Should().Be(1);
        (await verification.MembershipRoleAssignments.CountAsync(assignment =>
            assignment.WorkspaceMembership!.AccessContextId == pair.ActorContextId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task TeamMutation_StaleTargetRevisionCannotOverwriteWinningChange()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"stale-target-{Guid.NewGuid():N}");
        await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.target-winner"), TeamAddCommand(pair, 1, _managerPropertyId), TeamMutationCodec);

        var stale = async () => await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.target-stale"), TeamAddCommand(pair, 1, _unscopedPropertyId), TeamMutationCodec);
        await stale.Should().ThrowAsync<StaleAccessRevisionException>();

        await using var db = NewContext();
        (await db.WorkspaceAccessContexts.Where(item => item.Id == pair.TargetContextId)
            .Select(item => item.AccessRevision).SingleAsync()).Should().Be(2);
        var managerPropertyIds = await db.MembershipRoleAssignmentProperties
            .Where(scope => scope.MembershipRoleAssignment!.WorkspaceMembershipId == pair.TargetMembershipId &&
                            scope.MembershipRoleAssignment.RoleProfileId == 2)
            .Select(scope => scope.PropertyId)
            .ToListAsync();
        managerPropertyIds.Should().Equal(_managerPropertyId);
    }

    [SkippableFact]
    public async Task PropertyScopeReplacement_CrossWorkspaceDecoyRollsBackScopeAndRevision()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"decoy-{Guid.NewGuid():N}");
        var command = new ReplaceWorkspaceAssignmentPropertyScopeCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            pair.TargetContextId, 1, pair.TargetAssignmentId, [_otherWorkspacePropertyId]);

        var act = async () => await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.decoy-scope"), command, TeamMutationCodec);
        await act.Should().ThrowAsync<DomainValidationException>();

        await using var db = NewContext();
        (await db.WorkspaceAccessContexts.Where(item => item.Id == pair.TargetContextId)
            .Select(item => item.AccessRevision).SingleAsync()).Should().Be(1);
        var propertyIds = await db.MembershipRoleAssignmentProperties
            .Where(scope => scope.MembershipRoleAssignmentId == pair.TargetAssignmentId)
            .Select(scope => scope.PropertyId)
            .ToListAsync();
        propertyIds.Should().Equal(_leasingPropertyId);
    }

    [SkippableFact]
    public async Task RevokingMembership_InvalidatesTargetSessionImmediately()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"revoke-{Guid.NewGuid():N}");
        var command = new ChangeWorkspaceMembershipStatusCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            pair.TargetContextId, 1, WorkspaceMembershipStatusAction.Revoke);

        var result = await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.revoke"), command, TeamMutationCodec);
        result.Value.AccessRevision.Should().Be(2);
        result.Value.ContextStatus.Should().Be(WorkspaceAccessContextStatus.Revoked);

        await using var db = NewContext();
        var resolve = async () => await new ActiveAccessContextResolver(db).ResolveAsync(
            pair.TargetSessionId, pair.TargetUserId, pair.TargetContextId, 2, _now.AddMinutes(1));
        await resolve.Should().ThrowAsync<AccessContextUnavailableException>();
    }

    [SkippableFact]
    public async Task SuspendedMembership_CanBeReactivatedWithAnotherFencedRevision()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"reactivate-{Guid.NewGuid():N}");
        var suspend = new ChangeWorkspaceMembershipStatusCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            pair.TargetContextId, 1, WorkspaceMembershipStatusAction.Suspend);
        await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.suspend"), suspend, TeamMutationCodec);

        var reactivate = suspend with
        {
            ExpectedRevision = 2,
            Action = WorkspaceMembershipStatusAction.Reactivate,
        };
        var result = await AtomicUnitOfWork.ExecuteAsync(
            Identity("test.team.reactivate"), reactivate, TeamMutationCodec);

        result.Value.AccessRevision.Should().Be(3);
        result.Value.ContextStatus.Should().Be(WorkspaceAccessContextStatus.Active);
        result.Value.MembershipStatus.Should().Be(WorkspaceMembershipStatus.Active);
        await using var db = NewContext();
        (await new ActiveAccessContextResolver(db).ResolveAsync(
            pair.TargetSessionId, pair.TargetUserId, pair.TargetContextId, 3, _now.AddMinutes(2)))
            .AccessContextId.Should().Be(pair.TargetContextId);
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
        first.ConsumedByOperationId = Guid.NewGuid();
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

        var leasingUnit = new Unit
        {
            PortfolioId = workspace.Id,
            Property = leasingProperty,
            UnitNumber = "Lease-1",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var managerUnit = new Unit
        {
            PortfolioId = workspace.Id,
            Property = managerProperty,
            UnitNumber = "Manage-1",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var leasingApplication = new RentalApplication
        {
            PortfolioId = workspace.Id,
            Property = leasingProperty,
            Unit = leasingUnit,
            FirstName = "Leasing",
            LastName = "Applicant",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = _now,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var managerApplication = new RentalApplication
        {
            PortfolioId = workspace.Id,
            Property = managerProperty,
            Unit = managerUnit,
            FirstName = "Manager",
            LastName = "Applicant",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = _now,
            CreatedAt = _now,
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
            ExpiresAtUtc = _now.AddDays(30),
        };
        db.AddRange(
            leasingAssignment,
            managerAssignment,
            technicianAssignment,
            workOrder,
            leasingUnit,
            managerUnit,
            leasingApplication,
            managerApplication,
            session);
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
        _leasingUnitId = leasingUnit.Id;
        _managerUnitId = managerUnit.Id;
        _leasingApplicationId = leasingApplication.Id;
        _managerApplicationId = managerApplication.Id;
    }

    private async Task<TeamAuthorityPair> SeedTeamAuthorityPairAsync(string suffix)
    {
        await using var db = NewContext();
        var actorUser = new ApplicationUser
        {
            UserName = $"team-admin-{suffix}@example.test",
            NormalizedUserName = $"TEAM-ADMIN-{suffix}@EXAMPLE.TEST".ToUpperInvariant(),
            Email = $"team-admin-{suffix}@example.test",
            NormalizedEmail = $"TEAM-ADMIN-{suffix}@EXAMPLE.TEST".ToUpperInvariant(),
            DisplayName = "Team Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var targetUser = new ApplicationUser
        {
            UserName = $"team-target-{suffix}@example.test",
            NormalizedUserName = $"TEAM-TARGET-{suffix}@EXAMPLE.TEST".ToUpperInvariant(),
            Email = $"team-target-{suffix}@example.test",
            NormalizedEmail = $"TEAM-TARGET-{suffix}@EXAMPLE.TEST".ToUpperInvariant(),
            DisplayName = "Team Target",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        db.AddRange(actorUser, targetUser);
        await db.SaveChangesAsync();

        var actorContext = new WorkspaceAccessContext
        {
            UserId = actorUser.Id,
            PortfolioId = _portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var actorMembership = new WorkspaceMembership
        {
            AccessContext = actorContext,
            PortfolioId = _portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var actorAssignment = Assignment(0, _portfolioId, 1,
            MembershipRoleAssignmentScopeKind.AllProperties);
        actorAssignment.WorkspaceMembership = actorMembership;

        var targetContext = new WorkspaceAccessContext
        {
            UserId = targetUser.Id,
            PortfolioId = _portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var targetMembership = new WorkspaceMembership
        {
            AccessContext = targetContext,
            PortfolioId = _portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Leasing,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var targetAssignment = Assignment(0, _portfolioId, 3,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        targetAssignment.WorkspaceMembership = targetMembership;
        targetAssignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PropertyId = _leasingPropertyId,
            PortfolioId = _portfolioId,
        });
        db.AddRange(actorAssignment, targetAssignment);
        await db.SaveChangesAsync();

        var actorSession = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = actorUser.Id,
            ActiveAccessContextId = actorContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now.AddMinutes(-5),
            LastSeenAtUtc = _now,
            ExpiresAtUtc = _now.AddDays(30),
        };
        var targetSession = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = targetUser.Id,
            ActiveAccessContextId = targetContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now.AddMinutes(-5),
            LastSeenAtUtc = _now,
            ExpiresAtUtc = _now.AddDays(30),
        };
        db.AddRange(actorSession, targetSession);
        await db.SaveChangesAsync();
        return new TeamAuthorityPair(
            _portfolioId,
            actorUser.Id,
            actorContext.Id,
            actorSession.Id,
            targetUser.Id,
            targetContext.Id,
            targetMembership.Id,
            targetAssignment.Id,
            targetSession.Id);
    }

    private AddWorkspaceRoleAssignmentCommand TeamAddCommand(
        TeamAuthorityPair pair,
        long expectedTargetRevision,
        int propertyId) => new(
        pair.PortfolioId,
        pair.ActorUserId,
        pair.ActorSessionId,
        pair.ActorContextId,
        1,
        pair.TargetContextId,
        expectedTargetRevision,
        RoleProfileKeys.PropertyManager,
        MembershipRoleAssignmentScopeKind.SelectedProperties,
        [propertyId],
        _now);

    private sealed record TeamAuthorityPair(
        int PortfolioId,
        int ActorUserId,
        int ActorContextId,
        Guid ActorSessionId,
        int TargetUserId,
        int TargetContextId,
        int TargetMembershipId,
        int TargetAssignmentId,
        Guid TargetSessionId);

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

    private CreateLoanRequest ValidLoanRequest(int propertyId) => new()
    {
        PropertyId = propertyId,
        Lender = "Atomic test lender",
        OriginalAmount = 200_000m,
        CurrentBalance = 200_000m,
        AnnualInterestRatePct = 6m,
        TermMonths = 360,
        StartDate = _now,
        DayOfMonthDue = 1,
        MonthlyPrincipalInterest = 1_200m,
        Status = LoanStatus.Active,
    };

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
