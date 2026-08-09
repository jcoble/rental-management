using System.Text.Json;
using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AuditDtoTests
{
    [Fact]
    public void LandlordResponse_ExcludesForensicFields_ButKeepsChangeReason()
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

        typeof(AuditEntryResponse).GetProperty("IpAddress").Should().BeNull();
        typeof(AuditEntryResponse).GetProperty("OldValues").Should().BeNull();
        typeof(AuditEntryResponse).GetProperty("NewValues").Should().BeNull();
        response.ChangeReason.Should().Be("Corrected the receipt total");

        var json = JsonSerializer.Serialize(response);
        json.Should().NotContain("IpAddress");
        json.Should().NotContain("OldValues");
        json.Should().NotContain("NewValues");
        json.Should().NotContain("203.0.113.8");
        json.Should().NotContain("{\"Amount\":10}");
        json.Should().NotContain("{\"Amount\":20}");
    }

    [Fact]
    public void AdminResponse_RetainsForensicFields()
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

        var response = AdminAuditEntryResponse.FromEntity(
            row,
            new AuditDescriber(),
            diff: new AuditDiffBuilder());

        response.ChangeReason.Should().Be("Corrected the receipt total");
        response.IpAddress.Should().Be("203.0.113.8");
        response.OldValues.Should().Be("{\"Amount\":10}");
        response.NewValues.Should().Be("{\"Amount\":20}");
    }
}
