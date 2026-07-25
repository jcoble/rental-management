using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Polymorphic attachment record. EntityId is bigint-compatible because canonical ledger entries use
/// a 64-bit identity while the remaining document targets safely widen their 32-bit ids.
/// </summary>
public class StoredFile : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? EntityType { get; set; }
    public long? EntityId { get; set; }
    public DateTime UploadedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
