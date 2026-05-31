namespace RentalCommand.Core.Configuration;

/// <summary>
/// Root strongly-typed application settings aggregating the individual configuration
/// sections. Bound from the root of configuration; the connection string lives at the
/// conventional "ConnectionStrings:DefaultConnection" path.
/// </summary>
public class AppSettings
{
    /// <summary>Primary (PostgreSQL) connection string.</summary>
    public string DefaultConnection { get; set; } = string.Empty;

    public JwtSettings Jwt { get; set; } = new();
    public UploadSettings Upload { get; set; } = new();
    public AssistantConfig Assistant { get; set; } = new();
}
