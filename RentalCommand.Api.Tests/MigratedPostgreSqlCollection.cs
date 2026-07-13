using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigratedPostgreSqlCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Migrated PostgreSQL";
}
