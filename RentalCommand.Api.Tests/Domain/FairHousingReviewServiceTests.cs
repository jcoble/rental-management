using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Domain;

public class FairHousingReviewServiceTests
{
    private readonly Mock<ILlmProvider> _llm = new();

    private FairHousingReviewService CreateSut() =>
        new(_llm.Object, NullLogger<FairHousingReviewService>.Instance);

    [Fact]
    public async Task ReviewAsync_NoOpLlm_ReturnsReviewedFalseAndNotCompliant()
    {
        // Default Moq behavior for ChatAsync is to return null/empty — the no-op provider shape.
        var sut = CreateSut();

        var result = await sut.ReviewAsync("Perfect for a single professional, no kids.", CancellationToken.None);

        result.Reviewed.Should().BeFalse();
        // Never imply compliant when nothing was actually checked.
        result.Compliant.Should().BeFalse();
        result.Issues.Should().BeEmpty();
        result.SuggestedRewrite.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_EmptyText_ReturnsReviewedFalse_WithoutCallingLlm()
    {
        var sut = CreateSut();

        var result = await sut.ReviewAsync("   ", CancellationToken.None);

        result.Reviewed.Should().BeFalse();
        result.Compliant.Should().BeFalse();
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewAsync_LlmFlagsIssues_ReturnsIssuesAndRewrite()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            {
              "compliant": false,
              "issues": [
                { "phrase": "no kids", "concern": "Excludes families with children (familial status)." }
              ],
              "suggestedRewrite": "Quiet one-bedroom apartment available now."
            }
            """);
        var sut = CreateSut();

        var result = await sut.ReviewAsync("Quiet apartment, no kids.", CancellationToken.None);

        result.Reviewed.Should().BeTrue();
        result.Compliant.Should().BeFalse();
        result.Issues.Should().ContainSingle();
        result.Issues[0].Phrase.Should().Be("no kids");
        result.SuggestedRewrite.Should().Be("Quiet one-bedroom apartment available now.");
    }

    [Fact]
    public async Task ReviewAsync_LlmReportsCompliant_ReturnsCompliantWithNoRewrite()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            ```json
            { "compliant": true, "issues": [], "suggestedRewrite": null }
            ```
            """);
        var sut = CreateSut();

        var result = await sut.ReviewAsync("Two-bedroom unit available June 1. Rent is $1,500/month.", CancellationToken.None);

        result.Reviewed.Should().BeTrue();
        result.Compliant.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.SuggestedRewrite.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_IssuesPresentButModelSaysCompliant_IsForcedNotCompliant()
    {
        // Defensive: a model that contradicts itself must not yield a false green light.
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
            {
              "compliant": true,
              "issues": [ { "phrase": "adults only", "concern": "Familial status." } ],
              "suggestedRewrite": "Available now."
            }
            """);
        var sut = CreateSut();

        var result = await sut.ReviewAsync("Adults only.", CancellationToken.None);

        result.Reviewed.Should().BeTrue();
        result.Compliant.Should().BeFalse();
        result.Issues.Should().ContainSingle();
    }

    [Fact]
    public async Task ReviewAsync_UnparseableOutput_ReturnsReviewedFalse()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("I think this looks fine to me, no JSON here.");
        var sut = CreateSut();

        var result = await sut.ReviewAsync("Some copy.", CancellationToken.None);

        result.Reviewed.Should().BeFalse();
        result.Compliant.Should().BeFalse();
    }
}
