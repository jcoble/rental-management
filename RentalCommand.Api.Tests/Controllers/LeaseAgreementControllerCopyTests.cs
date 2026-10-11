using FluentAssertions;

namespace RentalCommand.Api.Tests.Controllers;

public sealed class LeaseAgreementControllerCopyTests
{
    [Fact]
    public void LeaseAgreementErrors_UsePlainLeaseVocabulary()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "RentalCommand.Api",
            "Controllers",
            "LeaseAgreementController.cs"));

        foreach (var message in new[]
        {
            "Lease draft not found",
            "Lease signing packet not found",
            "Lease document not found",
            "Lease document file not found",
            "Lease source scan not found",
            "Lease source scan file not found",
            "The lease is unavailable or has no signer snapshot."
        })
        {
            source.Should().Contain(message);
        }

        source.Should().NotContain("Agreement draft not found");
        source.Should().NotContain("Issued Agreement signature packet not found");
        source.Should().NotContain("Agreement artifact not found");
        source.Should().NotContain("Agreement artifact file not found on storage");
        source.Should().NotContain("Agreement source scan not found");
        source.Should().NotContain("Agreement source scan file not found on storage");
        source.Should().NotContain("The Agreement is unavailable or has no signer snapshot.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
