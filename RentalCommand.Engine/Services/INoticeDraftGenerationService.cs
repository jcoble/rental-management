namespace RentalCommand.Engine.Services;

/// <summary>
/// Engine-side driver for the durable tenant-notice work queue. One set-based PostgreSQL command
/// produces the claimed batch's exact drafts. Draft policies stop after editable copy; Auto policies
/// atomically render, queue enabled destinations, and complete the fenced work claim.
/// </summary>
public interface INoticeDraftGenerationService
{
    /// <summary>Process one claimed batch. Returns the number of newly inserted drafts.</summary>
    Task<int> GenerateAllAsync(CancellationToken ct = default);
}
