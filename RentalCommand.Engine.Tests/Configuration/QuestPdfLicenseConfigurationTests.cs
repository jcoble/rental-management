using FluentAssertions;

namespace RentalCommand.Engine.Tests.Configuration;

public class QuestPdfLicenseConfigurationTests
{
    [Fact]
    public void EngineStartupConfiguresQuestPdfCommunityLicenseBeforeExecutedPdfGenerator()
    {
        var root = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "RentalCommand.Engine", "Program.cs"));

        var licenseIndex = program.IndexOf(
            "QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;",
            StringComparison.Ordinal);
        var generatorIndex = program.IndexOf(
            "RentalCommand.Api.Services.Esign.ExecutedLeasePdfGenerator",
            StringComparison.Ordinal);

        licenseIndex.Should().BeGreaterThanOrEqualTo(0);
        generatorIndex.Should().BeGreaterThanOrEqualTo(0);
        licenseIndex.Should().BeLessThan(generatorIndex);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RentalCommand.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find RentalCommand.sln above the test output directory.");
    }
}
