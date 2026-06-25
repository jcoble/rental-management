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
