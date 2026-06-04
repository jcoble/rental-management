using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Screening;

/// <summary>
/// No-op <see cref="IScreeningProvider"/> used when no screening API key is configured. Every request
/// returns a clear "not configured" result (never a false "passed") and never contacts any third party.
/// Mirrors how the Stripe/LLM/e-sign integrations degrade gracefully when their keys are absent.
/// </summary>
public sealed class DisabledScreeningProvider : IScreeningProvider
{
    private readonly ILogger<DisabledScreeningProvider> _logger;

    public DisabledScreeningProvider(ILogger<DisabledScreeningProvider> logger)
    {
        _logger = logger;
    }

    public bool IsConfigured => false;

    public Task<ScreeningProviderResult> RequestScreeningAsync(ScreeningRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("Screening is not configured — RequestScreeningAsync is a no-op.");
        return Task.FromResult(ScreeningProviderResult.NotConfigured());
    }
}
