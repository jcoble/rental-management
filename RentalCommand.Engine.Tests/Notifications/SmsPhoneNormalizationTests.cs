using FluentAssertions;
using RentalCommand.Api.Services.Sms;

namespace RentalCommand.Engine.Tests.Notifications;

public class SmsPhoneNormalizationTests
{
    [Theory]
    [InlineData("330-396-6191", "+13303966191")]
    [InlineData("(330) 396-6191", "+13303966191")]
    [InlineData("+1 (330) 396-6191", "+13303966191")]
    public void NormalizeSmsNumber_ConvertsUsNumbersToE164(string input, string expected)
    {
        // Normalization moved from RoutingNotificationChannel into the pluggable SmsDispatcher
        // (now a public static helper); the behaviour and expectations are unchanged.
        SmsDispatcher.NormalizeSmsNumber(input).Should().Be(expected);
    }
}
