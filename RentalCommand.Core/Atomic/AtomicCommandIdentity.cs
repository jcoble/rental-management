namespace RentalCommand.Core.Atomic;

/// <summary>
/// Stable identity supplied by a command caller. The pair is persisted under a unique constraint,
/// so replay after a timeout or unknown commit can return the original result without executing the
/// mutation again.
/// </summary>
public sealed record AtomicCommandIdentity
{
    public AtomicCommandIdentity(string commandType, string idempotencyKey)
    {
        CommandType = RequireValue(commandType, nameof(commandType), 160);
        IdempotencyKey = RequireValue(idempotencyKey, nameof(idempotencyKey), 200);
    }

    public string CommandType { get; }
    public string IdempotencyKey { get; }

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
