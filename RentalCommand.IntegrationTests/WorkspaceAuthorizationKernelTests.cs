using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Auth;
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
    private const string ApiPassword = "workspace-authorization-api-test-password";
    private static readonly AtomicJsonResultCodec<WorkspaceAccessMutationResult> MutationCodec =
        new("workspace-access-mutation-result.v1");
    private static readonly AtomicJsonResultCodec<CreateWorkspaceMembershipResult> TeamCreateCodec =
        new("workspace-team.membership.create.v1");
    private static readonly AtomicJsonResultCodec<WorkspaceTeamMutationResult> TeamMutationCodec =
        new("workspace-team.mutation.v1");
    private static readonly AtomicJsonResultCodec<StartAuthSessionResult> StartSessionCodec =
        new("auth-session-start-result:v1");
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private string _apiConnectionString = string.Empty;
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
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER ROLE rentalcommand_api PASSWORD '{ApiPassword}';");
        await SeedKernelAsync(db);
        _apiConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = "rentalcommand_api",
            Password = ApiPassword,
            Pooling = false,
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, AccessTestActor>();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicMoneyMutationCommand,
            AtomicMoneyMutationResult,
            AtomicMoneyMutationHandler>();
        services.AddAtomicCommandHandler<
            CreateWorkspaceMembershipCommand,
            CreateWorkspaceMembershipResult,
            CreateWorkspaceMembershipHandler>();
        services.AddAtomicCommandHandler<
            StartAuthSessionCommand,
            StartAuthSessionResult,
            StartAuthSessionHandler>();
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
        await using var db = await NewAuthorizationQueryContextAsync();
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
                .ListAsync(_userId, null))
            .Single(item => item.AccessContextId == _accessContextId);

        option.DefaultExperience.Should().Be(
            WorkspaceExperience.Leasing,
            "the envelope SQL ordering chooses Leasing before Maintenance when Management is unavailable");
        await transaction.RollbackAsync();
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
    public async Task AssignedWorkOrdersScope_FailsClosedUntilResponsibilityJoinExists()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);
        var technicianAssignmentId = await db.MembershipRoleAssignments
            .Where(item => item.WorkspaceMembershipId == _membershipId && item.RoleProfileId == 4)
            .Select(item => item.Id)
            .SingleAsync();

        db.WorkOrderResponsibilities.Add(new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = _portfolioId,
            PropertyId = _managerPropertyId,
            WorkOrderId = _managerWorkOrderId,
            WorkspaceMembershipId = _membershipId,
            MembershipRoleAssignmentId = technicianAssignmentId,
            Kind = WorkOrderResponsibilityKind.Primary,
            EffectiveFromUtc = _now.AddHours(1),
            AssignedByUserId = _userId,
            AssignedByAccessContextId = _accessContextId,
            AssignedReason = "Clock boundary proof",
            AssignedAtUtc = _now.AddHours(1),
        });
        await db.SaveChangesAsync();

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
        sql.Should().Contain("rc_api_effective_capability_scopes");
        sql.Should().NotContain("AuthSessions");
        sql.Should().NotContain("RoleProfileCapabilities");
        sql.Should().NotContain("MembershipRoleAssignmentProperties");
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
            sql.Should().Contain("rc_api_effective_capability_scopes");
            sql.Should().NotContain("AuthSessions");
            sql.Should().NotContain("RoleProfileCapabilities");
            sql.Should().NotContain("MembershipRoleAssignmentProperties");
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
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "stale-test",
            ValidLoanRequest(_managerPropertyId), _now);
        var identity = AtomicMoneyMutation.Identity(command);

        var act = () => ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec);
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
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "cross-scope-test",
            ValidLoanRequest(_unscopedPropertyId), _now);
        var identity = AtomicMoneyMutation.Identity(command);

        var outcome = await ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec);
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
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "rollback-test", request, _now);
        var identity = AtomicMoneyMutation.Identity(command);

        var act = () => ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec);
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
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "stable-loan-create", request, _now);
        var identity = AtomicMoneyMutation.Identity(command);

        var first = await ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec);
        var replay = await ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        var changedRequest = ValidLoanRequest(_managerPropertyId);
        changedRequest.Lender = "Different lender";
        var changedCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "stable-loan-create",
            changedRequest, _now);
        var conflictAct = async () => await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(changedCommand), changedCommand, AtomicMoneyMutation.Codec);

        await conflictAct.Should().ThrowAsync<AtomicIdempotencyConflictException>();

        await using var verify = NewContext();
        (await verify.Loans.AsNoTracking().CountAsync(row =>
            row.Id == first.Value.EntityId && row.PortfolioId == _portfolioId)).Should().Be(1);
        (await verify.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_ManualLoanCreateUsesSimulationBusinessDateForDebtServiceBoundary()
    {
        SkipIfNoDocker();
        var simulatedBusinessDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        await using (var setup = NewContext())
        {
            var clock = await setup.SimulationClocks.SingleAsync(row => row.Id == 1);
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = simulatedBusinessDate;
            clock.RealAnchorUtc = _now;
            clock.TimeZoneId = "UTC";
            clock.UpdatedAtRealUtc = _now;
            await setup.SaveChangesAsync();
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var request = ValidLoanRequest(_managerPropertyId);
        request.StartDate = new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        request.CurrentBalance = 125_825m;
        request.DayOfMonthDue = 12;
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "simulated-loan-create",
            request, simulatedBusinessDate);
        var identity = AtomicMoneyMutation.Identity(command);

        var result = await ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec);

        result.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var verify = NewContext();
        var loan = await verify.Loans.AsNoTracking()
            .SingleAsync(row => row.Id == result.Value.EntityId && row.PortfolioId == _portfolioId);
        loan.StartDate.Should().Be(request.StartDate);
        loan.CurrentBalance.Should().Be(request.CurrentBalance);
        loan.DebtServiceAutomationStartDate.Should().Be(simulatedBusinessDate);
        loan.CreatedAt.Should().Be(simulatedBusinessDate);
        loan.UpdatedAt.Should().Be(simulatedBusinessDate);
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_PostLoanPayment_IsIdempotentAndRollsBackEveryRowOnFailure()
    {
        SkipIfNoDocker();
        int loanId;
        int firstPaymentId;
        int secondPaymentId;
        await using (var seed = NewContext())
        {
            var loan = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = _managerPropertyId,
                Lender = "Posting proof lender",
                OriginalAmount = 200_000m,
                CurrentBalance = 200_000m,
                AnnualInterestRatePct = 6m,
                TermMonths = 360,
                StartDate = _now,
                DayOfMonthDue = 1,
                MonthlyPrincipalInterest = 1_200m,
                MonthlyEscrow = 300m,
                Status = LoanStatus.Active,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            seed.Loans.Add(loan);
            await seed.SaveChangesAsync();
            var first = new LoanPayment
            {
                PortfolioId = _portfolioId,
                LoanId = loan.Id,
                PeriodKey = "2027-01",
                DueDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 1_000m,
                PrincipalAmount = 200m,
                EscrowAmount = 300m,
                TotalAmount = 1_500m,
                BalanceAfter = 199_800m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = _now,
            };
            var second = new LoanPayment
            {
                PortfolioId = _portfolioId,
                LoanId = loan.Id,
                PeriodKey = "2027-02",
                DueDate = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 999m,
                PrincipalAmount = 201m,
                EscrowAmount = 300m,
                TotalAmount = 1_500m,
                BalanceAfter = 199_599m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = _now,
            };
            seed.LoanPayments.AddRange(first, second);
            await seed.SaveChangesAsync();
            loanId = loan.Id;
            firstPaymentId = first.Id;
            secondPaymentId = second.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var sourceUpdateCommand = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan,
            AtomicMoneyOperation.Update,
            loanId,
            "loan-source-due-day-and-escrow",
            new UpdateLoanRequest
            {
                DayOfMonthDue = 20,
                MonthlyEscrow = 318m,
            },
            _now.AddHours(1));
        var sourceUpdateIdentity = AtomicMoneyMutation.Identity(sourceUpdateCommand);
        var sourceUpdateOutboxKey =
            $"money:{_portfolioId}:{_accessContextId}:{AtomicMoneyDomain.Loan}:" +
            $"{AtomicMoneyOperation.Update}:{nameof(Loan)}:{loanId}:" +
            $"{sourceUpdateCommand.IdempotencyKey}:data-update";

        var sourceUpdate = await ExecuteAtomicAsync(
            sourceUpdateIdentity, sourceUpdateCommand, AtomicMoneyMutation.Codec);
        sourceUpdate.Disposition.Should().Be(AtomicCommandDisposition.Executed);

        DateTime sourceUpdatedAt;
        await using (var verify = NewContext())
        {
            var source = await verify.Loans.AsNoTracking()
                .Where(row => row.Id == loanId)
                .Select(row => new
                {
                    row.DayOfMonthDue,
                    row.MonthlyEscrow,
                    row.UpdatedAt,
                })
                .SingleAsync();
            source.DayOfMonthDue.Should().Be(20);
            source.MonthlyEscrow.Should().Be(318m);
            sourceUpdatedAt = source.UpdatedAt;
            (await verify.Loans.CountAsync(row => row.Id == loanId)).Should().Be(1);
            var receipt = await verify.AtomicCommandReceipts.AsNoTracking()
                .SingleAsync(row =>
                    row.CommandType == sourceUpdateIdentity.CommandType &&
                    row.IdempotencyKey == sourceUpdateIdentity.IdempotencyKey);
            receipt.AttemptId.Should().Be(sourceUpdate.AttemptId);
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == sourceUpdateIdentity.CommandType &&
                row.CommandIdempotencyKey == sourceUpdateIdentity.IdempotencyKey &&
                row.EntityType == nameof(Loan) &&
                row.EntityId == loanId)).Should().Be(1);
            (await verify.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == sourceUpdateOutboxKey &&
                row.MessageType == "data-update")).Should().Be(1);
        }

        var sourceUpdateReplay = await ExecuteAtomicAsync(
            sourceUpdateIdentity, sourceUpdateCommand, AtomicMoneyMutation.Codec);
        sourceUpdateReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        sourceUpdateReplay.Value.Should().Be(sourceUpdate.Value);
        sourceUpdateReplay.AttemptId.Should().Be(sourceUpdate.AttemptId);

        await using (var verify = NewContext())
        {
            var source = await verify.Loans.AsNoTracking()
                .Where(row => row.Id == loanId)
                .Select(row => new
                {
                    row.DayOfMonthDue,
                    row.MonthlyEscrow,
                    row.UpdatedAt,
                })
                .SingleAsync();
            source.DayOfMonthDue.Should().Be(20);
            source.MonthlyEscrow.Should().Be(318m);
            source.UpdatedAt.Should().Be(sourceUpdatedAt);
            (await verify.Loans.CountAsync(row => row.Id == loanId)).Should().Be(1);
            (await verify.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == sourceUpdateIdentity.CommandType &&
                row.IdempotencyKey == sourceUpdateIdentity.IdempotencyKey)).Should().Be(1);
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == sourceUpdateIdentity.CommandType &&
                row.CommandIdempotencyKey == sourceUpdateIdentity.IdempotencyKey &&
                row.EntityType == nameof(Loan) &&
                row.EntityId == loanId)).Should().Be(1);
            (await verify.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == sourceUpdateOutboxKey &&
                row.MessageType == "data-update")).Should().Be(1);
        }

        var paidDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.PostPayment, firstPaymentId,
            "post-loan-payment-once",
            new PostLoanPaymentRequest { LoanId = loanId, PaidDate = paidDate }, _now);
        var identity = AtomicMoneyMutation.Identity(command);

        var firstOutcome = await ExecuteAtomicAsync(
            identity, command, AtomicMoneyMutation.Codec);
        var replay = await ExecuteAtomicAsync(
            identity, command, AtomicMoneyMutation.Codec);

        firstOutcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(firstOutcome.Value);
        await using (var verify = NewContext())
        {
            var original = await verify.LoanPayments.AsNoTracking()
                .SingleAsync(payment => payment.Id == firstPaymentId);
            original.Status.Should().Be(LoanPaymentStatus.Scheduled);
            original.PaidDate.Should().BeNull();
            var correction = await verify.LoanPaymentCorrections.AsNoTracking()
                .SingleAsync(row => row.LoanPaymentId == firstPaymentId);
            correction.AttemptId.Should().Be(firstOutcome.AttemptId);
            correction.Status.Should().Be(LoanPaymentStatus.Paid);
            correction.PaidDate.Should().Be(paidDate);
            correction.BalanceAfter.Should().Be(199_800m);
            (await verify.Loans.Where(row => row.Id == loanId)
                .Select(row => row.CurrentBalance)
                .SingleAsync()).Should().Be(199_800m);
            (await verify.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == identity.CommandType &&
                receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
            (await verify.AtomicAuditLogs.CountAsync(audit =>
                audit.AttemptId == firstOutcome.AttemptId)).Should().Be(3);
            (await verify.OutboxMessages.CountAsync(message =>
                message.IdempotencyKey.EndsWith(
                    $":{command.IdempotencyKey}:data-update"))).Should().Be(2);
        }

        await using (var inject = NewContext())
        {
            await inject.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION fail_loan_payment_correction_insert() RETURNS trigger AS $$
                BEGIN
                    IF NEW."LoanPaymentId" = {secondPaymentId} THEN
                        RAISE EXCEPTION 'injected loan payment correction failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_loan_payment_correction_insert
                BEFORE INSERT ON "LoanPaymentCorrections"
                FOR EACH ROW EXECUTE FUNCTION fail_loan_payment_correction_insert();
                """);
        }

        var failedCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.PostPayment, secondPaymentId,
            "post-loan-payment-failure", new PostLoanPaymentRequest
            {
                LoanId = loanId,
                PaidDate = paidDate.AddMonths(1),
            }, _now);
        var failedIdentity = AtomicMoneyMutation.Identity(failedCommand);
        try
        {
            var act = () => ExecuteAtomicAsync(
                failedIdentity, failedCommand, AtomicMoneyMutation.Codec);
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            await using var cleanup = NewContext();
            await cleanup.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS fail_loan_payment_correction_insert ON "LoanPaymentCorrections";
                DROP FUNCTION IF EXISTS fail_loan_payment_correction_insert();
                """);
        }

        await using (var verify = NewContext())
        {
            var state = await verify.LoanPayments
                .Where(payment => payment.Id == secondPaymentId)
                .Select(payment => new
                {
                    payment.Status,
                    payment.PaidDate,
                    payment.Loan!.CurrentBalance,
                })
                .SingleAsync();
            state.Status.Should().Be(LoanPaymentStatus.Scheduled);
            state.PaidDate.Should().BeNull();
            state.CurrentBalance.Should().Be(199_800m);
            (await verify.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == failedIdentity.CommandType &&
                receipt.IdempotencyKey == failedIdentity.IdempotencyKey)).Should().Be(0);
            (await verify.LoanPaymentCorrections.CountAsync(correction =>
                correction.LoanPaymentId == secondPaymentId)).Should().Be(0);
            (await verify.AtomicAuditLogs.CountAsync(audit =>
                audit.CommandType == failedIdentity.CommandType &&
                audit.CommandIdempotencyKey == failedIdentity.IdempotencyKey)).Should().Be(0);
            (await verify.OutboxMessages.CountAsync(message =>
                message.IdempotencyKey.EndsWith(
                    $":{failedCommand.IdempotencyKey}:data-update"))).Should().Be(0);
        }
    }

    [SkippableFact]
    public async Task CapitalizePaidExpenseReversesExpenseAndPostsOneCapitalPurchase()
    {
        SkipIfNoDocker();
        int expenseId;
        await using (var seed = NewContext())
        {
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = _managerPropertyId,
                Category = ScheduleECategory.Repairs,
                Description = "Replace roof with capital improvement",
                Status = ExpenseStatus.Paid,
                Amount = 8_500m,
                IncurredAt = new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                PaidAt = new DateTime(2027, 1, 11, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            seed.Expenses.Add(expense);
            await seed.SaveChangesAsync();
            await CommitDirectPostingAsync(seed, (context, ct) =>
                MoneyAccountingPosting.PostExpenseOccurrenceAsync(
                    seed, context, expense, _userId, ct),
                "capitalize-paid-expense-seed");
            expenseId = expense.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var command = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.CapitalizeExpense,
            expenseId,
            "capitalize-paid-expense",
            new CapitalizeExpenseRequest
            {
                InServiceDate = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc),
                Description = "Roof replacement",
            },
            _now.AddHours(1));
        var result = await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);
        var replay = await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);

        result.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(result.Value);
        await using var verify = NewContext();
        var asset = await verify.CapitalAssets.AsNoTracking()
            .SingleAsync(row => row.SourceExpenseId == expenseId);
        asset.CostBasis.Should().Be(8_500m);
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.CapitalPurchase
            && entry.SourceId == asset.Id)).Should().Be(1);
        var expenseLines = await verify.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == _portfolioId
                && line.LedgerAccount!.SystemKey == "repairs-and-maintenance"
                && line.SourceLineId == expenseId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Debit = group.Sum(line => line.DebitAmount),
                Credit = group.Sum(line => line.CreditAmount),
            })
            .SingleAsync();
        expenseLines.Debit.Should().Be(expenseLines.Credit);
        var cashLines = await verify.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == _portfolioId
                && line.LedgerAccount!.SystemKey == "operating-cash"
                && (line.JournalEntry.SourceType == JournalSourceType.ExpensePayment
                    || line.JournalEntry.SourceType == JournalSourceType.CapitalPurchase)
                && (line.SourceLineId == expenseId || line.SourceLineId == asset.Id))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Debit = group.Sum(line => line.DebitAmount),
                Credit = group.Sum(line => line.CreditAmount),
            })
            .SingleAsync();
        (cashLines.Credit - cashLines.Debit).Should().Be(8_500m);
    }

    [SkippableFact]
    public async Task CapitalizeExpenseJournalFailureRollsBackAssetLinkJournalAndCompanions()
    {
        SkipIfNoDocker();
        int expenseId;
        await using (var seed = NewContext())
        {
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = _managerPropertyId,
                Category = ScheduleECategory.Repairs,
                Description = "Capitalization failure expense",
                Status = ExpenseStatus.Paid,
                Amount = 6_400m,
                IncurredAt = new DateTime(2027, 1, 16, 0, 0, 0, DateTimeKind.Utc),
                PaidAt = new DateTime(2027, 1, 16, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            seed.Expenses.Add(expense);
            await seed.SaveChangesAsync();
            expenseId = expense.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var command = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.CapitalizeExpense,
            expenseId,
            "capitalize-expense-journal-failure",
            new CapitalizeExpenseRequest
            {
                InServiceDate = new DateTime(2027, 1, 17, 0, 0, 0, DateTimeKind.Utc),
                Description = "Failed roof capitalization",
            },
            _now.AddHours(1));
        var identity = AtomicMoneyMutation.Identity(command);

        await using (var inject = NewContext())
        {
            await inject.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION fail_capital_purchase_insert() RETURNS trigger AS $$
                BEGIN
                    IF NEW."SourceType" = {(int)JournalSourceType.CapitalPurchase} THEN
                        RAISE EXCEPTION 'injected capital purchase failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_capital_purchase_insert
                BEFORE INSERT ON "JournalEntries"
                FOR EACH ROW EXECUTE FUNCTION fail_capital_purchase_insert();
                """);
        }

        try
        {
            await FluentActions.Invoking(() =>
                    ExecuteAtomicAsync(identity, command, AtomicMoneyMutation.Codec))
                .Should().ThrowAsync<Exception>();
        }
        finally
        {
            await using var cleanup = NewContext();
            await cleanup.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS fail_capital_purchase_insert ON "JournalEntries";
                DROP FUNCTION IF EXISTS fail_capital_purchase_insert();
                """);
        }

        await using var verify = NewContext();
        (await verify.Expenses.CountAsync(row => row.Id == expenseId
            && row.CapitalizedAssetId == null)).Should().Be(1);
        (await verify.CapitalAssets.CountAsync(row => row.SourceExpenseId == expenseId)).Should().Be(0);
        (await verify.JournalEntries.CountAsync(row => row.PortfolioId == _portfolioId
            && row.SourceType == JournalSourceType.CapitalPurchase)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.EndsWith($":{command.IdempotencyKey}:data-update"))).Should().Be(0);
    }

    [SkippableFact]
    public async Task CapitalAssetFactUpdateReversesAndRepostsWhileDescriptionOnlyUpdateDoesNotPost()
    {
        SkipIfNoDocker();
        int assetId;
        await using (var seed = NewContext())
        {
            var asset = new CapitalAsset
            {
                PortfolioId = _portfolioId,
                PropertyId = _managerPropertyId,
                UnitId = _managerUnitId,
                Description = "Original asset",
                CostBasis = 700m,
                InServiceDate = new DateTime(2027, 1, 13, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            seed.CapitalAssets.Add(asset);
            await seed.SaveChangesAsync();
            await CommitDirectPostingAsync(seed, (context, ct) =>
                MoneyAccountingPosting.PostCapitalPurchaseAsync(
                    seed, context, asset, _userId, ct),
                "capital-asset-update-seed");
            assetId = asset.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var dimensionOnly = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.Update,
            assetId,
            "capital-asset-dimension-only",
            new UpdateCapitalAssetRequest { ClearUnit = true },
            _now.AddMinutes(30));
        await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(dimensionOnly), dimensionOnly, AtomicMoneyMutation.Codec);
        await using (var afterDimension = NewContext())
        {
            (await afterDimension.JournalEntries.CountAsync(entry =>
                entry.PortfolioId == _portfolioId
                && entry.SourceType == JournalSourceType.CapitalPurchase
                && entry.Lines.Any(line => line.SourceLineId == assetId))).Should().Be(3);
            var activeDimensionUnitIds = await afterDimension.JournalLines
                .Where(line => line.JournalEntry!.PortfolioId == _portfolioId
                    && line.JournalEntry.SourceType == JournalSourceType.CapitalPurchase
                    && line.JournalEntry.ReversesJournalEntryId == null
                    && !afterDimension.JournalEntries.Any(reversal =>
                        reversal.ReversesJournalEntryId == line.JournalEntryId)
                    && line.SourceLineId == assetId)
                .Select(line => line.UnitId)
                .ToListAsync();
            activeDimensionUnitIds.Should().OnlyContain(unitId => unitId == null);
        }

        var descriptionOnly = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.Update,
            assetId,
            "capital-asset-description-only",
            new UpdateCapitalAssetRequest { Description = "Descriptive edit" },
            _now.AddHours(1));
        await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(descriptionOnly), descriptionOnly, AtomicMoneyMutation.Codec);
        await using (var afterDescription = NewContext())
        {
            (await afterDescription.JournalEntries.CountAsync(entry =>
                entry.PortfolioId == _portfolioId
                && entry.SourceType == JournalSourceType.CapitalPurchase
                && entry.Lines.Any(line => line.SourceLineId == assetId))).Should().Be(3);
        }

        var factUpdate = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.Update,
            assetId,
            "capital-asset-cost-correction",
            new UpdateCapitalAssetRequest { CostBasis = 850m },
            _now.AddHours(2));
        await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(factUpdate), factUpdate, AtomicMoneyMutation.Codec);

        await using var verify = NewContext();
        var entries = await verify.JournalEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == _portfolioId
                && entry.SourceType == JournalSourceType.CapitalPurchase
                && entry.Lines.Any(line => line.SourceLineId == assetId))
            .OrderBy(entry => entry.Id)
            .ToListAsync();
        entries.Should().HaveCount(5);
        var original = entries.Single(entry => entry.SourceId == assetId
            && entry.ReversesJournalEntryId is null);
        entries.Should().Contain(entry => entry.ReversesJournalEntryId == original.Id);
        entries.Should().Contain(entry => entry.SourceId != assetId
            && entry.ReversesJournalEntryId == null);
    }

    [SkippableFact]
    public async Task CapitalAssetDateUpdateReversesAndRepostsCapitalPurchase()
    {
        SkipIfNoDocker();
        int assetId;
        await using (var seed = NewContext())
        {
            var asset = new CapitalAsset
            {
                PortfolioId = _portfolioId,
                PropertyId = _managerPropertyId,
                Description = "Date-corrected asset",
                CostBasis = 725m,
                InServiceDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            seed.CapitalAssets.Add(asset);
            await seed.SaveChangesAsync();
            await CommitDirectPostingAsync(seed, (context, ct) =>
                MoneyAccountingPosting.PostCapitalPurchaseAsync(
                    seed, context, asset, _userId, ct),
                "capital-asset-date-seed");
            assetId = asset.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var command = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.Update,
            assetId,
            "capital-asset-date-correction",
            new UpdateCapitalAssetRequest
            {
                InServiceDate = new DateTime(2027, 1, 18, 0, 0, 0, DateTimeKind.Utc),
            },
            _now.AddHours(1));
        await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);

        await using var verify = NewContext();
        var entries = await verify.JournalEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == _portfolioId
                && entry.SourceType == JournalSourceType.CapitalPurchase
                && entry.Lines.Any(line => line.SourceLineId == assetId))
            .ToListAsync();
        entries.Should().HaveCount(3);
        var original = entries.Single(entry => entry.SourceId == assetId
            && entry.ReversesJournalEntryId is null);
        entries.Should().Contain(entry => entry.ReversesJournalEntryId == original.Id);
        entries.Should().Contain(entry => entry.SourceId != assetId
            && entry.ReversesJournalEntryId == null);
    }

    [SkippableFact]
    public async Task CapitalAssetDeleteReversesPostedCapitalPurchaseBeforeSoftDelete()
    {
        SkipIfNoDocker();
        int assetId;
        await using (var seed = NewContext())
        {
            var asset = new CapitalAsset
            {
                PortfolioId = _portfolioId,
                PropertyId = _managerPropertyId,
                Description = "Asset to delete",
                CostBasis = 900m,
                InServiceDate = new DateTime(2027, 1, 14, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            seed.CapitalAssets.Add(asset);
            await seed.SaveChangesAsync();
            await CommitDirectPostingAsync(seed, (context, ct) =>
                MoneyAccountingPosting.PostCapitalPurchaseAsync(
                    seed, context, asset, _userId, ct),
                "capital-asset-delete-seed");
            assetId = asset.Id;
        }

        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var command = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset,
            AtomicMoneyOperation.Delete,
            assetId,
            "capital-asset-delete",
            new object(),
            _now.AddHours(1));
        await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);

        await using var verify = NewContext();
        (await verify.CapitalAssets.CountAsync(asset => asset.Id == assetId)).Should().Be(0);
        var entries = await verify.JournalEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == _portfolioId
                && entry.SourceType == JournalSourceType.CapitalPurchase
                && entry.Lines.Any(line => line.SourceLineId == assetId))
            .ToListAsync();
        entries.Should().HaveCount(2);
        var original = entries.Single(entry => entry.SourceId == assetId
            && entry.ReversesJournalEntryId is null);
        var reversal = entries.Single(entry => entry.ReversesJournalEntryId == original.Id);
        var originalLines = await verify.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntryId == original.Id)
            .Select(line => new { line.LedgerAccountId, line.DebitAmount, line.CreditAmount })
            .ToListAsync();
        var reversalLines = await verify.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntryId == reversal.Id)
            .Select(line => new { line.LedgerAccountId, line.DebitAmount, line.CreditAmount })
            .ToListAsync();
        reversalLines.Should().HaveSameCount(originalLines);
        foreach (var originalLine in originalLines)
        {
            reversalLines.Should().Contain(reversalLine =>
                reversalLine.LedgerAccountId == originalLine.LedgerAccountId
                && reversalLine.DebitAmount == originalLine.CreditAmount
                && reversalLine.CreditAmount == originalLine.DebitAmount);
        }
    }

    [SkippableFact]
    public async Task AtomicMoneyMutation_DeleteReplaysAfterTheBusinessRowIsSoftDeleted()
    {
        SkipIfNoDocker();
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var createCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Create, 0, "loan-before-delete",
            ValidLoanRequest(_managerPropertyId), _now);
        var created = await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(createCommand), createCommand, AtomicMoneyMutation.Codec);
        var deleteCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Loan, AtomicMoneyOperation.Delete, created.Value.EntityId,
            "stable-loan-delete", new object(), _now);
        var deleteIdentity = AtomicMoneyMutation.Identity(deleteCommand);

        var firstDelete = await ExecuteAtomicAsync(
            deleteIdentity, deleteCommand, AtomicMoneyMutation.Codec);
        var replayedDelete = await ExecuteAtomicAsync(
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
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Create, 0, "effective-expense-create",
            createRequest, _now);
        var created = await ExecuteAtomicAsync(
            AtomicMoneyMutation.Identity(createCommand), createCommand, AtomicMoneyMutation.Codec);

        var patchCommand = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, created.Value.EntityId,
            "effective-expense-invalid-patch",
            new UpdateExpenseRequest { PropertyId = _leasingPropertyId }, _now);
        var patchOutcome = await ExecuteAtomicAsync(
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
    public async Task AtomicMoneyMutation_ExpensePatchUsesBusinessClockAndReplaysExactCommit()
    {
        SkipIfNoDocker();
        var createdAtUtc = new DateTime(2027, 1, 30, 5, 0, 0, DateTimeKind.Utc);
        var updatedAtUtc = new DateTime(2027, 1, 31, 5, 0, 0, DateTimeKind.Utc);
        var scope = new WorkspaceReadScope(
            _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 7);
        var createCommand = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense,
            AtomicMoneyOperation.Create,
            0,
            "expense-clock-create",
            new CreateExpenseRequest
            {
                PropertyId = _managerPropertyId,
                Category = ScheduleECategory.Repairs,
                Description = "Business-clock repair",
                Status = ExpenseStatus.Pending,
                Amount = 125m,
                IncurredAt = createdAtUtc,
            },
            createdAtUtc);
        var createIdentity = AtomicMoneyMutation.Identity(createCommand);
        var created = await ExecuteAtomicAsync(
            createIdentity, createCommand, AtomicMoneyMutation.Codec);
        created.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        created.Value.EntityId.Should().BePositive(
            "future frozen business time must not expire present-day session authority");
        var updateCommand = AtomicMoneyMutation.Command(
            scope,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense,
            AtomicMoneyOperation.Update,
            created.Value.EntityId,
            "expense-clock-update",
            new UpdateExpenseRequest { Notes = "Frozen-clock update" },
            updatedAtUtc);
        var updateIdentity = AtomicMoneyMutation.Identity(updateCommand);

        var first = await ExecuteAtomicAsync(
            updateIdentity, updateCommand, AtomicMoneyMutation.Codec);
        var replay = await ExecuteAtomicAsync(
            updateIdentity, updateCommand, AtomicMoneyMutation.Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        await using var verify = NewContext();
        var persisted = await verify.Expenses.AsNoTracking()
            .Where(row => row.Id == created.Value.EntityId && row.PortfolioId == _portfolioId)
            .Select(row => new { row.CreatedAt, row.UpdatedAt, row.Notes })
            .SingleAsync();
        persisted.CreatedAt.Should().Be(createdAtUtc);
        persisted.UpdatedAt.Should().Be(updatedAtUtc);
        persisted.Notes.Should().Be("Frozen-clock update");
        (await verify.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == updateIdentity.CommandType &&
            row.IdempotencyKey == updateIdentity.IdempotencyKey)).Should().Be(1);
        var auditTimestamps = await verify.AtomicAuditLogs.AsNoTracking()
            .Where(row =>
                (row.CommandType == createIdentity.CommandType &&
                 row.CommandIdempotencyKey == createIdentity.IdempotencyKey) ||
                (row.CommandType == updateIdentity.CommandType &&
                 row.CommandIdempotencyKey == updateIdentity.IdempotencyKey))
            .Select(row => new { row.CommandType, row.CommandIdempotencyKey, row.Timestamp })
            .ToListAsync();
        var createAudits = auditTimestamps.Where(row =>
            row.CommandType == createIdentity.CommandType &&
            row.CommandIdempotencyKey == createIdentity.IdempotencyKey).ToArray();
        createAudits.Should().NotBeEmpty();
        createAudits.Should().OnlyContain(row => row.Timestamp == createdAtUtc);
        var updateAudits = auditTimestamps.Where(row =>
            row.CommandType == updateIdentity.CommandType &&
            row.CommandIdempotencyKey == updateIdentity.IdempotencyKey).ToArray();
        updateAudits.Should().NotBeEmpty();
        updateAudits.Should().OnlyContain(row => row.Timestamp == updatedAtUtc);
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
    public async Task WorkspaceAuthorityGuard_RejectsHandlerThatForgetsRevisionAdvance()
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

        var act = async () => await ExecuteAtomicAsync(
            Identity(nameof(WorkspaceAuthorityGuard_RejectsHandlerThatForgetsRevisionAdvance)),
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

        var first = await ExecuteAtomicAsync(identity, command, TeamCreateCodec);
        var replay = await ExecuteAtomicAsync(identity, command with
        {
            ActorAuthSessionId = pair.ActorSessionId,
            ActorAccessRevision = 1,
        }, TeamCreateCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var db = NewContext();
        (await db.WorkspaceMemberships.CountAsync(item =>
            item.AccessContextId == first.Value.AccessContextId)).Should().Be(1);
        (await db.WorkspaceAccessContexts
            .Where(item => item.Id == first.Value.AccessContextId)
            .Select(item => item.LastAuthorizedExperience)
            .SingleAsync()).Should().Be(WorkspaceExperience.Leasing);
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
    public async Task TeamInvite_UsesRealSecurityClockForImmediateActivationAndLoginUnderSimAhead()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"sim-ahead-invite-{Guid.NewGuid():N}");
        var simAheadUtc = new DateTime(2027, 1, 18, 15, 0, 0, DateTimeKind.Utc);
        var lowerBoundUtc = DateTime.UtcNow.AddMinutes(-1);
        var email = $"sim-ahead-member-{Guid.NewGuid():N}@example.test";
        var created = await ExecuteAtomicAsync(
            Identity("test.team.sim-ahead.membership.create"),
            new CreateWorkspaceMembershipCommand(
                pair.PortfolioId,
                pair.ActorUserId,
                pair.ActorSessionId,
                pair.ActorContextId,
                1,
                email,
                "Simulation Ahead Member",
                RoleProfileKeys.PropertyManager,
                MembershipRoleAssignmentScopeKind.SelectedProperties,
                [_managerPropertyId],
                simAheadUtc,
                "https://localhost:5667"),
            TeamCreateCodec);
        var upperBoundUtc = DateTime.UtcNow.AddMinutes(1);

        string invitationToken;
        long invitationId;
        await using (var db = NewContext())
        {
            var membership = await db.WorkspaceMemberships.AsNoTracking()
                .SingleAsync(item => item.Id == created.Value.WorkspaceMembershipId);
            var assignment = await db.MembershipRoleAssignments.AsNoTracking()
                .SingleAsync(item => item.Id == created.Value.AssignmentId);
            var option = await new EffectiveAccessContextSelectionQuery(db)
                .ListAsync(created.Value.UserId, created.Value.AccessContextId);
            var invitation = await db.WorkspaceInvitations.AsNoTracking()
                .SingleAsync(item => item.WorkspaceMembershipId == created.Value.WorkspaceMembershipId);
            var outbox = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(message =>
                    message.IdempotencyKey ==
                    $"workspace-invitation:{created.Value.WorkspaceMembershipId}:activation-v1");

            membership.EffectiveFromUtc.Should().BeOnOrAfter(lowerBoundUtc);
            membership.EffectiveFromUtc.Should().BeBefore(upperBoundUtc);
            membership.EffectiveFromUtc.Should().BeBefore(simAheadUtc);
            assignment.EffectiveFromUtc.Should().BeOnOrAfter(lowerBoundUtc);
            assignment.EffectiveFromUtc.Should().BeBefore(upperBoundUtc);
            assignment.EffectiveFromUtc.Should().BeBefore(simAheadUtc);
            option.Should().ContainSingle(item =>
                item.AccessContextId == created.Value.AccessContextId &&
                item.DefaultExperience == WorkspaceExperience.Management);

            invitationId = invitation.Id;
            invitationToken = ReadActivationToken(outbox.Payload);
        }

        var activated = await ActivateInvitationAsApiAsync(
            invitationId,
            created.Value.UserId,
            CreateWorkspaceMembershipHandler.HashInvitationToken(invitationToken));
        var issuedAtUtc = DateTime.UtcNow;
        var started = await ExecuteAtomicAsync(
            SessionRefreshCommandIdentity.ForStart(Guid.NewGuid()),
            new StartAuthSessionCommand(
                created.Value.UserId,
                created.Value.AccessContextId,
                created.Value.AccessRevision,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                LowerSha256(Guid.NewGuid().ToString("N")),
                issuedAtUtc,
                issuedAtUtc.AddDays(30),
                issuedAtUtc.AddDays(7),
                issuedAtUtc.AddDays(30)),
            StartSessionCodec);

        activated.InvitedUserId.Should().Be(created.Value.UserId);
        activated.AccessContextId.Should().Be(created.Value.AccessContextId);
        started.Value.Started.Should().BeTrue(
            "team access created during simulation must be immediately login-eligible on the real security clock");
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

        var result = await ExecuteAtomicAsync(
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

        var act = async () => await ExecuteAtomicAsync(
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

        var first = await ExecuteAtomicAsync(identity, command, TeamMutationCodec);
        var replay = await ExecuteAtomicAsync(identity, command, TeamMutationCodec);

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

        var act = async () => await ExecuteAtomicAsync(
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

        var act = async () => await ExecuteAtomicAsync(
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
        await ExecuteAtomicAsync(
            Identity("test.team.target-winner"), TeamAddCommand(pair, 1, _managerPropertyId), TeamMutationCodec);

        var stale = async () => await ExecuteAtomicAsync(
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

        var act = async () => await ExecuteAtomicAsync(
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
    public async Task PropertyScopeReplacement_OverlappingSelectedPropertiesReplacesScopeAndWritesReceipt()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"replace-scope-{Guid.NewGuid():N}");
        var command = new ReplaceWorkspaceAssignmentPropertyScopeCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            pair.TargetContextId, 1, pair.TargetAssignmentId, [_leasingPropertyId, _managerPropertyId]);

        var result = await ExecuteAtomicAsync(
            Identity("test.team.replace-overlap"), command, TeamMutationCodec);

        result.Value.AccessRevision.Should().Be(2);
        result.Value.AssignmentId.Should().Be(pair.TargetAssignmentId);

        await using var db = NewContext();
        (await db.WorkspaceAccessContexts.Where(item => item.Id == pair.TargetContextId)
            .Select(item => item.AccessRevision).SingleAsync()).Should().Be(2);
        var propertyIds = await db.MembershipRoleAssignmentProperties
            .Where(scope => scope.MembershipRoleAssignmentId == pair.TargetAssignmentId)
            .OrderBy(scope => scope.PropertyId)
            .Select(scope => scope.PropertyId)
            .ToListAsync();
        propertyIds.Should().Equal(_leasingPropertyId, _managerPropertyId);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "test.team.replace-overlap" &&
            receipt.Status == AtomicCommandReceiptStatus.Completed)).Should().Be(1);
    }

    [SkippableFact]
    public async Task RevokingMembership_InvalidatesTargetSessionImmediately()
    {
        SkipIfNoDocker();
        var pair = await SeedTeamAuthorityPairAsync($"revoke-{Guid.NewGuid():N}");
        var command = new ChangeWorkspaceMembershipStatusCommand(
            pair.PortfolioId, pair.ActorUserId, pair.ActorSessionId, pair.ActorContextId, 1,
            pair.TargetContextId, 1, WorkspaceMembershipStatusAction.Revoke);

        var result = await ExecuteAtomicAsync(
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
        await ExecuteAtomicAsync(
            Identity("test.team.suspend"), suspend, TeamMutationCodec);

        var reactivate = suspend with
        {
            ExpectedRevision = 2,
            Action = WorkspaceMembershipStatusAction.Reactivate,
        };
        var result = await ExecuteAtomicAsync(
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
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
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
        await new ChartOfAccountsSeedService(db).SeedAsync(_portfolioId);
        await db.SaveChangesAsync();
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
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
        };
        var targetSession = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = targetUser.Id,
            ActiveAccessContextId = targetContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now.AddMinutes(-5),
            LastSeenAtUtc = _now,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
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

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        if (command is AtomicMoneyMutationCommand money)
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
                .ExecuteAsync(identity.IdempotencyKey, AtomicMoneyMutation.Write(money, db));
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        return await scope.ServiceProvider
            .GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, codec);
    }

    private sealed record UnsafeWorkspaceAssignmentMutationCommand(
        int AccessContextId,
        long ExpectedRevision,
        int AssignmentId,
        DateTime ChangedAtUtc) : IAtomicCommandData;

    private sealed class UnsafeWorkspaceAssignmentMutationHandler
        : IAtomicCommandHandler<UnsafeWorkspaceAssignmentMutationCommand, WorkspaceAccessMutationResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly WorkspaceAccessRevisionGuard _accessRevisionGuard;

        public UnsafeWorkspaceAssignmentMutationHandler(
            RentalCommandDbContext db,
            WorkspaceAccessRevisionGuard accessRevisionGuard)
        {
            _db = db;
            _accessRevisionGuard = accessRevisionGuard;
        }

        public async Task<WorkspaceAccessMutationResult> HandleAsync(
            UnsafeWorkspaceAssignmentMutationCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var assignment = await _db.MembershipRoleAssignments
                .SingleAsync(item => item.Id == command.AssignmentId &&
                                     item.WorkspaceMembership!.AccessContextId == command.AccessContextId, ct);
            assignment.UpdatedAtUtc = command.ChangedAtUtc;
            await _accessRevisionGuard.ValidatePendingMutationAsync(
                _db,
                command.AccessContextId,
                command.ExpectedRevision,
                ct);

            return new WorkspaceAccessMutationResult(
                command.AccessContextId,
                command.AssignmentId,
                command.ExpectedRevision);
        }

        public Task AuthorizeReplayAsync(
            UnsafeWorkspaceAssignmentMutationCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Atomic access services are unavailable.");

    private static AtomicCommandIdentity Identity(string commandType) =>
        new(commandType, Guid.NewGuid().ToString("N"));

    private static string ReadActivationToken(string outboxPayload)
    {
        using var document = JsonDocument.Parse(outboxPayload);
        var body = document.RootElement.GetProperty("body").GetString()
            ?? throw new InvalidOperationException("Invitation email body is missing.");
        const string marker = "activate-team?token=";
        var markerIndex = body.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException("Invitation email body is missing the activation token.");
        }

        var tokenStart = markerIndex + marker.Length;
        var tokenEnd = body.IndexOfAny(['\r', '\n', ' '], tokenStart);
        var token = tokenEnd < 0
            ? body[tokenStart..]
            : body[tokenStart..tokenEnd];
        return Uri.UnescapeDataString(token.Trim());
    }

    private static string LowerSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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

    private static async Task CommitDirectPostingAsync<T>(
        RentalCommandDbContext db,
        Func<IAtomicCommandContext, CancellationToken, Task<T>> post,
        string operationKey)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var auditScope = new AtomicAuditScope(TimeProvider.System);
        var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
        var attemptId = Guid.NewGuid();
        commandContext.BeginAttempt(attemptId);
        commandContext.BindReceipt(Guid.NewGuid());
        using var attempt = auditScope.BeginAttempt(
            new AtomicCommandIdentity("test.accounting.post", operationKey), attemptId, db);
        await post(commandContext, CancellationToken.None);
        await commandContext.FlushBusinessAsync();
        await transaction.CommitAsync();
        commandContext.EndAttempt();
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private async Task<WorkspaceInvitationActivationRow> ActivateInvitationAsApiAsync(
        long invitationId,
        int invitedUserId,
        string tokenHash)
    {
        await using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(_apiConnectionString)
                .Options);
        return await db.Database.SqlQuery<WorkspaceInvitationActivationRow>($"""
                SELECT * FROM rc_activate_workspace_invitation(
                    {invitationId}, {invitedUserId}, {tokenHash}, {"hashed-password-for-test"},
                    {Guid.NewGuid().ToString("N")}, {Guid.NewGuid().ToString("N")})
                """)
            .SingleAsync();
    }

    private async Task<RentalCommandDbContext> NewAuthorizationQueryContextAsync()
    {
        var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(_apiConnectionString)
                .Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('app.current_portfolio_id', {_portfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {_sessionId.ToString()}, false),
                   set_config('app.current_user_id', {_userId.ToString()}, false),
                   set_config('app.current_access_context_id', {_accessContextId.ToString()}, false),
                   set_config('app.access_revision', {"7"}, false)
            """);
        return db;
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; workspace authorization kernel test skipped.");

    private sealed class WorkspaceInvitationActivationRow
    {
        public int PortfolioId { get; set; }
        public int WorkspaceMembershipId { get; set; }
        public int AccessContextId { get; set; }
        public int InvitedUserId { get; set; }
        public DateTime AcceptedAtUtc { get; set; }
    }

    private sealed class AccessTestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:workspace-access";
        public string? IpAddress => "127.0.0.1";
    }
}
