using System.Text.Json;
using FluentAssertions;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Notifications;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AtomicNoticeDraftOutboxTests
{
    private static readonly DateTime Now =
        new(2026, 8, 13, 10, 15, 0, DateTimeKind.Utc);

    [Fact]
    public void GeneratedDrafts_StagesCreateOnlyForNewRowsInMixedResult()
    {
        var staged = new List<OutboxMessage>();
        var context = new Mock<IAtomicCommandContext>();
        context.Setup(candidate => candidate.StageOutbox(It.IsAny<OutboxMessage>()))
            .Callback<OutboxMessage>(staged.Add);
        var command = new AtomicNoticeDraftMutationCommand(
            PortfolioId: 3,
            ActorUserId: 7,
            AuthSessionId: Guid.NewGuid(),
            AccessContextId: 11,
            ExpectedAccessRevision: 2,
            Operation: AtomicNoticeDraftOperation.Generate,
            NoticeDraftId: 0,
            RequestJson: "{}",
            DeliveryIdempotencyKey: "notice-generation");
        var drafts = new[]
        {
            new AtomicGeneratedTenantNoticeDraft
            {
                DraftId = 41,
                WasCreated = false,
                CreatedCount = 1,
            },
            new AtomicGeneratedTenantNoticeDraft
            {
                DraftId = 42,
                WasCreated = true,
                CreatedCount = 1,
            },
        };

        AtomicNoticeDraftMutationRule.StageCreatedDraftUpdates(
            context.Object, command, drafts, Now);

        var message = staged.Should().ContainSingle().Subject;
        message.IdempotencyKey.Should()
            .Be("notice-generation:notice-draft:42:data-update");
        using var payload = JsonDocument.Parse(message.Payload);
        payload.RootElement.GetProperty("entityType").GetString()
            .Should().Be(nameof(NoticeDraft));
        payload.RootElement.GetProperty("entityId").GetInt32().Should().Be(42);
        payload.RootElement.GetProperty("operation").GetString().Should().Be("create");
    }
}
