namespace RentalCommand.Core.Enums;

/// <summary>Why the most recent delivery attempt did not reach provider acceptance.</summary>
public enum OutboxFailureKind
{
    Retryable,
    ConfigurationBlocked,
    Permanent,
}
