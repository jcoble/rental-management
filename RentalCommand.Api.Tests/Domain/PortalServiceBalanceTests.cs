using System.Data.Common;
using System.Reflection;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>Translation and route proofs for canonical tenant-account portal money reads.</summary>
public sealed class PortalServiceBalanceTests
{
    private static readonly PortalTenantReadScope Scope = new(
        17,
        23,
        31,
        7);

    [Fact]
    public void AccountPage_UsesEffectiveTenantAccessAndCanonicalViewsInOnePagedStatement()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildTenantAccountPageQuery(Scope, new PortalTenantAccountListQuery
        {
            Search = "Maple",
            Lifecycle = "Occupied",
            Sort = "-unsupported",
            Skip = 5,
            Take = 20,
        }).ToQueryString();

        AssertCurrentTenantAccess(sql);
        sql.Should().Contain("authorized_tenant_accounts AS MATERIALIZED",
            "the portal account page must join one materialized tenant-account authorization set");
        sql.Should().Contain("vw_effective_tenant_access", Exactly.Once());
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC", "unknown sort fields retain canonical newest-first ordering");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
    }

    [Theory]
    [InlineData("propertyName", false)]
    [InlineData("-propertyName", true)]
    public void AccountPropertySort_EndsWithUniqueTenantAccountId(string sort, bool descending)
    {
        using var db = NewContext();
        var pageQuery = NewService(db).BuildTenantAccountPageQuery(Scope,
            new PortalTenantAccountListQuery { Sort = sort });
        pageQuery.ToQueryString().Should().Contain("ORDER BY");

        var orderings = GetOrderings(pageQuery);
        orderings.Select(ordering => ordering.Member).Should().Equal(
            nameof(PortalTenantAccountResponse.PropertyName),
            nameof(PortalTenantAccountResponse.UnitNumber),
            nameof(PortalTenantAccountResponse.TenantAccountId));
        orderings.Select(ordering => ordering.Descending).Should().OnlyContain(
            value => value == descending,
            "every stable ordering key must follow the requested direction");
    }

    [Fact]
    public void EntryAndChargePages_AuthorizeTheSpecificAccountAndKeepProvenanceAndPagingInSql()
    {
        using var db = NewContext();
        var service = NewService(db);
        var entrySql = service.BuildTenantLedgerEntryPageQuery(Scope, 41,
            new PortalTenantLedgerEntryListQuery
            {
                Search = "rent",
                EntryType = TenantLedgerEntryType.RentCharge,
                Sort = "-unsupported",
                Skip = 10,
                Take = 10,
            }).ToQueryString();
        var chargeSql = service.BuildTenantChargePageQuery(Scope, 41,
            new PortalTenantChargeListQuery
            {
                IsPastDue = true,
                Sort = "-openAmount",
                Skip = 10,
                Take = 10,
            }).ToQueryString();

        foreach (var sql in new[] { entrySql, chargeSql })
        {
            AssertCurrentTenantAccess(sql);
            sql.Should().Contain("TenantLedgerEntries");
            sql.Should().Contain("LeaseAgreementId");
            sql.Should().Contain("LeaseAddendumId");
            sql.Should().Contain("SourceStoredFileId");
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            sql.Should().Contain("OFFSET");
        }
        entrySql.Should().Contain("LEFT JOIN");
        entrySql.Should().Contain("RentTrackingStartOn");
        entrySql.Should().Contain("ReversesEntryId");
        chargeSql.Should().Contain("vw_tenant_charge_balances");
        chargeSql.Should().Contain("\"OpenAmount\" > 0.0");
        chargeSql.Should().NotContain("\"DueOn\" <= ");
        chargeSql.Should().NotContain("\"BusinessDate\"");
        entrySql.Should().Contain("DESC", "unknown entry sorts retain newest-first ordering");
    }

    [Theory]
    [InlineData("effectiveOn", "dueOn", false)]
    [InlineData("-effectiveOn", "-dueOn", true)]
    public void EntryAndChargeCanonicalDateSorts_AreExplicitAndHonorDirection(
        string entrySort,
        string chargeSort,
        bool descending)
    {
        using var db = NewContext();
        var service = NewService(db);
        var entrySql = service.BuildTenantLedgerEntryPageQuery(Scope, 41,
            new PortalTenantLedgerEntryListQuery { Sort = entrySort }).ToQueryString();
        var chargeSql = service.BuildTenantChargePageQuery(Scope, 41,
            new PortalTenantChargeListQuery { Sort = chargeSort }).ToQueryString();

        foreach (var sql in new[] { entrySql, chargeSql })
        {
            sql.Should().Contain("ORDER BY");
            if (descending)
                sql.Should().Contain("DESC");
            else
                sql.Should().NotContain("DESC");
        }
    }

    [Fact]
    public void DepositRead_IsTenantAccessAuthorizedAndUsesCanonicalDepositView()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildTenantAccountDepositQuery(Scope, 41).ToQueryString();

        AssertCurrentTenantAccess(sql);
        sql.Should().Contain("SecurityDepositAccounts");
        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("OriginatingAgreementId");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("LeaseManagementId");
    }

    [Fact]
    public void ExecutedAgreementDownload_BindsCurrentTenantRelationshipAndArtifactInOneStatement()
    {
        using var db = NewContext();
        var sql = NewService(db)
            .BuildExecutedAgreementArtifactQuery(Scope, 91, 108)
            .ToQueryString();

        AssertCurrentTenantAccess(sql);
        sql.Should().Contain("LeaseManagementId");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("ExecutedArtifactId");
        sql.Should().Contain("DeletedAt");
    }

    [Fact]
    public void WorkOrderPage_AuthorizesRelationshipFiltersSortsAndPagesInSql()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildWorkOrderPageQuery(
            Scope,
            73,
            new PortalTenantWorkOrderListQuery
            {
                Search = "heater",
                OpenOnly = true,
                Sort = "-requestedAt",
                Skip = 20,
                Take = 20,
            }).ToQueryString();

        AssertCurrentTenantAccess(sql);
        sql.Should().Contain("WorkOrders");
        sql.Should().Contain("LeaseManagementId");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain($"NOT IN ({(int)WorkOrderStatus.Completed}, {(int)WorkOrderStatus.Cancelled}, {(int)WorkOrderStatus.Archived})");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().Contain("73");
    }

    [Fact]
    public void AdversarialAccountLookup_BindsUserContextRevisionAndRequestedAccountInSql()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildAuthorizedTenantAccountQuery(Scope)
            .Where(account => account.Id == 999)
            .ToQueryString();

        AssertCurrentTenantAccess(sql);
        sql.Should().Contain("999");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("LeaseManagementId");
        sql.Should().NotContain("LeaseManagementParties",
            "portal money authorization must not infer access from a tenant party role");
    }

    [Fact]
    public void Controller_RemovesLegacyReadsAddsCanonicalReadsAndPreservesAutopayRoutes()
    {
        var getRoutes = typeof(PortalController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<HttpGetAttribute>()?.Template)
            .Where(template => template is not null)
            .ToArray();

        getRoutes.Should().Contain([
            "access-state",
            "tenant-accounts/page",
            "tenant-accounts/{id:int}",
            "tenant-accounts/{id:int}/entries/page",
            "tenant-accounts/{id:int}/history",
            "tenant-accounts/{id:int}/charges/page",
            "tenant-accounts/{id:int}/deposit",
            "tenant-accounts/{tenantAccountId:int}/autopay",
        ]);
        typeof(PortalAccessStateResponse).GetProperties()
            .Select(property => property.Name)
            .Should().Equal(nameof(PortalAccessStateResponse.HasActiveTenantAccess));
        getRoutes.Should().NotContain("balance");
        getRoutes.Should().NotContain("payments");
    }

    private static void AssertCurrentTenantAccess(string sql)
    {
        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("UserId");
        sql.Should().Contain("AccessContextId");
        sql.Should().Contain("AccessRevision");
        sql.Should().Contain(Scope.UserId.ToString());
        sql.Should().Contain(Scope.AccessContextId.ToString());
        sql.Should().Contain(Scope.AccessRevision.ToString());
        sql.Should().NotContain("money.balances.read");
        sql.Should().NotContain("MembershipRoleAssignments");
    }

    private static IReadOnlyList<(string Member, bool Descending)> GetOrderings(IQueryable query)
    {
        var expression = query.Expression;
        var orderings = new List<(string Member, bool Descending)>();
        while (expression is MethodCallExpression call)
        {
            if (call.Method.Name is nameof(Queryable.Skip) or nameof(Queryable.Take))
            {
                expression = call.Arguments[0];
                continue;
            }
            if (call.Method.Name is not (nameof(Queryable.OrderBy)
                or nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenBy)
                or nameof(Queryable.ThenByDescending)))
            {
                break;
            }

            var selector = ((UnaryExpression)call.Arguments[1]).Operand
                .Should().BeAssignableTo<LambdaExpression>().Subject;
            var member = selector.Body.Should().BeAssignableTo<MemberExpression>()
                .Which.Member.Name;
            var descending = call.Method.Name is nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenByDescending);
            orderings.Add((member, descending));
            expression = call.Arguments[0];
        }

        orderings.Reverse();
        return orderings;
    }

    private static PortalService NewService(RentalCommandDbContext db) =>
        new(
            db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
            Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>());

    private static RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}

