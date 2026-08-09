using FluentAssertions;
using Npgsql;
using RentalCommand.Api.Data;

namespace RentalCommand.Api.Tests.Data;

public sealed class ApiDatabaseConnectionStringTests
{
    [Fact]
    public void WithJitDisabled_AddsSessionOptionWithoutChangingConnectionIdentity()
    {
        const string original = "Host=db;Database=rental;Username=rentalcommand_api;Password=test";

        var configured = new NpgsqlConnectionStringBuilder(
            ApiDatabaseConnectionString.WithJitDisabled(original));

        configured.Host.Should().Be("db");
        configured.Database.Should().Be("rental");
        configured.Username.Should().Be("rentalcommand_api");
        configured.Options.Should().Be("-c jit=off");
    }

    [Fact]
    public void WithJitDisabled_PreservesExistingSessionOptionsAndIsIdempotent()
    {
        const string original =
            "Host=db;Database=rental;Username=rentalcommand_api;Password=test;Options=-c statement_timeout=120000";

        var once = ApiDatabaseConnectionString.WithJitDisabled(original);
        var twice = ApiDatabaseConnectionString.WithJitDisabled(once);
        var configured = new NpgsqlConnectionStringBuilder(twice);

        configured.Options.Should().Be("-c statement_timeout=120000 -c jit=off");
    }
}
