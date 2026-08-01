namespace RentalCommand.Engine.Workers;

public interface IScanProcessingCycleService
{
    Task<int> RunOneCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken);
}
