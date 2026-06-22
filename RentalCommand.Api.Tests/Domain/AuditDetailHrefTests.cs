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
}