[Collection(MigratedPostgreSqlCollection.Name1)]
public sealed class PortalServicePayableChargePostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public PortalServicePayableChargePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
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
    public async Task ChargePage_ReturnsOnlyEffectiveAuthorizedOpenCharges_AndCountsAndPagesInSql()
    {
        var scenario = await SeedScenarioAsync();
        _commands.Clear();

        var page = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .ListTenantAccountChargesPageAsync(
                scenario.Scope,
                scenario.TenantAccountId,
                new PortalTenantChargeListQuery { Take = 20 });

        page.Should().NotBeNull();
        page!.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item =>
            item.TenantLedgerEntryId == scenario.DueOpenRentId);
        page.Items.Should().NotContain(item =>
            item.TenantLedgerEntryId == scenario.FutureOpenRentId);

        _commands.Should().HaveCount(3,
            "the endpoint uses one identity query, one count query, and one bounded page query");
        var chargeCommands = _commands
            .Where(sql => sql.Contains("vw_tenant_charge_balances", StringComparison.Ordinal))
            .ToArray();
        chargeCommands.Should().HaveCount(2);
        chargeCommands.Should().OnlyContain(sql =>
            sql.Contains("\"OpenAmount\" > 0.0", StringComparison.Ordinal)
            && !sql.Contains("\"DueOn\" <= ", StringComparison.Ordinal)
            && !sql.Contains("\"BusinessDate\"", StringComparison.Ordinal));
        chargeCommands.Should().ContainSingle(sql =>
            sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase));
        chargeCommands.Should().ContainSingle(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        _commands.Clear();
        var focusedLedgerPage = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .ListTenantAccountEntriesPageAsync(
                scenario.Scope,
                scenario.TenantAccountId,
                new PortalTenantLedgerEntryListQuery
                {
                    Search = "future-open-rent",
                    Take = 20,
                });

        focusedLedgerPage.Should().NotBeNull();
        focusedLedgerPage!.Items.Should().BeEmpty(
            "future-effective immutable ledger rows must not appear before the portfolio business date reaches them");
        _commands.Should().HaveCount(3,
            "the focused ledger entry read keeps identity, count, and page work DB-side");
        _commands.Should().Contain(sql =>
            sql.Contains("TenantLedgerEntries", StringComparison.Ordinal)
            && sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        _commands.Clear();
        var deniedPage = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .ListTenantAccountChargesPageAsync(
                scenario.Scope,
                scenario.UnauthorizedTenantAccountId,
                new PortalTenantChargeListQuery { Take = 20 });

        deniedPage.Should().BeNull();
        _commands.Should().ContainSingle("unauthorized account lookup stops before charge queries");
    }

    [Fact]
    public async Task AccountPage_ExcludesUnauthorizedTenantAccount_AndJoinsMaterializedAccessSet()
    {
        var scenario = await SeedScenarioAsync();
        _commands.Clear();

        var page = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .ListTenantAccountsPageAsync(
                scenario.Scope,
                new PortalTenantAccountListQuery { Take = 20 });

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.TenantAccountId == scenario.TenantAccountId);
        page.Items.Should().NotContain(item => item.TenantAccountId == scenario.UnauthorizedTenantAccountId);
        _commands.Should().HaveCount(2, "account count and page must remain bounded database statements");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("authorized_tenant_accounts AS MATERIALIZED", StringComparison.Ordinal),
            "portal account count/page SQL must preserve the effective tenant-access boundary");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("vw_effective_tenant_access", StringComparison.Ordinal),
            "portal account count/page must resolve the effective access view once per statement");
    }

    [Fact]
    public async Task EntryPage_HidesBackfillBeforeRentTrackingStartAndOrphanedBackfillReversalInSql()
    {
        var scenario = await SeedScenarioAsync();
        _commands.Clear();

        var page = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .ListTenantAccountEntriesPageAsync(
                scenario.Scope,
                scenario.TenantAccountId,
                new PortalTenantLedgerEntryListQuery
                {
                    Take = 20,
                });

        page.Should().NotBeNull();
        page!.Items.Select(item => item.TenantLedgerEntryId).Should().Contain([
            scenario.DueOpenRentId,
            scenario.VisibleReversalId,
        ]);
        page.Items.Select(item => item.TenantLedgerEntryId).Should().NotContain([
            scenario.FutureOpenRentId,
            scenario.PreStartChargeId,
            scenario.PreStartReversalId,
        ]);
        page.Items.Single(item => item.TenantLedgerEntryId == scenario.VisibleReversalId)
            .ReversesEntryId.Should().Be(scenario.ReversedManualChargeId);

        _commands.Should().HaveCount(3,
            "the endpoint uses one identity query, one count query, and one bounded page query");
        var entryCommands = _commands
            .Where(sql => sql.Contains("TenantLedgerEntries", StringComparison.Ordinal))
            .ToArray();
        entryCommands.Should().HaveCount(2);
        entryCommands.Should().OnlyContain(sql =>
            sql.Contains("RentTrackingStartOn", StringComparison.Ordinal)
            && sql.Contains("ReversesEntryId", StringComparison.Ordinal)
            && sql.Contains("LEFT JOIN", StringComparison.OrdinalIgnoreCase));
        entryCommands.Should().ContainSingle(sql =>
            sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase));
        entryCommands.Should().ContainSingle(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AccountHistory_UsesOneStatementAndKeepsLegitimatePreMarkerCharge()
    {
        var scenario = await SeedScenarioAsync();
        var businessDate = await _context.Db.Database
            .SqlQuery<DateOnly>($"SELECT rc_business_date({scenario.Scope.PortfolioId}) AS \"Value\"")
            .SingleAsync();
        var account = await _context.Db.TenantAccounts
            .SingleAsync(item => item.Id == scenario.TenantAccountId);
        account.RentTrackingStartOn = businessDate.AddDays(-1);

        var legitimatePreMarkerCharge = Charge(
            scenario.TenantAccountId,
            scenario.LeaseAgreementId,
            TenantLedgerEntryType.RentCharge,
            1650m,
            businessDate.AddDays(-3),
            "legitimate-pre-marker-rent");
        legitimatePreMarkerCharge.PostedAtUtc = DateTime.UtcNow.AddMinutes(2);
        var laterReceipt = Receipt(
            scenario.TenantAccountId,
            1650m,
            businessDate,
            "later-full-payment");
        laterReceipt.PostedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        var sameDayCharge = Charge(
            scenario.TenantAccountId,
            null,
            TenantLedgerEntryType.ManualCharge,
            25m,
            businessDate,
            "same-day-charge");
        sameDayCharge.PostedAtUtc = DateTime.UtcNow.AddMinutes(2);
        var accountCredit = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = scenario.Scope.PortfolioId,
            TenantAccountId = scenario.TenantAccountId,
            EntryType = TenantLedgerEntryType.Credit,
            Direction = TenantLedgerDirection.Credit,
            Amount = 5000m,
            Currency = "USD",
            EffectiveOn = businessDate,
            PostedAtUtc = DateTime.UtcNow,
            Description = "account-credit",
            BusinessKey = "portal-history:account-credit",
            CreatedByUserId = scenario.Scope.UserId,
        };
        _context.Db.TenantLedgerEntries.AddRange(
            legitimatePreMarkerCharge, laterReceipt, sameDayCharge, accountCredit);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        _commands.Clear();

        var history = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .GetTenantAccountHistoryAsync(
                scenario.Scope,
                scenario.TenantAccountId,
                new PortalTenantAccountHistoryQuery
                {
                    Period = "all",
                    Take = 200,
                    FocusedEntryId = legitimatePreMarkerCharge.Id,
                });

        history.Should().NotBeNull();
        history!.Items.Should().Contain(item =>
            item.TenantLedgerEntryId == legitimatePreMarkerCharge.Id
            && item.SignedAmount == 1650m
            && item.IsFocused);
        var rentReceipt = history.Items.Single(item => item.Description == "rent-receipt");
        rentReceipt.Allocations.Should().ContainSingle(allocation =>
            allocation.AllocationId > 0
            && allocation.TargetDescription == "settled-rent"
            && allocation.Amount == 900m
            && allocation.EffectiveOn == businessDate.AddDays(-5));
        history.Items.Should().NotContain(item =>
            item.TenantLedgerEntryId == scenario.PreStartChargeId
            || item.TenantLedgerEntryId == scenario.PreStartReversalId);
        var sameDayDirectionOrder = history.Items
            .Where(item => item.EffectiveOn == businessDate)
            .Select(item => item.Direction == TenantLedgerDirection.Debit ? 0 : 1)
            .ToArray();
        sameDayDirectionOrder.Should().Equal(
            sameDayDirectionOrder.OrderBy(value => value),
            "same-day debits must precede credits regardless of posting timestamp");
        history.ClosingBalance.Should().Be(
            history.BeginningBalance + history.Items.Sum(item => item.SignedAmount));
        var historyCommands = _commands.ToArray();
        var canonicalBalance = await _context.Db.TenantAccountBalanceProjections
            .Where(row => row.TenantAccountId == scenario.TenantAccountId)
            .Select(row => row.ReceivableBalance)
            .SingleAsync();
        history.CurrentDue.Should().Be(canonicalBalance);
        history.CurrentDue.Should().BeNegative("an account credit is a negative amount owed");
        historyCommands.Should().ContainSingle(
            "summary, authorization, period calculations, windows, paging, and focus are one DB statement");
        historyCommands[0].Should().Contain("rc_portal_tenant_account_history");
    }

    [Fact]
    public async Task AccountHistory_WithoutFocusedEntry_SerializesEveryItemAsNotFocused()
    {
        var scenario = await SeedScenarioAsync();
        _commands.Clear();

        var history = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .GetTenantAccountHistoryAsync(
                scenario.Scope,
                scenario.TenantAccountId,
                new PortalTenantAccountHistoryQuery
                {
                    Period = "all",
                    Take = 200,
                });

        history.Should().NotBeNull();
        history!.Items.Should().NotBeEmpty();
        history.Items.Should().OnlyContain(item => !item.IsFocused,
            "a missing focused-entry parameter is false, never JSON null");
        _commands.Should().ContainSingle(
            "the no-focus account history still executes as one database statement");
    }

    [Fact]
    public async Task AccountHistory_UnauthorizedAccountFailsClosedInOneStatement()
    {
        var scenario = await SeedScenarioAsync();
        _commands.Clear();

        var history = await new PortalService(
                _context.Db, Mock.Of<ILeaseQaService>(), TimeProvider.System,
                Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>())
            .GetTenantAccountHistoryAsync(
                scenario.Scope,
                scenario.UnauthorizedTenantAccountId,
                new PortalTenantAccountHistoryQuery());

        history.Should().BeNull();
        _commands.Should().ContainSingle();
        _commands[0].Should().Contain("rc_portal_tenant_account_history");
    }

    private async Task<Scenario> SeedScenarioAsync()
    {
        const int portfolioId = 1;
        const int userId = 1;
        var now = DateTime.UtcNow;
        var businessDate = await _context.Db.Database
            .SqlQuery<DateOnly>($"SELECT rc_business_date({portfolioId}) AS \"Value\"")
            .SingleAsync();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = "Payable charge property",
            AddressLine1 = "1 Charge Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolioId,
            FirstName = "Portal",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(accessContext, property, tenant);
        await _context.Db.SaveChangesAsync();

        var unit = new Unit
        {
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.Add(unit);
        await _context.Db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-PAYABLE-CHARGES",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = userId,
            RowVersion = Guid.NewGuid(),
        };
        var unauthorizedManagement = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-PAYABLE-CHARGES-DENIED",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = userId,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.AddRange(management, unauthorizedManagement);
        await _context.Db.SaveChangesAsync();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = "TA-PAYABLE-CHARGES",
            Currency = "USD",
            RentTrackingStartOn = businessDate,
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = userId,
        };
        var unauthorizedAccount = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = unauthorizedManagement.Id,
            AccountNumber = "TA-PAYABLE-CHARGES-DENIED",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = userId,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = businessDate.AddDays(-1),
            ChangeReason = "portal payable charge proof",
            CreatedAtUtc = now,
            CreatedByUserId = userId,
        };
        _context.Db.AddRange(account, unauthorizedAccount, party);
        await _context.Db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-PAYABLE-CHARGES",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = businessDate.AddMonths(-1),
            TermEndOn = businessDate.AddYears(1),
            GoverningFromOn = businessDate.AddMonths(-1),
            BaseRentAmount = 1100m,
            RentDueDay = 1,
            SecurityDepositObligation = 500m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                portfolioId, userId, now),
            CreatedAtUtc = now,
            CreatedByUserId = userId,
            UpdatedAtUtc = now,
        };
        _context.Db.LeaseAgreements.Add(agreement);
        await _context.Db.SaveChangesAsync();

        _context.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = portfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = "portal.tenant@example.test",
            SigningOrder = 1,
            IsRequired = true,
        });

        _context.Db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            AccessContextId = accessContext.Id,
            ApplicationUserId = userId,
            LeaseManagementPartyId = party.Id,
            GrantedAtUtc = now,
            GrantedByUserId = userId,
            Reason = "portal payable charge proof",
        });

        var settledDeposit = Charge(
            account.Id, null, TenantLedgerEntryType.DepositCharge, 500m,
            businessDate.AddDays(-10), "settled-deposit");
        var settledRent = Charge(
            account.Id, agreement.Id, TenantLedgerEntryType.RentCharge, 900m,
            businessDate.AddDays(-5), "settled-rent");
        var futureOpenRent = Charge(
            account.Id, agreement.Id, TenantLedgerEntryType.RentCharge, 1000m,
            businessDate.AddDays(1), "future-open-rent");
        var dueOpenRent = Charge(
            account.Id, agreement.Id, TenantLedgerEntryType.RentCharge, 1100m,
            businessDate, "due-open-rent");
        var reversedManualCharge = Charge(
            account.Id, agreement.Id, TenantLedgerEntryType.ManualCharge, 750m,
            businessDate, "reversed-manual-charge");
        var preStartCharge = Charge(
            account.Id, agreement.Id, TenantLedgerEntryType.RentCharge, 650m,
            businessDate.AddDays(-2), "pre-start-rent");
        var unauthorizedFutureOpenRent = Charge(
            unauthorizedAccount.Id, null, TenantLedgerEntryType.ManualCharge, 1200m,
            businessDate.AddDays(1), "unauthorized-future-open-rent");
        var depositReceipt = Receipt(account.Id, 500m, businessDate, "deposit-receipt");
        var rentReceipt = Receipt(account.Id, 900m, businessDate, "rent-receipt");
        _context.Db.TenantLedgerEntries.AddRange(
            settledDeposit, settledRent, futureOpenRent, dueOpenRent,
            reversedManualCharge, preStartCharge, unauthorizedFutureOpenRent,
            depositReceipt, rentReceipt);
        await _context.Db.SaveChangesAsync();

        var reversal = Reversal(account.Id, reversedManualCharge.Id, 750m, businessDate,
            "reversed-manual-charge");
        var preStartReversal = Reversal(account.Id, preStartCharge.Id, 650m, businessDate,
            "pre-start-rent");
        _context.Db.TenantLedgerEntries.AddRange(reversal, preStartReversal);
        await _context.Db.SaveChangesAsync();

        _context.Db.TenantLedgerAllocations.AddRange(
            Allocation(account.Id, settledDeposit.Id, depositReceipt.Id, 500m, now, "settled-deposit"),
            Allocation(account.Id, settledRent.Id, rentReceipt.Id, 900m, now, "settled-rent"));
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        return new Scenario(
            new PortalTenantReadScope(
                portfolioId, userId, accessContext.Id, accessContext.AccessRevision),
            account.Id,
            dueOpenRent.Id,
            futureOpenRent.Id,
            reversedManualCharge.Id,
            reversal.Id,
            preStartCharge.Id,
            preStartReversal.Id,
            agreement.Id,
            unauthorizedAccount.Id);
    }

    private static TenantLedgerEntry Charge(
        int accountId,
        int? leaseAgreementId,
        TenantLedgerEntryType entryType,
        decimal amount,
        DateOnly dueOn,
        string key) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        TenantAccountId = accountId,
        LeaseAgreementId = leaseAgreementId,
        EntryType = entryType,
        Direction = TenantLedgerDirection.Debit,
        Amount = amount,
        Currency = "USD",
        EffectiveOn = dueOn,
        DueOn = dueOn,
        PostedAtUtc = DateTime.UtcNow,
        Description = key,
        BusinessKey = $"portal-payable:{key}",
        CreatedByUserId = 1,
    };

    private static TenantLedgerEntry Reversal(
        int accountId,
        long reversesEntryId,
        decimal amount,
        DateOnly effectiveOn,
        string key) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        TenantAccountId = accountId,
        EntryType = TenantLedgerEntryType.Reversal,
        Direction = TenantLedgerDirection.Credit,
        Amount = amount,
        Currency = "USD",
        EffectiveOn = effectiveOn,
        PostedAtUtc = DateTime.UtcNow,
        Description = $"{key} reversal",
        BusinessKey = $"portal-payable:{key}:reversal",
        ReversesEntryId = reversesEntryId,
        CreatedByUserId = 1,
    };

    private static TenantLedgerEntry Receipt(
        int accountId,
        decimal amount,
        DateOnly effectiveOn,
        string key) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        TenantAccountId = accountId,
        EntryType = TenantLedgerEntryType.PaymentReceipt,
        Direction = TenantLedgerDirection.Credit,
        Amount = amount,
        Currency = "USD",
        EffectiveOn = effectiveOn,
        PostedAtUtc = DateTime.UtcNow,
        Description = key,
        BusinessKey = $"portal-payable:{key}",
        CreatedByUserId = 1,
    };

    private static TenantLedgerAllocation Allocation(
        int accountId,
        long debitId,
        long creditId,
        decimal amount,
        DateTime allocatedAtUtc,
        string key) => new()
    {
        PortfolioId = 1,
        TenantAccountId = accountId,
        DebitEntryId = debitId,
        CreditEntryId = creditId,
        Amount = amount,
        AllocatedAtUtc = allocatedAtUtc,
        BusinessKey = $"portal-payable:{key}:allocation",
        CreatedByUserId = 1,
    };

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record Scenario(
        PortalTenantReadScope Scope,
        int TenantAccountId,
        long DueOpenRentId,
        long FutureOpenRentId,
        long ReversedManualChargeId,
        long VisibleReversalId,
        long PreStartChargeId,
        long PreStartReversalId,
        int LeaseAgreementId,
        int UnauthorizedTenantAccountId);
}
