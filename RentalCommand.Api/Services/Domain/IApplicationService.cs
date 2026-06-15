using RentalCommand.Api.DTOs;

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
    /// invalid. The PropertyId/UnitId on the request are validated to be in that portfolio (ignored
    /// if not). The caller supplies the request IP for the FCRA consent record.
    /// </summary>
    Task<SubmitApplicationResult?> SubmitAsync(
        string token, SubmitApplicationRequest request, string? ipAddress, CancellationToken ct = default);

    // --- Authed (portfolio-scoped) ---

    Task<IReadOnlyList<ApplicationResponse>> ListAsync(
        int portfolioId, string? status, ListQuery query, CancellationToken ct = default);

    Task<ApplicationResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Approves an application and creates a Tenant from its data. Returns <c>null</c> when the
    /// application is not found in the portfolio; throws <see cref="InvalidOperationException"/> when
    /// it is not in an approvable state.
    /// </summary>
    Task<ApproveApplicationResult?> ApproveAsync(int portfolioId, int id, int userId, CancellationToken ct = default);

    Task<ApplicationResponse?> DeclineAsync(int portfolioId, int id, int userId, string? reason, CancellationToken ct = default);

    Task<ApplicationResponse?> WithdrawAsync(int portfolioId, int id, int userId, CancellationToken ct = default);

    /// <summary>Generates or rotates the portfolio's public application token and returns the apply link.</summary>
    Task<ApplicationLinkResult> GenerateLinkAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Returns the portfolio's editable application-form config (normalized; defaults when unset) plus
    /// the property/unit options the landlord can choose a default from.
    /// </summary>
    Task<FormConfigEditorResponse> GetFormConfigAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Validates and saves the landlord's application-form config onto the portfolio. Throws
    /// <see cref="ApplicationFormConfigValidationException"/> when the config is invalid (e.g. a default
    /// property/unit not in the portfolio, a dropdown with no choices). Returns the normalized saved config.
    /// </summary>
    Task<ApplicationFormConfigDto> SaveFormConfigAsync(
        int portfolioId, int userId, SaveFormConfigRequest request, CancellationToken ct = default);
}
