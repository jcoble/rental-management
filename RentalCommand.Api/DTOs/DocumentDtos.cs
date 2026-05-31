namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Core.Entities.StoredFile"/> via the Documents hub.</summary>
public sealed class DocumentDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int? EntityId { get; set; }

    /// <summary>True when the content type indicates an image (used by UI to pick a preview vs icon).</summary>
    public bool IsImage { get; set; }

    public DateTime UploadedAt { get; set; }

    /// <summary>Optional caller-supplied label (e.g. "signed_lease", "photo"). Not persisted as a
    /// separate column — the Documents hub does not currently store category on the row. Callers
    /// should treat this as informational only until a migration adds a Category column.</summary>
    public string? Category { get; set; }
}
