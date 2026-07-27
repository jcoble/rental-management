namespace RentalCommand.Core.Entities;

/// <summary>
/// Portfolio-owned switches and timing for scheduled business automations. Delivery destinations
/// intentionally do not live here; team routing and each user's alert preferences resolve those.
/// </summary>
public sealed class AutomationSettings
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    // Rent charges are core ledger behavior, not an optional automation. This persisted value is
    // retained for schema compatibility and is constrained to true by the database.
    public bool EnableRentCharges { get; set; } = true;
    public int RentChargeLeadDays { get; set; } = 5;
    public bool EnableLateFees { get; set; }
    public int LateFeeGraceDays { get; set; } = 5;
    public bool EnableLeaseExpiryReminders { get; set; } = true;
    public int LeaseExpiryReminderDays { get; set; } = 60;
    public bool EnableRecurringMaintenance { get; set; } = true;
    public bool EnableMorningBriefing { get; set; } = true;
    public int MorningBriefingSendHourLocal { get; set; } = 8;
    public bool MorningBriefingIncludeEmpty { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
}
