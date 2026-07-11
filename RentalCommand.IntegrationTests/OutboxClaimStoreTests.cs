using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Data;
using RentalCommand.Data.Outbox;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for claim ordering, concurrency, expiry, and fencing.</summary>
public sealed class OutboxClaimStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        _dockerAvailable = await DockerSocketPreflightAsync();
        if (!_dockerAvailable) return;

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("rentalcommand")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Deferred_old_row_does_not_hide_later_eligible_row()
    {
        SkipIfDockerUnavailable();
        var now = Utc(2026, 7, 10, 20, 0);
        await SeedAsync(
            Message("deferred", now.AddHours(-2), now.AddHours(1)),
            Message("ready", now.AddHours(-1), now));

        await using var db = NewContext();
        var claims = await new OutboxClaimStore(db).ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), 10);

        claims.Should().ContainSingle().Which.IdempotencyKey.Should().Be("ready");
    }

    [SkippableFact]
    public async Task Concurrent_workers_claim_each_row_at_most_once()
    {
        SkipIfDockerUnavailable();
        var now = DateTime.UtcNow;
        await SeedAsync(Enumerable.Range(1, 12)
            .Select(index => Message($"concurrent-{index}", now.AddMinutes(-index), now))
            .ToArray());

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var results = await Task.WhenAll(
            new OutboxClaimStore(dbA).ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), 12),
            new OutboxClaimStore(dbB).ClaimAsync("worker-b", now, TimeSpan.FromMinutes(2), 12));

        var all = results.SelectMany(rows => rows).ToArray();
        all.Should().HaveCount(12);
        all.Select(row => row.Id).Should().OnlyHaveUniqueItems();
    }

    [SkippableFact]
    public async Task Expired_claim_is_reclaimed_with_new_fencing_token()
    {
        SkipIfDockerUnavailable();
        var now = DateTime.UtcNow;
        var row = Message("expired", now.AddMinutes(-10), now.AddMinutes(-10));
        row.ClaimOwner = "dead-worker";
        row.ClaimToken = Guid.NewGuid();
        row.ClaimExpiresAtUtc = now.AddSeconds(-1);
        var oldToken = row.ClaimToken;
        await SeedAsync(row);

        await using var db = NewContext();
        var claim = (await new OutboxClaimStore(db)
            .ClaimAsync("replacement-worker", now, TimeSpan.FromMinutes(2), 1)).Single();

        claim.ClaimToken.Should().NotBe(oldToken!.Value);
        claim.AttemptCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task Stale_completion_cannot_finalize_reclaimed_row()
    {
        SkipIfDockerUnavailable();
        var now = DateTime.UtcNow;
        await SeedAsync(Message("fenced", now.AddMinutes(-1), now));

        Guid staleToken;
        long id;
        await using (var firstDb = NewContext())
        {
            var first = (await new OutboxClaimStore(firstDb)
                .ClaimAsync("worker-a", now, TimeSpan.FromSeconds(1), 1)).Single();
            staleToken = first.ClaimToken;
            id = first.Id;
        }

        await using var secondDb = NewContext();
        var store = new OutboxClaimStore(secondDb);
        var current = (await store.ClaimAsync("worker-b", now.AddSeconds(2), TimeSpan.FromMinutes(2), 1)).Single();

        (await store.MarkAcceptedAsync(id, staleToken, now.AddSeconds(3), "fake", "stale"))
            .Should().Be(0);
        (await store.MarkAcceptedAsync(id, current.ClaimToken, now.AddSeconds(3), "fake", "current"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Configuration_failure_is_terminal_and_never_claimed_again()
    {
        SkipIfDockerUnavailable();
        var now = DateTime.UtcNow;
        await SeedAsync(Message("blocked", now.AddMinutes(-1), now));

        await using var db = NewContext();
        var store = new OutboxClaimStore(db);
        var claim = (await store.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), 1)).Single();
        (await store.MarkDeadLetteredAsync(
            claim.Id,
            claim.ClaimToken,
            now,
            OutboxFailureKind.ConfigurationBlocked,
            "Provider is not configured."))
            .Should().Be(1);

        (await store.ClaimAsync("worker-b", now.AddHours(1), TimeSpan.FromMinutes(2), 1))
            .Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Worker_marks_email_accepted_only_after_channel_returns()
    {
        SkipIfDockerUnavailable();
        await ResetOutboxAsync();
        var now = DateTime.UtcNow;
        await SeedAsync(new OutboxMessage
        {
            MessageType = "email",
            Payload = """{"to":"tenant@example.com","subject":"Hello","body":"Body"}""",
            IdempotencyKey = "dispatch-email",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        var channel = new CapturingChannel();

        await RunWorkerAsync(channel, new CapturingPushSender());

        channel.Emails.Should().ContainSingle().Which.Should().Be("tenant@example.com");
        await using var verify = NewContext();
        var row = await verify.OutboxMessages.SingleAsync(message => message.IdempotencyKey == "dispatch-email");
        row.AcceptedAtUtc.Should().NotBeNull();
        row.Provider.Should().Be("email");
        row.ProviderMessageId.Should().Be("email-provider-id");
        row.ClaimToken.Should().BeNull();
    }

    [SkippableFact]
    public async Task Worker_records_missing_provider_as_blocked_not_accepted()
    {
        SkipIfDockerUnavailable();
        await ResetOutboxAsync();
        var now = DateTime.UtcNow;
        await SeedAsync(new OutboxMessage
        {
            MessageType = "email",
            Payload = """{"to":"tenant@example.com","subject":"Hello","body":"Body"}""",
            IdempotencyKey = "dispatch-blocked",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        var channel = new CapturingChannel { SuppressEmail = true };

        await RunWorkerAsync(channel, new CapturingPushSender());

        await using var verify = NewContext();
        var row = await verify.OutboxMessages.SingleAsync(message => message.IdempotencyKey == "dispatch-blocked");
        row.AcceptedAtUtc.Should().BeNull();
        row.DeadLetteredAtUtc.Should().NotBeNull();
        row.FailureKind.Should().Be(OutboxFailureKind.ConfigurationBlocked);
    }

    [SkippableFact]
    public async Task Worker_sends_one_destination_specific_push_row()
    {
        SkipIfDockerUnavailable();
        await ResetOutboxAsync();
        var now = DateTime.UtcNow;
        await SeedAsync(new OutboxMessage
        {
            MessageType = "push",
            Payload = """{"deviceToken":"device-a","title":"Rent","body":"Received"}""",
            IdempotencyKey = "dispatch-push:device-a",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        var push = new CapturingPushSender();

        await RunWorkerAsync(new CapturingChannel(), push);

        push.Tokens.Should().Equal("device-a");
        await using var verify = NewContext();
        var row = await verify.OutboxMessages.SingleAsync(message => message.IdempotencyKey == "dispatch-push:device-a");
        row.Provider.Should().Be("fcm");
        row.ProviderMessageId.Should().Be("push-provider-id");
    }

    [SkippableFact]
    public async Task Worker_retries_durable_blob_cleanup_until_storage_accepts_it()
    {
        SkipIfDockerUnavailable();
        await ResetOutboxAsync();
        var now = DateTime.UtcNow;
        await SeedAsync(new OutboxMessage
        {
            MessageType = "blob-delete",
            Payload = """{"storagePath":"stored/document.pdf"}""",
            IdempotencyKey = "stored-file-delete:41",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        var storage = new CapturingFileStorage { RemainingDeleteFailures = 1 };

        await RunWorkerAsync(new CapturingChannel(), new CapturingPushSender(), storage);

        await using (var retryDb = NewContext())
        {
            var failed = await retryDb.OutboxMessages.SingleAsync(message => message.IdempotencyKey == "stored-file-delete:41");
            failed.AcceptedAtUtc.Should().BeNull();
            failed.DeadLetteredAtUtc.Should().BeNull();
            failed.AttemptCount.Should().Be(1);
            failed.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await retryDb.SaveChangesAsync();
        }

        await RunWorkerAsync(new CapturingChannel(), new CapturingPushSender(), storage);

        storage.DeleteAttempts.Should().Equal("stored/document.pdf", "stored/document.pdf");
        await using var verify = NewContext();
        var accepted = await verify.OutboxMessages.SingleAsync(message => message.IdempotencyKey == "stored-file-delete:41");
        accepted.AcceptedAtUtc.Should().NotBeNull();
        accepted.Provider.Should().Be("file-storage");
        accepted.ProviderMessageId.Should().Be($"outbox-{accepted.Id}");
    }

    private async Task SeedAsync(params OutboxMessage[] messages)
    {
        await using var db = NewContext();
        db.OutboxMessages.AddRange(messages);
        await db.SaveChangesAsync();
    }

    private async Task ResetOutboxAsync()
    {
        await using var db = NewContext();
        await db.OutboxMessages.ExecuteDeleteAsync();
    }

    private async Task RunWorkerAsync(
        INotificationChannel channel,
        IPushSender pushSender,
        IFileStorage? fileStorage = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RentalCommandDbContext>(options => options.UseNpgsql(_connectionString!));
        services.AddScoped<IOutboxClaimStore, OutboxClaimStore>();
        services.AddScoped(_ => channel);
        services.AddScoped<INotificationChannel>(_ => channel);
        services.AddSingleton(pushSender);
        services.AddSingleton<IPushSender>(pushSender);
        services.AddSingleton<IFileStorage>(fileStorage ?? new CapturingFileStorage());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var worker = new TestableOutboxWorker(provider);
        await worker.RunCycleAsync(scope.ServiceProvider);
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString!)
            .Options);

    private static OutboxMessage Message(string key, DateTime createdAtUtc, DateTime nextAttemptAtUtc) => new()
    {
        MessageType = "email",
        Payload = "{}",
        IdempotencyKey = key,
        CreatedAtUtc = createdAtUtc,
        NextAttemptAtUtc = nextAttemptAtUtc,
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private void SkipIfDockerUnavailable() =>
        Skip.IfNot(_dockerAvailable, "Docker socket preflight failed; PostgreSQL outbox tests skipped.");

    private static async Task<bool> DockerSocketPreflightAsync()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (Uri.TryCreate(dockerHost, UriKind.Absolute, out var hostUri)
            && hostUri.Scheme is "tcp" or "http" or "https")
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(hostUri.Host, hostUri.Port > 0 ? hostUri.Port : 2375);
                return true;
            }
            catch (SocketException) { return false; }
        }

        var candidates = new[]
        {
            dockerHost?.StartsWith("unix://", StringComparison.Ordinal) == true ? dockerHost[7..] : null,
            "/var/run/docker.sock",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima/default/docker.sock"),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal);

        foreach (var path in candidates)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path!));
                return true;
            }
            catch (SocketException) { }
        }

        return false;
    }

    private sealed class TestableOutboxWorker : OutboxDispatchWorker
    {
        public TestableOutboxWorker(IServiceProvider services)
            : base(services, NullLogger<OutboxDispatchWorker>.Instance) { }

        public Task<int> RunCycleAsync(IServiceProvider scopedProvider) =>
            ExecuteCycleAsync(scopedProvider, CancellationToken.None);
    }

    private sealed class CapturingChannel : INotificationChannel
    {
        public List<string> Emails { get; } = [];
        public bool SuppressEmail { get; init; }

        public Task<NotificationDeliveryReceipt> SendSmsAsync(
            string toPhoneNumber,
            string message,
            NotificationDeliveryContext delivery,
            int? portfolioId = null,
            CancellationToken ct = default) =>
            Task.FromResult(new NotificationDeliveryReceipt("sms", "sms-provider-id"));

        public Task<NotificationDeliveryReceipt> SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            NotificationDeliveryContext delivery,
            string? htmlBody = null,
            CancellationToken ct = default)
        {
            if (SuppressEmail)
                throw new NotificationDeliverySuppressedException("Email provider is not configured.");
            Emails.Add(toEmail);
            return Task.FromResult(new NotificationDeliveryReceipt("email", "email-provider-id"));
        }
    }

    private sealed class CapturingPushSender : IPushSender
    {
        public List<string> Tokens { get; } = [];

        public Task<PushSendResult> SendAsync(
            string deviceToken,
            string title,
            string body,
            IReadOnlyDictionary<string, string>? data,
            NotificationDeliveryContext delivery,
            CancellationToken ct = default)
        {
            Tokens.Add(deviceToken);
            return Task.FromResult(PushSendResult.Ok("push-provider-id"));
        }
    }

    private sealed class CapturingFileStorage : IFileStorage
    {
        public int RemainingDeleteFailures { get; set; }
        public List<string> DeleteAttempts { get; } = [];

        public Task<string> UploadAsync(
            Stream content,
            string fileName,
            string contentType,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            DeleteAttempts.Add(path);
            if (RemainingDeleteFailures-- > 0)
            {
                throw new IOException("Simulated transient storage failure.");
            }

            return Task.CompletedTask;
        }
    }
}
