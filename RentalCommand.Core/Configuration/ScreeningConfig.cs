namespace RentalCommand.Core.Configuration;

/// <summary>
/// Selects an installed adapter. Concrete adapters own strongly typed endpoints and secrets; the
/// provider-neutral layer never guesses a vendor's credential or HTTP shape.
/// </summary>
public sealed class ScreeningConfig
{
    public const string SectionName = "Screening";
    public string? ProviderKey { get; set; }
}
