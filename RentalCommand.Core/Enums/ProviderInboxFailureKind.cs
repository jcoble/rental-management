namespace RentalCommand.Core.Enums;

/// <summary>Why a verified provider event could not be reconciled on its latest attempt.</summary>
public enum ProviderInboxFailureKind
{
    Retryable,
    Unmatched,
    Permanent,
}
