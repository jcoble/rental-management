using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Operations;

/// <summary>
/// Serializes every mutation that can add or change evidence for a work-order progression.
///
/// Global advisory-lock order is: all WorkspaceAccessContext ids in ascending order, then all
/// Appointment ids in ascending order, then all WorkOrder ids in ascending order. Commands that
/// use scope or portfolio advisory locks acquire those before this order. A command that touches
/// more than one id in a class must resolve the complete set before acquiring any lock in that
/// class; no path may acquire these three classes in reverse order.
/// </summary>
public static class WorkOrderProgressionLock
{
    public static async Task AcquireWorkspaceAccessContextsAsync(
        IAtomicCommandContext context,
        CancellationToken ct,
        params int?[] accessContextIds)
    {
        foreach (var accessContextId in accessContextIds
                     .Where(id => id is > 0)
                     .Select(id => id!.Value)
                     .Distinct()
                     .OrderBy(id => id))
        {
            await context.AcquireLockAsync("WorkspaceAccessContext", accessContextId, ct);
        }
    }

    public static Task AcquireAppointmentAsync(
        IAtomicCommandContext context,
        CancellationToken ct,
        int appointmentId) =>
        context.AcquireLockAsync("Appointment", appointmentId, ct);

    public static async Task AcquireAppointmentsAsync(
        IAtomicCommandContext context,
        CancellationToken ct,
        params int?[] appointmentIds)
    {
        foreach (var appointmentId in appointmentIds
                     .Where(id => id is > 0)
                     .Select(id => id!.Value)
                     .Distinct()
                     .OrderBy(id => id))
        {
            await AcquireAppointmentAsync(context, ct, appointmentId);
        }
    }

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
