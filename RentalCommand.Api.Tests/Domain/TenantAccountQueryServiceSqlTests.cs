using System.Reflection;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Translation-only proofs. These tests do not connect to PostgreSQL; they require Npgsql to render
/// every TenantAccount read as a server-side statement containing its authorization predicate.
/// </summary>
public sealed class TenantAccountQueryServiceSqlTests
{
    private static readonly WorkspaceReadScope Scope = new(
        17,
        23,
        Guid.Parse("d9d88978-4f4c-4539-b284-817c550f51db"),
        31,
        7);

    [Fact]
    public void DetailRead_UsesCanonicalBalanceViewAndFullPropertyAuthorization()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildDetailQuery(Scope, 41).ToQueryString();

        AssertAuthorized(sql);
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("LeaseManagementId");
    }

    [Fact]
    public void AccountPage_SearchesSelectsSortsAndPagesInsideOneAuthorizedStatement()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildAccountPageQuery(Scope, new TenantAccountListQuery
        {
            Search = "Maple",
            Lifecycle = "Occupied",
            Closed = false,
            Sort = "-unsupported",
            Skip = 10,
            Take = 25,
        }).ToQueryString();

        AssertAuthorized(sql);
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("CurrentPrimaryTenantName");
        sql.Should().Contain("AccountNumber");
        sql.Should().Contain("RelationshipNumber");
        sql.Should().Contain("PropertyName");
        sql.Should().Contain("UnitNumber");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("DESC",
            "unknown account sort prefixes must not invert the stable property/unit default");
    }

    [Fact]
    public void GlobalEntryPage_AppliesAllFiltersAndPagingToOneAuthorizedCrossAccountStatement()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildGlobalEntryPageQuery(Scope,
            new TenantLedgerEntryGlobalListQuery
            {
                TenantAccountId = 41,
                EntryType = TenantLedgerEntryType.PaymentReceipt,
                Direction = TenantLedgerDirection.Credit,
                Search = "check 1042",
                From = new DateTime(2026, 2, 1),
                To = new DateTime(2026, 2, 28),
                Sort = "-amount",
                Skip = 20,
                Take = 20,
            }).ToQueryString();

        AssertAuthorized(sql);
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("EntryType");
        sql.Should().Contain("Direction");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().Contain("LeaseAgreementId");
        sql.Should().Contain("LeaseAddendumId");
        sql.Should().Contain("ProviderPaymentAttemptId");
        sql.Should().Contain("SourceStoredFileId");
        sql.Should().Contain("41");
    }

    [Fact]
    public void EntryAndDepositPages_TranslateToSql()
    {
        using var db = NewContext();
        var service = NewService(db);
        var entrySql = service.BuildEntryPageQuery(Scope, 41,
            new TenantLedgerEntryListQuery
            {
                Skip = 20,
                Take = 20,
                Sort = "-postedAtUtc",
            }).ToQueryString();
        var depositSql = service.BuildDepositPageQuery(Scope,
            new TenantAccountDepositListQuery
            {
                TenantAccountId = 41,
                Skip = 20,
                Take = 20,
                Sort = "-createdAtUtc",
            }).ToQueryString();

        AssertAuthorized(entrySql);
        AssertDepositAuthorized(depositSql);
        foreach (var sql in new[] { entrySql, depositSql })
        {
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            sql.Should().Contain("OFFSET");
        }
        entrySql.Should().Contain("TenantLedgerEntries");
        depositSql.Should().Contain("vw_security_deposit_balances");
    }

    [Fact]
    public void DepositPage_AppliesContextFiltersCanonicalBalanceViewAndPagingInOneAuthorizedStatement()
    {
        using var db = NewContext();
        var pageQuery = NewService(db).BuildDepositPageQuery(Scope,
            new TenantAccountDepositListQuery
            {
                TenantAccountId = 41,
                PropertyId = 53,
                Status = "Held",
                Search = "Maple",
                Sort = "-heldBalance",
                Skip = 20,
                Take = 25,
            });
        var sql = pageQuery.ToQueryString();

        AssertDepositAuthorized(sql);
        sql.Should().Contain("SecurityDepositAccounts");
        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("PropertyId");
        sql.Should().Contain("DepositStatus");
        sql.Should().Contain("HeldBalance");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().Contain("41");
        sql.Should().Contain("53");

        AssertFinalOrderingKey(pageQuery, nameof(TenantAccountDepositListItemResponse.SecurityDepositAccountId));
    }

    [Fact]
    public void DepositPage_CreatedAtSortPagesAuthorizedIdsBeforeAggregateViews()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildDepositPageQuery(
            Scope,
            new TenantAccountDepositListQuery
            {
                Sort = "-createdAtUtc",
                Skip = 0,
                Take = 20,
            }).ToQueryString();

        AssertDepositAuthorized(sql);
        var limitIndex = sql.IndexOf("LIMIT", StringComparison.Ordinal);
        limitIndex.Should().BeGreaterThan(-1);
        limitIndex.Should().BeLessThan(
            sql.IndexOf("vw_lease_management_lifecycle", StringComparison.Ordinal),
            "the authorized deposit IDs must be paged before lifecycle display data is joined");
        limitIndex.Should().BeLessThan(
            sql.IndexOf("vw_security_deposit_balances", StringComparison.Ordinal),
            "the authorized deposit IDs must be paged before aggregate balances are joined");
    }

    [Fact]
    public void DepositPage_UnsupportedSortKeepsStableAscendingPropertyUnitDepositOrder()
    {
        using var db = NewContext();
        var pageQuery = NewService(db).BuildDepositPageQuery(Scope,
            new TenantAccountDepositListQuery { Sort = "-unsupported" });
        var sql = pageQuery.ToQueryString();

        AssertDepositAuthorized(sql);
        var orderBy = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        orderBy.Should().MatchRegex(
            "ORDER BY .*\\\"Name\\\", .*\\\"UnitNumber\\\", .*\\\"Id\\\"",
            "the default deposit order must remain property, unit, then unique deposit id");
        orderBy.Should().NotContain("DESC",
            "unsupported sort prefixes must not invert the canonical stable default");
        AssertFinalOrderingKey(pageQuery, nameof(TenantAccountDepositListItemResponse.SecurityDepositAccountId));
    }

    [Fact]
    public void DepositCount_AvoidsAggregateViewsUntilAFilterNeedsThem()
    {
        using var db = NewContext();
        var service = NewService(db);

        var commonCountSql = service.BuildDepositCountQuery(
            Scope,
            new TenantAccountDepositListQuery { PropertyId = 53 }).ToQueryString();
        var filteredCountSql = service.BuildDepositCountQuery(
            Scope,
            new TenantAccountDepositListQuery { Search = "Maple" }).ToQueryString();

        AssertDepositAuthorized(commonCountSql);
        commonCountSql.Should().Contain("SecurityDepositAccounts");
        commonCountSql.Should().Contain("PropertyId");
        commonCountSql.Should().NotContain("vw_security_deposit_balances");
        commonCountSql.Should().NotContain("vw_lease_management_lifecycle");

        AssertDepositAuthorized(filteredCountSql);
        filteredCountSql.Should().Contain("vw_security_deposit_balances");
        filteredCountSql.Should().Contain("vw_lease_management_lifecycle");
        filteredCountSql.Should().Contain("ILIKE");
    }

    [Fact]
    public void EntryPage_FiltersSortsAndPagesInOneAuthorizedStatementWithLegalProvenance()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildEntryPageQuery(Scope, 41, new TenantLedgerEntryListQuery
        {
            Search = "rent",
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            From = new DateTime(2026, 1, 1),
            To = new DateTime(2026, 1, 31),
            Sort = "-amount",
            Skip = 20,
            Take = 10,
        }).ToQueryString();

        AssertAuthorized(sql);
        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().Contain("LeaseAgreementId");
        sql.Should().Contain("LeaseAddendumId");
        sql.Should().Contain("ReversesEntryId");
        sql.Should().Contain("SourceStoredFileId");
    }

    [Fact]
    public void EntryDetail_UsesExactAuthorizedAccountEntryJoinAndLeftJoinedFullProvenance()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildEntryDetailQuery(Scope, 41, 991).ToQueryString();

        AssertAuthorized(sql);
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LEFT JOIN");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LeaseAddenda");
        sql.Should().Contain("TenantPaymentAttempts");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("ProviderObjectId");
        sql.Should().Contain("FailureReason");
        sql.Should().Contain("AgreementNumber");
        sql.Should().Contain("AddendumNumber");
        sql.Should().Contain("FileName");
        sql.Should().Contain("41");
        sql.Should().Contain("991");
        sql.Should().NotContain("ClaimToken");
        sql.Should().NotContain("FilePath");
    }

    [Fact]
    public void UnsupportedEntryAndChargeSorts_KeepCanonicalDefaultsRegardlessOfPrefix()
    {
        using var db = NewContext();
        var service = NewService(db);
        var entrySql = service.BuildEntryPageQuery(Scope, 41, new TenantLedgerEntryListQuery
        {
            Sort = "unsupported",
        }).ToQueryString();
        var prefixedEntrySql = service.BuildEntryPageQuery(Scope, 41, new TenantLedgerEntryListQuery
        {
            Sort = "-unsupported",
        }).ToQueryString();
        var chargeSql = service.BuildChargePageQuery(Scope, 41, new TenantChargeListQuery
        {
            Sort = "unsupported",
        }).ToQueryString();
        var prefixedChargeSql = service.BuildChargePageQuery(Scope, 41, new TenantChargeListQuery
        {
            Sort = "-unsupported",
        }).ToQueryString();

        foreach (var sql in new[] { entrySql, prefixedEntrySql })
        {
            AssertAuthorized(sql);
            sql.Should().Contain("ORDER BY").And.Contain("DESC");
        }
        foreach (var sql in new[] { chargeSql, prefixedChargeSql })
        {
            AssertAuthorized(sql);
            sql.Should().Contain("ORDER BY");
            sql.Should().NotContain("DESC");
        }
    }

    [Theory]
    [InlineData("effectiveOn", false)]
    [InlineData("-effectiveOn", true)]
    public void EntryEffectiveOnSort_IsExplicitAndHonorsDirection(string sort, bool descending)
    {
        using var db = NewContext();
        var sql = NewService(db).BuildEntryPageQuery(Scope, 41, new TenantLedgerEntryListQuery
        {
            Sort = sort,
        }).ToQueryString();

        sql.Should().Contain("ORDER BY");
        if (descending)
            sql.Should().Contain("DESC");
        else
            sql.Should().NotContain("DESC");
    }

    [Theory]
    [InlineData("dueOn", false)]
    [InlineData("-dueOn", true)]
    public void ChargeDueOnSort_IsExplicitAndHonorsDirection(string sort, bool descending)
    {
        using var db = NewContext();
        var sql = NewService(db).BuildChargePageQuery(Scope, 41, new TenantChargeListQuery
        {
            Sort = sort,
        }).ToQueryString();

        sql.Should().Contain("ORDER BY");
        if (descending)
            sql.Should().Contain("DESC");
        else
            sql.Should().NotContain("DESC");
    }

    [Fact]
    public void ChargePage_UsesChargeViewAndKeepsAuthorizationFilteringAndPagingDatabaseSide()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildChargePageQuery(Scope, 41, new TenantChargeListQuery
        {
            Search = "late fee",
            IsPastDue = true,
            Sort = "-openAmount",
            Skip = 5,
            Take = 25,
        }).ToQueryString();

        AssertAuthorized(sql);
        sql.Should().Contain("vw_tenant_charge_balances");
        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().Contain("LeaseAgreementId");
        sql.Should().Contain("LeaseAddendumId");
    }

    [Fact]
    public void DepositRead_UsesDepositViewAndNamesAccountRelationshipAndOriginatingAgreement()
    {
        using var db = NewContext();
        var sql = NewService(db).BuildDepositQuery(Scope, 41).ToQueryString();

        AssertDepositAuthorized(sql);
        sql.Should().Contain("SecurityDepositAccounts");
        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("LeaseManagementId");
        sql.Should().Contain("OriginatingAgreementId");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("PropertyId");
        sql.Should().Contain("UnitId");
        sql.Should().Contain("AccountNumber");
        sql.Should().Contain("RelationshipNumber");
    }

    [Fact]
    public void DepositReads_AllowEitherDepositCapabilityAndDenyBalanceOnlyInTranslatedSql()
    {
        using var db = NewContext();
        var queryService = NewService(db);
        var moveOutService = NewMoveOutService(db);
        var statements = new[]
        {
            queryService.BuildDepositPageQuery(
                Scope, new TenantAccountDepositListQuery()).ToQueryString(),
            queryService.BuildDepositQuery(Scope, 41).ToQueryString(),
            moveOutService.BuildAuthorizedDepositProperties(Scope).ToQueryString(),
        };

        foreach (var sql in statements)
            AssertDepositAuthorized(sql);
    }

    [Fact]
    public void Controller_ExposesOnlyTheRequestedCanonicalReadRoutes()
    {
        typeof(TenantAccountsController).GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/v1/tenant-accounts");

        var routes = typeof(TenantAccountsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<HttpGetAttribute>()?.Template)
            .Where(template => template is not null)
            .Should().BeEquivalentTo(
                "page",
                "entries/page",
                "deposits/page",
                "{id:int}",
                "{id:int}/entries/page",
                "{tenantAccountId:int}/entries/{tenantLedgerEntryId:long}",
                "{id:int}/charges/page",
                "{id:int}/deposit",
                "{id:int}/deposit/move-out-statement");
    }

    private static void AssertAuthorized(string sql)
    {
        sql.Should().Contain("public.rc_api_effective_capability_scopes(", Exactly.Once());
        sql.Should().Contain(CapabilityKeys.MoneyBalancesRead);
        sql.Should().Contain(Scope.SessionId.ToString());
        sql.Should().NotContain("AuthSessions");
        sql.Should().NotContain("RoleProfileCapabilities");
        sql.Should().NotContain("MembershipRoleAssignments");
        sql.Should().NotContain("MembershipRoleAssignmentProperties");
    }

    private static void AssertDepositAuthorized(string sql)
    {
        sql.Should().Contain("public.rc_api_effective_capability_scopes(", Exactly.Once());
        sql.Should().Contain(CapabilityKeys.MoneyDepositsManage);
        sql.Should().Contain(CapabilityKeys.LeasingDepositsRead);
        sql.Should().NotContain("UNION",
            "both allowed capabilities must share one authorization predicate instead of duplicating the full session/access graph");
        sql.Should().NotContain(CapabilityKeys.MoneyBalancesRead,
            "balance-only access must not authorize security-deposit reads");
        sql.Should().Contain(Scope.SessionId.ToString());
        sql.Should().NotContain("AuthSessions");
        sql.Should().NotContain("RoleProfileCapabilities");
        sql.Should().NotContain("MembershipRoleAssignments");
        sql.Should().NotContain("MembershipRoleAssignmentProperties");
    }

    private static void AssertFinalOrderingKey(IQueryable query, string expectedMemberName)
    {
        var expression = query.Expression;
        while (expression is MethodCallExpression call
               && call.Method.Name is nameof(Queryable.Skip) or nameof(Queryable.Take))
        {
            expression = call.Arguments[0];
        }

        var finalOrdering = expression.Should().BeAssignableTo<MethodCallExpression>().Subject;
        finalOrdering.Method.Name.Should().BeOneOf(nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending));
        var selector = ((UnaryExpression)finalOrdering.Arguments[1]).Operand
            .Should().BeAssignableTo<LambdaExpression>().Subject;
        selector.Body.Should().BeAssignableTo<MemberExpression>()
            .Which.Member.Name.Should().Be(expectedMemberName);
    }

    private static TenantAccountQueryService NewService(RentalCommandDbContext db) =>
        new(db, TimeProvider.System);

    private static TenantAccountMoveOutStatementService NewMoveOutService(
        RentalCommandDbContext db) =>
        new(
            db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IMoveOutStatementPdfGenerator>(),
            Mock.Of<ILogger<TenantAccountMoveOutStatementService>>(),
            TimeProvider.System);

    private static RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}
