using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// The headline Phase 2 path (AC-2 / AC-5 / AC-6) against a FAKE <see cref="IAccountingProvider"/>:
/// the pull-into-domain import (a) creates a real RC <see cref="Payment"/> from a pulled payment
/// whose customer has a confirmed Customer→Tenant mapping (right lease / amount / Paid / date),
/// (b) does NOT duplicate on re-import (the ledger gates it), and (c) routes an unmatchable txn to
/// the review queue (never silently created, never dropped). Plus the auto-link decision: an
/// unambiguous name match auto-confirms; two equally-strong rivals stay a suggestion.
/// Deliberately the single focused integration test — the sandbox flow is the real signal.
/// </summary>
public sealed class AccountingImportServiceTests : IDisposable
{
    private const int PortfolioId = 1; // seeded by SqliteTestContext
    private const string Realm = "realm-acme";

    private readonly SqliteTestContext _ctx = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Import_CreatesPayment_NoDuplicateOnReimport_AndQueuesUnmatched()
    {
        // --- Arrange: skeleton (property/unit/tenant/lease) + a Connected QBO connection + a confirmed
        // Customer→Tenant mapping. A pulled payment for that customer should become a real RC Payment.
        var now = DateTime.UtcNow;
        SeedSkeleton(now);

        var conn = SeedConnectedConnection();

        // Confirmed Customer "QBC-1" → Tenant 30 (drives the create; AC-6).
        _ctx.Db.AccountingEntityMappings.Add(new AccountingEntityMapping
        {
            PortfolioId = PortfolioId,
            AccountingConnectionId = conn.Id,
            ExternalType = ExternalKind.Customer,
            ExternalId = "QBC-1",
            ExternalDisplayName = "Marcus Williams",
            LocalEntityType = LocalEntityKind.Tenant,
            LocalEntityId = 30,
            ConfirmedAt = now,
            Confidence = 1.0m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();

        var paidOn = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var fake = new FakeAccountingProvider(AccountingProvider.QuickBooks)
        {
            Payments =
            {
                // Mapped customer → becomes a real Payment.
                new ExtPaymentDto("QBP-1", "QBC-1", 1200m, paidOn, "Check", "1001", paidOn, null, "{}"),
                // Unmapped customer → review queue (AC-6 / D-8).
                new ExtPaymentDto("QBP-2", "QBC-UNKNOWN", 500m, paidOn, "Cash", "1002", paidOn, null, "{}"),
            },
        };

        var sut = CreateService(fake);

        // --- Act: first import.
        var summary = await sut.ImportAsync(conn, since: null, CancellationToken.None);

        // --- Assert: one Payment created with the right shape (AC-2).
        summary.PaymentsImported.Should().Be(1);
        summary.NeedsReview.Should().Be(1);

        var payment = await _ctx.Db.Payments.AsNoTracking().SingleAsync(p => p.PortfolioId == PortfolioId);
        payment.LeaseId.Should().Be(100);                       // the tenant's active lease
        payment.Amount.Should().Be(1200m);
        payment.Status.Should().Be(PaymentStatus.Paid);
        payment.PaymentType.Should().Be(PaymentType.Rent);      // default tenant-mapped money-in (D-9)
        payment.PaidDate.Should().Be(paidOn);
        payment.ExternalReference.Should().Be("1001");

        // Ledger: one Imported row (mapped), one Unmatched row (unmapped) — nothing dropped (AC-6).
        var ledger = await _ctx.Db.AccountingSyncMaps.AsNoTracking()
            .Where(m => m.AccountingConnectionId == conn.Id)
            .ToListAsync();
        ledger.Should().HaveCount(2);
        ledger.Single(m => m.ExternalId == "QBP-1").Status.Should().Be(LedgerStatus.Imported);
        ledger.Single(m => m.ExternalId == "QBP-1").LocalEntityId.Should().Be(payment.Id);
        ledger.Single(m => m.ExternalId == "QBP-2").Status.Should().Be(LedgerStatus.Unmatched);

        // --- Act: re-import the SAME pull. AC-5: the ledger must gate re-creation.
        var reConn = await _ctx.Db.AccountingConnections.SingleAsync(c => c.Id == conn.Id);
        var summary2 = await sut.ImportAsync(reConn, since: null, CancellationToken.None);

        // --- Assert: no second Payment, no second ledger rows.
        summary2.PaymentsImported.Should().Be(0);
        (await _ctx.Db.Payments.AsNoTracking().CountAsync(p => p.PortfolioId == PortfolioId))
            .Should().Be(1, "re-importing the same transaction must never create a second RC row");
        (await _ctx.Db.AccountingSyncMaps.AsNoTracking().CountAsync(m => m.AccountingConnectionId == conn.Id))
            .Should().Be(2, "the unique ledger index gates re-import; no duplicate ledger rows");
    }

    [Fact]
    public async Task Mapping_AutoConfirms_OnUnambiguousNameMatch()
    {
        // A single tenant whose name exactly contains the external customer name → NameMatcher 1.0,
        // a single viable candidate → auto-link (ConfirmedAt set).
        var now = DateTime.UtcNow;
        var conn = SeedConnectedConnection();
        _ctx.Db.Tenants.Add(new Tenant
        {
            Id = 501, PortfolioId = PortfolioId, FirstName = "Jordan", LastName = "Rivera",
            CreatedAt = now, UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateService(new FakeAccountingProvider(AccountingProvider.QuickBooks)
        {
            Customers = { new ExtCustomerDto("QBC-A", "Jordan Rivera", true, now, null, null, "{}") },
        });

        await sut.ImportAsync(conn, since: null, CancellationToken.None);

        var mapping = await _ctx.Db.AccountingEntityMappings.AsNoTracking()
            .SingleAsync(m => m.ExternalType == ExternalKind.Customer && m.ExternalId == "QBC-A");
        mapping.LocalEntityId.Should().Be(501);
        mapping.ConfirmedAt.Should().NotBeNull("a single clear name match is unambiguous → auto-linked");
    }

    [Fact]
    public async Task Mapping_StaysSuggestion_WhenTwoRivalsAreEquallyStrong()
    {
        // Two tenants with the SAME name as the external customer → both score 1.0, a tie within the
        // rival margin → ambiguous, so the mapping is surfaced as a suggestion (ConfirmedAt null).
        var now = DateTime.UtcNow;
        var conn = SeedConnectedConnection();
        _ctx.Db.Tenants.AddRange(
            new Tenant { Id = 601, PortfolioId = PortfolioId, FirstName = "Sam", LastName = "Lee", CreatedAt = now, UpdatedAt = now },
            new Tenant { Id = 602, PortfolioId = PortfolioId, FirstName = "Sam", LastName = "Lee", CreatedAt = now, UpdatedAt = now });
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateService(new FakeAccountingProvider(AccountingProvider.QuickBooks)
        {
            Customers = { new ExtCustomerDto("QBC-B", "Sam Lee", true, now, null, null, "{}") },
        });

        await sut.ImportAsync(conn, since: null, CancellationToken.None);

        var mapping = await _ctx.Db.AccountingEntityMappings.AsNoTracking()
            .SingleAsync(m => m.ExternalType == ExternalKind.Customer && m.ExternalId == "QBC-B");
        mapping.LocalEntityId.Should().NotBeNull("the best candidate is still recorded for the landlord to confirm");
        mapping.ConfirmedAt.Should().BeNull("two equally-strong rivals are ambiguous → never auto-linked");
    }

    // ----------------------------------------------------------------------------------

    private void SeedSkeleton(DateTime now)
    {
        _ctx.Db.Properties.Add(new Property
        {
            Id = 10, PortfolioId = PortfolioId, Name = "Maple Court",
            AddressLine1 = "10 Maple Ct", City = "Columbus", State = "OH", PostalCode = "43215",
            CreatedAt = now, UpdatedAt = now,
        });
        _ctx.Db.Units.Add(new Unit
        {
            Id = 20, PropertyId = 10, UnitNumber = "1", Bedrooms = 2, Bathrooms = 1, MarketRent = 1200m,
            CreatedAt = now, UpdatedAt = now,
        });
        _ctx.Db.Tenants.Add(new Tenant
        {
            Id = 30, PortfolioId = PortfolioId, FirstName = "Marcus", LastName = "Williams",
            CreatedAt = now, UpdatedAt = now,
        });
        _ctx.Db.Leases.Add(new Lease
        {
            Id = 100, PortfolioId = PortfolioId, PropertyId = 10, UnitId = 20, TenantId = 30,
            LeaseNumber = "L-1", Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-2), EndDate = now.AddMonths(10),
            MonthlyRent = 1200m, RentDueDay = 1, CreatedAt = now, UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private AccountingConnection SeedConnectedConnection()
    {
        var protector = _dp.CreateProtector("RentalCommand.Accounting.v1");
        var conn = new AccountingConnection
        {
            PortfolioId = PortfolioId,
            Provider = AccountingProvider.QuickBooks,
            Status = AccountingConnectionStatus.Connected,
            ExternalAccountId = Realm,
            AccessTokenCipherText = protector.Protect("access-token"),
            RefreshTokenCipherText = protector.Protect("refresh-token"),
            TokenExpiresAt = DateTime.UtcNow.AddHours(1),
            PullEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.AccountingConnections.Add(conn);
        _ctx.Db.SaveChanges();
        return conn;
    }

    private AccountingImportService CreateService(IAccountingProvider provider)
    {
        var qbOptions = new QuickBooksOptions
        {
            ClientId = "id", ClientSecret = "secret", Environment = "sandbox",
        };
        var settingsResolver = new AccountingAppSettingsResolver(new StaticOptionsMonitor<QuickBooksOptions>(qbOptions));
        var providerResolver = new AccountingProviderResolver(new[] { provider });
        return new AccountingImportService(
            _ctx.Db, _dp, providerResolver, settingsResolver,
            NullLogger<AccountingImportService>.Instance);
    }

    /// <summary>Configurable fake provider — returns the seeded pull DTOs; push/auth are unused here.</summary>
    private sealed class FakeAccountingProvider : IAccountingProvider
    {
        public FakeAccountingProvider(AccountingProvider provider) => Provider = provider;

        public AccountingProvider Provider { get; }

        public List<ExtCustomerDto> Customers { get; } = new();
        public List<ExtVendorDto> Vendors { get; } = new();
        public List<ExtAccountDto> Accounts { get; } = new();
        public List<ExtPaymentDto> Payments { get; } = new();
        public List<ExtExpenseDto> Expenses { get; } = new();

        public AccountingCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, true);

        public string BuildAuthorizeUrl(AccountingAppSettings s, string redirectUri, string state, string? codeChallenge)
            => "https://provider.test/authorize";

        public Task<AccountingTokenResult> ExchangeCodeAsync(AccountingAppSettings s, AccountingCallback cb, CancellationToken ct)
            => Task.FromResult(new AccountingTokenResult("a", "r", DateTime.UtcNow.AddHours(1), Realm, null));

        public Task<AccountingTokenResult> RefreshTokenAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct)
            => Task.FromResult(new AccountingTokenResult("a", "r", DateTime.UtcNow.AddHours(1), null, null));

        public Task RevokeAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct) => Task.CompletedTask;

        public Task<AccountingPullResult<ExtCustomerDto>> PullCustomersAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtCustomerDto>(Customers, Newest(Customers.Select(c => c.UpdatedAtUtc)), false));

        public Task<AccountingPullResult<ExtVendorDto>> PullVendorsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtVendorDto>(Vendors, Newest(Vendors.Select(v => v.UpdatedAtUtc)), false));

        public Task<AccountingPullResult<ExtAccountDto>> PullAccountsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtAccountDto>(Accounts, Newest(Accounts.Select(a => a.UpdatedAtUtc)), false));

        public Task<AccountingPullResult<ExtPaymentDto>> PullPaymentsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtPaymentDto>(Payments, Newest(Payments.Select(p => p.UpdatedAtUtc)), false));

        public Task<AccountingPullResult<ExtExpenseDto>> PullExpensesAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtExpenseDto>(Expenses, Newest(Expenses.Select(e => e.UpdatedAtUtc)), false));

        public Task<AcctPushResult> UpsertIncomeAsync(AcctCallCtx ctx, AcctIncomeDoc doc, CancellationToken ct)
            => Task.FromResult(new AcctPushResult(AcctPushOutcome.Created, "1"));

        public Task<AcctPushResult> UpsertExpenseAsync(AcctCallCtx ctx, AcctExpenseDoc doc, CancellationToken ct)
            => Task.FromResult(new AcctPushResult(AcctPushOutcome.Created, "1"));

        private static DateTime? Newest(IEnumerable<DateTime?> values)
        {
            var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return present.Count == 0 ? null : present.Max();
        }
    }

    /// <summary>Tiny IOptionsMonitor over a fixed value (mirrors the Phase 1 test helper).</summary>
    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
