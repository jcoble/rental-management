using FluentAssertions;

namespace RentalCommand.Api.Tests.Configuration;

public sealed class PreviewIntegrationConfigurationContractTests
{
    [Fact]
    public void ApiProgram_BindsNotificationsConfiguration()
    {
        var source = ReadRepositoryFile("RentalCommand.Api", "Program.cs");

        source.Should().Contain(
            "builder.Services.Configure<RentalCommand.Core.Configuration.NotificationsConfig>(");
        source.Should().Contain(
            "builder.Configuration.GetSection(RentalCommand.Core.Configuration.NotificationsConfig.SectionName)");
    }

    [Fact]
    public void PreviewCompose_MapsIntegrationSettingsWithoutLiteralCredentials()
    {
        var compose = ReadRepositoryFile("deploy", "docker-compose.preview.yml");
        var api = ServiceBlock(compose, "api");
        var engine = ServiceBlock(compose, "engine");
        var web = ServiceBlock(compose, "web");

        var apiMappings = new[]
        {
            "PlatformAdmin__Emails__0: ${PLATFORM_ADMIN_EMAILS:-}",
            "Plaid__Environment: ${PLAID_ENVIRONMENT:-sandbox}",
            "Plaid__ClientId: ${PLAID_CLIENT_ID:-}",
            "Plaid__Secret: ${PLAID_SECRET:-}",
            "Plaid__RedirectUri: ${PLAID_REDIRECT_URI:-}",
            "Plaid__AndroidPackageName: ${PLAID_ANDROID_PACKAGE_NAME:-com.rentalcommand.rental_command.dev}",
            "QuickBooks__ClientId: ${QUICKBOOKS_CLIENT_ID:-}",
            "QuickBooks__ClientSecret: ${QUICKBOOKS_CLIENT_SECRET:-}",
            "QuickBooks__Environment: ${QUICKBOOKS_ENVIRONMENT:-sandbox}",
            "QuickBooks__RedirectUri: ${QUICKBOOKS_REDIRECT_URI:-}",
            "Notifications__PublicWebhookBaseUrl: ${WEB_ORIGIN}",
            "Notifications__Twilio__AccountSid: ${TWILIO_ACCOUNT_SID:-}",
            "Notifications__Twilio__AuthToken: ${TWILIO_AUTH_TOKEN:-}",
            "Notifications__Twilio__FromNumber: ${TWILIO_FROM_NUMBER:-}",
        };
        var engineMappings = new[]
        {
            "QuickBooks__ClientId: ${QUICKBOOKS_CLIENT_ID:-}",
            "QuickBooks__ClientSecret: ${QUICKBOOKS_CLIENT_SECRET:-}",
            "QuickBooks__Environment: ${QUICKBOOKS_ENVIRONMENT:-sandbox}",
            "QuickBooks__RedirectUri: ${QUICKBOOKS_REDIRECT_URI:-}",
            "Notifications__Email__Transport: ${EMAIL_TRANSPORT:-SendGrid}",
            "Notifications__SendGrid__ApiKey: ${SENDGRID_API_KEY:-}",
            "Notifications__SendGrid__FromEmail: ${SENDGRID_FROM_EMAIL:-}",
            "Notifications__SendGrid__FromName: ${SENDGRID_FROM_NAME:-Rental Command}",
            "Notifications__Twilio__AccountSid: ${TWILIO_ACCOUNT_SID:-}",
            "Notifications__Twilio__AuthToken: ${TWILIO_AUTH_TOKEN:-}",
            "Notifications__Twilio__FromNumber: ${TWILIO_FROM_NUMBER:-}",
        };

        foreach (var mapping in apiMappings)
        {
            api.Should().Contain(mapping);
        }

        foreach (var mapping in engineMappings)
        {
            engine.Should().Contain(mapping);
        }

        web.Should().Contain("PLATFORM_ADMIN_EMAILS: ${PLATFORM_ADMIN_EMAILS:-}");
        compose.Should().NotContain("Notifications__SendGrid__ApiKey: \"\"");
        compose.Should().NotContain("Notifications__Twilio__AuthToken: \"\"");
    }

    [Fact]
    public void PreviewCompose_PreservesStableOriginAndPracticalCpuLimits()
    {
        var compose = ReadRepositoryFile("deploy", "docker-compose.preview.yml");

        compose.Should().Contain("Cors__AllowedOrigins__0: ${WEB_ORIGIN}");
        compose.Should().Contain("App__WebBaseUrl: ${WEB_ORIGIN}");
        compose.Should().Contain("ORIGIN: ${WEB_ORIGIN}");
        compose.Should().Contain("Assistant__ApiKey: ${ASSISTANT_API_KEY:-}");
        compose.Should().Contain("GooglePlaces__ApiKey: ${GOOGLE_PLACES_API_KEY:-}");
        compose.Should().Contain("Authentication__Google__ClientId: ${GOOGLE_CLIENT_ID:-}");
        compose.Should().Contain("PUBLIC_GOOGLE_CLIENT_ID: ${GOOGLE_CLIENT_ID:-}");

        ServiceBlock(compose, "postgres").Should().Contain("cpus: 2.0");
        ServiceBlock(compose, "api").Should().Contain("cpus: 2.0");
    }

    private static string ServiceBlock(string compose, string serviceName)
    {
        var marker = $"  {serviceName}:{Environment.NewLine}";
        var start = compose.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);

        var nextService = compose.IndexOf(
            $"{Environment.NewLine}  ",
            start + marker.Length,
            StringComparison.Ordinal);

        while (nextService >= 0
               && nextService + 3 < compose.Length
               && char.IsWhiteSpace(compose[nextService + 3]))
        {
            nextService = compose.IndexOf(
                $"{Environment.NewLine}  ",
                nextService + 3,
                StringComparison.Ordinal);
        }

        return nextService < 0
            ? compose[start..]
            : compose[start..nextService];
    }

    private static string ReadRepositoryFile(params string[] path)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
