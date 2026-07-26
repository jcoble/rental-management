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
    public void NotificationReadState_IsUserScopedAndCannotCrossPortfolios()
    {
        using var db = CreateDb();
        var notification = db.Model.FindEntityType(typeof(Notification))!;
        var readState = db.Model.FindEntityType(typeof(NotificationReadState))!;

        notification.FindProperty("IsRead").Should().BeNull();
        notification.FindProperty("ReadAt").Should().BeNull();
        readState.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal(
            nameof(NotificationReadState.PortfolioId),
            nameof(NotificationReadState.NotificationId),
            nameof(NotificationReadState.UserId));
        readState.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(Notification) &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NotificationReadState.NotificationId),
                nameof(NotificationReadState.PortfolioId),
            }));
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
    public void MorningBriefingAndAccountSecurityRouting_AreWorkspaceOnlyAtTheDatabaseBoundary()
    {
        using var db = CreateDb();
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var entity = designTimeModel.FindEntityType(typeof(TeamRoutingRule))!;

        entity.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_TeamRoutingRules_WorkspaceOnlyTopics" &&
            constraint.Sql.Contains("MorningBriefing") &&
            constraint.Sql.Contains("AccountAndSecurity"));
    }

    [Fact]
    public void WorkspaceRouting_IsUniqueEvenWhenPropertyIdIsNull()
    {
        using var db = CreateDb();
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var entity = designTimeModel.FindEntityType(typeof(TeamRoutingRule))!;

        entity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.GetDatabaseName() == "IX_TeamRoutingRules_PortfolioId_Topic_Workspace" &&
            index.GetFilter() == "\"PropertyId\" IS NULL");
        entity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(TeamRoutingRule.PortfolioId),
                nameof(TeamRoutingRule.Topic),
                nameof(TeamRoutingRule.PropertyId),
            }) &&
            index.GetFilter() == "\"PropertyId\" IS NOT NULL");
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
    public void SuppliedLegalTemplateV2_UsesNewStableIdsAndJurisdictionNeutralContent()
    {
        SuppliedNoticeTemplateBaseline.V2Legal.Should().HaveCount(2);
        SuppliedNoticeTemplateBaseline.V2Legal.Select(template => template.Id)
            .Should().Equal(6, 7);
        SuppliedNoticeTemplateBaseline.V2Legal.Select(template => template.SystemKey)
            .Should().Equal("lease-non-renewal", "late-rent-late-fee");
        SuppliedNoticeTemplateBaseline.V1.Select(template => template.Id)
            .Concat(SuppliedNoticeTemplateBaseline.V2Legal.Select(template => template.Id))
            .Should().OnlyHaveUniqueItems();
        SuppliedNoticeTemplateBaseline.LegalVersion.Should().Be(2);
        SuppliedNoticeTemplateBaseline.LegalV2Provenance
            .Should().Contain("jurisdiction-neutral")
            .And.Contain("local timing and content review required");
        SuppliedNoticeTemplateBaseline.LegalV2PublishedAtUtc.Kind.Should().Be(DateTimeKind.Utc);

        var nonRenewal = SuppliedNoticeTemplateBaseline.V2Legal[0];
        nonRenewal.Body.Should().Contain("{{tenant_name}}")
            .And.Contain("{{property_address}}")
            .And.Contain("{{lease_end_date}}")
            .And.Contain("return possession")
            .And.Contain("Security-deposit and final-account handling")
            .And.Contain("state and local timing, delivery, and content requirement")
            .And.Contain("not a determination that the notice is legally sufficient");

        var lateRent = SuppliedNoticeTemplateBaseline.V2Legal[1];
        lateRent.Body.Should().Contain("{{tenant_name}}")
            .And.Contain("{{property_address}}")
            .And.Contain("{{overdue_amount}}")
            .And.Contain("{{today}}")
            .And.Contain("{{rent_due_date}}")
            .And.Contain("{{late_fee_amount}}")
            .And.Contain("only if that fee is authorized by the rental agreement")
            .And.Contain("believe the amount is incorrect")
            .And.Contain("state and local timing, delivery, and content requirement")
            .And.Contain("not a determination that the notice is legally sufficient");
    }

    [Fact]
    public void SuppliedLegalTemplateV3_IsTheExactSafeAppendOnlySuccessor()
    {
        const string nonRenewalRemovedParagraph =
            "\n\nWorkspace administrator: before sending, review and adapt this starting copy for every state and local timing, delivery, and content requirement. This jurisdiction-neutral template is not a determination that the notice is legally sufficient.";
        const string lateRentOriginalFinalParagraph =
            "Any future action will be taken only under the rental agreement and applicable requirements. Workspace administrator: before sending, review and adapt this starting copy for every state and local timing, delivery, and content requirement. This jurisdiction-neutral template is not a determination that the notice is legally sufficient.";
        const string lateRentSafeFinalParagraph =
            "Any future action will be taken only under the rental agreement and applicable requirements.";

        SuppliedNoticeTemplateBaseline.V3Legal.Should().HaveCount(2);
        SuppliedNoticeTemplateBaseline.V3Legal.Select(template => template.Id)
            .Should().Equal(8, 9);
        SuppliedNoticeTemplateBaseline.V3Legal.Select(template => template.SystemKey)
            .Should().Equal("lease-non-renewal", "late-rent-late-fee");
        SuppliedNoticeTemplateBaseline.SafeLegalVersion.Should().Be(3);
        SuppliedNoticeTemplateBaseline.SafeLegalV3PublishedAtUtc.Should()
            .Be(new DateTime(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc));
        SuppliedNoticeTemplateBaseline.SafeLegalV3PublishedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        SuppliedNoticeTemplateBaseline.V1.Select(template => template.Id)
            .Concat(SuppliedNoticeTemplateBaseline.V2Legal.Select(template => template.Id))
            .Concat(SuppliedNoticeTemplateBaseline.V3Legal.Select(template => template.Id))
            .Should().OnlyHaveUniqueItems();

        var v2NonRenewal = SuppliedNoticeTemplateBaseline.V2Legal[0];
        var v3NonRenewal = SuppliedNoticeTemplateBaseline.V3Legal[0];
        v3NonRenewal.Subject.Should().Be(v2NonRenewal.Subject);
        v3NonRenewal.Body.Should().Be(v2NonRenewal.Body.Replace(
            nonRenewalRemovedParagraph,
            string.Empty,
            StringComparison.Ordinal));

        var v2LateRent = SuppliedNoticeTemplateBaseline.V2Legal[1];
        var v3LateRent = SuppliedNoticeTemplateBaseline.V3Legal[1];
        v3LateRent.Subject.Should().Be(v2LateRent.Subject);
        v3LateRent.Body.Should().Be(v2LateRent.Body.Replace(
            lateRentOriginalFinalParagraph,
            lateRentSafeFinalParagraph,
            StringComparison.Ordinal));

        SuppliedNoticeTemplateBaseline.V3Legal.Should().OnlyContain(template =>
            !template.Body.Contains("Workspace administrator:", StringComparison.Ordinal) &&
            !template.Body.Contains(
                "not a determination that the notice is legally sufficient",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SuppliedTemplateWorkspaceCopyAndDraftPolicies_AreOneConflictSafeCommand()
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
        command.Format.Should().Contain("INSERT INTO \"TenantNoticePolicies\"");
        command.Format.Should().Contain(
            "ON CONFLICT (\"PortfolioId\", \"AutomationKey\") DO NOTHING");
        command.Format.Should().NotContain("UPDATE");
        command.GetArguments().Should().Equal(
            41,
            73,
            createdAtUtc,
            createdAtUtc,
            createdAtUtc,
            41);
    }

    [Fact]
    public void DeliveryEvidence_HasOneOutboxRowAndOneDestinationPerRenderedNotice()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(NoticeDeliveryEvidence))!;
        var expectedDestinationProperties = new[]
        {
            nameof(NoticeDeliveryEvidence.RenderedNoticeId),
            nameof(NoticeDeliveryEvidence.RecipientLeaseManagementPartyId),
            nameof(NoticeDeliveryEvidence.Channel),
        };

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Single().Name == nameof(NoticeDeliveryEvidence.OutboxMessageId));
        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(expectedDestinationProperties));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDeliveryEvidence.RecipientLeaseManagementPartyId),
                nameof(NoticeDeliveryEvidence.PortfolioId),
            }));
    }

    [Fact]
    public void NoticeDraft_UsesCanonicalRelationshipPartyAndLegalProvenance()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(NoticeDraft))!;

        entity.FindProperty("LeaseId").Should().BeNull();
        entity.FindProperty("PaymentId").Should().BeNull();
        entity.FindProperty("RecipientTenantId").Should().BeNull();
        entity.FindProperty(nameof(NoticeDraft.LeaseManagementId)).Should().NotBeNull();
        entity.FindProperty(nameof(NoticeDraft.TenantAccountId)).Should().NotBeNull();
        entity.FindProperty(nameof(NoticeDraft.RecipientLeaseManagementPartyId)).Should().NotBeNull();
        entity.FindProperty(nameof(NoticeDraft.LeaseAgreementId)).Should().NotBeNull();
        entity.FindProperty(nameof(NoticeDraft.LeaseAddendumId)).Should().NotBeNull();
        entity.FindProperty(nameof(NoticeDraft.TenantLedgerEntryId)).Should().NotBeNull();

        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.RecipientLeaseManagementPartyId),
                nameof(NoticeDraft.LeaseManagementId),
                nameof(NoticeDraft.PortfolioId),
            }));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.TenantAccountId),
                nameof(NoticeDraft.LeaseManagementId),
                nameof(NoticeDraft.PortfolioId),
            }));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.LeaseAgreementId),
                nameof(NoticeDraft.LeaseManagementId),
                nameof(NoticeDraft.PortfolioId),
            }));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.TenantLedgerEntryId),
                nameof(NoticeDraft.TenantAccountId),
                nameof(NoticeDraft.PortfolioId),
            }));
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.LeaseAddendumId),
                nameof(NoticeDraft.LeaseManagementId),
                nameof(NoticeDraft.PortfolioId),
            }));

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.PortfolioId),
                nameof(NoticeDraft.TenantLedgerEntryId),
                nameof(NoticeDraft.RecipientLeaseManagementPartyId),
                nameof(NoticeDraft.NoticeType),
            }));
        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(NoticeDraft.PortfolioId),
                nameof(NoticeDraft.LeaseManagementId),
                nameof(NoticeDraft.LeaseAgreementId),
                nameof(NoticeDraft.RecipientLeaseManagementPartyId),
                nameof(NoticeDraft.NoticeType),
            }));
    }

    [Fact]
    public void TenantNoticeWorkItem_IsBoundToOneCanonicalRecipientParty()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(TenantNoticeWorkItem))!;

        entity.FindProperty(nameof(TenantNoticeWorkItem.RecipientLeaseManagementPartyId))
            .Should().NotBeNull();
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(TenantNoticeWorkItem.RecipientLeaseManagementPartyId),
                nameof(TenantNoticeWorkItem.LeaseManagementId),
                nameof(TenantNoticeWorkItem.PortfolioId),
            }));
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
