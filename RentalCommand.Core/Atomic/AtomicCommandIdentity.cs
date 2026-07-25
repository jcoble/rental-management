namespace RentalCommand.Core.Atomic;

/// <summary>
/// Stable identity supplied by a command caller. The pair is persisted under a unique constraint,
/// so replay after a timeout or unknown commit can return the original result without executing the
/// mutation again.
/// </summary>
public sealed record AtomicCommandIdentity : IAtomicCommandData
{
    public AtomicCommandIdentity(string commandType, string idempotencyKey)
        : this(commandType, idempotencyKey, requestFingerprint: null)
    {
    }

    private AtomicCommandIdentity(
        string commandType,
        string idempotencyKey,
        string? requestFingerprint)
    {
        CommandType = RequireValue(commandType, nameof(commandType), 160);
        IdempotencyKey = RequireValue(idempotencyKey, nameof(idempotencyKey), 200);
        RequestFingerprint = requestFingerprint;
    }

    public string CommandType { get; }
    public string IdempotencyKey { get; }
    public string? RequestFingerprint { get; }

    /// <summary>
    /// Returns the identity bound to the canonical business payload for this execution. The
    /// fingerprint is computed by the kernel rather than accepted from an HTTP header, so a caller
    /// cannot make two different commands appear identical.
    /// </summary>
    public AtomicCommandIdentity BindRequest(IAtomicCommandData command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = AtomicCommandFingerprint.Create(command);
        if (RequestFingerprint is not null
            && !string.Equals(RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The command identity is already bound to another request payload.");
        }

        return RequestFingerprint is null
            ? new AtomicCommandIdentity(CommandType, IdempotencyKey, fingerprint)
            : this;
    }

    private static string RequireValue(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"{parameterName} cannot exceed {maxLength} characters.");
        }

        return value;
    }
}
