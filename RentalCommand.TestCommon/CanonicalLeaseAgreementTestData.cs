using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.TestCommon;

public static class CanonicalLeaseAgreementTestData
{
    public static void MarkFullyExecuted(
        this RentalCommandDbContext db,
        LeaseAgreement agreement,
        LeaseManagementParty responsibleParty,
        Tenant tenant,
        int actorUserId,
        DateTime executedAtUtc)
    {
        db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = agreement.PortfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = responsibleParty.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}".Trim(),
            EmailSnapshot = tenant.Email
                ?? $"tenant-{tenant.Id}@example.test",
            SigningOrder = 1,
            IsRequired = true,
        });
        db.SaveChanges();

        var issuedFile = NewStoredFile(
            agreement.PortfolioId,
            $"agreement-{agreement.Id}-issued.pdf",
            executedAtUtc);
        var executedFile = NewStoredFile(
            agreement.PortfolioId,
            $"agreement-{agreement.Id}-executed.pdf",
            executedAtUtc);
        db.StoredFiles.AddRange(issuedFile, executedFile);
        db.SaveChanges();

        var issuedArtifact = NewArtifact(
            agreement.PortfolioId,
            issuedFile,
            LegalDocumentArtifactKind.IssuedAgreement,
            actorUserId,
            executedAtUtc,
            'a');
        var executedArtifact = NewArtifact(
            agreement.PortfolioId,
            executedFile,
            LegalDocumentArtifactKind.ExecutedAgreement,
            actorUserId,
            executedAtUtc,
            'c');
        db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = executedAtUtc;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = executedAtUtc;
        db.SaveChanges();
    }

    private static StoredFile NewStoredFile(int portfolioId, string fileName, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        FileName = fileName,
        FilePath = $"test/{Guid.NewGuid():N}/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact NewArtifact(
        int portfolioId,
        StoredFile file,
        LegalDocumentArtifactKind kind,
        int actorUserId,
        DateTime now,
        char hashCharacter) => new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            StoredFileId = file.Id,
            ArtifactKind = kind,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            ByteLength = file.FileSize,
            ContentSha256 = new string(hashCharacter, 64),
            LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement
            ? new string('b', 64)
            : null,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };
}
