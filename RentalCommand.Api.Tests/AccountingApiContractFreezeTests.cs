using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Tests;

public sealed class AccountingApiContractFreezeTests
{
    [Fact]
    public void Accounting_routes_match_the_frozen_v1_surface()
    {
        var routes = typeof(AccountingController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes()
                .OfType<HttpMethodAttribute>()
                .Select(attribute => $"{attribute.HttpMethods.Single()} {attribute.Template}"))
            .ToArray();

        routes.Should().Contain(
            "GET chart-of-accounts",
            "POST chart-of-accounts",
            "PATCH chart-of-accounts/{id:int}",
            "GET general-ledger",
            "GET journal-entries/{publicId:guid}",
            "GET trial-balance",
            "GET balance-sheet",
            "GET income-statement");
    }

    [Fact]
    public void Tenant_money_routes_include_ledger_summaries_and_recurring_charge_lifecycle()
    {
        var routes = typeof(TenantAccountMoneyController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes()
                .OfType<HttpMethodAttribute>()
                .Select(attribute => $"{attribute.HttpMethods.Single()} {attribute.Template}"))
            .ToArray();

        routes.Should().Contain(
            "GET ledger",
            "GET ledger/{entryId:long}",
            "GET month-summary",
            "GET ledger-summary",
            "GET recurring-charges",
            "POST recurring-charges",
            "PATCH recurring-charges/{id:int}",
            "POST recurring-charges/{id:int}/deactivate");
    }

    [Fact]
    public void Frozen_dto_shapes_keep_required_running_and_typed_allocation_fields()
    {
        typeof(TenantLedgerRow).GetProperty(nameof(TenantLedgerRow.RunningAmountOwed))
            .Should().NotBeNull()
            .And.Subject!.PropertyType.Should().Be(typeof(decimal));
        typeof(TenantLedgerRow).GetProperty(nameof(TenantLedgerRow.Allocations))
            .Should().NotBeNull()
            .And.Subject!.PropertyType.Should().Be(typeof(IReadOnlyList<AllocationRef>));
        typeof(GeneralLedgerRow).GetProperty(nameof(GeneralLedgerRow.RunningBalance))
            .Should().NotBeNull()
            .And.Subject!.PropertyType.Should().Be(typeof(decimal?));
        typeof(JournalDetail).GetProperty(nameof(JournalDetail.Currency))
            .Should().NotBeNull();
        typeof(JournalDetail).GetProperty(nameof(JournalDetail.BankReconciliationEvidence))
            .Should().NotBeNull()
            .And.Subject!.PropertyType.Should().Be(typeof(BankReconciliationEvidence));
        typeof(CreateRecurringTenantChargeRequest).GetProperty("Currency")
            .Should().BeNull();
    }
}
