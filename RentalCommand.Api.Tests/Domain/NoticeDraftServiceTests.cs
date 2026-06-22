using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class NoticeDraftServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GenerateAsync_DefaultRunOnlyCreatesLeaseNoticesInsideTriggerWindow()
    {
        var (nearLease, farLease) = SeedActiveLeases();
        var sut = CreateService();

        var result = await sut.GenerateAsync(1);

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == nearLease.Id &&
            d.NoticeType == "RenewalOffer");
        result.Drafts.Should().NotContain(d => d.LeaseId == farLease.Id);
    }

    [Fact]
    public async Task GenerateAsync_ForcedMoveOutNoticeCanTargetLeaseOutsideTriggerWindow()
    {
        var (_, farLease) = SeedActiveLeases();
        var sut = CreateService();

        var result = await sut.GenerateAsync(
            1,
            new GenerateNoticeDraftsRequest
            {
                TenantId = farLease.TenantId,
                NoticeType = "MoveOutReminder",
            });

        result.CreatedCount.Should().Be(1);
        result.Drafts.Should().ContainSingle(d =>
            d.LeaseId == farLease.Id &&
            d.NoticeType == "MoveOutReminder");
    }

    private NoticeDraftService CreateService() => new(
        _ctx.Db,
        new NoopConversationService(),
        new NoopLlmProvider(),
        NullLogger<NoticeDraftService>.Instance);

    private (Lease NearLease, Lease FarLease) SeedActiveLeases()
    {
        var now = DateTime.UtcNow;
        var property = new RentalCommand.Core.Entities.Property
        {
            PortfolioId = 1,
            Name = "Maple Grove Duplex",
            AddressLine1 = "100 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nearUnit = new Unit
        {
            Property = property,
            UnitNumber = "A",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1400,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var farUnit = new Unit
        {
            Property = property,
            UnitNumber = "B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nearTenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Avery",
            LastName = "Brooks",
            Email = "avery@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var farTenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Morgan",
            LastName = "Pierce",
            Email = "morgan@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nearLease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = nearUnit,
            Tenant = nearTenant,
            LeaseNumber = "NEAR-001",
            Status = LeaseStatus.Active,
            StartDate = now.Date.AddMonths(-10),
            EndDate = now.Date.AddDays(60),
            MonthlyRent = 1400,
            SecurityDeposit = 1400,
            LateFeeAmount = 50,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var farLease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = farUnit,
            Tenant = farTenant,
            LeaseNumber = "FAR-001",
            Status = LeaseStatus.Active,
            StartDate = now.Date.AddMonths(-2),
            EndDate = now.Date.AddDays(120),
            MonthlyRent = 1450,
            SecurityDeposit = 1450,
            LateFeeAmount = 50,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.AddRange(nearLease, farLease);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return (nearLease, farLease);
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

    private sealed class NoopConversationService : IConversationService
    {
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<ConversationListResponse> ListPageAsync(int portfolioId, ListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new ConversationListResponse());

        public Task<int> GetUnreadCountAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<ConversationDetail?> GetAsync(int portfolioId, int id, CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> StartAsync(
            int portfolioId,
            int tenantId,
            string subject,
            string body,
            List<string> channels,
            bool acknowledgedFairHousingReview = false,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(new ConversationDetail { Id = 1, TenantId = tenantId, Subject = subject });

        public Task<ConversationDetail?> PostMessageAsync(
            int portfolioId,
            int id,
            string body,
            List<string> channels,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(
            int portfolioId,
            int tenantId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<ConversationDetail?> GetForTenantAsync(
            int portfolioId,
            int tenantId,
            int id,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> TenantStartAsync(
            int portfolioId,
            int tenantId,
            string subject,
            string body,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);

        public Task<ConversationDetail?> TenantPostAsync(
            int portfolioId,
            int tenantId,
            int id,
            string body,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationDetail?>(null);
    }
}
