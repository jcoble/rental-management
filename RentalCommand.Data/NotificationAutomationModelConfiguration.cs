using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data;

internal static class NotificationAutomationModelConfiguration
{
    internal static void ConfigureNotificationsAndAutomation(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Platform).IsRequired().HasMaxLength(20);
            // One physical device token may sign into multiple workspaces, but only once per workspace.
            entity.HasIndex(e => new { e.PortfolioId, e.Token }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.UserId });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Type).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.NavigationExperience).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.NavigationDestination).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.NavigationResourceKind).HasMaxLength(120);
            entity.Property(e => e.NavigationParentResourceKind).HasMaxLength(120);
            entity.Property(e => e.NavigationChildResourceKind).HasMaxLength(120);
            entity.Property(e => e.NavigationAction).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.NavigationFallbackDestination).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.RelatedEntityType).HasMaxLength(120);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Notifications_NavigationIntentShape",
                """
                (
                    "NavigationExperience" IS NULL
                    AND "NavigationDestination" IS NULL
                    AND "NavigationAccessContextId" IS NULL
                    AND "NavigationAccessRevision" IS NULL
                    AND "NavigationResourceKind" IS NULL
                    AND "NavigationResourceId" IS NULL
                    AND "NavigationParentResourceKind" IS NULL
                    AND "NavigationParentResourceId" IS NULL
                    AND "NavigationChildResourceKind" IS NULL
                    AND "NavigationChildResourceId" IS NULL
                    AND "NavigationAction" IS NULL
                    AND "NavigationExpiresAtUtc" IS NULL
                    AND "NavigationFallbackDestination" IS NULL
                )
                OR
                (
                    "NavigationExperience" IS NOT NULL
                    AND "NavigationExperience" IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')
                    AND "NavigationDestination" IS NOT NULL
                    AND "NavigationDestination" IN (
                        'Home', 'Notifications', 'Rentals', 'Owners', 'Money', 'Work', 'Inbox',
                        'UnitSummary', 'UnitTenantLease', 'UnitMoney', 'UnitMaintenance', 'UnitRecords',
                        'TenantLedgerEntry', 'Expense', 'ScanDraft', 'Message', 'WorkOrder',
                        'TechnicianWork', 'LeasingRental', 'LeasingApplication', 'LeasingAppointment',
                        'LeasingConversation', 'LeasingMoveIn', 'TenantAccount'
                    )
                    AND "NavigationAccessContextId" IS NOT NULL
                    AND "NavigationAccessContextId" > 0
                    AND "NavigationAccessRevision" IS NOT NULL
                    AND "NavigationAccessRevision" > 0
                    AND "NavigationAction" IS NOT NULL
                    AND "NavigationAction" IN ('Open', 'Review', 'Resolve')
                    AND "NavigationExpiresAtUtc" IS NOT NULL
                    AND "NavigationFallbackDestination" IS NOT NULL
                    AND "NavigationFallbackDestination" IN ('Home', 'Notifications')
                    AND (("NavigationResourceKind" IS NULL AND "NavigationResourceId" IS NULL)
                        OR ("NavigationResourceKind" IS NOT NULL
                            AND btrim("NavigationResourceKind") <> ''
                            AND "NavigationResourceId" IS NOT NULL
                            AND "NavigationResourceId" > 0))
                    AND (("NavigationParentResourceKind" IS NULL AND "NavigationParentResourceId" IS NULL)
                        OR ("NavigationParentResourceKind" IS NOT NULL
                            AND btrim("NavigationParentResourceKind") <> ''
                            AND "NavigationParentResourceId" IS NOT NULL
                            AND "NavigationParentResourceId" > 0))
                    AND (("NavigationChildResourceKind" IS NULL AND "NavigationChildResourceId" IS NULL)
                        OR ("NavigationChildResourceKind" IS NOT NULL
                            AND btrim("NavigationChildResourceKind") <> ''
                            AND "NavigationChildResourceId" IS NOT NULL
                            AND "NavigationChildResourceId" > 0))
                )
                """));
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationReadState>(entity =>
        {
            entity.HasKey(e => new { e.PortfolioId, e.NotificationId, e.UserId });
            entity.HasIndex(e => new { e.PortfolioId, e.UserId, e.NotificationId });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Notification)
                .WithMany(e => e.ReadStates)
                .HasForeignKey(e => new { e.NotificationId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EnableRentCharges).HasDefaultValue(true);
            entity.Property(e => e.EnableLeaseExpiryReminders).HasDefaultValue(true);
            entity.Property(e => e.EnableRecurringMaintenance).HasDefaultValue(true);
            entity.Property(e => e.EnableMorningBriefing).HasDefaultValue(true);
            entity.Property(e => e.RentChargeLeadDays).HasDefaultValue(5);
            entity.Property(e => e.LateFeeGraceDays).HasDefaultValue(5);
            entity.Property(e => e.LeaseExpiryReminderDays).HasDefaultValue(60);
            entity.Property(e => e.MorningBriefingSendHourLocal).HasDefaultValue(8);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_AutomationSettings_RentChargesAlwaysEnabled",
                "\"EnableRentCharges\" = TRUE"));
            entity.HasIndex(e => e.PortfolioId).IsUnique();
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessagingProviderSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SmsProvider).HasMaxLength(32);
            entity.Property(e => e.SmsCredentialACipherText).HasMaxLength(4000);
            entity.Property(e => e.SmsCredentialBCipherText).HasMaxLength(4000);
            entity.Property(e => e.SmsCredentialCCipherText).HasMaxLength(4000);
            entity.Property(e => e.SmsFromNumberCipherText).HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkspaceLlmCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(32);
            entity.Property(e => e.ModelId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.ApiKeyCipherText).IsRequired().HasMaxLength(4000);
            entity.HasIndex(e => e.PortfolioId).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LlmUsageEvidence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(32);
            entity.Property(e => e.ModelId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Feature).IsRequired().HasMaxLength(80);
            entity.Property(e => e.EstimatedCostUsd).HasPrecision(18, 8);
            entity.HasIndex(e => new { e.PortfolioId, e.OccurredAtUtc });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAlertPreference>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PortfolioId, e.UserId }).IsUnique();
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamRoutingRule>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_TeamRoutingRules_WorkspaceOnlyTopics",
                "\"PropertyId\" IS NULL OR \"Topic\" NOT IN ('AccountAndSecurity', 'MorningBriefing')"));
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Topic).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => new { e.PortfolioId, e.Topic })
                .HasDatabaseName("IX_TeamRoutingRules_PortfolioId_Topic_Workspace")
                .HasFilter("\"PropertyId\" IS NULL")
                .IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.Topic, e.PropertyId })
                .HasFilter("\"PropertyId\" IS NOT NULL")
                .IsUnique();
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property).WithMany().HasForeignKey(e => e.PropertyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamRoutingRuleRecipient>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => new { e.TeamRoutingRuleId, e.UserId }).IsUnique();
            entity.HasOne(e => e.Rule).WithMany(e => e.Recipients)
                .HasForeignKey(e => new { e.TeamRoutingRuleId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantNoticePolicy>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.AutomationKey).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Classification).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.FailureBehavior).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.ReviewedJurisdictionCode).HasMaxLength(80);
            entity.HasIndex(e => new { e.PortfolioId, e.AutomationKey }).IsUnique();
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.TemplateVersion).WithMany()
                .HasForeignKey(e => new { e.WorkspaceNoticeTemplateVersionId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SystemNoticeTemplateVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.SystemKey).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Classification).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(8000);
            entity.Property(e => e.JurisdictionCode).HasMaxLength(80);
            entity.Property(e => e.Provenance).IsRequired().HasMaxLength(1000);
            entity.HasIndex(e => new { e.SystemKey, e.Version }).IsUnique();
            entity.HasData(SuppliedNoticeTemplateBaseline.V1.Select(template => new
            {
                template.Id,
                template.SystemKey,
                Version = SuppliedNoticeTemplateBaseline.Version,
                template.Classification,
                template.Subject,
                template.Body,
                JurisdictionCode = (string?)null,
                Provenance = SuppliedNoticeTemplateBaseline.Provenance,
                SuppliedNoticeTemplateBaseline.PublishedAtUtc,
            }));
        });

        modelBuilder.Entity<WorkspaceNoticeTemplateVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.SystemKey).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(8000);
            entity.Property(e => e.JurisdictionCode).HasMaxLength(80);
            entity.HasIndex(e => new { e.PortfolioId, e.SystemKey, e.Version }).IsUnique();
            entity.HasOne(e => e.Portfolio).WithMany().HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.BasedOnSystemTemplateVersion).WithMany().HasForeignKey(e => e.BasedOnSystemTemplateVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RenderedNotice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(8000);
            entity.Property(e => e.ContentSha256).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TemplateProvenance).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.JurisdictionCode).HasMaxLength(80);
            entity.HasIndex(e => new { e.PortfolioId, e.NoticeDraftId }).IsUnique();
            entity.HasOne(e => e.Portfolio).WithMany()
                .HasForeignKey(e => e.PortfolioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NoticeDeliveryEvidence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RecipientRole).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Channel).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Destination).IsRequired().HasMaxLength(500);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(300);
            entity.HasIndex(e => e.OutboxMessageId).IsUnique();
            entity.HasIndex(e => e.ConversationMessageId).IsUnique()
                .HasFilter("\"ConversationMessageId\" IS NOT NULL");
            entity.HasIndex(e => new { e.RenderedNoticeId, e.RecipientLeaseManagementPartyId, e.Channel }).IsUnique();
            entity.HasOne(e => e.RenderedNotice).WithMany()
                .HasForeignKey(e => new { e.RenderedNoticeId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.RecipientLeaseManagementParty).WithMany()
                .HasForeignKey(e => new { e.RecipientLeaseManagementPartyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.OutboxMessage).WithMany().HasForeignKey(e => e.OutboxMessageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ConversationMessage).WithMany()
                .HasForeignKey(e => e.ConversationMessageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantNoticeWorkItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.BusinessKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ClaimOwner).HasMaxLength(200);
            entity.HasIndex(e => e.BusinessKey).IsUnique();
            entity.HasIndex(e => new { e.Status, e.DueAtUtc, e.ClaimExpiresAtUtc, e.Id });
            entity.HasIndex(e => new { e.PortfolioId, e.TenantLedgerEntryId })
                .HasFilter("\"TenantLedgerEntryId\" IS NOT NULL");
            entity.HasIndex(e => new
                { e.RecipientLeaseManagementPartyId, e.LeaseManagementId, e.PortfolioId });
            entity.HasOne(e => e.Policy).WithMany()
                .HasForeignKey(e => new { e.TenantNoticePolicyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.RecipientLeaseManagementParty).WithMany()
                .HasForeignKey(e => new
                    { e.RecipientLeaseManagementPartyId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

    }
}

