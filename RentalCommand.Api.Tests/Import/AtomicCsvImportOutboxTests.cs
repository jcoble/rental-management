using System.Text.Json;
using FluentAssertions;
using Moq;
using RentalCommand.Api.Services.Import;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Import;

namespace RentalCommand.Api.Tests.Import;

public sealed class AtomicCsvImportOutboxTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void UnitImport_StagesDataUpdateForEachCreatedUnit()
    {
        var (context, staged) = CapturingContext();
        var rows = new[]
        {
            new AtomicUnitImportRowResult(
                2, true, false, 41, 7, "2A", 2, 1, 1_250m, []),
        };

        AtomicUnitCsvImportRule.StageCreatedRowUpdates(
            context.Object, portfolioId: 3, importOperationDigest: "unit-digest", rows, Now);

        AssertDataUpdate(staged.Should().ContainSingle().Subject, "Unit", 41);
    }

    [Fact]
    public void PaymentImport_StagesDataUpdateForEachAffectedTenantAccount()
    {
        var (context, staged) = CapturingContext();
        var rows = new[]
        {
            new AtomicPaymentCsvImportRowResult(
                2, true, false, 9_001, 73, []),
        };

        AtomicPaymentCsvImportRule.StageCreatedRowUpdates(
            context.Object, portfolioId: 3, importOperationDigest: "payment-digest", rows, Now);

        AssertDataUpdate(staged.Should().ContainSingle().Subject, nameof(TenantAccount), 73);
    }

    [Fact]
    public void CoreImport_StagesDataUpdateForEachCreatedDomainEntity()
    {
        var (context, staged) = CapturingContext();
        var rows = new[]
        {
            new AtomicCoreCsvImportRowResult(2, true, false, 58, null, []),
        };

        AtomicCoreCsvImportRule.StageCreatedRowUpdates(
            context.Object, portfolioId: 3, AtomicCoreCsvImportDomain.Expense,
            importOperationDigest: "expense-digest", rows, Now);

        AssertDataUpdate(staged.Should().ContainSingle().Subject, "Expense", 58);
    }

    private static (Mock<IAtomicCommandContext> Context, List<OutboxMessage> Staged)
        CapturingContext()
    {
        var staged = new List<OutboxMessage>();
        var context = new Mock<IAtomicCommandContext>();
        context.Setup(candidate => candidate.StageOutbox(It.IsAny<OutboxMessage>()))
            .Callback<OutboxMessage>(staged.Add);
        return (context, staged);
    }

    private static void AssertDataUpdate(OutboxMessage message, string entityType, int entityId)
    {
        message.MessageType.Should().Be("data-update");
        using var payload = JsonDocument.Parse(message.Payload);
        payload.RootElement.GetProperty("entityType").GetString().Should().Be(entityType);
        payload.RootElement.GetProperty("entityId").GetInt32().Should().Be(entityId);
        payload.RootElement.GetProperty("entityId").GetInt32().Should().BeGreaterThan(0);
    }
}
