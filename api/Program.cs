using System.Text.Json.Serialization;
using Lifecycle.Api;
using Lifecycle.Api.Middleware;
using Lifecycle.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

var dbConnectionString = builder.Configuration.GetConnectionString("Default")
    ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "lifecycle.db")}";
builder.Services.AddDbContext<LifecycleDbContext>(options =>
    options.UseSqlite(dbConnectionString));

builder.Services.AddSingleton<SseService>();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LifecycleDbContext>();
    db.Database.Migrate();
}

Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "uploads"));

app.UseCors();
app.UseStaticFiles();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    appBuilder => appBuilder.UseMiddleware<ApiKeyAuthMiddleware>());

app.MapPortfolioEndpoints();
app.MapPropertyEndpoints();
app.MapUnitEndpoints();
app.MapOwnerEndpoints();
app.MapTenantEndpoints();
app.MapVendorEndpoints();
app.MapLeaseEndpoints();
app.MapPaymentEndpoints();
app.MapAccountingEndpoints();
app.MapWorkOrderEndpoints();
app.MapAppointmentEndpoints();
app.MapInspectionEndpoints();
app.MapActivityEndpoints();
app.MapAiEndpoints();
app.MapSseEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
