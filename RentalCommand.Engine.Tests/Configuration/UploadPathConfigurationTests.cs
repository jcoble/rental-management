using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace RentalCommand.Engine.Tests.Configuration;

public class UploadPathConfigurationTests
{
    [Fact]
    public void DefaultLocalUploadPathsResolveToSharedRepoUploadsDirectory()
    {
        var root = FindRepoRoot();
        var expected = Path.Combine(root, "uploads");

        var apiBasePath = ReadUploadBasePath(root, "RentalCommand.Api", "appsettings.Development.json");
        var engineBasePath = ReadUploadBasePath(root, "RentalCommand.Engine", "appsettings.json");

        ResolveFromProject(root, "RentalCommand.Api", apiBasePath).Should().Be(expected);
        ResolveFromProject(root, "RentalCommand.Engine", engineBasePath).Should().Be(expected);
    }

    [Fact]
    public void LocalDockerComposeSharesUploadVolumeBetweenApiAndEngine()
    {
        var root = FindRepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        Regex.Matches(compose, @"Upload__BasePath=/var/lib/rentalcommand/uploads")
            .Count.Should().BeGreaterThanOrEqualTo(2);
        Regex.Matches(compose, @"uploads:/var/lib/rentalcommand/uploads")
            .Count.Should().BeGreaterThanOrEqualTo(2);
        compose.Should().Contain("  uploads:");
    }

    private static string ReadUploadBasePath(string root, string projectDir, string fileName)
    {
        var path = Path.Combine(root, projectDir, fileName);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("Upload").GetProperty("BasePath").GetString()
            ?? throw new InvalidOperationException($"Upload:BasePath is missing from {path}");
    }

    private static string ResolveFromProject(string root, string projectDir, string configuredPath)
        => Path.GetFullPath(Path.Combine(root, projectDir, configuredPath));

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
