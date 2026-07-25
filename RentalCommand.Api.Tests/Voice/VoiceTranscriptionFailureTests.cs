using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Voice;

public class VoiceTranscriptionFailureTests
{
    [Fact]
    public async Task OpenAiTranscriber_MissingApiKey_ThrowsUnavailableInsteadOfBlankTranscript()
    {
        using var http = new HttpClient(new NoNetworkHandler());
        var sut = new OpenAiAudioTranscriptionService(
            http,
            Options.Create(new AssistantConfig { ApiKey = "" }),
            NullLogger<OpenAiAudioTranscriptionService>.Instance);

        var act = () => sut.TranscribeAsync([1, 2, 3], "audio/webm", "voice.webm", CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Voice transcription*not configured*");
    }

    [Fact]
    public async Task CreateDraft_TranscriptionUnavailable_Returns503NotSilence400()
    {
        var voice = new Mock<IVoiceIntakeService>(MockBehavior.Strict);
        voice.Setup(v => v.CreateDraftAsync(
                It.Is<WorkspaceReadScope>(scope => scope.PortfolioId == 42),
                It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 1, 2, 3 })),
            "audio/webm",
            null,
            "voice-transcription-failure",
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VoiceTranscriptionUnavailableException("Voice transcription is not configured."));

        var controller = CreateController(voice.Object);
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "audio", "voice.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm",
        };

        var result = await controller.CreateDraft(
            file, null, "voice-transcription-failure", CancellationToken.None);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        objectResult.Value.Should().BeEquivalentTo(new { error = "Voice transcription is not configured." });
        voice.VerifyAll();
    }

    private static VoiceController CreateController(IVoiceIntakeService voice)
    {
        var http = new DefaultHttpContext();
        http.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserId: 7,
            AccessContextId: 1,
            PortfolioId: 42,
            AccessRevision: 1,
            LastAuthorizedExperience: null,
            WorkspaceMembershipId: null,
            DefaultExperience: null);

        return new VoiceController(voice)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = http,
            },
        };
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The missing-key path should not call the network.");
    }
}
