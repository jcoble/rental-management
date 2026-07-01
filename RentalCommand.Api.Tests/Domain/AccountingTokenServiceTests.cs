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
/// AC-7 guard for the proactive token refresh: a near-expiry connection refreshes its tokens (BOTH
/// rotate; re-encrypted at rest, never plaintext; expiry advanced), and a dead refresh token
/// (<see cref="AccountingReconnectRequiredException"/>) flips the connection to <see cref="AccountingConnectionStatus.NeedsReconnect"/>
/// and blanks the tokens — never a silent failure. Tests the shared <see cref="AccountingTokenService"/>
/// directly (the worker is a thin DB-claim + advisory-lock wrapper around it).
/// </summary>
public sealed class AccountingTokenServiceTests : IDisposable
{
    private const int PortfolioId = 1; // seeded by SqliteTestContext

    private readonly SqliteTestContext _ctx = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task RefreshAsync_RotatesAndReencryptsBothTokens_AndAdvancesExpiry()
    {
        var protector = _dp.CreateProtector("RentalCommand.Accounting.v1");
        var nearExpiry = DateTime.UtcNow.AddMinutes(5);
        var conn = SeedConnection(protector, "old-access", "old-refresh", nearExpiry, AccountingConnectionStatus.Connected);

        var newExpiry = DateTime.UtcNow.AddHours(1);
        var fake = new FakeRefreshProvider(
            new AccountingTokenResult("new-access", "new-refresh", newExpiry, null, null));
        var sut = CreateService(fake);

        var result = await sut.RefreshAsync(_ctx.Db, conn, CancellationToken.None);

        result.Outcome.Should().Be(AccountingTokenService.RefreshOutcome.Refreshed);
        result.AccessToken.Should().Be("new-access"); // decrypted, for a 401-retry caller to reuse

        var saved = await _ctx.Db.AccountingConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        saved.Status.Should().Be(AccountingConnectionStatus.Connected);
        saved.TokenExpiresAt.Should().Be(newExpiry);

        // BOTH tokens rotated and are stored ONLY as cipher text (not plaintext), and decrypt to the new pair.
        saved.AccessTokenCipherText.Should().NotBeNullOrEmpty();
        saved.AccessTokenCipherText.Should().NotContain("new-access");
        saved.RefreshTokenCipherText.Should().NotBeNullOrEmpty();
        saved.RefreshTokenCipherText.Should().NotContain("new-refresh");
        protector.Unprotect(saved.AccessTokenCipherText!).Should().Be("new-access");
        protector.Unprotect(saved.RefreshTokenCipherText!).Should().Be("new-refresh");

        // The provider was handed the DECRYPTED old refresh token.
        fake.LastRefreshTokenSeen.Should().Be("old-refresh");
    }

    [Fact]
    public async Task RefreshAsync_OnInvalidGrant_FlipsToNeedsReconnect_AndBlanksTokens()
    {
        var protector = _dp.CreateProtector("RentalCommand.Accounting.v1");
        var conn = SeedConnection(protector, "a", "dead-refresh", DateTime.UtcNow.AddMinutes(5), AccountingConnectionStatus.Connected);

        var fake = new FakeRefreshProvider(
            throwReconnect: new AccountingReconnectRequiredException("invalid_grant — refresh token dead"));
        var sut = CreateService(fake);

        var result = await sut.RefreshAsync(_ctx.Db, conn, CancellationToken.None);

        result.Outcome.Should().Be(AccountingTokenService.RefreshOutcome.NeedsReconnect);
        result.AccessToken.Should().BeNull();

        var saved = await _ctx.Db.AccountingConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        saved.Status.Should().Be(AccountingConnectionStatus.NeedsReconnect);
        saved.AccessTokenCipherText.Should().BeNull();
        saved.RefreshTokenCipherText.Should().BeNull();
        saved.TokenExpiresAt.Should().BeNull();
        saved.LastError.Should().Contain("reconnect");
    }

    // ----------------------------------------------------------------------------------

    private AccountingConnection SeedConnection(
        IDataProtector protector, string access, string refresh, DateTime expiresAt, AccountingConnectionStatus status)
    {
        var conn = new AccountingConnection
        {
            PortfolioId = PortfolioId,
            Provider = AccountingProvider.QuickBooks,
            Status = status,
            ExternalAccountId = "realm-1",
            AccessTokenCipherText = protector.Protect(access),
            RefreshTokenCipherText = protector.Protect(refresh),
            TokenExpiresAt = expiresAt,
            PullEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.AccountingConnections.Add(conn);
        _ctx.Db.SaveChanges();
        return conn;
    }

    private AccountingTokenService CreateService(IAccountingProvider provider)
    {
        var qbOptions = new QuickBooksOptions { ClientId = "id", ClientSecret = "secret", Environment = "sandbox" };
        var settingsResolver = new AccountingAppSettingsResolver(new StaticOptionsMonitor<QuickBooksOptions>(qbOptions));
        var providerResolver = new AccountingProviderResolver(new[] { provider });
        return new AccountingTokenService(_dp, providerResolver, settingsResolver, TimeProvider.System, NullLogger<AccountingTokenService>.Instance);
    }

    /// <summary>Fake provider whose RefreshTokenAsync returns a fixed result or throws a reconnect-required.</summary>
    private sealed class FakeRefreshProvider : IAccountingProvider
    {
        private readonly AccountingTokenResult? _result;
        private readonly AccountingReconnectRequiredException? _throwReconnect;

        public FakeRefreshProvider(AccountingTokenResult result) => _result = result;
        public FakeRefreshProvider(AccountingReconnectRequiredException throwReconnect) => _throwReconnect = throwReconnect;

        public string? LastRefreshTokenSeen { get; private set; }

        public AccountingProvider Provider => AccountingProvider.QuickBooks;
        public AccountingCapabilities Capabilities { get; } = new(true, true, true, true, true, true, true);

        public Task<AccountingTokenResult> RefreshTokenAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct)
        {
            LastRefreshTokenSeen = refreshToken;
            if (_throwReconnect != null)
            {
                throw _throwReconnect;
            }

            return Task.FromResult(_result!);
        }

        // Unused by these tests.
        public string BuildAuthorizeUrl(AccountingAppSettings s, string redirectUri, string state, string? codeChallenge) => "x";
        public Task<AccountingTokenResult> ExchangeCodeAsync(AccountingAppSettings s, AccountingCallback cb, CancellationToken ct) => throw new NotImplementedException();
        public Task RevokeAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct) => Task.CompletedTask;
        public Task<AccountingPullResult<ExtCustomerDto>> PullCustomersAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountingPullResult<ExtVendorDto>> PullVendorsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountingPullResult<ExtAccountDto>> PullAccountsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountingPullResult<ExtPaymentDto>> PullPaymentsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountingPullResult<ExtExpenseDto>> PullExpensesAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct) => throw new NotImplementedException();
        public Task<AcctPushResult> UpsertIncomeAsync(AcctCallCtx ctx, AcctIncomeDoc doc, CancellationToken ct) => throw new NotImplementedException();
        public Task<AcctPushResult> UpsertExpenseAsync(AcctCallCtx ctx, AcctExpenseDoc doc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
