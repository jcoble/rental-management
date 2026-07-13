namespace RentalCommand.Core.Enums;

public enum TenantNoticeMode
{
    Off,
    Draft,
    Auto,
}

public enum NoticeClassification
{
    Courtesy,
    Operational,
    Legal,
}

public enum NoticeFailureBehavior
{
    StopAndRequireReview,
    RetryThenDraft,
    RetryThenFail,
}

public enum NoticeDeliveryChannel
{
    TenantPortal,
    MobilePush,
    Email,
    Sms,
}

public enum NoticeRecipientRole
{
    PrimaryTenant,
    CoTenant,
    Guarantor,
    Occupant,
}

public enum TeamRoutingTopic
{
    RentAndMoney,
    ApplicationsAndLeasing,
    WorkOrders,
    OwnerStatementsAndDecisions,
    AccountAndSecurity,
    MorningBriefing,
}

public enum TenantNoticeWorkStatus
{
    Pending,
    Claimed,
    Completed,
    Blocked,
}
