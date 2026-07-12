using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
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

        routes.Should().BeEquivalentTo("receipts", "charges", "charges/{chargeEntryId:long}/reversals",
            "deposit/fund", "deposit/deductions", "deposit/refunds");
    }

    [Fact]
    public void Every_tenant_money_append_route_requires_an_idempotency_header()
    {
        var methods = typeof(TenantAccountMoneyController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null);

        methods.Should().OnlyContain(method => method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key"));
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
    public void Every_money_handler_reauthorizes_atomic_replay()
    {
        typeof(RecordTenantReceiptHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RecordTenantReceiptCommand>>();
        typeof(PostTenantChargeHandler).Should()
            .Implement<IAtomicReplayAuthorizer<PostTenantChargeCommand>>();
        typeof(ReverseTenantChargeHandler).Should()
            .Implement<IAtomicReplayAuthorizer<ReverseTenantChargeCommand>>();
        typeof(FundSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<FundSecurityDepositCommand>>();
        typeof(DeductSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<DeductSecurityDepositCommand>>();
        typeof(RefundSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RefundSecurityDepositCommand>>();
    }

    [Fact]
    public void Charge_reversal_is_typed_to_charge_routes_and_has_no_client_amount()
    {
        typeof(PostTenantChargeRequest).GetProperty("EntryType").Should().BeNull();
        typeof(PostTenantChargeCommand).GetProperty("EntryType").Should().BeNull();
        typeof(PostTenantChargeRequest).GetProperty("LeaseAgreementId").Should().BeNull();
        typeof(PostTenantChargeRequest).GetProperty("LeaseAddendumId").Should().BeNull();
        typeof(PostTenantChargeCommand).GetProperty("LeaseAgreementId").Should().BeNull();
        typeof(PostTenantChargeCommand).GetProperty("LeaseAddendumId").Should().BeNull();
        typeof(ReverseTenantChargeRequest).GetProperty("Amount").Should().BeNull();
        typeof(ReverseTenantChargeCommand).GetProperty("Amount").Should().BeNull();
        typeof(ReverseTenantChargeRequest).GetProperty("Reason").Should().NotBeNull();
        typeof(ReverseTenantChargeCommand).GetProperty("Reason").Should().NotBeNull();
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
