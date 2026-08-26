using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Configuration gating for the provider-backed tenant-account payment flow.
/// </summary>
public class StripeCheckoutTests : IDisposable
{
    private const int PortfolioId = 1;
    private const string WebhookSecret = "whsec_test_secret";

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------
    // Gating

    [Fact]
    public async Task Checkout_WhenStripeDisabled_ReturnsNotEnabled()
    {
        var sut = BuildService(enabled: false);

        var result = await sut.CreatePaymentCheckoutSessionAsync(
            PortfolioId, tenantId: 10, tenantAccountId: 1, chargeLedgerEntryId: 1,
            actorUserId: 1, successUrl: null, cancelUrl: null, CancellationToken.None);

        // Gated: no Stripe call and no canonical provider attempt created.
        result.Result.Should().Be(CheckoutResult.Outcome.NotEnabled);
        _ctx.Db.TenantPaymentAttempts.Should().BeEmpty();
    }

    [Fact]
    public async Task AutopayEnroll_WhenStripeDisabled_ReturnsNotEnabled()
    {
        var sut = BuildService(enabled: false);

        var result = await sut.CreateAutopaySetupSessionAsync(
            PortfolioId, tenantId: 10, tenantAccountId: 1, actorUserId: 1,
            operationKey: "setup-disabled",
            successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotEnabled);
    }

    [Fact]
    public async Task IsOnlinePaymentsAvailableAsync_ReflectsStripeConfiguration()
    {
        var disabled = BuildService(enabled: false);
        var enabled = BuildService(enabled: true);

        (await disabled.IsOnlinePaymentsAvailableAsync(PortfolioId, CancellationToken.None)).Should().BeFalse();
        (await enabled.IsOnlinePaymentsAvailableAsync(PortfolioId, CancellationToken.None)).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Helpers

    private StripePaymentService BuildService(bool enabled)
    {
        var config = new StripeConfig
        {
            SecretKey = enabled ? "sk_test_fake" : null,
            PublishableKey = enabled ? "pk_test_fake" : null,
            WebhookSecret = WebhookSecret,
        };

        return new StripePaymentService(
            Options.Create(config),
            new SandboxGuard(_ctx.Db),
            NullLogger<StripePaymentService>.Instance,
            TimeProvider.System,
            _ctx.Db,
            enabled
                ? new CanonicalNotFoundRequestWriteExecutor()
                : new UnexpectedRequestWriteExecutor());
    }

    private sealed class CanonicalNotFoundRequestWriteExecutor : IWriteExecutor
    {
        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey, TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData where TResult : notnull
        {
            var command = write.Request;
            object result = command switch
            {
                PrepareProviderPaymentCreateCommand prepare => new PrepareProviderPaymentCreateResult(
                    PrepareProviderPaymentCreateOutcome.NotFound, prepare.PortfolioId,
                    prepare.TenantAccountId, prepare.ChargeLedgerEntryId, 0, 0,
                    prepare.Currency, prepare.Provider, prepare.IdempotencyKey, null, null),
                PrepareProviderAutopaySetupCommand setup => new PrepareProviderAutopaySetupResult(
                    PrepareProviderAutopaySetupOutcome.NotFound, setup.PortfolioId,
                    setup.TenantAccountId, 0, setup.ActorUserId, 0, setup.Provider,
                    setup.IdempotencyKey),
                _ => throw new InvalidOperationException($"Unexpected command {typeof(TCommand).Name}."),
            };
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
        }
    }

    private sealed class UnexpectedRequestWriteExecutor : IWriteExecutor
    {
        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey, TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData where TResult : notnull =>
            throw new InvalidOperationException("A provider write was not expected.");
    }

}
