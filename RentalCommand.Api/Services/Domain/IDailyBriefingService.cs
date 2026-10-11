using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IDailyBriefingService
{
    Task<BriefingResponse> ComposeAsync(WorkspaceReadScope scope, CancellationToken ct = default);
}
