using FluentAssertions;
using RentalCommand.Api.Services.Sms;

namespace RentalCommand.Api.Tests.Domain;

public sealed class SmsProviderHttpTests
{
    [Fact]
    public void TryReadString_ReturnsNestedProviderReceipt()
    {
        SmsProviderHttp.TryReadString("""{"data":{"id":"message-123"}}""", "data", "id")
            .Should().Be("message-123");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"data\":{}}")]
    public void TryReadString_ReturnsNull_WhenSuccessBodyHasNoUsableReceipt(string body)
    {
        SmsProviderHttp.TryReadString(body, "data", "id").Should().BeNull();
    }
}
