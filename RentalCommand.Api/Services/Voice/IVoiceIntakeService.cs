using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Voice;

public interface IVoiceIntakeService
{
    Task<ScanDraft> CreateDraftAsync(
        int portfolioId,
        byte[] audioBytes,
        string? contentType,
        string? providedTranscript,
        CancellationToken ct = default);
}
