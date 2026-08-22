using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name4)]
public sealed class LeaseManagementHistoryProjectionTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private WorkspaceReadScope _scope;

    public LeaseManagementHistoryProjectionTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(LeaseManagementHistoryProjectionTests));
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task HistoryProjection_FlagsMissingSignatureRequest()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "History Projection Property",
            AddressLine1 = "1 History Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "A",
            MarketRent = 1_200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-HISTORY-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var noRequestSource = NewSource(now, "no-request");
        var matchingSource = NewSource(now, "matching-request");
        var noRequestArtifact = NewArtifact(now, "history-no-request.pdf");
        var matchingArtifact = NewArtifact(now, "history-matching-request.pdf");
        var noRequestAgreement = NewIssuedAgreement(
            management, noRequestSource, now, "AGR-HISTORY-NATIVE");
        var matchingAgreement = NewIssuedAgreement(
            management, matchingSource, now, "AGR-HISTORY-ESIGN");
        matchingAgreement.VersionNumber = 2;
        matchingAgreement.ChangeType = LeaseAgreementChangeType.Correction;
        matchingAgreement.CorrectionReason = "History projection test correction";
        matchingAgreement.ReplacesAgreement = noRequestAgreement;
        var noRequestSigner = NewSigner(noRequestAgreement);
        var matchingSigner = NewSigner(matchingAgreement);

        _context.Db.AddRange(
            property,
            unit,
            management,
            noRequestSource,
            matchingSource,
            noRequestArtifact.StoredFile!,
            matchingArtifact.StoredFile!,
            noRequestArtifact,
            matchingArtifact,
            noRequestAgreement,
            matchingAgreement,
            noRequestSigner,
            matchingSigner);
        await _context.Db.SaveChangesAsync();

        noRequestAgreement.IssuedArtifact = noRequestArtifact;
        noRequestAgreement.IssuedAtUtc = now;
        matchingAgreement.IssuedArtifact = matchingArtifact;
        matchingAgreement.IssuedAtUtc = now;
        var matchingRequest = new SignatureRequest
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseAgreement = matchingAgreement,
            IssuedArtifact = matchingArtifact,
            Provider = "native",
            IdempotencyKey = $"history-request:{Guid.NewGuid():N}",
            Status = SignatureRequestStatus.Prepared,
            Subject = "History projection request",
            PreparedAtUtc = now,
            CreatedByUserId = 1,
        };
        _context.Db.Add(matchingRequest);
        await _context.Db.SaveChangesAsync();
        await _context.ActivateApiScopeAsync(_scope);

        var access = new LeaseManagementReadContext(
            _scope.PortfolioId,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision);
        var service = new LeaseManagementQueryService(_context.Db, TimeProvider.System);

        var sql = service.BuildAgreementHistoryQuery(
                access,
                management.Id,
                new LeaseLegalHistoryQuery { Take = 20 })
            .ToQueryString();
        sql.Should().Contain("\"SignatureRequests\"");
        sql.Should().Contain("EXISTS");

        var page = await service.ListAgreementHistoryPageAsync(
            access,
            management.Id,
            new LeaseLegalHistoryQuery { Take = 20 });

        page.Should().NotBeNull();
        page!.Items.Should().HaveCount(2);

        var missingRequest = page.Items.Single(item => item.LeaseAgreementId == noRequestAgreement.Id);
        missingRequest.HasSignatureRequest.Should().BeFalse();
        missingRequest.SignatureRequestId.Should().BeNull();

        var matching = page.Items.Single(item => item.LeaseAgreementId == matchingAgreement.Id);
        matching.HasSignatureRequest.Should().BeTrue();
        matching.SignatureRequestId.Should().Be(matchingRequest.Id);
    }

    private static LegalDocumentSourceVersion NewSource(DateTime now, string suffix) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
        BusinessKey = $"history-source:{suffix}:{Guid.NewGuid():N}",
        RendererKey = $"history-{suffix}",
        RendererVersion = 1,
        SnapshotPayload = "{}",
        CreatedAtUtc = now,
        CreatedByUserId = 1,
    };

    private static LegalDocumentArtifact NewArtifact(DateTime now, string fileName)
    {
        var file = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = fileName,
            FilePath = $"history/{Guid.NewGuid():N}/{fileName}",
            ContentType = "application/pdf",
            FileSize = 100,
            ContentSha256 = new string('a', 64),
            UploadedAt = now,
        };

        return new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFile = file,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            ByteLength = file.FileSize,
            ContentSha256 = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
    }

    private static LeaseAgreement NewIssuedAgreement(
        LeaseManagement management,
        LegalDocumentSourceVersion source,
        DateTime now,
        string agreementNumber) => new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            VersionNumber = 1,
            AgreementNumber = agreementNumber,
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 1, 1),
            TermEndOn = new DateOnly(2026, 12, 31),
            GoverningFromOn = new DateOnly(2026, 1, 1),
            BaseRentAmount = 1_200m,
            RentDueDay = 1,
            SecurityDepositObligation = 0m,
            LateFeeAmount = 0m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
            UpdatedAtUtc = now,
        };

    private static LeaseAgreementSigner NewSigner(LeaseAgreement agreement) => new()
    {
        PortfolioId = PortfolioId,
        LeaseAgreement = agreement,
        SignerRole = LeaseLegalSignerRole.PrimaryTenant,
        NameSnapshot = "History Projection Tenant",
        EmailSnapshot = "history-projection@example.test",
        SigningOrder = 1,
        IsRequired = true,
    };
}
