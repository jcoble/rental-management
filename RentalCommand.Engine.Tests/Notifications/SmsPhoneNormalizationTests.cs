using FluentAssertions;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Notifications;

public class SmsPhoneNormalizationTests
{
    [Theory]
    [InlineData("330-396-6191", "+13303966191")]
    [InlineData("(330) 396-6191", "+13303966191")]
    [InlineData("+1 (330) 396-6191", "+13303966191")]
    public void NormalizeSmsNumber_ConvertsUsNumbersToE164(string input, string expected)
    {
        var method = typeof(RoutingNotificationChannel).GetMethod(
            "NormalizeSmsNumber",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(null, [input]).Should().Be(expected);
    }
}
