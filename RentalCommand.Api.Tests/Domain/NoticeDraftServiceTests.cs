using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class NoticeDraftServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GenerateAsync_DefaultRunOnlyCreatesLeaseNoticesInsideTriggerWindow()
    {
        var (nearLease, farLease) = SeedActiveLeases();
        var sut = CreateService();

        var result = await sut.GenerateAsync(1);

        // The near lease (60 days out) is inside the 75-day renewal/month-to-month window, so a
        // portfolio-wide run produces BOTH a renewal and a month-to-month draft for it.
        result.CreatedCount.Should().Be(2);
        result.Drafts.Should().Contain(d =>
            d.LeaseId == nearLease.Id &&
            d.NoticeType == "RenewalOffer");
        result.Drafts.Should().Contain(d =>
            d.LeaseId == nearLease.Id &&
            d.NoticeType == "MonthToMonthConversion");
        result.Drafts.Should().NotContain(d => d.LeaseId == farLease.Id);
        result.Drafts.Should().NotContain(d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_ForcedMoveOutNoticeCanTargetLeaseOutsideTriggerWindow()
    {
        var (_, farLease) = SeedActiveLeases();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = farLease.TenantId,
                NoticeType = "MoveOutReminder",
            });

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == farLease.Id &&
            d.NoticeType == "MoveOutReminder");
    }

    [Fact]
    public async Task GenerateAsync_ForcedRenewalFallsBackQuicklyWhenLlmIsSlow()
    {
        var (_, farLease) = SeedActiveLeases();
        var slowLlm = new SlowLlmProvider();
        var sut = CreateService(slowLlm);

        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();
        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = farLease.TenantId,
                NoticeType = "RenewalOffer",
            },
            testTimeout.Token);
        elapsed.Stop();

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == farLease.Id &&
            d.NoticeType == "RenewalOffer" &&
            d.Subject == "Lease renewal for Maple Grove Duplex Unit B");
        slowLlm.ChatCalls.Should().Be(1);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GenerateAsync_TenantScopedForcedRequestReturnsExistingDraftWhenAlreadyOpen()
    {
        var (_, farLease) = SeedActiveLeases();
        var now = DateTime.UtcNow;
        _ctx.Db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = 1,
            LeaseId = farLease.Id,
            TenantId = farLease.TenantId,
            PropertyId = farLease.PropertyId,
            NoticeType = "RenewalOffer",
            Status = "Draft",
            Subject = "Existing renewal draft",
            Body = "Existing body",
            Reason = "Already generated.",
            TriggerDate = farLease.EndDate.Date,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = farLease.TenantId,
                NoticeType = "RenewalOffer",
            });

        result.CreatedCount.Should().Be(0);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == farLease.Id &&
            d.NoticeType == "RenewalOffer" &&
            d.Subject == "Existing renewal draft");
    }

    [Fact]
    public async Task GenerateAsync_LateRentNoticeExcludesEndedFixedTermLeasePayments()
    {
        var (currentLease, staleLease) = SeedActiveLeases();
        var now = DateTime.UtcNow;
        var today = now.Date;
        var stale = _ctx.Db.Leases.Find(staleLease.Id)!;
        stale.EndDate = today.AddDays(-1);
        stale.UpdatedAt = now;
        _ctx.Db.Payments.AddRange(
            new Payment
            {
                PortfolioId = 1,
                LeaseId = currentLease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = 1400m,
                DueDate = today.AddDays(-10),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = 1,
                LeaseId = staleLease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = 1450m,
                DueDate = today.AddDays(-20),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest { NoticeType = "LateRentNotice" });

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == currentLease.Id &&
            d.NoticeType == "LateRentNotice");
        result.Drafts.Should().NotContain(d => d.LeaseId == staleLease.Id);
    }

    [Fact]
    public async Task GenerateAsync_RendersActiveLandlordTemplate_WhenOneExists()
    {
        var (nearLease, _) = SeedActiveLeases();
        _ctx.Db.NoticeTemplates.Add(new RentalCommand.Core.Entities.NoticeTemplate
        {
            PortfolioId = 1,
            NoticeType = "RenewalOffer",
            Subject = "Renewal for {{tenant_name}}",
            Body = "Hi {{tenant_name}}, renew {{property_address}}?",
            IsActive = true,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var sut = CreateService();
        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = nearLease.TenantId,
                NoticeType = "RenewalOffer",
            });

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("Renewal for Avery Brooks", draft.Subject);
        Assert.StartsWith("Hi Avery Brooks, renew", draft.Body);
    }

    [Fact]
    public async Task GenerateAsync_EmptyBodyTemplate_FallsBackToDeterministicCopy()
    {
        // Bypass the service guard by inserting the template directly into the DB.
        // The template has an empty Body, so ComposeAsync must fall through to the deterministic copy.
        var (nearLease, _) = SeedActiveLeases();
        _ctx.Db.NoticeTemplates.Add(new RentalCommand.Core.Entities.NoticeTemplate
        {
            PortfolioId = 1,
            NoticeType = "RenewalOffer",
            Subject = "Not empty subject",
            Body = "",   // empty — should trigger fallback
            IsActive = true,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var sut = CreateService();
        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = nearLease.TenantId,
                NoticeType = "RenewalOffer",
            });

        var draft = Assert.Single(result.Drafts);
        // Must NOT be blank — deterministic subject contains the property name.
        Assert.Equal("Lease renewal for Maple Grove Duplex Unit A", draft.Subject);
        Assert.False(string.IsNullOrWhiteSpace(draft.Body));
    }

    [Fact]
    public async Task GenerateAsync_PopulatesPortfolioNameToken()
    {
        // Update the seeded portfolio (Id=1) name so we can assert the token fills.
        var portfolio = _ctx.Db.Portfolios.Find(1)!;
        portfolio.Name = "Sunrise Rentals";
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var lease = SeedSingleActiveLease(firstName: "Dana", lastName: "West", monthlyRent: 1200m);
        _ctx.Db.NoticeTemplates.Add(new RentalCommand.Core.Entities.NoticeTemplate
        {
            PortfolioId = 1,
            NoticeType = "RenewalOffer",
            Subject = "Renewal from {{portfolio_name}}",
            Body = "From {{portfolio_name}}",
            IsActive = true,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var sut = CreateService();
        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = lease.TenantId,
                NoticeType = "RenewalOffer",
            });

        var draft = Assert.Single(result.Drafts);
        Assert.Contains("Sunrise Rentals", draft.Body);
        Assert.Contains("Sunrise Rentals", draft.Subject);
    }

    private NoticeDraftService CreateService(ILlmProvider? llm = null) => new(
        _ctx.Db,
        new NoopConversationService(),
        llm ?? new NoopLlmProvider(),
        NullLogger<NoticeDraftService>.Instance,
        TimeProvider.System);

    [Fact]
    public async Task GenerateAsync_RentReminder_produces_one_draft_with_tenant_name()
    {
        var lease = SeedSingleActiveLease(firstName: "Sam", lastName: "Rivera", monthlyRent: 1500m);
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest { TenantId = lease.TenantId, NoticeType = "RentReminder" });

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("RentReminder", draft.NoticeType);
        Assert.Contains("Sam Rivera", draft.Body);
    }

    [Fact]
    public async Task GenerateAsync_MonthToMonthConversion_produces_one_draft()
    {
        var lease = SeedSingleActiveLease(firstName: "Sam", lastName: "Rivera", monthlyRent: 1500m);
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest { TenantId = lease.TenantId, NoticeType = "MonthToMonthConversion" });

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("MonthToMonthConversion", draft.NoticeType);
    }

    [Fact]
    public async Task GenerateAsync_PortfolioWide_does_not_create_RentReminder()
    {
        // Lease ends 300 days out — outside the lease-end window — so no lease-end drafts at all.
        SeedSingleActiveLease();
        var sut = CreateService();

        var result = await sut.GenerateAsync(1, null);

        Assert.DoesNotContain(result.Drafts, d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_PortfolioWide_CreatesRentReminder_ForUpcomingPayment_AndIsIdempotent()
    {
        var lease = SeedSingleActiveLease(firstName: "Sam", lastName: "Rivera", monthlyRent: 1500m);
        var now = DateTime.UtcNow;
        var dueDate = now.Date.AddDays(3);
        _ctx.Db.Payments.Add(new Payment
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1500m,
            DueDate = dueDate,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var first = await sut.GenerateAsync(1, null);

        var reminders = first.Drafts.Where(d => d.NoticeType == "RentReminder").ToList();
        reminders.Should().ContainSingle();
        reminders[0].LeaseId.Should().Be(lease.Id);
        reminders[0].TriggerDate.Date.Should().Be(dueDate);

        _ctx.Db.ChangeTracker.Clear();
        var second = await sut.GenerateAsync(1, null);
        second.Drafts.Should().NotContain(d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_PaymentScopedRentReminder_TargetsRequestedPayment()
    {
        var lease = SeedSingleActiveLease(firstName: "Sam", lastName: "Rivera", monthlyRent: 1500m);
        var otherLease = SeedSingleActiveLease(firstName: "Avery", lastName: "Brooks", monthlyRent: 1700m);
        var now = DateTime.UtcNow;
        var dueDate = now.Date.AddDays(3);
        var targetPayment = new Payment
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1500m,
            DueDate = dueDate,
            PeriodKey = dueDate.ToString("yyyy-MM"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Payments.AddRange(
            targetPayment,
            new Payment
            {
                PortfolioId = 1,
                LeaseId = otherLease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = 1700m,
                DueDate = dueDate,
                PeriodKey = dueDate.ToString("yyyy-MM"),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                PaymentId = targetPayment.Id,
                NoticeType = "RentReminder",
            });

        result.CreatedCount.Should().Be(1);
        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.PaymentId.Should().Be(targetPayment.Id);
        draft.LeaseId.Should().Be(lease.Id);
        draft.NoticeType.Should().Be("RentReminder");
        draft.TriggerDate.Date.Should().Be(dueDate);
    }

    [Fact]
    public async Task GenerateAsync_PaymentScopedLateNotice_IncludesLateFeeForSamePeriod()
    {
        var lease = SeedSingleActiveLease(firstName: "Tom", lastName: "Hardy", monthlyRent: 2200m);
        var now = DateTime.UtcNow;
        var dueDate = now.Date.AddDays(-10);
        var periodKey = dueDate.ToString("yyyy-MM");
        var rent = new Payment
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Late,
            Amount = 2200m,
            DueDate = dueDate,
            PeriodKey = periodKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Payments.AddRange(
            rent,
            new Payment
            {
                PortfolioId = 1,
                LeaseId = lease.Id,
                PaymentType = PaymentType.LateFee,
                Status = PaymentStatus.Scheduled,
                Amount = 75m,
                DueDate = dueDate.AddDays(5),
                PeriodKey = periodKey,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                PaymentId = rent.Id,
                NoticeType = "LateRentNotice",
            });

        result.CreatedCount.Should().Be(1);
        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.PaymentId.Should().Be(rent.Id);
        draft.NoticeType.Should().Be("LateRentNotice");
        draft.Reason.Should().Contain("$75");
        draft.Body.Should().Contain("$2,275");
    }

    [Fact]
    public async Task GenerateAsync_LeaseScopedLateNotice_DoesNotLeakOtherTenantLease()
    {
        var currentLease = SeedSingleActiveLease(firstName: "Tom", lastName: "Hardy", monthlyRent: 2200m);
        var otherLease = SeedSingleActiveLease(firstName: "Tom", lastName: "Hardy", monthlyRent: 1800m);
        var now = DateTime.UtcNow;
        var currentPeriod = now.Date.AddDays(-8).ToString("yyyy-MM");
        _ctx.Db.Payments.AddRange(
            new Payment
            {
                PortfolioId = 1,
                LeaseId = currentLease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Late,
                Amount = 146.67m,
                DueDate = now.Date.AddDays(-38),
                PeriodKey = now.Date.AddDays(-38).ToString("yyyy-MM"),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = 1,
                LeaseId = currentLease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Late,
                Amount = 2200m,
                DueDate = now.Date.AddDays(-8),
                PeriodKey = currentPeriod,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = 1,
                LeaseId = currentLease.Id,
                PaymentType = PaymentType.LateFee,
                Status = PaymentStatus.Scheduled,
                Amount = 75m,
                DueDate = now.Date.AddDays(-1),
                PeriodKey = currentPeriod,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = 1,
                LeaseId = otherLease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Late,
                Amount = 1800m,
                DueDate = now.Date.AddDays(-12),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = currentLease.TenantId,
                LeaseId = currentLease.Id,
                NoticeType = "LateRentNotice",
            });

        result.CreatedCount.Should().Be(1);
        var draft = result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == currentLease.Id &&
            d.NoticeType == "LateRentNotice").Which;
        draft.Body.Should().Contain("$2,275");
        draft.Body.Should().NotContain("$222");
        result.Drafts.Should().NotContain(d => d.LeaseId == otherLease.Id);
    }

    [Fact]
    public async Task GenerateAsync_PaymentScopedExistingNotice_ReturnsExistingWithoutDuplicate()
    {
        var lease = SeedSingleActiveLease(firstName: "Sam", lastName: "Rivera", monthlyRent: 1500m);
        var now = DateTime.UtcNow;
        var payment = new Payment
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1500m,
            DueDate = now.Date.AddDays(3),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Payments.Add(payment);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentId = payment.Id,
            TenantId = lease.TenantId,
            PropertyId = lease.PropertyId,
            NoticeType = "RentReminder",
            Status = "Approved",
            Subject = "Existing reminder",
            Body = "Existing body",
            Reason = "Already sent.",
            TriggerDate = payment.DueDate.Date,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                PaymentId = payment.Id,
                NoticeType = "RentReminder",
            });

        result.CreatedCount.Should().Be(0);
        result.Drafts.Should().ContainSingle(d =>
            d.PaymentId == payment.Id &&
            d.Subject == "Existing reminder");
        _ctx.Db.NoticeDrafts.Count(d => d.PaymentId == payment.Id).Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_PortfolioWide_DoesNotCreateRentReminder_ForPaymentBeyondLeadWindow()
    {
        var lease = SeedSingleActiveLease(firstName: "Sam", lastName: "Rivera", monthlyRent: 1500m);
        var now = DateTime.UtcNow;
        _ctx.Db.Payments.Add(new Payment
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1500m,
            DueDate = now.Date.AddDays(30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        var result = await sut.GenerateAsync(1, null);

        result.Drafts.Should().NotContain(d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_PortfolioWide_CreatesMonthToMonthAlongsideRenewal_ForNearEndLease()
    {
        var (nearLease, _) = SeedActiveLeases();
        var sut = CreateService();

        var result = await sut.GenerateAsync(1, null);

        result.Drafts.Should().Contain(d =>
            d.LeaseId == nearLease.Id && d.NoticeType == "RenewalOffer");
        result.Drafts.Should().Contain(d =>
            d.LeaseId == nearLease.Id && d.NoticeType == "MonthToMonthConversion");
        result.Drafts.Should().NotContain(d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_ExplicitRentReminder_WithUpcomingPayment_ProducesExactlyOneReminder()
    {
        // Regression test for task 354: explicit per-tenant RentReminder request should NOT
        // trigger the period-based path when an upcoming payment exists within the lead window.
        // Previously, both paths fired → two drafts.
        var lease = SeedSingleActiveLease(firstName: "Chris", lastName: "Jordan", monthlyRent: 1600m);
        var now = DateTime.UtcNow;
        var paymentDueDate = now.Date.AddDays(3);  // Within the 7-day lead window
        _ctx.Db.Payments.Add(new Payment
        {
            PortfolioId = 1,
            LeaseId = lease.Id,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1600m,
            DueDate = paymentDueDate,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var sut = CreateService();

        // Explicit per-tenant RentReminder request
        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = lease.TenantId,
                NoticeType = "RentReminder"
            });

        // Should produce exactly ONE RentReminder, not two
        var reminders = result.Drafts.Where(d => d.NoticeType == "RentReminder").ToList();
        reminders.Should().ContainSingle();
        reminders[0].LeaseId.Should().Be(lease.Id);
        reminders[0].TenantId.Should().Be(lease.TenantId);
    }

    /// <summary>Seeds one active lease with a far-future end date (always outside the default trigger window).</summary>
    private Lease SeedSingleActiveLease(string firstName = "Sam", string lastName = "Rivera", decimal monthlyRent = 1500m)
    {
        var now = DateTime.UtcNow;
        var property = new RentalCommand.Core.Entities.Property
        {
            PortfolioId = 1,
            Name = "River View Flats",
            AddressLine1 = "10 River Rd",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = monthlyRent,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = $"SINGLE-{Guid.NewGuid().ToString()[..6].ToUpperInvariant()}",
            Status = LeaseStatus.Active,
            StartDate = now.Date.AddMonths(-6),
            EndDate = now.Date.AddDays(300),  // far future — outside default renewal/move-out window
            MonthlyRent = monthlyRent,
            SecurityDeposit = monthlyRent,
            LateFeeAmount = 50,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return lease;
    }

    private (Lease NearLease, Lease FarLease) SeedActiveLeases()
    {
        var now = DateTime.UtcNow;
        var property = new RentalCommand.Core.Entities.Property
        {
            PortfolioId = 1,
            Name = "Maple Grove Duplex",
            AddressLine1 = "100 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nearUnit = new Unit
        {
            Property = property,
            UnitNumber = "A",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1400,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var farUnit = new Unit
        {
            Property = property,
            UnitNumber = "B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nearTenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Avery",
            LastName = "Brooks",
            Email = "avery@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var farTenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Morgan",
            LastName = "Pierce",
            Email = "morgan@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nearLease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = nearUnit,
            Tenant = nearTenant,
            LeaseNumber = "NEAR-001",
            Status = LeaseStatus.Active,
            StartDate = now.Date.AddMonths(-10),
            EndDate = now.Date.AddDays(60),
            MonthlyRent = 1400,
            SecurityDeposit = 1400,
            LateFeeAmount = 50,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var farLease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = farUnit,
            Tenant = farTenant,
            LeaseNumber = "FAR-001",
            Status = LeaseStatus.Active,
            StartDate = now.Date.AddMonths(-2),
            EndDate = now.Date.AddDays(120),
            MonthlyRent = 1450,
            SecurityDeposit = 1450,
            LateFeeAmount = 50,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.AddRange(nearLease, farLease);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return (nearLease, farLease);
    }

    private sealed class NoopLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "noop"));
    }

    private sealed class SlowLlmProvider : ILlmProvider
    {
        public int ChatCalls { get; private set; }

        public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
        {
            ChatCalls++;
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "{\"subject\":\"slow\",\"body\":\"slow\"}";
        }

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "noop"));
    }

    private sealed class NoopConversationService : IConversationService
    {
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<ConversationListResponse> ListPageAsync(int portfolioId, ListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ConversationListResponse());

        public Task<int> GetUnreadCountAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<ConversationDetail?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> StartAsync(
            int portfolioId,
            int tenantId,
            string subject,
            string body,
            List<string> channels,
            string operationKey,
            bool acknowledgedFairHousingReview = false,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(new ConversationDetail { Id = 1, TenantId = tenantId, Subject = subject });

        public Task<ConversationDetail?> PostMessageAsync(
            int portfolioId,
            int id,
            string body,
            List<string> channels,
            string operationKey,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(
            int portfolioId,
            int tenantId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<ConversationDetail?> GetForTenantAsync(
            int portfolioId,
            int tenantId,
            int id,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> TenantStartAsync(
            int portfolioId,
            int tenantId,
            string subject,
            string body,
            string operationKey,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> TenantPostAsync(
            int portfolioId,
            int tenantId,
            int id,
            string body,
            string operationKey,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);
    }
}
