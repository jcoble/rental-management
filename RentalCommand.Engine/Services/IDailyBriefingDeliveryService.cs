namespace RentalCommand.Engine.Services;

public interface IDailyBriefingDeliveryService
{
    Task<int> EnqueueDueAsync(DateTime? utcNow = null, CancellationToken ct = default);
}
