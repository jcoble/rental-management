using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using Testcontainers.PostgreSql;

namespace RentalCommand.TestCommon;

/// <summary>
/// Shares one PostgreSQL container across the test process, migrates a template database once,
/// and clones an isolated database for each test. PostgreSQL-only persistence tests therefore
/// exercise the production provider without paying the migration cost for every fact.
/// </summary>
public sealed class MigratedPostgreSqlFixture : IAsyncLifetime
{
    private const string TemplateDatabase = "rentalcommand_test_template";
    private const string MigratedIntegrationTemplate = "rentalcommand_integration_migrated";
    private const string ModelIntegrationTemplate = "rentalcommand_integration_model";
    private static readonly Lazy<Task<PostgreSqlContainer>> SharedPostgres = new(StartPostgresAsync);
    private PostgreSqlContainer? _postgres;
    private string _adminConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = await SharedPostgres.Value;
        _adminConnectionString = BuildConnectionString("postgres");
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        var postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithCreateParameterModifier(parameters => parameters.Platform = DockerPlatform)
            .WithTmpfsMount("/var/lib/postgresql/data")
            .WithCommand(
                "-c", "fsync=off",
                "-c", "synchronous_commit=off",
                "-c", "full_page_writes=off",
                "-c", "max_locks_per_transaction=1024")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await postgres.StartAsync();

        var adminConnectionString = BuildConnectionString(postgres, "postgres");
        await ExecuteAdminCommandAsync(adminConnectionString, $"CREATE DATABASE \"{TemplateDatabase}\"");

        var templateConnectionString = BuildConnectionString(postgres, TemplateDatabase);
        await using var db = CreateDbContext(templateConnectionString);
        await db.Database.MigrateAsync();
        var seededAt = DateTime.UtcNow;
        db.AddRange(
            new Portfolio
            {
                Name = "Test Portfolio",
                ManagementCompanyName = "Test Co",
                TimeZone = "UTC",
                CreatedAt = seededAt,
                UpdatedAt = seededAt,
            },
            new ApplicationUser
            {
                UserName = "postgres-tests@example.test",
                NormalizedUserName = "POSTGRES-TESTS@EXAMPLE.TEST",
                Email = "postgres-tests@example.test",
                NormalizedEmail = "POSTGRES-TESTS@EXAMPLE.TEST",
                DisplayName = "PostgreSQL Test Actor",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = seededAt,
            });
        await db.SaveChangesAsync();
        if (await db.Portfolios.Select(portfolio => portfolio.Id).SingleAsync() != 1 ||
            await db.Users.Select(user => user.Id).SingleAsync() != 1)
        {
            throw new InvalidOperationException("The migrated test template must seed Portfolio and User Id 1.");
        }

        await ExecuteAdminCommandAsync(
            adminConnectionString,
            $"CREATE DATABASE \"{MigratedIntegrationTemplate}\"");
        await using (var migrated = CreateDbContext(
                         BuildConnectionString(postgres, MigratedIntegrationTemplate)))
        {
            await migrated.Database.MigrateAsync();
        }

        await ExecuteAdminCommandAsync(
            adminConnectionString,
            $"CREATE DATABASE \"{ModelIntegrationTemplate}\"");
        await using (var model = CreateDbContext(
                         BuildConnectionString(postgres, ModelIntegrationTemplate)))
        {
            await model.Database.EnsureCreatedAsync();
        }

        await ExecuteAdminCommandAsync(
            adminConnectionString,
            $"ALTER ROLE rentalcommand_api PASSWORD '{SharedPostgreSqlDatabase.ApiPassword}'; " +
            $"ALTER ROLE rentalcommand_engine PASSWORD '{SharedPostgreSqlDatabase.EnginePassword}';");

