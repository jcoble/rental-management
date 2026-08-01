using RentalCommand.Core.Atomic;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Deliberately inert dispatcher used by composition tests while production target writers are absent.
/// It is not registered by the API and therefore cannot create a second confirmation path.
/// </summary>
public sealed class UnsupportedScanConfirmationTargetWriter : IScanConfirmationTargetWriter
{
    public UnsupportedScanConfirmationTargetWriter() { }

    public bool Supports(ScanConfirmationTargetKind kind) => false;

    public Task<ScanConfirmationTargetWriteResult> WriteAsync(
        ConfirmScanDraftCommand command,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        throw new InvalidOperationException("No production scan-confirm target writer is installed.");
}
