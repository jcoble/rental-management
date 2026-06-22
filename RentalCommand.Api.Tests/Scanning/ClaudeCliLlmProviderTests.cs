using FluentAssertions;
using RentalCommand.Api.Scanning;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class ClaudeCliLlmProviderTests
{
    [Fact]
    public void ParseToolProtocolResponse_WhenToolCallJson_ReturnsToolUse()
    {
        var result = ClaudeCliLlmProvider.ParseToolProtocolResponse(
            """
            {"tool_calls":[{"id":"call-expenses","name":"list_recent_expenses","arguments":{"withinDays":30}}]}
            """,
            AllowedTools(),
            "claude-cli:sonnet");

        result.StopReason.Should().Be("tool_use");
        result.ToolCalls.Should().ContainSingle();
        result.ToolCalls[0].Id.Should().Be("call-expenses");
        result.ToolCalls[0].Name.Should().Be("list_recent_expenses");
        result.ToolCalls[0].ArgumentsJson.Should().Be("""{"withinDays":30}""");
        result.ModelId.Should().Be("claude-cli:sonnet");
    }

    [Fact]
    public void ParseToolProtocolResponse_WhenArgumentsAreFencedString_NormalizesArgumentsJson()
    {
        var result = ClaudeCliLlmProvider.ParseToolProtocolResponse(
            """
            ```json
            {"tool_calls":[{"name":"list_recent_payments","arguments":"```json\n{\"withinDays\":7}\n```"}]}
            ```
            """,
            AllowedTools(),
            "claude-cli:sonnet");

        result.StopReason.Should().Be("tool_use");
        result.ToolCalls.Should().ContainSingle();
        result.ToolCalls[0].Id.Should().Be("call-1");
        result.ToolCalls[0].Name.Should().Be("list_recent_payments");
        result.ToolCalls[0].ArgumentsJson.Should().Be("""{"withinDays":7}""");
    }

    [Fact]
    public void ParseToolProtocolResponse_WhenAnswerJson_ReturnsFinalAnswer()
    {
        var result = ClaudeCliLlmProvider.ParseToolProtocolResponse(
            """{"answer":"Open Accounting, then choose New Payment."}""",
            AllowedTools(),
            "claude-cli:sonnet");

        result.StopReason.Should().Be("end");
        result.Text.Should().Be("Open Accounting, then choose New Payment.");
        result.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void ParseToolProtocolResponse_WhenUnknownToolName_IgnoresCall()
    {
        var result = ClaudeCliLlmProvider.ParseToolProtocolResponse(
            """{"tool_calls":[{"id":"call-notion","name":"notion_search","arguments":{"query":"expenses"}}]}""",
            AllowedTools(),
            "claude-cli:sonnet");

        result.StopReason.Should().Be("end");
        result.ToolCalls.Should().BeEmpty();
    }

    private static HashSet<string> AllowedTools() =>
    [
        "list_recent_expenses",
        "list_recent_payments",
    ];
}
