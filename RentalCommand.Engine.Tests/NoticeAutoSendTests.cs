using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Tests.Automation;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests;

public class NoticeAutoSendTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task AutoSend_type_with_template_is_approved_not_left_draft()
    {
        SeedLeaseEndingSoon();
        SeedSettings(LeaseEndAutoAction.Renewal);
        _ctx.Db.NoticeTemplates.Add(new NoticeTemplate
        {
            PortfolioId = 1,
            NoticeType  = "RenewalOffer",
            Subject     = "S",
            Body        = "Hi {{tenant_name}}",
            IsActive    = true,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var svc = BuildService();
        await svc.GenerateAllAsync(default);

        var renewal = _ctx.Db.NoticeDrafts.Single(d => d.PortfolioId == 1 && d.NoticeType == "RenewalOffer");
        Assert.Equal("Approved", renewal.Status);
        Assert.NotNull(renewal.ApprovedAt);
    }

    [Fact]
    public async Task AutoSend_NonRenewal_approves_moveout_and_leaves_renewal_draft()
    {
        // Lease within the move-out window (≤30 days) so both MoveOut and Renewal drafts generate.
        SeedLeaseEndingSoon(daysToEnd: 20);
        SeedSettings(LeaseEndAutoAction.NonRenewal);
        _ctx.Db.NoticeTemplates.Add(new NoticeTemplate
        {
            PortfolioId = 1,
            NoticeType  = "MoveOutReminder",
            Subject     = "S",
            Body        = "Hi {{tenant_name}}",
            IsActive    = true,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var svc = BuildService();
        await svc.GenerateAllAsync(default);

        var moveOut = _ctx.Db.NoticeDrafts.Single(d => d.PortfolioId == 1 && d.NoticeType == "MoveOutReminder");
        Assert.Equal("Approved", moveOut.Status);
        Assert.NotNull(moveOut.ApprovedAt);

        // The non-matching lease-end draft stays in the queue for manual review.
        var renewal = _ctx.Db.NoticeDrafts.Single(d => d.PortfolioId == 1 && d.NoticeType == "RenewalOffer");
        Assert.Equal("Draft", renewal.Status);
    }

    [Fact]
    public async Task AutoSend_without_template_stays_draft()
    {
        SeedLeaseEndingSoon();
        SeedSettings(LeaseEndAutoAction.Renewal);
        // No template authored.
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var svc = BuildService();
        await svc.GenerateAllAsync(default);

        var renewal = _ctx.Db.NoticeDrafts.Single(d => d.PortfolioId == 1 && d.NoticeType == "RenewalOffer");
        Assert.Equal("Draft", renewal.Status);
    }

    // -----------------------------------------------------------------------
    // Harness

    private NoticeDraftGenerationService BuildService()
    {
        var cfg = new NotificationsConfig
        {
            EnableNoticeAutopilot = true,
            NotifyTenants         = true,
            // ChannelPreferences left empty → ResolveChannels falls back to defaults
            // (in-app + email on) so LeaseExpiry resolves to ≥1 channel.
        };

        var noticeDraftService = new NoticeDraftService(
            _ctx.Db,
            new NoopConversationService(),
            new NoopLlmProvider(),
            NullLogger<NoticeDraftService>.Instance,
            TimeProvider.System);

        return new NoticeDraftGenerationService(
            _ctx.Db,
            noticeDraftService,
            new FakeNotificationSettingsService(cfg),
            NullLogger<NoticeDraftGenerationService>.Instance);
    }

    private void SeedSettings(LeaseEndAutoAction action)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Set<NotificationSettings>().Add(new NotificationSettings
        {
            PortfolioId        = 1,
            NotifyTenants      = true,
            LeaseEndAutoAction = action,
            CreatedAt          = now,
            UpdatedAt          = now,
        });
        _ctx.Db.SaveChanges();
    }

    /// <summary>Seeds one active lease ending inside the lease-end trigger window.</summary>
    private Lease SeedLeaseEndingSoon(int daysToEnd = 45)
    {
        var now   = DateTime.UtcNow;
        var today = now.Date;

        var property = new Property
        {
            PortfolioId  = 1,
            Name         = "Maple Grove Duplex",
            AddressLine1 = "100 Maple",
            City         = "Columbus",
            State        = "OH",
            PostalCode   = "43215",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var unit = new Unit
        {
            Property   = property,
            UnitNumber = "A",
            Bedrooms   = 2,
            Bathrooms  = 1,
            MarketRent = 1400,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName   = "Avery",
            LastName    = "Brooks",
            Email       = "avery@example.test",
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        var lease = new Lease
        {
            PortfolioId     = 1,
            Property        = property,
            Unit            = unit,
            Tenant          = tenant,
            LeaseNumber     = "NEAR-001",
            Status          = LeaseStatus.Active,
            StartDate       = today.AddMonths(-10),
            EndDate         = today.AddDays(daysToEnd),
            MonthlyRent     = 1400,
            SecurityDeposit = 1400,
            LateFeeAmount   = 50,
            RentDueDay      = 1,
            CreatedAt       = now,
            UpdatedAt       = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return lease;
    }

    // -----------------------------------------------------------------------
    // Minimal no-op collaborators (copied from RentalCommand.Api.Tests).

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
