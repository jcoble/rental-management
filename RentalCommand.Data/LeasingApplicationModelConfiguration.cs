using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class LeasingApplicationModelConfiguration
{
    internal static void ConfigureLeasingApplications(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoticeDraft>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NoticeType).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(40);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.GenerationPrompt).HasMaxLength(8000);
            entity.Property(e => e.ApprovedChannels).HasMaxLength(100);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.LeaseManagementId);
            entity.HasIndex(e => e.TenantAccountId);
            entity.HasIndex(e => e.RecipientLeaseManagementPartyId);
            entity.HasIndex(e => e.LeaseAgreementId);
            entity.HasIndex(e => e.LeaseAddendumId);
            entity.HasIndex(e => e.TenantLedgerEntryId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new
                { e.PortfolioId, e.TenantLedgerEntryId, e.RecipientLeaseManagementPartyId, e.NoticeType })
                .IsUnique()
                .HasFilter("\"TenantLedgerEntryId\" IS NOT NULL AND \"Status\" IN ('Draft','Approved')")
                .HasDatabaseName("UX_NoticeDrafts_OpenLedgerNotice");
            entity.HasIndex(e => new
                { e.PortfolioId, e.LeaseManagementId, e.LeaseAgreementId,
                    e.RecipientLeaseManagementPartyId, e.NoticeType })
                .IsUnique()
                .HasFilter("\"TenantLedgerEntryId\" IS NULL AND \"LeaseAgreementId\" IS NOT NULL AND \"Status\" IN ('Draft','Approved')")
                .HasDatabaseName("UX_NoticeDrafts_OpenAgreementNotice");
            entity.HasIndex(e => e.TenantNoticePolicyId);
            entity.HasIndex(e => e.WorkspaceNoticeTemplateVersionId);
            entity.HasIndex(e => e.RenderedNoticeId).IsUnique();
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.LeaseManagement)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantAccount)
                .WithMany()
                .HasForeignKey(e => new { e.TenantAccountId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.RecipientLeaseManagementParty)
                .WithMany()
                .HasForeignKey(e => new { e.RecipientLeaseManagementPartyId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAgreementId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAddendum)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAddendumId, e.LeaseManagementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.LeaseManagementId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TenantLedgerEntry)
                .WithMany()
                .HasForeignKey(e => new { e.TenantLedgerEntryId, e.TenantAccountId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.TenantAccountId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Conversation)
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RentalListing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Headline).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Rent).HasPrecision(18, 2);
            entity.Property(e => e.SecurityDeposit).HasPrecision(18, 2);
            entity.Property(e => e.Bedrooms).HasPrecision(5, 2);
            entity.Property(e => e.Bathrooms).HasPrecision(5, 2);
            entity.Property(e => e.LeaseTerms).HasMaxLength(1000);
            entity.Property(e => e.PetPolicy).HasMaxLength(1000);
            entity.Property(e => e.Utilities).HasMaxLength(1000);
            entity.Property(e => e.Parking).HasMaxLength(1000);
            entity.Property(e => e.Amenities).HasMaxLength(2000);
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.PropertyId);
            entity.HasIndex(e => e.UnitId);
            entity.HasIndex(e => new { e.PortfolioId, e.UnitId })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL");
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Unit)
                .WithMany(u => u.RentalListings)
                .HasForeignKey(e => new { e.UnitId, e.PropertyId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PropertyId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ListingPhoto>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Caption).HasMaxLength(300);
            entity.Property(e => e.FileName).HasMaxLength(260);
            entity.Property(e => e.Sha256).HasMaxLength(64);
            entity.HasIndex(e => new { e.RentalListingId, e.Position }).IsUnique();
            entity.HasOne(e => e.RentalListing)
                .WithMany(e => e.Photos)
                .HasForeignKey(e => new { e.RentalListingId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => new { Id = e.StoredFileId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ListingPublication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.ExternalListingId).HasMaxLength(200);
            entity.Property(e => e.ListingUrl).HasMaxLength(1000);
            entity.Property(e => e.ApplicationUrl).HasMaxLength(1000);
            entity.Property(e => e.ManagementUrl).HasMaxLength(1000);
            entity.Property(e => e.LastConfirmedExternalStatus).HasMaxLength(120);
            entity.Property(e => e.LastDeliveryKey).HasMaxLength(200);
            entity.Property(e => e.LastDeliveryStatus).HasMaxLength(80);
            entity.Property(e => e.LastDeliveryError).HasMaxLength(2000);
            entity.HasIndex(e => new { e.RentalListingId, e.ProviderKey, e.Mode }).IsUnique();
            entity.HasOne(e => e.RentalListing)
                .WithMany(e => e.Publications)
                .HasForeignKey(e => new { e.RentalListingId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalListingSignal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderMessageKey).IsRequired().HasMaxLength(300);
            entity.Property(e => e.SignalType).IsRequired().HasMaxLength(80);
            entity.Property(e => e.SuggestedExternalListingId).HasMaxLength(200);
            entity.Property(e => e.SuggestedListingUrl).HasMaxLength(1000);
            entity.Property(e => e.SuggestedExternalStatus).HasMaxLength(120);
            entity.Property(e => e.Disposition).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => new { e.PortfolioId, e.ProviderMessageKey }).IsUnique();
            entity.HasIndex(e => new { e.ListingPublicationId, e.Disposition, e.ReceivedAtUtc });
            entity.HasOne(e => e.ListingPublication)
                .WithMany(e => e.ExternalSignals)
                .HasForeignKey(e => new { e.ListingPublicationId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RentalApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.CurrentAddress).HasMaxLength(500);
            entity.Property(e => e.Employer).HasMaxLength(200);
            entity.Property(e => e.MonthlyIncome).HasPrecision(18, 2);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.DecisionReason).HasMaxLength(1000);
            entity.Property(e => e.ConsentIpAddress).HasMaxLength(64);
            // Provenance of the photo-ID/pay-stub autofill, stored as Postgres jsonb.
            entity.Property(e => e.IdExtractedFields).HasColumnType("jsonb");
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SubmittedAtUtc);
            entity.HasIndex(e => new { e.PreparedLeaseManagementId, e.PortfolioId })
                .IsUnique()
                .HasFilter("\"PreparedLeaseManagementId\" IS NOT NULL");
            entity.HasQueryFilter(e => e.DeletedAt == null);
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.RentalApplications)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Unit)
                .WithMany()
                .HasForeignKey(e => e.UnitId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ApprovedTenant)
                .WithMany()
                .HasForeignKey(e => e.ApprovedTenantId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.PreparedLeaseManagement)
                .WithOne(e => e.PreparedFromApplication)
                .HasForeignKey<RentalApplication>(e => new { e.PreparedLeaseManagementId, e.PortfolioId })
                .HasPrincipalKey<LeaseManagement>(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApplicantScreening>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.ProviderKey).HasMaxLength(80);
            entity.Property(e => e.ProviderDisplayName).IsRequired().HasMaxLength(160);
            entity.Property(e => e.ProviderReference).HasMaxLength(200);
            entity.Property(e => e.ProviderHostedUrl).HasMaxLength(2000);
            entity.Property(e => e.OperationKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Decision).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.DecisionReason).HasMaxLength(1000);
            entity.Property(e => e.CreditReportingAgencyName).HasMaxLength(300);
            entity.Property(e => e.CreditReportingAgencyAddress).HasMaxLength(500);
            entity.Property(e => e.CreditReportingAgencyPhone).HasMaxLength(80);
            entity.HasIndex(e => new { e.PortfolioId, e.ApplicationId, e.OperationKey }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.ApplicationId, e.LastStatusAtUtc });
            entity.HasIndex(e => new { e.ProviderKey, e.ProviderReference })
                .IsUnique()
                .HasFilter("\"ProviderKey\" IS NOT NULL AND \"ProviderReference\" IS NOT NULL");
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Application)
                .WithMany(e => e.Screenings)
                .HasForeignKey(e => new { e.ApplicationId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.DecisionRecordedByUser)
                .WithMany()
                .HasForeignKey(e => e.DecisionRecordedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApplicantScreeningMilestone>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(80);
            entity.Property(e => e.DeliveryId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => new { e.Source, e.DeliveryId }).IsUnique();
            entity.HasIndex(e => new { e.PortfolioId, e.ApplicantScreeningId, e.OccurredAtUtc });
            entity.HasOne(e => e.ApplicantScreening)
                .WithMany(e => e.Milestones)
                .HasForeignKey(e => new { e.ApplicantScreeningId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdverseActionNotice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.CreditReportingAgency).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.ApplicationId);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Application)
                .WithMany()
                .HasForeignKey(e => e.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => e.StoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

    }
}

