namespace RentalCommand.Api.Services.Voice;

/// <summary>
/// The voice endpoint is reachable, but the transcription provider cannot run
/// because required provider configuration is missing or unavailable.
/// </summary>
public sealed class VoiceTranscriptionUnavailableException : InvalidOperationException
{
    public VoiceTranscriptionUnavailableException(string message) : base(message)
    {
    }
}
