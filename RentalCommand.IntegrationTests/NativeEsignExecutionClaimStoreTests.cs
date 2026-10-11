using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Esign;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for native e-sign execution leases and fencing.</summary>
public sealed class NativeEsignExecutionClaimStoreTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _postgres;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _postgres.StartAsync();
        }
        catch
        {
            return;
        }

        _dockerAvailable = true;
        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Claims_are_disjoint_expiry_reclaimable_and_completion_is_fenced()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        await SeedRequestsAsync(now, 4);

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var batches = await Task.WhenAll(
            new NativeEsignExecutionClaimStore(dbA)
                .ClaimBatchAsync("engine-a", TimeSpan.FromMinutes(10), 2),
            new NativeEsignExecutionClaimStore(dbB)
                .ClaimBatchAsync("engine-b", TimeSpan.FromMinutes(10), 2));

        var claims = batches.SelectMany(batch => batch).ToArray();
        claims.Should().HaveCount(4);
        claims.Select(claim => claim.Id).Should().OnlyHaveUniqueItems();
        claims.Select(claim => claim.ClaimToken).Should().OnlyHaveUniqueItems();
        dbA.Database.CurrentTransaction.Should().BeNull("blob work must start after claim commit");
        dbB.Database.CurrentTransaction.Should().BeNull();

        await using (var cannotSteal = NewContext())
        {
            (await new NativeEsignExecutionClaimStore(cannotSteal)
                .ClaimBatchAsync("engine-c", TimeSpan.FromMinutes(10), 4))
                .Should().BeEmpty("unexpired execution leases cannot be stolen");
        }

        var stale = claims[0];
        await using (var expire = NewContext())
        {
            await expire.SignatureRequests.Where(request => request.Id == stale.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(request => request.ExecutionClaimExpiresAtUtc, now.AddSeconds(-1)));
        }

        NativeEsignExecutionClaim replacement;
        await using (var reclaim = NewContext())
        {
            replacement = (await new NativeEsignExecutionClaimStore(reclaim)
                .ClaimBatchAsync("replacement", TimeSpan.FromMinutes(10), 1)).Single();
        }
        replacement.Id.Should().Be(stale.Id);
        replacement.ClaimToken.Should().NotBe(stale.ClaimToken);

        await using var complete = NewContext();
        var store = new NativeEsignExecutionClaimStore(complete);
        (await store.ReleaseForRetryAsync(stale.Id, stale.ClaimToken, "stale"))
            .Should().Be(0, "an expired worker cannot clear a newer execution lease");
        (await store.ReleaseForRetryAsync(replacement.Id, replacement.ClaimToken, "retry"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Claim_statement_excludes_requests_with_unsigned_signers()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var ids = await SeedRequestsAsync(now, 2);
        await using (var unsigned = NewContext())
        {
            await unsigned.SignatureSigners
                .Where(signer => signer.SignatureRequestId == ids[1])
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(signer => signer.Status, SignatureSignerStatus.Pending));
        }

        await using var db = NewContext();
        var claims = await new NativeEsignExecutionClaimStore(db)
            .ClaimBatchAsync("engine", TimeSpan.FromMinutes(10), 10);
        claims.Should().ContainSingle();
        claims.Single().Id.Should().Be(ids[0]);
    }

    [SkippableFact]
    public async Task Direct_successor_uniqueness_rejects_a_correction_and_renewal_branch()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        await SeedRequestsAsync(now, 1);

        await using var db = NewContext();
        var source = await db.LeaseAgreements.AsNoTracking().SingleAsync(agreement => agreement.Id == 4_000);
        db.LeaseAgreements.Add(Successor(source, 4_001, 2, LeaseAgreementChangeType.Correction, now));
        await db.SaveChangesAsync();

        db.LeaseAgreements.Add(Successor(source, 4_002, 3, LeaseAgreementChangeType.Renewal, now));
        var act = () => db.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("IX_LeaseAgreements_DurableDirectSuccessor");
    }

    [SkippableFact]
    public async Task Reciprocal_lineage_validator_rejects_a_mismatched_effective_date_at_commit()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        await SeedRequestsAsync(now, 1);

        await using var db = NewContext();
        var source = await db.LeaseAgreements.AsNoTracking().SingleAsync(agreement => agreement.Id == 4_000);
        var successor = Successor(source, 4_001, 2, LeaseAgreementChangeType.Correction, now);
        db.LeaseAgreements.Add(successor);
        await db.SaveChangesAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.LeaseAgreements.Where(agreement => agreement.Id == source.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(agreement => agreement.SupersededEffectiveOn, successor.GoverningFromOn.AddDays(1))
                .SetProperty(agreement => agreement.SupersededByAgreementId, successor.Id)
                .SetProperty(agreement => agreement.SupersessionRecordedAtUtc, now));

        var act = () => transaction.CommitAsync();
        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be("P0001");
        exception.Which.MessageText.Should().Contain("reciprocal direct successor");
    }

    private static LeaseAgreement Successor(
        LeaseAgreement source,
        int id,
        int version,
        LeaseAgreementChangeType changeType,
        DateTime now)
    {
        var isCorrection = changeType == LeaseAgreementChangeType.Correction;
        var governingFrom = isCorrection
            ? source.GoverningFromOn.AddDays(1)
            : source.TermEndOn!.Value.AddDays(1);
        return new LeaseAgreement
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            PortfolioId = source.PortfolioId,
            LeaseManagementId = source.LeaseManagementId,
            VersionNumber = version,
            AgreementNumber = $"AGR-BRANCH-{version}",
            ChangeType = changeType,
            CorrectionReason = isCorrection ? "Corrected integration-test agreement facts." : null,
            ReplacesAgreementId = isCorrection ? source.Id : null,
            RenewsAgreementId = isCorrection ? null : source.Id,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = isCorrection ? source.TermStartOn : governingFrom,
            TermEndOn = isCorrection ? source.TermEndOn : governingFrom.AddYears(1),
            GoverningFromOn = governingFrom,
            BaseRentAmount = source.BaseRentAmount,
            RentDueDay = source.RentDueDay,
            SecurityDepositObligation = source.SecurityDepositObligation,
            LateFeeAmount = source.LateFeeAmount,
            GracePeriodDays = source.GracePeriodDays,
            Currency = source.Currency,
            TermsSchemaVersion = source.TermsSchemaVersion,
            TermsPayload = source.TermsPayload,
            DocumentSourceVersionId = source.DocumentSourceVersionId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = source.CreatedByUserId,
            DraftRevision = 1,
        };
    }

    private async Task<int[]> SeedRequestsAsync(DateTime now, int count)
    {
        await using var db = NewContext();
        var portfolio = new Portfolio
        {
            Id = 1,
            Name = $"E-sign claims {Guid.NewGuid():N}",
            ManagementCompanyName = "Claims",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = 1,
            UserName = "claims@example.test",
            NormalizedUserName = "CLAIMS@EXAMPLE.TEST",
            Email = "claims@example.test",
            NormalizedEmail = "CLAIMS@EXAMPLE.TEST",
            DisplayName = "Claims Manager",
            CreatedAt = now,
        };
        var property = new Property
        {
            Id = 1,
            PortfolioId = portfolio.Id,
            Name = "Claims property",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Id = 1,
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            Id = 1,
            PortfolioId = portfolio.Id,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Claims lease",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var documentSourceVersion = new LegalDocumentSourceVersion
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = "template:1:v1",
            DocumentTemplate = template,
            DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };

        db.AddRange(portfolio, user, property, unit, template, documentSourceVersion);
        var agreementsToIssue = new List<(LeaseAgreement Agreement, int ArtifactId)>();
        for (var index = 0; index < count; index++)
        {
            var relationshipId = 3_000 + index;
            var agreementId = 4_000 + index;
            var agreementSignerId = 5_000 + index;
            var requestId = 6_000 + index;
            var storedFileId = 1_000 + index;
            var artifactId = 2_000 + index;
            var preparedAtUtc = now.AddMinutes(index);
            var storageKey = $"agreements/{Guid.NewGuid():N}.pdf";

            var agreement = new LeaseAgreement
            {
                Id = agreementId,
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                LeaseManagementId = relationshipId,
                VersionNumber = 1,
                AgreementNumber = $"AGR-{index + 1}",
                ChangeType = LeaseAgreementChangeType.Initial,
                TermType = LeaseAgreementTermType.FixedTerm,
                TermStartOn = DateOnly.FromDateTime(now),
                TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
                GoverningFromOn = DateOnly.FromDateTime(now),
                BaseRentAmount = 1_000,
                RentDueDay = 1,
                SecurityDepositObligation = 1_000,
                LateFeeAmount = 50,
                GracePeriodDays = 5,
                Currency = "USD",
                TermsSchemaVersion = 1,
                TermsPayload = "{}",
                DocumentSourceVersionId = documentSourceVersion.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = user.Id,
            };
            agreementsToIssue.Add((agreement, artifactId));

            db.AddRange(
                new LeaseManagement
                {
                    Id = relationshipId,
                    PublicId = Guid.NewGuid(),
                    PortfolioId = portfolio.Id,
                    PropertyId = property.Id,
                    UnitId = unit.Id,
                    RelationshipNumber = $"REL-{index + 1}",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = user.Id,
                    RowVersion = Guid.NewGuid(),
                },
                new StoredFile
                {
                    Id = storedFileId,
                    PortfolioId = portfolio.Id,
                    FileName = $"agreement-{index + 1}.pdf",
                    FilePath = storageKey,
                    ContentType = "application/pdf",
                    FileSize = 1,
                    EntityType = nameof(LeaseAgreement),
                    EntityId = agreementId,
                    UploadedAt = now,
                },
                new LegalDocumentArtifact
                {
                    Id = artifactId,
                    PublicId = Guid.NewGuid(),
                    PortfolioId = portfolio.Id,
                    StoredFileId = storedFileId,
                    ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
                    StorageKey = storageKey,
                    FileName = $"agreement-{index + 1}.pdf",
                    ContentType = "application/pdf",
                    ByteLength = 1,
                    ContentSha256 = new string('a', 64),
                    LegalIssuanceFingerprint = new string('b', 64),
                    CreatedAtUtc = now,
                    CreatedByUserId = user.Id,
                },
                agreement,
                new LeaseAgreementSigner
                {
                    Id = agreementSignerId,
                    PortfolioId = portfolio.Id,
                    LeaseAgreementId = agreementId,
                    SignerRole = LeaseLegalSignerRole.PrimaryTenant,
                    NameSnapshot = "Test Signer",
                    EmailSnapshot = $"signer-{index + 1}@example.test",
                    SigningOrder = 1,
                    IsRequired = true,
                },
                new SignatureRequest
                {
                    Id = requestId,
                    PortfolioId = portfolio.Id,
                    PublicId = Guid.NewGuid(),
                    LeaseAgreementId = agreementId,
                    Provider = "native",
                    IdempotencyKey = $"claim-{index + 1}",
                    Status = SignatureRequestStatus.ExecutionPending,
                    Subject = $"Agreement {index + 1}",
                    IssuedArtifactId = artifactId,
                    PreparedAtUtc = preparedAtUtc,
                    ProviderAcceptedAtUtc = preparedAtUtc,
                    CreatedByUserId = user.Id,
                    Signers =
                    [
                        new SignatureSigner
                        {
                            Id = 7_000 + index,
                            PortfolioId = portfolio.Id,
                            AgreementSignerId = agreementSignerId,
                            NameSnapshot = "Test Signer",
                            EmailSnapshot = $"signer-{index + 1}@example.test",
                            SigningOrder = 1,
                            IsRequired = true,
                            TokenHash = index.ToString("x64"),
                            TokenExpiresAtUtc = now.AddDays(1),
                            Status = SignatureSignerStatus.Signed,
                            SignatureType = SignatureSignatureType.Typed,
                            TypedName = "Test Signer",
                            ConsentGivenAtUtc = now,
                            SignedAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                        },
                    ],
                });
        }

        await db.SaveChangesAsync();
        foreach (var (agreement, artifactId) in agreementsToIssue)
        {
            agreement.IssuedArtifactId = artifactId;
            agreement.IssuedAtUtc = now;
        }
        await db.SaveChangesAsync();

        return await db.SignatureRequests
            .AsNoTracking()
            .OrderBy(request => request.PreparedAtUtc)
            .Select(request => request.Id)
            .ToArrayAsync();
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }
}
