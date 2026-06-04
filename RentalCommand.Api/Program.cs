using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Extensions;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

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
var llmProvider = builder.Configuration.GetValue<string>("Assistant:Provider") ?? "openai";
if (string.Equals(llmProvider, "anthropic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.AnthropicLlmProvider>(c =>
    {
        c.BaseAddress = new Uri("https://api.anthropic.com/");
        c.Timeout = TimeSpan.FromSeconds(90);
    });
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

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

// Fail fast on an unconfigured signing key. HS256 needs >= 256 bits (32 chars), and the
// committed placeholder must never be used outside local dev — otherwise anyone could
// forge tokens. The placeholder stays usable in Development so local dev needs no secret.
if (string.IsNullOrWhiteSpace(jwtSettings.SecretKey) || jwtSettings.SecretKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SecretKey is missing or too short (need >= 32 chars). Set it via configuration or a secret store.");
}
if (!builder.Environment.IsDevelopment() && jwtSettings.SecretKey.Contains("CHANGE_ME_IN_PRODUCTION"))
{
    throw new InvalidOperationException(
        "Jwt:SecretKey is still the committed placeholder. Set a real secret (env Jwt__SecretKey or a secret store) before deploying.");
}

// --- Database ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<RentalCommandDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHttpContextAccessor();

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
            ClockSkew = TimeSpan.Zero
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

builder.Services.AddAuthorization();

// --- Auth services ---
builder.Services.AddHttpClient("GoogleAuth");
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IUserMigrationService, UserMigrationService>();
builder.Services.AddScoped<IAuthEmailSender, OutboxAuthEmailSender>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
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
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddHealthChecks();

// --- SignalR (realtime hubs) + DataUpdateService broadcaster ---
// Mirror the REST enum-as-string convention on realtime payloads too.
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddScoped<IDataUpdateService, DataUpdateService>();
builder.Services.AddScoped<INotificationHubService, NotificationHubService>();

// --- Domain feature services (per-entity scoped CRUD) ---
builder.Services.AddDomainServices();

// --- Outbox message publisher (API-side: enqueues rows; Engine dispatches them) ---
builder.Services.AddScoped<IMessagePublisher, RentalCommand.Api.Services.OutboxMessagePublisher>();

// --- Scheduled owner statement worker (default OFF; set Reports:EmailOwnerStatementsMonthly=true to enable) ---
builder.Services.AddHostedService<RentalCommand.Api.Services.ScheduledOwnerStatementWorker>();

// --- Stripe payment services (gated — no-ops when Stripe keys are absent) ---
builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();

var app = builder.Build();

// Apply migrations + seed the default admin/roles/portfolio so login works on a fresh database.
// Migration is idempotent (no-op when already applied); seeding is gated by Seed:Enabled (Development).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
    await db.Database.MigrateAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
    await seeder.SeedAsync();

    // Demo data seeder — creates realistic interlinked data for portfolio 1 when enabled.
    // Idempotent: skips immediately if any properties already exist for portfolio 1.
    if (app.Configuration.GetValue<bool>("Seed:DemoData", false))
    {
        var demoSeeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
        await demoSeeder.SeedAsync();
    }
}

app.UseCors("WebApp");

// Map auth-context failures to a clean 401 instead of a 500. GetPortfolioId()/GetUserId() throw
// UnauthorizedAccessException when an authenticated request lacks the portfolioId/sub claim they
// require (e.g. a token with no portfolio scope) — without this the throw would surface as a 500.
// Registered high so it wraps controller execution.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException)
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
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

// --- SignalR hubs (auth required; websocket transports pass the JWT via ?access_token=) ---
app.MapHub<NotificationHub>("/api/v1/hubs/notifications");
app.MapHub<DataUpdateHub>("/api/v1/hubs/updates");

app.Run();

// Exposed for WebApplicationFactory<Program> in integration/Tier-2 tests.
public partial class Program;
