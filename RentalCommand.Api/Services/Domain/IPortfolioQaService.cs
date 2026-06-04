using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Natural-language Q&amp;A over live portfolio data using an LLM with tool-calling.
/// All queries are implicitly scoped to the caller's portfolio — no portfolio id is
/// accepted from external input.
/// </summary>
public interface IPortfolioQaService
{
    /// <param name="delivery">
    /// Optional email/SMS delivery of the answer. When omitted, the answer is only returned in the
    /// response. Recipient defaults are resolved by the caller (e.g. the signed-in user's email).
    /// </param>
    Task<AskResponse> AskAsync(
        int portfolioId,
        string question,
        IReadOnlyList<QaTurn>? history,
        QaDeliveryOptions? delivery = null,
        CancellationToken ct = default);
}
