using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Services;

namespace RentalCommand.Api.Services.Domain;

public interface ICapitalAssetService
{
    Task<IReadOnlyList<CapitalAssetResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, CapitalAssetListQuery query, CancellationToken ct = default);
    Task<CapitalAssetListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, CapitalAssetListQuery query, CancellationToken ct = default);
    Task<CapitalAssetResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, int? depreciationYear = null, CancellationToken ct = default);
    Task<CapitalAssetResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateCapitalAssetRequest request, string operationKey,
        CancellationToken ct = default);
    Task<CapitalAssetResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateCapitalAssetRequest request, string operationKey,
        CancellationToken ct = default);
    Task<CapitalAssetResponse?> CapitalizeExpenseAuthorizedAsync(
        WorkspaceReadScope scope, int expenseId, CapitalizeExpenseRequest request, string operationKey,
        CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, string operationKey, CancellationToken ct = default);

    Task<IReadOnlyList<CapitalAssetResponse>> ListAsync(
        int portfolioId, CapitalAssetListQuery query, CancellationToken ct = default);

    Task<CapitalAssetListResponse> ListPageAsync(
        int portfolioId, CapitalAssetListQuery query, CancellationToken ct = default);

    Task<CapitalAssetResponse?> GetAsync(
        int portfolioId, int id, int? depreciationYear = null, CancellationToken ct = default);

    DepreciationResult AnnualDepreciationForYear(CapitalAsset asset, int year);
}
