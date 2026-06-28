using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One merge/signing anchor on a document template. Coordinates are normalized percentages with a
/// top-left origin so browser placement and PDF rendering share the same contract.
/// </summary>
public class DocumentTemplateField
{
    public int Id { get; set; }
    public int DocumentTemplateId { get; set; }

    public string FieldKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DocumentTemplateFieldKind Kind { get; set; } = DocumentTemplateFieldKind.Text;
    public DocumentTemplateSignerRole SignerRole { get; set; } = DocumentTemplateSignerRole.None;

    public int PageNumber { get; set; } = 1;
    public double XPct { get; set; }
    public double YPct { get; set; }
    public double WidthPct { get; set; } = 0.12;
    public double HeightPct { get; set; } = 0.03;

    public bool Required { get; set; }
    public bool Locked { get; set; }
    public int SortOrder { get; set; }
    public string? DefaultText { get; set; }

    public DocumentTemplate? DocumentTemplate { get; set; }
}

