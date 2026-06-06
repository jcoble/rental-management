using FluentAssertions;
using RentalCommand.Api.Services.Voice;

namespace RentalCommand.Api.Tests.Voice;

public class VoiceSlotsTests
{
    private static Dictionary<string, string> Fields(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Expense_MissingAmount_AsksAmount()
    {
        var result = VoiceSlots.Evaluate("Expense", Fields(("property_id", "5")));

        result.Missing.Should().ContainSingle().Which.Should().Be("amount");
        result.NextPrompt.Should().Be("How much was it?");
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public void Expense_AmountAndProperty_IsComplete()
    {
        var result = VoiceSlots.Evaluate("Expense", Fields(("amount", "40"), ("property_id", "5")));

        result.Complete.Should().BeTrue();
        result.NextPrompt.Should().BeNull();
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void Total_Satisfies_Amount_Slot()
    {
        var result = VoiceSlots.Evaluate("Expense", Fields(("total", "40"), ("property_id", "5")));

        result.Missing.Should().NotContain("amount");
        result.Complete.Should().BeTrue();
    }

    [Fact]
    public void NoSpecificProperty_Satisfies_PropertySlot()
    {
        var result = VoiceSlots.Evaluate(
            "Expense",
            Fields(("amount", "40"), ("property_id", VoiceSlots.NoneSentinel)));

        result.Complete.Should().BeTrue();
    }

    [Fact]
    public void MissingBoth_AsksAmountFirst()
    {
        var result = VoiceSlots.Evaluate("Expense", Fields());

        result.Missing.Should().BeEquivalentTo(["amount", "property_id"]);
        result.NextPrompt.Should().Be("How much was it?");
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public void UnknownRecordType_IsAlwaysComplete()
    {
        var result = VoiceSlots.Evaluate("WorkOrder", Fields());

        result.Complete.Should().BeTrue();
        result.Missing.Should().BeEmpty();
        result.NextPrompt.Should().BeNull();
    }

    [Fact]
    public void BlankAmountValue_CountsAsMissing()
    {
        var result = VoiceSlots.Evaluate("Expense", Fields(("amount", "  "), ("property_id", "5")));

        result.Missing.Should().Contain("amount");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("0.00")]
    [InlineData("$0")]
    public void ZeroAmount_CountsAsMissing(string zero)
    {
        // The LLM emits "0"/"0.0" when the speaker never said an amount; that must
        // still trigger the "How much was it?" question.
        var result = VoiceSlots.Evaluate("Expense", Fields(("amount", zero), ("property_id", "5")));

        result.Missing.Should().ContainSingle().Which.Should().Be("amount");
        result.NextPrompt.Should().Be("How much was it?");
        result.Complete.Should().BeFalse();
    }

    [Theory]
    [InlineData("40")]
    [InlineData("40.00")]
    [InlineData("$1,200.50")]
    public void PositiveAmount_SatisfiesSlot(string amount)
    {
        var result = VoiceSlots.Evaluate("Expense", Fields(("amount", amount), ("property_id", "5")));

        result.Missing.Should().NotContain("amount");
        result.Complete.Should().BeTrue();
    }
}
