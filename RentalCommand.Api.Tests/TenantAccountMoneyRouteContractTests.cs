using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Payments;

namespace RentalCommand.Api.Tests;

public sealed class TenantAccountMoneyRouteContractTests
{
    [Fact]
    public void Canonical_tenant_account_routes_are_explicit_append_commands()
    {
        var routes = typeof(TenantAccountMoneyController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpPostAttribute>()
                .Select(attribute => attribute.Template))
            .ToArray();

        routes.Should().BeEquivalentTo("receipts", "deposit/fund", "deposit/deductions", "deposit/refunds");
    }

    [Fact]
    public void Legacy_payment_and_deposit_mutation_routes_are_not_exposed()
    {
        typeof(PaymentController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Should().OnlyContain(attribute => attribute is HttpGetAttribute);

        typeof(SecurityDepositsController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Should().OnlyContain(attribute => attribute is HttpGetAttribute);
    }

    [Fact]
    public void Every_money_handler_reauthorizes_receipt_replay()
    {
        typeof(RecordTenantReceiptHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RecordTenantReceiptCommand>>();
        typeof(FundSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<FundSecurityDepositCommand>>();
        typeof(DeductSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<DeductSecurityDepositCommand>>();
        typeof(RefundSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RefundSecurityDepositCommand>>();
    }

    [Fact]
    public void Completed_deposit_payout_has_direct_provenance_without_a_required_tenant_ledger_row()
    {
        typeof(SecurityDepositEntry).GetProperty(nameof(SecurityDepositEntry.PayoutExternalReference))
            .Should().NotBeNull();
        var ledgerResultProperty = typeof(SecurityDepositMutationResult)
            .GetProperty(nameof(SecurityDepositMutationResult.TenantLedgerEntryId));

        Nullable.GetUnderlyingType(ledgerResultProperty!.PropertyType).Should().Be(typeof(long));
    }
}
