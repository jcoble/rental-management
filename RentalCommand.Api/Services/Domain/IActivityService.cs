using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only access to the portfolio's append-only <see cref="Core.Entities.ActivityLog"/> feed. Every
/// query is filtered by the caller's portfolio id; there are no create/update/delete operations.
/// </summary>
public interface IActivityService
{
    Task<IReadOnlyList<ActivityResponse>> ListAsync(
        int portfolioId, RentalActivityType? type, string? entityType, int? entityId, ListQuery query, CancellationToken ct = default);
}
