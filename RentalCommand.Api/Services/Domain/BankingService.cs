using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

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
    private static readonly AtomicJsonResultCodec<RouteBankTransactionResult> RoutingCodec =
        new("banking.routing.result.v1");

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

        var transactions = await TransactionsWithSuggestionsQuery(portfolioId, BaseTransactions(portfolioId))
            .OrderByDescending(t => t.PostedAt)
            .ThenByDescending(t => t.Id)
            .Take(10)
            .ToListAsync(ct);

        var unmatchedCount = await _db.BankTransactions
            .CountAsync(t => t.PortfolioId == portfolioId && t.MatchStatus == "Unmatched", ct);

        var suggestedMatchCount = await CountSuggestibleUnmatchedAsync(portfolioId, ct);

        return new BankingSummaryResponse
        {
            ConnectionCount = connectionCount,
            TransactionCount = await _db.BankTransactions.CountAsync(t => t.PortfolioId == portfolioId, ct),
            UnmatchedCount = unmatchedCount,
            SuggestedMatchCount = suggestedMatchCount,
            LastSyncedAt = lastSyncedAt,
            Connections = connections.Select(MapConnection).ToList(),
            RecentTransactions = transactions.Select(MapTransaction).ToList()
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
        WorkspaceReadScope scope,
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

        var settings = await GetRuntimeSettingsAsync(scope.PortfolioId, ct);
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Plaid settings are not configured.");
        }

        var accountIdHash = ExternalLookupHash(accountId)!;
        var now = _timeProvider.UtcNow();
        var publicTokenHash = ExternalLookupHash(publicToken)!;
        var requestHash = Digest(
            scope.PortfolioId,
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
                $"{scope.PortfolioId}:{Digest(clientOperationId)}"),
            new PreparePlaidTokenExchangeCommand(
                scope.PortfolioId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                CapabilityKeys.BankConnectionsManage,
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
                && row.PortfolioId == scope.PortfolioId, ct);
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
                    $"{scope.PortfolioId}:{exchangeAttempt.Id:N}"),
                new AdmitPlaidTokenExchangeCommand(scope.PortfolioId, exchangeAttempt.Id, _timeProvider.UtcNow()),
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
                    $"{scope.PortfolioId}:{exchangeAttempt.Id:N}"),
                new RecordPlaidTokenExchangeReceiptCommand(
                    scope.PortfolioId,
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
                $"{scope.PortfolioId}:{exchangeAttempt.Id:N}"),
            new ApplyPlaidConnectionCommand(scope.PortfolioId, exchangeAttempt.Id, _timeProvider.UtcNow()),
            PlaidConnectionCodec,
            ct);

        if (exchangeAttempt.CompletedAtUtc is null)
            await SyncPlaidConnectionAsync(scope.PortfolioId, outcome.Value.ConnectionId, ct);
        return await _db.BankConnections.AsNoTracking()
            .Where(row => row.PortfolioId == scope.PortfolioId && row.Id == outcome.Value.ConnectionId)
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
                ? new List<BankTransactionSqlRow>()
                : await TransactionsWithSuggestionsQuery(
                        portfolioId,
                        BaseTransactions(portfolioId).AsNoTracking()
                            .Where(row => outcome.Value.AffectedTransactionIds.Contains(row.Id)))
                    .OrderBy(row => row.Id)
                    .ToListAsync(ct);
            return new SyncBankConnectionResponse
            {
                Connection = MapConnection(canonicalConnection),
                ImportedCount = outcome.Value.ImportedCount,
                SkippedCount = outcome.Value.SkippedCount,
                Transactions = affected.Select(MapTransaction).ToList(),
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
        var transactions = await TransactionsWithSuggestionsQuery(portfolioId, query)
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
            Items = transactions.Select(MapTransaction).ToList(),
        };
    }

    public async Task<ImportBankTransactionsResponse> ImportAsync(
        int portfolioId,
        ImportBankTransactionsRequest request,
        CancellationToken ct = default)
    {
        if (request.Statement is null && request.Transactions.Count == 0)
        {
            throw new InvalidOperationException("A bank statement or at least one bank transaction is required.");
        }
        if (request.Transactions.Count > MaxImportBatch)
        {
            throw new InvalidOperationException($"A bank import cannot contain more than {MaxImportBatch} transactions.");
        }

        var now = _timeProvider.UtcNow();
        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "Manual" : request.Provider.Trim();
        var institution = string.IsNullOrWhiteSpace(request.InstitutionName) ? "Imported bank" : request.InstitutionName.Trim();
        var account = string.IsNullOrWhiteSpace(request.AccountName) ? "Imported account" : request.AccountName.Trim();
        var inputs = request.Transactions.Select(ToInput).ToArray();
        if (inputs.Any(input => input.ProviderTransactionId.Length == 0))
        {
            throw new InvalidOperationException("Every imported bank transaction requires a provider transaction id.");
        }
        var statement = ToStatementInput(request.Statement);
        var requestIdentity = Digest(
            portfolioId,
            provider,
            institution,
            account,
            Normalize(request.AccountMask),
            request.Transactions.Count,
            JsonSerializer.Serialize(statement),
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
                now,
                statement),
            ImportCodec,
            ct);

        var connection = await _db.BankConnections.AsNoTracking()
            .SingleAsync(row => row.PortfolioId == portfolioId && row.Id == outcome.Value.ConnectionId, ct);
        var importedStatement = outcome.Value.StatementId is not { } statementId
            ? null
            : await _db.BankStatements.AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == statementId)
                .Select(row => new BankStatementResponse
                {
                    Id = row.Id,
                    BankConnectionId = row.BankConnectionId,
                    PeriodStart = row.PeriodStart,
                    PeriodEnd = row.PeriodEnd,
                    OpeningBalance = row.OpeningBalance,
                    ClosingBalance = row.ClosingBalance,
                    StatementMovement = row.StatementMovement,
                    IsoCurrencyCode = row.IsoCurrencyCode,
                    ImportedAtUtc = row.ImportedAtUtc,
                })
                .SingleAsync(ct);
        var imported = outcome.Value.ImportedTransactionIds.Count == 0
            ? new List<BankTransactionSqlRow>()
            : await TransactionsWithSuggestionsQuery(
                    portfolioId,
                    BaseTransactions(portfolioId).AsNoTracking()
                        .Where(row => outcome.Value.ImportedTransactionIds.Contains(row.Id)))
                .OrderBy(row => row.Id)
                .ToListAsync(ct);

        return new ImportBankTransactionsResponse
        {
            Connection = MapConnection(connection),
            Statement = importedStatement,
            ImportedCount = outcome.Value.ImportedCount,
            SkippedCount = outcome.Value.SkippedCount,
            Transactions = imported.Select(MapTransaction).ToList(),
        };
    }

    public async Task<OperationalBankTransactionResponse?> MatchAsync(
        WorkspaceReadScope scope,
        int transactionId,
        MatchBankTransactionRequest request,
        CancellationToken ct = default)
    {
        ValidateMatchTarget(
            request.TenantAccountId,
            request.TenantLedgerEntryId,
            request.ExpenseId,
            request.LoanPaymentId,
            request.OwnerDistributionId,
            request.TransferBankTransactionId,
            request.ExpectedTransferUpdatedAtUtc);
        // An explicit retry must reach the receipt kernel after the first attempt committed.
        // The handler owns authorization and exact target validation.
        var transaction = await BaseTransactions(scope.PortfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        var updated = await ReconcileAsync(
            scope,
            transaction,
            MatchAction(
                request.TenantLedgerEntryId,
                request.ExpenseId,
                request.LoanPaymentId,
                request.OwnerDistributionId,
                request.TransferBankTransactionId),
            request.TenantAccountId,
            request.TenantLedgerEntryId,
            request.ExpenseId,
            request.LoanPaymentId,
            request.OwnerDistributionId,
            request.TransferBankTransactionId,
            request.ExpectedTransferUpdatedAtUtc,
            request.OperationKey,
            request.ExpectedUpdatedAtUtc,
            ct);
        return updated is null ? null : MapOperationalTransactionResponse(updated);
    }

    public async Task<BankTransactionResponse?> RouteTransactionAsync(
        WorkspaceReadScope scope,
        int transactionId,
        RouteBankTransactionRequest request,
        CancellationToken ct = default)
    {
        ValidateOperation(request.OperationKey, request.ExpectedUpdatedAtUtc);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.transaction.route",
                $"{scope.PortfolioId}:{scope.AccessContextId}:{transactionId}:{request.OperationKey}"),
            new RouteBankTransactionCommand(
                scope.PortfolioId, transactionId, request.PropertyId, request.ExpectedUpdatedAtUtc,
                _timeProvider.UtcNow(), scope.UserId, scope.SessionId, scope.AccessContextId,
                scope.AccessRevision, request.OperationKey),
            RoutingCodec,
            ct);
        return outcome.Value.Outcome switch
        {
            RouteBankTransactionOutcome.TransactionNotFound or RouteBankTransactionOutcome.PropertyNotFound => null,
            RouteBankTransactionOutcome.StaleVersion => throw new BankingConflictException(
                "This bank transaction changed after it was loaded. Refresh and choose the property again."),
            _ => await LoadFullTransactionAsync(scope.PortfolioId, transactionId, ct),
        };
    }

    public async Task<BankTransactionResponse?> ClearMatchAsync(
        WorkspaceReadScope scope, int transactionId, BankTransactionMutationRequest request, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(scope.PortfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        return await ReconcileAsync(scope, transaction, BankReconciliationAction.Clear,
            null, null, null, null, null, null, null,
            request.OperationKey, request.ExpectedUpdatedAtUtc, ct,
            CapabilityKeys.MoneyReconciliationDestructive);
    }

    public async Task<BankReviewQueueResponse> GetReviewQueueAsync(
        WorkspaceReadScope scope,
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
        var ranked = RankedSuggestionsQuery(scope.PortfolioId, null, scope);
        var count = await ranked.CountAsync(ct);
        var rows = await (
                from transaction in _db.BankTransactions.AsNoTracking()
                join suggestion in ranked on transaction.Id equals suggestion.TransactionId
                orderby transaction.PostedAt descending, transaction.Id descending
                select new BankReviewQueueSqlRow
                {
                    Id = transaction.Id,
                    PostedAt = transaction.PostedAt,
                    Description = transaction.Description,
                    MerchantName = transaction.MerchantName,
                    Amount = transaction.Amount,
                    IsoCurrencyCode = transaction.IsoCurrencyCode,
                    Category = transaction.Category,
                    MatchStatus = transaction.MatchStatus,
                    UpdatedAt = transaction.UpdatedAt,
                    Confidence = suggestion.Confidence > 0.99m ? 0.99m : suggestion.Confidence,
                    Label = suggestion.Label,
                    Reason = suggestion.Reason,
                })
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        var items = rows.Select(MapOperationalQueueItem).ToList();

        return new BankReviewQueueResponse
        {
            Count = count,
            Skip = skip,
            Take = take,
            Items = items,
        };
    }

    public async Task<OperationalBankTransactionResponse?> ConfirmMatchAsync(
        WorkspaceReadScope scope,
        int transactionId,
        ConfirmBankMatchRequest request,
        CancellationToken ct = default)
    {
        // An explicit canonical receipt identity or expense wins; otherwise use the current
        // SQL-ranked suggestion so one-tap confirmation does not need to echo identifiers.
        int? tenantAccountId = request.TenantAccountId;
        long? tenantLedgerEntryId = request.TenantLedgerEntryId;
        int? expenseId = request.ExpenseId;
        int? loanPaymentId = request.LoanPaymentId;
        int? ownerDistributionId = request.OwnerDistributionId;
        int? transferBankTransactionId = request.TransferBankTransactionId;
        DateTime? expectedTransferUpdatedAtUtc = request.ExpectedTransferUpdatedAtUtc;

        ValidateOptionalMatchTarget(
            tenantAccountId,
            tenantLedgerEntryId,
            expenseId,
            loanPaymentId,
            ownerDistributionId,
            transferBankTransactionId,
            expectedTransferUpdatedAtUtc);

        var hasExplicitTarget = TargetCount(
                tenantLedgerEntryId,
                expenseId,
                loanPaymentId,
                ownerDistributionId,
                transferBankTransactionId) != 0;
        var transaction = await BaseTransactions(scope.PortfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;

        if (!hasExplicitTarget)
        {
            if (transaction.MatchStatus == "Matched")
            {
                tenantAccountId = transaction.MatchedTenantAccountId;
                tenantLedgerEntryId = transaction.MatchedTenantLedgerEntryId;
                expenseId = transaction.MatchedExpenseId;
                loanPaymentId = transaction.MatchedLoanPaymentId;
                ownerDistributionId = transaction.MatchedOwnerDistributionId;
                transferBankTransactionId = transaction.MatchedBankTransactionId;
            }
            else if (transaction.MatchStatus == "Unmatched")
            {
                var suggestion = await LoadSqlRankedSuggestionAsync(
                    scope.PortfolioId, transaction.Id, scope, ct);
                if (suggestion is null) return null;
                if (suggestion.EntityType.Equals("TenantLedgerEntry", StringComparison.OrdinalIgnoreCase))
                {
                    tenantAccountId = suggestion.TenantAccountId;
                    tenantLedgerEntryId = suggestion.EntityId;
                }
                else if (suggestion.EntityType.Equals("Expense", StringComparison.OrdinalIgnoreCase))
                    expenseId = checked((int)suggestion.EntityId);
                else if (suggestion.EntityType.Equals("LoanPayment", StringComparison.OrdinalIgnoreCase))
                    loanPaymentId = checked((int)suggestion.EntityId);
                else if (suggestion.EntityType.Equals("OwnerDistribution", StringComparison.OrdinalIgnoreCase))
                    ownerDistributionId = checked((int)suggestion.EntityId);
                else if (suggestion.EntityType.Equals("BankTransfer", StringComparison.OrdinalIgnoreCase))
                    transferBankTransactionId = checked((int)suggestion.EntityId);
                else
                    return null;
            }
            else
                return null;

            if (transferBankTransactionId is { } transferId)
            {
                expectedTransferUpdatedAtUtc = await _db.BankTransactions.AsNoTracking()
                    .Where(row => row.PortfolioId == scope.PortfolioId && row.Id == transferId)
                    .Select(row => (DateTime?)row.UpdatedAt)
                    .SingleOrDefaultAsync(ct);
                if (expectedTransferUpdatedAtUtc is null) return null;
            }
        }

        var updated = await ReconcileAsync(
            scope,
            transaction,
            MatchAction(
                tenantLedgerEntryId,
                expenseId,
                loanPaymentId,
                ownerDistributionId,
                transferBankTransactionId),
            tenantAccountId,
            tenantLedgerEntryId,
            expenseId,
            loanPaymentId,
            ownerDistributionId,
            transferBankTransactionId,
            hasExplicitTarget ? expectedTransferUpdatedAtUtc : null,
            request.OperationKey,
            request.ExpectedUpdatedAtUtc,
            ct,
            resolvedSuggestionTransferUpdatedAtUtc:
                !hasExplicitTarget ? expectedTransferUpdatedAtUtc : null);
        return updated is null ? null : MapOperationalTransactionResponse(updated);
    }

    public async Task<BankTransactionResponse?> DismissMatchAsync(
        WorkspaceReadScope scope, int transactionId, BankTransactionMutationRequest request, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(scope.PortfolioId).AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (transaction == null) return null;
        return await ReconcileAsync(scope, transaction, BankReconciliationAction.Dismiss,
            null, null, null, null, null, null, null,
            request.OperationKey, request.ExpectedUpdatedAtUtc, ct,
            CapabilityKeys.MoneyReconciliationDestructive);
    }

    public async Task<BankTransactionResponse?> IgnoreTransactionAsync(
        WorkspaceReadScope scope, int transactionId, BankTransactionMutationRequest request, CancellationToken ct = default)
    {
        var transaction = await BaseTransactions(scope.PortfolioId).AsNoTracking()
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
        return await ReconcileAsync(scope, transaction, BankReconciliationAction.Ignore,
            null, null, null, null, null, null, null,
            request.OperationKey, request.ExpectedUpdatedAtUtc, ct,
            CapabilityKeys.MoneyReconciliationDestructive);
    }

    private IQueryable<BankTransaction> BaseTransactions(int portfolioId)
    {
        return _db.BankTransactions
            .Include(t => t.BankConnection)
            .Include(t => t.Property)
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
                (_db.TenantLedgerEntries.Any(entry =>
                    entry.PortfolioId == portfolioId &&
                    ((entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && entry.Direction == TenantLedgerDirection.Credit
                        && t.Amount > 0m)
                     || (entry.EntryType == TenantLedgerEntryType.TransferIn
                         || entry.EntryType == TenantLedgerEntryType.TransferOut)
                        && ((entry.Direction == TenantLedgerDirection.Credit && t.Amount > 0m)
                            || (entry.Direction == TenantLedgerDirection.Debit && t.Amount < 0m))) &&
                    entry.Amount >= (t.Amount < 0m ? -t.Amount : t.Amount) - 0.01m &&
                    entry.Amount <= (t.Amount < 0m ? -t.Amount : t.Amount) + 0.01m &&
                    entry.EffectiveOn >= DateOnly.FromDateTime(t.PostedAt.AddDays(-7)) &&
                    entry.EffectiveOn <= DateOnly.FromDateTime(t.PostedAt.AddDays(7))))
                ||
                // Withdrawals (spend) suggest against expenses; expense amounts are stored positive while
                // the bank withdrawal is negative, so compare against the absolute amount.
                (t.Amount < 0 && _db.Expenses.Any(e =>
                    e.PortfolioId == portfolioId &&
                    e.Amount >= -t.Amount - 0.01m && e.Amount <= -t.Amount + 0.01m &&
                    (e.PaidAt ?? e.IncurredAt) >= t.PostedAt.AddDays(-7) &&
                    (e.PaidAt ?? e.IncurredAt) <= t.PostedAt.AddDays(7)))
                ||
                (t.Amount < 0 && LoanPaymentEffectiveQuery.From(_db).Any(payment =>
                    payment.PortfolioId == portfolioId &&
                    payment.Status == LoanPaymentStatus.Paid &&
                    payment.TotalAmount >= -t.Amount - 0.01m &&
                    payment.TotalAmount <= -t.Amount + 0.01m &&
                    (payment.PaidDate ?? payment.DueDate) >= t.PostedAt.AddDays(-7) &&
                    (payment.PaidDate ?? payment.DueDate) <= t.PostedAt.AddDays(7)))
                ||
                (t.Amount < 0 && _db.OwnerDistributions.Any(distribution =>
                    distribution.PortfolioId == portfolioId &&
                    distribution.Status == OwnerDistributionStatus.Approved &&
                    distribution.Amount >= -t.Amount - 0.01m &&
                    distribution.Amount <= -t.Amount + 0.01m &&
                    distribution.Date >= t.PostedAt.AddDays(-7) &&
                    distribution.Date <= t.PostedAt.AddDays(7)))
                ||
                _db.BankTransactions.Any(other =>
                    other.PortfolioId == portfolioId &&
                    other.Id != t.Id &&
                    other.BankConnectionId != t.BankConnectionId &&
                    other.MatchStatus == "Unmatched" &&
                    other.Amount >= -t.Amount - 0.01m &&
                    other.Amount <= -t.Amount + 0.01m &&
                    other.PostedAt >= t.PostedAt.AddDays(-3) &&
                    other.PostedAt <= t.PostedAt.AddDays(3)));
    }

    /// <summary>
    /// Property-scoped operational queue. The authorization predicates remain correlated inside the
    /// bank-line query, so count, ordering, and paging all execute against the caller's current
    /// session/capability/property scope in PostgreSQL.
    /// </summary>
    private IQueryable<BankTransaction> SuggestibleUnmatchedTransactionsQuery(WorkspaceReadScope scope)
    {
        var now = _timeProvider.UtcNow();
        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, CapabilityKeys.MoneyReconciliationOperate, now);
        var authorizedExpenses = _db.Expenses.AsNoTracking()
            .WhereMoneyAuthorized(_db, scope, CapabilityKeys.MoneyReconciliationOperate, now);
        var authorizedLoans = _db.Loans.AsNoTracking()
            .WhereMoneyAuthorized(_db, scope, CapabilityKeys.MoneyReconciliationOperate, now);
        var authorizedDistributions = _db.OwnerDistributions.AsNoTracking()
            .WhereMoneyAuthorized(_db, scope, CapabilityKeys.MoneyReconciliationOperate, now);
        var allProperties = _db.AuthorizedAllPropertyAssignments(
            scope,
            CapabilityKeys.MoneyReconciliationOperate,
            CapabilityAuthorizationTargetKind.Property,
            now);

        return _db.BankTransactions.AsNoTracking()
            .Where(transaction =>
                transaction.PortfolioId == scope.PortfolioId &&
                transaction.MatchStatus == "Unmatched" &&
                ((transaction.PropertyId != null &&
                  authorizedProperties.Any(property => property.Id == transaction.PropertyId))
                 || (transaction.PropertyId == null && allProperties.Any())))
            .Where(transaction =>
                (_db.TenantLedgerEntries.AsNoTracking().Any(entry =>
                    entry.PortfolioId == scope.PortfolioId &&
                    ((entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && entry.Direction == TenantLedgerDirection.Credit
                        && transaction.Amount > 0m)
                     || (entry.EntryType == TenantLedgerEntryType.TransferIn
                         || entry.EntryType == TenantLedgerEntryType.TransferOut)
                        && ((entry.Direction == TenantLedgerDirection.Credit && transaction.Amount > 0m)
                            || (entry.Direction == TenantLedgerDirection.Debit && transaction.Amount < 0m))) &&
                    entry.Amount >= (transaction.Amount < 0m ? -transaction.Amount : transaction.Amount) - 0.01m &&
                    entry.Amount <= (transaction.Amount < 0m ? -transaction.Amount : transaction.Amount) + 0.01m &&
                    entry.EffectiveOn >= DateOnly.FromDateTime(transaction.PostedAt.AddDays(-7)) &&
                    entry.EffectiveOn <= DateOnly.FromDateTime(transaction.PostedAt.AddDays(7)) &&
                    entry.TenantAccount!.LeaseManagement!.PropertyId == transaction.PropertyId)) ||
                (transaction.Amount < 0 && authorizedExpenses.Any(expense =>
                    expense.DeletedAt == null &&
                    (expense.Status == ExpenseStatus.Pending ||
                     expense.Status == ExpenseStatus.Approved ||
                     expense.Status == ExpenseStatus.Paid) &&
                    expense.Amount >= -transaction.Amount - 0.01m &&
                    expense.Amount <= -transaction.Amount + 0.01m &&
                    (expense.PaidAt ?? expense.IncurredAt) >= transaction.PostedAt.AddDays(-7) &&
                    (expense.PaidAt ?? expense.IncurredAt) <= transaction.PostedAt.AddDays(7) &&
                    (expense.PropertyId == transaction.PropertyId
                        || (expense.PropertyId == null && expense.Unit!.PropertyId == transaction.PropertyId)
                        || (expense.PropertyId == null && expense.UnitId == null
                            && expense.WorkOrder!.PropertyId == transaction.PropertyId)
                        || (transaction.PropertyId == null && expense.PropertyId == null
                            && expense.UnitId == null && expense.WorkOrderId == null)))
                ||
                (transaction.Amount < 0 && authorizedLoans.Any(loan =>
                    loan.Payments.Any(payment =>
                        payment.Status == LoanPaymentStatus.Paid &&
                        payment.TotalAmount >= -transaction.Amount - 0.01m &&
                        payment.TotalAmount <= -transaction.Amount + 0.01m &&
                        (payment.PaidDate ?? payment.DueDate) >= transaction.PostedAt.AddDays(-7) &&
                        (payment.PaidDate ?? payment.DueDate) <= transaction.PostedAt.AddDays(7)) &&
                    loan.PropertyId == transaction.PropertyId))
                ||
                (transaction.Amount < 0 && authorizedDistributions.Any(distribution =>
                    distribution.Status == OwnerDistributionStatus.Approved &&
                    distribution.Amount >= -transaction.Amount - 0.01m &&
                    distribution.Amount <= -transaction.Amount + 0.01m &&
                    distribution.Date >= transaction.PostedAt.AddDays(-7) &&
                    distribution.Date <= transaction.PostedAt.AddDays(7) &&
                    (distribution.PropertyId == transaction.PropertyId
                     || (distribution.PropertyId == null && transaction.PropertyId == null))))
                ||
                (allProperties.Any() && _db.BankTransactions.AsNoTracking().Any(other =>
                    other.PortfolioId == scope.PortfolioId &&
                    other.Id != transaction.Id &&
                    other.BankConnectionId != transaction.BankConnectionId &&
                    other.MatchStatus == "Unmatched" &&
                    other.Amount >= -transaction.Amount - 0.01m &&
                    other.Amount <= -transaction.Amount + 0.01m &&
                    other.PostedAt >= transaction.PostedAt.AddDays(-3) &&
                    other.PostedAt <= transaction.PostedAt.AddDays(3)))));
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

    private static BankStatementInput? ToStatementInput(ImportBankStatementControl? statement)
    {
        if (statement is null) return null;
        if (statement.PeriodStart == default || statement.PeriodEnd == default)
            throw new InvalidOperationException("A bank statement requires a period start and end.");
        if (statement.PeriodStart > statement.PeriodEnd)
            throw new InvalidOperationException("A bank statement period cannot end before it starts.");

        var currency = Normalize(statement.IsoCurrencyCode)?.ToUpperInvariant() ?? "USD";
        if (currency.Length > 8)
            throw new InvalidOperationException("A bank statement currency code cannot exceed 8 characters.");
        var movement = statement.ClosingBalance - statement.OpeningBalance;
        return new BankStatementInput(
            statement.PeriodStart,
            statement.PeriodEnd,
            statement.OpeningBalance,
            statement.ClosingBalance,
            movement,
            currency);
    }

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
        WorkspaceReadScope scope,
        BankTransaction current,
        BankReconciliationAction action,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        int? expenseId,
        int? loanPaymentId,
        int? ownerDistributionId,
        int? transferBankTransactionId,
        DateTime? expectedTransferUpdatedAtUtc,
        string operationKey,
        DateTime expectedUpdatedAtUtc,
        CancellationToken ct,
        string requiredCapability = CapabilityKeys.MoneyReconciliationOperate,
        DateTime? resolvedSuggestionTransferUpdatedAtUtc = null)
    {
        ValidateOperation(operationKey, expectedUpdatedAtUtc);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.transaction.reconcile",
                $"{scope.PortfolioId}:{scope.AccessContextId}:{current.Id}:{operationKey}"),
            new ReconcileBankTransactionCommand(
                scope.PortfolioId,
                current.Id,
                action,
                tenantAccountId,
                tenantLedgerEntryId,
                expenseId,
                loanPaymentId,
                ownerDistributionId,
                transferBankTransactionId,
                expectedTransferUpdatedAtUtc,
                expectedUpdatedAtUtc,
                _timeProvider.UtcNow(),
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                requiredCapability,
                operationKey,
                resolvedSuggestionTransferUpdatedAtUtc),
            ReconciliationCodec,
            ct);
        if (outcome.Value.Outcome is ReconcileBankTransactionOutcome.TransactionNotFound
            or ReconcileBankTransactionOutcome.TargetNotFound
            or ReconcileBankTransactionOutcome.RouteRequired)
        {
            return null;
        }
        if (outcome.Value.Outcome == ReconcileBankTransactionOutcome.StaleVersion)
            throw new BankingConflictException(
                "This bank transaction changed after it was loaded. Refresh before reconciling it.");
        return outcome.Value.Transaction is null ? null : MapTransaction(outcome.Value.Transaction);
    }

    private static void ValidateOperation(string operationKey, DateTime expectedUpdatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 128)
            throw new DomainValidationException("A stable operationKey of at most 128 characters is required.");
        if (expectedUpdatedAtUtc == default)
            throw new DomainValidationException("expectedUpdatedAtUtc is required.");
    }

    private async Task<BankTransactionResponse?> LoadFullTransactionAsync(
        int portfolioId, int transactionId, CancellationToken ct)
    {
        var canonical = await TransactionsWithSuggestionsQuery(
                portfolioId,
                BaseTransactions(portfolioId).AsNoTracking().Where(row => row.Id == transactionId))
            .SingleOrDefaultAsync(ct);
        return canonical is null ? null : MapTransaction(canonical);
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

    private static void ValidateMatchTarget(
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        int? expenseId,
        int? loanPaymentId,
        int? ownerDistributionId,
        int? transferBankTransactionId,
        DateTime? expectedTransferUpdatedAtUtc)
    {
        ValidateOptionalMatchTarget(
            tenantAccountId,
            tenantLedgerEntryId,
            expenseId,
            loanPaymentId,
            ownerDistributionId,
            transferBankTransactionId,
            expectedTransferUpdatedAtUtc);
        if (TargetCount(
                tenantLedgerEntryId,
                expenseId,
                loanPaymentId,
                ownerDistributionId,
                transferBankTransactionId) == 0)
        {
            throw new DomainValidationException(
                "Provide exactly one canonical bank reconciliation target.");
        }
    }

    private static void ValidateOptionalMatchTarget(
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        int? expenseId,
        int? loanPaymentId,
        int? ownerDistributionId,
        int? transferBankTransactionId,
        DateTime? expectedTransferUpdatedAtUtc)
    {
        var hasReceiptIdentity = tenantAccountId.HasValue || tenantLedgerEntryId.HasValue;
        if (hasReceiptIdentity && (!tenantAccountId.HasValue || !tenantLedgerEntryId.HasValue))
        {
            throw new DomainValidationException(
                "tenantAccountId and tenantLedgerEntryId must be supplied together.");
        }
        var targetCount = TargetCount(
            tenantLedgerEntryId,
            expenseId,
            loanPaymentId,
            ownerDistributionId,
            transferBankTransactionId);
        if (targetCount > 1)
        {
            throw new DomainValidationException(
                "Provide exactly one bank reconciliation target.");
        }
        if (transferBankTransactionId.HasValue != expectedTransferUpdatedAtUtc.HasValue)
            throw new DomainValidationException(
                "transferBankTransactionId and expectedTransferUpdatedAtUtc must be supplied together.");
    }

    private static int TargetCount(
        long? tenantLedgerEntryId,
        int? expenseId,
        int? loanPaymentId,
        int? ownerDistributionId,
        int? transferBankTransactionId) =>
        (tenantLedgerEntryId.HasValue ? 1 : 0)
        + (expenseId.HasValue ? 1 : 0)
        + (loanPaymentId.HasValue ? 1 : 0)
        + (ownerDistributionId.HasValue ? 1 : 0)
        + (transferBankTransactionId.HasValue ? 1 : 0);

    private static BankReconciliationAction MatchAction(
        long? tenantLedgerEntryId,
        int? expenseId,
        int? loanPaymentId,
        int? ownerDistributionId,
        int? transferBankTransactionId) =>
        tenantLedgerEntryId.HasValue ? BankReconciliationAction.MatchReceipt
        : expenseId.HasValue ? BankReconciliationAction.MatchExpense
        : loanPaymentId.HasValue ? BankReconciliationAction.MatchLoanPayment
        : ownerDistributionId.HasValue ? BankReconciliationAction.MatchOwnerDistribution
        : transferBankTransactionId.HasValue ? BankReconciliationAction.MatchTransfer
        : throw new DomainValidationException("A canonical bank reconciliation target is required.");

    private async Task<BankMatchSuggestionResponse?> LoadSqlRankedSuggestionAsync(
        int portfolioId,
        int transactionId,
        WorkspaceReadScope? scope,
        CancellationToken ct)
    {
        return await RankedSuggestionsQuery(portfolioId, [transactionId], scope)
            .Select(r => new BankMatchSuggestionResponse
            {
                EntityType = r.EntityType,
                EntityId = r.EntityId,
                TenantAccountId = r.TenantAccountId,
                Confidence = r.Confidence > 0.99m ? 0.99m : r.Confidence,
                Label = string.IsNullOrWhiteSpace(r.Label) ? r.EntityType.ToLowerInvariant() : r.Label,
                Reason = r.Reason,
            })
            .SingleOrDefaultAsync(ct);
    }

    private IQueryable<BankSuggestionRankRow> RankedSuggestionsQuery(
        int portfolioId,
        int[]? transactionIds,
        WorkspaceReadScope? scope)
    {
        var receiptEntries = _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId);
        var expenses = _db.Expenses.AsNoTracking()
            .Where(expense => expense.PortfolioId == portfolioId);
        var loanPayments = LoanPaymentEffectiveQuery.From(_db)
            .Where(payment => payment.PortfolioId == portfolioId);
        var ownerDistributions = _db.OwnerDistributions.AsNoTracking()
            .Where(distribution => distribution.PortfolioId == portfolioId);
        if (scope is { } scopedAccess)
        {
            var now = _timeProvider.UtcNow();
            var authorizedProperties = _db.Properties.AsNoTracking()
                .WhereAuthorized(_db, scopedAccess, CapabilityKeys.MoneyReconciliationOperate, now);
            receiptEntries = receiptEntries.Where(entry => authorizedProperties.Any(property =>
                property.Id == entry.TenantAccount!.LeaseManagement!.PropertyId));
            expenses = expenses.WhereMoneyAuthorized(
                _db, scopedAccess, CapabilityKeys.MoneyReconciliationOperate, now);
            var authorizedLoans = _db.Loans.AsNoTracking().WhereMoneyAuthorized(
                _db, scopedAccess, CapabilityKeys.MoneyReconciliationOperate, now);
            loanPayments = loanPayments.Where(payment =>
                authorizedLoans.Any(loan => loan.Id == payment.LoanId));
            ownerDistributions = ownerDistributions.WhereMoneyAuthorized(
                _db, scopedAccess, CapabilityKeys.MoneyReconciliationOperate, now);
        }

        var tenantLedgerCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from entry in receiptEntries
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
                (transactionIds == null || transactionIds.Contains(t.Id)) &&
                t.PortfolioId == portfolioId &&
                t.PropertyId != null &&
                t.MatchStatus == "Unmatched" &&
                entry.PortfolioId == portfolioId &&
                ((entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && entry.Direction == TenantLedgerDirection.Credit
                        && t.Amount > 0m)
                    || (entry.EntryType == TenantLedgerEntryType.TransferIn
                        && ((entry.Direction == TenantLedgerDirection.Credit && t.Amount > 0m)
                            || (entry.Direction == TenantLedgerDirection.Debit && t.Amount < 0m)))
                    || (entry.EntryType == TenantLedgerEntryType.TransferOut
                        && ((entry.Direction == TenantLedgerDirection.Credit && t.Amount > 0m)
                            || (entry.Direction == TenantLedgerDirection.Debit && t.Amount < 0m)))) &&
                entry.Amount >= (t.Amount < 0m ? -t.Amount : t.Amount) - 0.01m &&
                entry.Amount <= (t.Amount < 0m ? -t.Amount : t.Amount) + 0.01m &&
                account.LeaseManagement!.PropertyId == t.PropertyId &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "TenantLedgerEntry",
                EntityId = entry.Id,
                TenantAccountId = entry.TenantAccountId,
                Confidence = hasNameMatch ? dateScore + 0.19m : dateScore,
                Label = entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    ? account.AccountNumber + " payment receipt"
                    : account.AccountNumber + " tenant-account transfer",
                Reason = entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    ? "Deposit amount and date line up with a posted tenant-account receipt."
                    : "Transfer amount and date line up with a posted tenant-account transfer.",
            };

        var expenseCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from e in expenses
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
                (transactionIds == null || transactionIds.Contains(t.Id)) &&
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                t.Amount < 0m &&
                e.PortfolioId == portfolioId &&
                e.DeletedAt == null &&
                (e.Status == ExpenseStatus.Pending ||
                 e.Status == ExpenseStatus.Approved ||
                 e.Status == ExpenseStatus.Paid) &&
                e.Amount >= -t.Amount - 0.01m &&
                e.Amount <= -t.Amount + 0.01m &&
                (e.PropertyId == t.PropertyId
                    || (e.PropertyId == null && e.Unit!.PropertyId == t.PropertyId)
                    || (e.PropertyId == null && e.UnitId == null && e.WorkOrder!.PropertyId == t.PropertyId)
                    || (t.PropertyId == null && e.PropertyId == null
                        && e.UnitId == null && e.WorkOrderId == null)) &&
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

        var loanPaymentCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from payment in loanPayments
            join loan in _db.Loans.AsNoTracking()
                on new { payment.LoanId, payment.PortfolioId }
                equals new { LoanId = loan.Id, loan.PortfolioId }
            let anchor = payment.PaidDate ?? payment.DueDate
            let bankText = ((t.MerchantName ?? "") + " " + t.Description).ToLower()
            let lenderName = loan.Lender.ToLower()
            let hasNameMatch =
                (lenderName != "" && bankText.Contains(lenderName)) ||
                bankText.Contains(payment.PeriodKey.ToLower())
            let dateScore =
                anchor >= t.PostedAt.AddDays(-1) && anchor <= t.PostedAt.AddDays(1) ? 0.84m :
                anchor >= t.PostedAt.AddDays(-2) && anchor <= t.PostedAt.AddDays(2) ? 0.76m :
                anchor >= t.PostedAt.AddDays(-4) && anchor <= t.PostedAt.AddDays(4) ? 0.66m :
                anchor >= t.PostedAt.AddDays(-7) && anchor <= t.PostedAt.AddDays(7) ? 0.56m :
                anchor >= t.PostedAt.AddDays(-14) && anchor <= t.PostedAt.AddDays(14) && hasNameMatch ? 0.46m :
                0m
            where
                (transactionIds == null || transactionIds.Contains(t.Id)) &&
                t.PortfolioId == portfolioId &&
                t.PropertyId != null &&
                t.MatchStatus == "Unmatched" &&
                t.Amount < 0m &&
                payment.Status == LoanPaymentStatus.Paid &&
                payment.TotalAmount >= -t.Amount - 0.01m &&
                payment.TotalAmount <= -t.Amount + 0.01m &&
                loan.PropertyId == t.PropertyId &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "LoanPayment",
                EntityId = payment.Id,
                TenantAccountId = null,
                Confidence = hasNameMatch ? dateScore + 0.15m : dateScore,
                Label = loan.Lender + " " + payment.PeriodKey + " loan payment",
                Reason = "Withdrawal amount, property, and date line up with a paid loan installment.",
            };

        var ownerDistributionCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from distribution in ownerDistributions
            let bankText = ((t.MerchantName ?? "") + " " + t.Description).ToLower()
            let ownerName = distribution.OwnerEntity!.Name.ToLower()
            let hasNameMatch =
                (ownerName != "" && bankText.Contains(ownerName)) ||
                (distribution.BankReference != null &&
                 bankText.Contains(distribution.BankReference.ToLower()))
            let dateScore =
                distribution.Date >= t.PostedAt.AddDays(-1) && distribution.Date <= t.PostedAt.AddDays(1) ? 0.84m :
                distribution.Date >= t.PostedAt.AddDays(-2) && distribution.Date <= t.PostedAt.AddDays(2) ? 0.76m :
                distribution.Date >= t.PostedAt.AddDays(-4) && distribution.Date <= t.PostedAt.AddDays(4) ? 0.66m :
                distribution.Date >= t.PostedAt.AddDays(-7) && distribution.Date <= t.PostedAt.AddDays(7) ? 0.56m :
                distribution.Date >= t.PostedAt.AddDays(-14) && distribution.Date <= t.PostedAt.AddDays(14) && hasNameMatch ? 0.46m :
                0m
            where
                (transactionIds == null || transactionIds.Contains(t.Id)) &&
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                t.Amount < 0m &&
                distribution.Status == OwnerDistributionStatus.Approved &&
                distribution.Amount >= -t.Amount - 0.01m &&
                distribution.Amount <= -t.Amount + 0.01m &&
                (distribution.PropertyId == t.PropertyId ||
                 (distribution.PropertyId == null && t.PropertyId == null)) &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "OwnerDistribution",
                EntityId = distribution.Id,
                TenantAccountId = null,
                Confidence = hasNameMatch ? dateScore + 0.15m : dateScore,
                Label = distribution.OwnerEntity!.Name + " owner distribution",
                Reason = "Withdrawal amount and date line up with an approved owner distribution.",
            };

        var bankTransferCandidates =
            from t in _db.BankTransactions.AsNoTracking()
            from other in _db.BankTransactions.AsNoTracking()
            let dateScore =
                other.PostedAt >= t.PostedAt.AddDays(-1) && other.PostedAt <= t.PostedAt.AddDays(1) ? 0.82m :
                other.PostedAt >= t.PostedAt.AddDays(-2) && other.PostedAt <= t.PostedAt.AddDays(2) ? 0.72m :
                other.PostedAt >= t.PostedAt.AddDays(-3) && other.PostedAt <= t.PostedAt.AddDays(3) ? 0.62m :
                0m
            where
                (transactionIds == null || transactionIds.Contains(t.Id)) &&
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                other.PortfolioId == portfolioId &&
                other.Id != t.Id &&
                other.BankConnectionId != t.BankConnectionId &&
                other.MatchStatus == "Unmatched" &&
                other.Amount >= -t.Amount - 0.01m &&
                other.Amount <= -t.Amount + 0.01m &&
                dateScore > 0m
            select new BankSuggestionRankRow
            {
                TransactionId = t.Id,
                EntityType = "BankTransfer",
                EntityId = other.Id,
                TenantAccountId = null,
                Confidence = dateScore,
                Label = "Transfer to/from " + other.BankConnection!.AccountName,
                Reason = "Equal and opposite statement lines in two accounts line up by date.",
            };

        if (scope is { } transferScope)
        {
            var now = _timeProvider.UtcNow();
            var allPropertyAssignments = _db.AuthorizedAllPropertyAssignments(
                transferScope,
                CapabilityKeys.MoneyReconciliationOperate,
                CapabilityAuthorizationTargetKind.Property,
                now);
            bankTransferCandidates = bankTransferCandidates.Where(_ => allPropertyAssignments.Any());
        }

        // Keep the set operation ahead of the final projection. EF/Npgsql cannot translate an
        // ordered GroupBy winner on top of this UNION ALL, nor can it union two already-ranked
        // projections. Correlating the union to each bank line translates to JOIN LATERAL with
        // ORDER BY / LIMIT 1, so PostgreSQL selects the deterministic winner without materializing
        // candidates in the application or issuing per-row queries.
        var candidates = tenantLedgerCandidates
            .Concat(expenseCandidates)
            .Concat(loanPaymentCandidates)
            .Concat(ownerDistributionCandidates)
            .Concat(bankTransferCandidates);
        return
            from transaction in _db.BankTransactions.AsNoTracking()
            where
                transaction.PortfolioId == portfolioId &&
                (transactionIds == null || transactionIds.Contains(transaction.Id))
            from candidate in candidates
                .Where(candidate => candidate.TransactionId == transaction.Id)
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.EntityType)
                .ThenBy(candidate => candidate.EntityId)
                .Take(1)
            select candidate;
    }

    /// <summary>
    /// Projects each bank line together with its current top suggestion in one translated SQL
    /// statement. Filtering, ordering, paging, ranking, and the left join all remain database-side;
    /// the only client-side work after materialization is copying the already-shaped scalar columns
    /// into the public response DTO.
    /// </summary>
    private IQueryable<BankTransactionSqlRow> TransactionsWithSuggestionsQuery(
        int portfolioId,
        IQueryable<BankTransaction> transactions)
    {
        var rankedSuggestions = RankedSuggestionsQuery(portfolioId, null, null);
        return
            from transaction in transactions.AsNoTracking()
            join suggestion in rankedSuggestions
                on transaction.Id equals suggestion.TransactionId into possibleSuggestions
            from suggestion in possibleSuggestions.DefaultIfEmpty()
            select new BankTransactionSqlRow
            {
                Id = transaction.Id,
                PropertyId = transaction.PropertyId,
                PropertyName = transaction.Property == null ? null : transaction.Property.Name,
                BankConnectionId = transaction.BankConnectionId,
                InstitutionName = transaction.BankConnection == null
                    ? string.Empty
                    : transaction.BankConnection.InstitutionName,
                AccountName = transaction.BankConnection == null
                    ? string.Empty
                    : transaction.BankConnection.AccountName,
                ProviderTransactionId = transaction.ProviderTransactionId,
                PostedAt = transaction.PostedAt,
                AuthorizedAt = transaction.AuthorizedAt,
                Description = transaction.Description,
                MerchantName = transaction.MerchantName,
                Amount = transaction.Amount,
                IsoCurrencyCode = transaction.IsoCurrencyCode,
                Category = transaction.Category,
                MatchedTenantAccountId = transaction.MatchedTenantAccountId,
                MatchedTenantLedgerEntryId = transaction.MatchedTenantLedgerEntryId,
                MatchedExpenseId = transaction.MatchedExpenseId,
                MatchedLoanPaymentId = transaction.MatchedLoanPaymentId,
                MatchedOwnerDistributionId = transaction.MatchedOwnerDistributionId,
                MatchedBankTransactionId = transaction.MatchedBankTransactionId,
                MatchStatus = transaction.MatchStatus,
                MatchConfidence = transaction.MatchConfidence,
                Notes = transaction.Notes,
                UpdatedAt = transaction.UpdatedAt,
                // Flatten the optional SQL row into nullable scalars. Testing the projected CLR
                // object itself for null makes EF construct BankSuggestionRankRow first, which
                // forces left-join NULLs through its non-nullable value properties.
                SuggestionEntityType = suggestion.EntityType,
                SuggestionEntityId = (long?)suggestion.EntityId,
                SuggestionTenantAccountId = suggestion.TenantAccountId,
                SuggestionConfidence = suggestion.Confidence > 0.99m
                    ? 0.99m
                    : (decimal?)suggestion.Confidence,
                SuggestionLabel = suggestion.Label,
                SuggestionReason = suggestion.Reason,
            };
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

    private sealed class BankTransactionSqlRow
    {
        public int Id { get; set; }
        public int? PropertyId { get; set; }
        public string? PropertyName { get; set; }
        public int BankConnectionId { get; set; }
        public string InstitutionName { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string ProviderTransactionId { get; set; } = string.Empty;
        public DateTime PostedAt { get; set; }
        public DateTime? AuthorizedAt { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? MerchantName { get; set; }
        public decimal Amount { get; set; }
        public string IsoCurrencyCode { get; set; } = "USD";
        public string? Category { get; set; }
        public int? MatchedTenantAccountId { get; set; }
        public long? MatchedTenantLedgerEntryId { get; set; }
        public int? MatchedExpenseId { get; set; }
        public int? MatchedLoanPaymentId { get; set; }
        public int? MatchedOwnerDistributionId { get; set; }
        public int? MatchedBankTransactionId { get; set; }
        public string MatchStatus { get; set; } = string.Empty;
        public decimal? MatchConfidence { get; set; }
        public string? Notes { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? SuggestionEntityType { get; set; }
        public long? SuggestionEntityId { get; set; }
        public int? SuggestionTenantAccountId { get; set; }
        public decimal? SuggestionConfidence { get; set; }
        public string? SuggestionLabel { get; set; }
        public string? SuggestionReason { get; set; }
    }

    private sealed class BankReviewQueueSqlRow
    {
        public int Id { get; set; }
        public DateTime PostedAt { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? MerchantName { get; set; }
        public decimal Amount { get; set; }
        public string IsoCurrencyCode { get; set; } = "USD";
        public string? Category { get; set; }
        public string MatchStatus { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
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

    private static BankTransactionResponse MapTransaction(BankTransactionSqlRow row) => new()
    {
        Id = row.Id,
        PropertyId = row.PropertyId,
        PropertyName = row.PropertyName,
        BankConnectionId = row.BankConnectionId,
        InstitutionName = row.InstitutionName,
        AccountName = row.AccountName,
        ProviderTransactionId = row.ProviderTransactionId,
        PostedAt = row.PostedAt,
        AuthorizedAt = row.AuthorizedAt,
        Description = row.Description,
        MerchantName = row.MerchantName,
        Amount = row.Amount,
        IsoCurrencyCode = row.IsoCurrencyCode,
        Category = row.Category,
        MatchedTenantAccountId = row.MatchedTenantAccountId,
        MatchedTenantLedgerEntryId = row.MatchedTenantLedgerEntryId,
        MatchedExpenseId = row.MatchedExpenseId,
        MatchedLoanPaymentId = row.MatchedLoanPaymentId,
        MatchedOwnerDistributionId = row.MatchedOwnerDistributionId,
        MatchedBankTransactionId = row.MatchedBankTransactionId,
        MatchStatus = row.MatchStatus,
        MatchConfidence = row.MatchConfidence,
        Notes = row.Notes,
        UpdatedAt = row.UpdatedAt,
        SuggestedMatch = row.SuggestionEntityType is null || row.SuggestionEntityId is null
            ? null
            : new BankMatchSuggestionResponse
            {
                EntityType = row.SuggestionEntityType,
                EntityId = row.SuggestionEntityId.Value,
                TenantAccountId = row.SuggestionTenantAccountId,
                Confidence = row.SuggestionConfidence ?? 0m,
                Label = string.IsNullOrWhiteSpace(row.SuggestionLabel)
                    ? row.SuggestionEntityType.ToLowerInvariant()
                    : row.SuggestionLabel,
                Reason = row.SuggestionReason ?? string.Empty,
            },
    };

    private static BankTransactionResponse MapTransaction(ReconciledBankTransactionSnapshot row) => new()
    {
        Id = row.Id,
        PropertyId = row.PropertyId,
        PropertyName = row.PropertyName,
        BankConnectionId = row.BankConnectionId,
        InstitutionName = row.InstitutionName,
        AccountName = row.AccountName,
        ProviderTransactionId = row.ProviderTransactionId,
        PostedAt = row.PostedAt,
        AuthorizedAt = row.AuthorizedAt,
        Description = row.Description,
        MerchantName = row.MerchantName,
        Amount = row.Amount,
        IsoCurrencyCode = row.IsoCurrencyCode,
        Category = row.Category,
        MatchedTenantAccountId = row.MatchedTenantAccountId,
        MatchedTenantLedgerEntryId = row.MatchedTenantLedgerEntryId,
        MatchedExpenseId = row.MatchedExpenseId,
        MatchedLoanPaymentId = row.MatchedLoanPaymentId,
        MatchedOwnerDistributionId = row.MatchedOwnerDistributionId,
        MatchedBankTransactionId = row.MatchedBankTransactionId,
        MatchStatus = row.MatchStatus,
        MatchConfidence = row.MatchConfidence,
        Notes = row.Notes,
        UpdatedAt = row.UpdatedAt,
    };

    private static BankReviewQueueItemResponse MapOperationalQueueItem(BankReviewQueueSqlRow row) => new()
    {
        Transaction = new OperationalBankTransactionResponse
        {
            Id = row.Id,
            PostedAt = row.PostedAt,
            Description = row.Description,
            MerchantName = row.MerchantName,
            Amount = row.Amount,
            IsoCurrencyCode = row.IsoCurrencyCode,
            Category = row.Category,
            MatchStatus = row.MatchStatus,
            UpdatedAt = row.UpdatedAt,
        },
        Suggestion = new OperationalBankMatchSuggestionResponse
        {
            Confidence = row.Confidence,
            Label = row.Label,
            Reason = row.Reason,
        },
    };

    private static OperationalBankTransactionResponse MapOperationalTransactionResponse(BankTransactionResponse transaction) => new()
    {
        Id = transaction.Id,
        PostedAt = transaction.PostedAt,
        Description = transaction.Description,
        MerchantName = transaction.MerchantName,
        Amount = transaction.Amount,
        IsoCurrencyCode = transaction.IsoCurrencyCode,
        Category = transaction.Category,
        MatchStatus = transaction.MatchStatus,
        UpdatedAt = transaction.UpdatedAt,
    };

}

public sealed class BankingConflictException(string message) : InvalidOperationException(message);
