using System.Net;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class EsignModelConfiguration
{
    internal static void ConfigureEsign(this ModelBuilder modelBuilder)
    {
        // Npgsql's inet type is represented by IPAddress. Keep the HTTP/domain boundary as a
        // normalized string while making the provider mapping explicit; EF handles nulls before
        // invoking the converter.
        var ipAddressConverter = new ValueConverter<string?, IPAddress?>(
            value => value == null ? null : IPAddress.Parse(value),
            value => value == null ? null : value.ToString());

        modelBuilder.Entity<SignatureRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.PortfolioId });
            entity.Property(e => e.PublicId).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ProviderEnvelopeId).HasMaxLength(200);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(300);
            entity.Property(e => e.FailureCode).HasMaxLength(100);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.ExecutionClaimOwner).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.Provider, e.ProviderEnvelopeId }).IsUnique()
                .HasFilter("\"ProviderEnvelopeId\" IS NOT NULL");
            entity.HasIndex(e => e.LeaseAgreementId).IsUnique()
                .HasFilter("\"LeaseAgreementId\" IS NOT NULL AND \"Status\" IN ('Prepared','Dispatching','AwaitingSignatures','Viewed','PartiallySigned','ExecutionPending')");
            entity.HasIndex(e => e.LeaseAddendumId).IsUnique()
                .HasFilter("\"LeaseAddendumId\" IS NOT NULL AND \"Status\" IN ('Prepared','Dispatching','AwaitingSignatures','Viewed','PartiallySigned','ExecutionPending')");
            entity.HasIndex(e => new { e.Status, e.NextAttemptAtUtc, e.PreparedAtUtc, e.Id });
            entity.HasIndex(e => new { e.ExecutionClaimExpiresAtUtc, e.Id })
                .HasFilter("\"ExecutionClaimToken\" IS NOT NULL");
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_SignatureRequest_Parent", "(\"LeaseAgreementId\" IS NULL) <> (\"LeaseAddendumId\" IS NULL)");
                table.HasCheckConstraint("CK_SignatureRequest_Execution", "(\"ExecutedArtifactId\" IS NULL AND \"CompletedAtUtc\" IS NULL) OR (\"ExecutedArtifactId\" IS NOT NULL AND \"CompletedAtUtc\" IS NOT NULL AND \"Status\" = 'Completed')");
                table.HasCheckConstraint("CK_SignatureRequest_Claim", "(\"ExecutionClaimOwner\" IS NULL AND \"ExecutionClaimToken\" IS NULL AND \"ExecutionClaimExpiresAtUtc\" IS NULL) OR (\"ExecutionClaimOwner\" IS NOT NULL AND \"ExecutionClaimToken\" IS NOT NULL AND \"ExecutionClaimExpiresAtUtc\" IS NOT NULL)");
            });
            entity.HasOne(e => e.Portfolio)
                .WithMany()
                .HasForeignKey(e => e.PortfolioId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAgreement)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAgreementId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.LeaseAddendum)
                .WithMany()
                .HasForeignKey(e => new { e.LeaseAddendumId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.IssuedArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.IssuedArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ExecutedArtifact)
                .WithMany()
                .HasForeignKey(e => new { e.ExecutedArtifactId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatedByUser).WithMany().HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SignatureSigner>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.SignatureRequestId, e.PortfolioId });
            entity.Property(e => e.NameSnapshot).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EmailSnapshot).IsRequired().HasMaxLength(320);
            entity.Property(e => e.TokenHash).IsRequired().HasColumnType("char(64)");
            entity.Property(e => e.TypedName).HasMaxLength(200);
            entity.Property(e => e.IpAddress).HasConversion(ipAddressConverter).HasColumnType("inet");
            entity.Property(e => e.UserAgent).HasMaxLength(1000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.SignatureType).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.SignatureRequestId, e.SigningOrder }).IsUnique();
            entity.HasOne(e => e.SignatureRequest)
                .WithMany(r => r.Signers)
                .HasForeignKey(e => new { e.SignatureRequestId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AgreementSigner).WithMany()
                .HasForeignKey(e => new { e.AgreementSignerId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AddendumSigner).WithMany()
                .HasForeignKey(e => new { e.AddendumSignerId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.DrawnSignatureStoredFile).WithMany()
                .HasForeignKey(e => new { e.DrawnSignatureStoredFileId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SignatureAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.IpAddress).HasConversion(ipAddressConverter).HasColumnType("inet");
            entity.Property(e => e.UserAgent).HasMaxLength(1000);
            entity.Property(e => e.Detail).HasMaxLength(2000);
            entity.HasIndex(e => new { e.PortfolioId, e.SignatureRequestId, e.OccurredAtUtc, e.Id });
            entity.HasOne(e => e.SignatureRequest)
                .WithMany(r => r.AuditEvents)
                .HasForeignKey(e => new { e.SignatureRequestId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SignatureSigner)
                .WithMany()
                .HasForeignKey(e => new { e.SignatureSignerId, e.SignatureRequestId, e.PortfolioId })
                .HasPrincipalKey(e => new { e.Id, e.SignatureRequestId, e.PortfolioId })
                .OnDelete(DeleteBehavior.Restrict);
        });

    }
}

