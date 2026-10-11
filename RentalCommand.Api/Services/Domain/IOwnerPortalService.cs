using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Database-validated coordinates for one relationship-scoped Owner read.</summary>
public readonly record struct OwnerPortalReadScope(
    int PortfolioId,
    int UserId,
    int AccessContextId,
    long AccessRevision);

public interface IOwnerPortalService
{
    Task<OwnerPortalOverviewResponse?> GetOverviewAsync(
        OwnerPortalReadScope scope, CancellationToken ct = default);

    Task<OwnerPortalPropertyPageResponse> ListPropertiesPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default);

    Task<OwnerPortalDistributionPageResponse> ListDistributionsPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default);

    Task<OwnerPortalItemPageResponse> ListApprovalsPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default);

    Task<OwnerPortalItemPageResponse> ListMessagesPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default);
}
