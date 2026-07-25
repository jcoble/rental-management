using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Payments;

namespace RentalCommand.Api.Tests;

public sealed class TenantAccountMoneyRouteContractTests
{
    [Theory]
    [InlineData("Payment")]
    [InlineData("TenantLedger")]
    public void Accounting_ledger_routes_carry_canonical_account_and_entry_ids(string kind)
    {
        AccountingService.DetailHrefFor(kind, 812, 42)
            .Should().Be("/tenant-accounts/42/entries/812");
        typeof(AccountingTransactionResponse).GetProperty(nameof(AccountingTransactionResponse.TenantAccountId))
            .Should().NotBeNull();
        typeof(LedgerTransactionResponse).GetProperty(nameof(LedgerTransactionResponse.TenantAccountId))
            .Should().NotBeNull();
    }

    [Fact]
    public void Accounting_ledger_routes_reject_missing_account_context()
    {
        var act = () => AccountingService.DetailHrefFor("Payment", 812, null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Canonical_tenant_account_routes_are_explicit_append_commands()
    {
        var routes = typeof(TenantAccountMoneyController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpPostAttribute>()
                .Select(attribute => attribute.Template))
            .ToArray();

        routes.Should().BeEquivalentTo("receipts", "charges", "charges/{chargeEntryId:long}/reversals",
            "credits", "adjustments", "reversals", "deposit/fund", "deposit/deductions",
            "deposit/refunds", "refunds", "deposit/reversals");
    }

    [Fact]
    public void Every_tenant_money_append_route_requires_an_idempotency_header()
    {
        var methods = typeof(TenantAccountMoneyController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null);

        var hasIdempotencyHeader = methods.Select(method => method.GetParameters().Any(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>() is { Name: "Idempotency-Key" }));

        hasIdempotencyHeader.Should().OnlyContain(required => required);
    }

    [Fact]
    public void Legacy_payment_controller_is_absent_and_stripe_webhook_route_survives()
    {
        typeof(TenantAccountMoneyController).Assembly
            .GetType("RentalCommand.Api.Controllers.PaymentController")
            .Should().BeNull();
        typeof(StripeWebhookController).GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/v1/payments/stripe");
    }

    [Fact]
    public void Legacy_deposit_controller_is_absent_after_tenant_account_cutover()
    {
        typeof(TenantAccountMoneyController).Assembly
            .GetType("RentalCommand.Api.Controllers.SecurityDepositsController")
            .Should().BeNull();
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
        typeof(PostTenantCreditHandler).Should()
            .Implement<IAtomicReplayAuthorizer<PostTenantCreditCommand>>();
        typeof(PostTenantAdjustmentHandler).Should()
            .Implement<IAtomicReplayAuthorizer<PostTenantAdjustmentCommand>>();
        typeof(ReverseTenantLedgerEntryHandler).Should()
            .Implement<IAtomicReplayAuthorizer<ReverseTenantLedgerEntryCommand>>();
        typeof(RefundTenantPaymentHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RefundTenantPaymentCommand>>();
        typeof(FundSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<FundSecurityDepositCommand>>();
        typeof(DeductSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<DeductSecurityDepositCommand>>();
        typeof(RefundSecurityDepositHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RefundSecurityDepositCommand>>();
        typeof(ReverseSecurityDepositEntryHandler).Should()
            .Implement<IAtomicReplayAuthorizer<ReverseSecurityDepositEntryCommand>>();
    }

    [Fact]
    public void Canonical_credit_adjustment_and_reversal_contracts_are_typed()
    {
        typeof(PostTenantCreditCommand).GetProperty(nameof(PostTenantCreditCommand.AllocateOldestCharges))
            .Should().NotBeNull();
        typeof(PostTenantAdjustmentCommand).GetProperty(nameof(PostTenantAdjustmentCommand.Direction))
            .Should().NotBeNull();
        typeof(PostTenantAdjustmentCommand).GetProperty("DueOn").Should().BeNull();
        typeof(PostTenantAdjustmentCommand).GetProperty("PaymentMethodSummary").Should().BeNull();
        typeof(ReverseTenantLedgerEntryCommand).GetProperty("Amount").Should().BeNull();
        typeof(ReverseTenantLedgerEntryCommand).GetProperty(nameof(ReverseTenantLedgerEntryCommand.ReversesEntryId))
            .Should().NotBeNull();
    }

    [Fact]
    public void Payment_refund_and_deposit_reversal_have_dedicated_typed_routes()
    {
        typeof(RefundTenantPaymentCommand).GetProperty(
            nameof(RefundTenantPaymentCommand.PaymentEntryId)).Should().NotBeNull();
        typeof(RefundTenantPaymentCommand).GetProperty("Amount").Should().BeNull();
        typeof(ReverseSecurityDepositEntryCommand).GetProperty(
            nameof(ReverseSecurityDepositEntryCommand.SecurityDepositAccountId))
            .Should().NotBeNull();
        typeof(ReverseSecurityDepositEntryCommand).GetProperty(
            nameof(ReverseSecurityDepositEntryCommand.ReversesEntryId)).Should().NotBeNull();
    }

    [Fact]
    public void Payment_correction_uses_authorized_idempotent_immutable_refund_contract()
    {
        var refund = typeof(TenantAccountMoneyController).GetMethod(
            nameof(TenantAccountMoneyController.RefundPayment))!;

        refund.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("refunds");
        refund.GetParameters().Should().Contain(parameter =>
            parameter.GetCustomAttribute<FromHeaderAttribute>() != null &&
            parameter.GetCustomAttribute<FromHeaderAttribute>()!.Name == "Idempotency-Key");
        refund.GetParameters().Should().Contain(parameter =>
            parameter.ParameterType == typeof(RefundTenantPaymentRequest));
        typeof(RefundTenantPaymentCommand).GetProperty(
            nameof(RefundTenantPaymentCommand.RequiredCapability)).Should().NotBeNull();
        typeof(RefundTenantPaymentHandler).Should()
            .Implement<IAtomicReplayAuthorizer<RefundTenantPaymentCommand>>();
        typeof(TenantPaymentRefundResult).GetProperty(
            nameof(TenantPaymentRefundResult.RefundEntryId)).Should().NotBeNull();
        typeof(TenantPaymentRefundResult).GetProperty(
            nameof(TenantPaymentRefundResult.CompensatedAllocationCount)).Should().NotBeNull();
        typeof(RefundTenantPaymentRequest).GetProperty("ReversesEntryId").Should().BeNull();
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
