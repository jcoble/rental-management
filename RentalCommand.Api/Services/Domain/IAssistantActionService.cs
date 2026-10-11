using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IAssistantActionService
{
    Task<AssistantActionDraftResponse> DraftAsync(
        WorkspaceReadScope scope,
        AssistantActionDraftRequest request,
        CancellationToken ct = default);

    Task<AssistantActionExecuteResponse> ExecuteAsync(
        WorkspaceReadScope scope,
        AssistantActionExecuteRequest request,
        string idempotencyKey,
        CancellationToken ct = default);
}
