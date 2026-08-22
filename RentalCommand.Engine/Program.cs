using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RentalCommand.Api.Extensions;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Sms;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine;
using RentalCommand.Engine.Extensions;
using RentalCommand.Engine.HealthChecks;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;

// NOTE: EnableLegacyTimestampBehavior is intentionally NOT set here.
// Every DB-written DateTime in the Engine is either DateTime.UtcNow-derived or
// explicitly constructed with DateTimeKind.Utc (see RentChargeService). This matches
// the API project which also runs without the legacy switch.
// QuestPDF Community license (free for small businesses / OSS) — required before any PDF is generated.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = Host.CreateApplicationBuilder(args);
var simulationEnabled = SimulationGate.IsEnabled(builder.Configuration, builder.Environment);
var commandBridgeOnly = builder.Configuration.GetValue<bool>("Simulation:CommandBridgeOnly");

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
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MigratorConnection")))
{
    throw new InvalidOperationException(
        "ConnectionStrings:MigratorConnection must not be available to the long-running Engine process.");
}

RentalCommand.Data.Security.RuntimeDatabaseRoleProvisioner.ValidateRuntimeConnectionString(
    connectionString,
    RentalCommand.Data.Security.DatabaseRuntimeIdentity.EngineRole,
    allowDevelopmentDefault: builder.Environment.IsDevelopment());

// Atomic commands attribute Engine mutations to the system actor inside the canonical audit scope.
builder.Services.AddScoped<RentalCommand.Core.Interfaces.ICurrentActor,
    RentalCommand.Data.Auditing.SystemCurrentActor>();

// The Engine's direct restricted database identity is the sole cross-workspace authority. It never
// receives or sets a mutable administrator/bypass flag.
builder.Services.AddSingleton<RentalCommand.Engine.Data.EngineRlsInterceptor>();
builder.Services.AddAtomicPersistenceKernel();
builder.Services.AddScoped<RentalCommand.Engine.Writes.IJobStepWriteExecutor,
    RentalCommand.Engine.Writes.JobStepWriteExecutor>();
// Shared Api-namespace services hosted in the Engine (notifications, e-sign, notices,
// conversations, LLM credentials) execute their writes through the request executor.
builder.Services.AddScoped<RentalCommand.Api.Writes.IRequestWriteExecutor,
    RentalCommand.Api.Writes.RequestWriteExecutor>();
builder.Services.AddGeneratedInfrastructureStores();
builder.Services.AddPendingFileUploadStore();

builder.Services.AddDbContext<RentalCommandDbContext>((sp, options) =>
    options.UseNpgsql(connectionString)
        .UseAtomicPersistenceKernel(sp)
        .AddInterceptors(sp.GetRequiredService<RentalCommand.Engine.Data.EngineRlsInterceptor>()));

// --- Master simulation clock (TSK-615) ---
// Same ambient TimeProvider + IAppTimeZoneProvider registration as the API so both processes agree on
// "now". Production / flag-off binds TimeProvider.System (real clock). The Engine has no auth handlers,
// so it does not pin any framework auth clock.
builder.Services.AddSimulationClock(builder.Configuration, builder.Environment);

// DB-outbox publisher + notification channel (SignalWire/Twilio SMS; SMTP or SendGrid email,
// config-selected; logs when unconfigured).
builder.Services.AddScoped<IMessagePublisher, RentalCommand.Data.Outbox.OutboxMessagePublisher>();
builder.Services.AddScoped<RentalCommand.Data.Outbox.IOutboxClaimStore, RentalCommand.Data.Outbox.OutboxClaimStore>();
builder.Services.AddScoped<RentalCommand.Data.Scanning.IScanProcessingClaimStore,
    RentalCommand.Data.Scanning.ScanProcessingClaimStore>();
builder.Services.AddScoped<
    RentalCommand.Core.Scanning.IScanConfirmationTargetWriter,
    RentalCommand.Data.Scanning.ProductionScanConfirmationTargetWriter>();
builder.Services.AddScoped<RentalCommand.Data.Simulation.ISimWorkerCommandClaimStore,
    RentalCommand.Data.Simulation.SimWorkerCommandClaimStore>();
builder.Services.AddScoped<RentalCommand.Data.Accounting.IAccountingConnectionClaimStore,
    RentalCommand.Data.Accounting.AccountingConnectionClaimStore>();
