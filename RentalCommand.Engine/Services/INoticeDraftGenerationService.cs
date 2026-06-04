namespace RentalCommand.Engine.Services;

/// <summary>
/// Engine-side driver for Lease Lifecycle Autopilot. Iterates portfolios and proactively generates
/// notice drafts (renewal offers, escalating late-rent notices, move-out reminders) via the Api's
/// <c>INoticeDraftService</c>, so drafts appear for one-tap approval WITHOUT the landlord tapping
/// "Generate". Drafts are never auto-sent — approval (and channel selection) stays manual.
/// </summary>
public interface INoticeDraftGenerationService
{
    /// <summary>Generate notice drafts across all portfolios. Returns the total number of drafts created.</summary>
    Task<int> GenerateAllAsync(CancellationToken ct = default);
}
