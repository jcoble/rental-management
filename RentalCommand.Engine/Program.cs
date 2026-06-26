using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentalCommand.Api.Extensions;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.HealthChecks;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;

// NOTE: EnableLegacyTimestampBehavior is intentionally NOT set here.
// Every DB-written DateTime in the Engine is either DateTime.UtcNow-derived or
// explicitly constructed with DateTimeKind.Utc (see RentChargeService). This matches
// the API project which also runs without the legacy switch.
var builder = Host.CreateApplicationBuilder(args);

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("RentalCommand");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Missing connection string 'DefaultConnection'. Set it in appsettings.json or via configuration.");

// Unified audit trail: the Engine has no HttpContext, so it attributes audit rows to "system".
// The scoped interceptor is resolved from the same scope as the DbContext (the (sp, options)
// overload) and auto-records IAuditable CRUD that workers perform.
builder.Services.AddScoped<RentalCommand.Core.Interfaces.ICurrentActor,
    RentalCommand.Data.Auditing.SystemCurrentActor>();
builder.Services.AddScoped<RentalCommand.Core.Interfaces.IAuditScope,
    RentalCommand.Data.Auditing.AuditScope>();
builder.Services.AddScoped<RentalCommand.Data.Auditing.AuditSaveChangesInterceptor>();

// Row-Level Security backstop (audit M-1): the Engine operates across all portfolios, so its RLS
// interceptor always sets app.is_admin = true (no HTTP context, no single portfolio). It sets the
// same session GUCs the tenant_isolation policies read, mirroring EdiPlatform's Engine.
builder.Services.AddSingleton<RentalCommand.Engine.Data.EngineRlsInterceptor>();

builder.Services.AddDbContext<RentalCommandDbContext>((sp, options) =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(
            sp.GetRequiredService<RentalCommand.Data.Auditing.AuditSaveChangesInterceptor>(),
            sp.GetRequiredService<RentalCommand.Engine.Data.EngineRlsInterceptor>()));

// DB-outbox publisher + notification channel (SignalWire/Twilio SMS; SMTP/Zoho or SendGrid email,
// config-selected; logs when unconfigured).
builder.Services.AddScoped<IMessagePublisher, OutboxMessagePublisher>();
// SMTP sender (MailKit) the channel delegates to when Notifications:Email:Transport == "Smtp".
builder.Services.AddSingleton<ISmtpEmailSender, SmtpEmailSender>();
// Pluggable SMS providers (BYO per-portfolio; platform-env fallback). Shared registration with the
// API; the dispatcher resolves the portfolio's chosen provider then dispatches to the matching impl.
builder.Services.AddSmsProviders();
builder.Services.AddHttpClient<INotificationChannel, RoutingNotificationChannel>();
// Push (FCM HTTP v1 via FirebaseAdmin). Singleton: the FirebaseApp is a process-global. Fail-soft
// when no Push:* credential is configured — the outbox worker still marks push messages sent.
builder.Services.Configure<PushConfig>(builder.Configuration.GetSection(PushConfig.SectionName));
builder.Services.AddSingleton<IPushSender, FcmPushSender>();
// Sandbox guard: the outbox worker uses it to HARD-suppress any send scoped to a sandbox portfolio.
builder.Services.AddScoped<ISandboxGuard, SandboxGuard>();

