using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name3)]
public sealed class AccountingAuthorizationPostgreSqlTests(MigratedPostgreSqlFixture fixture)
{
    [Fact]
    public async Task PropertyManager_AssignedPropertyASeesOnlyPropertyALines()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var page = await scenario.Service.GetGeneralLedgerAsync(
            scenario.ManagerScope, new GeneralLedgerQuery { Take = 200 });

        page.Items.Should().HaveCount(7);
        page.Items.Should().OnlyContain(row =>
            row.PropertyId == scenario.PropertyA.Id ||
            row.UnitId == scenario.UnitA.Id ||
            row.TenantAccountId == scenario.TenantAccountA.Id);
    }

    [Fact]
    public async Task PropertyManager_GuessedPropertyBFilterReturnsNoLines()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var page = await scenario.Service.GetGeneralLedgerAsync(
            scenario.ManagerScope,
            new GeneralLedgerQuery { PropertyId = scenario.PropertyB.Id, Take = 200 });

        page.TotalCount.Should().Be(0);
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task PropertyManager_StatementsAggregateOnlyAuthorizedProperties()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var statement = await scenario.Service.GetTrialBalanceAsync(
            scenario.ManagerScope,
            new StatementQuery { To = new DateOnly(2026, 8, 31), Currency = "USD" });

        statement.TotalDebits.Should().Be(175m);
        statement.TotalCredits.Should().Be(175m);
        statement.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task PropertyManager_CannotOpenJournalContainingUnauthorizedProperty()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var detail = await scenario.Service.GetJournalDetailAsync(
            scenario.ManagerScope, scenario.MixedJournalPublicId);
        var sourceJournals = await scenario.Service.GetSourceJournalsAsync(
            scenario.ManagerScope,
            new SourceJournalQuery { SourceType = JournalSourceType.OpeningBalance, SourceId = 8303 });

        detail.Should().BeNull();
        sourceJournals.Should().BeEmpty();
    }

    [Fact]
    public async Task Administrator_SeesSameCompleteMixedJournal()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.AdministratorScope);

        var detail = await scenario.Service.GetJournalDetailAsync(
            scenario.AdministratorScope, scenario.MixedJournalPublicId);

        detail.Should().NotBeNull();
        detail!.Lines.Should().HaveCount(2);
        detail.TotalDebits.Should().Be(50m);
        detail.TotalCredits.Should().Be(50m);
    }

    [Fact]
    public async Task TenantAccountAuthorization_DeniesAccountOutsideAssignedProperty()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var allowed = await context.Db.CanReadTenantAccountAsync(
            scenario.ManagerScope,
            scenario.TenantAccountA.Id,
            CapabilityKeys.MoneyBalancesRead,
            DateTime.UtcNow,
            CancellationToken.None);
        var denied = await context.Db.CanReadTenantAccountAsync(
            scenario.ManagerScope,
            scenario.TenantAccountB.Id,
            CapabilityKeys.MoneyBalancesRead,
            DateTime.UtcNow,
            CancellationToken.None);

        allowed.Should().BeTrue();
        denied.Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizationFailure_DoesNotRevealWhetherHiddenResourceExists()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var hiddenJournal = await scenario.Service.GetJournalDetailAsync(
            scenario.ManagerScope, scenario.PropertyBJournalPublicId);
        var absentJournal = await scenario.Service.GetJournalDetailAsync(
            scenario.ManagerScope, Guid.NewGuid());
        var hiddenAccount = await context.Db.CanReadTenantAccountAsync(
            scenario.ManagerScope, scenario.TenantAccountB.Id, CapabilityKeys.MoneyBalancesRead,
            DateTime.UtcNow, CancellationToken.None);
        var absentAccount = await context.Db.CanReadTenantAccountAsync(
            scenario.ManagerScope, int.MaxValue, CapabilityKeys.MoneyBalancesRead,
            DateTime.UtcNow, CancellationToken.None);

        hiddenJournal.Should().BeNull();
        absentJournal.Should().BeNull();
        hiddenAccount.Should().BeFalse();
        absentAccount.Should().BeFalse();
    }

    [Fact]
    public async Task AccountingAuthorization_ResolvesUnitAndTenantDimensionsInSqlAndRestrictsOwnerOrUnallocatedLines()
    {
        await using var context = await fixture.CreateContextAsync();
        var scenario = await SeedScenarioAsync(context);
        await context.ActivateApiScopeAsync(scenario.ManagerScope);

        var manager = await scenario.Service.GetGeneralLedgerAsync(
            scenario.ManagerScope, new GeneralLedgerQuery { Take = 200 });
        await context.ActivateApiScopeAsync(scenario.AdministratorScope);
        var administrator = await scenario.Service.GetGeneralLedgerAsync(
            scenario.AdministratorScope, new GeneralLedgerQuery { Take = 200 });

        manager.Items.Should().Contain(row => row.UnitId == scenario.UnitA.Id && row.PropertyId == null);
        manager.Items.Should().Contain(row =>
            row.TenantAccountId == scenario.TenantAccountA.Id && row.PropertyId == null && row.UnitId == null);
        manager.Items.Should().NotContain(row => row.OwnerEntityId != null);
        manager.Items.Should().NotContain(row =>
            row.PropertyId == null && row.UnitId == null && row.TenantAccountId == null && row.OwnerEntityId == null);
        administrator.Items.Should().Contain(row => row.OwnerEntityId != null);
        administrator.Items.Should().Contain(row =>
            row.PropertyId == null && row.UnitId == null && row.TenantAccountId == null && row.OwnerEntityId == null);
    }

    private static async Task<Scenario> SeedScenarioAsync(MigratedPostgreSqlTestContext context)
    {
        var now = DateTime.UtcNow;
        var propertyA = Property("Accounting A", now);
        var propertyB = Property("Accounting B", now);
        context.Db.Properties.AddRange(propertyA, propertyB);
        await context.Db.SaveChangesAsync();

        var unitA = Unit(propertyA, "A", now);
        var unitB = Unit(propertyB, "B", now);
        context.Db.Units.AddRange(unitA, unitB);
        await context.Db.SaveChangesAsync();

        var tenantA = await AddTenantAccountAsync(context, propertyA, unitA, "A", now);
        var tenantB = await AddTenantAccountAsync(context, propertyB, unitB, "B", now);
        var owner = new OwnerEntity
        {
            PortfolioId = 1,
            Name = "Portfolio Owner",
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.OwnerEntities.Add(owner);
        await new ChartOfAccountsSeedService(context.Db).SeedAsync(1);
        await context.Db.SaveChangesAsync();

        var manager = context.Db.SeedPropertyManagerScope(1, propertyA.Id, $"accounting-auth-{Guid.NewGuid():N}");
        var administrator = context.Db.SeedAdministratorScope(1, $"accounting-admin-{Guid.NewGuid():N}");
        var cash = await context.Db.LedgerAccounts.SingleAsync(account =>
            account.PortfolioId == 1 && account.SystemKey == "operating-cash");
        var income = await context.Db.LedgerAccounts.SingleAsync(account =>
            account.PortfolioId == 1 && account.SystemKey == "rental-income");

        await PostAsync(context, 8301, 100m,
            Line(cash.Id, debit: 100m, propertyId: propertyA.Id),
            Line(income.Id, credit: 100m, propertyId: propertyA.Id));
        var propertyBJournal = await PostAsync(context, 8302, 200m,
            Line(cash.Id, debit: 200m, propertyId: propertyB.Id),
            Line(income.Id, credit: 200m, propertyId: propertyB.Id));
        var mixed = await PostAsync(context, 8303, 50m,
            Line(cash.Id, debit: 50m, propertyId: propertyA.Id),
            Line(income.Id, credit: 50m, propertyId: propertyB.Id));
        await PostAsync(context, 8304, 25m,
            Line(cash.Id, debit: 25m, unitId: unitA.Id),
            Line(income.Id, credit: 25m, unitId: unitA.Id));
        await PostAsync(context, 8305, 50m,
            Line(cash.Id, debit: 50m, tenantAccountId: tenantA.Id),
            Line(income.Id, credit: 50m, tenantAccountId: tenantA.Id));
        await PostAsync(context, 8306, 10m,
            Line(cash.Id, debit: 10m, ownerEntityId: owner.Id),
            Line(income.Id, credit: 10m, ownerEntityId: owner.Id));
        await PostAsync(context, 8307, 5m,
            Line(cash.Id, debit: 5m),
            Line(income.Id, credit: 5m));

        return new Scenario(
            new AccountingLedgerReadModelService(context.Db), manager, administrator,
            propertyA, propertyB, unitA, tenantA, tenantB, mixed.PublicId, propertyBJournal.PublicId);
    }

    private static Property Property(string name, DateTime now) => new()
    {
        PortfolioId = 1,
        Name = name,
        AddressLine1 = "1 Main",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Unit Unit(Property property, string number, DateTime now) => new()
    {
        PortfolioId = 1,
        PropertyId = property.Id,
        UnitNumber = number,
        MarketRent = 1_000m,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static async Task<TenantAccount> AddTenantAccountAsync(
        MigratedPostgreSqlTestContext context,
        Property property,
        Unit unit,
        string suffix,
        DateTime now)
    {
        var lease = new LeaseManagement
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"REL-AUTH-{suffix}-{Guid.NewGuid():N}"[..24],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        context.Db.LeaseManagements.Add(lease);
        await context.Db.SaveChangesAsync();
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagementId = lease.Id,
            AccountNumber = $"TA-{suffix}-{Guid.NewGuid():N}"[..20],
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        context.Db.TenantAccounts.Add(account);
        await context.Db.SaveChangesAsync();
        return account;
    }

    private static AccountingProposedLine Line(
        int accountId,
        decimal debit = 0m,
        decimal credit = 0m,
        int? propertyId = null,
        int? unitId = null,
        int? tenantAccountId = null,
        int? ownerEntityId = null) => new()
    {
        LedgerAccountId = accountId,
        DebitAmount = debit,
        CreditAmount = credit,
        PropertyId = propertyId,
        UnitId = unitId,
        TenantAccountId = tenantAccountId,
        OwnerEntityId = ownerEntityId,
    };

    private static async Task<JournalEntry> PostAsync(
        MigratedPostgreSqlTestContext context,
        long sourceId,
        decimal amount,
        params AccountingProposedLine[] lines)
    {
        var entry = await new AccountingPostingService(context.Db).PostAsync(new AccountingProposedEntry
        {
            PortfolioId = 1,
            EffectiveOn = sourceId == 8303 ? new DateOnly(2026, 9, 1) : new DateOnly(2026, 8, 1),
            Currency = "USD",
            Description = $"Authorized journal {amount}",
            SourceType = JournalSourceType.OpeningBalance,
            SourceId = sourceId,
            SourceBusinessKey = $"accounting-auth:{sourceId}",
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            AtomicReceiptId = Guid.NewGuid(),
            UserId = 1,
            Lines = lines,
        });
        await context.Db.SaveChangesAsync();
        context.Db.ChangeTracker.Clear();
        return entry;
    }

    private sealed record Scenario(
        AccountingLedgerReadModelService Service,
        WorkspaceReadScope ManagerScope,
        WorkspaceReadScope AdministratorScope,
        Property PropertyA,
        Property PropertyB,
        Unit UnitA,
        TenantAccount TenantAccountA,
        TenantAccount TenantAccountB,
        Guid MixedJournalPublicId,
        Guid PropertyBJournalPublicId);
}
