var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed for WebApplicationFactory<Program> in integration/Tier-2 tests.
public partial class Program;
