using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IBankingService
{
    Task<BankingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default);
    Task<PlaidSettingsResponse> GetPlaidSettingsAsync(int portfolioId, CancellationToken ct = default);
    Task<PlaidLinkTokenResponse> CreatePlaidLinkTokenAsync(int portfolioId, int userId, string? platform, CancellationToken ct = default);
    Task<BankConnectionResponse> ExchangePlaidPublicTokenAsync(int portfolioId, ExchangePlaidPublicTokenRequest request, CancellationToken ct = default);
    Task<SyncBankConnectionResponse?> SyncPlaidConnectionAsync(int portfolioId, int connectionId, CancellationToken ct = default);
    Task<IReadOnlyList<BankConnectionResponse>> ListConnectionsAsync(int portfolioId, CancellationToken ct = default);
    Task<IReadOnlyList<BankTransactionResponse>> ListTransactionsAsync(int portfolioId, string? status, CancellationToken ct = default);
    Task<ImportBankTransactionsResponse> ImportAsync(int portfolioId, ImportBankTransactionsRequest request, CancellationToken ct = default);
    Task<BankTransactionResponse?> MatchAsync(int portfolioId, int transactionId, MatchBankTransactionRequest request, CancellationToken ct = default);
    Task<BankTransactionResponse?> ClearMatchAsync(int portfolioId, int transactionId, CancellationToken ct = default);
}
