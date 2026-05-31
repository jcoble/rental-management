namespace RentalCommand.Core.Configuration;

/// <summary>
/// Startup-seed settings (bound from the "Seed" configuration section). Controls the default
/// Admin account + Portfolio that let login be smoke-tested on a fresh database. Disabled by
/// default; enable it only in non-production environments (set in appsettings.Development.json).
/// </summary>
public class SeedSettings
{
    public const string SectionName = "Seed";

    /// <summary>When true, the seeder ensures roles, a default Portfolio, and the Admin user exist at startup.</summary>
    public bool Enabled { get; set; }

    /// <summary>Email/username of the default Admin account.</summary>
    public string AdminEmail { get; set; } = "admin@rentalcommand.local";

    /// <summary>Known dev password for the default Admin account. Meets the configured password policy.</summary>
    public string AdminPassword { get; set; } = "Admin123!";

    /// <summary>Display name of the default Admin account.</summary>
    public string AdminDisplayName { get; set; } = "Rental Command Admin";

    /// <summary>Name of the default Portfolio the Admin account is scoped to.</summary>
    public string PortfolioName { get; set; } = "Default Portfolio";

    /// <summary>Management company name stored on the default Portfolio.</summary>
    public string ManagementCompanyName { get; set; } = "Rental Command";
}