builder.Services.AddScoped<AccountingPostingService>();
builder.Services.AddScoped<ChartOfAccountsSeedService>();
// SMTP sender (MailKit) the channel delegates to when Notifications:Email:Transport == "Smtp".
builder.Services.AddSingleton<ISmtpEmailSender, SmtpEmailSender>();
// Pluggable SMS providers (BYO per-portfolio; platform-env fallback). Shared registration with the
// API; the dispatcher resolves the portfolio's chosen provider then dispatches to the matching impl.
builder.Services.AddSmsProviders();
builder.Services.AddNotificationDeliveryChannel(builder.Configuration);
// Push (FCM HTTP v1 via FirebaseAdmin). Singleton: the FirebaseApp is a process-global. Fail-soft
// when no Push:* credential is configured — the outbox worker still marks push messages sent.
builder.Services.Configure<PushConfig>(builder.Configuration.GetSection(PushConfig.SectionName));
builder.Services.AddSingleton<IPushSender, FcmPushSender>();
// Scan pipeline: config, LLM provider, file storage, no-op data-update, worker.
builder.Services.Configure<AssistantConfig>(builder.Configuration.GetSection(AssistantConfig.SectionName));
builder.Services.Configure<UploadSettings>(builder.Configuration.GetSection(UploadSettings.SectionName));
builder.Services.Configure<NotificationsConfig>(builder.Configuration.GetSection(NotificationsConfig.SectionName));
var llmProvider = builder.Configuration.GetValue<string>("Assistant:Provider") ?? "openai";
builder.Services.AddSingleton<IImageTextExtractor, TesseractImageTextExtractor>();
builder.Services.AddHttpClient<OpenAiLlmProvider>(c =>
{
    c.BaseAddress = new Uri("https://api.openai.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient<AnthropicLlmProvider>(c =>
{
    c.BaseAddress = new Uri("https://api.anthropic.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddScoped<IWorkspaceLlmExtractionProvider>(sp =>
    sp.GetRequiredService<OpenAiLlmProvider>());
builder.Services.AddScoped<IWorkspaceLlmExtractionProvider>(sp =>
    sp.GetRequiredService<AnthropicLlmProvider>());
builder.Services.AddScoped<IWorkspaceAuthorizationEvaluator, WorkspaceAuthorizationEvaluator>();
builder.Services.AddScoped<IWorkspaceLlmCredentialService, WorkspaceLlmCredentialService>();
builder.Services.AddScoped<IWorkspaceLlmCredentialResolver>(sp =>
    sp.GetRequiredService<IWorkspaceLlmCredentialService>());
builder.Services.AddScoped<ILlmUsageEvidenceRecorder>(sp =>
    sp.GetRequiredService<IWorkspaceLlmCredentialService>());
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
builder.Services.AddScoped<PendingFileUploadCleanupService>();
// Native e-sign execution is shared with the API. Signatures are committed before PDF/blob work;
// this service lets the Engine finish any durable ExecutionPending request after a transient failure.
builder.Services.AddSingleton<RentalCommand.Api.Services.Esign.IExecutedLeasePdfGenerator,
    RentalCommand.Api.Services.Esign.ExecutedLeasePdfGenerator>();
builder.Services.AddScoped<RentalCommand.Api.Services.Esign.INativeEsignExecutionService,
    RentalCommand.Api.Services.Esign.NativeEsignExecutionService>();
builder.Services.AddScoped<RentalCommand.Data.Esign.INativeEsignExecutionClaimStore,
    RentalCommand.Data.Esign.NativeEsignExecutionClaimStore>();
builder.Services.AddScoped<NativeEsignReconciliationService>();
builder.Services.AddScoped<RentalCommand.Data.Payments.IProviderInboxClaimStore,
    RentalCommand.Data.Payments.ProviderInboxClaimStore>();
builder.Services.AddScoped<ProviderInboxReconciliationService>();
builder.Services.AddScoped<RentalCommand.Data.Automation.IScheduledAutomationClaimStore,
    RentalCommand.Data.Automation.ScheduledAutomationClaimStore>();
// Realtime backplane (TSK-624): the Engine can't reach the API's in-memory SignalR hub, so it
// publishes each entity change as a Postgres NOTIFY on its own pooled connection. The API-hosted
// EntityChangeListener LISTENs and re-broadcasts to the hub. Shared NpgsqlDataSource so publishes
// reuse a dedicated pool rather than opening a raw connection per event.
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddSingleton<IDataUpdateService, NotifyDataUpdateService>();
builder.Services.AddSingleton<RentalCommand.Api.Services.RealtimeInvalidationQueue>();
builder.Services.AddSingleton<IRealtimeInvalidationQueue>(sp =>
    sp.GetRequiredService<RentalCommand.Api.Services.RealtimeInvalidationQueue>());

// Phase 4 automation services (gated by Notifications flags; financial ones default OFF).
builder.Services.AddScoped<IRentChargeService, RentChargeService>();
builder.Services.AddScoped<IDebtServiceService, DebtServiceService>();
builder.Services.AddScoped<IRecurringExpenseGenerationService, RecurringExpenseGenerationService>();
builder.Services.AddScoped<
    IRecurringTenantChargeGenerationService,
    RecurringTenantChargeGenerationService>();
builder.Services.AddScoped<IRecurringMaintenanceService, RecurringMaintenanceService>();
// Online-payments autopay charging (gated: no-op unless Stripe is configured).
builder.Services.Configure<StripeConfig>(builder.Configuration.GetSection(StripeConfig.SectionName));
builder.Services.AddOptions<InteractivePaymentReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(InteractivePaymentReconciliationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<InteractivePaymentReconciliationOptions>,
    InteractivePaymentReconciliationOptionsValidator>();
builder.Services.AddScoped<RentalCommand.Api.Services.Payments.IInteractivePaymentProviderClient,
    RentalCommand.Api.Services.Payments.StripeInteractivePaymentProviderClient>();
builder.Services.AddScoped<IAutopayChargeService, AutopayChargeService>();
builder.Services.AddScoped<IAutopayProviderClient, StripeAutopayProviderClient>();
builder.Services.AddScoped<InteractivePaymentReconciliationService>();
builder.Services.AddScoped<ILateFeeService, LateFeeService>();
builder.Services.AddScoped<ITenantNoticeCandidateGenerationService, TenantNoticeCandidateGenerationService>();
builder.Services.AddScoped<ITenantNoticeDraftSetStore, TenantNoticeDraftSetStore>();
builder.Services.AddScoped<IDailyBriefingService, DailyBriefingService>();
builder.Services.AddScoped<IDailyBriefingDeliveryService, DailyBriefingDeliveryService>();
builder.Services.AddScoped<IMessagingProviderSettingsResolver, MessagingProviderSettingsResolver>();

// Tenant-notice work is drafted by one PostgreSQL set command, then Auto policies use the same
// canonical approval/outbox command as the API. The Engine does not use the manual draft façade.
builder.Services.AddScoped<IFairHousingReviewService, FairHousingReviewService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<INotificationFoundationService, NotificationFoundationService>();
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

// Command-bridge-only mode is a dev/simulation safety surface: it retains the clock refresher
// registered by AddSimulationClock, the command bridge, its status reporting, and the single-instance
// advisory-lock watcher. Every autonomous worker and dispatcher remains unstarted.
builder.Services.AddEngineHostedServices(simulationEnabled, commandBridgeOnly);

// Health check over the heartbeat table. The Engine has no HTTP port, so there is no
// endpoint to scrape — the registration keeps parity with EdiPlatform and lets the check
// be reused/inspected; the watchdog WARNING logs are the primary "is it alive" signal.
builder.Services.AddHealthChecks()
    .AddCheck<EngineWorkerHealthCheck>("workers");

var host = builder.Build();

// --- Single-instance safety: PostgreSQL advisory lock ---
// The dedicated non-pooled connection already uses the direct restricted Engine login. A new Engine
// waits for the prior holder instead of retaining owner authority merely to terminate it.
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
        await RentalCommand.Data.Security.DatabaseRuntimeIdentity.ValidateOpenedConnectionAsync(
            lockConnection,
            RentalCommand.Data.Security.DatabaseRuntimeIdentity.EngineRole);
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

var acquiredImmediately = false;
await using (var tryLockCmd = lockConnection.CreateCommand())
{
    tryLockCmd.CommandText = $"SELECT pg_try_advisory_lock({Program.AdvisoryLockKey})";
    acquiredImmediately = (bool?)await tryLockCmd.ExecuteScalarAsync() == true;
}

if (!acquiredImmediately)
{
    Program.LockContested = true;
    logger.LogWarning(
        "Another Engine holds advisory lock {LockKey}; waiting for it to stop.",
        Program.AdvisoryLockKey);
    await using var lockCmd = lockConnection.CreateCommand();
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

    /// <summary>True if this instance encountered and waited for a prior lock holder.</summary>
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
