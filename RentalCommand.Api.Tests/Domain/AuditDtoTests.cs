using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AuditDtoTests
{
    [Fact]
    public void FromEntity_CarriesCapturedAuditDetails_WhenPresent()
    {
        var row = new AtomicAuditLog
        {
            Id = 42,
            PortfolioId = 7,
            EntityType = "Expense",
            EntityId = 5,
            Operation = AuditLogOperation.Updated,
            OldValues = "{\"Amount\":10}",
            NewValues = "{\"Amount\":20}",
            ChangeReason = "Corrected the receipt total",
            IpAddress = "203.0.113.8",
        };

        var response = AuditEntryResponse.FromEntity(row, new AuditDescriber(), new AuditDiffBuilder());

        response.ChangeReason.Should().Be("Corrected the receipt total");
        response.IpAddress.Should().Be("203.0.113.8");
        response.OldValues.Should().Be("{\"Amount\":10}");
        response.NewValues.Should().Be("{\"Amount\":20}");
    }
}
