using FluentAssertions;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests the conservative heuristic that routes a question to the knowledge-base (how-to) path
/// versus the live-data tool path. When in doubt the heuristic must return false so the existing
/// portfolio-data answers keep working.
/// </summary>
public sealed class PortfolioQaRoutingTests
{
    [Theory]
    [InlineData("How do I record a payment?")]
    [InlineData("What is a security deposit?")]
    [InlineData("Where do I add a tenant?")]
    [InlineData("Walk me through setting up a property")]
    [InlineData("Explain how the scan to draft flow works")]
    [InlineData("What are the steps to send a lease for signature?")]
    public void LooksLikeHowTo_ProductQuestions_RouteToDocs(string question)
    {
        PortfolioQaService.LooksLikeHowTo(question).Should().BeTrue();
    }

    [Theory]
    [InlineData("Who hasn't paid rent this month?")]
    [InlineData("How much have I collected?")]
    [InlineData("Show me my overdue rent")]
    [InlineData("Which of my tenants have expiring leases?")]
    [InlineData("How many units do I have vacant?")]
    [InlineData("List my vendors")]
    public void LooksLikeHowTo_DataQuestions_StayOnDataPath(string question)
    {
        PortfolioQaService.LooksLikeHowTo(question).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeHowTo_EmptyOrBlank_ReturnsFalse(string question)
    {
        PortfolioQaService.LooksLikeHowTo(question).Should().BeFalse();
    }
}
