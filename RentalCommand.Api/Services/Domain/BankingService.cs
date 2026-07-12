using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class BankingService : IBankingService
{
    private const int DefaultReviewQueueTake = 50;
    private const int MaxReviewQueueTake = 100;
    private const int MaxImportBatch = 500;
    private const int MaxSyncRetries = 3;
    private static readonly AtomicJsonResultCodec<ApplyPlaidConnectionResult> PlaidConnectionCodec =
        new("banking.plaid.connection.result.v1");
    private static readonly AtomicJsonResultCodec<PreparePlaidTokenExchangeResult> PlaidExchangePrepareCodec =
        new("banking.plaid.exchange.prepare.result.v1");
    private static readonly AtomicJsonResultCodec<AdmitPlaidTokenExchangeResult> PlaidExchangeAdmitCodec =
        new("banking.plaid.exchange.admit.result.v1");
    private static readonly AtomicJsonResultCodec<RecordPlaidTokenExchangeReceiptResult> PlaidExchangeReceiptCodec =
        new("banking.plaid.exchange.receipt.result.v1");
    private static readonly AtomicJsonResultCodec<ApplyPlaidSyncResult> PlaidSyncCodec =
        new("banking.plaid.sync.result.v1");
    private static readonly AtomicJsonResultCodec<ImportBankTransactionsResult> ImportCodec =
        new("banking.import.result.v1");
    private static readonly AtomicJsonResultCodec<ReconcileBankTransactionResult> ReconciliationCodec =
        new("banking.reconciliation.result.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IPlaidBankingProvider _plaid;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly PlaidOptions _plaidOptions;
    private readonly TimeProvider _timeProvider;

    public BankingService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        IPlaidBankingProvider plaid,
        IAtomicUnitOfWork atomic,
        IOptions<PlaidOptions> plaidOptions,
        TimeProvider timeProvider)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("RentalCommand.Banking.v1");
        _plaid = plaid;
        _atomic = atomic;
        _plaidOptions = plaidOptions.Value;
        _timeProvider = timeProvider;
    }

    public async Task<BankingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default)
    {
        var connectionsQuery = _db.BankConnections
            .AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId);

        var connectionCount = await connectionsQuery.CountAsync(ct);

        var connections = await connectionsQuery
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
            ConnectionCount = connectionCount,
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
        var clientOperationId = Normalize(request.ClientOperationId)
            ?? throw new InvalidOperationException("A stable client operation id is required for Plaid token exchange.");
        if (clientOperationId.Length > 160)
            throw new InvalidOperationException("Plaid ClientOperationId cannot exceed 160 characters.");
        var accountId = Normalize(request.AccountId)
            ?? throw new InvalidOperationException("A Plaid account id is required.");

        var settings = await GetRuntimeSettingsAsync(portfolioId, ct);
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Plaid settings are not configured.");
        }

        var accountIdHash = ExternalLookupHash(accountId)!;
        var now = _timeProvider.UtcNow();
        var publicTokenHash = ExternalLookupHash(publicToken)!;
        var requestHash = Digest(
            portfolioId,
            publicTokenHash,
            accountIdHash,
            Normalize(request.InstitutionName),
            Normalize(request.AccountName),
            Normalize(request.AccountMask),
            Normalize(request.AccountType),
            Normalize(request.AccountSubtype));
        var prepared = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "banking.plaid.exchange.prepare",
                $"{portfolioId}:{Digest(clientOperationId)}"),
            new PreparePlaidTokenExchangeCommand(
                portfolioId,
                clientOperationId,
                requestHash,
                publicTokenHash,
                Normalize(request.InstitutionName) ?? "Plaid bank",
                Normalize(request.AccountName) ?? "Linked account",
                Normalize(request.AccountMask),
                Normalize(request.AccountType),
                Normalize(request.AccountSubtype),
                ProtectNullable(accountId)!,
                accountIdHash,
                now),
            PlaidExchangePrepareCodec,
            ct);
        var exchangeAttempt = await _db.PlaidTokenExchangeAttempts.AsNoTracking()
            .SingleAsync(row => row.Id == prepared.Value.ExchangeAttemptId
                && row.PortfolioId == portfolioId, ct);
        if (!string.Equals(exchangeAttempt.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("This Plaid operation id is already bound to a different request.");

        if (exchangeAttempt.CompletedAtUtc is null && exchangeAttempt.RemoteReceiptRecordedAtUtc is null)
        {
            if (exchangeAttempt.RemoteAdmittedAtUtc is not null)
            {
                throw new InvalidOperationException(
                    "Plaid token exchange was admitted previously but no local receipt is available. " +
                    "The single-use public token will not be exchanged again; support must reconcile this attempt.");
            }
            var admitted = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "banking.plaid.exchange.admit",
                    $"{portfolioId}:{exchangeAttempt.Id:N}"),
                new AdmitPlaidTokenExchangeCommand(portfolioId, exchangeAttempt.Id, _timeProvider.UtcNow()),
                PlaidExchangeAdmitCodec,
                ct);
            if (admitted.Value.Outcome != AdmitPlaidTokenExchangeOutcome.Admitted
                || admitted.Disposition != AtomicCommandDisposition.Executed)
            {
                throw new InvalidOperationException(
                    "Plaid token exchange is already owned by an admitted request. " +
                    "The single-use public token will not be exchanged concurrently.");
            }

            // Plaid I/O is deliberately outside every local database transaction. The durable
            // admission above makes an unknown remote outcome visible and prevents blind reuse.
            var remote = await _plaid.ExchangePublicTokenAsync(settings, publicToken, ct);
            var itemId = Normalize(remote.ItemId)
                ?? throw new InvalidOperationException("Plaid returned an empty item id.");
            var accessToken = Normalize(remote.AccessToken)
                ?? throw new InvalidOperationException("Plaid returned an empty access token.");
            var providerIdentity = Normalize(remote.RequestId)
                ?? $"fallback-{Digest(itemId, accountId, accessToken)}";
            await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "banking.plaid.exchange.receipt",
                    $"{portfolioId}:{exchangeAttempt.Id:N}"),
                new RecordPlaidTokenExchangeReceiptCommand(
                    portfolioId,
                    exchangeAttempt.Id,
                    providerIdentity,
                    ProtectNullable(itemId)!,
                    ExternalLookupHash(itemId)!,
                    ProtectNullable(accessToken)!,
                    _timeProvider.UtcNow()),
                PlaidExchangeReceiptCodec,
                ct);
        }

        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "banking.plaid.connection.apply",
                $"{portfolioId}:{exchangeAttempt.Id:N}"),
            new ApplyPlaidConnectionCommand(portfolioId, exchangeAttempt.Id, _timeProvider.UtcNow()),
            PlaidConnectionCodec,
            ct);

        if (exchangeAttempt.CompletedAtUtc is null)
            await SyncPlaidConnectionAsync(portfolioId, outcome.Value.ConnectionId, ct);
        return await _db.BankConnections.AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId && row.Id == outcome.Value.ConnectionId)
            .Select(row => MapConnection(row))
            .SingleAsync(ct);
    }

    public async Task<SyncBankConnectionResponse?> SyncPlaidConnectionAsync(
        int portfolioId,
        int connectionId,
        CancellationToken ct = default)
    {
        var settings = await GetRuntimeSettingsAsync(portfolioId, ct);
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Plaid settings are not configured.");
        }
        for (var retry = 0; retry < MaxSyncRetries; retry++)
        {
            var connection = await _db.BankConnections.AsNoTracking()
                .SingleOrDefaultAsync(row => row.PortfolioId == portfolioId
                    && row.Id == connectionId
                    && row.Provider == "Plaid", ct);
            if (connection is null) return null;
            var accessToken = UnprotectNullable(connection.ExternalAccessTokenCipherText);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("This bank connection does not have a Plaid access token.");
            }

            var cursor = UnprotectNullable(connection.SyncCursorCipherText);
            var synced = await _plaid.SyncTransactionsAsync(settings, accessToken, cursor, ct);
            var linkedAccountId = UnprotectNullable(connection.ExternalAccountIdCipherText);
            var added = NormalizePlaidInputs(synced.Added, linkedAccountId);
            var modified = NormalizePlaidInputs(synced.Modified, linkedAccountId);
            var removed = synced.RemovedTransactionIds
                .Select(Normalize)
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxImportBatch)
                .ToArray();
            var providerIdentity = Normalize(synced.RequestId)
                ?? $"fallback-{Digest(connectionId, cursor, synced.NextCursor, JsonSerializer.Serialize(added), JsonSerializer.Serialize(modified), JsonSerializer.Serialize(removed))}";
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("banking.plaid.sync.apply", $"{portfolioId}:{connectionId}:{Digest(providerIdentity)}"),
                new ApplyPlaidSyncCommand(
                    portfolioId,
                    connectionId,
                    connection.SyncCursorCipherText,
                    ProtectNullable(synced.NextCursor),
                    added.Items,
                    added.InputCount,
                    modified.Items,
                    modified.InputCount,
                    removed,
                    providerIdentity,
                    _timeProvider.UtcNow()),
                PlaidSyncCodec,
                ct);
            if (outcome.Value.Outcome == ApplyPlaidSyncOutcome.StaleCursor) continue;
            if (outcome.Value.Outcome == ApplyPlaidSyncOutcome.ConnectionNotFound) return null;

            var canonicalConnection = await _db.BankConnections.AsNoTracking()
                .SingleAsync(row => row.PortfolioId == portfolioId && row.Id == connectionId, ct);
            var affected = outcome.Value.AffectedTransactionIds.Count == 0
                ? new List<BankTransaction>()
                : await BaseTransactions(portfolioId).AsNoTracking()
                    .Where(row => outcome.Value.AffectedTransactionIds.Contains(row.Id))
                    .OrderBy(row => row.Id)
                    .ToListAsync(ct);
            return new SyncBankConnectionResponse
            {
                Connection = MapConnection(canonicalConnection),
                ImportedCount = outcome.Value.ImportedCount,
                SkippedCount = outcome.Value.SkippedCount,
                Transactions = await MapTransactionsWithSuggestionsAsync(portfolioId, affected, ct),
            };
        }

        throw new InvalidOperationException("The bank connection changed repeatedly while applying the Plaid sync result. Retry the sync.");
    }

    public async Task<BankTransactionListResponse> ListTransactionsAsync(
        int portfolioId,
        string? status,
        int skip = 0,
        int take = ListQuery.DefaultTake,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = take <= 0 ? ListQuery.DefaultTake : Math.Min(take, ListQuery.MaxTake);
        var query = BaseTransactions(portfolioId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.MatchStatus == status);
        }

        var totalCount = await query.CountAsync(ct);
        var transactions = await query
            .OrderByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new BankTransactionListResponse
        {
            TotalCount = totalCount,
            Skip = skip,
            Take = take,
            Items = await MapTransactionsWithSuggestionsAsync(portfolioId, transactions, ct),
        };
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
        if (request.Transactions.Count > MaxImportBatch)
        {
            throw new InvalidOperationException($"A bank import cannot contain more than {MaxImportBatch} transactions.");
        }

        var now = _timeProvider.UtcNow();
        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "Manual" : request.Provider.Trim();
        var institution = string.IsNullOrWhiteSpace(request.InstitutionName) ? "Imported bank" : request.InstitutionName.Trim();
        var account = string.IsNullOrWhiteSpace(request.AccountName) ? "Imported account" : request.AccountName.Trim();
        var inputs = request.Transactions
            .Select(ToInput)
            .Where(input => input.ProviderTransactionId.Length > 0)
            .GroupBy(input => input.ProviderTransactionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var requestIdentity = Digest(
            portfolioId,
            provider,
            institution,
            account,
            Normalize(request.AccountMask),
            request.Transactions.Count,
            JsonSerializer.Serialize(inputs));
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.import.apply", $"{portfolioId}:{requestIdentity}"),
            new ImportBankTransactionsCommand(
                portfolioId,
                provider,
                institution,
                account,
                Normalize(request.AccountMask),
                Normalize(request.AccountType),
                Normalize(request.AccountSubtype),
                inputs,
                request.Transactions.Count,
                requestIdentity,
                now),
            ImportCodec,
            ct);

        var connection = await _db.BankConnections.AsNoTracking()
            .SingleAsync(row => row.PortfolioId == portfolioId && row.Id == outcome.Value.ConnectionId, ct);
        var imported = outcome.Value.ImportedTransactionIds.Count == 0
            ? new List<BankTransaction>()
            : await BaseTransactions(portfolioId).AsNoTracking()
                .Where(row => outcome.Value.ImportedTransactionIds.Contains(row.Id))
                .OrderBy(row => row.Id)
                .ToListAsync(ct);

        return new ImportBankTransactionsResponse
        {
            Connection = MapConnection(connection),
            ImportedCount = outcome.Value.ImportedCount,
            SkippedCount = outcome.Value.SkippedCount,
            Transactions = await MapTransactionsWithSuggestionsAsync(portfolioId, imported, ct),
        };
    }

    public async Task<BankTransactionResponse?> MatchAsync(
        int portfolioId,
        int transactionId,
        MatchBankTransactionRequest request,
        CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        ValidateMatchTarget(request.TenantAccountId, request.TenantLedgerEntryId, request.ExpenseId);
        return await ReconcileAsync(
            portfolioId,
            transaction,
            request.ExpenseId.HasValue ? BankReconciliationAction.MatchExpense : BankReconciliationAction.MatchReceipt,
            request.TenantAccountId,
            request.TenantLedgerEntryId,
            request.ExpenseId,
            ct);
    }

    public async Task<BankTransactionResponse?> ClearMatchAsync(int portfolioId, int transactionId, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        return await ReconcileAsync(portfolioId, transaction, BankReconciliationAction.Clear, null, null, null, ct);
    }

    public async Task<BankReviewQueueResponse> GetReviewQueueAsync(
        int portfolioId,
        int skip = 0,
        int take = DefaultReviewQueueTake,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = take <= 0 ? DefaultReviewQueueTake : Math.Min(take, MaxReviewQueueTake);

        // The review queue is every imported line that still needs a human decision: it is
        // Unmatched (not yet confirmed, dismissed, or removed) AND the matcher currently has a
        // payment/expense candidate for it. These are the lines at risk of double-counting a
        // scanned receipt against the bank deposit/withdrawal.
        var candidateQuery = SuggestibleUnmatchedTransactionsQuery(portfolioId);
        var count = await candidateQuery.CountAsync(ct);
        var transactions = await candidateQuery
            .OrderByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(take)
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
            Skip = skip,
            Take = take,
            Items = items,
        };
    }

    public async Task<BankTransactionResponse?> ConfirmMatchAsync(
        int portfolioId,
        int transactionId,
        ConfirmBankMatchRequest request,
        CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        // An explicit canonical receipt identity or expense wins; otherwise use the current
        // SQL-ranked suggestion so one-tap confirmation does not need to echo identifiers.
        int? tenantAccountId = request.TenantAccountId;
        long? tenantLedgerEntryId = request.TenantLedgerEntryId;
        int? expenseId = request.ExpenseId;

        ValidateOptionalMatchTarget(tenantAccountId, tenantLedgerEntryId, expenseId);

        if (tenantLedgerEntryId is null && expenseId is null)
        {
            var suggestion = (await MapTransactionsWithSuggestionsAsync(portfolioId, [transaction], ct))
                .Single().SuggestedMatch;
            if (suggestion == null) return null;
            if (suggestion.EntityType.Equals("TenantLedgerEntry", StringComparison.OrdinalIgnoreCase))
            {
                tenantAccountId = suggestion.TenantAccountId;
                tenantLedgerEntryId = suggestion.EntityId;
            }
            else
                expenseId = checked((int)suggestion.EntityId);
        }

        return await ReconcileAsync(
            portfolioId,
            transaction,
            tenantLedgerEntryId.HasValue ? BankReconciliationAction.MatchReceipt : BankReconciliationAction.MatchExpense,
            tenantAccountId,
            tenantLedgerEntryId,
            expenseId,
            ct);
    }

    public async Task<BankTransactionResponse?> DismissMatchAsync(int portfolioId, int transactionId, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        return await ReconcileAsync(portfolioId, transaction, BankReconciliationAction.Dismiss, null, null, null, ct);
    }

    public async Task<BankTransactionResponse?> IgnoreTransactionAsync(int portfolioId, int transactionId, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(portfolioId).AsNoTracking()
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
        return await ReconcileAsync(portfolioId, transaction, BankReconciliationAction.Ignore, null, null, null, ct);
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
                // Deposits suggest against canonical posted tenant receipt entries.
                (t.Amount > 0 && _db.TenantLedgerEntries.Any(entry =>
                    entry.PortfolioId == portfolioId &&
                    entry.EntryType == TenantLedgerEntryType.PaymentReceipt &&
                    entry.Direction == TenantLedgerDirection.Credit &&
                    entry.Amount >= t.Amount - 0.01m && entry.Amount <= t.Amount + 0.01m &&
                    entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-7)) &&
                    entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(7))))
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

    private static BankTransactionInput ToInput(ImportBankTransactionItem item) => new(
        Normalize(item.ProviderTransactionId) ?? string.Empty,
        item.PostedAt.ToUtc(),
        item.AuthorizedAt.ToUtc(),
        item.Description.Trim(),
        Normalize(item.MerchantName),
        item.Amount,
        Normalize(item.IsoCurrencyCode) ?? "USD",
        Normalize(item.Category),
        item.RawData);

    private sealed record NormalizedPlaidInputs(
        IReadOnlyList<BankTransactionInput> Items,
        int InputCount);

    private static NormalizedPlaidInputs NormalizePlaidInputs(
        IReadOnlyList<PlaidSyncedTransaction> source,
        string? linkedAccountId)
    {
        var eligible = source
            .Where(item => linkedAccountId is null || item.AccountId == linkedAccountId)
            .ToArray();
        var items = eligible
            .Select(item => new BankTransactionInput(
                Normalize(item.TransactionId) ?? string.Empty,
                item.PostedAt.ToUtc(),
                item.AuthorizedAt.ToUtc(),
                item.Description.Trim(),
                Normalize(item.MerchantName),
                -item.Amount,
                Normalize(item.IsoCurrencyCode) ?? "USD",
                Normalize(item.Category),
                item.RawData))
            .Where(item => item.ProviderTransactionId.Length > 0)
            .GroupBy(item => item.ProviderTransactionId, StringComparer.Ordinal)
            // Plaid transaction ids are immutable identities. If a malformed response repeats one,
            // preserve the first authoritative occurrence rather than allowing a later duplicate to
            // silently reverse its amount direction or replace its descriptive data. This also matches
            // manual-import deduplication; legitimate changes arrive in Plaid's Modified collection.
            .Select(group => group.First())
            .Take(MaxImportBatch)
            .ToArray();
        return new NormalizedPlaidInputs(items, eligible.Length);
    }

    private async Task<BankTransactionResponse?> ReconcileAsync(
        int portfolioId,
        BankTransaction current,
        BankReconciliationAction action,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        int? expenseId,
        CancellationToken ct)
    {
        var identity = Digest(
            portfolioId,
            current.Id,
            current.UpdatedAt.ToUniversalTime().Ticks,
            action,
            tenantAccountId,
            tenantLedgerEntryId,
            expenseId);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.transaction.reconcile", $"{portfolioId}:{current.Id}:{identity}"),
            new ReconcileBankTransactionCommand(
                portfolioId,
                current.Id,
                action,
                tenantAccountId,
                tenantLedgerEntryId,
                expenseId,
                current.UpdatedAt,
                _timeProvider.UtcNow()),
            ReconciliationCodec,
            ct);
        if (outcome.Value.Outcome is ReconcileBankTransactionOutcome.TransactionNotFound
            or ReconcileBankTransactionOutcome.TargetNotFound)
        {
            return null;
        }

        var canonical = await BaseTransactions(portfolioId).AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == current.Id, ct);
        return canonical is null
            ? null
            : (await MapTransactionsWithSuggestionsAsync(portfolioId, [canonical], ct)).Single();
    }

    private static string Digest(params object?[] values)
    {
        var canonical = string.Join("\u001f", values.Select(value => value switch
        {
            DateTime timestamp => timestamp.ToUniversalTime().ToString("O"),
            _ => value?.ToString() ?? "<null>",
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
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

    private static void ValidateMatchTarget(
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        int? expenseId)
    {
        ValidateOptionalMatchTarget(tenantAccountId, tenantLedgerEntryId, expenseId);
        if (tenantLedgerEntryId is null && expenseId is null)
        {
            throw new InvalidOperationException(
                "Provide a tenantAccountId with tenantLedgerEntryId, or provide an expenseId.");
        }
    }

    private static void ValidateOptionalMatchTarget(
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        int? expenseId)
    {
        var hasReceiptIdentity = tenantAccountId.HasValue || tenantLedgerEntryId.HasValue;
        if (hasReceiptIdentity && (!tenantAccountId.HasValue || !tenantLedgerEntryId.HasValue))
        {
            throw new InvalidOperationException(
                "tenantAccountId and tenantLedgerEntryId must be supplied together.");
        }
        if (hasReceiptIdentity && expenseId.HasValue)
        {
            throw new InvalidOperationException(
                "Provide either a tenant receipt identity or an expenseId, not both.");
        }
    }

    private async Task<Dictionary<int, BankMatchSuggestionResponse>> LoadSqlRankedSuggestionsAsync(
        int portfolioId,
        int[] transactionIds,
        CancellationToken ct)
    {
        var receiptCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from entry in _db.TenantLedgerEntries.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { entry.TenantAccountId, entry.PortfolioId }
                equals new { TenantAccountId = account.Id, account.PortfolioId }
            let bankText = ((t.MerchantName ?? "") + " " + t.Description).ToLower()
            let hasNameMatch =
                bankText.Contains(account.AccountNumber.ToLower()) ||
                bankText.Contains(account.LeaseManagement!.RelationshipNumber.ToLower()) ||
                bankText.Contains(account.LeaseManagement!.Property!.Name.ToLower()) ||
                account.LeaseManagement!.Parties.Any(party =>
                    bankText.Contains((party.Tenant!.FirstName + " " + party.Tenant!.LastName).Trim().ToLower()))
            let dateScore =
                entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-1)) && entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(1)) ? 0.80m :
                entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-2)) && entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(2)) ? 0.72m :
                entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-4)) && entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(4)) ? 0.62m :
                entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-7)) && entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(7)) ? 0.52m :
                entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-14)) && entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(14)) && hasNameMatch ? 0.42m :
                0m
            where
                transactionIds.Contains(t.Id) &&
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                t.Amount > 0m &&
                entry.PortfolioId == portfolioId &&
                entry.EntryType == TenantLedgerEntryType.PaymentReceipt &&
                entry.Direction == TenantLedgerDirection.Credit &&
                entry.Amount >= t.Amount - 0.01m &&
                entry.Amount <= t.Amount + 0.01m &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "TenantLedgerEntry",
                EntityId = entry.Id,
                TenantAccountId = entry.TenantAccountId,
                Confidence = hasNameMatch ? dateScore + 0.19m : dateScore,
                Label = account.AccountNumber + " payment receipt",
                Reason = "Deposit amount and date line up with a posted tenant-account receipt.",
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
                TenantAccountId = null,
                Confidence = hasNameMatch ? dateScore + 0.19m : dateScore,
                Label = e.Vendor == null ? e.Description : e.Vendor!.Name,
                Reason = "Withdrawal amount and date line up with an expense.",
            };

        var rows = await receiptCandidates
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
                TenantAccountId = r.TenantAccountId,
                Confidence = r.Confidence > 0.99m ? 0.99m : r.Confidence,
                Label = string.IsNullOrWhiteSpace(r.Label) ? r.EntityType.ToLowerInvariant() : r.Label,
                Reason = r.Reason,
            });
    }

    private sealed class BankSuggestionRankRow
    {
        public int TransactionId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public long EntityId { get; set; }
        public int? TenantAccountId { get; set; }
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
        MatchedTenantAccountId = t.MatchedTenantAccountId,
        MatchedTenantLedgerEntryId = t.MatchedTenantLedgerEntryId,
        MatchedExpenseId = t.MatchedExpenseId,
        MatchStatus = t.MatchStatus,
        MatchConfidence = t.MatchConfidence,
        Notes = t.Notes,
        SuggestedMatch = suggestion
    };
}
