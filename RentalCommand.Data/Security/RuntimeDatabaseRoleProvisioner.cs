using Npgsql;

namespace RentalCommand.Data.Security;

/// <summary>One-shot migration support for setting the two runtime login passwords.</summary>
public static class RuntimeDatabaseRoleProvisioner
{
    public static void ValidateRuntimeConnectionString(
        string connectionString,
        string expectedRole,
        bool allowDevelopmentDefault = false)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Username, expectedRole, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(builder.Password))
        {
            throw new InvalidOperationException(
                $"Runtime connection must use direct restricted login '{expectedRole}' with its own password.");
        }

        if (!allowDevelopmentDefault &&
            builder.Password is ("rentalcommand_api_dev" or "rentalcommand_engine_dev"))
        {
            throw new InvalidOperationException(
                $"Committed development database password for '{expectedRole}' is forbidden outside Development.");
        }
    }

    public static async Task ProvisionAsync(
        string migratorConnectionString,
        string apiConnectionString,
        string engineConnectionString,
        bool allowDevelopmentDefaults = false,
        CancellationToken cancellationToken = default)
    {
        ValidateRuntimeConnectionString(
            apiConnectionString,
            DatabaseRuntimeIdentity.ApiRole,
            allowDevelopmentDefaults);
        ValidateRuntimeConnectionString(
            engineConnectionString,
            DatabaseRuntimeIdentity.EngineRole,
            allowDevelopmentDefaults);

        var api = new NpgsqlConnectionStringBuilder(apiConnectionString);
        var engine = new NpgsqlConnectionStringBuilder(engineConnectionString);
        if (string.Equals(api.Password, engine.Password, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("API and Engine database passwords must be distinct.");
        }

        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken);
        await SetPasswordAsync(connection, DatabaseRuntimeIdentity.ApiRole, api.Password!, cancellationToken);
        await SetPasswordAsync(connection, DatabaseRuntimeIdentity.EngineRole, engine.Password!, cancellationToken);
    }

    private static async Task SetPasswordAsync(
        NpgsqlConnection connection,
        string role,
        string password,
        CancellationToken cancellationToken)
    {
        await using var format = connection.CreateCommand();
        format.CommandText = "SELECT format('ALTER ROLE %I PASSWORD %L', @role, @password);";
        format.Parameters.AddWithValue("role", role);
        format.Parameters.AddWithValue("password", password);
        var sql = (string?)await format.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Could not build password command for {role}.");

        await using var alter = connection.CreateCommand();
        alter.CommandText = sql;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
