using FluentAssertions;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AuditDetailHrefTests
{
    [Fact]
    public void BuildDetailHref_UsesCanonicalMaintenanceRouteForWorkOrders()
    {
        AuditEntryResponse.BuildDetailHref("WorkOrder", 42).Should().Be("/maintenance/42");
    }

    [Fact]
    public void BuildDetailHref_UsesUnitCommandCenterTabsWhenUnitContextExists()
    {
        AuditEntryResponse.BuildDetailHref("WorkOrder", 42, 5).Should().Be("/units/5?tab=maintenance&wo=42");
        AuditEntryResponse.BuildDetailHref("Lease", 43, 5).Should().Be("/units/5?tab=lease&lease=43");
        AuditEntryResponse.BuildDetailHref("Payment", 44, 5).Should().Be("/units/5?tab=rent&payment=44");
        AuditEntryResponse.BuildDetailHref("Expense", 45, 5).Should().Be("/units/5?tab=expenses&expense=45");
        AuditEntryResponse.BuildDetailHref("RentalApplication", 46, 5).Should().Be("/units/5?tab=applications&app=46");
    }

    [Fact]
    public void BuildDetailHref_UsesCanonicalMaintenanceRouteForInspections()
    {
        AuditEntryResponse.BuildDetailHref("Inspection", 7).Should().Be("/maintenance/inspections/7");
    }

    [Fact]
    public void BuildDetailHref_UsesCanonicalVendorRouteForVendors()
    {
        AuditEntryResponse.BuildDetailHref("Vendor", 9).Should().Be("/vendors/9");
    }
}
