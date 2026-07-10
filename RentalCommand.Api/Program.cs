using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Extensions;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

// QuestPDF Community license (free for small businesses / OSS) — required before any PDF is generated.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("RentalCommand");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Raise Kestrel's hard body-size limit (default 30 MB) so large scan uploads
// (up to 50 MB per UploadSettings) are not silently rejected with 413 before
// the controller runs. 64 MB gives headroom over the 50 MB app-level gate for
// multipart framing overhead. The UploadSettings.MaxFileSizeBytes check in
// FileUploadValidator remains the real policy gate.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64_000_000);

// Raise the ASP.NET multipart body limit to match.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 64_000_000);

// --- Configuration binding ---
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));
builder.Services.Configure<SeedSettings>(builder.Configuration.GetSection(SeedSettings.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.PlatformAdminOptions>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.PlatformAdminOptions.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.AssistantConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.AssistantConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.UploadSettings>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.UploadSettings.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.GoogleAuthOptions>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.GoogleAuthOptions.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.StripeConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.StripeConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.ReportsConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.ReportsConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.EsignConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.EsignConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.ScreeningConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.ScreeningConfig.SectionName));
builder.Services.Configure<RentalCommand.Core.Configuration.QuickBooksOptions>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.QuickBooksOptions.SectionName));
var llmProvider = builder.Configuration.GetValue<string>("Assistant:Provider") ?? "openai";
builder.Services.AddSingleton<RentalCommand.Api.Scanning.IImageTextExtractor,
    RentalCommand.Api.Scanning.TesseractImageTextExtractor>();
