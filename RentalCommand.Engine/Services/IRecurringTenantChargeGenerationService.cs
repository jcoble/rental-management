namespace RentalCommand.Engine.Services;

/// <summary>
/// Materializes due recurring tenant-charge schedules through the atomic command path.
/// </summary>
public interface IRecurringTenantChargeGenerationService
{
    Task<int> GenerateAsync(CancellationToken ct = default);
}
