using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Domain;

public interface IScreeningService
{
    Task<ScreeningWorkspaceResponse?> GetWorkspaceAsync(
        WorkspaceReadScope scope, int applicationId, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> StartIntegratedAsync(
        WorkspaceReadScope scope, int applicationId,
        StartIntegratedScreeningRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> TrackExternalAsync(
        WorkspaceReadScope scope, int applicationId,
        TrackExternalScreeningRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> UpdateExternalAsync(
        WorkspaceReadScope scope, int applicationId, int screeningId,
        UpdateExternalScreeningRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> RecordDecisionAsync(
        WorkspaceReadScope scope, int applicationId, int screeningId,
        RecordScreeningDecisionRequest request, CancellationToken ct = default);

    Task<ApplicantScreeningResponse?> ApplyProviderDeliveryAsync(
        ScreeningProviderStatusDelivery delivery, CancellationToken ct = default);

    Task<AdverseActionNoticeResponse?> GenerateAdverseActionAsync(
        WorkspaceReadScope scope, int applicationId,
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
