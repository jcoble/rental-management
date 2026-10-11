namespace RentalCommand.Engine.Services;

/// <summary>Creates due canonical tenant-notice work items from lease-ending policy.</summary>
public interface ITenantNoticeCandidateGenerationService
{
    Task<int> GenerateDueAsync(CancellationToken ct = default);
}
