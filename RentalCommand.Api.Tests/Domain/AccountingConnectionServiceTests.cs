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
/// Phase 1 backbone guard: the encrypted-token round trip (AC-4). Connecting through
/// a fake provider must persist the OAuth tokens ONLY as cipher text — recoverable to
/// the original value, never stored as plaintext — and disconnect must blank them.
/// Deliberately the single focused unit test for Phase 1; the system is in flux and
/// the sandbox flow is the real signal.
/// </summary>
public class AccountingConnectionServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CompleteCallback_PersistsTokensAsCipherText_AndRoundTrips()
    {
        const string access = "qbo-access-token-PLAINTEXT-secret";
        const string refresh = "qbo-refresh-token-PLAINTEXT-secret";
        var provider = new FakeAccountingProvider(AccountingProvider.QuickBooks,
            new AccountingTokenResult(access, refresh, DateTime.UtcNow.AddHours(1), "realm-123", "Acme Books"));

        var sut = CreateService(provider);

        // Seed a state row (what StartConnect persists) so the callback can consume it.
        _ctx.Db.OAuthStates.Add(new OAuthState
        {
            PortfolioId = 1,
            Provider = AccountingProvider.QuickBooks,
            StateToken = "state-token-abc",
            RedirectUri = "https://localhost/api/v1/integrations/accounting/callback",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();

        var (portfolioId, resolvedProvider) = await sut.CompleteCallbackFromStateAsync(
            new AccountingCallback("auth-code", "state-token-abc", "realm-123", null),
            CancellationToken.None);

        portfolioId.Should().Be(1);
        resolvedProvider.Should().Be(AccountingProvider.QuickBooks);

        var conn = await _ctx.Db.AccountingConnections.AsNoTracking()
            .SingleAsync(c => c.PortfolioId == 1 && c.Provider == AccountingProvider.QuickBooks);

        conn.Status.Should().Be(AccountingConnectionStatus.Connected);
        conn.ExternalAccountId.Should().Be("realm-123");
        conn.CompanyName.Should().Be("Acme Books");

        // AC-4: at rest it is cipher text, NOT the plaintext token.
        conn.AccessTokenCipherText.Should().NotBeNullOrEmpty();
        conn.AccessTokenCipherText.Should().NotContain(access);
        conn.RefreshTokenCipherText.Should().NotBeNullOrEmpty();
        conn.RefreshTokenCipherText.Should().NotContain(refresh);

        // …and it decrypts back to the original (the ProtectNullable/UnprotectNullable round trip).
        var protector = _dp.CreateProtector("RentalCommand.Accounting.v1");
        protector.Unprotect(conn.AccessTokenCipherText!).Should().Be(access);
        protector.Unprotect(conn.RefreshTokenCipherText!).Should().Be(refresh);

        // The single-use state row was consumed.
        (await _ctx.Db.OAuthStates.CountAsync()).Should().Be(0);

        // Disconnect blanks the tokens.
        await sut.DisconnectAsync(1, AccountingProvider.QuickBooks, CancellationToken.None);
        var afterDisconnect = await _ctx.Db.AccountingConnections.AsNoTracking()
            .SingleAsync(c => c.PortfolioId == 1 && c.Provider == AccountingProvider.QuickBooks);
        afterDisconnect.Status.Should().Be(AccountingConnectionStatus.Disconnected);
        afterDisconnect.AccessTokenCipherText.Should().BeNull();
        afterDisconnect.RefreshTokenCipherText.Should().BeNull();
    }

    private AccountingConnectionService CreateService(params IAccountingProvider[] providers)
    {
        var qbOptions = Options.Create(new QuickBooksOptions
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            Environment = "sandbox",
        });
        var settingsResolver = new AccountingAppSettingsResolver(new StaticOptionsMonitor<QuickBooksOptions>(qbOptions.Value));
        var providerResolver = new AccountingProviderResolver(providers);
        var importService = new AccountingImportService(
            _ctx.Db, _dp, providerResolver, settingsResolver,
            NullLogger<AccountingImportService>.Instance);
        return new AccountingConnectionService(
            _ctx.Db, _dp, providerResolver, settingsResolver, importService,
            NullLogger<AccountingConnectionService>.Instance);
    }

    /// <summary>Minimal fake provider — only the auth surface the Phase 1 connection lifecycle calls.</summary>
    private sealed class FakeAccountingProvider : IAccountingProvider
    {
        private readonly AccountingTokenResult _token;

        public FakeAccountingProvider(AccountingProvider provider, AccountingTokenResult token)
        {
            Provider = provider;
            _token = token;
        }

        public AccountingProvider Provider { get; }

        public AccountingCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, true);

        public string BuildAuthorizeUrl(AccountingAppSettings s, string redirectUri, string state, string? codeChallenge)
            => $"https://provider.test/authorize?state={state}";

        public Task<AccountingTokenResult> ExchangeCodeAsync(AccountingAppSettings s, AccountingCallback cb, CancellationToken ct)
            => Task.FromResult(_token);

        public Task<AccountingTokenResult> RefreshTokenAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct)
            => Task.FromResult(_token);

        public Task RevokeAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct)
            => Task.CompletedTask;

        public Task<AccountingPullResult<ExtCustomerDto>> PullCustomersAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtCustomerDto>([], null, false));

        public Task<AccountingPullResult<ExtVendorDto>> PullVendorsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtVendorDto>([], null, false));

        public Task<AccountingPullResult<ExtAccountDto>> PullAccountsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtAccountDto>([], null, false));

        public Task<AccountingPullResult<ExtPaymentDto>> PullPaymentsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtPaymentDto>([], null, false));

        public Task<AccountingPullResult<ExtExpenseDto>> PullExpensesAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct)
            => Task.FromResult(new AccountingPullResult<ExtExpenseDto>([], null, false));

        public Task<AcctPushResult> UpsertIncomeAsync(AcctCallCtx ctx, AcctIncomeDoc doc, CancellationToken ct)
            => Task.FromResult(new AcctPushResult(AcctPushOutcome.Created, "income-1"));

        public Task<AcctPushResult> UpsertExpenseAsync(AcctCallCtx ctx, AcctExpenseDoc doc, CancellationToken ct)
            => Task.FromResult(new AcctPushResult(AcctPushOutcome.Created, "expense-1"));
    }

    /// <summary>Tiny IOptionsMonitor over a fixed value (no change notifications needed in the test).</summary>
    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
