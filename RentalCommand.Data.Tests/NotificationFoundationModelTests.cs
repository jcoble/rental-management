using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Tests;

public sealed class NotificationFoundationModelTests
{
    private static RentalCommandDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RentalCommandDbContext(options);
    }

    [Fact]
    public void PersonalAlerts_AreUniquePerWorkspaceAndUser()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(UserAlertPreference))!;
        var expectedProperties = new[]
        {
            nameof(UserAlertPreference.PortfolioId),
            nameof(UserAlertPreference.UserId),
        };

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(expectedProperties));
    }

    [Fact]
    public void TemplateVersions_AreAppendOnlyByWorkspaceSystemKeyAndVersion()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(WorkspaceNoticeTemplateVersion))!;
        var expectedProperties = new[]
        {
            nameof(WorkspaceNoticeTemplateVersion.PortfolioId),
            nameof(WorkspaceNoticeTemplateVersion.SystemKey),
            nameof(WorkspaceNoticeTemplateVersion.Version),
        };

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(expectedProperties));
    }

    [Fact]
    public void SuppliedTemplateV1_IsAStableMigrationSeed()
    {
        using var db = CreateDb();
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var entity = designTimeModel.FindEntityType(typeof(SystemNoticeTemplateVersion))!;
        var seedRows = entity.GetSeedData()
            .OrderBy(row => (int)row[nameof(SystemNoticeTemplateVersion.Id)]!)
            .ToArray();

        seedRows.Should().HaveCount(5);
        seedRows.Select(row => (int)row[nameof(SystemNoticeTemplateVersion.Id)]!)
            .Should().Equal(1, 2, 3, 4, 5);
        seedRows.Select(row => (string)row[nameof(SystemNoticeTemplateVersion.SystemKey)]!)
            .Should().Equal(
                "rent-reminder",
                "lease-renewal-offer",
                "month-to-month-offer",
                "lease-non-renewal",
                "late-rent-late-fee");
        seedRows.Select(row => new
            {
                Id = (int)row[nameof(SystemNoticeTemplateVersion.Id)]!,
                SystemKey = (string)row[nameof(SystemNoticeTemplateVersion.SystemKey)]!,
                Classification = (RentalCommand.Core.Enums.NoticeClassification)row[nameof(SystemNoticeTemplateVersion.Classification)]!,
                Subject = (string)row[nameof(SystemNoticeTemplateVersion.Subject)]!,
                Body = (string)row[nameof(SystemNoticeTemplateVersion.Body)]!,
            })
            .Should().BeEquivalentTo(
                SuppliedNoticeTemplateBaseline.V1,
                options => options.WithStrictOrdering());
        seedRows.Should().OnlyContain(row =>
            (int)row[nameof(SystemNoticeTemplateVersion.Version)]! == 1 &&
            (string)row[nameof(SystemNoticeTemplateVersion.Provenance)]! ==
                SuppliedNoticeTemplateBaseline.Provenance &&
            (DateTime)row[nameof(SystemNoticeTemplateVersion.PublishedAtUtc)]! ==
                SuppliedNoticeTemplateBaseline.PublishedAtUtc);
        SuppliedNoticeTemplateBaseline.PublishedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        entity.FindProperty(nameof(SystemNoticeTemplateVersion.Id))!.ValueGenerated
            .Should().Be(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never);
    }

    [Fact]
    public void SuppliedTemplateWorkspaceCopy_IsOneConflictSafeInsertSelect()
    {
        var createdAtUtc = new DateTime(2026, 7, 13, 1, 2, 3, DateTimeKind.Utc);
        var command = SuppliedNoticeTemplateBaseline.BuildWorkspaceV1CopyCommand(
            41,
            73,
            createdAtUtc);

        command.Format.Should().Contain("INSERT INTO \"WorkspaceNoticeTemplateVersions\"");
        command.Format.Should().Contain("FROM \"SystemNoticeTemplateVersions\" AS system");
        command.Format.Should().Contain("WHERE system.\"Version\" = 1");
        command.Format.Should().Contain(
            "ON CONFLICT (\"PortfolioId\", \"SystemKey\", \"Version\") DO NOTHING");
        command.Format.Should().NotContain("UPDATE");
        command.GetArguments().Should().Equal(41, 73, createdAtUtc);
    }

    [Fact]
    public void DeliveryEvidence_HasOneOutboxRowAndOneDestinationPerRenderedNotice()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(NoticeDeliveryEvidence))!;
        var expectedDestinationProperties = new[]
        {
            nameof(NoticeDeliveryEvidence.RenderedNoticeId),
            nameof(NoticeDeliveryEvidence.RecipientTenantId),
            nameof(NoticeDeliveryEvidence.Channel),
        };

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Single().Name == nameof(NoticeDeliveryEvidence.OutboxMessageId));
        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(expectedDestinationProperties));
    }

    [Fact]
    public async Task TemplateVersions_CannotBeUpdatedInPlace()
    {
        await using var db = CreateDb();
        var version = new SystemNoticeTemplateVersion
        {
            Id = 99, SystemKey = "rent-reminder", Version = 1, Subject = "Subject", Body = "Body",
            Provenance = "test", PublishedAtUtc = DateTime.UtcNow,
        };
        db.SystemNoticeTemplateVersions.Add(version);
        await db.SaveChangesAsync();

        version.Subject = "silently replaced";

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*immutable*");
    }
}
