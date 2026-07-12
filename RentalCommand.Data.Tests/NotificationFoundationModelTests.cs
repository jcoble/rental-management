using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

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

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(UserAlertPreference.PortfolioId), nameof(UserAlertPreference.UserId)]));
    }

    [Fact]
    public void TemplateVersions_AreAppendOnlyByWorkspaceSystemKeyAndVersion()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(WorkspaceNoticeTemplateVersion))!;

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceNoticeTemplateVersion.PortfolioId),
                nameof(WorkspaceNoticeTemplateVersion.SystemKey),
                nameof(WorkspaceNoticeTemplateVersion.Version)]));
    }

    [Fact]
    public void DeliveryEvidence_HasOneOutboxRowAndOneDestinationPerRenderedNotice()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(NoticeDeliveryEvidence))!;

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Single().Name == nameof(NoticeDeliveryEvidence.OutboxMessageId));
        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(NoticeDeliveryEvidence.RenderedNoticeId),
                nameof(NoticeDeliveryEvidence.RecipientTenantId),
                nameof(NoticeDeliveryEvidence.Channel)]));
    }

    [Fact]
    public async Task TemplateVersions_CannotBeUpdatedInPlace()
    {
        await using var db = CreateDb();
        var version = new SystemNoticeTemplateVersion
        {
            SystemKey = "rent-reminder", Version = 1, Subject = "Subject", Body = "Body",
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
