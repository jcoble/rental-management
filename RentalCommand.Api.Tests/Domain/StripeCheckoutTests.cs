using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Configuration;
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
            PortfolioId, tenantId: 10, payment.Id, successUrl: null, cancelUrl: null, CancellationToken.None);

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
            PortfolioId, tenantId: 10, lease.Id, successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotEnabled);
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
            PortfolioId, tenantId: 20, payment.Id, successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotFound);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AutopayEnroll_ForAnotherTenantsLease_ReturnsNotFound()
    {
        var (lease, _) = SeedLeaseAndScheduledRent(tenantId: 10);
        var sut = BuildService(enabled: true);

        var result = await sut.CreateAutopaySetupSessionAsync(
            PortfolioId, tenantId: 20, lease.Id, successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotFound);
    }

    // -----------------------------------------------------------------------
    // Webhook: checkout.session.completed marks the linked Payment Paid (idempotently).

    [Fact]
    public async Task Webhook_CheckoutSessionCompleted_MarksPaymentPaid_AndIsIdempotent()
    {
        var (_, payment) = SeedLeaseAndScheduledRent(tenantId: 10);

        // Simulate the pending transaction the checkout endpoint created (keyed by the session id).
        const string sessionId = "cs_test_123";
        _ctx.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            PortfolioId = PortfolioId,
            PaymentId = payment.Id,
            Amount = payment.Amount,
            Currency = "usd",
            Provider = "stripe",
            ProviderPaymentIntentId = sessionId,
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();

        var sut = BuildService(enabled: true);
        var (json, signature) = BuildSignedCheckoutCompletedEvent(
            eventId: "evt_1", sessionId: sessionId, paymentId: payment.Id, paymentStatus: "paid");

        await sut.HandleWebhookEventAsync(json, signature, CancellationToken.None);

        var paid = _ctx.Db.Payments.Single(p => p.Id == payment.Id);
        paid.Status.Should().Be(PaymentStatus.Paid);
        paid.PaidDate.Should().NotBeNull();

        var tx = _ctx.Db.PaymentTransactions.Single();
        tx.Status.Should().Be(PaymentTransactionStatus.Succeeded);

        // Idempotent: a duplicate delivery (same event id) is skipped — state unchanged, no second event row.
        await sut.HandleWebhookEventAsync(json, signature, CancellationToken.None);
        _ctx.Db.StripeWebhookEvents.Count(e => e.EventId == "evt_1").Should().Be(1);
    }

    [Fact]
    public async Task Webhook_CheckoutSessionCompleted_WhenUnpaid_DoesNotMarkPaid()
    {
        var (_, payment) = SeedLeaseAndScheduledRent(tenantId: 10);

        const string sessionId = "cs_test_ach_pending";
        _ctx.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            PortfolioId = PortfolioId,
            PaymentId = payment.Id,
            Amount = payment.Amount,
            Provider = "stripe",
            ProviderPaymentIntentId = sessionId,
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();

        var sut = BuildService(enabled: true);
        // ACH sessions can complete while still "unpaid" (processing) — must NOT flip to Paid yet.
        var (json, signature) = BuildSignedCheckoutCompletedEvent(
            eventId: "evt_2", sessionId: sessionId, paymentId: payment.Id, paymentStatus: "unpaid");

        await sut.HandleWebhookEventAsync(json, signature, CancellationToken.None);

        _ctx.Db.Payments.Single(p => p.Id == payment.Id).Status.Should().Be(PaymentStatus.Scheduled);
        _ctx.Db.PaymentTransactions.Single().Status.Should().Be(PaymentTransactionStatus.Pending);
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
            _ctx.Db,
            Options.Create(config),
            new SandboxGuard(_ctx.Db),
            NullLogger<StripePaymentService>.Instance);
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

    /// <summary>
    /// Builds a minimal <c>checkout.session.completed</c> event JSON and a valid Stripe-Signature
    /// header (HMAC-SHA256 of <c>{timestamp}.{payload}</c> with the webhook secret) so the real
    /// <c>EventUtility.ConstructEvent</c> signature check passes.
    /// </summary>
    private static (string json, string signature) BuildSignedCheckoutCompletedEvent(
        string eventId, string sessionId, int paymentId, string paymentStatus)
    {
        var json = $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2024-04-10",
          "created": 1700000000,
          "livemode": false,
          "pending_webhooks": 1,
          "request": { "id": null, "idempotency_key": null },
          "type": "checkout.session.completed",
          "data": {
            "object": {
              "id": "{{sessionId}}",
              "object": "checkout.session",
              "mode": "payment",
              "payment_status": "{{paymentStatus}}",
              "payment_intent": "pi_test_for_{{sessionId}}",
              "metadata": { "paymentId": "{{paymentId}}", "portfolioId": "1" }
            }
          }
        }
        """;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = $"{timestamp}.{json}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var signature = Convert.ToHexString(hash).ToLowerInvariant();

        return (json, $"t={timestamp},v1={signature}");
    }
}
