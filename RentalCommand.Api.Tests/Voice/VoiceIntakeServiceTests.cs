using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Scanning;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Voice;

[Collection(MigratedPostgreSqlCollection.Name)]
public class VoiceIntakeServiceTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly Mock<ILlmProvider> _llm = new();
    private readonly Mock<IAudioTranscriptionService> _transcriber = new();
    private readonly Mock<IFileStorage> _storage = new();
    private MigratedPostgreSqlTestContext _ctx = null!;
    private WorkspaceReadScope _scope;
    private ServiceProvider _services = null!;

    public VoiceIntakeServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _scope = _ctx.Db.SeedAdministratorScope(1, nameof(VoiceIntakeServiceTests));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            CreateVoiceScanDraftCommand,
            ScanDraftMutationResult,
            CreateVoiceScanDraftHandler>();
        services.AddAtomicCommandHandler<
            AnswerVoiceScanDraftCommand,
            ScanDraftMutationResult,
            AnswerVoiceScanDraftHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(
                    _ctx.Db.Database.GetDbConnection(),
                    contextOwnsConnection: false)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task CreateDraftAsync_NonExpenseIntent_CreatesAmbiguousExpenseDraft()
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
        await _ctx.ActivateApiScopeAsync(_scope);

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
            _services.GetRequiredService<IAtomicUnitOfWork>(),
            _llm.Object,
            _transcriber.Object,
            _storage.Object,
            NullLogger<VoiceIntakeService>.Instance,
            TimeProvider.System);

        var draft = await sut.CreateDraftAsync(
            scope: _scope,
            audioBytes: Array.Empty<byte>(),
            contentType: null,
            providedTranscript: "Frank says water is coming through Unit 3 ceiling.",
            operationKey: "voice-intake-non-expense",
            ct: CancellationToken.None);

        draft.Status.Should().Be("Reviewing");
        draft.TargetEntityType.Should().Be("Expense");
        draft.FilePath.Should().StartWith("voice://");
        draft.ExtractedFields.Should().NotBeNullOrWhiteSpace();

        using var json = JsonDocument.Parse(draft.ExtractedFields!);
        json.RootElement.GetProperty("transcript").GetProperty("value").GetString()
            .Should().Contain("Unit 3");
        json.RootElement.GetProperty("voice_intent").GetProperty("value").GetString()
            .Should().Be("WorkOrder");
        json.RootElement.GetProperty("voice_ambiguous").GetProperty("value").GetString()
            .Should().Be("true");
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
        await _ctx.ActivateApiScopeAsync(_scope);
        var sut = new VoiceIntakeService(
            _ctx.Db,
            _services.GetRequiredService<IAtomicUnitOfWork>(),
            _llm.Object,
            _transcriber.Object,
            _storage.Object,
            NullLogger<VoiceIntakeService>.Instance,
            TimeProvider.System);

        var act = () => sut.CreateDraftAsync(
            scope: _scope,
            audioBytes: Array.Empty<byte>(),
            contentType: null,
            providedTranscript: transcript,
            operationKey: $"voice-intake-low-quality-{transcript}",
            ct: CancellationToken.None);

        await act.Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*clearer voice note*");

        (await _ctx.Db.ScanDrafts.CountAsync()).Should().Be(0);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
