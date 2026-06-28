using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Reusable document template returned to the management UI.</summary>
public sealed class DocumentTemplateResponse
{
    public int Id { get; init; }
    public int PortfolioId { get; init; }
    public DocumentTemplateKind Kind { get; init; }
    public DocumentTemplateStatus Status { get; init; }
    public DocumentTemplateRenderMode RenderMode { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int? OriginalStoredFileId { get; init; }
    public int? CompiledStoredFileId { get; init; }
    public bool HasDraftHtml { get; init; }
    public bool DefaultForPortfolio { get; init; }
    public int? PropertyId { get; init; }
    public int Version { get; init; }
    public int FieldCount { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public DateTime? ArchivedAtUtc { get; init; }
    public IReadOnlyList<DocumentTemplateFieldResponse> Fields { get; init; } = [];
    public string TestId => $"document-template-{Id}";

    public static DocumentTemplateResponse FromEntity(
        DocumentTemplate template,
        IReadOnlyList<DocumentTemplateField>? fields = null,
        int? fieldCount = null)
    {
        var fieldList = fields ?? template.Fields;
        return new()
        {
            Id = template.Id,
            PortfolioId = template.PortfolioId,
            Kind = template.Kind,
            Status = template.Status,
            RenderMode = template.RenderMode,
            Name = template.Name,
            Description = template.Description,
            OriginalStoredFileId = template.OriginalStoredFileId,
            CompiledStoredFileId = template.CompiledStoredFileId,
            HasDraftHtml = !string.IsNullOrWhiteSpace(template.DraftHtml),
            DefaultForPortfolio = template.DefaultForPortfolio,
            PropertyId = template.PropertyId,
            Version = template.Version,
            FieldCount = fieldCount ?? fieldList.Count,
            CreatedAtUtc = template.CreatedAtUtc,
            UpdatedAtUtc = template.UpdatedAtUtc,
            ArchivedAtUtc = template.ArchivedAtUtc,
            Fields = fieldList
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Id)
                .Select(DocumentTemplateFieldResponse.FromEntity)
                .ToList(),
        };
    }
}

public sealed class DocumentTemplateFieldResponse
{
    public int Id { get; init; }
    public int DocumentTemplateId { get; init; }
    public string FieldKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public DocumentTemplateFieldKind Kind { get; init; }
    public DocumentTemplateSignerRole SignerRole { get; init; }
    public int PageNumber { get; init; }
    public double XPct { get; init; }
    public double YPct { get; init; }
    public double WidthPct { get; init; }
    public double HeightPct { get; init; }
    public bool Required { get; init; }
    public bool Locked { get; init; }
    public int SortOrder { get; init; }
    public string? DefaultText { get; init; }
    public string TestId => $"document-template-field-{Id}";

    public static DocumentTemplateFieldResponse FromEntity(DocumentTemplateField field) => new()
    {
        Id = field.Id,
        DocumentTemplateId = field.DocumentTemplateId,
        FieldKey = field.FieldKey,
        Label = field.Label,
        Kind = field.Kind,
        SignerRole = field.SignerRole,
        PageNumber = field.PageNumber,
        XPct = field.XPct,
        YPct = field.YPct,
        WidthPct = field.WidthPct,
        HeightPct = field.HeightPct,
        Required = field.Required,
        Locked = field.Locked,
        SortOrder = field.SortOrder,
        DefaultText = field.DefaultText,
    };
}

public sealed class DocumentTemplateListResponse
{
    public IReadOnlyList<DocumentTemplateResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class CreateDocumentTemplateRequest
{
    public DocumentTemplateKind Kind { get; set; } = DocumentTemplateKind.Lease;
    public DocumentTemplateRenderMode RenderMode { get; set; } = DocumentTemplateRenderMode.Overlay;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public int? OriginalStoredFileId { get; set; }
    public int? CompiledStoredFileId { get; set; }
    public int? PropertyId { get; set; }
    public bool DefaultForPortfolio { get; set; }

    public string? DraftHtml { get; set; }
}

public sealed class UpdateDocumentTemplateRequest
{
    public DocumentTemplateStatus? Status { get; set; }
    public DocumentTemplateRenderMode? RenderMode { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    public int? OriginalStoredFileId { get; set; }
    public int? CompiledStoredFileId { get; set; }
    public int? PropertyId { get; set; }
    public bool? DefaultForPortfolio { get; set; }
    public string? DraftHtml { get; set; }
}

public sealed class CreateDocumentTemplateFieldRequest
{
    [Required]
    [MaxLength(120)]
    public string FieldKey { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Label { get; set; }

    public DocumentTemplateFieldKind Kind { get; set; } = DocumentTemplateFieldKind.Text;
    public DocumentTemplateSignerRole SignerRole { get; set; } = DocumentTemplateSignerRole.None;

    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [Range(0, 1)]
    public double XPct { get; set; }

    [Range(0, 1)]
    public double YPct { get; set; }

    [Range(0.0001, 1)]
    public double WidthPct { get; set; } = 0.12;

    [Range(0.0001, 1)]
    public double HeightPct { get; set; } = 0.03;

    public bool Required { get; set; }
    public bool Locked { get; set; }
    public int SortOrder { get; set; }

    [MaxLength(500)]
    public string? DefaultText { get; set; }
}

public sealed class UpdateDocumentTemplateFieldRequest
{
    [MaxLength(120)]
    public string? FieldKey { get; set; }

    [MaxLength(200)]
    public string? Label { get; set; }

    public DocumentTemplateFieldKind? Kind { get; set; }
    public DocumentTemplateSignerRole? SignerRole { get; set; }

    [Range(1, int.MaxValue)]
    public int? PageNumber { get; set; }

    [Range(0, 1)]
    public double? XPct { get; set; }

    [Range(0, 1)]
    public double? YPct { get; set; }

    [Range(0.0001, 1)]
    public double? WidthPct { get; set; }

    [Range(0.0001, 1)]
    public double? HeightPct { get; set; }

    public bool? Required { get; set; }
    public bool? Locked { get; set; }
    public int? SortOrder { get; set; }

    [MaxLength(500)]
    public string? DefaultText { get; set; }
}

public sealed record DocumentTemplateFieldCatalogItemResponse(
    string FieldKey,
    string Label,
    DocumentTemplateFieldKind Kind,
    DocumentTemplateSignerRole SignerRole,
    bool RequiredForSignature,
    string Description);
