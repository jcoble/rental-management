using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class NoticeDraftServiceTests : IDisposable
{
    private readonly NoticeFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GenerateAsync_DefaultRunOnlyCreatesRelationshipNoticesInsideTriggerWindow()
    {
        var near = _fixture.SeedRelationship("Avery", "Brooks", 1400m, 60);
        var far = _fixture.SeedRelationship("Morgan", "Pierce", 1450m, 120);

        var result = await CreateService().GenerateAsync(1);

        result.CreatedCount.Should().Be(2);
        result.Drafts.Should().Contain(d =>
            d.LeaseManagementId == near.Management.Id && d.NoticeType == "RenewalOffer");
        result.Drafts.Should().Contain(d =>
            d.LeaseManagementId == near.Management.Id && d.NoticeType == "MonthToMonthConversion");
        result.Drafts.Should().NotContain(d => d.LeaseManagementId == far.Management.Id);
        result.Drafts.Should().NotContain(d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_ForcedMoveOutCanTargetRelationshipOutsideTriggerWindow()
    {
        var relationship = _fixture.SeedRelationship("Morgan", "Pierce", 1450m, 120);

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                RecipientTenantId = relationship.Tenant.Id,
                LeaseManagementId = relationship.Management.Id,
                TenantAccountId = relationship.Account.Id,
                NoticeType = "MoveOutReminder",
            });

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseManagementId == relationship.Management.Id &&
            d.TenantAccountId == relationship.Account.Id &&
            d.RecipientTenantId == relationship.Tenant.Id &&
            d.NoticeType == "MoveOutReminder");
    }

    [Fact]
    public async Task GenerateAsync_ForcedRenewalFallsBackQuicklyWhenLlmIsSlow()
    {
        var relationship = _fixture.SeedRelationship("Morgan", "Pierce", 1450m, 120);
        var slowLlm = new SlowLlmProvider();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();
        var result = await CreateService(slowLlm).GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                LeaseManagementId = relationship.Management.Id,
                NoticeType = "RenewalOffer",
            },
            timeout.Token);
        elapsed.Stop();

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseManagementId == relationship.Management.Id &&
            d.Subject.Contains("Lease renewal for", StringComparison.Ordinal));
        slowLlm.ChatCalls.Should().Be(1);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GenerateAsync_RelationshipScopedRequestReturnsExistingOpenDraft()
    {
        var relationship = _fixture.SeedRelationship("Morgan", "Pierce", 1450m, 120);
        var now = DateTime.UtcNow;
        _fixture.Db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = 1,
            LeaseManagementId = relationship.Management.Id,
            TenantAccountId = relationship.Account.Id,
            RecipientTenantId = relationship.Tenant.Id,
            PropertyId = relationship.Property.Id,
            NoticeType = "RenewalOffer",
            Status = "Draft",
            Subject = "Existing renewal draft",
            Body = "Existing body",
            Reason = "Already generated.",
            TriggerDate = relationship.Agreement.TermEndOn!.Value.ToDateTime(TimeOnly.MinValue),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _fixture.Db.SaveChangesAsync();
        _fixture.Db.ChangeTracker.Clear();

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                LeaseManagementId = relationship.Management.Id,
                NoticeType = "RenewalOffer",
            });

        result.CreatedCount.Should().Be(0);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseManagementId == relationship.Management.Id &&
            d.Subject == "Existing renewal draft");
    }

    [Fact]
    public async Task GenerateAsync_LateRentExcludesClosedRelationshipCharges()
    {
        var current = _fixture.SeedRelationship("Current", "Tenant", 1400m, 300);
        var closed = _fixture.SeedRelationship("Former", "Tenant", 1450m, -1, lifecycle: "Closed");
        _fixture.AddCharge(current, TenantLedgerEntryType.RentCharge, 1400m, -10);
        _fixture.AddCharge(closed, TenantLedgerEntryType.RentCharge, 1450m, -20);

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest { NoticeType = "LateRentNotice" });

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseManagementId == current.Management.Id && d.NoticeType == "LateRentNotice");
        result.Drafts.Should().NotContain(d => d.LeaseManagementId == closed.Management.Id);
    }

    [Fact]
    public async Task GenerateAsync_RendersActiveLandlordTemplateWithCanonicalRecipientTokens()
    {
        var relationship = _fixture.SeedRelationship("Avery", "Brooks", 1400m, 60);
        _fixture.Db.NoticeTemplates.Add(new NoticeTemplate
        {
            PortfolioId = 1,
            NoticeType = "RenewalOffer",
            Subject = "Renewal for {{tenant_name}}",
            Body = "Hi {{tenant_name}}, renew {{property_address}} with {{portfolio_name}}?",
            IsActive = true,
        });
        await _fixture.Db.SaveChangesAsync();
        _fixture.Db.ChangeTracker.Clear();

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                RecipientTenantId = relationship.Tenant.Id,
                NoticeType = "RenewalOffer",
            });

        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.Subject.Should().Be("Renewal for Avery Brooks");
        draft.Body.Should().Contain("Avery Brooks").And.Contain("Test Portfolio");
    }

    [Fact]
    public async Task GenerateAsync_ManualRentReminderUsesRelationshipAgreementTerms()
    {
        var relationship = _fixture.SeedRelationship("Sam", "Rivera", 1500m, 300);

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                RecipientTenantId = relationship.Tenant.Id,
                LeaseManagementId = relationship.Management.Id,
                TenantAccountId = relationship.Account.Id,
                NoticeType = "RentReminder",
            });

        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.NoticeType.Should().Be("RentReminder");
        draft.LeaseManagementId.Should().Be(relationship.Management.Id);
        draft.TenantAccountId.Should().Be(relationship.Account.Id);
        draft.RecipientTenantId.Should().Be(relationship.Tenant.Id);
        draft.Body.Should().Contain("Sam Rivera").And.Contain("$1,500");
    }

    [Fact]
    public async Task GenerateAsync_PortfolioWideUpcomingChargeCreatesOneLedgerScopedReminder()
    {
        var relationship = _fixture.SeedRelationship("Sam", "Rivera", 1500m, 300);
        var charge = _fixture.AddCharge(relationship, TenantLedgerEntryType.RentCharge, 1500m, 3);

        var first = await CreateService().GenerateAsync(1);

        var reminder = first.Drafts.Should().ContainSingle(d => d.NoticeType == "RentReminder").Which;
        reminder.TenantLedgerEntryId.Should().Be(charge.Id);
        reminder.TenantAccountId.Should().Be(relationship.Account.Id);
        reminder.TriggerDate.Date.Should().Be(charge.DueOn!.Value.ToDateTime(TimeOnly.MinValue));

        _fixture.Db.ChangeTracker.Clear();
        var second = await CreateService().GenerateAsync(1);
        second.Drafts.Should().NotContain(d => d.NoticeType == "RentReminder");
    }

    [Fact]
    public async Task GenerateAsync_LedgerScopedReminderTargetsRequestedChargeOnly()
    {
        var target = _fixture.SeedRelationship("Sam", "Rivera", 1500m, 300);
        var other = _fixture.SeedRelationship("Avery", "Brooks", 1700m, 300);
        var targetCharge = _fixture.AddCharge(target, TenantLedgerEntryType.RentCharge, 1500m, 3);
        _fixture.AddCharge(other, TenantLedgerEntryType.RentCharge, 1700m, 3);

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                RecipientTenantId = target.Tenant.Id,
                LeaseManagementId = target.Management.Id,
                TenantAccountId = target.Account.Id,
                TenantLedgerEntryId = targetCharge.Id,
                NoticeType = "RentReminder",
            });

        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.TenantLedgerEntryId.Should().Be(targetCharge.Id);
        draft.LeaseManagementId.Should().Be(target.Management.Id);
        draft.TenantAccountId.Should().Be(target.Account.Id);
        draft.RecipientTenantId.Should().Be(target.Tenant.Id);
    }

    [Fact]
    public async Task GenerateAsync_LedgerScopedLateNoticeUsesStableUsdCopyAndRelatedFee()
    {
        var relationship = _fixture.SeedRelationship("Tom", "Hardy", 2200m, 300);
        var rent = _fixture.AddCharge(relationship, TenantLedgerEntryType.RentCharge, 2200m, -10);
        _fixture.AddCharge(relationship, TenantLedgerEntryType.LateFeeCharge, 75m, -10);
        using var culture = new CurrentCultureScope(CultureInfo.GetCultureInfo("fr-FR"));

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantLedgerEntryId = rent.Id,
                NoticeType = "LateRentNotice",
            });

        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.TenantLedgerEntryId.Should().Be(rent.Id);
        draft.Reason.Should().Contain("$75");
        draft.Body.Should().Contain("$2,275");
    }

    [Fact]
    public async Task GenerateAsync_RelationshipScopeDoesNotLeakAnotherTenantAccount()
    {
        var target = _fixture.SeedRelationship("Tom", "Hardy", 2200m, 300);
        var other = _fixture.SeedRelationship("Taylor", "Other", 1800m, 300);
        _fixture.AddCharge(target, TenantLedgerEntryType.RentCharge, 2200m, -8);
        _fixture.AddCharge(other, TenantLedgerEntryType.RentCharge, 1800m, -12);

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                RecipientTenantId = target.Tenant.Id,
                LeaseManagementId = target.Management.Id,
                TenantAccountId = target.Account.Id,
                NoticeType = "LateRentNotice",
            });

        var draft = result.Drafts.Should().ContainSingle().Which;
        draft.LeaseManagementId.Should().Be(target.Management.Id);
        draft.TenantAccountId.Should().Be(target.Account.Id);
        draft.RecipientTenantId.Should().Be(target.Tenant.Id);
        result.Drafts.Should().NotContain(d => d.LeaseManagementId == other.Management.Id);
    }

    [Fact]
    public async Task GenerateAsync_LedgerScopedExistingApprovedNoticeReturnsWithoutDuplicate()
    {
        var relationship = _fixture.SeedRelationship("Sam", "Rivera", 1500m, 300);
        var charge = _fixture.AddCharge(relationship, TenantLedgerEntryType.RentCharge, 1500m, 3);
        var now = DateTime.UtcNow;
        _fixture.Db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = 1,
            LeaseManagementId = relationship.Management.Id,
            TenantAccountId = relationship.Account.Id,
            TenantLedgerEntryId = charge.Id,
            RecipientTenantId = relationship.Tenant.Id,
            PropertyId = relationship.Property.Id,
            NoticeType = "RentReminder",
            Status = "Approved",
            Subject = "Existing reminder",
            Body = "Existing body",
            Reason = "Already sent.",
            TriggerDate = charge.DueOn!.Value.ToDateTime(TimeOnly.MinValue),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _fixture.Db.SaveChangesAsync();
        _fixture.Db.ChangeTracker.Clear();

        var result = await CreateService().GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantLedgerEntryId = charge.Id,
                NoticeType = "RentReminder",
            });

        result.CreatedCount.Should().Be(0);
        result.Drafts.Should().ContainSingle(d =>
            d.TenantLedgerEntryId == charge.Id && d.Subject == "Existing reminder");
        var persistedCount = await _fixture.Db.NoticeDrafts.CountAsync(
            d => d.TenantLedgerEntryId == charge.Id);
        persistedCount.Should().Be(1);
    }

    private NoticeDraftService CreateService(ILlmProvider? llm = null) => new(
        _fixture.Db,
        new NoopConversationService(),
        llm ?? new NoopLlmProvider(),
        NullLogger<NoticeDraftService>.Instance,
        TimeProvider.System);

    private sealed class NoticeFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private int _nextId = 10;
        private long _nextLedgerId = 100;

        public NoticeFixture()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseSqlite(_connection)
                .Options;
            Db = new FixtureDbContext(options);
            Db.Database.EnsureCreated();

            Db.Portfolios.Add(new Portfolio
            {
                Id = 1,
                Name = "Test Portfolio",
                ManagementCompanyName = "Test Co",
                TimeZone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            Db.Users.Add(new ApplicationUser
            {
                Id = 1,
                UserName = "canonical-notice@test.local",
                NormalizedUserName = "CANONICAL-NOTICE@TEST.LOCAL",
                Email = "canonical-notice@test.local",
                NormalizedEmail = "CANONICAL-NOTICE@TEST.LOCAL",
                DisplayName = "Canonical Notice Test",
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow,
            });
            Db.DocumentTemplates.Add(new DocumentTemplate
            {
                Id = 1,
                PortfolioId = 1,
                Name = "Canonical test agreement",
                Version = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }

        public FixtureDbContext Db { get; }

        public NoticeRelationship SeedRelationship(
            string firstName,
            string lastName,
            decimal rent,
            int termEndDays,
            string lifecycle = "Occupied")
        {
            var id = _nextId++;
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var property = new Property
            {
                Id = id,
                PortfolioId = 1,
                Name = $"Canonical Property {id}",
                AddressLine1 = $"{id} Main St",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var unit = new Unit
            {
                Id = id,
                PortfolioId = 1,
                Property = property,
                UnitNumber = id.ToString(CultureInfo.InvariantCulture),
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = rent,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var tenant = new Tenant
            {
                Id = id,
                PortfolioId = 1,
                FirstName = firstName,
                LastName = lastName,
                Email = $"tenant-{id}@example.test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var management = new LeaseManagement
            {
                Id = id,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                Property = property,
                Unit = unit,
                RelationshipNumber = $"REL-{id}",
                PlannedPossessionAtUtc = now.AddMonths(-6),
                PossessionGivenAtUtc = now.AddMonths(-6),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = 1,
                RowVersion = Guid.NewGuid(),
            };
            var party = new LeaseManagementParty
            {
                Id = id,
                PortfolioId = 1,
                LeaseManagement = management,
                Tenant = tenant,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = today.AddMonths(-6),
                ChangeReason = "Canonical notice fixture",
                CreatedAtUtc = now,
                CreatedByUserId = 1,
            };
            var agreement = new LeaseAgreement
            {
                Id = id,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                LeaseManagement = management,
                VersionNumber = 1,
                AgreementNumber = $"AGR-{id}-V1",
                ChangeType = LeaseAgreementChangeType.Initial,
                TermType = LeaseAgreementTermType.FixedTerm,
                TermStartOn = today.AddMonths(-6),
                TermEndOn = today.AddDays(termEndDays),
                GoverningFromOn = today.AddMonths(-6),
                BaseRentAmount = rent,
                RentDueDay = 1,
                SecurityDepositObligation = rent,
                LateFeeAmount = 50m,
                GracePeriodDays = 5,
                Currency = "USD",
                TermsSchemaVersion = 1,
                TermsPayload = "{}",
                DocumentTemplateId = 1,
                DocumentTemplateVersion = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = 1,
            };
            var account = new TenantAccount
            {
                Id = id,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                LeaseManagement = management,
                AccountNumber = $"TA-{id}",
                Currency = "USD",
                OpenedAtUtc = now.AddMonths(-6),
                CreatedAtUtc = now,
                CreatedByUserId = 1,
            };

            Db.AddRange(property, unit, tenant, management, party, agreement, account);
            Db.SaveChanges();
            Db.LeaseManagementLifecycleProjections.Add(new LeaseManagementLifecycleProjection
            {
                PortfolioId = 1,
                PropertyId = property.Id,
                UnitId = unit.Id,
                LeaseManagementId = management.Id,
                EffectiveNowUtc = now,
                BusinessDate = today,
                Lifecycle = lifecycle,
                CurrentAgreementId = agreement.Id,
                CurrentPartyCount = 1,
                CurrentResidentCount = 1,
                CurrentFinanciallyResponsiblePartyCount = 1,
                CurrentPrimaryPartyId = party.Id,
                CurrentPrimaryTenantId = tenant.Id,
                CurrentPrimaryTenantName = $"{firstName} {lastName}",
                TenantAccountId = account.Id,
            });
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            return new NoticeRelationship(management, agreement, account, tenant, property, unit);
        }

        public TenantLedgerEntry AddCharge(
            NoticeRelationship relationship,
            TenantLedgerEntryType type,
            decimal amount,
            int dueInDays)
        {
            var now = DateTime.UtcNow;
            var dueOn = DateOnly.FromDateTime(now).AddDays(dueInDays);
            var entry = new TenantLedgerEntry
            {
                Id = _nextLedgerId++,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                TenantAccountId = relationship.Account.Id,
                EntryType = type,
                Direction = TenantLedgerDirection.Debit,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = dueOn,
                DueOn = dueOn,
                PostedAtUtc = now,
                Description = type.ToString(),
                BusinessKey = $"notice-test:{relationship.Account.Id}:{type}:{dueOn:yyyy-MM-dd}",
                LeaseAgreementId = relationship.Agreement.Id,
                CreatedByUserId = 1,
            };
            Db.TenantLedgerEntries.Add(entry);
            Db.SaveChanges();
            Db.TenantChargeBalanceProjections.Add(new TenantChargeBalanceProjection
            {
                PortfolioId = 1,
                TenantAccountId = relationship.Account.Id,
                TenantLedgerEntryId = entry.Id,
                BusinessDate = DateOnly.FromDateTime(now),
                EntryType = type.ToString(),
                Currency = "USD",
                EffectiveOn = dueOn,
                DueOn = dueOn,
                OriginalAmount = amount,
                OpenAmount = amount,
                IsPastDue = dueOn < DateOnly.FromDateTime(now),
            });
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return entry;
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class FixtureDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<LeaseManagementLifecycleProjection>()
                .HasKey(row => new { row.PortfolioId, row.LeaseManagementId });
            modelBuilder.Entity<LeaseManagementLifecycleProjection>()
                .ToTable("NoticeTestLeaseLifecycle");
            modelBuilder.Entity<TenantChargeBalanceProjection>()
                .HasKey(row => new { row.PortfolioId, row.TenantAccountId, row.TenantLedgerEntryId });
            modelBuilder.Entity<TenantChargeBalanceProjection>()
                .ToTable("NoticeTestChargeBalances");

            modelBuilder.Entity<LeaseAgreement>().Property(row => row.TermsPayload).HasColumnType("TEXT");
            modelBuilder.Entity<LeaseAgreement>().ToTable("LeaseAgreements");
            modelBuilder.Entity<LeaseManagement>().ToTable("LeaseManagements");
            modelBuilder.Entity<LeaseManagementParty>().ToTable("LeaseManagementParties");
            modelBuilder.Entity<TenantAccount>().ToTable("TenantAccounts");
            modelBuilder.Entity<TenantLedgerEntry>().ToTable("TenantLedgerEntries");
        }
    }

    private sealed record NoticeRelationship(
        LeaseManagement Management,
        LeaseAgreement Agreement,
        TenantAccount Account,
        Tenant Tenant,
        Property Property,
        Unit Unit);

    private sealed class CurrentCultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

        public CurrentCultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

        public void Dispose() => CultureInfo.CurrentCulture = _originalCulture;
    }

    private sealed class NoopLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "noop"));
    }

    private sealed class SlowLlmProvider : ILlmProvider
    {
        public int ChatCalls { get; private set; }

        public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
        {
            ChatCalls++;
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "{\"subject\":\"slow\",\"body\":\"slow\"}";
        }

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "noop"));
    }

    private sealed class NoopConversationService : IConversationService
    {
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<ConversationListResponse> ListPageAsync(int portfolioId, ListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ConversationListResponse());

        public Task<int> GetUnreadCountAsync(int portfolioId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<ConversationDetail?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> StartAsync(
            int portfolioId,
            int tenantId,
            string subject,
            string body,
            List<string> channels,
            string operationKey,
            bool acknowledgedFairHousingReview = false,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(new ConversationDetail
            {
                Id = 1,
                TenantId = tenantId,
                Subject = subject,
            });

        public Task<ConversationDetail?> PostMessageAsync(
            int portfolioId,
            int id,
            string body,
            List<string> channels,
            string operationKey,
            CancellationToken ct = default) => Task.FromResult<ConversationDetail?>(null);

        public Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(
            int portfolioId,
            int tenantId,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<ConversationDetail?> GetForTenantAsync(
            int portfolioId,
            int tenantId,
            int id,
            CancellationToken ct = default) => Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> TenantStartAsync(
            int portfolioId,
            int tenantId,
            string subject,
            string body,
            string operationKey,
            CancellationToken ct = default) => Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> TenantPostAsync(
            int portfolioId,
            int tenantId,
            int id,
            string body,
            string operationKey,
            CancellationToken ct = default) => Task.FromResult<ConversationDetail?>(null);
    }
}
