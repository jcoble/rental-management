using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class BankingService : IBankingService
{
    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IPlaidBankingProvider _plaid;
    private readonly PlaidOptions _plaidOptions;

    public BankingService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        IPlaidBankingProvider plaid,
        IOptions<PlaidOptions> plaidOptions)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.Banking.v1");
        _plaid = plaid;
        _plaidOptions = plaidOptions.Value;
    }

    public async Task<BankingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default)
    {
        var connections = await _db.BankConnections
            .Where(c => c.PortfolioId == portfolioId)
            .OrderBy(c => c.InstitutionName)
            .ThenBy(c => c.AccountName)
            .ToListAsync(ct);

        var lastSyncedAt = await _db.BankConnections
            .Where(c => c.PortfolioId == portfolioId && c.LastSyncedAt != null)
            .MaxAsync(c => c.LastSyncedAt, ct);

        var transactions = await BaseTransactions(portfolioId)
            .OrderByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(10)
            .ToListAsync(ct);

        var unmatchedCount = await _db.BankTransactions
            .CountAsync(t => t.PortfolioId == portfolioId && t.MatchStatus == "Unmatched", ct);

        var suggestedMatchCount = await CountSuggestibleUnmatchedAsync(portfolioId, ct);

        var mappedTransactions = await MapTransactionsWithSuggestionsAsync(portfolioId, transactions, ct);

        return new BankingSummaryResponse
        {
            ConnectionCount = connections.Count,
            TransactionCount = await _db.BankTransactions.CountAsync(t => t.PortfolioId == portfolioId, ct),
            UnmatchedCount = unmatchedCount,
            SuggestedMatchCount = suggestedMatchCount,
            LastSyncedAt = lastSyncedAt,
            Connections = connections.Select(MapConnection).ToList(),
            RecentTransactions = mappedTransactions
        };
    }

    public async Task<IReadOnlyList<BankConnectionResponse>> ListConnectionsAsync(int portfolioId, CancellationToken ct = default)
    {
        return await _db.BankConnections
            .Where(c => c.PortfolioId == portfolioId)
            .OrderBy(c => c.InstitutionName)
            .ThenBy(c => c.AccountName)
            .Select(c => MapConnection(c))
            .ToListAsync(ct);
    }

    public Task<PlaidSettingsResponse> GetPlaidSettingsAsync(int portfolioId, CancellationToken ct = default)
    {
        return Task.FromResult(ToPlaidSettingsResponse());
    }

    public async Task<PlaidLinkTokenResponse> CreatePlaidLinkTokenAsync(
        int portfolioId,
        int userId,
        string? platform,
        CancellationToken ct = default)
    {
        var settings = ApplyLinkPlatform(await GetRuntimeSettingsAsync(portfolioId, ct), platform);
        if (!settings.Configured)
        {
            return new PlaidLinkTokenResponse
            {
                Configured = false,
                Message = "Plaid client id and secret must be configured in server secrets before creating a Link token."
            };
        }

        var result = await _plaid.CreateLinkTokenAsync(settings, portfolioId, userId, ct);
        return new PlaidLinkTokenResponse
        {
            LinkToken = result.LinkToken,
            Expiration = result.Expiration,
            RequestId = result.RequestId,
            Configured = true
        };
    }

    public async Task<BankConnectionResponse> ExchangePlaidPublicTokenAsync(
        int portfolioId,
        ExchangePlaidPublicTokenRequest request,
        CancellationToken ct = default)
    {
        var publicToken = Normalize(request.PublicToken)
            ?? throw new InvalidOperationException("A Plaid public token is required.");
        var accountId = Normalize(request.AccountId)
            ?? throw new InvalidOperationException("A Plaid account id is required.");

        var settings = await GetRuntimeSettingsAsync(portfolioId, ct);
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Plaid settings are not configured.");
        }

        var exchange = await _plaid.ExchangePublicTokenAsync(settings, publicToken, ct);
        var now = DateTime.UtcNow;
        var itemIdHash = ExternalLookupHash(exchange.ItemId);
        var accountIdHash = ExternalLookupHash(accountId);
        var connection = await _db.BankConnections
            .FirstOrDefaultAsync(c =>
                c.PortfolioId == portfolioId &&
                c.Provider == "Plaid" &&
                c.ExternalItemIdHash == itemIdHash &&
                c.ExternalAccountIdHash == accountIdHash,
                ct);

        if (connection == null)
        {
            connection = new BankConnection
            {
                PortfolioId = portfolioId,
                Provider = "Plaid",
                CreatedAt = now,
            };
            _db.BankConnections.Add(connection);
        }

        connection.InstitutionName = Normalize(request.InstitutionName) ?? "Plaid bank";
        connection.AccountName = Normalize(request.AccountName) ?? "Linked account";
        connection.AccountMask = Normalize(request.AccountMask);
        connection.AccountType = Normalize(request.AccountType);
        connection.AccountSubtype = Normalize(request.AccountSubtype);
        connection.ExternalItemIdCipherText = ProtectNullable(exchange.ItemId);
        connection.ExternalAccountIdCipherText = ProtectNullable(accountId);
        connection.ExternalItemIdHash = itemIdHash;
        connection.ExternalAccountIdHash = accountIdHash;
        connection.ExternalAccessTokenCipherText = ProtectNullable(exchange.AccessToken);
        connection.Status = "Active";
        connection.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await SyncPlaidConnectionAsync(portfolioId, connection.Id, ct);
        return MapConnection(connection);
    }

    public async Task<SyncBankConnectionResponse?> SyncPlaidConnectionAsync(
        int portfolioId,
        int connectionId,
        CancellationToken ct = default)
    {
        var connection = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.PortfolioId == portfolioId && c.Id == connectionId && c.Provider == "Plaid", ct);
        if (connection == null) return null;

        var accessToken = UnprotectNullable(connection.ExternalAccessTokenCipherText);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("This bank connection does not have a Plaid access token.");
        }

        var settings = await GetRuntimeSettingsAsync(portfolioId, ct);
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Plaid settings are not configured.");
        }

        var cursor = UnprotectNullable(connection.SyncCursorCipherText);
        var synced = await _plaid.SyncTransactionsAsync(settings, accessToken, cursor, ct);
        var now = DateTime.UtcNow;
        var linkedAccountId = UnprotectNullable(connection.ExternalAccountIdCipherText);
        var imported = new List<BankTransaction>();
        var modified = new List<BankTransaction>();
        var skipped = 0;
        var incomingIds = synced.Added
            .Concat(synced.Modified)
            .Where(t => linkedAccountId == null || t.AccountId == linkedAccountId)
            .Select(t => t.TransactionId.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingTransactions = await _db.BankTransactions
            .Where(t => t.BankConnectionId == connection.Id && incomingIds.Contains(t.ProviderTransactionId))
            .ToListAsync(ct);
        var existing = existingTransactions
            .ToDictionary(t => t.ProviderTransactionId, StringComparer.OrdinalIgnoreCase);

        foreach (var item in synced.Added.Where(t => linkedAccountId == null || t.AccountId == linkedAccountId))
        {
            var providerTransactionId = item.TransactionId.Trim();
            if (providerTransactionId.Length == 0 || existing.ContainsKey(providerTransactionId))
            {
                skipped++;
                continue;
            }

            var transaction = new BankTransaction
            {
                PortfolioId = portfolioId,
                BankConnectionId = connection.Id,
                ProviderTransactionId = providerTransactionId,
                PostedAt = item.PostedAt.ToUtc(),
                AuthorizedAt = item.AuthorizedAt.ToUtc(),
                Description = item.Description.Trim(),
                MerchantName = Normalize(item.MerchantName),
                Amount = -item.Amount,
                IsoCurrencyCode = string.IsNullOrWhiteSpace(item.IsoCurrencyCode) ? "USD" : item.IsoCurrencyCode.Trim(),
                Category = Normalize(item.Category),
                RawData = item.RawData,
                MatchStatus = "Unmatched",
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.BankTransactions.Add(transaction);
            existing[providerTransactionId] = transaction;
            imported.Add(transaction);
        }

        foreach (var item in synced.Modified.Where(t => linkedAccountId == null || t.AccountId == linkedAccountId))
        {
            var providerTransactionId = item.TransactionId.Trim();
            if (providerTransactionId.Length == 0 || !existing.TryGetValue(providerTransactionId, out var transaction))
            {
                skipped++;
                continue;
            }

            ApplyPlaidTransaction(transaction, item, now);
            modified.Add(transaction);
        }

        var removedIds = synced.RemovedTransactionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedIds.Count > 0)
        {
            var removedTransactions = await _db.BankTransactions
                .Where(t => t.BankConnectionId == connection.Id && removedIds.Contains(t.ProviderTransactionId))
                .ToListAsync(ct);
            foreach (var transaction in removedTransactions)
            {
                transaction.MatchedPaymentId = null;
                transaction.MatchedExpenseId = null;
                transaction.MatchStatus = "Removed";
                transaction.MatchConfidence = null;
                transaction.Notes = "Removed by Plaid sync.";
                transaction.UpdatedAt = now;
            }
        }

        connection.SyncCursorCipherText = ProtectNullable(synced.NextCursor);
        connection.LastSyncedAt = now;
        connection.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var mapped = await MapTransactionsWithSuggestionsAsync(portfolioId, imported.Concat(modified).ToList(), ct);
        return new SyncBankConnectionResponse
        {
            Connection = MapConnection(connection),
            ImportedCount = imported.Count,
            SkippedCount = skipped,
            Transactions = mapped
        };
    }

    public async Task<IReadOnlyList<BankTransactionResponse>> ListTransactionsAsync(
        int portfolioId,
        string? status,
        CancellationToken ct = default)
    {
        var query = BaseTransactions(portfolioId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.MatchStatus == status);
        }

        var transactions = await query
            .OrderByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(100)
            .ToListAsync(ct);

        return await MapTransactionsWithSuggestionsAsync(portfolioId, transactions, ct);
    }

    public async Task<ImportBankTransactionsResponse> ImportAsync(
        int portfolioId,
        ImportBankTransactionsRequest request,
        CancellationToken ct = default)
    {
        if (request.Transactions.Count == 0)
        {
            throw new InvalidOperationException("At least one bank transaction is required.");
        }

        var now = DateTime.UtcNow;
        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "Manual" : request.Provider.Trim();
        var institution = string.IsNullOrWhiteSpace(request.InstitutionName) ? "Imported bank" : request.InstitutionName.Trim();
        var account = string.IsNullOrWhiteSpace(request.AccountName) ? "Imported account" : request.AccountName.Trim();

        var connection = await _db.BankConnections
            .FirstOrDefaultAsync(c =>
                c.PortfolioId == portfolioId &&
                c.Provider == provider &&
                c.InstitutionName == institution &&
                c.AccountName == account &&
                c.AccountMask == request.AccountMask, ct);

        if (connection == null)
        {
            connection = new BankConnection
            {
                PortfolioId = portfolioId,
                Provider = provider,
                InstitutionName = institution,
                AccountName = account,
                AccountMask = request.AccountMask,
                AccountType = request.AccountType,
                AccountSubtype = request.AccountSubtype,
                Status = "Active",
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.BankConnections.Add(connection);
            await _db.SaveChangesAsync(ct);
        }

        var imported = new List<BankTransaction>();
        var skipped = 0;
        var incomingIds = request.Transactions
            .Select(t => t.ProviderTransactionId.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingIds = await _db.BankTransactions
            .Where(t => t.BankConnectionId == connection.Id && incomingIds.Contains(t.ProviderTransactionId))
            .Select(t => t.ProviderTransactionId)
            .ToListAsync(ct);
        var existing = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in request.Transactions)
        {
            var providerTransactionId = item.ProviderTransactionId.Trim();
            if (providerTransactionId.Length == 0 || existing.Contains(providerTransactionId))
            {
                skipped++;
                continue;
            }

            var transaction = new BankTransaction
            {
                PortfolioId = portfolioId,
                BankConnectionId = connection.Id,
                ProviderTransactionId = providerTransactionId,
                PostedAt = item.PostedAt.ToUtc(),
                AuthorizedAt = item.AuthorizedAt.ToUtc(),
                Description = item.Description.Trim(),
                MerchantName = item.MerchantName,
                Amount = item.Amount,
                IsoCurrencyCode = string.IsNullOrWhiteSpace(item.IsoCurrencyCode) ? "USD" : item.IsoCurrencyCode.Trim(),
                Category = item.Category,
                RawData = item.RawData,
                MatchStatus = "Unmatched",
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.BankTransactions.Add(transaction);
            imported.Add(transaction);
        }

        connection.LastSyncedAt = now;
        connection.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var mapped = await MapTransactionsWithSuggestionsAsync(portfolioId, imported, ct);

        return new ImportBankTransactionsResponse
        {
            Connection = MapConnection(connection),
            ImportedCount = imported.Count,
            SkippedCount = skipped,
            Transactions = mapped
        };
    }

    public async Task<BankTransactionResponse?> MatchAsync(
        int portfolioId,
        int transactionId,
        MatchBankTransactionRequest request,
        CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;

        var entityType = request.EntityType.Trim();
        if (entityType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
        {
            var exists = await _db.Payments.AnyAsync(p => p.PortfolioId == portfolioId && p.Id == request.EntityId, ct);
            if (!exists) return null;
            transaction.MatchedPaymentId = request.EntityId;
            transaction.MatchedExpenseId = null;
        }
        else if (entityType.Equals("Expense", StringComparison.OrdinalIgnoreCase))
        {
            var exists = await _db.Expenses.AnyAsync(e => e.PortfolioId == portfolioId && e.Id == request.EntityId, ct);
            if (!exists) return null;
            transaction.MatchedExpenseId = request.EntityId;
            transaction.MatchedPaymentId = null;
        }
        else
        {
            throw new InvalidOperationException("EntityType must be Payment or Expense.");
        }

        transaction.MatchStatus = "Matched";
        transaction.MatchConfidence = 1m;
        transaction.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct)).Single();
    }

    public async Task<BankTransactionResponse?> ClearMatchAsync(int portfolioId, int transactionId, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;

        transaction.MatchedPaymentId = null;
        transaction.MatchedExpenseId = null;
        transaction.MatchStatus = "Unmatched";
        transaction.MatchConfidence = null;
        transaction.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct)).Single();
    }

    public async Task<BankReviewQueueResponse> GetReviewQueueAsync(int portfolioId, CancellationToken ct = default)
    {
        // The review queue is every imported line that still needs a human decision: it is
        // Unmatched (not yet confirmed, dismissed, or removed) AND the matcher currently has a
        // payment/expense candidate for it. These are the lines at risk of double-counting a
        // scanned receipt against the bank deposit/withdrawal.
        var candidateQuery = SuggestibleUnmatchedTransactionsQuery(portfolioId);
        var count = await candidateQuery.CountAsync(ct);
        var transactions = await candidateQuery
            .OrderByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .ToListAsync(ct);

        var mapped = await MapTransactionsWithSuggestionsAsync(portfolioId, transactions, ct);

        var items = mapped
            .Select(t => new BankReviewQueueItemResponse
            {
                Transaction = t,
                Suggestion = t.SuggestedMatch!,
            })
            .ToList();

        return new BankReviewQueueResponse
        {
            Count = count,
            Items = items,
        };
    }

    public async Task<BankTransactionResponse?> ConfirmMatchAsync(
        int portfolioId,
        int transactionId,
        ConfirmBankMatchRequest request,
        CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;

        // Resolve the target. An explicit paymentId/expenseId wins; otherwise fall back to the
        // current suggestion so a one-tap "confirm" from the queue works without echoing the id.
        int? paymentId = request.PaymentId;
        int? expenseId = request.ExpenseId;

        if (paymentId.HasValue && expenseId.HasValue)
        {
            throw new InvalidOperationException("Provide either a paymentId or an expenseId, not both.");
        }

        if (paymentId is null && expenseId is null)
        {
            var suggestion = (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct))
                .Single().SuggestedMatch;
            if (suggestion == null) return null;
            if (suggestion.EntityType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                paymentId = suggestion.EntityId;
            else
                expenseId = suggestion.EntityId;
        }

        if (paymentId.HasValue)
        {
            var exists = await _db.Payments.AnyAsync(p => p.PortfolioId == portfolioId && p.Id == paymentId.Value, ct);
            if (!exists) return null;
            transaction.MatchedPaymentId = paymentId.Value;
            transaction.MatchedExpenseId = null;
        }
        else
        {
            var exists = await _db.Expenses.AnyAsync(e => e.PortfolioId == portfolioId && e.Id == expenseId!.Value, ct);
            if (!exists) return null;
            transaction.MatchedExpenseId = expenseId!.Value;
            transaction.MatchedPaymentId = null;
        }

        // "Matched" + the matched id is what accounting uses to treat the bank line and the recorded
        // payment/expense as the SAME money, so it is not counted twice.
        transaction.MatchStatus = "Matched";
        transaction.MatchConfidence = 1m;
        transaction.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct)).Single();
    }

    public async Task<BankTransactionResponse?> DismissMatchAsync(int portfolioId, int transactionId, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;

        // Reviewed and decided to be NOT a match. The line is real bank money that does not
        // correspond to a recorded payment/expense, so it leaves the queue but stays in the books
        // (accounting still counts dismissed, unlinked bank activity — only the link is cleared).
        transaction.MatchedPaymentId = null;
        transaction.MatchedExpenseId = null;
        transaction.MatchStatus = "Dismissed";
        transaction.MatchConfidence = null;
        transaction.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct)).Single();
    }

    public async Task<BankTransactionResponse?> IgnoreTransactionAsync(int portfolioId, int transactionId, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId)
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;

        // Personal / not-business money (Starbucks, Uber, an owner draw, etc.). We reuse the existing
        // "Removed" status because that is the one accounting already excludes everywhere
        // (MatchStatus != "Removed"), so an ignored line never inflates the business P&L. It also
        // leaves the Unmatched review queue, yet stays listable via ?status=Removed. The Notes marker
        // distinguishes a deliberate user "ignore" from a Plaid-sync removal.
        // TODO(no-migration): persist a per-merchant "auto-ignore" memory so future lines from the
        // same merchant are pre-suggested as ignore. That needs a new table (e.g. IgnoredMerchant),
        // which is out of scope for this migration-free wave.
        transaction.MatchedPaymentId = null;
        transaction.MatchedExpenseId = null;
        transaction.MatchStatus = "Removed";
        transaction.MatchConfidence = null;
        transaction.Notes = "Marked personal / ignored by the landlord.";
        transaction.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct)).Single();
    }

    private IQueryable<BankTransaction> BaseTransactions(int portfolioId)
    {
        return _db.BankTransactions
            .Include(t => t.BankConnection)
            .Where(t => t.PortfolioId == portfolioId);
    }

    /// <summary>
    /// Counts how many Unmatched bank lines across the WHOLE portfolio have at least one plausible
    /// payment/expense match — the "Suggestions" KPI. Computed entirely SQL-side as a single COUNT with
    /// a correlated EXISTS, so it neither caps at the recent-preview page nor loads/loops rows in memory.
    /// The EXISTS uses the suggester's hard gates: amount equal within a cent, and the candidate's cash
    /// date within ±7 days of the posting (the name-similarity refinement in the per-row suggester only
    /// ever *widens* this window, so this DB count is a tight, slightly-conservative equivalent).
    /// </summary>
    private Task<int> CountSuggestibleUnmatchedAsync(int portfolioId, CancellationToken ct)
    {
        return SuggestibleUnmatchedTransactionsQuery(portfolioId).CountAsync(ct);
    }

    private IQueryable<BankTransaction> SuggestibleUnmatchedTransactionsQuery(int portfolioId)
    {
        return BaseTransactions(portfolioId)
            .Where(t => t.MatchStatus == "Unmatched")
            .Where(t =>
                // Deposits (income) suggest against eligible recorded/expected payments.
                (t.Amount > 0 && _db.Payments.Any(p =>
                    p.PortfolioId == portfolioId &&
                    p.Status != PaymentStatus.Failed &&
                    p.Status != PaymentStatus.Refunded &&
                    p.Amount >= t.Amount - 0.01m && p.Amount <= t.Amount + 0.01m &&
                    (p.PaidDate ?? p.DueDate) >= t.PostedAt.AddDays(-7) &&
                    (p.PaidDate ?? p.DueDate) <= t.PostedAt.AddDays(7)))
                ||
                // Withdrawals (spend) suggest against expenses; expense amounts are stored positive while
                // the bank withdrawal is negative, so compare against the absolute amount.
                (t.Amount < 0 && _db.Expenses.Any(e =>
                    e.PortfolioId == portfolioId &&
                    e.Amount >= -t.Amount - 0.01m && e.Amount <= -t.Amount + 0.01m &&
                    (e.PaidAt ?? e.IncurredAt) >= t.PostedAt.AddDays(-7) &&
                    (e.PaidAt ?? e.IncurredAt) <= t.PostedAt.AddDays(7))));
    }

    private Task<PlaidRuntimeSettings> GetRuntimeSettingsAsync(int portfolioId, CancellationToken ct)
    {
        return Task.FromResult(new PlaidRuntimeSettings(
            NormalizeEnvironment(_plaidOptions.Environment),
            _plaidOptions.ClientId,
            _plaidOptions.Secret,
            Normalize(_plaidOptions.RedirectUri),
            Normalize(_plaidOptions.AndroidPackageName)));
    }

    private static PlaidRuntimeSettings ApplyLinkPlatform(PlaidRuntimeSettings settings, string? platform)
    {
        var normalized = Normalize(platform)?.ToLowerInvariant();
        return normalized is "android" or "mobile-android"
            ? settings with { RedirectUri = null }
            : settings with { AndroidPackageName = null };
    }

    private PlaidSettingsResponse ToPlaidSettingsResponse()
    {
        return new PlaidSettingsResponse
        {
            PlaidEnvironment = NormalizeEnvironment(_plaidOptions.Environment),
            Configured = _plaidOptions.Configured,
        };
    }

    private string? ProtectNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : _protector.Protect(value);

    private static string? ExternalLookupHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes);
    }

    private string? UnprotectNullable(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
            return null;

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string NormalizeEnvironment(string? value)
    {
        var normalized = Normalize(value)?.ToLowerInvariant();
        return normalized is "development" or "production" ? normalized : "sandbox";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ApplyPlaidTransaction(BankTransaction transaction, PlaidSyncedTransaction item, DateTime now)
    {
        transaction.PostedAt = item.PostedAt.ToUtc();
        transaction.AuthorizedAt = item.AuthorizedAt.ToUtc();
        transaction.Description = item.Description.Trim();
        transaction.MerchantName = Normalize(item.MerchantName);
        transaction.Amount = -item.Amount;
        transaction.IsoCurrencyCode = string.IsNullOrWhiteSpace(item.IsoCurrencyCode) ? "USD" : item.IsoCurrencyCode.Trim();
        transaction.Category = Normalize(item.Category);
        transaction.RawData = item.RawData;
        if (transaction.MatchStatus == "Matched")
        {
            transaction.MatchStatus = "Unmatched";
            transaction.MatchConfidence = null;
            transaction.MatchedPaymentId = null;
            transaction.MatchedExpenseId = null;
            transaction.Notes = "Plaid modified this transaction after it was matched; review the match again.";
        }
        transaction.UpdatedAt = now;
    }

    private async Task<IReadOnlyList<BankTransactionResponse>> MapTransactionsWithSuggestionsAsync(
        int portfolioId,
        IReadOnlyList<BankTransaction> transactions,
        CancellationToken ct)
    {
        if (transactions.Count == 0) return [];

        var unmatchedTransactionIds = transactions
            .Where(t => t.MatchStatus == "Unmatched")
            .Select(t => t.Id)
            .ToArray();

        var suggestionsByTransactionId = unmatchedTransactionIds.Length == 0
            ? new Dictionary<int, BankMatchSuggestionResponse>()
            : await LoadSqlRankedSuggestionsAsync(portfolioId, unmatchedTransactionIds, ct);

        return transactions
            .Select(t => MapTransaction(t, suggestionsByTransactionId.GetValueOrDefault(t.Id)))
            .ToList();
    }

    private async Task<Dictionary<int, BankMatchSuggestionResponse>> LoadSqlRankedSuggestionsAsync(
        int portfolioId,
        int[] transactionIds,
        CancellationToken ct)
    {
        var paymentCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from p in _db.Payments.AsNoTracking()
            let anchor = p.PaidDate ?? p.DueDate
            let bankText = ((t.MerchantName ?? "") + " " + t.Description).ToLower()
            let tenantName = (p.Lease!.Tenant!.FirstName + " " + p.Lease!.Tenant!.LastName).Trim().ToLower()
            let leaseNumber = p.Lease!.LeaseNumber.ToLower()
            let propertyName = p.Lease!.Property!.Name.ToLower()
            let hasNameMatch =
                (tenantName != "" && bankText.Contains(tenantName)) ||
                (leaseNumber != "" && bankText.Contains(leaseNumber)) ||
                (propertyName != "" && bankText.Contains(propertyName))
            let dateScore =
                anchor >= t.PostedAt.AddDays(-1) && anchor <= t.PostedAt.AddDays(1) ? 0.80m :
                anchor >= t.PostedAt.AddDays(-2) && anchor <= t.PostedAt.AddDays(2) ? 0.72m :
                anchor >= t.PostedAt.AddDays(-4) && anchor <= t.PostedAt.AddDays(4) ? 0.62m :
                anchor >= t.PostedAt.AddDays(-7) && anchor <= t.PostedAt.AddDays(7) ? 0.52m :
                anchor >= t.PostedAt.AddDays(-14) && anchor <= t.PostedAt.AddDays(14) && hasNameMatch ? 0.42m :
                0m
            where
                transactionIds.Contains(t.Id) &&
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                t.Amount > 0m &&
                p.PortfolioId == portfolioId &&
                p.Status != PaymentStatus.Failed &&
                p.Status != PaymentStatus.Refunded &&
                p.Amount >= t.Amount - 0.01m &&
                p.Amount <= t.Amount + 0.01m &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "Payment",
                EntityId = p.Id,
                Confidence = hasNameMatch ? dateScore + 0.19m : dateScore,
                Label = (p.Lease!.Tenant!.FirstName + " " + p.Lease!.Tenant!.LastName).Trim() + " rent payment",
                Reason = "Deposit amount and date line up with an expected or recorded payment.",
            };

        var expenseCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from e in _db.Expenses.AsNoTracking()
            let anchor = e.PaidAt ?? e.IncurredAt
            let bankText = ((t.MerchantName ?? "") + " " + t.Description).ToLower()
            let vendorName = e.Vendor != null ? e.Vendor.Name.ToLower() : ""
            let expenseDescription = e.Description.ToLower()
            let hasNameMatch =
                (vendorName != "" && bankText.Contains(vendorName)) ||
                (expenseDescription != "" && bankText.Contains(expenseDescription))
            let dateScore =
                anchor >= t.PostedAt.AddDays(-1) && anchor <= t.PostedAt.AddDays(1) ? 0.80m :
                anchor >= t.PostedAt.AddDays(-2) && anchor <= t.PostedAt.AddDays(2) ? 0.72m :
                anchor >= t.PostedAt.AddDays(-4) && anchor <= t.PostedAt.AddDays(4) ? 0.62m :
                anchor >= t.PostedAt.AddDays(-7) && anchor <= t.PostedAt.AddDays(7) ? 0.52m :
                anchor >= t.PostedAt.AddDays(-14) && anchor <= t.PostedAt.AddDays(14) && hasNameMatch ? 0.42m :
                0m
            where
                transactionIds.Contains(t.Id) &&
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                t.Amount < 0m &&
                e.PortfolioId == portfolioId &&
                e.Amount >= -t.Amount - 0.01m &&
                e.Amount <= -t.Amount + 0.01m &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "Expense",
                EntityId = e.Id,
                Confidence = hasNameMatch ? dateScore + 0.19m : dateScore,
                Label = e.Vendor == null ? e.Description : e.Vendor!.Name,
                Reason = "Withdrawal amount and date line up with an expense.",
            };

        var rows = await paymentCandidates
            .Concat(expenseCandidates)
            .GroupBy(c => c.TransactionId)
            .Select(g => g
                .OrderByDescending(c => c.Confidence)
                .ThenBy(c => c.EntityType)
                .ThenBy(c => c.EntityId)
                .First())
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.TransactionId,
            r => new BankMatchSuggestionResponse
            {
                EntityType = r.EntityType,
                EntityId = r.EntityId,
                Confidence = r.Confidence > 0.99m ? 0.99m : r.Confidence,
                Label = string.IsNullOrWhiteSpace(r.Label) ? r.EntityType.ToLowerInvariant() : r.Label,
                Reason = r.Reason,
            });
    }

    private static BankMatchSuggestionResponse? SuggestMatch(
        BankTransaction transaction,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<Expense> expenses)
    {
        // Only Unmatched lines are still open for review; Matched/Dismissed/Removed have a decision.
        if (transaction.MatchStatus != "Unmatched") return null;

        if (transaction.Amount > 0)
        {
            var payment = payments
                .Select(p => new { Payment = p, Score = ScorePayment(transaction, p) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (payment != null)
            {
                var tenant = payment.Payment.Lease?.Tenant;
                var tenantName = tenant == null ? "payment" : $"{tenant.FirstName} {tenant.LastName}".Trim();
                return new BankMatchSuggestionResponse
                {
                    EntityType = "Payment",
                    EntityId = payment.Payment.Id,
                    Confidence = payment.Score,
                    Label = $"{tenantName} rent payment",
                    Reason = "Deposit amount and date line up with an expected or recorded payment."
                };
            }
        }

        if (transaction.Amount < 0)
        {
            var expense = expenses
                .Select(e => new { Expense = e, Score = ScoreExpense(transaction, e) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (expense != null)
            {
                return new BankMatchSuggestionResponse
                {
                    EntityType = "Expense",
                    EntityId = expense.Expense.Id,
                    Confidence = expense.Score,
                    Label = expense.Expense.Vendor?.Name ?? expense.Expense.Description,
                    Reason = "Withdrawal amount and date line up with an expense."
                };
            }
        }

        return null;
    }

    // Amount is the hard gate; date proximity sets the base score; a merchant-name match against the
    // payment's tenant/lease/property (or the expense's vendor/description) nudges the score up so a
    // named candidate outranks an unnamed same-amount one. This only sharpens the SUGGESTION the user
    // confirms — nothing is auto-matched.
    private static decimal ScorePayment(BankTransaction transaction, Payment payment)
    {
        if (Math.Abs(transaction.Amount - payment.Amount) > 0.01m) return 0m;
        var anchor = payment.PaidDate ?? payment.DueDate;

        // Names a deposit's merchant line might carry: the tenant, the lease number, the property.
        var tenant = payment.Lease?.Tenant;
        var nameMatch = NameMatchStrength(
            transaction.MerchantName,
            transaction.Description,
            tenant == null ? null : $"{tenant.FirstName} {tenant.LastName}",
            payment.Lease?.LeaseNumber,
            payment.Lease?.Property?.Name);

        return CombineScore(transaction.PostedAt, anchor, nameMatch);
    }

    private static decimal ScoreExpense(BankTransaction transaction, Expense expense)
    {
        if (Math.Abs(Math.Abs(transaction.Amount) - expense.Amount) > 0.01m) return 0m;
        var anchor = expense.PaidAt ?? expense.IncurredAt;

        var nameMatch = NameMatchStrength(
            transaction.MerchantName,
            transaction.Description,
            expense.Vendor?.Name,
            expense.Description);

        return CombineScore(transaction.PostedAt, anchor, nameMatch);
    }

    /// <summary>
    /// Folds date proximity and merchant↔name similarity into a single confidence in (0,1].
    /// A name match both raises the score and slightly relaxes the date window (so a clearly-named
    /// line still suggests even if it posted a few days late); with no name signal the original
    /// date-only band and 7-day cutoff are preserved.
    /// </summary>
    private static decimal CombineScore(DateTime postedAt, DateTime anchor, decimal nameMatch)
    {
        var days = Math.Abs((postedAt.Date - anchor.Date).Days);

        // A strong name match buys a little extra date slack; otherwise keep the hard 7-day cutoff.
        var maxDays = nameMatch >= 0.6m ? 14 : 7;
        if (days > maxDays) return 0m;

        var dateScore = days switch
        {
            0 => 0.80m,
            <= 2 => 0.72m,
            <= 4 => 0.62m,
            <= 7 => 0.52m,
            _ => 0.42m
        };

        // Name match contributes up to +0.20; the base bands sit below the old 0.98/0.90/... so a
        // named match lands near the top and an unnamed match stays a notch lower.
        var score = dateScore + nameMatch * 0.20m;
        return Math.Min(score, 0.99m);
    }

    /// <summary>
    /// Strength in [0,1] that the bank line's merchant/description refers to one of the candidate
    /// names. Delegates to the shared <see cref="NameMatcher"/> (one engine, also used by the
    /// accounting import mapper) after combining the merchant + description into one external string.
    /// </summary>
    private static decimal NameMatchStrength(string? bankMerchant, string? bankDescription, params string?[] candidateNames)
        => NameMatcher.NameMatchStrength($"{bankMerchant} {bankDescription}", candidateNames);

    private sealed class BankSuggestionRankRow
    {
        public int TransactionId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public int EntityId { get; set; }
        public decimal Confidence { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    private static BankConnectionResponse MapConnection(BankConnection c) => new()
    {
        Id = c.Id,
        Provider = c.Provider,
        InstitutionName = c.InstitutionName,
        AccountName = c.AccountName,
        AccountMask = c.AccountMask,
        AccountType = c.AccountType,
        AccountSubtype = c.AccountSubtype,
        Status = c.Status,
        LastSyncedAt = c.LastSyncedAt
    };

    private static BankTransactionResponse MapTransaction(
        BankTransaction t,
        BankMatchSuggestionResponse? suggestion) => new()
    {
        Id = t.Id,
        BankConnectionId = t.BankConnectionId,
        InstitutionName = t.BankConnection?.InstitutionName ?? "",
        AccountName = t.BankConnection?.AccountName ?? "",
        ProviderTransactionId = t.ProviderTransactionId,
        PostedAt = t.PostedAt,
        AuthorizedAt = t.AuthorizedAt,
        Description = t.Description,
        MerchantName = t.MerchantName,
        Amount = t.Amount,
        IsoCurrencyCode = t.IsoCurrencyCode,
        Category = t.Category,
        MatchedPaymentId = t.MatchedPaymentId,
        MatchedExpenseId = t.MatchedExpenseId,
        MatchStatus = t.MatchStatus,
        MatchConfidence = t.MatchConfidence,
        Notes = t.Notes,
        SuggestedMatch = suggestion
    };
}
