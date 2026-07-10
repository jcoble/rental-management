namespace RentalCommand.Api.Services.Esign;

/// <summary>
/// Renders and atomically attaches the immutable executed document for a native e-sign request
/// whose signatures have already been durably captured.
/// </summary>
public interface INativeEsignExecutionService
{
    /// <summary>
    /// Finalizes one request when it is in <c>ExecutionPending</c>. The operation is idempotent:
    /// an already-completed request returns <see langword="true"/> without creating another file.
    /// </summary>
    Task<bool> FinalizePendingAsync(int signatureRequestId, CancellationToken ct = default);
}
