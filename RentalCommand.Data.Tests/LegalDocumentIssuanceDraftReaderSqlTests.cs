using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

/// <summary>
/// Generated-SQL guards for legal-document issuance reads. These tests inspect the exact
/// production query builders: authorization, legal identity, revision comparison, and primary
/// signer selection must remain in one provider-translated PostgreSQL statement.
/// </summary>
public sealed class LegalDocumentIssuanceDraftReaderSqlTests
{
    private static readonly WorkspaceReadScope Scope = new(
        PortfolioId: 41,
        UserId: 43,
        SessionId: Guid.Parse("7867e44a-2a2e-42bf-93f8-1c9d76b6677a"),
        AccessContextId: 47,
        AccessRevision: 53);

    private static readonly DateTime SecurityNowUtc =
        new(2026, 7, 13, 14, 15, 16, DateTimeKind.Utc);

    [Fact]
    public void Agreement_draft_read_is_one_authorized_translated_postgresql_statement()
    {
        using var db = Context();
        var query = new LegalDocumentIssuanceDraftReader(db).BuildAgreementQuery(
            Scope,
            leaseManagementId: 59,
            leaseAgreementId: 61,
            expectedDraftRevision: 67,
            securityNowUtc: SecurityNowUtc);

        var sql = query.ToQueryString();

        AssertSingleTranslatedStatement(sql);
        AssertWorkspaceCapabilityAndPropertyScope(sql);
        AssertExactLegalFacts(
            sql,
            legalTable: "LeaseAgreements",
            legalParameterName: "leaseAgreementId",
            legalId: 61,
            expectedDraftRevision: 67);
        sql.Should().Contain("\"LeaseManagements\"");
        sql.Should().MatchRegex($"\\\"Id\\\" = {ParameterReferencePattern("leaseManagementId")}");
        AssertNamedParameterValue(sql, "leaseManagementId", 59);
        AssertPrimarySignerSelection(sql, "LeaseAgreementSigners");
    }

    [Fact]
    public void Addendum_draft_read_is_one_authorized_translated_postgresql_statement()
    {
        using var db = Context();
        var query = new LegalDocumentIssuanceDraftReader(db).BuildAddendumQuery(
            Scope,
            leaseManagementId: 71,
            leaseAddendumId: 73,
            expectedDraftRevision: 79,
            securityNowUtc: SecurityNowUtc);

        var sql = query.ToQueryString();

        AssertSingleTranslatedStatement(sql);
        AssertWorkspaceCapabilityAndPropertyScope(sql);
        AssertExactLegalFacts(
            sql,
            legalTable: "LeaseAddenda",
            legalParameterName: "leaseAddendumId",
            legalId: 73,
            expectedDraftRevision: 79);
        sql.Should().Contain("\"LeaseManagements\"");
        sql.Should().Contain("\"LeaseAgreements\"",
            "the addendum projection must obtain canonical financial terms from its exact base Agreement");
        sql.Should().Contain("\"BaseAgreementId\"");
        sql.Should().Contain("\"LeaseAddendumFinancialEffects\"",
            "typed contractual effects must be selected with the exact Addendum draft");
        sql.Should().Contain("\"EffectType\"");
        sql.Should().Contain("\"Amount\"");
        sql.Should().Contain("\"EffectiveFromOn\"");
        sql.Should().MatchRegex(
            "ORDER BY[^;]*?\\\"EffectType\\\"[^;]*?\\\"EffectiveFromOn\\\"[^;]*?\\\"Id\\\"",
            "typed effects must be ordered stably in PostgreSQL");
        sql.Should().MatchRegex($"\\\"Id\\\" = {ParameterReferencePattern("leaseManagementId")}");
        AssertNamedParameterValue(sql, "leaseManagementId", 71);
        AssertPrimarySignerSelection(sql, "LeaseAddendumSigners");
    }

    private static void AssertSingleTranslatedStatement(string sql)
    {
        sql.Should().Contain("SELECT");
        sql.Should().NotContain("ClientEvaluation");
        SqlWithoutParameterDeclarations(sql).TrimStart().Should()
            .StartWith("SELECT")
            .And.NotContain(";",
                "the production IQueryable must translate to one PostgreSQL command, not a batch");
    }

