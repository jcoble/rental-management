using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IDailyBriefingService
{
    Task<BriefingResponse> ComposeAsync(int portfolioId, CancellationToken ct = default);
}
