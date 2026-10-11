using FluentAssertions;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Notices;

public sealed class NoticeDeliveryContentSafetyTests
{
    [Theory]
    [InlineData("Rent reminder", "Hello {{tenant_name}}")]
    [InlineData("Rent reminder", "Hello {Sofia Rodriguez}")]
    [InlineData("Rent reminder", "Hello {Sofia\r\nRodriguez}")]
    [InlineData("{unfinished}", "Hello Sofia")]
    [InlineData("Rent reminder", "Workspace administrator: review this first.")]
    [InlineData("Rent reminder", "WORKSPACE ADMINISTRATOR: review this first.")]
    public void FindIssue_RejectsReservedMergeResidueAndInternalInstructions(
        string subject,
        string body)
    {
        NoticeDeliveryContentSafety.FindIssue(subject, body)
            .Should().Be(NoticeDeliveryContentSafety.CorrectionMessage);

        var action = () => NoticeDeliveryContentSafety.RequireSafe(subject, body);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage(NoticeDeliveryContentSafety.CorrectionMessage);
    }

    [Theory]
    [InlineData("Rent reminder", "Hello Sofia, your rent is due Friday.")]
    [InlineData("Question?", "Use commas, periods, dashes - and parentheses (safely).")]
    [InlineData("Opening brace {", "Closing brace } without a balanced fragment.")]
    [InlineData("Empty braces {}", "An empty pair is not merge content.")]
    public void FindIssue_AllowsSafeCopyAndOrdinaryPunctuation(
        string subject,
        string body)
    {
        NoticeDeliveryContentSafety.IsSafe(subject, body).Should().BeTrue();
        NoticeDeliveryContentSafety.FindIssue(subject, body).Should().BeNull();
    }

    [Fact]
    public void CorrectionMessage_DoesNotEchoTenantContent()
    {
        var issue = NoticeDeliveryContentSafety.FindIssue(
            "{Private Tenant Name}",
            "Workspace administrator: private body");

        issue.Should().Be(NoticeDeliveryContentSafety.CorrectionMessage);
        issue.Should().NotContain("Private Tenant Name")
            .And.NotContain("private body");
    }
}
