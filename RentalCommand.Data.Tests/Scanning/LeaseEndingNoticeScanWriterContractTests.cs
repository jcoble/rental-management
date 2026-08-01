using FluentAssertions;

namespace RentalCommand.Data.Tests.Scanning;

public sealed class LeaseEndingNoticeScanWriterContractTests
{
    [Fact]
    public void Writer_UsesCanonicalLeaseEndingDispositionCommand()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "RentalCommand.Data",
                "Scanning",
                "ProductionScanConfirmationTargetWriter.cs"));

        source.Should().Contain("ScanConfirmationTargetKind.LeaseEndingNotice => WriteLeaseEndingNoticeAsync");
        source.Should().Contain("RecordLeaseEndingDispositionHandler");
        source.Should().Contain("LeaseManagementEndingDisposition.NonRenewalMoveOut");
        source.Should().Contain("CanonicalEntityType: nameof(LeaseManagement)");
        source.Should().Contain("TargetAuditRecorded: true");
        source.Should().Contain("A scanned notice source file is required");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
