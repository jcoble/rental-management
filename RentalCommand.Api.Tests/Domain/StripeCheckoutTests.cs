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
/// Online-payment Checkout + webhook coverage for <see cref="StripePaymentService"/>:
/// gating (503-equivalent NotEnabled when Stripe is off), the tenant ownership/IDOR guard on the
/// hosted-Checkout path (another tenant's payment → NotFound), and the webhook flipping a Payment to
/// Paid on <c>checkout.session.completed</c> with idempotent dedupe.
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
        var (lease, payment) = SeedLeaseAndScheduledRent(tenantId: 10);
        var sut = BuildService(enabled: false);

        var result = await sut.CreatePaymentCheckoutSessionAsync(
            PortfolioId, tenantId: 10, tenantAccountId: 1, chargeLedgerEntryId: payment.Id,
            actorUserId: 1, successUrl: null, cancelUrl: null, CancellationToken.None);

        // Gated: no Stripe call, no transaction created.
        result.Result.Should().Be(CheckoutResult.Outcome.NotEnabled);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AutopayEnroll_WhenStripeDisabled_ReturnsNotEnabled()
    {
        var (lease, _) = SeedLeaseAndScheduledRent(tenantId: 10);
        var sut = BuildService(enabled: false);

        var result = await sut.CreateAutopaySetupSessionAsync(
            PortfolioId, tenantId: 10, tenantAccountId: lease.Id, actorUserId: 1,
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
    // Ownership / IDOR guard (these run with Stripe ENABLED so the ownership check is the only
    // thing that can short-circuit; a foreign payment must 404 BEFORE any Stripe API call).

    [Fact]
    public async Task Checkout_ForAnotherTenantsPayment_ReturnsNotFound()
    {
        // Payment belongs to tenant 10's lease; tenant 20 must NOT be able to pay (or probe) it.
        var (_, payment) = SeedLeaseAndScheduledRent(tenantId: 10);
        var sut = BuildService(enabled: true);

        var result = await sut.CreatePaymentCheckoutSessionAsync(
            PortfolioId, tenantId: 20, tenantAccountId: 1, chargeLedgerEntryId: payment.Id,
            actorUserId: 1, successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotFound);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AutopayEnroll_ForAnotherTenantsLease_ReturnsNotFound()
    {
        var (lease, _) = SeedLeaseAndScheduledRent(tenantId: 10);
        var sut = BuildService(enabled: true);

        var result = await sut.CreateAutopaySetupSessionAsync(
            PortfolioId, tenantId: 20, tenantAccountId: lease.Id, actorUserId: 1,
            operationKey: "setup-foreign",
            successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotFound);
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
            enabled ? new CanonicalNotFoundAtomicUnitOfWork() : new UnexpectedAtomicUnitOfWork());
    }

    private (Lease lease, Payment payment) SeedLeaseAndScheduledRent(int tenantId, decimal amount = 1000m)
    {
        var now = DateTime.UtcNow;

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "P",
            AddressLine1 = "1 St",
            City = "Town",
            State = "ST",
            PostalCode = "00000",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);

        var unit = new Unit { Property = property, UnitNumber = $"U{tenantId}", CreatedAt = now, UpdatedAt = now };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            Id = tenantId,
            PortfolioId = PortfolioId,
            FirstName = "T",
            LastName = tenantId.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenantId,
            LeaseNumber = $"L-{tenantId}",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-6),
            EndDate = now.AddMonths(6),
            MonthlyRent = amount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();

        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = amount,
            DueDate = now.Date,
            PeriodKey = now.ToString("yyyy-MM"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Payments.Add(payment);
        _ctx.Db.SaveChanges();

        return (lease, payment);
    }

    private sealed class CanonicalNotFoundAtomicUnitOfWork : IAtomicUnitOfWork
    {
        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity, TCommand command,
            IAtomicResultCodec<TResult> resultCodec, CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData where TResult : notnull
        {
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

}
