using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Remote-only accounting pull orchestrator. Every provider call and token refresh completes before
/// the bounded response is admitted to the receipt-backed PostgreSQL atomic apply command.
/// </summary>
public sealed class AccountingImportService
{
    internal const int MaxItemsPerResource = 2_000;

    private readonly IDataProtector _protector;
    private readonly AccountingProviderResolver _providerResolver;
    private readonly AccountingAppSettingsResolver _settingsResolver;
    private readonly AccountingTokenService _tokenService;
    private readonly IAccountingConnectionClaimStore _claims;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountingImportService> _logger;

    public AccountingImportService(
        IDataProtectionProvider dataProtection,
        AccountingProviderResolver providerResolver,
        AccountingAppSettingsResolver settingsResolver,
        AccountingTokenService tokenService,
        IAccountingConnectionClaimStore claims,
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        ILogger<AccountingImportService> logger)
    {
        _protector = dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        _providerResolver = providerResolver;
        _settingsResolver = settingsResolver;
        _tokenService = tokenService;
        _claims = claims;
        _atomic = atomic;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public sealed record ImportSummary(
        int CustomersMapped,
        int VendorsMapped,
        int AccountsMapped,
        int PaymentsImported,
        int ExpensesImported,
        int NeedsReview);

    public async Task<ImportSummary> ImportAsync(
        AccountingConnection connection,
        DateTime? since,
        CancellationToken ct,
        AccountingWorkerFence workerFence)
    {
        if (workerFence.Operation != AccountingWorkerOperation.Pull)
            throw new InvalidOperationException(
                "Accounting pulls must hold a durable pull claim before contacting the provider.");

        var provider = _providerResolver.Resolve(connection.Provider);
        var ctx = BuildCallContext(connection);
        var cursors = ParseCursors(connection.LastPulledAtJson);
        var caps = provider.Capabilities;

        var customerPull = caps.CanPullCustomers
            ? await SafePullAsync("customers", () => PullWithRefreshAsync(
                connection, c => provider.PullCustomersAsync(c, GetCursor(cursors, "customers", since), ct), ct))
            : null;
        var vendorPull = caps.CanPullVendors
            ? await SafePullAsync("vendors", () => PullWithRefreshAsync(
                connection, c => provider.PullVendorsAsync(c, GetCursor(cursors, "vendors", since), ct), ct))
            : null;
        var accountPull = caps.CanPullAccounts
            ? await SafePullAsync("accounts", () => PullWithRefreshAsync(
                connection, c => provider.PullAccountsAsync(c, GetCursor(cursors, "accounts", since), ct), ct))
            : null;
        var paymentPull = caps.CanPullPayments
            ? await SafePullAsync("payments", () => PullWithRefreshAsync(
                connection, c => provider.PullPaymentsAsync(c, GetCursor(cursors, "payments", since), ct), ct))
            : null;
        var expensePull = caps.CanPullExpenses
            ? await SafePullAsync("expenses", () => PullWithRefreshAsync(
                connection, c => provider.PullExpensesAsync(c, GetCursor(cursors, "expenses", since), ct), ct))
            : null;

        ValidateBounded("customers", customerPull);
        ValidateBounded("vendors", vendorPull);
        ValidateBounded("accounts", accountPull);
        ValidateBounded("payments", paymentPull);
        ValidateBounded("expenses", expensePull);

        SetCursorIfPulled(cursors, "customers", customerPull?.MaxUpdatedAtUtc);
        SetCursorIfPulled(cursors, "vendors", vendorPull?.MaxUpdatedAtUtc);
        SetCursorIfPulled(cursors, "accounts", accountPull?.MaxUpdatedAtUtc);
        SetCursorIfPulled(cursors, "payments", paymentPull?.MaxUpdatedAtUtc);
        SetCursorIfPulled(cursors, "expenses", expensePull?.MaxUpdatedAtUtc);

        var appliedAt = _timeProvider.UtcNow();
        var batch = new
        {
            connection.Id,
            Claim = workerFence.ClaimToken,
            Customers = customerPull?.Items ?? [],
            Vendors = vendorPull?.Items ?? [],
            Accounts = accountPull?.Items ?? [],
            Payments = paymentPull?.Items ?? [],
            Expenses = expensePull?.Items ?? [],
            Cursors = cursors,
        };
        var batchJson = JsonSerializer.Serialize(batch);
        var batchIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchJson)));
        var command = new ApplyAccountingPullResultCommand(
            connection.PortfolioId,
            connection.Id,
            workerFence.ClaimToken,
            batchIdentity,
            customerPull?.Items ?? [],
            vendorPull?.Items ?? [],
            accountPull?.Items ?? [],
            paymentPull?.Items ?? [],
            expensePull?.Items ?? [],
            JsonSerializer.Serialize(cursors),
            appliedAt);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "accounting.pull.apply",
                $"{connection.Id}:{workerFence.ClaimToken:N}:{batchIdentity}"),
            command,
            new AtomicJsonResultCodec<ApplyAccountingPullResult>("accounting.pull.apply.v1"),
            ct);
        var value = outcome.Value;
        return new ImportSummary(
            value.CustomersMapped,
            value.VendorsMapped,
            value.AccountsMapped,
            value.PaymentsImported,
            value.ExpensesImported,
            value.NeedsReview);

        async Task<T> PullWithRefreshAsync<T>(
            AccountingConnection conn,
            Func<AcctCallCtx, Task<T>> call,
            CancellationToken token)
        {
            try
            {
                return await call(ctx);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var rotation = await _claims.ClaimInlineTokenRotationAsync(
                    conn.Id,
                    workerFence.ClaimToken,
                    $"inline-pull:{Environment.MachineName}:{Guid.NewGuid():N}",
                    TimeSpan.FromMinutes(3),
                    token)
                    ?? throw new DbUpdateConcurrencyException(
                        "The accounting token is already rotating or the pull claim is stale.");
                var refresh = await _tokenService.RefreshAsync(rotation.Connection, rotation.Fence, token);
                if (refresh.Outcome != AccountingTokenService.RefreshOutcome.Refreshed || refresh.AccessToken is null)
                    throw new AccountingReconnectRequiredException(
                        $"Accounting access token for connection {conn.Id} could not be refreshed.", ex);

                ctx = ctx with { AccessToken = refresh.AccessToken };
                return await call(ctx);
            }
        }

        async Task<AccountingPullResult<T>?> SafePullAsync<T>(
            string resource,
            Func<Task<AccountingPullResult<T>>> pull)
        {
            try
            {
                return await pull();
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       and not DbUpdateConcurrencyException
                                       and not AccountingReconnectRequiredException)
            {
                _logger.LogError(ex,
                    "Accounting provider pull {Resource} failed for connection {ConnectionId}; other resources continue",
                    resource,
                    connection.Id);
                return null;
            }
        }
    }

    private static void ValidateBounded<T>(string resource, AccountingPullResult<T>? pull)
    {
        if (pull is null) return;
        if (pull.Items.Count > MaxItemsPerResource)
            throw new InvalidOperationException(
                $"Accounting provider returned {pull.Items.Count} {resource}; maximum is {MaxItemsPerResource}.");
        if (pull.MoreAvailable)
            throw new InvalidOperationException(
                $"Accounting provider reported an incomplete {resource} page; cursor cannot advance atomically.");
    }

    private AcctCallCtx BuildCallContext(AccountingConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.AccessTokenCipherText))
            throw new InvalidOperationException("Connection has no access token; reconnect required.");
        var realm = connection.ExternalAccountId
            ?? throw new InvalidOperationException("Connection has no external account id.");
        try
        {
            return new AcctCallCtx(
                realm,
                _protector.Unprotect(connection.AccessTokenCipherText),
                _settingsResolver.Resolve(connection.Provider).UseSandbox);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Stored access token could not be decrypted; reconnect required.", ex);
        }
    }

    private static Dictionary<string, DateTime?> ParseCursors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DateTime?>>(json)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static DateTime? GetCursor(
        IReadOnlyDictionary<string, DateTime?> cursors,
        string resource,
        DateTime? fallback) =>
        fallback ?? (cursors.TryGetValue(resource, out var cursor) ? cursor : null);

    private static void SetCursorIfPulled(
        IDictionary<string, DateTime?> cursors,
        string resource,
        DateTime? cursor)
    {
        if (cursor is not null) cursors[resource] = cursor;
    }
}