if (string.Equals(llmProvider, "anthropic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.AnthropicLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.anthropic.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else if (string.Equals(llmProvider, "gemini", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.GeminiLlmProvider>(c =>
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
    builder.Services.AddSingleton<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.ClaudeCliLlmProvider>();
}
else // default: openai
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.OpenAiLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.openai.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
builder.Services.AddScoped<RentalCommand.Core.Interfaces.IFileStorage, RentalCommand.Api.Scanning.DiskFileStorage>();
builder.Services.AddHttpClient<IAudioTranscriptionService, OpenAiAudioTranscriptionService>(c =>
{
    c.BaseAddress = new Uri("https://api.openai.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});

// Google Places (New) address autocomplete. Key from the "GooglePlaces" config section
// (user-secrets in dev, container env in prod). Disabled gracefully when no key is set.
builder.Services.Configure<RentalCommand.Core.Configuration.GooglePlacesConfig>(
    builder.Configuration.GetSection(RentalCommand.Core.Configuration.GooglePlacesConfig.SectionName));
builder.Services.AddHttpClient<RentalCommand.Api.Services.Places.GooglePlacesService>(c =>
{
    c.BaseAddress = new Uri("https://places.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
});

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

// Fail fast on an unconfigured signing key. HS256 needs >= 256 bits (32 chars), and the
// committed placeholders must never be used outside local dev — otherwise anyone could forge tokens.
// Placeholder values stay usable in Development so local dev needs no secret.
JwtSecretGuard.Validate(jwtSettings.SecretKey, builder.Environment.IsDevelopment());

// --- Database ---
// The scoped AuditSaveChangesInterceptor is resolved from the same scope as the DbContext (the
// (sp, options) overload), so it can read the per-request ICurrentActor / IAuditScope.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddHttpContextAccessor();

// Row-Level Security backstop (audit M-1): a connection interceptor sets the per-request
// app.current_portfolio_id / app.is_admin session GUCs that the tenant_isolation policies read, so
// tenant isolation is enforced at the DB layer in addition to the app-layer PortfolioId filters.
// Registered alongside the audit interceptor on the same DbContext.
builder.Services.AddSingleton<RentalCommand.Api.Data.IRlsActorModeAccessor,
    RentalCommand.Api.Data.RlsActorModeAccessor>();
builder.Services.AddSingleton<RentalCommand.Api.Data.RlsConnectionInterceptor>();
builder.Services.AddDbContext<RentalCommandDbContext>((sp, options) =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(
            sp.GetRequiredService<RentalCommand.Data.Auditing.AuditSaveChangesInterceptor>(),
            sp.GetRequiredService<RentalCommand.Api.Data.RlsConnectionInterceptor>()));

// --- ASP.NET Identity (int keys) ---
builder.Services.AddIdentity<ApplicationUser, IdentityRole<int>>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole<int>>()
    .AddEntityFrameworkStores<RentalCommandDbContext>()
    .AddDefaultTokenProviders();

// --- Authentication: JWT bearer (default) + API key scheme ---
// Called after AddIdentity so the JWT bearer scheme (not Identity's cookie) is the default.
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            // A small skew tolerance (NOT the 5-min default, NOT zero): zero meant a token expiring
            // even a second early by the server clock was a hard 401 — turning any client/server clock
            // drift, or a request landing right on the 15-min boundary (e.g. a slow scan upload), into
            // an intermittent "valid token rejected" logout. 2 min absorbs real-world drift safely.
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        options.Events = new JwtBearerEvents
        {
            // Allow SignalR hubs (added later) to pass the token via query string.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/v1/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                if (context.Exception is SecurityTokenExpiredException)
                {
                    context.Response.Headers.Append("X-Token-Expired", "true");
                }
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.AuthenticationScheme, _ => { });

// Platform super-admin allowlist (F6 / TSK-212): operator endpoints (Engine Health) are gated
// by a config email list, not a role. Fails closed when the list is empty. The policy logic and
// its allowlist parsing live in RentalCommand.Api.Auth.PlatformAdminPolicy so the gate and its
// security test share one source of truth.
var platformAdminAllowlist = RentalCommand.Api.Auth.PlatformAdminPolicy.BuildAllowlist(
    builder.Configuration
        .GetSection(RentalCommand.Core.Configuration.PlatformAdminOptions.SectionName)
        .Get<RentalCommand.Core.Configuration.PlatformAdminOptions>());

builder.Services.AddAuthorization(options =>
{
    RentalCommand.Api.Auth.PlatformAdminPolicy.Register(options, platformAdminAllowlist);
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, CapabilityAuthorizationPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, CapabilityAuthorizationHandler>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IActiveAccessContextResolver,
    ActiveAccessContextResolver>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IWorkspaceAuthorizationEvaluator,
    WorkspaceAuthorizationEvaluator>();
builder.Services.AddScoped<RentalCommand.Core.Authorization.IMembershipAssignmentScopeValidator,
    MembershipAssignmentScopeValidator>();

// --- Auth services ---
builder.Services.AddHttpClient("GoogleAuth");
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IUserMigrationService, UserMigrationService>();
builder.Services.AddScoped<IAuthEmailSender, OutboxAuthEmailSender>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
// On-demand tenant portal provisioning: shared by the startup seeder and the staff "grant portal
// access" endpoint (TenantController) so a tenant added after boot can be given a login without a restart.
builder.Services.AddScoped<ITenantPortalProvisioningService, TenantPortalProvisioningService>();
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddScoped<DemoDataSeeder>();

// --- CORS (restrict to the web app origin) ---
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "https://localhost:5667" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebApp", policy =>
    {
        policy.WithOrigins(corsOrigins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

// Serialize/accept enums as their string names (e.g. "InProgress", "Normal") rather than
// integers, matching what the SvelteKit client sends and renders. Without this the API
// binds enums as numbers and 400s on the client's string enum values.
// Dev-only simulation controllers ([SimulationOnly]) have all their routes stripped at startup when
// simulation is inactive (production / flag off), so they simply do not exist there.
var simulationEnabled = SimulationGate.IsEnabled(builder.Configuration, builder.Environment);
builder.Services.AddControllers(options =>
    {
        options.Conventions.Add(new SimulationOnlyConvention(simulationEnabled));
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddHealthChecks();

// Global exception handling: maps domain-rule violations to clean 400/409 ProblemDetails and prevents
// raw persistence faults (DbUpdate/Postgres constraint violations) from leaking SQL/type/stack/constraint
// names to the client (full detail is still logged server-side). See GlobalExceptionHandler.
builder.Services.AddExceptionHandler<RentalCommand.Api.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// --- SignalR (realtime hubs) + DataUpdateService broadcaster ---
// Mirror the REST enum-as-string convention on realtime payloads too.
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddScoped<IDataUpdateService, DataUpdateService>();

// Realtime backplane bridge (TSK-624): LISTENs on the Postgres channel the Engine NOTIFYs, and
// re-broadcasts each cross-process entity change onto the SignalR hub via DataUpdateService. Without
// this, Engine-originated automation (rent charges, late fees, notices, scan completion, …) never
// pushes live — the hub is in-memory per-process and the Engine runs in a separate process.
builder.Services.AddHostedService<EntityChangeListener>();

// --- Domain feature services (per-entity scoped CRUD) ---
builder.Services.AddDomainServices();

// --- Outbox message publisher (API-side: enqueues rows; Engine dispatches them) ---
builder.Services.AddScoped<IMessagePublisher, RentalCommand.Api.Services.OutboxMessagePublisher>();

// --- Scheduled owner statement worker (default OFF; set Reports:EmailOwnerStatementsMonthly=true to enable) ---
builder.Services.AddHostedService<RentalCommand.Api.Services.ScheduledOwnerStatementWorker>();

// --- Stripe payment services (gated — no-ops when Stripe keys are absent) ---
builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();

// --- E-sign provider. Out of the box this is the NATIVE, ESIGN/UETA-compliant provider (always
// available — no third-party key needed). When a Dropbox Sign key IS configured the gated DropboxSign
// provider takes over instead (unchanged). So e-sign works natively by default and can be swapped to a
// hosted provider purely by setting Esign:ApiKey.
var esignConfig = builder.Configuration.GetSection(RentalCommand.Core.Configuration.EsignConfig.SectionName)
    .Get<RentalCommand.Core.Configuration.EsignConfig>() ?? new RentalCommand.Core.Configuration.EsignConfig();
if (esignConfig.Enabled)
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.IEsignProvider,
        RentalCommand.Api.Services.Esign.DropboxSignEsignProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.hellosign.com/v3/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else
{
    builder.Services.AddScoped<RentalCommand.Core.Interfaces.IEsignProvider,
        RentalCommand.Api.Services.Esign.NativeEsignProvider>();
}

// --- Tenant-screening provider (gated — like Stripe/LLM/e-sign, the real TransUnion call is only wired
// when a key is set; otherwise a no-op provider returns a clear "not configured" result and never
// contacts a third party. FCRA: screening is also never run without recorded applicant consent). ---
var screeningConfig = builder.Configuration.GetSection(RentalCommand.Core.Configuration.ScreeningConfig.SectionName)
    .Get<RentalCommand.Core.Configuration.ScreeningConfig>() ?? new RentalCommand.Core.Configuration.ScreeningConfig();
if (screeningConfig.Enabled)
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.IScreeningProvider,
        RentalCommand.Api.Services.Screening.TransUnionScreeningProvider>(c =>
    {
        c.BaseAddress = new Uri(screeningConfig.BaseUrl);
        c.Timeout = TimeSpan.FromSeconds(90);
    });
}
else
{
    builder.Services.AddScoped<RentalCommand.Core.Interfaces.IScreeningProvider,
        RentalCommand.Api.Services.Screening.DisabledScreeningProvider>();
}

// Admin Engine Health: reads the Engine's worker-heartbeat table + active LLM config to back
// the admin /admin/engine page (the Engine has no HTTP port, so the DB is the health contract).
builder.Services.AddScoped<RentalCommand.Api.Services.Admin.IAdminEngineStatusService,
    RentalCommand.Api.Services.Admin.AdminEngineStatusService>();

// --- Master simulation clock (TSK-615) ---
// Binds the ambient TimeProvider + IAppTimeZoneProvider. In production (or when Simulation:Enabled is
// false) this is TimeProvider.System — real clock, unchanged behavior. In non-prod with the flag on it
// binds the controllable SimulationTimeProvider; pinFrameworkAuthClock keeps cookie/security-stamp auth
// timing on the real clock even while domain time is simulated (S1).
builder.Services.AddSimulationClock(builder.Configuration, builder.Environment, pinFrameworkAuthClock: true);

var app = builder.Build();

// Apply migrations + seed the default admin/roles/portfolio so login works on a fresh database.
// Migration is idempotent (no-op when already applied); seeding is gated by Seed:Enabled (Development).
using (var scope = app.Services.CreateScope())
{
    var rlsActorMode = scope.ServiceProvider.GetRequiredService<RentalCommand.Api.Data.IRlsActorModeAccessor>();
    using var backgroundActor = rlsActorMode.Begin(RentalCommand.Api.Data.RlsActorMode.Background);
    var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
    // Advisory-locked so the API and Engine (both self-migrate on startup) don't race on a fresh batch.
    await DatabaseMigrator.MigrateWithLockAsync(db);

    var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
    await seeder.SeedAsync();

    // Demo data seeder — creates realistic interlinked data for portfolio 1 when enabled.
    // Idempotent: skips immediately if any properties already exist for portfolio 1.
    if (app.Configuration.GetValue<bool>("Seed:DemoData", false))
    {
        var demoSeeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
        await demoSeeder.SeedAsync();
    }

    // One-off self-owner backfill — gives pre-feature portfolios with no owners a primary self-owner.
    // OFF by default and idempotent. It WRITES owner rows, so it must be explicitly enabled per
    // environment (set Backfill:SelfOwners=true) and is intentionally NOT run unsupervised on prod.
    if (app.Configuration.GetValue<bool>("Backfill:SelfOwners", false))
    {
        var backfill = scope.ServiceProvider
            .GetRequiredService<RentalCommand.Api.Services.Domain.SelfOwnerBackfillService>();
        await backfill.RunAsync();
    }
}

// Behind Traefik (TLS terminator) the API receives plain HTTP on :8080, so honor X-Forwarded-Proto
// / X-Forwarded-For FIRST — otherwise Request.Scheme is "http", any absolute URL the API emits is
// http:// (browser mixed-content "Not Secure"), and the client IP is the proxy's.
//
// SECURITY (L-6): we do NOT trust forwarded headers from *any* caller — that lets a peer spoof
// X-Forwarded-For/Proto, poisoning the client IP used for refresh-token IP logging and the request
// scheme. The only ingress is Traefik on the internal Docker network, so we trust forwarded headers
// only from the proxy network(s). Configurable via ForwardedHeaders:KnownNetworks (a list of CIDRs);
// when unset we default to the RFC 1918 private ranges + loopback, which covers the Docker bridge the
// API actually sits on while still rejecting forwarded headers from any public source. The API is
// never published directly (compose keeps :8080 unpublished), so this is fail-safe if the topology
// changes.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Traefik adds one hop; allow a little headroom but don't trust an arbitrarily deep chain.
    ForwardLimit = 2
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();

var configuredKnownNetworks = app.Configuration
    .GetSection("ForwardedHeaders:KnownNetworks")
    .Get<string[]>();
var knownNetworkCidrs = configuredKnownNetworks is { Length: > 0 }
    ? configuredKnownNetworks
    : new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128" };

foreach (var cidr in knownNetworkCidrs)
{
    if (string.IsNullOrWhiteSpace(cidr)) continue;
    if (System.Net.IPNetwork.TryParse(cidr.Trim(), out var network))
    {
        forwardedHeadersOptions.KnownIPNetworks.Add(network);
    }
    else
    {
        app.Logger.LogWarning("Ignoring invalid ForwardedHeaders:KnownNetworks entry '{Cidr}'.", cidr);
    }
}
app.UseForwardedHeaders(forwardedHeadersOptions);

// Outermost exception wrapper: turns domain-rule violations into clean 400/409 ProblemDetails and keeps
// raw persistence faults from leaking internals (see GlobalExceptionHandler). Registered before the
// inline auth-context middleware below so anything that bubbles past it (DbUpdateException, domain
// validation, etc.) is mapped cleanly; MissingAuthContextException is still handled by that middleware
// and never reaches here.
app.UseExceptionHandler();

app.UseCors("WebApp");

// Map auth-context failures to a clean 401 instead of a 500. GetPortfolioId()/GetUserId() throw
// MissingAuthContextException when an authenticated request lacks the portfolioId/sub claim they
// require (e.g. a token with no portfolio scope) — without this the throw would surface as a 500.
// NOTE: catch the SPECIFIC type, NOT generic UnauthorizedAccessException — the BCL throws the latter
// for filesystem permission errors (e.g. an unwritable upload volume), and treating those as 401
// disguises infra failures as "session expired". Those now propagate to an honest 500.
// Registered high so it wraps controller execution.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (RentalCommand.Api.Controllers.MissingAuthContextException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
        }
    }
});

app.UseAuthentication();
// Platform bypass is explicit and server-owned. The ordinary customer Admin/Workspace Administrator
// role is intentionally irrelevant; only the separately configured platform allowlist opens it.
app.Use(async (context, next) =>
{
    if (!RentalCommand.Api.Auth.PlatformAdminPolicy.IsPlatformAdmin(context.User, platformAdminAllowlist))
    {
        await next();
        return;
    }

    var actorMode = context.RequestServices
        .GetRequiredService<RentalCommand.Api.Data.IRlsActorModeAccessor>();
    using var platformActor = actorMode.Begin(RentalCommand.Api.Data.RlsActorMode.Platform);
    await next();
});
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

// --- SignalR hubs (auth required; websocket transports pass the JWT via ?access_token=) ---
app.MapHub<DataUpdateHub>("/api/v1/hubs/updates");

app.Run();

// Exposed for WebApplicationFactory<Program> in integration/Tier-2 tests.
public partial class Program;
