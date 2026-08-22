using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class DocumentScanningModelConfiguration
{
    internal static void ConfigureDocumentsAndScanning(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(260);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.ContentSha256).HasMaxLength(64).HasColumnType("char(64)");
            entity.Property(e => e.EntityType).HasMaxLength(120);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => new { e.PortfolioId, e.EntityType, e.EntityId, e.ContentSha256 })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL AND \"ContentSha256\" IS NOT NULL")
                .HasDatabaseName("UX_StoredFiles_Active_Target_ContentSha256");
            // #7 ScanProcessingWorker polls StoredFiles.FirstOrDefaultAsync(f => f.FilePath == …) every
            // ~2s; the column was unindexed → sequential scan of an ever-growing table each poll.
            entity.HasIndex(e => e.FilePath).HasDatabaseName("IX_StoredFiles_FilePath");
            entity.HasQueryFilter(e => e.DeletedAt == null);
        });

        modelBuilder.Entity<PendingFileUpload>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Purpose).IsRequired().HasMaxLength(80);
            entity.Property(e => e.OperationKeyHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.RequestFingerprint).IsRequired().HasMaxLength(64);
            entity.Property(e => e.StoragePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(260);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.State).HasConversion<int>();
            entity.Property(e => e.CleanupClaimOwner).HasMaxLength(200);
            entity.HasIndex(e => new { e.PortfolioId, e.ActorScopeId, e.Purpose, e.OperationKeyHash }).IsUnique();
            entity.HasIndex(e => new { e.State, e.CreatedAtUtc, e.CleanupClaimExpiresAtUtc });
            entity.HasIndex(e => e.StoredFileId);
            entity.HasOne(e => e.StoredFile)
                .WithMany()
                .HasForeignKey(e => e.StoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.RenderMode).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.DraftHtml).HasColumnType("text");
            entity.Property(e => e.IsSandboxSeeded).HasDefaultValue(false);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.PortfolioId, e.Kind, e.Status });
            entity.HasIndex(e => new { e.PortfolioId, e.Kind, e.DefaultForPortfolio });
            entity.HasIndex(e => e.PropertyId);
            entity.HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DocumentTemplate_Version", "\"Version\" >= 1");
            });
            entity.HasOne(e => e.Portfolio)
                .WithMany(p => p.DocumentTemplates)
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.OriginalStoredFile)
                .WithMany()
                .HasForeignKey(e => e.OriginalStoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.CompiledStoredFile)
                .WithMany()
                .HasForeignKey(e => e.CompiledStoredFileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentTemplateField>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FieldKey).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Label).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.SignerRole).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.DefaultText).HasMaxLength(500);
            entity.HasIndex(e => new { e.DocumentTemplateId, e.PortfolioId });
            entity.HasIndex(e => new { e.PortfolioId, e.DocumentTemplateId, e.FieldKey });
            entity.HasQueryFilter(e => e.DocumentTemplate!.Portfolio!.DeletedAt == null);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DocumentTemplateField_Page", "\"PageNumber\" >= 1");
                t.HasCheckConstraint("CK_DocumentTemplateField_XPct", "\"XPct\" >= 0 AND \"XPct\" <= 1");
                t.HasCheckConstraint("CK_DocumentTemplateField_YPct", "\"YPct\" >= 0 AND \"YPct\" <= 1");
                t.HasCheckConstraint("CK_DocumentTemplateField_WidthPct", "\"WidthPct\" > 0 AND \"WidthPct\" <= 1");
                t.HasCheckConstraint("CK_DocumentTemplateField_HeightPct", "\"HeightPct\" > 0 AND \"HeightPct\" <= 1");
                t.HasCheckConstraint("CK_DocumentTemplateField_XExtent", "\"XPct\" + \"WidthPct\" <= 1");
                t.HasCheckConstraint("CK_DocumentTemplateField_YExtent", "\"YPct\" + \"HeightPct\" <= 1");
            });
            entity.HasOne(e => e.DocumentTemplate)
                .WithMany(t => t.Fields)
                .HasForeignKey(e => new { e.DocumentTemplateId, e.PortfolioId })
                .HasPrincipalKey(t => new { t.Id, t.PortfolioId })
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_DocumentTemplateFields_DocumentTemplates_Scope");
        });

        modelBuilder.Entity<ScanDraft>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.SourceContentSha256).HasColumnType("char(64)");
            entity.Property(e => e.SourceLabel).HasMaxLength(100);
            entity.Property(e => e.CaptureExperience).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ThumbnailPath).HasMaxLength(1024);
            entity.Property(e => e.TargetEntityType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ModelId).HasMaxLength(120);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.ReviewedBy).HasMaxLength(200);
            entity.Property(e => e.ProcessingClaimOwner).HasMaxLength(200);
            entity.Property(e => e.CostUsd).HasPrecision(18, 4);
            entity.Property(e => e.ExtractedFields).HasColumnType("jsonb");
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => new { e.Status, e.ProcessingClaimExpiresAtUtc, e.CreatedAt, e.Id });
            entity.HasIndex(e => e.BatchId);
            entity.HasIndex(e => e.SourceStoredFileId);
            entity.HasIndex(e => e.CapturePropertyId);
            entity.HasIndex(e => e.CaptureUnitId);
            entity.HasIndex(e => e.CaptureLeaseManagementId);
            entity.HasIndex(e => e.CaptureLeaseAgreementId);
            entity.HasIndex(e => e.CaptureTenantAccountId);
            entity.HasIndex(e => e.CaptureTenantLedgerEntryId);
            entity.HasIndex(e => e.CaptureWorkOrderId);
            entity.HasIndex(e => e.CaptureApplicationId);
            entity.HasIndex(e => e.CaptureRentalListingId);
            entity.HasIndex(e => new { e.PortfolioId, e.SourceContentSha256 });
            // The owning batch is optional (single-file scans carry null). SetNull rather than Cascade
            // so a draft (and the record it created) survives if a batch row is ever removed.
            entity.HasOne(e => e.Batch)
                .WithMany()
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.SourceStoredFile)
                .WithMany()
                .HasForeignKey(e => e.SourceStoredFileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureProperty)
                .WithMany()
                .HasForeignKey(e => e.CapturePropertyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureUnit)
                .WithMany()
                .HasForeignKey(e => e.CaptureUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureLeaseManagement)
                .WithMany()
                .HasForeignKey(e => e.CaptureLeaseManagementId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureLeaseAgreement)
                .WithMany()
                .HasForeignKey(e => e.CaptureLeaseAgreementId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureTenantAccount)
                .WithMany()
                .HasForeignKey(e => e.CaptureTenantAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureTenantLedgerEntry)
                .WithMany()
                .HasForeignKey(e => e.CaptureTenantLedgerEntryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureWorkOrder)
                .WithMany()
                .HasForeignKey(e => e.CaptureWorkOrderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureApplication)
                .WithMany()
                .HasForeignKey(e => e.CaptureApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CaptureRentalListing)
                .WithMany()
                .HasForeignKey(e => e.CaptureRentalListingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ScanBatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.TargetEntityType).IsRequired().HasMaxLength(120);
            // Stored as the string enum name to match the app-wide string-enum convention.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PortfolioId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }
}

