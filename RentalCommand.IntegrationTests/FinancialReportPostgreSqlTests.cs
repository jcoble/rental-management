using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Reporting;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FinancialReportPostgreSqlCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Financial reports PostgreSQL";
}

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class FinancialReportPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<CapturedCommand> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public FinancialReportPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task AllExpenseScopesAndAllocations_AppearExactlyOnce()
    {
        var maple = await SeedPropertyAsync("Maple");
        var oak = await SeedPropertyAsync("Oak");
        var allPropertiesScope = await SeedScopeAsync(
            "all-financial-reports@example.test",
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties);
        var selectedScope = await SeedScopeAsync(
            "selected-financial-reports@example.test",
            RoleProfileKeys.PropertyManager,
            MembershipRoleAssignmentScopeKind.SelectedProperties,
            [maple.Id]);
        await SeedExpenseAsync(maple.Id, 300m, new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        await SeedExpenseAsync(oak.Id, 450m, new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc));
        await SeedExpenseAsync(null, 125m, new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc));
        await SeedOwnerAllocatedExpenseAsync(maple.Id, 40m, new DateTime(2026, 1, 13, 0, 0, 0, DateTimeKind.Utc));
        var mapleUnit = await SeedUnitAsync(maple, "2");
        await SeedUnitAllocatedExpenseAsync(mapleUnit, 25m, new DateTime(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc));
        await SeedWorkOrderExpenseAsync(mapleUnit, 35m, new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var service = NewService();
        _commands.Clear();
        var allPropertiesPnl = await service.GetPropertyProfitAndLossAsync(allPropertiesScope, new ReportRangeQuery
        {
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        });
        var allPropertiesPnlCommands = _commands.ToArray();
        await WriteArtifactsAsync("all-properties-pnl", allPropertiesPnlCommands);

        _commands.Clear();
        var allPropertiesTrueCashFlow = await service.GetTrueCashFlowAsync(allPropertiesScope, new ReportRangeQuery
        {
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        });
        var allPropertiesTrueCashFlowCommands = _commands.ToArray();
        await WriteArtifactsAsync("all-properties-true-cash-flow", allPropertiesTrueCashFlowCommands);

        allPropertiesPnl.Rows.Single(row => row.PropertyId == maple.Id).Expense.Should().Be(400m);
        allPropertiesPnl.Rows.Single(row => row.PropertyId == oak.Id).Expense.Should().Be(450m);
        allPropertiesPnl.TotalExpense.Should().Be(975m);
        allPropertiesPnlCommands.Count(command =>
            command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Should().Be(3, "P&L executes row projection plus SQL income and expense total aggregates");
        allPropertiesPnlCommands.Should().Contain(command =>
            command.Sql.Contains("\"MembershipRoleAssignments\"", StringComparison.Ordinal) &&
            ContainsSqlOrParameter(command, "AllProperties") &&
            command.Sql.Contains("IS NULL", StringComparison.OrdinalIgnoreCase),
            "portfolio-scope P&L expenses require the effective AllProperties reports.read authority branch");

        allPropertiesTrueCashFlow.Properties.Single(row => row.PropertyId == maple.Id)
            .OperatingExpenses.Should().Be(400m);
        allPropertiesTrueCashFlow.TotalOperatingExpenses.Should().Be(975m);
        allPropertiesTrueCashFlowCommands.Count(command =>
            command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Should().Be(3, "true cash flow executes rows, property totals, and operating-expense aggregate");
        allPropertiesTrueCashFlowCommands.Should().Contain(command =>
            command.Sql.Contains("\"MembershipRoleAssignments\"", StringComparison.Ordinal) &&
            ContainsSqlOrParameter(command, "AllProperties") &&
            command.Sql.Contains("IS NULL", StringComparison.OrdinalIgnoreCase),
            "portfolio-scope true cash-flow expenses require the effective AllProperties reports.read authority branch");

        _commands.Clear();
        var selectedPnl = await service.GetPropertyProfitAndLossAsync(selectedScope, new ReportRangeQuery
        {
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        });
        var selectedPnlCommands = _commands.ToArray();
        await WriteArtifactsAsync("selected-pnl", selectedPnlCommands);

        _commands.Clear();
        var selectedTrueCashFlow = await service.GetTrueCashFlowAsync(selectedScope, new ReportRangeQuery
        {
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        });
        await WriteArtifactsAsync("selected-true-cash-flow", _commands);

        _commands.Clear();
        var explicitFilterPnl = await service.GetPropertyProfitAndLossAsync(allPropertiesScope, new ReportRangeQuery
        {
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            PropertyIds = [maple.Id],
        });
        await WriteArtifactsAsync("explicit-filter-pnl", _commands);

        selectedPnl.Rows.Should().ContainSingle();
        selectedPnl.Rows[0].Expense.Should().Be(400m);
        selectedPnl.TotalExpense.Should().Be(400m);
        selectedTrueCashFlow.Properties.Should().ContainSingle();
        selectedTrueCashFlow.TotalOperatingExpenses.Should().Be(400m);
        explicitFilterPnl.TotalExpense.Should().Be(400m);
    }

    [Fact]
    public async Task CashFlow_ComposesMonthJoinNetAndSortInPostgreSql()
    {
        var property = await SeedPropertyAsync("Maple");
        var lease = await SeedLeaseAsync(property);
        var scope = await SeedScopeAsync(
            "cash-flow-financial-reports@example.test",
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties);
        await SeedPaymentAsync(lease, 1_000m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        await SeedPaymentAsync(lease, 500m, new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        await SeedExpenseAsync(property.Id, 200m, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        await SeedExpenseAsync(null, 75m, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        var service = NewService();
        _commands.Clear();

        var report = await service.GetCashFlowAsync(scope, new ReportRangeQuery
        {
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
        });
        var selects = CaptureCommandSql(_commands);
        await WriteSqlArtifactAsync("cash-flow-month-composition", selects.Select(command => command.Sql));
        await WriteExplainArtifactAsync("cash-flow-month-composition", await ExplainAsync(selects));

        report.Months.Select(month => month.MonthKey).Should().Equal("2026-01", "2026-02", "2026-03");
        report.Months.Single(month => month.Month == 1).Income.Should().Be(1_000m);
        report.Months.Single(month => month.Month == 1).Expense.Should().Be(200m);
        report.Months.Single(month => month.Month == 1).Net.Should().Be(800m);
        report.Months.Single(month => month.Month == 2).Income.Should().Be(0m);
        report.Months.Single(month => month.Month == 2).Expense.Should().Be(75m);
        report.Months.Single(month => month.Month == 3).Income.Should().Be(500m);
        report.TotalIncome.Should().Be(1_500m);
        report.TotalExpense.Should().Be(275m);
        report.TotalNet.Should().Be(1_225m);
        report.TotalCount.Should().Be(3);
        selects.Should().HaveCount(3, "cash flow executes month composition plus SQL income and expense totals");
        selects[0].Sql.Should().Contain("UNION", "month rows are composed in SQL from the month anchor");
        selects[0].Sql.Should().Contain("ORDER BY", "month sorting is deterministic in PostgreSQL");
        selects[0].Sql.Contains("SUM", StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue("monthly income, expense, and net are aggregated in SQL");
    }

    [Fact]
    public async Task SecurityDeposits_PageDeterministicallyInPostgreSql()
    {
        var maple = await SeedPropertyAsync("Maple");
        var oak = await SeedPropertyAsync("Oak");
        var mapleLease = await SeedLeaseAsync(maple);
        var oakLease = await SeedLeaseAsync(oak);
        var scope = await SeedScopeAsync(
            "deposit-financial-reports@example.test",
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties);
        var receiptAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var transferredAt = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        await SeedSecurityDepositAsync(mapleLease, receiptAt, transferredAt);
        await SeedSecurityDepositAsync(oakLease, receiptAt.AddDays(1), transferredAt.AddDays(1), "oak", 900m, 100m);

        var service = NewService();
        _commands.Clear();

        var report = await service.GetSecurityDepositRegisterAsync(scope, new ReportRangeQuery
        {
            Skip = 1,
            Take = 1,
            Sort = "property",
        });
        var selects = CaptureCommandSql(_commands);
        await WriteSqlArtifactAsync("security-deposit-deterministic-paging", selects.Select(command => command.Sql));
        await WriteExplainArtifactAsync("security-deposit-deterministic-paging", await ExplainAsync(selects));

        report.TotalCount.Should().Be(2);
        report.Skip.Should().Be(1);
        report.Take.Should().Be(1);
        report.Sort.Should().Be("property");
        report.Rows.Should().ContainSingle();
        report.Rows[0].PropertyName.Should().Be("Oak");
        report.Rows[0].Returned.Should().Be(100m);
        report.Rows[0].ReturnedAt.Should().Be(transferredAt.AddDays(1));
        report.TotalHeld.Should().Be(1_900m);
        report.TotalReturned.Should().Be(500m);
        report.TotalCurrentBalance.Should().Be(1_400m);
        selects.Should().HaveCount(3, "deposit register executes count, paged rows, and totals");
        selects.Should().Contain(command =>
            command.Sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            command.Sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            command.Sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        selects.Should().Contain(command => ContainsSqlOrParameter(command, "TransferOut"));
    }

    [Fact]
    public async Task CapturedPlans_AreWrittenForVerifier()
    {
        var property = await SeedPropertyAsync("Maple");
        var lease = await SeedLeaseAsync(property);
        var scope = await SeedScopeAsync(
            "plan-capture-financial-reports@example.test",
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties);
        await SeedPaymentAsync(lease, 100m, new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc));

        var service = NewService();
        _commands.Clear();

        await service.GetCashFlowAsync(scope, new ReportRangeQuery
        {
            From = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
        });
        var selects = CaptureCommandSql(_commands);
        await WriteSqlArtifactAsync("captured-plans", selects.Select(command => command.Sql));
        await WriteExplainArtifactAsync("captured-plans", await ExplainAsync(selects));

        selects.Should().NotBeEmpty("the verifier artifacts are written from captured PostgreSQL commands");
    }

    private ReportsService NewService() =>
        new(
            _context.Db,
            new OwnerStatementService(_context.Db, TimeProvider.System),
            new ScheduleEService(_context.Db),
            new PropertyDispositionService(_context.Db, TimeProvider.System),
            TimeProvider.System);

    private async Task<WorkspaceReadScope> SeedScopeAsync(
        string email,
        string roleKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        IReadOnlyCollection<int>? selectedPropertyIds = null)
    {
        var authNow = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = email,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = authNow,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = authNow.AddMinutes(-1),
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = authNow.AddMinutes(-1),
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        foreach (var propertyId in selectedPropertyIds ?? [])
        {
            assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
            {
                PortfolioId = PortfolioId,
                PropertyId = propertyId,
            });
        }
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = authNow,
            LastSeenAtUtc = authNow,
            ExpiresAtUtc = authNow.AddHours(1),
        };

        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();

        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private async Task<Property> SeedPropertyAsync(string name)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = $"{name} Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        return property;
    }

    private async Task<Unit> SeedUnitAsync(Property property, string unitNumber)
    {
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = unitNumber,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Units.Add(unit);
        await _context.Db.SaveChangesAsync();
        return unit;
    }

    private async Task<Expense> SeedExpenseAsync(int? propertyId, decimal amount, DateTime paidAt)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = propertyId is null
                ? ExpenseOperationalScope.Portfolio
                : ExpenseOperationalScope.Property,
            PropertyId = propertyId,
            Category = ScheduleECategory.Repairs,
            Description = propertyId is null ? "Portfolio expense" : "Property expense",
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = paidAt,
            PaidAt = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _context.Db.Expenses.Add(expense);
        await _context.Db.SaveChangesAsync();
        return expense;
    }

    private async Task SeedOwnerAllocatedExpenseAsync(int propertyId, decimal amount, DateTime paidAt)
    {
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Owner LLC",
            OwnerEntityType = OwnerEntityType.LLC,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.OwnerEntities.Add(owner);
        await _context.Db.SaveChangesAsync();
        var expense = await SeedExpenseAsync(propertyId, amount, paidAt);
        _context.Db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = PortfolioId,
            ExpenseId = expense.Id,
            TargetKind = ExpenseAllocationTargetKind.OwnerEntity,
            OwnerEntityId = owner.Id,
            Amount = amount,
            CreatedAt = paidAt,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task SeedUnitAllocatedExpenseAsync(Unit unit, decimal amount, DateTime paidAt)
    {
        var expense = await SeedExpenseAsync(null, amount, paidAt);
        expense.OperationalScope = ExpenseOperationalScope.Unit;
        expense.PropertyId = unit.PropertyId;
        expense.UnitId = unit.Id;
        _context.Db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = PortfolioId,
            ExpenseId = expense.Id,
            TargetKind = ExpenseAllocationTargetKind.Unit,
            UnitId = unit.Id,
            Amount = amount,
            CreatedAt = paidAt,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task SeedWorkOrderExpenseAsync(Unit unit, decimal amount, DateTime paidAt)
    {
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Title = "Financial report work order",
            Description = "Financial report work order",
            Category = "General",
            RequestedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        var expense = await SeedExpenseAsync(unit.PropertyId, amount, paidAt);
        expense.OperationalScope = ExpenseOperationalScope.WorkOrder;
        expense.UnitId = unit.Id;
        expense.WorkOrderId = workOrder.Id;
        await _context.Db.SaveChangesAsync();
    }

    private async Task<LeaseAgreement> SeedLeaseAsync(Property property)
    {
        var unit = await SeedUnitAsync(property, "1");
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Ann",
            LastName = "Acre",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Tenants.Add(tenant);
        await _context.Db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{property.Id}-{Guid.NewGuid():N}"[..16],
            PlannedPossessionAtUtc = Now.AddMonths(-1),
            PossessionGivenAtUtc = Now.AddMonths(-1),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.LeaseManagements.Add(management);
        await _context.Db.SaveChangesAsync();

        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{management.Id}",
            Currency = "USD",
            OpenedAtUtc = Now.AddMonths(-1),
            CreatedAtUtc = Now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(Now.AddMonths(-1)),
            ChangeReason = "Financial report test",
            CreatedAtUtc = Now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"A-{property.Id}-{Guid.NewGuid():N}"[..16],
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(Now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(Now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(Now.AddMonths(-1)),
            BaseRentAmount = 1000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, 1, Now),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            CreatedByUserId = 1,
            LeaseManagement = management,
        };
        management.TenantAccount = account;
        _context.Db.AddRange(account, party, agreement);
        await _context.Db.SaveChangesAsync();
        return agreement;
    }

    private async Task SeedPaymentAsync(LeaseAgreement lease, decimal amount, DateTime paidAt)
    {
        var account = lease.LeaseManagement!.TenantAccount!;
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(paidAt),
            DueOn = DateOnly.FromDateTime(paidAt),
            PostedAtUtc = paidAt,
            Description = "Rent charge",
            BusinessKey = $"charge:{Guid.NewGuid():N}",
            LeaseAgreementId = lease.Id,
            CreatedByUserId = 1,
        };
        _context.Db.TenantLedgerEntries.Add(charge);
        await _context.Db.SaveChangesAsync();
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(paidAt),
            PostedAtUtc = paidAt,
            Description = "Rent receipt",
            BusinessKey = $"receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        _context.Db.TenantLedgerEntries.Add(receipt);
        await _context.Db.SaveChangesAsync();
        _context.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = amount,
            AllocatedAtUtc = paidAt,
            BusinessKey = $"allocation:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task SeedSecurityDepositAsync(
        LeaseAgreement lease,
        DateTime receiptAt,
        DateTime transferredAt,
        string suffix = "transfer",
        decimal received = 1000m,
        decimal transferred = 400m)
    {
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccountId = lease.LeaseManagement!.TenantAccount!.Id,
            OriginatingAgreementId = lease.Id,
            Currency = "USD",
            CreatedAtUtc = receiptAt,
            CreatedByUserId = 1,
        };
        _context.Db.SecurityDepositAccounts.Add(depositAccount);
        await _context.Db.SaveChangesAsync();
        _context.Db.SecurityDepositEntries.AddRange(
            new SecurityDepositEntry
            {
                PortfolioId = PortfolioId,
                SecurityDepositAccountId = depositAccount.Id,
                EntryType = SecurityDepositEntryType.Receipt,
                Direction = SecurityDepositDirection.Increase,
                Amount = received,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(receiptAt),
                PostedAtUtc = receiptAt,
                BusinessKey = $"deposit:receipt:{suffix}",
                Description = "Deposit received",
                LeaseAgreementId = lease.Id,
                CreatedByUserId = 1,
            },
            new SecurityDepositEntry
            {
                PortfolioId = PortfolioId,
                SecurityDepositAccountId = depositAccount.Id,
                EntryType = SecurityDepositEntryType.TransferOut,
                Direction = SecurityDepositDirection.Decrease,
                Amount = transferred,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(transferredAt),
                PostedAtUtc = transferredAt,
                BusinessKey = $"deposit:transfer-out:{suffix}",
                Description = "Deposit transferred out",
                TransferPublicId = Guid.NewGuid(),
                LeaseAgreementId = lease.Id,
                CreatedByUserId = 1,
            });
        await _context.Db.SaveChangesAsync();
    }

    private async Task WriteArtifactsAsync(string name, IReadOnlyCollection<CapturedCommand> commands)
    {
        var selects = CaptureCommandSql(commands);
        await WriteSqlArtifactAsync(name, selects.Select(command => command.Sql));
        await WriteExplainArtifactAsync(name, await ExplainAsync(selects));
    }

    private static IReadOnlyList<CapturedCommand> CaptureCommandSql(IReadOnlyCollection<CapturedCommand> commands) =>
        commands
            .Where(command => command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private async Task<IReadOnlyList<string>> ExplainAsync(IEnumerable<CapturedCommand> captured)
    {
        var explain = new List<string>();
        foreach (var command in captured)
        {
            explain.Add(await ExplainAsync(command));
        }

        return explain;
    }

    private async Task<string> ExplainAsync(CapturedCommand captured)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {captured.Sql}",
            connection);
        foreach (var parameter in captured.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, rows);
    }

    private static Task WriteSqlArtifactAsync(string section, IEnumerable<string> entries) =>
        WriteArtifactAsync("FOUNDATION_REPORT_SQL_OUTPUT", "L05-reports-sql.txt", section, entries);

    private static Task WriteExplainArtifactAsync(string section, IEnumerable<string> entries) =>
        WriteArtifactAsync("FOUNDATION_REPORT_EXPLAIN_OUTPUT", "L05-reports-explain.txt", section, entries);

    private static async Task WriteArtifactAsync(
        string environmentVariable,
        string fallbackFileName,
        string section,
        IEnumerable<string> entries)
    {
        var path = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = ResolveArtifactPath(
                Path.Combine("Docs/Testing/Results/2026-07-16-foundation-parallel", fallbackFileName));
        }
        else
        {
            path = ResolveArtifactPath(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.AppendAllTextAsync(path, $"""

            ## {section}

            {string.Join($"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}", entries)}

            """);
    }

    private static string ResolveArtifactPath(string path)
    {
        if (Path.IsPathFullyQualified(path)) return path;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            path);
    }

    private static bool ContainsSqlOrParameter(CapturedCommand command, string value) =>
        command.Sql.Contains(value, StringComparison.Ordinal) ||
        command.Parameters.Any(parameter =>
            string.Equals(Convert.ToString(parameter.Value), value, StringComparison.Ordinal));

    private sealed record CapturedParameter(string Name, object? Value);

    private sealed record CapturedCommand(string Sql, IReadOnlyList<CapturedParameter> Parameters);

    private sealed class QueryRecorder(List<CapturedCommand> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        private void Capture(DbCommand command)
        {
            commands.Add(new CapturedCommand(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => new CapturedParameter(parameter.ParameterName, parameter.Value))
                    .ToArray()));
        }
    }
}
