using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A reusable checklist definition for a kind of inspection. Built-in templates are code-defined
/// (see <c>InspectionTemplateCatalog</c>) and surface with a null <see cref="PortfolioId"/> and
/// <see cref="IsBuiltIn"/> = true; custom templates are portfolio-scoped DB rows.
/// </summary>
public class InspectionTemplate
{
    public int Id { get; set; }

    /// <summary>Null for built-in (system) templates; set for portfolio-owned custom templates.</summary>
    public int? PortfolioId { get; set; }

    public string Name { get; set; } = string.Empty;
    public InspectionType InspectionType { get; set; } = InspectionType.Routine;

    /// <summary>True for code-defined system defaults (not editable, shared across all portfolios).</summary>
    public bool IsBuiltIn { get; set; }

    public List<InspectionTemplateItem> Items { get; set; } = [];

    public Portfolio? Portfolio { get; set; }
}
