using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Esign;

/// <summary>
/// No-op <see cref="IEsignProvider"/> used when no e-sign API key is configured. Every operation returns
/// a clear "not configured" result (never a false success) and never contacts any third party. Mirrors
/// how the Stripe/LLM integrations degrade gracefully when their keys are absent.
/// </summary>
public sealed class DisabledEsignProvider : IEsignProvider
{
    private readonly ILogger<DisabledEsignProvider> _logger;

    public DisabledEsignProvider(ILogger<DisabledEsignProvider> logger)
    {
        _logger = logger;
    }

    public bool IsConfigured => false;

    public Task<EsignResult> SendForSignatureAsync(EsignRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("E-sign is not configured — SendForSignatureAsync is a no-op.");
        return Task.FromResult(EsignResult.NotConfigured());
    }

    public Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default)
    {
        _logger.LogDebug("E-sign is not configured — GetStatusAsync is a no-op.");
        return Task.FromResult(EsignResult.NotConfigured());
    }

    public Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default)
    {
        _logger.LogDebug("E-sign is not configured — DownloadSignedDocumentAsync is a no-op.");
        return Task.FromResult<byte[]?>(null);
    }
}
