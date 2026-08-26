using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class UnitMoneyPostgreSqlTests : IAsyncLifetime
{
    private static readonly WorkspaceReadScope Scope = new(
        1,
        1,
        Guid.Parse("8c72f7a4-6346-4a4a-a3f4-2ee807ed8ce0"),
        44,
        9);
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public UnitMoneyPostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task MoneySections_ArePagedAndAggregatedInPostgreSql()
    {
        var service = new TenantAccountQueryService(_context.Db, TimeProvider.System);
        var activity = service.BuildGlobalEntryPageQuery(Scope,
            new TenantLedgerEntryGlobalListQuery
            {
                TenantAccountId = 41,
                Skip = 10,
                Take = 10,
                Sort = "-postedAtUtc",
            });
        var charges = service.BuildChargePageQuery(Scope, 41,
            new TenantChargeListQuery
            {
                Skip = 10,
                Take = 10,
                Sort = "-openAmount",
            });
        var deposits = service.BuildDepositPageQuery(Scope,
            new TenantAccountDepositListQuery
            {
                TenantAccountId = 41,
                Skip = 10,
                Take = 10,
                Sort = "-heldBalance",
            });
        var activitySql = activity.ToQueryString();
        var chargeSql = charges.ToQueryString();
        var depositSql = deposits.ToQueryString();

        CaptureSql("ACCOUNT_ACTIVITY_PAGE", activitySql);
        CaptureSql("CHARGE_ALLOCATION_OPEN_AMOUNT_PAGE", chargeSql);
        CaptureSql("DEPOSIT_PAGE", depositSql);

        foreach (var sql in new[] { activitySql, chargeSql, depositSql })
        {
            sql.Should().Contain("rc_api_effective_capability_scopes");
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            sql.Should().Contain("OFFSET");
        }
        chargeSql.Should().Contain("vw_tenant_charge_balances");
        chargeSql.Should().Contain("OpenAmount");
        chargeSql.Should().Contain("NetAllocations");
        depositSql.Should().Contain("vw_security_deposit_balances");
        depositSql.Should().Contain("HeldBalance");

        (await activity.ToListAsync()).Should().BeEmpty();
        (await charges.ToListAsync()).Should().BeEmpty();
        (await deposits.ToListAsync()).Should().BeEmpty();

        var now = DateTime.UtcNow;
        var singleRental = new Property
        {
            PortfolioId = Scope.PortfolioId,
            Name = "PostgreSQL SingleRental",
            RentalStructure = RentalStructure.SingleRental,
            AddressLine1 = "1 SQL Lane",
            City = "Test",
            State = "NY",
            PostalCode = "10001",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = Scope.PortfolioId,
            Property = singleRental,
            UnitNumber = "SingleRental",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(singleRental, unit);
        await _context.Db.SaveChangesAsync();
        (await _context.Db.Properties
                .Where(property => property.Id == singleRental.Id)
                .Select(property => property.RentalStructure)
                .SingleAsync())
            .Should().Be(RentalStructure.SingleRental);

        var expenseService = new ExpenseService(
            _context.Db,
            Mock.Of<IFileStorage>(),
            TimeProvider.System,
            Mock.Of<IWriteExecutor>());
        var loanService = new LoanService(
            _context.Db,
            TimeProvider.System,
            Mock.Of<IWriteExecutor>());

        _commands.Clear();
        var unitExpensePage = await expenseService.ListPageAsync(
            Scope,
            propertyId: null,
            unitId: unit.Id,
            workOrderId: null,
            workOrderLinkedOnly: false,
            new ExpenseListQuery
            {
                Skip = 10,
                Take = 10,
                Sort = "-incurredAt",
            });
        var unitExpenseCommands = _commands.ToArray();
        CaptureSql("UNIT_OPERATING_EXPENSE_PAGE_COMMAND_COUNT", unitExpenseCommands.Length.ToString());
        for (var i = 0; i < unitExpenseCommands.Length; i++)
            CaptureSql($"UNIT_OPERATING_EXPENSE_PAGE_COMMAND_{i + 1}", unitExpenseCommands[i]);
        unitExpensePage.Items.Should().BeEmpty();
        unitExpenseCommands.Should().HaveCount(2);
        unitExpenseCommands[0].Should().ContainEquivalentOf("count(*)");
        unitExpenseCommands[1].Should().Contain("rc_api_effective_capability_scopes");
        unitExpenseCommands[1].Should().Contain("UnitId");
        unitExpenseCommands[1].Should().ContainEquivalentOf("sum(");
        unitExpenseCommands[1].Should().Contain("ORDER BY");
        unitExpenseCommands[1].Should().Contain("LIMIT");
        unitExpenseCommands[1].Should().Contain("OFFSET");

        _commands.Clear();
        var propertyExpensePage = await expenseService.ListPageAsync(
            Scope,
            propertyId: singleRental.Id,
            unitId: null,
            workOrderId: null,
            workOrderLinkedOnly: false,
            new ExpenseListQuery
            {
                OperationalScope = ExpenseOperationalScope.Property,
                Skip = 10,
                Take = 10,
                Sort = "-incurredAt",
            });
        var propertyExpenseCommands = _commands.ToArray();
        CaptureSql("SINGLE_RENTAL_PROPERTY_EXPENSE_PAGE_COMMAND_COUNT", propertyExpenseCommands.Length.ToString());
        for (var i = 0; i < propertyExpenseCommands.Length; i++)
            CaptureSql($"SINGLE_RENTAL_PROPERTY_EXPENSE_PAGE_COMMAND_{i + 1}", propertyExpenseCommands[i]);
        propertyExpensePage.Items.Should().BeEmpty();
        propertyExpenseCommands.Should().HaveCount(2);
        propertyExpenseCommands[0].Should().ContainEquivalentOf("count(*)");
        propertyExpenseCommands[1].Should().Contain("rc_api_effective_capability_scopes");
        propertyExpenseCommands[1].Should().Contain("PropertyId");
        propertyExpenseCommands[1].Should().Contain("OperationalScope");
        propertyExpenseCommands[1].Should().ContainEquivalentOf("sum(");
        propertyExpenseCommands[1].Should().Contain("ORDER BY");
        propertyExpenseCommands[1].Should().Contain("LIMIT");
        propertyExpenseCommands[1].Should().Contain("OFFSET");

        _commands.Clear();
        var financingPage = await loanService.ListPageAsync(
            Scope,
            singleRental.Id,
            new ListQuery
            {
                Skip = 10,
                Take = 10,
                Sort = "-startDate",
            });
        var financingCommands = _commands.ToArray();
        CaptureSql("SINGLE_RENTAL_FINANCING_PAGE_COMMAND_COUNT", financingCommands.Length.ToString());
        for (var i = 0; i < financingCommands.Length; i++)
            CaptureSql($"SINGLE_RENTAL_FINANCING_PAGE_COMMAND_{i + 1}", financingCommands[i]);
        financingPage.Items.Should().BeEmpty();
        financingCommands.Should().HaveCount(2);
        financingCommands[0].Should().ContainEquivalentOf("count(*)");
        financingCommands[1].Should().Contain("rc_api_effective_capability_scopes");
        financingCommands[1].Should().Contain("Properties");
        financingCommands[1].Should().Contain("PropertyId");
        financingCommands[1].Should().Contain("ORDER BY");
        financingCommands[1].Should().Contain("LIMIT");
        financingCommands[1].Should().Contain("OFFSET");
    }

    [Fact]
    public async Task CrossScopeAccount_IsDeniedBeforeProjection()
    {
        var now = DateTime.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"unit-money-cross-scope-{suffix}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Unit money cross-scope verifier",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"Unit money cross-scope {suffix}",
            ManagementCompanyName = "Unit money verifier",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(user, portfolio);
        await _context.Db.SaveChangesAsync();

        var authorizedProperty = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Authorized property {suffix}",
            AddressLine1 = "1 Authorized Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var crossScopeProperty = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Cross-scope property {suffix}",
            AddressLine1 = "2 Cross-Scope Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var authorizedUnit = new Unit
        {
            PortfolioId = portfolio.Id,
            Property = authorizedProperty,
            UnitNumber = "A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var crossScopeUnit = new Unit
        {
            PortfolioId = portfolio.Id,
            Property = crossScopeProperty,
            UnitNumber = "B",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(authorizedProperty, crossScopeProperty, authorizedUnit, crossScopeUnit);
        await _context.Db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.PropertyManager).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = assignment,
            PortfolioId = portfolio.Id,
            PropertyId = authorizedProperty.Id,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        var crossScopeManagement = new LeaseManagement
        {
            PortfolioId = portfolio.Id,
            PropertyId = crossScopeProperty.Id,
            UnitId = crossScopeUnit.Id,
            RelationshipNumber = $"CROSS-{suffix}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.AddRange(assignment, session, crossScopeManagement);
        await _context.Db.SaveChangesAsync();

        var crossScopeAccount = new TenantAccount
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = crossScopeManagement.Id,
            AccountNumber = $"TA-CROSS-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var crossScopeAgreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = crossScopeManagement.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-CROSS-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 875m,
            RentDueDay = 1,
            SecurityDepositObligation = 0m,
            LateFeeAmount = 0m,
            GracePeriodDays = 0,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                portfolio.Id,
                user.Id,
                now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        _context.Db.AddRange(crossScopeAccount, crossScopeAgreement);
        await _context.Db.SaveChangesAsync();
        var crossScopeEntry = new TenantLedgerEntry
        {
            PortfolioId = portfolio.Id,
            TenantAccountId = crossScopeAccount.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 875m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            DueOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = "Existing cross-scope rent charge",
            BusinessKey = $"cross-scope:{suffix}",
            LeaseAgreementId = crossScopeAgreement.Id,
            CreatedByUserId = user.Id,
        };
        _context.Db.TenantLedgerEntries.Add(crossScopeEntry);
        await _context.Db.SaveChangesAsync();

        _commands.Clear();
        var unrestrictedSource = await (
            from account in _context.Db.TenantAccounts.AsNoTracking()
            join management in _context.Db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _context.Db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join entry in _context.Db.TenantLedgerEntries.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { entry.PortfolioId, entry.TenantAccountId }
            where account.Id == crossScopeAccount.Id
                && entry.Id == crossScopeEntry.Id
            select new
            {
                TenantAccountId = account.Id,
                TenantLedgerEntryId = entry.Id,
                entry.LeaseAgreementId,
                management.PropertyId,
            }).SingleAsync();
        unrestrictedSource.TenantAccountId.Should().Be(crossScopeAccount.Id);
        unrestrictedSource.TenantLedgerEntryId.Should().Be(crossScopeEntry.Id);
        unrestrictedSource.LeaseAgreementId.Should().Be(crossScopeAgreement.Id);
        unrestrictedSource.PropertyId.Should().Be(crossScopeProperty.Id);

        var limitedScope = new WorkspaceReadScope(
            portfolio.Id,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
        var query = new TenantAccountQueryService(_context.Db, TimeProvider.System)
            .BuildEntryDetailQuery(limitedScope, crossScopeAccount.Id, crossScopeEntry.Id);
        _commands.Clear();

        var projectedEntries = await query.ToListAsync();
        var commands = _commands.ToArray();
        CaptureSql("CROSS_SCOPE_ACCOUNT_DETAIL_COMMAND_COUNT", commands.Length.ToString());
        for (var i = 0; i < commands.Length; i++)
            CaptureSql($"CROSS_SCOPE_ACCOUNT_DETAIL_COMMAND_{i + 1}", commands[i]);

        projectedEntries.Should().BeEmpty();
        commands.Should().ContainSingle();
        commands[0].Should().Contain("rc_api_effective_capability_scopes");
        commands[0].Should().Contain("TenantAccounts");
        commands[0].Should().Contain("TenantLedgerEntries");
    }

    private void CaptureSql(string label, string sql)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(sql);
    }

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
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
}
