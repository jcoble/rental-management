using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;

// Npgsql maps DateTime to `timestamp with time zone`; legacy behavior lets us write
// Unspecified-kind DateTimes (matches the API host's configuration).
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Missing connection string 'DefaultConnection'. Set it in appsettings.json or via configuration.");

builder.Services.AddDbContext<RentalCommandDbContext>(options => options.UseNpgsql(connectionString));

// DB-outbox publisher + notification channel (Twilio SMS / SendGrid email; logs when unconfigured).
builder.Services.AddScoped<IMessagePublisher, OutboxMessagePublisher>();
builder.Services.AddHttpClient<INotificationChannel, RoutingNotificationChannel>();

// Scan pipeline: config, LLM provider, file storage, no-op data-update, worker.
builder.Services.Configure<AssistantConfig>(builder.Configuration.GetSection(AssistantConfig.SectionName));
builder.Services.Configure<UploadSettings>(builder.Configuration.GetSection(UploadSettings.SectionName));
builder.Services.Configure<NotificationsConfig>(builder.Configuration.GetSection(NotificationsConfig.SectionName));
var llmProvider = builder.Configuration.GetValue<string>("Assistant:Provider") ?? "openai";
if (string.Equals(llmProvider, "anthropic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<ILlmProvider, AnthropicLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.anthropic.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else // default: openai
{
    builder.Services.AddHttpClient<ILlmProvider, OpenAiLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.openai.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
builder.Services.AddScoped<IFileStorage, DiskFileStorage>();
builder.Services.AddSingleton<IDataUpdateService, EngineDataUpdateService>();

// Phase 4 automation services (gated by Notifications flags; financial ones default OFF).
builder.Services.AddScoped<IRentChargeService, RentChargeService>();
builder.Services.AddScoped<ILateFeeService, LateFeeService>();
builder.Services.AddScoped<ILeaseExpiryReminderService, LeaseExpiryReminderService>();

// Workers (each is its own BackgroundService).
builder.Services.AddHostedService<OutboxDispatchWorker>();
builder.Services.AddHostedService<ScanProcessingWorker>();
builder.Services.AddHostedService<RentChargeWorker>();
builder.Services.AddHostedService<LateFeeWorker>();
builder.Services.AddHostedService<LeaseExpiryReminderWorker>();

var host = builder.Build();

// --- Single-instance safety: PostgreSQL advisory lock ---
// Hold a dedicated, non-pooled connection open for the host's lifetime so the advisory
// lock is held for as long as the Engine runs and released cleanly on shutdown.
var logger = host.Services.GetRequiredService<ILogger<Program>>();

var lockConnString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
var lockConnection = new NpgsqlConnection(lockConnString);
await lockConnection.OpenAsync();

await using (var lockCmd = lockConnection.CreateCommand())
{
    // pg_try_advisory_lock returns immediately; if another Engine already holds the lock we abort
    // rather than block forever.
    lockCmd.CommandText = $"SELECT pg_try_advisory_lock({Program.AdvisoryLockKey})";
    var acquired = (bool?)await lockCmd.ExecuteScalarAsync() ?? false;
    if (!acquired)
    {
        logger.LogError(
            "Another Engine instance already holds advisory lock {LockKey}. Exiting.",
            Program.AdvisoryLockKey);
        await lockConnection.DisposeAsync();
        return;
    }
}

logger.LogInformation(
    "Engine advisory lock {LockKey} acquired (PID {Pid}) — this is the only running instance",
    Program.AdvisoryLockKey, Environment.ProcessId);

// Release the lock on graceful shutdown.
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation(
        "Engine shutting down (PID {Pid}) — releasing advisory lock {LockKey}",
        Environment.ProcessId, Program.AdvisoryLockKey);
    try
    {
        // Closing the connection drops the session-level advisory lock automatically.
        lockConnection.Close();
        lockConnection.Dispose();
    }
    catch
    {
        // Best effort — the lock is released when the backend session ends regardless.
    }
});

await host.RunAsync();

/// <summary>
/// Hosts the advisory-lock key. Declared as a partial class so the Engine.Tests project can
/// reference <c>Program</c> if needed.
/// </summary>
public partial class Program
{
    /// <summary>RentalCommand Engine advisory lock key (distinct from EdiPlatform's).</summary>
    internal const int AdvisoryLockKey = 59484;
}
