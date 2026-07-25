using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface ITenantAccountQueryService
{
    Task<TenantAccountPageResponse> ListAccountsPageAsync(
        WorkspaceReadScope scope,
        TenantAccountListQuery query,
        CancellationToken ct = default);

    Task<TenantLedgerEntryGlobalPageResponse> ListEntriesPageAsync(
        WorkspaceReadScope scope,
        TenantLedgerEntryGlobalListQuery query,
        CancellationToken ct = default);

    Task<TenantAccountDepositPageResponse> ListDepositsPageAsync(
        WorkspaceReadScope scope,
        TenantAccountDepositListQuery query,
        CancellationToken ct = default);

    Task<TenantAccountDetailResponse?> GetAsync(
        WorkspaceReadScope scope, int tenantAccountId, CancellationToken ct = default);

    Task<TenantLedgerEntryPageResponse?> ListEntriesPageAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantLedgerEntryListQuery query,
        CancellationToken ct = default);

    Task<TenantLedgerEntryDetailResponse?> GetEntryAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        long tenantLedgerEntryId,
        CancellationToken ct = default);

    Task<TenantChargePageResponse?> ListChargesPageAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantChargeListQuery query,
        CancellationToken ct = default);

    Task<TenantAccountDepositResponse?> GetDepositAsync(
        WorkspaceReadScope scope, int tenantAccountId, CancellationToken ct = default);
}
