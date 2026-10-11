namespace RentalCommand.Core.Policies;

public static class DeleteEligibilityPolicy
{
    public const string TenantCurrentResidentReason =
        "This tenant is a current resident in an occupied rental; return possession or change the household first.";
    public const string TenantHistoryReason =
        "This tenant has rental relationship history; keep the tenant record to preserve agreements and account history.";

    public static bool CanDeleteTenant(int activeLeaseCount, int leaseHistoryCount) =>
        activeLeaseCount == 0 && leaseHistoryCount == 0;

    public static string? TenantBlockedReason(int activeLeaseCount, int leaseHistoryCount) =>
        activeLeaseCount > 0
            ? TenantCurrentResidentReason
            : leaseHistoryCount > 0
                ? TenantHistoryReason
                : null;
}
