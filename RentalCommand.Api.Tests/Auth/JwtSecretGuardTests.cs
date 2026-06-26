using FluentAssertions;
using RentalCommand.Api.Auth;

namespace RentalCommand.Api.Tests.Auth;

public class JwtSecretGuardTests
{
    private const string AppSettingsPlaceholder = "CHANGE_ME_IN_PRODUCTION_use_a_64_char_random_secret_value_here_000";
    private const string EnvExamplePlaceholder = "CHANGE_ME_use_a_64_char_random_secret_value_here";
    private const string StrongSecret = "real-prod-secret-64-plus-chars-0123456789abcdefghijklmnopqrstuvwxyz";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-secret")]
    public void Validate_RejectsMissingOrShortSecrets(string? secret)
    {
        var act = () => JwtSecretGuard.Validate(secret, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing or too short*");
    }

    [Theory]
    [InlineData(AppSettingsPlaceholder)]
    [InlineData(EnvExamplePlaceholder)]
    [InlineData("change_me_use_a_64_char_random_secret_value_here")]
    public void Validate_RejectsCommittedPlaceholdersOutsideDevelopment(string secret)
    {
        var act = () => JwtSecretGuard.Validate(secret, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*placeholder*");
    }

    [Fact]
    public void Validate_AllowsCommittedPlaceholdersInDevelopment()
    {
        var act = () => JwtSecretGuard.Validate(AppSettingsPlaceholder, isDevelopment: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AllowsStrongNonPlaceholderSecret()
    {
        var act = () => JwtSecretGuard.Validate(StrongSecret, isDevelopment: false);

        act.Should().NotThrow();
    }
}
