using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Operations;

/// <summary>
/// Serializes every mutation that can add or change evidence for a work-order progression.
///
/// Commands that use scope or portfolio advisory locks acquire those before this helper. When a
/// command touches more than one work order, this helper acquires all work-order locks in
/// ascending id order so two commands cannot deadlock while resolving the same set differently.
/// </summary>
public static class WorkOrderProgressionLock
{
    public static async Task AcquireAsync(
        IAtomicCommandContext context,
        CancellationToken ct,
        params int?[] workOrderIds)
    {
        foreach (var workOrderId in workOrderIds
                     .Where(id => id is > 0)
                     .Select(id => id!.Value)
                     .Distinct()
                     .OrderBy(id => id))
        {
            await context.AcquireLockAsync("WorkOrder", workOrderId, ct);
        }
    }
}
