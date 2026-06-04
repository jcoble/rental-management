using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant screening with an FCRA flow on top of a submitted <see cref="Core.Entities.RentalApplication"/>.
/// All operations are portfolio-scoped by the caller's JWT claim. Screening is never run without recorded
/// FCRA consent, and the screening provider is gated (a clear "not configured" result, never a false
/// "passed"). A declined+screened application can produce an FCRA adverse-action notice.
/// </summary>
public interface IScreeningService
{
    /// <summary>
    /// Requests a screening for the application. Returns <c>null</c> when the application is not found in
    /// the portfolio. Throws <see cref="ConsentRequiredException"/> when FCRA consent was not recorded,
    /// and <see cref="ScreeningNotConfiguredException"/> when no screening provider is configured. On
    /// success a <see cref="Core.Entities.ScreeningResult"/> is created and the application moves to
    /// UnderReview.
    /// </summary>
    Task<ScreeningResultResponse?> RequestScreeningAsync(int portfolioId, int applicationId, int userId, CancellationToken ct = default);

    /// <summary>Lists the screening result(s) for an application, newest first. <c>null</c> when the application is not found.</summary>
    Task<IReadOnlyList<ScreeningResultResponse>?> GetScreeningResultsAsync(int portfolioId, int applicationId, CancellationToken ct = default);

    /// <summary>
    /// Generates an FCRA adverse-action notice PDF for the application, stores it as a StoredFile and an
    /// <see cref="Core.Entities.AdverseActionNotice"/> row, and optionally emails it to the applicant.
    /// Returns <c>null</c> when the application is not found in the portfolio.
    /// </summary>
    Task<AdverseActionNoticeResponse?> GenerateAdverseActionAsync(
        int portfolioId, int applicationId, int userId, GenerateAdverseActionRequest request, CancellationToken ct = default);
}

/// <summary>Thrown when screening is requested for an application that did not record FCRA consent.</summary>
public sealed class ConsentRequiredException : Exception
{
    public ConsentRequiredException() : base("FCRA consent required") { }
}

/// <summary>Thrown when screening is requested but no screening provider is configured (gated).</summary>
public sealed class ScreeningNotConfiguredException : Exception
{
    public ScreeningNotConfiguredException() : base("screening not configured") { }
}
