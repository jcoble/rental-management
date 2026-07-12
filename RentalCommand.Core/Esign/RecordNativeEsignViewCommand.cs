using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Esign;

public enum NativeEsignViewOutcome
{
    Available,
    NotFound,
    Expired,
}

/// <summary>
/// Records the first legally significant opening of one native signing session. The raw token is
/// command input only; callers must use a one-way token digest for the durable command identity.
/// </summary>
public sealed record RecordNativeEsignViewCommand(
    string TokenHash,
    string? IpAddress,
    string? UserAgent,
    DateTime OccurredAtUtc) : IAtomicCommandData;

/// <summary>Receipt-safe admission result. Current display state is deliberately queried after commit.</summary>
public sealed record RecordNativeEsignViewResult(
    NativeEsignViewOutcome Outcome,
    string? Error,
    int SignatureRequestId) : IAtomicResultData;
