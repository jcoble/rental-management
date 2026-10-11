using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Screening;

/// <summary>
/// No-op <see cref="IScreeningProvider"/> used when no screening API key is configured. Every request
/// returns a clear "not configured" result (never a false "passed") and never contacts any third party.
/// Mirrors how the Stripe/LLM/e-sign integrations degrade gracefully when their keys are absent.
/// </summary>
public sealed class DisabledScreeningProvider : IScreeningProvider
{
    public ScreeningProviderDescriptor Descriptor { get; } = new(
        "unconfigured",
        "Integrated screening",
        false,
        new ScreeningProviderCapabilities(false, false, false, false, false));

    public Task<ScreeningInvitationResult> CreateInvitationAsync(
        ScreeningInvitationRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("No integrated screening provider adapter is configured.");

    public Task<ScreeningProviderDeliveryVerification> VerifyDeliveryAsync(
        ScreeningProviderCallback callback, CancellationToken ct = default) =>
        Task.FromResult(ScreeningProviderDeliveryVerification.Rejected("provider_not_configured"));
}
