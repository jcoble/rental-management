using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// LLM-backed Fair Housing Act compliance review of landlord-written copy (tenant notices today,
/// listings later). Flags language that references — or could be read to discriminate against — a
/// protected class (race, color, religion, sex, national origin, familial status, disability) plus
/// steering/preference phrasing, and suggests a compliant rewrite.
/// <para>
/// Safety contract: when no LLM key is configured the provider is a no-op; in that case the result is
/// <c>Reviewed = false</c> and the copy is NEVER reported as compliant — the UI must surface "AI review
/// unavailable" rather than a false green light.
/// </para>
/// </summary>
public interface IFairHousingReviewService
{
    Task<FairHousingReviewResult> ReviewAsync(string text, CancellationToken ct = default);
}
