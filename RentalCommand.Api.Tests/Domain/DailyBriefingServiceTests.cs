using FluentAssertions;
using RentalCommand.Api.DTOs;
using System.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name2)]
public class DailyBriefingServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 1;

    private readonly List<string> _executedSql = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private DailyBriefingService _sut = null!;

    public DailyBriefingServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_executedSql)]);
        _db = _ctx.Db;
        _sut = new DailyBriefingService(_db, new NoopLlmProvider(), TimeProvider.System);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task ComposeAsync_RanksAndCapsBriefingCandidatesInSql()
    {
        SeedBriefingData();
        _executedSql.Clear();

        var briefing = await _sut.ComposeAsync(
            await SeedAdministratorScopeAsync(null, DateTime.UtcNow), CancellationToken.None);

        briefing.Bullets.Should().Contain(b => b.Category == "Maintenance" && b.Severity == "critical");
        briefing.Bullets.Should().Contain(b => b.Category == "RentLate");
        briefing.Bullets.Should().Contain(b => b.Category == "RentDue");
        briefing.Bullets.Should().Contain(b => b.Category == "Appointment");
        briefing.Bullets.Should().Contain(b => b.Category == "Inspection");
        briefing.Bullets.Should().Contain(b => b.Category == "LeaseExpiring");
        briefing.Bullets
            .Where(b => b.EntityType is "WorkOrder" or nameof(TenantAccount) or nameof(LeaseAgreement))
            .Should().OnlyContain(b => b.UnitId > 0,
                "unit-tied dashboard action items should deep-link into the unit Command Center");

        _executedSql.Should().Contain(command =>
            command.Contains("vw_morning_briefing_candidates", StringComparison.OrdinalIgnoreCase)
            && command.Contains("RequiredCapability", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && command.Contains("money.balances.read", StringComparison.OrdinalIgnoreCase)
            && command.Contains("work.read", StringComparison.OrdinalIgnoreCase),
            "daily briefing candidate ranking and cap must happen in one DB-side query before bullet formatting");
    }

    [Fact]
    public async Task ComposeAsync_ExcludesEndedFixedTermLeasePaymentsFromActiveAttention()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var endedRelationship = SeedRelationship(
            today,
            leaseNumber: "L-OLD",
            propertyName: "Westview Four-Plex",
            unitNumber: "101",
            tenantFirstName: "Jordan",
            tenantLastName: "Smith",
            startDate: today.AddMonths(-15),
            endDate: today.AddMonths(-3),
            lifecycle: "Occupied");
        var currentRelationship = SeedRelationship(
            today,
            leaseNumber: "L-CURRENT",
            propertyName: "Eastland 8-Plex",
            unitNumber: "4B",
            tenantFirstName: "Kevin",
            tenantLastName: "Brown",
            startDate: today.AddMonths(-6),
            endDate: today.AddMonths(6),
            lifecycle: "Occupied");

        SeedOpenRentCharge(endedRelationship, 925m, DateOnly.FromDateTime(today.AddMonths(-10)));
        SeedOpenRentCharge(currentRelationship, 975m, DateOnly.FromDateTime(today.AddDays(-7)));

        var briefing = await _sut.ComposeAsync(
            await SeedAdministratorScopeAsync(null, DateTime.UtcNow), CancellationToken.None);

        briefing.Bullets.Should().ContainSingle(b =>
            b.Category == "RentLate" &&
            b.EntityId == currentRelationship.Account.Id);
        briefing.Bullets.Should().NotContain(b =>
            b.Category == "RentLate" &&
            b.Title.Contains("L-OLD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComposeAsync_RentAttentionUsesTenantUnitPropertyLabelInsteadOfLeaseNumber()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var relationship = SeedRelationship(
            today,
            leaseNumber: "L2024-003",
            propertyName: "Westview Four-Plex",
            unitNumber: "101",
            tenantFirstName: "Jordan",
            tenantLastName: "Smith",
            startDate: today.AddMonths(-6),
            endDate: today.AddMonths(6),
            lifecycle: "Occupied");

        SeedOpenRentCharge(relationship, 925m, DateOnly.FromDateTime(today.AddDays(-7)));

        var briefing = await _sut.ComposeAsync(
            await SeedAdministratorScopeAsync(null, DateTime.UtcNow), CancellationToken.None);

        var rentBullet = briefing.Bullets.Should().ContainSingle(b => b.Category == "RentLate").Subject;
        rentBullet.Title.Should().Contain("Jordan Smith");
        rentBullet.Title.Should().Contain("Westview Four-Plex");
        rentBullet.Title.Should().Contain("Unit 101");
        rentBullet.Title.Should().NotContain("L2024-003");
    }

    [Fact]
    public async Task ComposeAsync_SelectedPropertyScope_ExcludesDecoyWorkCandidate()
    {
        var now = DateTime.UtcNow;
        var allowed = SeedBareProperty("Allowed", now);
        var decoy = SeedBareProperty("Decoy", now);
        _db.WorkOrders.AddRange(
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = allowed.Id,
                Title = "Allowed emergency",
                Description = "Visible",
                Priority = WorkOrderPriority.Emergency,
                Status = WorkOrderStatus.New,
                RequestedAt = now,
                UpdatedAt = now,
            },
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = decoy.Id,
                Title = "Decoy emergency",
                Description = "Hidden",
                Priority = WorkOrderPriority.Emergency,
                Status = WorkOrderStatus.New,
                RequestedAt = now,
                UpdatedAt = now,
            });
        await _db.SaveChangesAsync();
        var scope = await SeedAdministratorScopeAsync(allowed.Id, now);

        var briefing = await _sut.ComposeAsync(scope, CancellationToken.None);

        briefing.Bullets.Should().ContainSingle(bullet => bullet.Category == "Maintenance");
        briefing.Bullets.Single(bullet => bullet.Category == "Maintenance").Title
            .Should().Contain("Allowed emergency");
    }

    [Fact]
    public async Task TryPolishSummaryAsync_ReturnsRulesOnlyBriefingWhenOptionalLlmPolishStalls()
    {
        var sut = new DailyBriefingService(
            _db,
            new HangingLlmProvider(),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        var (summary, enhanced) = await sut.TryPolishSummaryAsync(
            [
                new BriefingBullet(
                    "Rent overdue",
                    "$1,050 was due 24 days ago",
                    "RentLate",
                    "critical",
                    nameof(TenantAccount),
                    1,
                    1)
            ],
            CancellationToken.None);

        stopwatch.Stop();
        summary.Should().BeNull();
        enhanced.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(1),
            "optional AI wording must never hold the factual Daily Briefing open");
    }

    private Property SeedBareProperty(string name, DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = $"{name} address",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(int? propertyId, DateTime now)
    {
        var user = _db.Users.Single(user => user.Id == ActorUserId);
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = propertyId.HasValue
                ? MembershipRoleAssignmentScopeKind.SelectedProperties
                : MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (propertyId.HasValue)
        {
            assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignment = assignment,
                PortfolioId = PortfolioId,
                PropertyId = propertyId.Value,
            });
        }
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _db.AddRange(assignment, session);
        _db.SaveChanges();
        var scope = new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, context.Id, context.AccessRevision);
        await _ctx.ActivateApiScopeAsync(scope);
        return scope;
    }

    private void SeedBriefingData()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var relationship = SeedRelationship(
            today,
            leaseNumber: "L-1A",
            propertyName: "Maple",
            unitNumber: "1A",
            tenantFirstName: "Maria",
            tenantLastName: "Tenant",
            startDate: today.AddMonths(-1),
            endDate: today.AddDays(30),
            lifecycle: "Occupied");

        _db.AddRange(
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = relationship.Property,
                Unit = relationship.Unit,
                Title = "Water leak",
                Description = "Water is entering the kitchen ceiling.",
                Priority = WorkOrderPriority.Emergency,
                Status = WorkOrderStatus.New,
                RequestedAt = now,
                UpdatedAt = now,
            },
            new Appointment
            {
                PortfolioId = PortfolioId,
                Property = relationship.Property,
                Unit = relationship.Unit,
                Title = "Showing",
                Type = AppointmentType.Showing,
                Status = AppointmentStatus.Scheduled,
                ScheduledStart = today.AddHours(14),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Inspection
            {
                PortfolioId = PortfolioId,
                Property = relationship.Property,
                Unit = relationship.Unit,
                Type = InspectionType.Routine,
                Status = InspectionStatus.Scheduled,
                ScheduledFor = today.AddDays(1).AddHours(9),
                CreatedAt = now,
                UpdatedAt = now,
            });
        _db.SaveChanges();
        SeedOpenRentCharge(relationship, 1_200m, DateOnly.FromDateTime(today.AddDays(-6)));
        SeedOpenRentCharge(relationship, 1_200m, DateOnly.FromDateTime(today));
    }

    private CanonicalRelationship SeedRelationship(
        DateTime today,
        string leaseNumber,
        string propertyName,
        string unitNumber,
        string tenantFirstName,
        string tenantLastName,
        DateTime startDate,
        DateTime endDate,
        string lifecycle)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = tenantFirstName,
            LastName = tenantLastName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit, tenant);
        _db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"REL-{Guid.NewGuid():N}"[..12],
            PlannedPossessionAtUtc = startDate,
            PossessionGivenAtUtc = startDate,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();

        var startOn = DateOnly.FromDateTime(startDate);
        var endOn = DateOnly.FromDateTime(endDate);
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = leaseNumber,
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = startOn,
            TermEndOn = endOn,
            GoverningFromOn = startOn,
            BaseRentAmount = 1_200m,
            RentDueDay = checked((short)today.Day),
            SecurityDepositObligation = 1_200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, ActorUserId, now),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = startOn,
            EffectiveThrough = lifecycle == "Closed" ? endOn : null,
            ChangeReason = "Canonical daily briefing fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{Guid.NewGuid():N}"[..12],
            Currency = "USD",
            OpenedAtUtc = startDate,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        _db.AddRange(agreement, party, account);
        _db.SaveChanges();
        _db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenantFirstName} {tenantLastName}",
            EmailSnapshot = $"{tenantFirstName}.{tenantLastName}@example.test".ToLowerInvariant(),
            SigningOrder = 1,
            IsRequired = true,
        });
        _db.SaveChanges();

        var issuedFile = NewStoredFile($"briefing-agreement-{agreement.Id}-issued.pdf", now);
        var executedFile = NewStoredFile($"briefing-agreement-{agreement.Id}-executed.pdf", now);
        _db.StoredFiles.AddRange(issuedFile, executedFile);
        _db.SaveChanges();
        var issuedArtifact = NewAgreementArtifact(
            issuedFile, LegalDocumentArtifactKind.IssuedAgreement, 'a', now);
        var executedArtifact = NewAgreementArtifact(
            executedFile, LegalDocumentArtifactKind.ExecutedAgreement, 'c', now);
        _db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now;
        _db.SaveChanges();

        if (lifecycle == "Closed")
        {
            management.PossessionReturnedAtUtc = endDate;
            management.AccountClosedAtUtc = endDate;
            account.ClosedAtUtc = endDate;
            account.CloseReasonCode = "LeaseEnded";
            _db.SaveChanges();
        }

        return new CanonicalRelationship(property, unit, tenant, management, agreement, party, account);
    }

    private static StoredFile NewStoredFile(string fileName, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        FileName = fileName,
        FilePath = $"test/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact NewAgreementArtifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        char hashCharacter,
        DateTime now) => new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
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
            CreatedByUserId = ActorUserId,
        };

    private TenantLedgerEntry SeedOpenRentCharge(
        CanonicalRelationship relationship,
        decimal amount,
        DateOnly dueOn)
    {
        var now = DateTime.UtcNow;
        var entry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = relationship.Account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = dueOn,
            DueOn = dueOn,
            PostedAtUtc = now,
            Description = "Rent charge",
            BusinessKey = $"briefing-rent:{relationship.Account.Id}:{dueOn:yyyy-MM-dd}",
            LeaseAgreementId = relationship.Agreement.Id,
            CreatedByUserId = ActorUserId,
        };
        _db.TenantLedgerEntries.Add(entry);
        _db.SaveChanges();
        return entry;
    }

    private sealed record CanonicalRelationship(
        Property Property,
        Unit Unit,
        Tenant Tenant,
        LeaseManagement Management,
        LeaseAgreement Agreement,
        LeaseManagementParty Party,
        TenantAccount Account);

    private sealed class NoopLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default)
            => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "test"));
    }

    private sealed class HangingLlmProvider : ILlmProvider
    {
        public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "";
        }

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default)
            => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "test"));
    }

}
