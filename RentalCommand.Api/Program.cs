using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Extensions;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration binding ---
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));
builder.Services.Configure<SeedSettings>(builder.Configuration.GetSection(SeedSettings.SectionName));

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

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
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IUserMigrationService, UserMigrationService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IdentitySeeder>();

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

builder.Services.AddControllers();
builder.Services.AddHealthChecks();

// --- SignalR (realtime hubs) + DataUpdateService broadcaster ---
builder.Services.AddSignalR().AddJsonProtocol();
builder.Services.AddScoped<IDataUpdateService, DataUpdateService>();
builder.Services.AddScoped<INotificationHubService, NotificationHubService>();

// --- Domain feature services (per-entity scoped CRUD) ---
builder.Services.AddDomainServices();

var app = builder.Build();

// Apply migrations + seed the default admin/roles/portfolio so login works on a fresh database.
// Migration is idempotent (no-op when already applied); seeding is gated by Seed:Enabled (Development).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
    await db.Database.MigrateAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
    await seeder.SeedAsync();
}

app.UseCors("WebApp");
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
