using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

public sealed class LegalDocumentSourceVersionResolverSqlTests
{
    [Theory]
    [InlineData("ResolveAuthoredSql", "AuthoredTemplateSnapshot")]
    [InlineData("ResolveImportedSql", "ImportedExternalDocument")]
    public void Atomic_source_resolution_is_insert_or_select_without_mutating_the_winner(
        string fieldName,
        string sourceKind)
    {
        var sql = StaticAtomicSql(fieldName);

        sql.Should().Contain("ON CONFLICT (\"PortfolioId\", \"BusinessKey\") DO NOTHING");
        sql.Should().Contain("RETURNING \"Id\"");
        sql.Should().Contain("(SELECT \"Id\" FROM inserted)");
        sql.Should().Contain("FROM \"LegalDocumentSourceVersions\" AS source");
        sql.Should().Contain($"source.\"SourceKind\" = '{sourceKind}'");
        sql.Should().NotContain("DO UPDATE");
    }

    [Fact]
    public void Atomic_source_resolution_replays_once_for_a_concurrent_invisible_winner()
    {
        var attempts = (int)(typeof(AtomicLeaseMutationPersistence)
            .GetField("ConcurrentSourceResolutionAttempts", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Missing source-resolution attempt contract."));

        attempts.Should().Be(2);
    }

    [Fact]
    public void Active_overlay_capture_is_one_snapshot_insert_and_return_statement()
    {
        var sql = StaticAtomicSql("ResolveActiveOverlayForRendererSql");

        sql.Should().Contain("WITH candidate AS MATERIALIZED");
        sql.Should().Contain("jsonb_agg(jsonb_build_object(");
        sql.Should().Contain("ORDER BY field.\"SortOrder\", field.\"Id\"");
        sql.Should().Contain("INSERT INTO \"LegalDocumentSourceVersions\"");
        sql.Should().Contain("ON CONFLICT (\"PortfolioId\", \"BusinessKey\") DO NOTHING");
        sql.Should().Contain("source.\"SnapshotPayload\"");
        sql.Should().Contain("source.\"SnapshotPayload\" ->> 'originalStoredFileId'");
        sql.Should().Contain("field.\"PortfolioId\" = template.\"PortfolioId\"");
        sql.Should().NotContain("UPDATE");
    }

    [Fact]
    public void Atomic_authored_snapshot_scopes_template_fields_in_the_same_statement()
    {
        var sql = StaticAtomicSql("ResolveAuthoredSql");

        sql.Should().Contain("field.\"DocumentTemplateId\" = template.\"Id\"");
        sql.Should().Contain("field.\"PortfolioId\" = template.\"PortfolioId\"");
    }

    [Fact]
    public void Built_in_resolver_targets_the_filtered_renderer_identity()
    {
        var sql = StaticAtomicSql("ResolveBuiltInSql");

        sql.Should().Contain("ON CONFLICT (\"PortfolioId\", \"RendererKey\", \"RendererVersion\")");
        sql.Should().Contain("WHERE \"SourceKind\" = 'BuiltInRenderer'");
        sql.Should().Contain("DO NOTHING");
        sql.Should().Contain("RETURNING \"Id\"");
        sql.Should().NotContain("UPDATE");
    }

    [Fact]
    public void Supplied_lease_source_has_stable_explicit_provenance()
    {
        BuiltInLeaseAgreementSource.BusinessKey.Should().Be("built-in:lease-agreement:v1");
        BuiltInLeaseAgreementSource.RendererKey.Should().Be("rental-command-built-in-lease-agreement");
        BuiltInLeaseAgreementSource.RendererVersion.Should().Be(1);
        BuiltInLeaseAgreementSource.SnapshotPayload.Should().Contain("RentalCommandSupplied");
    }

    [Fact]
    public void Built_in_source_identity_is_unique_in_the_database_model()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=none;Password=none")
                .Options);

        var entity = db.Model.FindEntityType(typeof(LegalDocumentSourceVersion))!;
        var index = entity.GetIndexes().Single(candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual(
                ["PortfolioId", "RendererKey", "RendererVersion"]));

        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("\"SourceKind\" = 'BuiltInRenderer'");
    }

    private static string StaticAtomicSql(string fieldName) =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)
            ?? throw new InvalidOperationException($"Missing atomic SQL field {fieldName}."));
}
