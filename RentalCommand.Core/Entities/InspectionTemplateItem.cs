namespace RentalCommand.Core.Entities;

/// <summary>One row in an <see cref="InspectionTemplate"/>: an area/room plus the item to check.</summary>
public class InspectionTemplateItem
{
    public int Id { get; set; }
    public int TemplateId { get; set; }

    public string Area { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public InspectionTemplate? Template { get; set; }
}
