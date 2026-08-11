using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class ReportsServicePostgreSqlTests(MigratedPostgreSqlFixture postgres)
{
    private static readonly DateTime FrozenBusinessNowUtc = new(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task OwnerDistributionsController_OmittedYearUsesPortfolioBusinessYearOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1, nameof(OwnerDistributionsController_OmittedYearUsesPortfolioBusinessYearOnPostgreSql));
        SeedFrozenBusinessDate(context, FrozenBusinessNowUtc);
        await context.ActivateApiScopeAsync(scope);
        var controller = CreateReportsController(context, scope);

        var result = await controller.OwnerDistributions(year: null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var report = ok.Value.Should().BeOfType<OwnerDistributionsResponse>().Subject;
        report.Year.Should().Be(2027);
    }

    [Fact]
    public async Task Vendor1099Controller_OmittedYearUsesPortfolioBusinessYearOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1, nameof(Vendor1099Controller_OmittedYearUsesPortfolioBusinessYearOnPostgreSql));
        SeedFrozenBusinessDate(context, FrozenBusinessNowUtc);
        await context.ActivateApiScopeAsync(scope);
        var controller = CreateReportsController(context, scope);

        var result = await controller.Vendor1099(year: null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var report = ok.Value.Should().BeOfType<Vendor1099Response>().Subject;
        report.Year.Should().Be(2027);
    }

    [Fact]
    public async Task GeneralLedger_ExecutesOneAuthorizedWindowQueryOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1, nameof(GeneralLedger_ExecutesOneAuthorizedWindowQueryOnPostgreSql));
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Maple",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.Properties.Add(property);
        await context.Db.SaveChangesAsync();
        context.Db.Expenses.Add(new Expense
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            OperationalScope = ExpenseOperationalScope.Property,
            Category = ScheduleECategory.Repairs,
            Description = "Plumbing repair",
            Status = ExpenseStatus.Paid,
            Amount = 125m,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.Db.SaveChangesAsync();
        await context.Db.Database.OpenConnectionAsync();
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);

        var sut = new ReportsService(
            context.Db,
            new OwnerStatementService(context.Db, TimeProvider.System),
            new ScheduleEService(context.Db),
            new PropertyDispositionService(context.Db, TimeProvider.System),
            TimeProvider.System);

        var report = await sut.GetGeneralLedgerAsync(scope, new ReportRangeQuery
        {
            From = now.AddDays(-1),
            To = now.AddDays(1),
        });

        var entry = report.Entries.Should().ContainSingle().Subject;
        entry.Description.Should().Be("Plumbing repair");
        entry.Amount.Should().Be(-125m);
        entry.RunningBalance.Should().Be(-125m);
        report.TotalIncome.Should().Be(0m);
        report.TotalExpense.Should().Be(125m);
        report.ClosingBalance.Should().Be(-125m);
    }

    [Fact]
    public async Task DashboardAccounting_UsesCanonicalCashFlowIncomeAndAllocationAwareExpensesOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var allPropertiesScope = context.Db.SeedAdministratorScope(
            1,
            nameof(DashboardAccounting_UsesCanonicalCashFlowIncomeAndAllocationAwareExpensesOnPostgreSql));
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = SeedProperty(context, "Cash Flow Maple", now);
        var unit = SeedUnit(context, property, "2B", now);
        var account = SeedTenantAccount(context, property, unit, now);
        var selectedPropertyScope = SeedSelectedWorkspaceAdministratorScope(
            context,
            property.Id,
            nameof(DashboardAccounting_UsesCanonicalCashFlowIncomeAndAllocationAwareExpensesOnPostgreSql));

        SeedReceiptAllocation(
            context,
            account,
            TenantLedgerEntryType.ManualCharge,
            1000m,
            now,
            "manual-charge-income");
        SeedReceiptAllocation(
            context,
            account,
            TenantLedgerEntryType.DepositCharge,
            300m,
            now,
            "security-deposit-liability");
        SeedPortfolioExpenses(context, unit, now);
        SeedUnmatchedBankActivity(context, property, now);
        context.Db.ChangeTracker.Clear();

        var dashboard = new DashboardService(
            context.Db,
            new AuditDescriber(),
            new FixedTimeProvider(now));

        await context.ActivateApiScopeAsync(allPropertiesScope);
        var allProperties = await dashboard.GetDashboardAsync(allPropertiesScope);

        allProperties.Should().NotBeNull();
        allProperties!.Accounting.PaidThisMonthAmount.Should().Be(1000m);
        allProperties.Accounting.ExpensesThisMonthAmount.Should().Be(445m);
        allProperties.Accounting.NetThisMonth.Should().Be(555m);

        await SetRequestScopeAsync(context, selectedPropertyScope);
        var selectedProperty = await dashboard.GetDashboardAsync(selectedPropertyScope);

        selectedProperty.Should().NotBeNull();
        selectedProperty!.Accounting.PaidThisMonthAmount.Should().Be(1000m);
        selectedProperty.Accounting.ExpensesThisMonthAmount.Should().Be(320m);
        selectedProperty.Accounting.NetThisMonth.Should().Be(680m);
    }

    [Fact]
    public async Task DashboardAccounting_ExcludesUnpaidExpenseStatuses()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1,
            nameof(DashboardAccounting_ExcludesUnpaidExpenseStatuses));
        var now = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = SeedProperty(context, "H3 dashboard property", now);
        context.Db.Expenses.AddRange(
            new Expense
            {
                PortfolioId = 1,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Paid dashboard expense",
                Status = ExpenseStatus.Paid,
                Amount = 150m,
                IncurredAt = now.AddDays(-2),
                PaidAt = now.AddDays(-1),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Expense
            {
                PortfolioId = 1,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Pending dashboard expense",
                Status = ExpenseStatus.Pending,
                Amount = 200m,
                IncurredAt = now.AddDays(-2),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Expense
            {
                PortfolioId = 1,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Draft dashboard expense",
                Status = ExpenseStatus.Draft,
                Amount = 300m,
                IncurredAt = now.AddDays(-2),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Expense
            {
                PortfolioId = 1,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Rejected dashboard expense",
                Status = ExpenseStatus.Rejected,
                Amount = 400m,
                IncurredAt = now.AddDays(-2),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await context.Db.SaveChangesAsync();
        await context.ActivateApiScopeAsync(scope);

        var dashboard = await new DashboardService(
                context.Db,
                new AuditDescriber(),
                new FixedTimeProvider(now))
            .GetDashboardAsync(scope);

        dashboard.Should().NotBeNull();
        dashboard!.Accounting.ExpensesThisMonthAmount.Should().Be(150m);
        dashboard.Accounting.NetThisMonth.Should().Be(-150m);
    }

    [Fact]
    public async Task DashboardAccounting_PreservesAllocationAndReversalAwareReceivableValuesOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1,
            nameof(DashboardAccounting_PreservesAllocationAndReversalAwareReceivableValuesOnPostgreSql));
        var now = DateTime.UtcNow;
        var property = SeedProperty(context, "Receivables Maple", now);
        var unit = SeedUnit(context, property, "3C", now);
        var account = SeedTenantAccount(context, property, unit, now);
        SeedDashboardReceivables(context, account, now);
        context.Db.ChangeTracker.Clear();

        await context.ActivateApiScopeAsync(scope);
        var dashboard = await new DashboardService(
                context.Db,
                new AuditDescriber(),
                new FixedTimeProvider(now))
            .GetDashboardAsync(scope);

        dashboard.Should().NotBeNull();
        dashboard!.Accounting.OverdueAmount.Should().Be(60m,
            "the overdue KPI subtracts allocations and exact reversals from past-due debits");
        var businessDate = DateOnly.FromDateTime(now);
        var expectedDueThisMonth = businessDate.AddDays(-2).Month == businessDate.Month
            ? 300m
            : 200m;
        dashboard.Accounting.DueThisMonthAmount.Should().Be(expectedDueThisMonth,
            "the due KPI includes original less reversed charges without subtracting payments");
        dashboard.Accounting.PaidThisMonthAmount.Should().Be(40m);
        dashboard.Accounting.ExpensesThisMonthAmount.Should().Be(0m);
        dashboard.Accounting.NetThisMonth.Should().Be(40m);
    }

    [Fact]
    public async Task RentLedger_ExecutesCanonicalCapabilityScopePipelineOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1, nameof(RentLedger_ExecutesCanonicalCapabilityScopePipelineOnPostgreSql));
        await ActivateApiScopeAsync(context, scope);
        var sut = new ReportsService(
            context.Db,
            new OwnerStatementService(context.Db, TimeProvider.System),
            new ScheduleEService(context.Db),
            new PropertyDispositionService(context.Db, TimeProvider.System),
            TimeProvider.System);
        var access = new LeaseManagementReadContext(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision);

        var report = await sut.GetRentLedgerAsync(access, new ReportRangeQuery());

        report.Leases.Should().BeEmpty();
        report.TotalCharged.Should().Be(0m);
        report.TotalCredits.Should().Be(0m);
        report.TotalBalance.Should().Be(0m);
    }

    private static async Task ActivateApiScopeAsync(
        MigratedPostgreSqlTestContext context,
        RentalCommand.Core.Authorization.WorkspaceReadScope scope)
    {
        await context.Db.Database.OpenConnectionAsync();
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);
    }

    private static ReportsController CreateReportsController(
        MigratedPostgreSqlTestContext context,
        WorkspaceReadScope scope)
    {
        var service = new ReportsService(
            context.Db,
            new OwnerStatementService(context.Db, TimeProvider.System),
            new ScheduleEService(context.Db),
            new PropertyDispositionService(context.Db, TimeProvider.System),
            TimeProvider.System);
        var controller = new ReportsController(service, TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] =
            new ActiveAccessContext(
                scope.SessionId,
                scope.UserId,
                scope.AccessContextId,
                scope.PortfolioId,
                scope.AccessRevision,
                WorkspaceExperience.Management,
                WorkspaceMembershipId: 1,
                DefaultExperience: WorkspaceExperience.Management);
        return controller;
    }

    private static void SeedFrozenBusinessDate(
        MigratedPostgreSqlTestContext context,
        DateTime businessNowUtc)
    {
        var clock = context.Db.SimulationClocks.SingleOrDefault(clock => clock.Id == 1);
        if (clock is null)
        {
            context.Db.SimulationClocks.Add(new SimulationClock { Id = 1 });
            clock = context.Db.SimulationClocks.Local.Single(clock => clock.Id == 1);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = businessNowUtc;
        clock.RealAnchorUtc = businessNowUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = businessNowUtc;
        context.Db.SaveChanges();
        context.Db.ChangeTracker.Clear();
    }

    private static async Task SetRequestScopeAsync(
        MigratedPostgreSqlTestContext context,
        WorkspaceReadScope scope)
    {
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);
    }

    private static Property SeedProperty(
        MigratedPostgreSqlTestContext context,
        string name,
        DateTime now)
    {
        var property = new Property
        {
            PortfolioId = 1,
            Name = name,
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.Properties.Add(property);
        context.Db.SaveChanges();
        return property;
    }

    private static Unit SeedUnit(
        MigratedPostgreSqlTestContext context,
        Property property,
        string unitNumber,
        DateTime now)
    {
        var unit = new Unit
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitNumber = unitNumber,
            MarketRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.Units.Add(unit);
        context.Db.SaveChanges();
        return unit;
    }

    private static TenantAccount SeedTenantAccount(
        MigratedPostgreSqlTestContext context,
        Property property,
        Unit unit,
        DateTime now)
    {
        var relationship = new LeaseManagement
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"REL-CASH-{Guid.NewGuid():N}"[..18],
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        context.Db.LeaseManagements.Add(relationship);
        context.Db.SaveChanges();

        var account = new TenantAccount
        {
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-CASH-{relationship.Id}",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        context.Db.TenantAccounts.Add(account);
        context.Db.SaveChanges();
        return account;
    }

    private static void SeedReceiptAllocation(
        MigratedPostgreSqlTestContext context,
        TenantAccount account,
        TenantLedgerEntryType chargeType,
        decimal amount,
        DateTime effectiveAt,
        string key)
    {
        var charge = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = chargeType,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(effectiveAt),
            DueOn = DateOnly.FromDateTime(effectiveAt),
            PostedAtUtc = effectiveAt,
            Description = chargeType.ToString(),
            BusinessKey = $"charge:{key}:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(effectiveAt),
            PostedAtUtc = effectiveAt,
            Description = "Payment received",
            BusinessKey = $"receipt:{key}:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        context.Db.TenantLedgerEntries.AddRange(charge, receipt);
        context.Db.SaveChanges();
        context.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = amount,
            AllocatedAtUtc = effectiveAt,
            BusinessKey = $"allocation:{key}:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
        context.Db.SaveChanges();
    }

    private static void SeedDashboardReceivables(
        MigratedPostgreSqlTestContext context,
        TenantAccount account,
        DateTime now)
    {
        var businessDate = DateOnly.FromDateTime(now);
        var monthEnd = new DateOnly(
            businessDate.Year,
            businessDate.Month,
            DateTime.DaysInMonth(businessDate.Year, businessDate.Month));
        var pastDue = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 100m,
            Currency = "USD",
            EffectiveOn = businessDate.AddDays(-3),
            DueOn = businessDate.AddDays(-2),
            PostedAtUtc = now,
            Description = "Partially paid past-due charge",
            BusinessKey = $"dashboard-past-due:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 40m,
            Currency = "USD",
            EffectiveOn = businessDate,
            PostedAtUtc = now,
            Description = "Partial payment",
            BusinessKey = $"dashboard-receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        var reversedCharge = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 25m,
            Currency = "USD",
            EffectiveOn = businessDate.AddDays(-3),
            DueOn = businessDate.AddDays(-2),
            PostedAtUtc = now,
            Description = "Reversed charge",
            BusinessKey = $"dashboard-reversed-charge:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        var futureDue = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 200m,
            Currency = "USD",
            EffectiveOn = businessDate,
            DueOn = monthEnd,
            PostedAtUtc = now,
            Description = "Current-month future charge",
            BusinessKey = $"dashboard-future-charge:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        context.Db.TenantLedgerEntries.AddRange(pastDue, receipt, reversedCharge, futureDue);
        context.Db.SaveChanges();

        context.Db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.Reversal,
            Direction = TenantLedgerDirection.Credit,
            Amount = 25m,
            Currency = "USD",
            EffectiveOn = businessDate,
            PostedAtUtc = now,
            Description = "Exact charge reversal",
            BusinessKey = $"dashboard-charge-reversal:{Guid.NewGuid():N}",
            ReversesEntryId = reversedCharge.Id,
            CreatedByUserId = 1,
        });
        context.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            DebitEntryId = pastDue.Id,
            CreditEntryId = receipt.Id,
            Amount = 40m,
            AllocatedAtUtc = now,
            BusinessKey = $"dashboard-allocation:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
        context.Db.SaveChanges();
    }

    private static void SeedPortfolioExpenses(
        MigratedPostgreSqlTestContext context,
        Unit unit,
        DateTime now)
    {
        var allocatedExpense = new Expense
        {
            PortfolioId = 1,
            OperationalScope = ExpenseOperationalScope.Portfolio,
            Category = ScheduleECategory.Repairs,
            Description = "Portfolio repair allocated to unit",
            Status = ExpenseStatus.Paid,
            Amount = 320m,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unallocatedPortfolioExpense = new Expense
        {
            PortfolioId = 1,
            OperationalScope = ExpenseOperationalScope.Portfolio,
            Category = ScheduleECategory.Repairs,
            Description = "Portfolio bookkeeping",
            Status = ExpenseStatus.Paid,
            Amount = 125m,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.Expenses.AddRange(allocatedExpense, unallocatedPortfolioExpense);
        context.Db.SaveChanges();
        context.Db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = 1,
            ExpenseId = allocatedExpense.Id,
            TargetKind = ExpenseAllocationTargetKind.Unit,
            UnitId = unit.Id,
            Amount = 320m,
            CreatedAt = now,
        });
        context.Db.SaveChanges();
    }

    private static void SeedUnmatchedBankActivity(
        MigratedPostgreSqlTestContext context,
        Property property,
        DateTime now)
    {
        var connection = new BankConnection
        {
            PortfolioId = 1,
            InstitutionName = "Test Bank",
            AccountName = "Operating",
            AccountMask = "0000",
            ExternalItemIdCipherText = "test-item",
            ExternalAccountIdCipherText = "test-account",
            ExternalItemIdHash = Guid.NewGuid().ToString("N"),
            ExternalAccountIdHash = Guid.NewGuid().ToString("N"),
            ExternalAccessTokenCipherText = "test-token",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.BankConnections.Add(connection);
        context.Db.SaveChanges();

        context.Db.BankTransactions.AddRange(
            new BankTransaction
            {
                PortfolioId = 1,
                BankConnectionId = connection.Id,
                PropertyId = null,
                ProviderTransactionId = $"trust-deposit-{Guid.NewGuid():N}",
                PostedAt = now,
                Description = "Unmatched trust deposit",
                Amount = 1818m,
                MatchStatus = "Unmatched",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new BankTransaction
            {
                PortfolioId = 1,
                BankConnectionId = connection.Id,
                PropertyId = property.Id,
                ProviderTransactionId = $"unmatched-withdrawal-{Guid.NewGuid():N}",
                PostedAt = now,
                Description = "Unmatched withdrawal",
                Amount = -1675m,
                MatchStatus = "Unmatched",
                CreatedAt = now,
                UpdatedAt = now,
            });
        context.Db.SaveChanges();
    }

    private static WorkspaceReadScope SeedSelectedWorkspaceAdministratorScope(
        MigratedPostgreSqlTestContext context,
        int propertyId,
        string fixtureName)
    {
        var now = DateTime.UtcNow;
        var normalizedFixtureName = fixtureName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant();
        var email = $"{normalizedFixtureName}-selected-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = $"{fixtureName} Selected Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SelectedProperties =
            [
                new MembershipRoleAssignmentProperty
                {
                    PortfolioId = 1,
                    PropertyId = propertyId,
                },
            ],
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

        context.Db.AddRange(assignment, session);
        context.Db.SaveChanges();

        return new WorkspaceReadScope(
            1,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
