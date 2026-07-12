using RentalCommand.Api.DTOs;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Domain;

public interface IScreeningService
{
    Task<ScreeningWorkspaceResponse?> GetWorkspaceAsync(
        int portfolioId, int applicationId, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> StartIntegratedAsync(
        int portfolioId, int applicationId, int userId,
        StartIntegratedScreeningRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> TrackExternalAsync(
        int portfolioId, int applicationId, int userId,
        TrackExternalScreeningRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> UpdateExternalAsync(
        int portfolioId, int applicationId, int screeningId,
        UpdateExternalScreeningRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> RecordDecisionAsync(
        int portfolioId, int applicationId, int screeningId, int userId,
        RecordScreeningDecisionRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> ApplyProviderDeliveryAsync(
        ScreeningProviderStatusDelivery delivery, CancellationToken ct = default);

    Task<AdverseActionNoticeResponse?> GenerateAdverseActionAsync(
        int portfolioId, int applicationId, int userId,
        GenerateAdverseActionRequest request, CancellationToken ct = default);
}

public sealed class ConsentRequiredException : Exception
{
    public ConsentRequiredException() : base("Applicant screening consent is required.") { }
}

public sealed class ScreeningNotConfiguredException : Exception
{
    public ScreeningNotConfiguredException() : base("No integrated screening provider is configured. Track an external screening instead.") { }
}
