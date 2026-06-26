using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

/// <summary>
/// Autopay off-session charging selection + safety. These tests exercise the gating, candidate
/// selection (only Active-enrolled, due, unpaid rent), and idempotency (no double-charge) — all of
/// which short-circuit BEFORE any Stripe network call, so they run without a real Stripe key. The
/// actual PaymentIntent creation + the Paid flip (via the API's existing webhook) are covered there.
/// </summary>
public class AutopayChargeServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Disabled_ChargesNothing()
    {
        var lease = SeedLease(tenantId: 10);
        SeedDueRent(lease);
        SeedEnrollment(lease, tenantId: 10, active: true);

        var sut = BuildService(enabled: false);

        var charged = await sut.ChargeDueAsync();

        // Gated: no-op, never reaches Stripe, no transactions created.
        charged.Should().Be(0);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipsLeasesWithoutActiveEnrollment()
    {
        // Stripe ENABLED so the only reason nothing charges is the selection filter (inactive
        // enrollment is excluded by the join BEFORE any network call).
        var lease = SeedLease(tenantId: 10);
        SeedDueRent(lease);
        SeedEnrollment(lease, tenantId: 10, active: false);

        var sut = BuildService(enabled: true);

        var charged = await sut.ChargeDueAsync();

        charged.Should().Be(0);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipsEndedFixedTermLeaseWithActiveEnrollment()
    {
        var lease = SeedLease(tenantId: 10);
        SeedDueRent(lease);
        SeedEnrollment(lease, tenantId: 10, active: true);
        lease.EndDate = DateTime.UtcNow.Date.AddDays(-1);
        lease.UpdatedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();

        var sut = BuildService(enabled: true);

        var charged = await sut.ChargeDueAsync();

        charged.Should().Be(0);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipsPaymentsAlreadyPaid()
    {
        var lease = SeedLease(tenantId: 10);
        var payment = SeedDueRent(lease);
        payment.Status = PaymentStatus.Paid;
        _ctx.Db.SaveChanges();
        SeedEnrollment(lease, tenantId: 10, active: true);

        var sut = BuildService(enabled: true);

        var charged = await sut.ChargeDueAsync();

        // Already paid → not a candidate → no Stripe call.
        charged.Should().Be(0);
    }

    [Fact]
    public async Task IsIdempotent_SkipsPaymentWithInFlightTransaction()
    {
        var lease = SeedLease(tenantId: 10);
        var payment = SeedDueRent(lease);
        SeedEnrollment(lease, tenantId: 10, active: true);

        // A pending transaction already exists for this payment (a prior charge is in flight) —
        // the service must NOT create a second charge (no double-charge), and must not call Stripe.
        _ctx.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            PortfolioId = PortfolioId,
            PaymentId = payment.Id,
            Amount = payment.Amount,
            Provider = "stripe",
            ProviderPaymentIntentId = "pi_existing",
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();

        var sut = BuildService(enabled: true);

        var charged = await sut.ChargeDueAsync();

        charged.Should().Be(0);
        _ctx.Db.PaymentTransactions.Should().HaveCount(1); // still just the pre-existing one
    }

    [Fact]
    public async Task IsIdempotent_DoesNotReChargeWhenAttemptWithSameKeyAlreadyExists()
    {
        // H-1 regression (real money): a prior hourly cycle charged Stripe and recorded a Pending
        // transaction carrying this charge's deterministic idempotency key, then the process died
        // before anything flipped the row. The NEXT cycle must recognise that prior attempt by its
        // IdempotencyKey and NOT create a second charge — no duplicate tenant debit.
        var lease = SeedLease(tenantId: 10);
        var payment = SeedDueRent(lease);
        SeedEnrollment(lease, tenantId: 10, active: true);

        // Deterministic key the service derives for this exact due charge (payment id + period).
        var idempotencyKey = $"autopay-{payment.Id}-{payment.PeriodKey}";

        var priorAttempt = new PaymentTransaction
        {
            PortfolioId = PortfolioId,
            PaymentId = payment.Id,
            Amount = payment.Amount,
            Provider = "stripe",
            IdempotencyKey = idempotencyKey,
            // No ProviderPaymentIntentId: models the crash window where the charge had returned but
            // the intent id was never persisted. Recovery must key off IdempotencyKey, not the intent.
            ProviderPaymentIntentId = null,
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.PaymentTransactions.Add(priorAttempt);
        _ctx.Db.SaveChanges();
        var priorId = priorAttempt.Id;

        var sut = BuildService(enabled: true);

        var charged = await sut.ChargeDueAsync();

        // No second charge initiated, and Stripe was never called (no network in this harness).
        charged.Should().Be(0);
        // Exactly one transaction still exists — the original — proving no duplicate row/charge.
        var rows = _ctx.Db.PaymentTransactions.Where(t => t.PaymentId == payment.Id).ToList();
        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(priorId);
        rows[0].IdempotencyKey.Should().Be(idempotencyKey);
    }

    [Fact]
    public async Task SkipsFuturePayments_NotYetDue()
    {
        var lease = SeedLease(tenantId: 10);
        var payment = SeedDueRent(lease);
        payment.DueDate = DateTime.UtcNow.AddDays(10); // not due yet
        _ctx.Db.SaveChanges();
        SeedEnrollment(lease, tenantId: 10, active: true);

        var sut = BuildService(enabled: true);

        var charged = await sut.ChargeDueAsync();

        charged.Should().Be(0);
        _ctx.Db.PaymentTransactions.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------

    private AutopayChargeService BuildService(bool enabled)
    {
        var config = new StripeConfig { SecretKey = enabled ? "sk_test_fake" : null };
        return new AutopayChargeService(
            _ctx.Db,
            Options.Create(config),
            NullLogger<AutopayChargeService>.Instance);
    }

    private Lease SeedLease(int tenantId)
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
            MonthlyRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private Payment SeedDueRent(Lease lease, decimal amount = 1000m)
    {
        var now = DateTime.UtcNow;
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
        return payment;
    }

    private AutopayEnrollment SeedEnrollment(Lease lease, int tenantId, bool active)
    {
        var enrollment = new AutopayEnrollment
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            TenantId = tenantId,
            StripeCustomerId = "cus_test",
            StripePaymentMethodId = "pm_test",
            Active = active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.AutopayEnrollments.Add(enrollment);
        _ctx.Db.SaveChanges();
        return enrollment;
    }
}
