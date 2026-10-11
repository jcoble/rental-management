namespace RentalCommand.Core.Enums;

/// <summary>Durable state of the single provider refresh-token rotation lane.</summary>
public enum AccountingTokenRotationState
{
    Idle = 0,
    InFlight = 1,
    RecoveryRequired = 2,
}
