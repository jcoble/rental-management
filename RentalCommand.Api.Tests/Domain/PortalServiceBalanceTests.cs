using System.Reflection;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

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
        chargeSql.Should().Contain("vw_tenant_charge_balances");
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
            "tenant-accounts/page",
            "tenant-accounts/{id:int}",
            "tenant-accounts/{id:int}/entries/page",
            "tenant-accounts/{id:int}/charges/page",
            "tenant-accounts/{id:int}/deposit",
            "tenant-accounts/{tenantAccountId:int}/autopay",
        ]);
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
        new(db, Mock.Of<ILeaseQaService>(), TimeProvider.System);

    private static RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}