    private static void AssertWorkspaceCapabilityAndPropertyScope(string sql)
    {
        sql.Should().Contain("\"Properties\"");
        Regex.Matches(sql, @"FROM public\.rc_api_effective_capability_scopes\(").Count.Should()
            .Be(2, "each issuance capability must use the canonical PostgreSQL authorization scope");
        sql.Should().Contain("\"ScopeKind\"");
        sql.Should().Contain("\"PropertyId\"");
        sql.Should().Contain("'AllProperties'");
        sql.Should().Contain("'SelectedProperties'");
        sql.Should().Contain(CapabilityKeys.RentalsManage);
        sql.Should().Contain(CapabilityKeys.LeasingAgreementsPrepare);
        sql.Should().Contain("UNION",
            "either issuance capability must authorize the same property-scoped SQL read");
        AssertNamedParameterValue(sql, "scope_PortfolioId", Scope.PortfolioId);
        sql.Should().MatchRegex($"\\\"PortfolioId\\\" = {ParameterReferencePattern("scope_PortfolioId")}");
        AssertCanonicalScopeFunctionBindings(sql);
    }

    private static void AssertCanonicalScopeFunctionBindings(string sql)
    {
        var calls = Regex.Matches(
            sql,
            """
            FROM\s+public\.rc_api_effective_capability_scopes\(\s*
                (?<portfolio>@[A-Za-z0-9_]+),\s*
                (?<session>@[A-Za-z0-9_]+),\s*
                (?<user>@[A-Za-z0-9_]+),\s*
                (?<context>@[A-Za-z0-9_]+),\s*
                (?<revision>@[A-Za-z0-9_]+),\s*
                (?<capabilities>@[A-Za-z0-9_]+),\s*
                (?<targetKind>@[A-Za-z0-9_]+)\)
            """,
            RegexOptions.IgnorePatternWhitespace);

        calls.Count.Should().Be(2);
        foreach (Match call in calls)
        {
            AssertParameterReferenceValue(sql, call.Groups["portfolio"].Value, Scope.PortfolioId);
            AssertParameterReferenceValue(sql, call.Groups["session"].Value, Scope.SessionId);
            AssertParameterReferenceValue(sql, call.Groups["user"].Value, Scope.UserId);
            AssertParameterReferenceValue(sql, call.Groups["context"].Value, Scope.AccessContextId);
            AssertParameterReferenceValue(sql, call.Groups["revision"].Value, Scope.AccessRevision);
        }
    }

    private static void AssertExactLegalFacts(
        string sql,
        string legalTable,
        string legalParameterName,
        int legalId,
        int expectedDraftRevision)
    {
        sql.Should().Contain($"\"{legalTable}\"");
        sql.Should().MatchRegex($"\\\"Id\\\" = {ParameterReferencePattern(legalParameterName)}");
        AssertNamedParameterValue(sql, legalParameterName, legalId);
        sql.Should().Contain("\"PortfolioId\"");
        sql.Should().Contain("\"LeaseManagementId\"");
        sql.Should().Contain("\"DraftRevision\"");
        sql.Should().MatchRegex($"\\\"DraftRevision\\\" = {ParameterReferencePattern("expectedDraftRevision")}");
        AssertNamedParameterValue(sql, "expectedDraftRevision", expectedDraftRevision);
    }

    private static void AssertPrimarySignerSelection(string sql, string signerTable)
    {
        sql.Should().Contain($"\"{signerTable}\"");
        sql.Should().Contain("\"NameSnapshot\"");
        sql.Should().Contain("\"EmailSnapshot\"");
        Regex.Matches(sql, "\\\"SignerRole\\\" = 'PrimaryTenant'").Count.Should()
            .BeGreaterThanOrEqualTo(1,
                "the tenant identity must be selected from a primary tenant in PostgreSQL");
        Regex.Matches(
                sql,
                "ORDER BY[^;]*?\\\"SigningOrder\\\"[^;]*?\\\"Id\\\"[^;]*?LIMIT 1",
                RegexOptions.Singleline)
            .Count.Should().BeGreaterThanOrEqualTo(1,
                "primary-signer ordering and selection must execute in PostgreSQL");
    }

    private static void AssertNamedParameterValue(string sql, string parameterName, object value)
    {
        var expectedValue = Regex.Escape(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        sql.Should().MatchRegex(
            $"-- {ParameterReferencePattern(parameterName)}='?{expectedValue}'?",
            $"{parameterName} must be bound into the translated PostgreSQL statement");
    }

    private static void AssertParameterReferenceValue(string sql, string parameterReference, object value)
    {
        var expectedValue = Regex.Escape(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        sql.Should().MatchRegex(
            $"-- {Regex.Escape(parameterReference.TrimStart('@'))}='?{expectedValue}'?",
            $"{parameterReference} must bind the canonical authorization input");
    }

    private static string ParameterReferencePattern(string parameterName) =>
        $"@(?:__)?{Regex.Escape(parameterName)}(?:_?[0-9]+)?";

    private static string SqlWithoutParameterDeclarations(string sql) => string.Join(
        '\n',
        sql.Split('\n').Where(line => !line.StartsWith("-- ", StringComparison.Ordinal)));

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);
}
