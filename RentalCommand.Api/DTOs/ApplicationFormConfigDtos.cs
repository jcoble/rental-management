using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

// ---------------------------------------------------------------------------
// Application-form configuration — the contract shared by the public form and
// the landlord configurator. The raw landlord JSON lives in
// Portfolio.ApplicationFormConfig; these DTOs are the *normalized* shape both
// sides exchange so neither the web client nor the public form ever parses raw
// landlord JSON. Normalization (and validation) is centralized in
// ApplicationFormConfigParser.
// ---------------------------------------------------------------------------

/// <summary>Allowed custom-field input kinds. Kept as plain strings to match the JSON contract.</summary>
public static class CustomFieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string YesNo = "yesno";
    public const string Select = "select";

    public static readonly IReadOnlyList<string> All = new[] { Text, Number, YesNo, Select };

    public static bool IsValid(string? type) => type is not null && All.Contains(type);
}

/// <summary>The repeatable employer/income section toggle.</summary>
public sealed class IncomeSourcesConfig
{
    /// <summary>When true the applicant may add more than one employer/income row. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>The pets section configuration.</summary>
public sealed class PetsConfig
{
    /// <summary>When true the form asks whether the applicant has pets (and collects pet rows). Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>When true the form mentions a pet deposit may apply (informational only). Defaults to false.</summary>
    public bool AskDeposit { get; set; }
}

/// <summary>A single landlord-authored custom question on the application form.</summary>
public sealed class CustomFieldConfig
{
    /// <summary>Stable id used as the answer key. Server-generated when the landlord omits it.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The question shown to the applicant.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>One of <see cref="CustomFieldTypes"/> (text | number | yesno | select).</summary>
    public string Type { get; set; } = CustomFieldTypes.Text;

    /// <summary>Whether the applicant must answer.</summary>
    public bool Required { get; set; }

    /// <summary>The choices for a <c>select</c> field; empty for other types.</summary>
    public IReadOnlyList<string> Options { get; set; } = [];
}

/// <summary>Pre-filled defaults the landlord sets for the apply link.</summary>
public sealed class FormDefaultsConfig
{
    /// <summary>Default property pre-selected on the form; null for none.</summary>
    public int? PropertyId { get; set; }

    /// <summary>Default unit pre-selected on the form; null for none.</summary>
    public int? UnitId { get; set; }

    /// <summary>Default desired move-in date (yyyy-MM-dd); null for none.</summary>
    public string? DesiredMoveInDate { get; set; }
}

/// <summary>
/// Normalized application-form configuration. Returned to the public form (so it renders the right
/// fields) and to the landlord configurator (the full editable shape). Always non-null and complete —
/// an absent/empty stored config normalizes to defaults (income on, pets off, no fields/defaults).
/// </summary>
public sealed class ApplicationFormConfigDto
{
    public IncomeSourcesConfig IncomeSources { get; set; } = new();
    public PetsConfig Pets { get; set; } = new();
    public IReadOnlyList<CustomFieldConfig> CustomFields { get; set; } = [];
    public FormDefaultsConfig Defaults { get; set; } = new();

    /// <summary>Default keys (e.g. "propertyId", "unitId", "desiredMoveInDate") the applicant may NOT change.</summary>
    public IReadOnlyList<string> Locked { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Landlord configurator wire shapes
// ---------------------------------------------------------------------------

/// <summary>
/// Returned by <c>GET /api/v1/applications/form-config</c>: the portfolio's current (normalized) config
/// plus the property/unit options the landlord can pick a default from.
/// </summary>
public sealed class FormConfigEditorResponse
{
    public ApplicationFormConfigDto Config { get; set; } = new();

    /// <summary>Properties (and their units) available as defaults — same shape the public form uses.</summary>
    public IReadOnlyList<PublicPropertyOption> Properties { get; set; } = [];
}

/// <summary>
/// Body for <c>PUT /api/v1/applications/form-config</c>. The landlord submits the full desired config;
/// the server validates it, generates missing custom-field ids, and persists it on the portfolio.
/// </summary>
public sealed class SaveFormConfigRequest
{
    public IncomeSourcesConfig IncomeSources { get; set; } = new();
    public PetsConfig Pets { get; set; } = new();

    [MaxLength(50)]
    public List<CustomFieldInput> CustomFields { get; set; } = [];

    public FormDefaultsConfig Defaults { get; set; } = new();
    public List<string> Locked { get; set; } = [];
}

/// <summary>A custom field as submitted by the landlord (id optional — generated when missing).</summary>
public sealed class CustomFieldInput
{
    public string? Id { get; set; }

    [MaxLength(120)]
    public string Label { get; set; } = string.Empty;

    public string Type { get; set; } = CustomFieldTypes.Text;
    public bool Required { get; set; }
    public List<string> Options { get; set; } = [];
}
