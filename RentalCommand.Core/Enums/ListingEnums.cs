namespace RentalCommand.Core.Enums;

public enum RentalListingStatus
{
    Draft = 0,
    ReadyToPublish = 1,
    Published = 2,
    Paused = 3,
    Filled = 4,
    Archived = 5,
}

public enum ListingPublicationMode
{
    Guided = 0,
    Connected = 1,
}

public enum ListingPublicationStatus
{
    Draft = 0,
    Ready = 1,
    Publishing = 2,
    Published = 3,
    Paused = 4,
    Failed = 5,
    Removed = 6,
}

public enum ExternalListingSignalDisposition
{
    Unconfirmed = 0,
    Confirmed = 1,
    Rejected = 2,
}
