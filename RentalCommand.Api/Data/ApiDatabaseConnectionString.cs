using System.Text.RegularExpressions;
using Npgsql;

namespace RentalCommand.Api.Data;

internal static partial class ApiDatabaseConnectionString
{
    private const string DisableJitOption = "-c jit=off";

    internal static string WithJitDisabled(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var options = builder.Options?.Trim();
        if (string.IsNullOrWhiteSpace(options))
        {
            builder.Options = DisableJitOption;
        }
        else if (!JitDisabledOptionRegex().IsMatch(options))
        {
            builder.Options = $"{options} {DisableJitOption}";
        }

        return builder.ConnectionString;
    }

    [GeneratedRegex(@"(?:^|\s)(?:-c\s+)?jit\s*=\s*off(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex JitDisabledOptionRegex();
}
