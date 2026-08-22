using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests;

public static class MigratedPostgreSqlCollection
{
    public const string Name1 = "Migrated PostgreSQL 1";
    public const string Name2 = "Migrated PostgreSQL 2";
    public const string Name3 = "Migrated PostgreSQL 3";
    public const string Name4 = "Migrated PostgreSQL 4";
}

[CollectionDefinition(MigratedPostgreSqlCollection.Name1)]
public sealed class MigratedPostgreSqlCollection1 : ICollectionFixture<MigratedPostgreSqlFixture>
{
}

[CollectionDefinition(MigratedPostgreSqlCollection.Name2)]
public sealed class MigratedPostgreSqlCollection2 : ICollectionFixture<MigratedPostgreSqlFixture>
{
}

[CollectionDefinition(MigratedPostgreSqlCollection.Name3)]
public sealed class MigratedPostgreSqlCollection3 : ICollectionFixture<MigratedPostgreSqlFixture>
{
}

[CollectionDefinition(MigratedPostgreSqlCollection.Name4)]
public sealed class MigratedPostgreSqlCollection4 : ICollectionFixture<MigratedPostgreSqlFixture>
{
}
