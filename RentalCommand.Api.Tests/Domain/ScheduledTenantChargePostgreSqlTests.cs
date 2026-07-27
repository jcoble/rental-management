using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class ScheduledTenantChargePostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc =
        new(2027, 01, 08, 14, 30, 00, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<ApplyScheduledTenantChargeBatchResult> Codec =
        new("scheduled-tenant-charges.rent.apply.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private IAtomicUnitOfWork _atomic = null!;

    public ScheduledTenantChargePostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            _ctx.ConnectionString);
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task RentBatch_DoesNotPostSecondJanuaryRentForCorrectionAfterInitialAgreementWasCharged()
    {
        var graph = SeedCorrectedAgreementWithExistingJanuaryRent();

        var result = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("scheduled-tenant-charges.rent.apply", Guid.NewGuid().ToString("N")),
            new ApplyScheduledTenantChargeBatchCommand(
                Guid.NewGuid(),
                200,
                IncludeRentCharges: true,
                IncludeLateFeeCharges: false,
                StateLateFeeCapsJson: "[]"),
            Codec);

        result.Value.RentChargeCount.Should().Be(0);
        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .OrderBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.Amount,
                entry.DueOn,
                entry.LeaseAgreementId,
                entry.BusinessKey,
            })
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].Amount.Should().Be(1_300m);
        rows[0].DueOn.Should().Be(new DateOnly(2027, 01, 01));
        rows[0].LeaseAgreementId.Should().Be(graph.InitialAgreementId);
        rows[0].BusinessKey.Should().Be($"rent:{graph.InitialAgreementPublicId}:2027-01");
    }

    private CorrectedLeaseGraph SeedCorrectedAgreementWithExistingJanuaryRent()
    {
        EnsureFrozenBusinessDate();
        EnsureAutomationSettings();

        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"scheduled-rent-test-source:{Guid.NewGuid():N}",
            RendererKey = "test-lease",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Scheduled Rent Test Property",
            AddressLine1 = "100 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "A",
            MarketRent = 1_300m,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-SCHEDULED-{Guid.NewGuid():N}",
            PossessionGivenAtUtc = SeededAtUtc.AddDays(-7),
            CreatedAtUtc = SeededAtUtc.AddDays(-7),
            UpdatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Scheduled",
            LastName = "Tenant",
            Email = "scheduled.tenant@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-SCHEDULED-{Guid.NewGuid():N}",
            Currency = "USD",
            OpenedAtUtc = SeededAtUtc.AddDays(-7),
            CreatedAtUtc = SeededAtUtc.AddDays(-7),
            CreatedByUserId = 1,
        };
        var initial = NewAgreement(
            relationship,
            source,
            versionNumber: 1,
            changeType: LeaseAgreementChangeType.Initial,
            governingFromOn: new DateOnly(2027, 01, 01),
            termStartOn: new DateOnly(2027, 01, 01),
            agreementNumber: $"AGR-SCHEDULED-{Guid.NewGuid():N}");

        _ctx.Db.AddRange(source, property, unit, relationship, tenant, party, account, initial);
        _ctx.Db.SaveChanges();

        AddTenantSigner(initial, party, tenant);
        ExecuteAgreement(initial, "initial");

        var correction = NewAgreement(
            relationship,
            source,
            versionNumber: 2,
            changeType: LeaseAgreementChangeType.Correction,
            governingFromOn: new DateOnly(2027, 01, 08),
            termStartOn: new DateOnly(2027, 01, 01),
            agreementNumber: $"{initial.AgreementNumber}-V2");
        correction.ReplacesAgreementId = initial.Id;
        correction.CorrectionReason = "Correct late fee amount in imported lease";
        _ctx.Db.LeaseAgreements.Add(correction);
        _ctx.Db.SaveChanges();

        AddTenantSigner(correction, party, tenant);
        initial.SupersededEffectiveOn = new DateOnly(2027, 01, 08);
        initial.SupersededByAgreementId = correction.Id;
        initial.SupersessionRecordedAtUtc = SeededAtUtc;
        _ctx.Db.SaveChanges();

        ExecuteAgreement(correction, "correction");

        var januaryRent = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_300m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 01),
            DueOn = new DateOnly(2027, 01, 01),
            PostedAtUtc = SeededAtUtc.AddDays(-7),
            Description = "Rent due Jan 1, 2027",
            BusinessKey = $"rent:{initial.PublicId}:2027-01",
            LeaseAgreement = initial,
            CreatedByUserId = 1,
        };
        _ctx.Db.TenantLedgerEntries.Add(januaryRent);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();

        return new CorrectedLeaseGraph(account.Id, initial.Id, initial.PublicId);
    }

    private static LeaseAgreement NewAgreement(
        LeaseManagement relationship,
        LegalDocumentSourceVersion source,
        int versionNumber,
        LeaseAgreementChangeType changeType,
        DateOnly governingFromOn,
        DateOnly termStartOn,
        string agreementNumber) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        LeaseManagement = relationship,
        VersionNumber = versionNumber,
        AgreementNumber = agreementNumber,
        ChangeType = changeType,
        TermType = LeaseAgreementTermType.FixedTerm,
        TermStartOn = termStartOn,
        TermEndOn = new DateOnly(2027, 12, 31),
        GoverningFromOn = governingFromOn,
        BaseRentAmount = 1_300m,
        RentDueDay = 1,
        SecurityDepositObligation = 1_300m,
        LateFeeAmount = changeType == LeaseAgreementChangeType.Correction ? 50m : 75m,
        GracePeriodDays = 5,
        Currency = "USD",
        TermsSchemaVersion = 1,
        TermsPayload = "{}",
        DocumentSourceVersion = source,
        CreatedAtUtc = SeededAtUtc,
        UpdatedAtUtc = SeededAtUtc,
        CreatedByUserId = 1,
    };

    private void AddTenantSigner(
        LeaseAgreement agreement,
        LeaseManagementParty party,
        Tenant tenant)
    {
        _ctx.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreement = agreement,
            LeaseManagementParty = party,
            Tenant = tenant,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        });
        _ctx.Db.SaveChanges();
    }

    private void ExecuteAgreement(LeaseAgreement agreement, string suffix)
    {
        var issuedFile = StoredFile($"agreement-{suffix}-{Guid.NewGuid():N}-issued.pdf");
        var executedFile = StoredFile($"agreement-{suffix}-{Guid.NewGuid():N}-executed.pdf");
        _ctx.Db.StoredFiles.AddRange(issuedFile, executedFile);
        _ctx.Db.SaveChanges();

        var issuedArtifact = AgreementArtifact(
            issuedFile,
            LegalDocumentArtifactKind.IssuedAgreement,
            new string('a', 64),
            new string('b', 64));
        var executedArtifact = AgreementArtifact(
            executedFile,
            LegalDocumentArtifactKind.ExecutedAgreement,
            new string('c', 64),
            null);
        _ctx.Db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _ctx.Db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = SeededAtUtc;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = SeededAtUtc;
        _ctx.Db.SaveChanges();
    }

    private static StoredFile StoredFile(string fileName) => new()
    {
        PortfolioId = PortfolioId,
        FileName = fileName,
        FilePath = $"test/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        UploadedAt = SeededAtUtc,
    };

    private static LegalDocumentArtifact AgreementArtifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        string contentSha,
        string? issuanceFingerprint) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = file.FilePath,
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = contentSha,
        LegalIssuanceFingerprint = issuanceFingerprint,
        CreatedAtUtc = SeededAtUtc,
        CreatedByUserId = 1,
    };

    private void EnsureFrozenBusinessDate()
    {
        var clock = _ctx.Db.SimulationClocks.SingleOrDefault(clock => clock.Id == 1);
        if (clock is null)
        {
            _ctx.Db.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = SeededAtUtc,
                RealAnchorUtc = SeededAtUtc,
                TimeZoneId = "UTC",
                UpdatedAtRealUtc = SeededAtUtc,
            });
        }
        else
        {
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = SeededAtUtc;
            clock.RealAnchorUtc = SeededAtUtc;
            clock.TimeZoneId = "UTC";
            clock.UpdatedAtRealUtc = SeededAtUtc;
        }

        _ctx.Db.SaveChanges();
    }

    private void EnsureAutomationSettings()
    {
        if (_ctx.Db.AutomationSettings.Any(settings => settings.PortfolioId == PortfolioId))
        {
            return;
        }

        _ctx.Db.AutomationSettings.Add(new AutomationSettings
        {
            PortfolioId = PortfolioId,
            EnableRentCharges = true,
            RentChargeLeadDays = 5,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        });
        _ctx.Db.SaveChanges();
    }

    private sealed record CorrectedLeaseGraph(
        int TenantAccountId,
        int InitialAgreementId,
        Guid InitialAgreementPublicId);
}
