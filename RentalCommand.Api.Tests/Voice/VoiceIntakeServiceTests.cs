using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Voice;

public class VoiceIntakeServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly Mock<ILlmProvider> _llm = new();
    private readonly Mock<IAudioTranscriptionService> _transcriber = new();
    private readonly Mock<IFileStorage> _storage = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CreateDraftAsync_TranscriptClassifiesWorkOrder_CreatesReviewingDraft()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Properties.Add(new Property
        {
            PortfolioId = 1,
            Name = "Oak House",
            AddressLine1 = "1 Oak St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43004",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();

        _llm.Setup(x => x.ChatAsync(It.Is<string>(p => p.Contains("water is coming through", StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            {
              "targetEntityType": "WorkOrder",
              "fields": {
                "property_id": { "value": "1", "confidence": 0.8 },
                "title": { "value": "Ceiling leak", "confidence": 0.9 },
                "description": { "value": "Water is coming through Unit 3 ceiling.", "confidence": 0.9 },
                "priority": { "value": "Emergency", "confidence": 0.8 }
              }
            }
            """);

        var sut = new VoiceIntakeService(
            _ctx.Db,
            _llm.Object,
            _transcriber.Object,
            _storage.Object,
            NullLogger<VoiceIntakeService>.Instance);

        var draft = await sut.CreateDraftAsync(
            portfolioId: 1,
            audioBytes: Array.Empty<byte>(),
            contentType: null,
            providedTranscript: "Frank says water is coming through Unit 3 ceiling.",
            ct: CancellationToken.None);

        draft.Status.Should().Be("Reviewing");
        draft.TargetEntityType.Should().Be("WorkOrder");
        draft.FilePath.Should().StartWith("voice://");
        draft.ExtractedFields.Should().NotBeNullOrWhiteSpace();

        using var json = JsonDocument.Parse(draft.ExtractedFields!);
        json.RootElement.GetProperty("transcript").GetProperty("value").GetString()
            .Should().Contain("Unit 3");
        json.RootElement.GetProperty("title").GetProperty("value").GetString()
            .Should().Be("Ceiling leak");
        json.RootElement.GetProperty("priority").GetProperty("value").GetString()
            .Should().Be("Emergency");
    }

    [Theory]
    [InlineData("uh")]
    [InlineData("...")]
    [InlineData("left right maybe")]
    public async Task CreateDraftAsync_LowQualityInitialTranscript_ThrowsBeforeCreatingNoisyDraft(string transcript)
    {
        var sut = new VoiceIntakeService(
            _ctx.Db,
            _llm.Object,
            _transcriber.Object,
            _storage.Object,
            NullLogger<VoiceIntakeService>.Instance);

        var act = () => sut.CreateDraftAsync(
            portfolioId: 1,
            audioBytes: Array.Empty<byte>(),
            contentType: null,
            providedTranscript: transcript,
            ct: CancellationToken.None);

        await act.Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*clearer voice note*");

        (await _ctx.Db.ScanDrafts.CountAsync()).Should().Be(0);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