        return postgres;
    }

    private static string DockerPlatform => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "linux/arm64",
        Architecture.X64 => "linux/amd64",
        _ => throw new PlatformNotSupportedException(
            $"PostgreSQL tests do not support {RuntimeInformation.ProcessArchitecture} hosts."),
    };

    public async Task<MigratedPostgreSqlTestContext> CreateContextAsync(
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var databaseName = $"rc_test_{Guid.NewGuid():N}";
        await ExecuteAdminCommandAsync(
            $"CREATE DATABASE \"{databaseName}\" TEMPLATE \"{TemplateDatabase}\"");

        var connectionString = BuildConnectionString(databaseName);
        return new MigratedPostgreSqlTestContext(
            CreateDbContext(connectionString, interceptors),
            connectionString,
            () => DropDatabaseAsync(databaseName));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string BuildConnectionString(string databaseName)
        => BuildConnectionString(_postgres!, databaseName);

    private static string BuildConnectionString(PostgreSqlContainer postgres, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Database = databaseName,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    private static RentalCommandDbContext CreateDbContext(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var builder = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connectionString);
        if (interceptors is not null)
        {
            builder.AddInterceptors(interceptors);
        }

        return new RentalCommandDbContext(builder.Options);
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        await ExecuteAdminCommandAsync($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
    }

    private async Task ExecuteAdminCommandAsync(string sql)
        => await ExecuteAdminCommandAsync(_adminConnectionString, sql);

    private static async Task ExecuteAdminCommandAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<(string DatabaseName, string ConnectionString)> CreateDatabaseAsync(
        SharedPostgreSqlSchema schema)
    {
        var postgres = await SharedPostgres.Value;
        var databaseName = $"rc_test_{Guid.NewGuid():N}";
        var adminConnectionString = BuildConnectionString(postgres, "postgres");
        var template = schema switch
        {
            SharedPostgreSqlSchema.Migrated => $" TEMPLATE \"{MigratedIntegrationTemplate}\"",
            SharedPostgreSqlSchema.Model => $" TEMPLATE \"{ModelIntegrationTemplate}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(schema)),
        };
        await ExecuteAdminCommandAsync(
            adminConnectionString,
            $"CREATE DATABASE \"{databaseName}\"{template}");
        return (databaseName, BuildConnectionString(postgres, databaseName));
    }

    internal static async Task DropSharedDatabaseAsync(string databaseName)
    {
        var postgres = await SharedPostgres.Value;
        var adminConnectionString = BuildConnectionString(postgres, "postgres");
        await ExecuteAdminCommandAsync(
            adminConnectionString,
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
    }
}

public enum SharedPostgreSqlSchema
{
    Migrated,
    Model,
}

public sealed class SharedPostgreSqlDatabase : IAsyncDisposable
{
    public const string ApiPassword = "rentalcommand-test-api";
    public const string EnginePassword = "rentalcommand-test-engine";

    private readonly SharedPostgreSqlSchema _schema;
    private string? _databaseName;
    private string? _connectionString;

    public SharedPostgreSqlDatabase(SharedPostgreSqlSchema schema)
    {
        _schema = schema;
    }

    public async Task StartAsync()
    {
        (_databaseName, _connectionString) = await MigratedPostgreSqlFixture.CreateDatabaseAsync(_schema);
    }

    public string GetConnectionString() => _connectionString
        ?? throw new InvalidOperationException("The shared PostgreSQL database has not been started.");

    public async ValueTask DisposeAsync()
    {
        if (_databaseName is null)
        {
            return;
        }

        await MigratedPostgreSqlFixture.DropSharedDatabaseAsync(_databaseName);
        _databaseName = null;
        _connectionString = null;
    }
}

public sealed class MigratedPostgreSqlTestContext : IAsyncDisposable
{
    private readonly Func<Task> _dropDatabase;
    private bool _disposed;

    internal MigratedPostgreSqlTestContext(
        RentalCommandDbContext db,
        string connectionString,
        Func<Task> dropDatabase)
    {
        Db = db;
        ConnectionString = connectionString;
        _dropDatabase = dropDatabase;
    }

    public RentalCommandDbContext Db { get; }
    public string ConnectionString { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Db.DisposeAsync();
        await _dropDatabase();
    }
}
