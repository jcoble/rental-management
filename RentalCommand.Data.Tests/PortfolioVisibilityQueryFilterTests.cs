using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Tests;

public sealed class PortfolioVisibilityQueryFilterTests
{
    [Fact]
    public void Required_relationships_below_filtered_principals_have_transitive_filters()
    {
        using var db = CreateDb();

        var unfilteredRequiredDependents = db.Model.GetEntityTypes()
            .SelectMany(dependent => dependent.GetForeignKeys()
                .Where(foreignKey =>
                    foreignKey.IsRequired &&
                    foreignKey.PrincipalEntityType.GetQueryFilter() is not null &&
                    dependent.GetQueryFilter() is null)
                .Select(foreignKey =>
                    $"{foreignKey.PrincipalEntityType.ClrType.Name} -> {dependent.ClrType.Name}"))
            .Distinct()
            .OrderBy(name => name)
            .ToArray();

        unfilteredRequiredDependents.Should().BeEmpty(
            "a required dependent must use the same DB-side visibility boundary as its filtered principal");
    }

    [Fact]
    public void Existing_soft_delete_filters_are_composed_with_portfolio_visibility()
    {
        using var db = CreateDb();

        QueryFilterFor<RentalListing>(db).ToString().Should()
            .Contain("row.DeletedAt == null")
            .And.Contain("row.Portfolio.DeletedAt == null");
        QueryFilterFor<EvictionCase>(db).ToString().Should()
            .Contain("row.DeletedAt == null")
            .And.Contain("row.Portfolio.DeletedAt == null");
        QueryFilterFor<EvictionCaseEvent>(db).ToString().Should()
            .Contain("row.DeletedAt == null")
            .And.Contain("row.EvictionCase.DeletedAt == null");
    }

    [Fact]
    public void Representative_filter_chains_translate_through_the_npgsql_provider()
    {
        using var db = CreateDb();

        var sql = new[]
        {
            db.Set<ExternalListingSignal>().Where(row => row.Id > 0).ToQueryString(),
            db.Set<ApplicationFinancialEntry>().Where(row => row.Id > 0).ToQueryString(),
            db.Set<SignatureAuditEvent>().Where(row => row.Id > 0).ToQueryString(),
            db.Set<TenantLedgerAllocation>().Where(row => row.Id > 0).ToQueryString(),
            db.Set<NoticeDeliveryEvidence>().Where(row => row.Id > 0).ToQueryString(),
            db.Set<TenantNoticeWorkItem>().Where(row => row.Id > 0).ToQueryString(),
        };

        sql.Should().OnlyContain(statement =>
            statement.Contains("WHERE", StringComparison.OrdinalIgnoreCase) &&
            statement.Contains("DeletedAt", StringComparison.Ordinal));
    }

    [Fact]
    public void Immutable_finance_and_legal_history_is_not_filtered_by_lifecycle_state()
    {
        using var db = CreateDb();

        QueryFilterFor<ApplicationFinancialAccount>(db).ToString().Should()
            .NotContain(nameof(ApplicationFinancialAccount.RentalApplication));
        QueryFilterFor<LeaseAgreement>(db).ToString().Should()
            .NotContain(nameof(LeaseAgreement.SupersededEffectiveOn))
            .And.NotContain(nameof(LeaseAgreement.FullyExecutedAtUtc));
        QueryFilterFor<TenantLedgerEntry>(db).ToString().Should()
            .NotContain(nameof(TenantAccount.ClosedAtUtc));
    }

    [Fact]
    public void Document_template_fields_are_directly_scoped_and_cannot_cross_portfolios()
    {
        using var db = CreateDb();

        typeof(IPortfolioScoped).IsAssignableFrom(typeof(DocumentTemplateField)).Should().BeTrue();
        var entity = db.Model.FindEntityType(typeof(DocumentTemplateField))!;
        var expectedForeignKey = new[]
        {
            nameof(DocumentTemplateField.DocumentTemplateId),
            nameof(DocumentTemplateField.PortfolioId),
        };
        var expectedPrincipalKey = new[]
        {
            nameof(DocumentTemplate.Id),
            nameof(DocumentTemplate.PortfolioId),
        };
        var expectedIndex = new[]
        {
            nameof(DocumentTemplateField.PortfolioId),
            nameof(DocumentTemplateField.DocumentTemplateId),
            nameof(DocumentTemplateField.FieldKey),
        };
        entity.FindProperty(nameof(DocumentTemplateField.PortfolioId))!.IsNullable.Should().BeFalse();
        entity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(expectedForeignKey) &&
            foreignKey.PrincipalKey.Properties.Select(property => property.Name).SequenceEqual(expectedPrincipalKey));
        entity.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(expectedIndex));
        FoundationBaselinePostgreSql.DirectPortfolioTables.Should().Contain("DocumentTemplateFields");
        FoundationBaselinePostgreSql.ChildPortfolioTables
            .Select(policy => policy.Table).Should().NotContain("DocumentTemplateFields");
    }

    private static System.Linq.Expressions.LambdaExpression QueryFilterFor<TEntity>(
        RentalCommandDbContext db) where TEntity : class =>
        db.Model.FindEntityType(typeof(TEntity))!.GetQueryFilter()!;

    private static RentalCommandDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=query_filter_contract;Username=contract;Password=contract")
            .ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;
        return new RentalCommandDbContext(options);
    }
}
