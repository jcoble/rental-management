using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Natural-language Q&amp;A over live portfolio data using an LLM with tool-calling.
/// All queries are implicitly scoped to the caller's portfolio — no portfolio id is
/// accepted from external input.
/// </summary>
public interface IPortfolioQaService
{
    Task<AskResponse> AskAsync(
        int portfolioId,
        string question,
        IReadOnlyList<QaTurn>? history,
        CancellationToken ct = default);
}
