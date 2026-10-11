namespace RentalCommand.Core.Enums;

public enum ScreeningMode
{
    Integrated,
    External,
}

public enum ApplicantScreeningStatus
{
    Created,
    AwaitingProvider,
    AwaitingApplicant,
    InProgress,
    Completed,
    Failed,
    Cancelled,
}
