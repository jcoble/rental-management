using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Api.DTOs;
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

/// <summary>
/// The slot-filling "Tell me" conversation: speak a partial expense, get asked
/// for the missing field, answer it, and reach a complete draft.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name3)]
public class VoiceConversationTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly Mock<ILlmProvider> _llm = new();
    private readonly Mock<IAudioTranscriptionService> _transcriber = new();
    private readonly Mock<IFileStorage> _storage = new();
    private MigratedPostgreSqlTestContext _ctx = null!;
    private WorkspaceReadScope _scope;
    private ServiceProvider _services = null!;
    private AsyncServiceScope _serviceScope;

    public VoiceConversationTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _scope = _ctx.Db.SeedAdministratorScope(1, nameof(VoiceConversationTests));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(
                    _ctx.Db.Database.GetDbConnection(),
                    contextOwnsConnection: false)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
        _serviceScope = _services.CreateAsyncScope();
    }

    public async Task DisposeAsync()
    {
        await _serviceScope.DisposeAsync();
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    private VoiceIntakeService CreateSut() => new(
        _serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
        _serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>(),
        _llm.Object,
        _transcriber.Object,
        _storage.Object,
        NullLogger<VoiceIntakeService>.Instance,
        TimeProvider.System);

    [Fact]
    public async Task Conversation_FillsMissingAmount_ThenCompletes()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Properties.Add(new Property
        {
            Id = 5,
            PortfolioId = _scope.PortfolioId,
            Name = "123 Main",
            AddressLine1 = "123 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43004",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();

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
        await _ctx.ActivateApiScopeAsync(_scope);

        var draft = await sut.CreateDraftAsync(
            scope: _scope,
            audioBytes: [],
            contentType: null,
            providedTranscript: "Log a plumbing expense for 123 Main.",
            operationKey: "voice-conversation-create",
            ct: CancellationToken.None);

        var turn1 = ScanDraftResponse.FromEntity(draft).WithVoiceSlots();
        turn1.Complete.Should().BeFalse();
        turn1.Ambiguous.Should().BeFalse();
        turn1.MissingRequired.Should().Contain("amount");
        turn1.NextPrompt.Should().Be("How much was it?");

        _serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()
            .ChangeTracker.Clear();
        var answered = await sut.AnswerAsync(
            scope: _scope,
            draftId: draft.Id,
            audioBytes: [],
            contentType: null,
            providedTranscript: "forty dollars",
            operationKey: "voice-conversation-answer",
            ct: CancellationToken.None);

        var turn2 = ScanDraftResponse.FromEntity(answered).WithVoiceSlots();
        turn2.Complete.Should().BeTrue();
        turn2.Ambiguous.Should().BeFalse();
        turn2.NextPrompt.Should().BeNull();
        turn2.Fields.Should().Contain(f => f.Name == "amount" && f.Value == "40");
        // Prior fields are preserved across the merge.
        turn2.Fields.Should().Contain(f => f.Name == "category" && f.Value == "plumbing");
    }

    [Fact]
    public async Task UnsupportedVoiceIntent_IsAmbiguousExpenseDraft_NotSaveable()
    {
        _llm.Setup(x => x.ChatAsync(
                It.Is<string>(p => p.Contains("leaky faucet", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            { "targetEntityType": "WorkOrder", "fields": {
                "title": { "value": "Leaky faucet", "confidence": 0.9 },
                "description": { "value": "Tenant reported a leaky faucet", "confidence": 0.9 } } }
            """);

        var sut = CreateSut();
        await _ctx.ActivateApiScopeAsync(_scope);

        var draft = await sut.CreateDraftAsync(
            scope: _scope,
            audioBytes: [],
            contentType: null,
            providedTranscript: "Tenant called about a leaky faucet.",
            operationKey: "voice-unsupported-create",
            ct: CancellationToken.None);

        draft.TargetEntityType.Should().Be("Expense");

        var turn = ScanDraftResponse.FromEntity(draft).WithVoiceSlots();
        turn.Complete.Should().BeFalse();
        turn.Ambiguous.Should().BeTrue();
        turn.NextPrompt.Should().Contain("expenses by voice");
        turn.Fields.Should().Contain(f => f.Name == "voice_intent" && f.Value == "WorkOrder");
        turn.Fields.Should().Contain(f => f.Name == "voice_ambiguous" && f.Value == "true");
    }

    [Fact]
    public async Task Answer_ReclassifiesAmbiguousIntentAsExpense_ClearsAmbiguousFlag()
    {
        _llm.Setup(x => x.ChatAsync(
                It.Is<string>(p => p.Contains("leaky faucet", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            { "targetEntityType": "WorkOrder", "fields": {
                "title": { "value": "Leaky faucet", "confidence": 0.9 } } }
            """);

        _llm.Setup(x => x.ChatAsync(
                It.Is<string>(p => p.Contains("invoice was forty dollars", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            { "targetEntityType": "Expense", "fields": {
                "vendor_name": { "value": "Plumber", "confidence": 0.8 },
                "amount": { "value": "40", "confidence": 0.9 },
                "category": { "value": "Repairs", "confidence": 0.8 } } }
            """);

        var sut = CreateSut();
        await _ctx.ActivateApiScopeAsync(_scope);

        var draft = await sut.CreateDraftAsync(
            scope: _scope,
            audioBytes: [],
            contentType: null,
            providedTranscript: "Tenant called about a leaky faucet.",
            operationKey: "voice-ambiguous-create",
            ct: CancellationToken.None);

        ScanDraftResponse.FromEntity(draft).WithVoiceSlots().Ambiguous.Should().BeTrue();

        _serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()
            .ChangeTracker.Clear();
        var answered = await sut.AnswerAsync(
            scope: _scope,
            draftId: draft.Id,
            audioBytes: [],
            contentType: null,
            providedTranscript: "The plumber invoice was forty dollars.",
            operationKey: "voice-ambiguous-answer",
            ct: CancellationToken.None);

        var turn = ScanDraftResponse.FromEntity(answered).WithVoiceSlots();
        turn.Complete.Should().BeTrue();
        turn.Ambiguous.Should().BeFalse();
        turn.Fields.Should().NotContain(f => f.Name == "voice_intent" || f.Name == "voice_ambiguous");
        turn.Fields.Should().Contain(f => f.Name == "amount" && f.Value == "40");
    }

    [Fact]
    public async Task Answer_UnknownDraft_Throws()
    {
        var sut = CreateSut();
        await _ctx.ActivateApiScopeAsync(_scope);

        var act = () => sut.AnswerAsync(
            scope: _scope,
            draftId: 999,
            audioBytes: [],
            contentType: null,
            providedTranscript: "forty dollars",
            operationKey: "voice-unknown-answer",
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
