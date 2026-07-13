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
        WorkspaceReadScope scope, CreateCapitalAssetRequest request, CancellationToken ct = default);
    Task<CapitalAssetResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateCapitalAssetRequest request, CancellationToken ct = default);
    Task<CapitalAssetResponse?> CapitalizeExpenseAuthorizedAsync(
        WorkspaceReadScope scope, int expenseId, CapitalizeExpenseRequest request, CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);

    Task<IReadOnlyList<CapitalAssetResponse>> ListAsync(
        int portfolioId, CapitalAssetListQuery query, CancellationToken ct = default);

    Task<CapitalAssetListResponse> ListPageAsync(
        int portfolioId, CapitalAssetListQuery query, CancellationToken ct = default);

    Task<CapitalAssetResponse?> GetAsync(
        int portfolioId, int id, int? depreciationYear = null, CancellationToken ct = default);

    Task<CapitalAssetResponse?> CreateAsync(
        int portfolioId, CreateCapitalAssetRequest request, CancellationToken ct = default);

    Task<CapitalAssetResponse?> UpdateAsync(
        int portfolioId, int id, UpdateCapitalAssetRequest request, CancellationToken ct = default);

    Task<CapitalAssetResponse?> CapitalizeExpenseAsync(
        int portfolioId, int expenseId, CapitalizeExpenseRequest request, CancellationToken ct = default);

    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);

    DepreciationResult AnnualDepreciationForYear(CapitalAsset asset, int year);
}
