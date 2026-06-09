using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Voice;

/// <summary>
/// The slot-filling "Tell me" conversation: speak a partial expense, get asked
/// for the missing field, answer it, and reach a complete draft.
/// </summary>
public class VoiceConversationTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly Mock<ILlmProvider> _llm = new();
    private readonly Mock<IAudioTranscriptionService> _transcriber = new();
    private readonly Mock<IFileStorage> _storage = new();

    public void Dispose() => _ctx.Dispose();

    private VoiceIntakeService CreateSut() => new(
        _ctx.Db,
        _llm.Object,
        _transcriber.Object,
        _storage.Object,
        NullLogger<VoiceIntakeService>.Instance);

    [Fact]
    public async Task Conversation_FillsMissingAmount_ThenCompletes()
    {
        // Base classification (no "forty" in the transcript yet): an expense with
        // property + category but no amount.
        _llm.Setup(x => x.ChatAsync(
                It.Is<string>(p => p.Contains("plumbing", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            { "targetEntityType": "Expense", "fields": {
                "property_id": { "value": "5", "confidence": 0.8 },
                "category": { "value": "plumbing", "confidence": 0.8 } } }
            """);
        // Once the answer ("forty dollars") is appended to the transcript, the
        // amount appears. Configured last so it wins for the combined prompt.
        _llm.Setup(x => x.ChatAsync(
                It.Is<string>(p => p.Contains("forty", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            { "targetEntityType": "Expense", "fields": {
                "property_id": { "value": "5", "confidence": 0.8 },
                "category": { "value": "plumbing", "confidence": 0.8 },
                "amount": { "value": "40", "confidence": 0.9 } } }
            """);

        var sut = CreateSut();

        var draft = await sut.CreateDraftAsync(
            portfolioId: 1,
            audioBytes: [],
            contentType: null,
            providedTranscript: "Log a plumbing expense for 123 Main.",
            ct: CancellationToken.None);

        var turn1 = ScanDraftResponse.FromEntity(draft).WithVoiceSlots();
        turn1.Complete.Should().BeFalse();
        turn1.MissingRequired.Should().Contain("amount");
        turn1.NextPrompt.Should().Be("How much was it?");

        var answered = await sut.AnswerAsync(
            portfolioId: 1,
            draftId: draft.Id,
            audioBytes: [],
            contentType: null,
            providedTranscript: "forty dollars",
            ct: CancellationToken.None);

        var turn2 = ScanDraftResponse.FromEntity(answered).WithVoiceSlots();
        turn2.Complete.Should().BeTrue();
        turn2.NextPrompt.Should().BeNull();
        turn2.Fields.Should().Contain(f => f.Name == "amount" && f.Value == "40");
        // Prior fields are preserved across the merge.
        turn2.Fields.Should().Contain(f => f.Name == "category" && f.Value == "plumbing");
    }

    [Fact]
    public async Task Answer_UnknownDraft_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.AnswerAsync(
            portfolioId: 1,
            draftId: 999,
            audioBytes: [],
            contentType: null,
            providedTranscript: "forty dollars",
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
