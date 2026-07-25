namespace RentalCommand.Core.Interfaces;

public interface IAudioTranscriptionService : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    Task<string> TranscribeAsync(
        byte[] audioBytes,
        string contentType,
        string fileName,
        CancellationToken ct = default);
}
