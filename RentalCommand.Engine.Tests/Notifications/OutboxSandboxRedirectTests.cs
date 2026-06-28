using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine.Tests.Notifications;

/// <summary>
/// Sandbox notification behaviour: generic messages scoped to a Sandbox portfolio are redirected to
/// the portfolio owner's own inbox, tagged <c>[Sandbox]</c>. Lease e-sign signing links are a typed
/// exception and stay addressed to the lease signer. A Live portfolio sends to the original recipient
/// unchanged.
/// </summary>
public sealed class OutboxSandboxRedirectTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _provider;
    private readonly CapturingNotificationChannel _channel = new();

    public OutboxSandboxRedirectTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<RentalCommandDbContext>(_ => new RentalCommandDbContext(options));
        services.AddScoped<INotificationChannel>(_ => _channel);
        // The outbox worker now also resolves IPushSender; a no-op suppressor keeps these
        // email/SMS-focused tests unaffected (no device tokens are seeded here anyway).
        services.AddSingleton<IPushSender>(new NoOpPushSender());
        services.AddScoped<ISandboxGuard, SandboxGuard>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.Database.EnsureCreated();

        // Sandbox portfolio (id 1) owned by the landlord; Live portfolio (id 2).
        db.Portfolios.Add(NewPortfolio(1, isSandbox: true));
        db.Portfolios.Add(NewPortfolio(2, isSandbox: false));

        // A real tenant for the sandbox portfolio so the tenant-portal user FK resolves.
        db.Tenants.Add(new Tenant { Id = 99, PortfolioId = 1, FirstName = "Tee", LastName = "Nant", Email = "real-tenant@example.com" });
        db.SaveChanges();

        // Landlord/owner account (PortfolioId set, no TenantId) plus a tenant-portal account that must
        // NOT be picked as the redirect target.
        // Tenant-portal user has a LOWER id than the owner: only the TenantId==null filter (not id
        // ordering) keeps it from being chosen as the redirect target.
        db.Users.Add(new ApplicationUser { Id = 9, Email = "tenant-portal@example.com", PortfolioId = 1, TenantId = 99, UserName = "tenant-portal@example.com" });
        db.Users.Add(new ApplicationUser { Id = 10, Email = "owner@landlord.test", PortfolioId = 1, UserName = "owner@landlord.test" });
        db.Users.Add(new ApplicationUser { Id = 20, Email = "live-owner@landlord.test", PortfolioId = 2, UserName = "live-owner@landlord.test" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Sandbox_email_is_redirected_to_portfolio_owner_and_tagged()
    {
        await SeedEmail(portfolioId: 1, to: "real-tenant@example.com", subject: "Please sign your lease");
        await RunCycle();

        _channel.Emails.Should().HaveCount(1);
        var (to, subject, body, _) = _channel.Emails[0];
        to.Should().Be("owner@landlord.test");
        subject.Should().StartWith("[Sandbox]");
        body.Should().Contain("real-tenant@example.com");
    }

    [Fact]
    public async Task Sandbox_email_prefers_self_owner_user_even_when_that_user_has_tenant_link()
    {
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            db.OwnerEntities.Add(new OwnerEntity
            {
                Id = 77,
                PortfolioId = 1,
                Name = "Real Owner",
                Email = "real-owner@example.com",
                IsPrimary = true,
            });
            db.Users.Add(new ApplicationUser
            {
                Id = 88,
                Email = "real-owner@example.com",
                PortfolioId = 1,
                TenantId = 99,
                OwnerEntityId = 77,
                UserName = "real-owner@example.com",
            });
            await db.SaveChangesAsync();
        }

        await SeedEmail(portfolioId: 1, to: "real-tenant@example.com", subject: "Please sign your lease");
        await RunCycle();

        _channel.Emails.Should().HaveCount(1);
        var (to, subject, body, _) = _channel.Emails[0];
        to.Should().Be("real-owner@example.com");
        subject.Should().StartWith("[Sandbox]");
        body.Should().Contain("real-tenant@example.com");
    }

    [Fact]
    public async Task Sandbox_lease_esign_email_goes_to_the_lease_signer_without_owner_redirect()
    {
        await SeedLeaseEsignEmail(portfolioId: 1, to: "real-tenant@example.com", subject: "Please sign: Lease L-2026-7");
        await RunCycle();

        _channel.Emails.Should().HaveCount(1);
        var (to, subject, body, _) = _channel.Emails[0];
        to.Should().Be("real-tenant@example.com");
        subject.Should().Be("Please sign: Lease L-2026-7");
        body.Should().NotContain("intended for real-tenant@example.com");
    }

    [Fact]
    public async Task Live_email_goes_to_the_original_recipient_unchanged()
    {
        await SeedEmail(portfolioId: 2, to: "real-tenant@example.com", subject: "Please sign your lease");
        await RunCycle();

        _channel.Emails.Should().HaveCount(1);
        var (to, subject, _, _) = _channel.Emails[0];
        to.Should().Be("real-tenant@example.com");
        subject.Should().Be("Please sign your lease");
    }

    [Fact]
    public async Task Suppressed_email_is_terminal_and_keeps_reason_for_queue_visibility()
    {
        _channel.SuppressEmailWith("Email delivery is not configured for this environment.");
        await SeedLeaseEsignEmail(portfolioId: 1, to: "real-tenant@example.com", subject: "Please sign: Lease L-2026-7");

        await RunCycle();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var message = await db.OutboxMessages.AsNoTracking().SingleAsync();

        message.SentAt.Should().NotBeNull();
        message.FailedAt.Should().BeNull();
        message.RetryCount.Should().Be(0);
        message.Error.Should().Be("Email delivery is not configured for this environment.");
    }

    private async Task SeedEmail(int portfolioId, string to, string subject)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = "email",
            Payload = System.Text.Json.JsonSerializer.Serialize(new { to, subject, body = "Click the link to sign." }),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedLeaseEsignEmail(int portfolioId, string to, string subject)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = "email",
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                source = OutboxPayloadSources.LeaseEsignSigningLink,
                leaseId = 123,
                to,
                subject,
                body = "Click the link to sign.",
            }),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task RunCycle()
    {
        var worker = new TestableOutboxWorker(_provider);
        using var scope = _provider.CreateScope();
        await worker.RunCycleAsync(scope.ServiceProvider);
    }

    private static Portfolio NewPortfolio(int id, bool isSandbox) => new()
    {
        Id = id,
        Name = $"P{id}",
        ManagementCompanyName = "Co",
        TimeZone = "UTC",
        IsSandbox = isSandbox,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>Exposes the protected cycle so a single drain can be driven from a test.</summary>
    private sealed class TestableOutboxWorker : OutboxDispatchWorker
    {
        public TestableOutboxWorker(IServiceProvider sp)
            : base(sp, NullLogger<OutboxDispatchWorker>.Instance) { }

        public Task RunCycleAsync(IServiceProvider scoped) => ExecuteCycleAsync(scoped, CancellationToken.None);
    }

    private sealed class NoOpPushSender : IPushSender
    {
        public Task<PushSendResult> SendAsync(
            string deviceToken, string title, string body,
            IReadOnlyDictionary<string, string>? data, CancellationToken ct = default) =>
            Task.FromResult(PushSendResult.Suppressed);
    }

    private sealed class CapturingNotificationChannel : INotificationChannel
    {
        public List<(string To, string Subject, string Body, string? HtmlBody)> Emails { get; } = new();
        public List<(string To, string Message)> SmsMessages { get; } = new();
        private string? _emailSuppressionReason;

        public void SuppressEmailWith(string reason)
        {
            _emailSuppressionReason = reason;
        }

        public Task SendEmailAsync(string toEmail, string subject, string body, string? htmlBody = null, CancellationToken ct = default)
        {
            if (_emailSuppressionReason is not null)
            {
                throw new NotificationDeliverySuppressedException(_emailSuppressionReason);
            }

            Emails.Add((toEmail, subject, body, htmlBody));
            return Task.CompletedTask;
        }

        public Task SendSmsAsync(string toPhoneNumber, string message, int? portfolioId = null, CancellationToken ct = default)
        {
            SmsMessages.Add((toPhoneNumber, message));
            return Task.CompletedTask;
        }
    }
}
