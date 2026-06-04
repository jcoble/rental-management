using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Code-defined built-in inspection templates. These are not stored in the database; they are
/// returned alongside any portfolio-custom templates and can be used to materialize an inspection's
/// checklist. Built-in ids are negative so they never collide with DB-generated (positive) ids of
/// custom templates, and callers can detect "this is a built-in" by id sign.
/// </summary>
public static class InspectionTemplateCatalog
{
    /// <summary>All built-in templates, each with a stable negative id and populated <see cref="InspectionTemplate.Items"/>.</summary>
    public static IReadOnlyList<InspectionTemplate> BuiltIns { get; } = BuildBuiltIns();

    /// <summary>Returns the built-in template with the given (negative) id, or null.</summary>
    public static InspectionTemplate? FindBuiltIn(int id) =>
        BuiltIns.FirstOrDefault(t => t.Id == id);

    /// <summary>True when the id refers to a built-in (code-defined) template.</summary>
    public static bool IsBuiltInId(int id) => id < 0;

    private static IReadOnlyList<InspectionTemplate> BuildBuiltIns()
    {
        var moveIn = Template(-1, "Move-In Inspection", InspectionType.MoveIn, new[]
        {
            ("Kitchen", "Sink & faucet"),
            ("Kitchen", "Appliances (stove, fridge, dishwasher)"),
            ("Kitchen", "Cabinets & countertops"),
            ("Kitchen", "Outlets & switches"),
            ("Bathroom", "Toilet"),
            ("Bathroom", "Sink & faucet"),
            ("Bathroom", "Shower/tub & plumbing"),
            ("Bathroom", "Ventilation/exhaust fan"),
            ("Living Areas", "Walls & ceilings"),
            ("Living Areas", "Flooring/carpet"),
            ("Living Areas", "Windows & screens"),
            ("Living Areas", "Doors & locks"),
            ("Bedrooms", "Walls, flooring & closets"),
            ("Bedrooms", "Windows & egress"),
            ("Safety", "Smoke detectors"),
            ("Safety", "Carbon monoxide detectors"),
            ("HVAC", "Heating & cooling operation"),
            ("Exterior", "Entry door & locks"),
        });

        var moveOut = Template(-2, "Move-Out Inspection", InspectionType.MoveOut, new[]
        {
            ("Kitchen", "Sink, faucet & disposal"),
            ("Kitchen", "Appliances cleaned & working"),
            ("Kitchen", "Cabinets & countertops condition"),
            ("Kitchen", "Floor condition"),
            ("Bathroom", "Toilet condition"),
            ("Bathroom", "Shower/tub & grout"),
            ("Bathroom", "Sink & plumbing"),
            ("Bathroom", "Ventilation/exhaust fan"),
            ("Living Areas", "Walls (holes, marks, paint)"),
            ("Living Areas", "Flooring/carpet condition"),
            ("Living Areas", "Windows & screens"),
            ("Bedrooms", "Walls, flooring & closets"),
            ("Safety", "Smoke detectors present & working"),
            ("Safety", "Carbon monoxide detectors present & working"),
            ("General", "Keys & remotes returned"),
            ("General", "Unit cleaned & trash removed"),
        });

        var annualSafety = Template(-3, "Annual Safety Inspection", InspectionType.AnnualSafety, new[]
        {
            ("Safety", "Smoke detectors (test & battery)"),
            ("Safety", "Carbon monoxide detectors (test & battery)"),
            ("Safety", "Fire extinguisher present & charged"),
            ("Safety", "Egress windows & exits clear"),
            ("Electrical", "Outlets & switches"),
            ("Electrical", "GFCI outlets in wet areas"),
            ("Electrical", "Electrical panel condition"),
            ("Plumbing", "Water heater (age & leaks)"),
            ("Plumbing", "Visible leaks under sinks"),
            ("HVAC", "Furnace/AC operation & filter"),
            ("HVAC", "Vents & airflow"),
            ("Structural", "Stairs, railings & decks"),
            ("Structural", "Roof & gutters (visible condition)"),
            ("Exterior", "Locks & exterior doors"),
        });

        return new[] { moveIn, moveOut, annualSafety };
    }

    private static InspectionTemplate Template(int id, string name, InspectionType type, (string Area, string Label)[] items)
    {
        var template = new InspectionTemplate
        {
            Id = id,
            PortfolioId = null,
            Name = name,
            InspectionType = type,
            IsBuiltIn = true,
        };

        var sort = 0;
        foreach (var (area, label) in items)
        {
            template.Items.Add(new InspectionTemplateItem
            {
                TemplateId = id,
                Area = area,
                Label = label,
                SortOrder = sort++,
            });
        }

        return template;
    }
}
