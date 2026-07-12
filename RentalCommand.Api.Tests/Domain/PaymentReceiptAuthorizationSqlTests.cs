using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PaymentReceiptAuthorizationSqlTests
{
    [Fact]
    public void Payment_service_has_no_mutable_edit_status_or_delete_surface()
    {
        var methodNames = typeof(IPaymentReceiptQueryService).GetMethods().Select(method => method.Name).ToArray();

        methodNames.Should().NotContain("UpdateAsync");
        methodNames.Should().NotContain("MarkPaidAsync");
        methodNames.Should().NotContain("MarkLeasePastDuePaidAsync");
        methodNames.Should().NotContain("DeleteAsync");
        typeof(PaymentController).BaseType.Should().Be(typeof(AuthenticatedPortfolioControllerBase),
            "receipt access is capability-scoped and must not fall back to legacy role names");
    }

    [Fact]
    public void Receipt_query_authorizes_session_revision_capability_and_property_before_paging()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
                .Options);
        var service = new PaymentReceiptQueryService(db, TimeProvider.System);
        var access = new PaymentReceiptReadContext(
            PortfolioId: 17,
            UserId: 29,
            SessionId: Guid.Parse("6e687a1f-dc38-4bea-b4c1-a5fc1722b279"),
            AccessContextId: 31,
            AccessRevision: 7);

        var sql = service.BuildReceiptQuery(access, new PaymentListQuery
            {
                TenantAccountId = 41,
                Search = "check 1487",
            })
            .OrderByDescending(row => row.PostedAtUtc)
            .ThenByDescending(row => row.Id)
            .Skip(20)
            .Take(20)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("WorkspaceAccessContexts");
        sql.Should().Contain("WorkspaceMemberships");
        sql.Should().Contain("MembershipRoleAssignments");
        sql.Should().Contain("RoleProfileCapabilities");
        sql.Should().Contain("CapabilityDefinitions");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain("money.balances.read");
        sql.Should().Contain("AccessRevision");
        sql.Should().Contain("ExpiresAtUtc");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("FROM \"Payments\"");

        var authorizationAt = sql.IndexOf("EXISTS", StringComparison.OrdinalIgnoreCase);
        var orderingAt = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        authorizationAt.Should().BeGreaterThanOrEqualTo(0);
        authorizationAt.Should().BeLessThan(orderingAt,
            "the authorization EXISTS must remain in the database WHERE clause before paging");
    }

    [Fact]
    public void Account_option_query_authorizes_manage_capability_and_searches_in_sql_before_paging()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
                .Options);
        var service = new PaymentReceiptQueryService(db, TimeProvider.System);
        var access = new PaymentReceiptReadContext(
            PortfolioId: 17,
            UserId: 29,
            SessionId: Guid.Parse("6e687a1f-dc38-4bea-b4c1-a5fc1722b279"),
            AccessContextId: 31,
            AccessRevision: 7);

        var sql = service.BuildAccountOptionQuery(access, new TenantAccountOptionQuery
            {
                Search = "Maple 2B",
            })
            .OrderBy(row => row.PropertyName)
            .ThenBy(row => row.UnitNumber)
            .Skip(25)
            .Take(25)
            .ToQueryString();

        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("WorkspaceAccessContexts");
        sql.Should().Contain("MembershipRoleAssignments");
        sql.Should().Contain("RoleProfileCapabilities");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain("money.payments.manage");
        sql.Should().Contain("AccessRevision");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("FROM \"Payments\"");

        var authorizationAt = sql.IndexOf("EXISTS", StringComparison.OrdinalIgnoreCase);
        var orderingAt = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        authorizationAt.Should().BeGreaterThanOrEqualTo(0);
        authorizationAt.Should().BeLessThan(orderingAt);
    }
}
