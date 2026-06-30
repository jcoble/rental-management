using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IAssistantActionService
{
    Task<AssistantActionDraftResponse> DraftAsync(
        int portfolioId,
        AssistantActionDraftRequest request,
        CancellationToken ct = default);

    Task<AssistantActionExecuteResponse> ExecuteAsync(
        int portfolioId,
        AssistantActionExecuteRequest request,
        CancellationToken ct = default);
}
