namespace RentalCommand.Core.Interfaces;

public interface IAudioTranscriptionService
{
    Task<string> TranscribeAsync(
        byte[] audioBytes,
        string contentType,
        string fileName,
        CancellationToken ct = default);
}
