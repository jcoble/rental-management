using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Rental-application intake and review. Public (no-login) operations resolve the portfolio by the
/// link token only; authed operations are portfolio-scoped by the caller's JWT claim. Approving an
/// application creates a real <see cref="Core.Entities.Tenant"/>.
/// </summary>
public interface IApplicationService
{
    // --- Public (token-resolved) ---

    /// <summary>
    /// Returns the minimal form info (company name + property/unit options) for a public link token,
    /// or <c>null</c> when the token does not resolve to a portfolio accepting applications.
    /// </summary>
    Task<PublicApplicationFormInfo?> GetPublicFormInfoAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Creates a Submitted application in the token's portfolio. Returns <c>null</c> when the token is
    /// invalid. Property/unit references must resolve to one consistent pair in that portfolio.
    /// </summary>
    Task<SubmitApplicationResult?> SubmitAsync(
        string token, SubmitApplicationRequest request, string? ipAddress, string operationKey,
        CancellationToken ct = default);

    // --- Authed (portfolio-scoped) ---

    /// <summary>
    /// Creates a Submitted application directly in the given portfolio from a landlord-scanned paper
    /// application (the scan-IN counterpart of <see cref="SubmitAsync"/>). PropertyId/UnitId on the request
    /// must resolve to one consistent pair in this portfolio; invalid references are rejected.
    /// Returns the created application as an <see cref="ApplicationResponse"/>.
    /// </summary>
    Task<ApplicationResponse> CreateFromScanAsync(
        WorkspaceReadScope scope,
        CreateApplicationRequest request,
        string operationKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<ApplicationResponse>> ListAsync(
        int portfolioId, string? status, ListQuery query, int? unitId = null, CancellationToken ct = default);

    Task<IReadOnlyList<ApplicationResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, string? status, ListQuery query, int? unitId = null,
        CancellationToken ct = default);

    Task<ApplicationListResponse> ListPageAsync(
        int portfolioId, string? status, ListQuery query, int? unitId = null, CancellationToken ct = default);

    Task<ApplicationListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, string? status, ListQuery query, int? unitId = null,
        CancellationToken ct = default);

    Task<ApplicationResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<ApplicationResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);

    /// <summary>
    /// Corrects landlord-editable applicant details while the application is still Submitted or
    /// UnderReview. Returns <c>null</c> when the application is not found in the portfolio.
    /// </summary>
    Task<ApplicationResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateApplicationRequest request,
        int userId,
        string operationKey,
        CancellationToken ct = default);

    /// <summary>
    /// Approves an application and creates a Tenant from its data. Returns <c>null</c> when the
    /// application is not found in the portfolio; throws <see cref="InvalidOperationException"/> when
    /// it is not in an approvable state.
    /// </summary>
    Task<ApproveApplicationResult?> ApproveAuthorizedAsync(
        WorkspaceReadScope scope, int id, int userId, string operationKey, CancellationToken ct = default);

    Task<ApplicationResponse?> DeclineAuthorizedAsync(
        WorkspaceReadScope scope, int id, int userId, string? reason, string operationKey, CancellationToken ct = default);

    Task<ApplicationResponse?> WithdrawAuthorizedAsync(
        WorkspaceReadScope scope, int id, int userId, string operationKey, CancellationToken ct = default);

    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, int userId, string operationKey, CancellationToken ct = default);

    /// <summary>Generates or rotates the portfolio's public application token and returns the apply link.</summary>
    Task<ApplicationLinkResult> GenerateLinkAsync(int portfolioId, CancellationToken ct = default);
}
