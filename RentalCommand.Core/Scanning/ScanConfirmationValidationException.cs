namespace RentalCommand.Core.Scanning;

/// <summary>
/// A reviewed scan cannot be persisted because its selected facts are incomplete, out of scope, or
/// conflict with current business data. The atomic transaction rolls back; the future API adapter
/// maps this exception to a reviewable client validation response rather than a server failure.
/// </summary>
public sealed class ScanConfirmationValidationException : Exception
{
    public ScanConfirmationValidationException(string message) : base(message) { }
}
