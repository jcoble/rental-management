using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RentalCommand.Data;

namespace RentalCommand.Api.Data;

/// <summary>
/// Gives EF tooling the same privileged, non-intercepted context used by the runtime migration
/// lease. The steady-state API context cannot bootstrap a clean database because its first open
/// intentionally assumes the least-privilege <c>rentalcommand_api</c> role already exists.
/// </summary>
public sealed class RentalCommandDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<RentalCommandDbContext>
{
    public RentalCommandDbContext CreateDbContext(string[] args)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var apiDirectory = Directory.Exists(Path.Combine(currentDirectory, "RentalCommand.Api"))
            ? Path.Combine(currentDirectory, "RentalCommand.Api")
            : currentDirectory;
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("MigratorConnection")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "EF tooling requires ConnectionStrings:MigratorConnection or DefaultConnection.");

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }
}
