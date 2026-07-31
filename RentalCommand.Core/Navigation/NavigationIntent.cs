namespace RentalCommand.Core.Navigation;

/// <summary>
/// A short-lived, access-bound request to open one destination in a client experience.
/// Resource identifiers are data only; clients own the exhaustive destination-to-route mapping.
/// </summary>
public sealed record NavigationIntent(
    NavigationExperience Experience,
    NavigationDestination Destination,
    int AccessContextId,
    long AccessRevision,
    string? ResourceKind,
    int? ResourceId,
    string? ParentResourceKind,
    int? ParentResourceId,
    string? ChildResourceKind,
    int? ChildResourceId,
    NavigationAction Action,
    DateTime ExpiresAtUtc,
    NavigationDestination FallbackDestination);

public enum NavigationExperience
{
    Management = 1,
    Leasing = 2,
    Maintenance = 3,
    Owner = 4,
    Tenant = 5,
}

/// <summary>
/// Closed destination set shared by API payloads and exhaustive client mappings.
/// This is deliberately not a URL or route template.
/// </summary>
public enum NavigationDestination
{
    Home = 1,
    Notifications = 2,
    Rentals = 3,
    Owners = 4,
    Money = 5,
    Work = 6,
    Inbox = 7,
    UnitSummary = 8,
    UnitTenantLease = 9,
    UnitMoney = 10,
    UnitMaintenance = 11,
    UnitRecords = 12,
    TenantLedgerEntry = 13,
    Expense = 14,
    ScanDraft = 15,
    Message = 16,
    WorkOrder = 17,
    TechnicianWork = 18,
    LeasingRental = 19,
    LeasingApplication = 20,
    LeasingAppointment = 21,
    LeasingConversation = 22,
    LeasingMoveIn = 23,
    TenantAccount = 24,
}

public enum NavigationAction
{
    Open = 1,
    Review = 2,
    Resolve = 3,
}
