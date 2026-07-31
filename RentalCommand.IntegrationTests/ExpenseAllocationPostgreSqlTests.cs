using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL acceptance proof for the D02 Expense scope/allocation kernel.</summary>
public sealed class ExpenseAllocationPostgreSqlTests : IAsyncLifetime
{
    private readonly DateTime _now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private int _portfolioId;
    private int _propertyId;
    private int _unitId;
    private int _workOrderId;
    private int _ownerEntityId;
    private int _userId;
    private int _accessContextId;
    private Guid _sessionId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_expense_allocations")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            await SeedAsync(db);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ExpenseAtomicFailureInterceptor>();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicMoneyMutationCommand,
            AtomicMoneyMutationResult,
            AtomicMoneyMutationHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<ExpenseAtomicFailureInterceptor>()));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task CanonicalFourScopes_PersistInheritedContextAndRejectMismatches()
    {
        SkipIfNoDocker();

        var portfolio = await CreateAsync("scope-portfolio", new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.Portfolio,
            Description = "Portfolio insurance",
            Amount = 10m,
            IncurredAt = _now,
        });
        var property = await CreateAsync("scope-property", new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.Property,
            PropertyId = _propertyId,
            Description = "Property permit",
            Amount = 20m,
            IncurredAt = _now,
        });
        var unit = await CreateAsync("scope-unit", new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.Unit,
            UnitId = _unitId,
            Description = "Unit appliance",
            Amount = 30m,
            IncurredAt = _now,
        });
        var workOrder = await CreateAsync("scope-work-order", new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.WorkOrder,
            WorkOrderId = _workOrderId,
            Description = "Repair labor",
            Amount = 40m,
            IncurredAt = _now,
        });

        await using var verify = NewContext();
        var scopes = await verify.Expenses.AsNoTracking()
            .Where(row => new[] { portfolio, property, unit, workOrder }.Contains(row.Id))
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Id,
                row.OperationalScope,
                row.PropertyId,
                row.UnitId,
                row.WorkOrderId,
            })
            .ToListAsync();
        scopes.Should().Contain(row =>
            row.Id == portfolio && row.OperationalScope == ExpenseOperationalScope.Portfolio &&
            row.PropertyId == null && row.UnitId == null && row.WorkOrderId == null);
        scopes.Should().Contain(row =>
            row.Id == property && row.OperationalScope == ExpenseOperationalScope.Property &&
            row.PropertyId == _propertyId && row.UnitId == null && row.WorkOrderId == null);
        scopes.Should().Contain(row =>
            row.Id == unit && row.OperationalScope == ExpenseOperationalScope.Unit &&
            row.PropertyId == _propertyId && row.UnitId == _unitId && row.WorkOrderId == null);
        scopes.Should().Contain(row =>
            row.Id == workOrder && row.OperationalScope == ExpenseOperationalScope.WorkOrder &&
            row.PropertyId == _propertyId && row.UnitId == _unitId &&
            row.WorkOrderId == _workOrderId);

        var mismatched = await ExecuteAsync("scope-mismatch", new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.Unit,
            PropertyId = await SeedOtherPortfolioPropertyAsync(),
            UnitId = _unitId,
            Description = "Cross-portfolio mismatch",
            Amount = 10m,
            IncurredAt = _now,
        });
        mismatched.Value.Found.Should().BeFalse();
    }

    [SkippableFact]
    public async Task TypedAllocations_RequirePositiveExactlyOneTargetAndExactNonemptyBalance()
    {
        SkipIfNoDocker();
        var expenseId = await CreateAsync("typed-balanced", new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.Unit,
            UnitId = _unitId,
            Description = "Balanced allocation",
            Amount = 100m,
            IncurredAt = _now,
            Allocations =
            [
                new ExpenseAllocationRequest
                {
                    TargetKind = ExpenseAllocationTargetKind.Property,
                    PropertyId = _propertyId,
                    Amount = 40m,
                },
                new ExpenseAllocationRequest
                {
                    TargetKind = ExpenseAllocationTargetKind.Unit,
                    UnitId = _unitId,
                    Amount = 30m,
                },
                new ExpenseAllocationRequest
                {
                    TargetKind = ExpenseAllocationTargetKind.OwnerEntity,
                    OwnerEntityId = _ownerEntityId,
                    Amount = 30m,
                },
            ],
        });

        await using (var verify = NewContext())
        {
            var persisted = await verify.ExpenseAllocations.AsNoTracking()
                .Where(row => row.ExpenseId == expenseId)
                .OrderBy(row => row.TargetKind)
                .Select(row => new
                {
                    row.TargetKind,
                    row.PropertyId,
                    row.UnitId,
                    row.OwnerEntityId,
                    row.Amount,
                })
                .ToListAsync();
            persisted.Should().HaveCount(3);
            (await verify.ExpenseAllocations
                .Where(row => row.ExpenseId == expenseId)
                .SumAsync(row => row.Amount)).Should().Be(100m);
        }

        await using var db = NewContext();
        db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = _portfolioId,
            ExpenseId = expenseId,
            TargetKind = ExpenseAllocationTargetKind.Unit,
            UnitId = _unitId,
            Amount = 1m,
            CreatedAt = _now,
        });
        var unbalanced = async () => await db.SaveChangesAsync();
        var exception = await unbalanced.Should().ThrowAsync<DbUpdateException>();
        var postgresException = exception.Which.InnerException.Should()
            .BeOfType<PostgresException>().Which;
        postgresException.SqlState.Should().Be("23514");
        postgresException.MessageText.Should()
            .Match("*allocation total*must exactly equal amount*");

        var mixedTarget = async () => await CreateAsync(
            "typed-mixed-target",
            new CreateExpenseRequest
            {
                Description = "Mixed target",
                Amount = 10m,
                IncurredAt = _now,
                Allocations =
                [
                    new ExpenseAllocationRequest
                    {
                        TargetKind = ExpenseAllocationTargetKind.Property,
                        PropertyId = _propertyId,
                        UnitId = _unitId,
                        Amount = 10m,
                    },
                ],
            });
        await mixedTarget.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one target*");

        var nonpositive = async () => await CreateAsync(
            "typed-nonpositive",
            new CreateExpenseRequest
            {
                Description = "Nonpositive target",
                Amount = 10m,
                IncurredAt = _now,
                Allocations =
                [
                    new ExpenseAllocationRequest
                    {
                        TargetKind = ExpenseAllocationTargetKind.Unit,
                        UnitId = _unitId,
                        Amount = 0m,
                    },
                ],
            });
        await nonpositive.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be positive*");

        var outsidePropertyId = await SeedOtherPortfolioPropertyAsync();
        var crossPortfolio = await ExecuteAsync(
            "typed-cross-portfolio",
            new CreateExpenseRequest
            {
                Description = "Cross portfolio target",
                Amount = 10m,
                IncurredAt = _now,
                Allocations =
                [
                    new ExpenseAllocationRequest
                    {
                        TargetKind = ExpenseAllocationTargetKind.Property,
                        PropertyId = outsidePropertyId,
                        Amount = 10m,
                    },
                ],
            });
        crossPortfolio.Value.Found.Should().BeFalse();
    }

    [SkippableFact]
    public async Task ReparentedAllocation_ValidatesBothSourceAndDestinationBalances()
    {
        SkipIfNoDocker();
        var sourceExpenseId = await CreateAsync("reparent-source", new CreateExpenseRequest
        {
            Description = "Reparent source",
            Amount = 100m,
            IncurredAt = _now,
            Allocations =
            [
                new ExpenseAllocationRequest
                {
                    TargetKind = ExpenseAllocationTargetKind.Unit,
                    UnitId = _unitId,
                    Amount = 60m,
                },
                new ExpenseAllocationRequest
                {
                    TargetKind = ExpenseAllocationTargetKind.OwnerEntity,
                    OwnerEntityId = _ownerEntityId,
                    Amount = 40m,
                },
            ],
        });
        var destinationExpenseId = await CreateAsync(
            "reparent-destination",
            new CreateExpenseRequest
            {
                Description = "Reparent destination",
                Amount = 40m,
                IncurredAt = _now,
            });

        await using (var invalid = NewContext())
        await using (var transaction = await invalid.Database.BeginTransactionAsync())
        {
            var moved = await invalid.ExpenseAllocations.SingleAsync(row =>
                row.ExpenseId == sourceExpenseId &&
                row.OwnerEntityId == _ownerEntityId);
            moved.ExpenseId = destinationExpenseId;
            await invalid.SaveChangesAsync();

            var commit = async () => await transaction.CommitAsync();
            await commit.Should().ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == "23514")
                .WithMessage("*allocation total*exactly equal*");
        }

        await using (var valid = NewContext())
        await using (var transaction = await valid.Database.BeginTransactionAsync())
        {
            var source = await valid.Expenses.SingleAsync(row => row.Id == sourceExpenseId);
            var moved = await valid.ExpenseAllocations.SingleAsync(row =>
                row.ExpenseId == sourceExpenseId &&
                row.OwnerEntityId == _ownerEntityId);
            source.Amount = 60m;
            moved.ExpenseId = destinationExpenseId;
            await valid.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verify = NewContext();
        var balances = await verify.Expenses.AsNoTracking()
            .Where(row => row.Id == sourceExpenseId || row.Id == destinationExpenseId)
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Id,
                row.Amount,
                AllocationTotal = row.Allocations.Sum(allocation => allocation.Amount),
            })
            .ToListAsync();
        balances.Should().ContainEquivalentOf(new
        {
            Id = sourceExpenseId,
            Amount = 60m,
            AllocationTotal = 60m,
        });
        balances.Should().ContainEquivalentOf(new
        {
            Id = destinationExpenseId,
            Amount = 40m,
            AllocationTotal = 40m,
        });
    }

    [SkippableFact]
    public async Task ConcurrentAllocationWrites_CannotCommitAnUnbalancedFinalSet()
    {
        SkipIfNoDocker();
        var expenseId = await CreateAsync("concurrent-balance", new CreateExpenseRequest
        {
            Description = "Concurrent allocation",
            Amount = 100m,
            IncurredAt = _now,
        });

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        async Task<bool> InsertAsync(int targetId)
        {
            await using var db = NewContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.ExpenseAllocations.Add(new ExpenseAllocation
            {
                PortfolioId = _portfolioId,
                ExpenseId = expenseId,
                TargetKind = ExpenseAllocationTargetKind.Unit,
                UnitId = targetId,
                Amount = 100m,
                CreatedAt = _now,
            });
            await db.SaveChangesAsync();
            if (Interlocked.Increment(ref entered) == 2) ready.SetResult();
            await release.Task;
            try
            {
                await transaction.CommitAsync();
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == "23514")
            {
                return false;
            }
        }

        var secondUnitId = await SeedSecondUnitAsync();
        var first = InsertAsync(_unitId);
        var second = InsertAsync(secondUnitId);
        await ready.Task;
        release.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().ContainSingle(result => result);
        outcomes.Should().ContainSingle(result => !result);
        await using var verify = NewContext();
        (await verify.ExpenseAllocations.Where(row => row.ExpenseId == expenseId)
            .SumAsync(row => row.Amount)).Should().Be(100m);
    }

    [SkippableFact]
    public async Task AtomicCreate_ReplayAndInjectedFailureCoverExpenseAllocationsAuditOutboxAndReceipt()
    {
        SkipIfNoDocker();
        var request = new CreateExpenseRequest
        {
            OperationalScope = ExpenseOperationalScope.Unit,
            UnitId = _unitId,
            Description = "Atomic allocated expense",
            Amount = 60m,
            IncurredAt = _now,
            Allocations =
            [
                new ExpenseAllocationRequest
                {
                    TargetKind = ExpenseAllocationTargetKind.Unit,
                    UnitId = _unitId,
                    Amount = 60m,
                },
            ],
        };
        var command = Command("atomic-replay", request);
        var first = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);
        var replay = await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);
        var auditIdempotencyKey = AtomicMoneyMutation.Identity(command).IdempotencyKey;

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using (var verify = NewContext())
        {
            (await verify.Expenses.CountAsync(row => row.Id == first.Value.EntityId)).Should().Be(1);
            (await verify.ExpenseAllocations.CountAsync(row =>
                row.ExpenseId == first.Value.EntityId)).Should().Be(1);
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandIdempotencyKey == auditIdempotencyKey)).Should().Be(1);
            (await verify.AtomicCommandReceipts.CountAsync(row =>
                row.IdempotencyKey == auditIdempotencyKey)).Should().Be(1);
            (await verify.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey.Contains(command.IdempotencyKey))).Should().Be(1);
        }

        var failure = Services.GetRequiredService<ExpenseAtomicFailureInterceptor>();
        failure.Arm();
        request.Description = "Must roll back";
        var failedCommand = Command("atomic-injected-failure", request);
        var act = async () => await Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(failedCommand), failedCommand, AtomicMoneyMutation.Codec);
        await act.Should().ThrowAsync<Exception>();

        await using var failed = NewContext();
        (await failed.Expenses.CountAsync(row => row.Description == "Must roll back")).Should().Be(0);
        (await failed.ExpenseAllocations.CountAsync(row =>
            row.Expense!.Description == "Must roll back")).Should().Be(0);
        (await failed.AtomicCommandReceipts.CountAsync(row =>
            row.IdempotencyKey == failedCommand.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AuthorizedFilterAggregateSortAndPage_StayInTwoPostgreSqlCommandsWithoutDuplicates()
    {
        SkipIfNoDocker();
        await CreateAsync("page-low", Allocated(20m, "Page low"));
        await CreateAsync("page-high", Allocated(40m, "Page high"));
        await CreateAsync("page-decoy", new CreateExpenseRequest
        {
            Description = "No allocation",
            Amount = 80m,
            IncurredAt = _now,
        });

        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(new SqlCaptureInterceptor(commands))
            .Options;
        await using var db = new RentalCommandDbContext(options);
        var service = new ExpenseService(
            db, Mock.Of<IFileStorage>(), TimeProvider.System, Mock.Of<IAtomicUnitOfWork>());
        var page = await service.ListPageAsync(
            new WorkspaceReadScope(
                _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 1),
            null, null, null, false,
            new ExpenseListQuery
            {
                OperationalScope = ExpenseOperationalScope.Portfolio,
                AllocationTargetKind = ExpenseAllocationTargetKind.Unit,
                AllocationTargetId = _unitId,
                Sort = "-allocationTotal",
                Skip = 0,
                Take = 1,
            });

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle();
        page.Items.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        page.Items[0].AllocationTotal.Should().Be(40m);
        commands.Should().HaveCount(2);
        commands[0].Should().ContainEquivalentOf("count(*)");
        commands[0].Should().Contain("EXISTS");
        commands[0].Should().Contain("ExpenseAllocations");
        commands[0].Should().Contain("RoleProfileCapabilities");
        commands[1].Should().ContainEquivalentOf("sum(");
        commands[1].Should().Contain("ORDER BY");
        commands[1].Should().Contain("LIMIT");
        commands[1].Should().Contain("OFFSET");
    }

    [SkippableFact]
    public async Task MigratedSchema_HasRlsGrantsSoftDeleteFilterAndSandboxDeletionAdmission()
    {
        SkipIfNoDocker();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              c.relrowsecurity,
              c.relforcerowsecurity,
              has_table_privilege('rentalcommand_api', '"ExpenseAllocations"', 'SELECT,INSERT,UPDATE,DELETE'),
              has_sequence_privilege('rentalcommand_api', '"ExpenseAllocations_Id_seq"', 'USAGE,SELECT'),
              EXISTS (
                SELECT 1 FROM pg_policies
                WHERE tablename = 'ExpenseAllocations' AND policyname = 'tenant_delete'
                  AND qual LIKE '%rc_sandbox_graduation_allows%'),
              EXISTS (
                SELECT 1 FROM pg_trigger
                WHERE tgrelid = '"ExpenseAllocations"'::regclass
                  AND tgname = 'trg_expense_allocation_balance_from_allocation'
                  AND tgdeferrable AND tginitdeferred)
            FROM pg_class c
            WHERE c.oid = '"ExpenseAllocations"'::regclass;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        for (var index = 0; index < 6; index++)
            reader.GetBoolean(index).Should().BeTrue();

        await using var modelContext = NewContext();
        var allocationFilter = modelContext.Model
            .FindEntityType(typeof(ExpenseAllocation))!.GetQueryFilter();
        allocationFilter.Should().NotBeNull();
        allocationFilter!.ToString().Should().Contain("DeletedAt");
        FoundationBaselinePostgreSql.SandboxGraduationDeleteTables
            .Should().Contain("ExpenseAllocations");
        SandboxService.SandboxGraduationDeleteOrder
            .Should().ContainInOrder("ExpenseAllocations", "ExpenseLineItems", "Expenses");
    }

    private CreateExpenseRequest Allocated(decimal amount, string description) => new()
    {
        Description = description,
        Amount = amount,
        IncurredAt = _now,
        Allocations =
        [
            new ExpenseAllocationRequest
            {
                TargetKind = ExpenseAllocationTargetKind.Unit,
                UnitId = _unitId,
                Amount = amount,
            },
        ],
    };

    private async Task<int> CreateAsync(string key, CreateExpenseRequest request)
    {
        var outcome = await ExecuteAsync(key, request);
        outcome.Value.Found.Should().BeTrue();
        return outcome.Value.EntityId;
    }

    private Task<AtomicCommandOutcome<AtomicMoneyMutationResult>> ExecuteAsync(
        string key,
        CreateExpenseRequest request)
    {
        var command = Command(key, request);
        return Atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec);
    }

    private AtomicMoneyMutationCommand Command(string key, CreateExpenseRequest request) =>
        AtomicMoneyMutation.Command(
            new WorkspaceReadScope(
                _portfolioId, _userId, _sessionId, _accessContextId, AccessRevision: 1),
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense,
            AtomicMoneyOperation.Create,
            0,
            key,
            request,
            _now);

    private async Task SeedAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = "expense@example.test",
            NormalizedUserName = "EXPENSE@EXAMPLE.TEST",
            Email = "expense@example.test",
            NormalizedEmail = "EXPENSE@EXAMPLE.TEST",
            DisplayName = "Expense Manager",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var portfolio = new Portfolio
        {
            Name = "Expense portfolio",
            ManagementCompanyName = "Expense portfolio",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = "Expense property",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var owner = new OwnerEntity
        {
            PortfolioId = portfolio.Id,
            Name = "Expense owner",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AddRange(property, owner);
        await db.SaveChangesAsync();

        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitNumber = "1A",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        var workOrder = new WorkOrder
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Title = "Expense work order",
            Description = "Expense work order",
            Category = "General",
            RequestedAt = _now,
            UpdatedAt = _now,
        };
        db.WorkOrders.Add(workOrder);

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var roleId = await db.RoleProfiles
            .Where(role => role.Key == RoleProfileKeys.PropertyManager)
            .Select(role => role.Id)
            .SingleAsync();
        membership.RoleAssignments.Add(new MembershipRoleAssignment
        {
            PortfolioId = portfolio.Id,
            RoleProfileId = roleId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        });
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync();
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
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync();

        _portfolioId = portfolio.Id;
        _propertyId = property.Id;
        _unitId = unit.Id;
        _workOrderId = workOrder.Id;
        _ownerEntityId = owner.Id;
        _userId = user.Id;
        _accessContextId = accessContext.Id;
        _sessionId = session.Id;
    }

    private async Task<int> SeedOtherPortfolioPropertyAsync()
    {
        await using var db = NewContext();
        var portfolio = new Portfolio
        {
            Name = "Other",
            ManagementCompanyName = "Other",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = "Other",
            AddressLine1 = "2 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task<int> SeedSecondUnitAsync()
    {
        await using var db = NewContext();
        var unit = new Unit
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            UnitNumber = "2A",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        return unit.Id;
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private IAtomicUnitOfWork Atomic =>
        Services.GetRequiredService<IAtomicUnitOfWork>();

    private IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Expense atomic services unavailable.");

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL Expense allocation verification.");

    private sealed class SqlCaptureInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ExpenseAtomicFailureInterceptor : DbCommandInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _armed, 0);
                throw new InvalidOperationException("injected Expense atomic finalization failure");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