// Scan pipeline: config, LLM provider, file storage, no-op data-update, worker.
builder.Services.Configure<AssistantConfig>(builder.Configuration.GetSection(AssistantConfig.SectionName));
builder.Services.Configure<UploadSettings>(builder.Configuration.GetSection(UploadSettings.SectionName));
builder.Services.Configure<NotificationsConfig>(builder.Configuration.GetSection(NotificationsConfig.SectionName));
var llmProvider = builder.Configuration.GetValue<string>("Assistant:Provider") ?? "openai";
builder.Services.AddSingleton<IImageTextExtractor, TesseractImageTextExtractor>();
if (string.Equals(llmProvider, "anthropic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<ILlmProvider, AnthropicLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.anthropic.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else if (string.Equals(llmProvider, "gemini", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<ILlmProvider, GeminiLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else if (string.Equals(llmProvider, "claude-cli", StringComparison.OrdinalIgnoreCase))
{
    // DEV-ONLY: shell out to the locally-installed Claude Code CLI (`claude -p`) so extraction can
    // use the developer's own Claude subscription at no per-token cost. No HttpClient — it invokes
    // the binary. Unsuitable for production; see ClaudeCliLlmProvider.
    builder.Services.AddSingleton<ILlmProvider, ClaudeCliLlmProvider>();
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
builder.Services.AddScoped<IDebtServiceService, DebtServiceService>();
builder.Services.AddScoped<IRecurringExpenseGenerationService, RecurringExpenseGenerationService>();
builder.Services.AddScoped<IRecurringMaintenanceService, RecurringMaintenanceService>();
// Online-payments autopay charging (gated: no-op unless Stripe is configured).
builder.Services.Configure<StripeConfig>(builder.Configuration.GetSection(StripeConfig.SectionName));
builder.Services.AddScoped<IAutopayChargeService, AutopayChargeService>();
builder.Services.AddScoped<ILateFeeService, LateFeeService>();
builder.Services.AddScoped<ILeaseExpiryReminderService, LeaseExpiryReminderService>();
builder.Services.AddScoped<IDailyBriefingService, DailyBriefingService>();
builder.Services.AddScoped<IDailyBriefingDeliveryService, DailyBriefingDeliveryService>();
builder.Services.AddScoped<INotificationSettingsService, NotificationSettingsService>();

// Lease Lifecycle Autopilot: proactively draft renewal/late/move-out notices for one-tap approval.
// Reuses the Api's NoticeDraftService (LLM copy + de-dup idempotency) — the same code the manual
// "Generate" button runs. ConversationService is its constructor dependency (only used by the
// approve path, which the worker never invokes; the Engine already provides IDataUpdateService).
// ConversationService's own constructor needs IFairHousingReviewService, so the Engine MUST register
// it too. Development host builds validate the whole DI graph on Build(), so a missing registration
// here crashes the entire Engine on boot — killing every worker (outbox/scan dispatch, debt service,
// late-fee sweep, notices), not just the notice path. FairHousingReviewService's only dependency is
// ILlmProvider, which the Engine already registers for scan extraction, so this adds no further graph.
builder.Services.AddScoped<IFairHousingReviewService, FairHousingReviewService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<INoticeDraftService, NoticeDraftService>();
builder.Services.AddScoped<INoticeDraftGenerationService, NoticeDraftGenerationService>();

// Accounting-integration pull worker dependencies (provider-agnostic). The Engine does not call
// AddDomainServices(), so the import engine + provider resolver/settings + the provider set are
// registered explicitly here; AddAccountingProviders() (shared with the API) also binds QuickBooks
// creds + registers each IAccountingProvider via AddHttpClient. IDataProtection is configured above
// with the same SetApplicationName/keys path as the API, so tokens encrypted by the API decrypt here.
builder.Services.AddScoped<RentalCommand.Api.Services.Domain.AccountingProviderResolver>();
builder.Services.AddScoped<RentalCommand.Api.Services.Domain.AccountingAppSettingsResolver>();
builder.Services.AddScoped<RentalCommand.Api.Services.Domain.AccountingImportService>();
// Shared token refresh+persist service — used by the import path's refresh-on-401 AND the token-refresh worker.
builder.Services.AddScoped<RentalCommand.Api.Services.Domain.AccountingTokenService>();
builder.Services.AddAccountingProviders();

// Engine resilience — persists worker heartbeats; consumed by the watchdog + health check.
// Scoped (it opens its own scope per call to isolate DB access).
builder.Services.AddScoped<EngineStatusReporter>();

// Workers (each is its own BackgroundService).
builder.Services.AddHostedService<OutboxDispatchWorker>();
builder.Services.AddHostedService<ScanProcessingWorker>();
builder.Services.AddHostedService<RentChargeWorker>();
builder.Services.AddHostedService<DebtServiceWorker>();
builder.Services.AddHostedService<RecurringExpenseWorker>();
builder.Services.AddHostedService<AutopayChargeWorker>();
builder.Services.AddHostedService<RecurringMaintenanceWorker>();
builder.Services.AddHostedService<LateFeeWorker>();
builder.Services.AddHostedService<LeaseExpiryReminderWorker>();
builder.Services.AddHostedService<DailyBriefingDeliveryWorker>();
builder.Services.AddHostedService<NoticeDraftWorker>();
// Continuous accounting pull: imports each Connected + PullEnabled connection's deltas into the domain.
builder.Services.AddHostedService<AccountingPullWorker>();
// Proactive token refresh: rotates access tokens before expiry so the continuous pull never dies on a stale token.
builder.Services.AddHostedService<AccountingTokenRefreshWorker>();

// Watcher: monitors the advisory lock connection; triggers graceful shutdown if a newer Engine takes over.
builder.Services.AddHostedService<AdvisoryLockWatcherService>();

// Watchdog: monitors worker heartbeats; warns (and ultimately restarts) if any worker is stuck.
builder.Services.AddHostedService<WorkerWatchdogService>();

// Health check over the heartbeat table. The Engine has no HTTP port, so there is no
// endpoint to scrape — the registration keeps parity with EdiPlatform and lets the check
// be reused/inspected; the watchdog WARNING logs are the primary "is it alive" signal.
builder.Services.AddHealthChecks()
    .AddCheck<EngineWorkerHealthCheck>("workers");

var host = builder.Build();

// --- Self-migrate ---
// The Engine applies EF Core migrations itself so it no longer depends on the API having
// created the schema. Both the API and Engine self-migrate on startup, so the migration runs
// under a shared PostgreSQL advisory lock (DatabaseMigrator) — concurrent MigrateAsync calls
// would otherwise race on a fresh batch and crash one process. Done BEFORE acquiring the worker
// advisory lock so the schema (incl. the heartbeat table the watchdog reads) exists first.
{
    var migrateLogger = host.Services.GetRequiredService<ILogger<Program>>();
    const int maxMigrateAttempts = 30;
    for (var attempt = 1; attempt <= maxMigrateAttempts; attempt++)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            // Advisory-locked so the Engine and API don't apply a fresh migration batch concurrently.
            await DatabaseMigrator.MigrateWithLockAsync(db);
            migrateLogger.LogInformation("Engine applied database migrations (or none pending).");
            break;
        }
        catch (Exception ex) when (attempt < maxMigrateAttempts)
        {
            migrateLogger.LogWarning(
                "Database not ready for migration yet (attempt {Attempt}/{Max}): {Message}. Retrying in 2s…",
                attempt, maxMigrateAttempts, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

// --- Single-instance safety: PostgreSQL advisory lock TAKEOVER ---
// A new Engine instance KILLS any existing holder and wins the lock, so a restart/redeploy
// always succeeds rather than getting stuck behind a zombie process. The dedicated, non-pooled
// connection is held open for the host's lifetime; AdvisoryLockWatcherService monitors it and
// triggers graceful shutdown if a still-newer Engine later terminates it.
var logger = host.Services.GetRequiredService<ILogger<Program>>();

var lockConnString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString();

// Open the dedicated lock connection. Keep a DB-not-ready retry on the FIRST open so a
// not-yet-up database still waits instead of crashing the process.
NpgsqlConnection? lockConnection = null;
const int maxDbAttempts = 30;
for (var attempt = 1; attempt <= maxDbAttempts; attempt++)
{
    try
    {
        lockConnection = new NpgsqlConnection(lockConnString);
        await lockConnection.OpenAsync();
        break;
    }
    catch (Exception ex) when (attempt < maxDbAttempts)
    {
        if (lockConnection is not null) { await lockConnection.DisposeAsync(); lockConnection = null; }
        logger.LogWarning(
            "Database not reachable yet (attempt {Attempt}/{Max}): {Message}. Retrying in 2s…",
            attempt, maxDbAttempts, ex.Message);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}
if (lockConnection is null)
{
    logger.LogError("Database never became reachable after {Max} attempts. Exiting.", maxDbAttempts);
    return;
}

// If another Engine currently holds the advisory lock, terminate its backend so we can take over.
await using (var checkCmd = lockConnection.CreateCommand())
{
    checkCmd.CommandText =
        $"SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND classid = 0 " +
        $"AND objid = {Program.AdvisoryLockKey} AND granted = true";
    var existingPid = await checkCmd.ExecuteScalarAsync();
    if (existingPid != null)
    {
        logger.LogWarning(
            "Another Engine instance detected (DB PID {Pid}). Terminating it to take over the advisory lock…",
            existingPid);
        await using var killCmd = lockConnection.CreateCommand();
        killCmd.CommandText = $"SELECT pg_terminate_backend({existingPid})";
        await killCmd.ExecuteScalarAsync();
        await Task.Delay(1000); // give the old backend time to die and release the lock
        Program.LockContested = true;
    }
}

// Acquire the advisory lock (blocking — succeeds now that any prior holder is gone).
await using (var lockCmd = lockConnection.CreateCommand())
{
    lockCmd.CommandText = $"SELECT pg_advisory_lock({Program.AdvisoryLockKey})";
    await lockCmd.ExecuteScalarAsync();
}

// Publish the connection so AdvisoryLockWatcherService can monitor it.
Program.AdvisoryLockConnection = lockConnection;
Program.AdvisoryLockHeld = true;

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
    Program.AdvisoryLockConnection = null;
    Program.AdvisoryLockHeld = false;
});

// --- Crash-recovery: re-arm scans stranded mid-extraction by a previous crash ---
// ScanProcessingWorker atomically claims a draft Pending → Processing before calling the
// LLM. If a prior Engine crashed / was killed (incl. the advisory-lock takeover above) after
// that claim but before reaching a terminal state, the draft is left in "Processing" — a state
// the worker never polls, so it would be invisible and unconfirmable forever. We run this only
// AFTER the advisory lock is held, so single-instance is guaranteed: no other Engine can
// legitimately own a "Processing" row, making every such row a genuine crash victim safe to
// reset to "Pending" for a fresh attempt. Only "Processing" is touched — "Confirming" is the
// API's mid-confirm claim and must be left alone.
try
{
    using var recoveryScope = host.Services.CreateScope();
    var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
    var reset = await recoveryDb.Database.ExecuteSqlRawAsync(
        "UPDATE \"ScanDrafts\" SET \"Status\" = 'Pending', \"ReviewedAt\" = NULL WHERE \"Status\" = 'Processing'");
    if (reset > 0)
    {
        logger.LogWarning(
            "Crash-recovery: reset {Count} scan draft(s) stranded in 'Processing' back to 'Pending' for reprocessing.",
            reset);
    }
    else
    {
        logger.LogInformation("Crash-recovery: no scan drafts were stranded in 'Processing'.");
    }
}
catch (Exception ex)
{
    // Non-fatal: the worker's in-process failure handling still drives live timeouts/exceptions
    // to 'Failed'. Don't block startup if this one-shot sweep fails (e.g. transient DB hiccup).
    logger.LogError(ex, "Crash-recovery sweep for stranded 'Processing' scan drafts failed; continuing startup.");
}

await host.RunAsync();

/// <summary>
/// Hosts the advisory-lock key and the shared lock state read by the resilience services.
/// Declared as a partial class so the Engine.Tests project can reference <c>Program</c> if needed.
/// </summary>
public partial class Program
{
    /// <summary>RentalCommand Engine advisory lock key (distinct from EdiPlatform's).</summary>
    internal const int AdvisoryLockKey = 59484;

    private static volatile bool _advisoryLockHeld;
    private static volatile bool _lockContested;
    private static DbConnection? _advisoryLockConnection;

    /// <summary>True once this instance holds the advisory lock; false after shutdown/takeover.</summary>
    internal static bool AdvisoryLockHeld
    {
        get => _advisoryLockHeld;
        set => _advisoryLockHeld = value;
    }

    /// <summary>True if this instance had to terminate a prior holder to take over.</summary>
    internal static bool LockContested
    {
        get => _lockContested;
        set => _lockContested = value;
    }

    /// <summary>The dedicated connection holding the advisory lock; monitored by the watcher.</summary>
    internal static DbConnection? AdvisoryLockConnection
    {
        get => Volatile.Read(ref _advisoryLockConnection);
        set => Volatile.Write(ref _advisoryLockConnection, value);
    }
}
