using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A bulk-scan batch: the landlord uploads MANY documents at once (e.g. importing every lease PDF
/// during migration) and each file becomes a <see cref="ScanDraft"/> linked back here by
/// <see cref="ScanDraft.BatchId"/>. The batch gives the review UI a single queue to confirm each
/// draft into a real record (a Lease, by default). The Engine worker processes the drafts the same
/// way it processes any other Pending draft — the batch is pure orchestration on top.
/// </summary>
public class ScanBatch
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>Optional human label for the batch (e.g. "2026 lease imports"); null when not supplied.</summary>
    public string? Name { get; set; }

    /// <summary>The entity every draft in the batch targets. Defaults to "Lease" (the migration on-ramp).</summary>
    public string TargetEntityType { get; set; } = "Lease";

    public ScanBatchStatus Status { get; set; } = ScanBatchStatus.Processing;

    /// <summary>Number of files accepted into the batch (one draft per file).</summary>
    public int FileCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
}
