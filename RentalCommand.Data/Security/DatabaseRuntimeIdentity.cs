using System.Data.Common;

namespace RentalCommand.Data.Security;

/// <summary>
/// Runtime database identities are direct, restricted logins. Application processes never connect
/// as the schema owner and never assume a role after opening a connection.
/// </summary>
public static class DatabaseRuntimeIdentity
{
    public const string ApiRole = "rentalcommand_api";
    public const string EngineRole = "rentalcommand_engine";
    public const string AtomicReadOnlyRole = "rentalcommand_atomic_readonly";

    public static void ValidateOpenedConnection(DbConnection connection, string expectedRole)
    {
        using var command = BuildValidationCommand(connection, expectedRole);
        var valid = command.ExecuteScalar() as bool?;
        if (valid is not true)
        {
            throw new InvalidOperationException(
                $"Database connection is not the restricted direct-login role '{expectedRole}'.");
        }
    }

    public static async Task ValidateOpenedConnectionAsync(
        DbConnection connection,
        string expectedRole,
        CancellationToken cancellationToken = default)
    {
        await using var command = BuildValidationCommand(connection, expectedRole);
        var valid = await command.ExecuteScalarAsync(cancellationToken);
        if (valid is not true)
        {
            throw new InvalidOperationException(
                $"Database connection is not the restricted direct-login role '{expectedRole}'.");
        }
    }

    private static DbCommand BuildValidationCommand(DbConnection connection, string expectedRole)
    {
        if (expectedRole is not (ApiRole or EngineRole))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRole));
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_user = @expected_role
               AND current_user = @expected_role
               AND runtime_role.rolcanlogin
               AND NOT runtime_role.rolsuper
               AND NOT runtime_role.rolcreatedb
               AND NOT runtime_role.rolcreaterole
               AND NOT runtime_role.rolinherit
               AND NOT runtime_role.rolreplication
               AND NOT runtime_role.rolbypassrls
               AND 1 = (
                 SELECT count(*)
                 FROM pg_auth_members membership
                 JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
                 WHERE membership.member = runtime_role.oid
                   AND granted_role.rolname = 'rentalcommand_atomic_readonly'
                   AND NOT membership.admin_option
                   AND NOT membership.inherit_option
                   AND membership.set_option)
               AND NOT EXISTS (
                 SELECT 1
                 FROM pg_auth_members membership
                 LEFT JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
                 WHERE membership.roleid = runtime_role.oid
                    OR (membership.member = runtime_role.oid
                        AND (granted_role.rolname <> 'rentalcommand_atomic_readonly'
                             OR membership.admin_option
                             OR membership.inherit_option
                             OR NOT membership.set_option)))
            FROM pg_roles runtime_role
            WHERE runtime_role.rolname = @expected_role;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "expected_role";
        parameter.Value = expectedRole;
        command.Parameters.Add(parameter);
        return command;
    }
}
