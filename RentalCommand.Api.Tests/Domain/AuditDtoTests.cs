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

    [Fact]
    public void AdminResponse_ProjectsProviderDeadLetterSearchFieldsForOperatorRecovery()
    {
        var row = new AtomicAuditLog
        {
            Id = 99,
            PortfolioId = 7,
            EntityType = nameof(TenantAccount),
            EntityId = 41,
            Operation = AuditLogOperation.Updated,
            ChangeReason = "Claimed provider event evt_dead_99 dead-lettered: exact target changed.",
            NewValues = JsonSerializer.Serialize(new
            {
                Id = 123L,
                ProviderObjectId = "pi_dead_99",
                Amount = 125.50m,
                Currency = "USD",
                ChargeLedgerEntryId = 808L,
                ProviderEventId = "evt_dead_99",
                ProviderReceivedAtUtc = DateTime.UtcNow.AddMinutes(-3),
                ProviderDeadLetteredAtUtc = DateTime.UtcNow,
                RecoveryStatus = "DeadLettered",
                RecoveryAction = "Reconcile or inspect provider object before retrying",
            }),
        };

        var response = AdminAuditEntryResponse.FromEntity(
            row, new AuditDescriber(), diff: new AuditDiffBuilder());

        response.IsProviderPaymentDeadLetter.Should().BeTrue();
        response.ProviderEventId.Should().Be("evt_dead_99");
        response.PaymentIntentId.Should().Be("pi_dead_99");
        response.PaymentAmount.Should().Be(125.50m);
        response.PaymentCurrency.Should().Be("USD");
        response.TargetChargeLedgerEntryId.Should().Be(808L);
        response.PaymentAttemptId.Should().Be(123L);
        response.RecoveryStatus.Should().Be("DeadLettered");
        response.RecoveryAction.Should().Contain("Reconcile");

        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("evt_dead_99");
        json.Should().Contain("pi_dead_99");
        json.Should().Contain("125.5");
        json.Should().Contain("808");
        json.Should().Contain("123");
    }
}
