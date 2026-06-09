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

    /// <summary>
    /// Answers the current question for an in-progress voice draft: appends the
    /// spoken answer to the draft's running transcript, re-classifies, and merges
    /// the result back in (new non-empty values win; prior values are kept where
    /// the re-classification dropped them). Drives the slot-filling conversation.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such draft in the portfolio.</exception>
    /// <exception cref="ArgumentException">Neither audio nor transcript provided.</exception>
    Task<ScanDraft> AnswerAsync(
        int portfolioId,
        int draftId,
        byte[] audioBytes,
        string? contentType,
        string? providedTranscript,
        CancellationToken ct = default);
}
