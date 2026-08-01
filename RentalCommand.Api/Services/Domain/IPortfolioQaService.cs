using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Natural-language Q&amp;A over live portfolio data using an LLM with tool-calling.
/// Every live-data query receives the server-validated workspace scope and intersects its own
/// capability with assigned properties in SQL. No portfolio or property scope is accepted from
/// external input.
/// </summary>
public interface IPortfolioQaService
{
    /// <param name="delivery">
    /// Optional email/SMS delivery of the answer. When omitted, the answer is only returned in the
    /// response. Recipient defaults are resolved by the caller (e.g. the signed-in user's email).
    /// </param>
    Task<AskResponse> AskAsync(
        WorkspaceReadScope scope,
        string question,
        IReadOnlyList<QaTurn>? history,
        QaDeliveryOptions? delivery = null,
        string? deliveryOperationId = null,
        CancellationToken ct = default);
}
