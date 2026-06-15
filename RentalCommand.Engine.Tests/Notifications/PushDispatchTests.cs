using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine.Tests.Notifications;

/// <summary>
/// The push send rail (P0-2 / TSK-207): AutomationNotifier enqueues a portfolio-scoped <c>push</c>
/// outbox row exactly when the channel matrix has Push enabled, and OutboxDispatchWorker fans that
/// row out to every registered device token for the portfolio — pruning tokens the provider reports
/// as permanently invalid and no-opping cleanly when there are no devices.
/// </summary>
public sealed class PushDispatchTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _provider;
    private readonly CapturingPushSender _push = new();

    public PushDispatchTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<RentalCommandDbContext>(_ => new RentalCommandDbContext(options));
        services.AddScoped<INotificationChannel>(_ => new ThrowingNotificationChannel());
        services.AddSingleton<IPushSender>(_push);
        services.AddScoped<ISandboxGuard, SandboxGuard>();
        services.AddScoped<IMessagePublisher, OutboxMessagePublisher>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.Database.EnsureCreated();
        db.Portfolios.Add(new Portfolio
        {
            Id = 1, Name = "P1", ManagementCompanyName = "Co", TimeZone = "UTC",
            IsSandbox = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Notifier_enqueues_a_push_row_when_push_is_enabled()
    {
        var channels = new NotificationChannelPreference
        {
            EnableInApp = false, EnableEmail = false, EnableSms = false, EnablePush = true,
        };

        await SendAsync(channels, actionUrl: "/payments/123", type: "RentConfirmation");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var push = await db.OutboxMessages.SingleAsync(m => m.MessageType == "push");
        push.PortfolioId.Should().Be(1);
        push.Payload.Should().Contain("/payments/123");
        push.Payload.Should().Contain("RentConfirmation");
    }

    [Fact]
    public async Task Notifier_does_not_enqueue_a_push_row_when_push_is_disabled()
    {
        var channels = new NotificationChannelPreference
        {
            EnableInApp = false, EnableEmail = false, EnableSms = false, EnablePush = false,
        };

        await SendAsync(channels);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        (await db.OutboxMessages.CountAsync(m => m.MessageType == "push")).Should().Be(0);
    }

    [Fact]
    public async Task Worker_fans_a_push_row_out_to_every_device_token()
    {
        await SeedDeviceTokens("tok-A", "tok-B");
        await SeedPushRow(actionUrl: "/work-orders/42");

        await RunCycle();

        _push.Sent.Should().HaveCount(2);
        _push.Sent.Select(s => s.Token).Should().BeEquivalentTo(new[] { "tok-A", "tok-B" });
        _push.Sent[0].Data.Should().ContainKey("actionUrl");
        _push.Sent[0].Data["actionUrl"].Should().Be("/work-orders/42");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        (await db.OutboxMessages.SingleAsync(m => m.MessageType == "push")).SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Worker_prunes_a_device_token_the_provider_reports_invalid()
    {
        await SeedDeviceTokens("good", "dead");
        _push.InvalidTokens.Add("dead");
        await SeedPushRow();

        await RunCycle();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var remaining = await db.DeviceTokens.Select(d => d.Token).ToListAsync();
        remaining.Should().BeEquivalentTo(new[] { "good" });
    }

    [Fact]
    public async Task Worker_marks_push_sent_with_no_devices_and_sends_nothing()
    {
        await SeedPushRow();

        await RunCycle();

        _push.Sent.Should().BeEmpty();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        (await db.OutboxMessages.SingleAsync(m => m.MessageType == "push")).SentAt.Should().NotBeNull();
    }

    private async Task SendAsync(NotificationChannelPreference channels, string? actionUrl = null, string type = "System")
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();
        var notifier = new AutomationNotifier(db, publisher);
        await notifier.SendAsync(
            portfolioId: 1,
            channels,
            new AutomationNotifier.InAppContent(type, "Title", "Body", ActionUrl: actionUrl),
            email: null,
            sms: null,
            now: DateTime.UtcNow,
            ct: CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private async Task SeedDeviceTokens(params string[] tokens)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        foreach (var t in tokens)
        {
            db.DeviceTokens.Add(new DeviceToken
            {
                PortfolioId = 1, UserId = 10, Token = t, Platform = "android",
                CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedPushRow(string? actionUrl = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = 1,
            MessageType = "push",
            Payload = System.Text.Json.JsonSerializer.Serialize(
                new { title = "Hi", body = "There", actionUrl, type = "System" }),
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

    private sealed class TestableOutboxWorker : OutboxDispatchWorker
    {
        public TestableOutboxWorker(IServiceProvider sp)
            : base(sp, NullLogger<OutboxDispatchWorker>.Instance) { }

        public Task RunCycleAsync(IServiceProvider scoped) => ExecuteCycleAsync(scoped, CancellationToken.None);
    }

    private sealed class CapturingPushSender : IPushSender
    {
        public List<(string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data)> Sent { get; } = new();
        public HashSet<string> InvalidTokens { get; } = new();

        public Task<PushSendResult> SendAsync(
            string deviceToken, string title, string body,
            IReadOnlyDictionary<string, string>? data, CancellationToken ct = default)
        {
            if (InvalidTokens.Contains(deviceToken))
            {
                return Task.FromResult(PushSendResult.Invalid());
            }
            Sent.Add((deviceToken, title, body, data ?? new Dictionary<string, string>()));
            return Task.FromResult(PushSendResult.Ok());
        }
    }

    private sealed class ThrowingNotificationChannel : INotificationChannel
    {
        public Task SendEmailAsync(string toEmail, string subject, string body, string? htmlBody = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("email channel should not be used by push tests");

        public Task SendSmsAsync(string toPhoneNumber, string message, int? portfolioId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("sms channel should not be used by push tests");
    }
}
