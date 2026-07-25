using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Portfolio"/>. Never expose the entity directly.</summary>
public class PortfolioResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ManagementCompanyName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public PortfolioStatus Status { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Settings { get; set; }

    /// <summary>Account-wide sandbox/live state. True = seeded example data.</summary>
    public bool IsSandbox { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>portfolio-1</c>.</summary>
    public string TestId => $"portfolio-{Id}";

    public static PortfolioResponse FromEntity(Portfolio e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Description = e.Description,
        ManagementCompanyName = e.ManagementCompanyName,
        TimeZone = e.TimeZone,
        Status = e.Status,
        Currency = e.Currency,
        Settings = e.Settings,
        IsSandbox = e.IsSandbox,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

/// <summary>
/// Current Sandbox/Live state of the account (portfolio). The web app uses <see cref="IsSandbox"/> to
/// render the persistent "Sandbox" banner and to offer the one-way "Go Live" action, and
/// <see cref="OnboardingChoicePending"/> to gate the first-login Sandbox-vs-Live choice screen.
/// </summary>
public class SandboxStateResponse
{
    public int PortfolioId { get; set; }

    /// <summary>True while the account is using seeded example data.</summary>
    public bool IsSandbox { get; set; }

    /// <summary>When the sandbox demo data was seeded; null once graduated to Live.</summary>
    public DateTime? SandboxSeededAtUtc { get; set; }

    /// <summary>
    /// True when the account has not yet made the first-login Sandbox-vs-Live choice. While true, the
    /// web/mobile clients route the user to the onboarding choice gate instead of the dashboard.
    /// </summary>
    public bool OnboardingChoicePending { get; set; }
}

/// <summary>
/// Small, server-shaped facts used by the web and mobile getting-started checklist. These are
/// deliberately counts/booleans so clients never download list payloads just to aggregate them.
/// </summary>
public class GettingStartedSignalsResponse
{
    public int PortfolioId { get; set; }
    public bool PortfolioNamed { get; set; }
    public int OwnerCount { get; set; }
    public int PropertyCount { get; set; }
    public int UnitCount { get; set; }
    public int TenantCount { get; set; }
    public int LeaseCount { get; set; }
    public bool HasNotificationEmail { get; set; }
    public bool HasTexting { get; set; }
    public bool HasAutomations { get; set; }
    public bool IsSandbox { get; set; }
}

/// <summary>
/// Body for the first-login onboarding decision. <c>mode</c> is "sandbox" (seed the demo portfolio) or
/// "live" (keep an empty real portfolio). Any other value is rejected with 400.
/// </summary>
public class OnboardingChoiceRequest
{
    [Required]
    public string Mode { get; set; } = string.Empty;
}

public class UpdatePortfolioRequest
{
    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? ManagementCompanyName { get; set; }

    [MaxLength(64)]
    public string? TimeZone { get; set; }

    public PortfolioStatus? Status { get; set; }

    [MaxLength(8)]
    public string? Currency { get; set; }

    [MaxLength(10000)]
    public string? Settings { get; set; }
}
