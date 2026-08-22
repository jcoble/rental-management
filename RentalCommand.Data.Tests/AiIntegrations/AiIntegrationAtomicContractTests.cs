using System.Text.RegularExpressions;
using FluentAssertions;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.AiIntegrations;

namespace RentalCommand.Data.Tests.AiIntegrations;

public sealed class AiIntegrationAtomicContractTests
{
    [Theory]
    [InlineData(
        typeof(ActivateWorkspaceLlmCredentialHandler),
        typeof(ActivateWorkspaceLlmCredentialCommand))]
    [InlineData(
        typeof(RotateWorkspaceLlmCredentialHandler),
        typeof(RotateWorkspaceLlmCredentialCommand))]
    [InlineData(
        typeof(RemoveWorkspaceLlmCredentialHandler),
        typeof(RemoveWorkspaceLlmCredentialCommand))]
    [InlineData(
        typeof(PortfolioQaDeliveryHandler),
        typeof(PortfolioQaDeliveryCommand))]
    public void UserFacingHandlers_ReauthorizeReceiptReplay(
        Type handlerType,
        Type commandType)
    {
        handlerType.GetMethod("AuthorizeReplayAsync", [commandType, typeof(IAtomicCommandContext), typeof(CancellationToken)])
            .Should().NotBeNull();
    }

    [Fact]
    public void CredentialHandlers_StageSecretFreeSemanticAudit()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/AiIntegrations/" +
                "AiIntegrationCommandHandlers.cs"));
        var compactSource = Regex.Replace(source, @"\s+", " ");

        compactSource.Should().Contain(
            "NewValues: JsonSerializer.Serialize(new { Provider = provider, ModelId = modelId, })");
        compactSource.Should().NotContain("ApiKeyIntentDigest");
        compactSource.Should().NotContain("apiKey");
    }

    [Fact]
    public void UsageHandler_ValidatesStableIdentityAndNonNegativeAmounts()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/AiIntegrations/" +
                "AiIntegrationCommandHandlers.cs"));

        source.Should().Contain("string.IsNullOrWhiteSpace(command.UsageEventIdentity)");
        source.Should().Contain("command.UsageEventIdentity.Length > 200");
        source.Should().Contain("command.LatencyMilliseconds < 0");
        source.Should().Contain("command.InputUnits < 0");
        source.Should().Contain("command.OutputUnits < 0");
        source.Should().Contain("command.EstimatedCostUsd < 0m");
        source.Should().Contain("\"LlmUsageEvidence\"");
        source.Should().Contain("StableUsageEventKey(command.PortfolioId, command.UsageEventIdentity)");
    }
}
