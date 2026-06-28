using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Portfolio-owned reusable document template. For TSK-201 the first supported kind is a landlord's
/// exact lease PDF rendered in Overlay mode; later the same model supports drafted/restyled leases and
/// rental applications.
/// </summary>
public class DocumentTemplate : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    public DocumentTemplateKind Kind { get; set; } = DocumentTemplateKind.Lease;
    public DocumentTemplateStatus Status { get; set; } = DocumentTemplateStatus.Draft;
    public DocumentTemplateRenderMode RenderMode { get; set; } = DocumentTemplateRenderMode.Overlay;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Uploaded landlord PDF used by Overlay mode.</summary>
    public int? OriginalStoredFileId { get; set; }

    /// <summary>Editable HTML/content for Restyle mode, introduced after the exact-PDF overlay path.</summary>
    public string? DraftHtml { get; set; }

    /// <summary>Generated source PDF for drafted templates, if one has been compiled.</summary>
    public int? CompiledStoredFileId { get; set; }

    /// <summary>True when this is the portfolio-wide default for its kind.</summary>
    public bool DefaultForPortfolio { get; set; }

    /// <summary>Optional property-specific default; null means portfolio-wide.</summary>
    public int? PropertyId { get; set; }

    /// <summary>Monotonic version incremented whenever template content or fields change.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public StoredFile? OriginalStoredFile { get; set; }
    public StoredFile? CompiledStoredFile { get; set; }
    public List<DocumentTemplateField> Fields { get; set; } = [];
}

