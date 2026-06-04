using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests for the markdown knowledge-base loader + simple keyword retrieval. Writes temporary .md
/// fixtures into a throwaway directory and points the loader at it — independent of the real
/// KnowledgeBase/*.md content authored elsewhere.
/// </summary>
public sealed class KnowledgeBaseServiceTests : IDisposable
{
    private readonly string _dir;

    public KnowledgeBaseServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rc-kb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    private KnowledgeBaseService CreateSut() =>
        new(_dir, NullLogger<KnowledgeBaseService>.Instance);

    private const string RecordPaymentDoc = """
        ---
        title: Recording a Payment
        category: Money
        slug: record-a-payment
        order: 2
        summary: How to record rent and other payments.
        keywords: payment, rent, record, collect, receipt
        ---
        Intro text about money before any section heading.

        ## Record a rent payment
        Open the lease, tap Add Payment, enter the amount and date, then save.

        ## Partial payments
        You can record a partial payment and the balance stays due.
        """;

    private const string SecurityDepositDoc = """
        ---
        title: Security Deposits
        category: Money
        slug: security-deposits
        order: 1
        summary: What a security deposit is and how it is held.
        keywords: deposit, security, trust, move-out
        ---
        ## What is a security deposit
        A security deposit is money held in trust per the lease, separate from rent income.
        """;

    // -------------------------------------------------------------------------
    // Frontmatter parsing
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseFile_ReadsFrontmatterMetadata()
    {
        WriteFile("payment.md", RecordPaymentDoc);
        var sut = CreateSut();

        var article = sut.GetArticle("record-a-payment");

        article.Should().NotBeNull();
        article!.Title.Should().Be("Recording a Payment");
        article.Category.Should().Be("Money");
        article.Order.Should().Be(2);
        article.Summary.Should().Be("How to record rent and other payments.");
        article.Keywords.Should().Contain(new[] { "payment", "rent", "record", "collect", "receipt" });
        // Body excludes the frontmatter but keeps the markdown.
        article.Body.Should().Contain("## Record a rent payment");
        article.Body.Should().NotContain("title: Recording a Payment");
    }

    [Fact]
    public void ParseFile_MissingSlug_FallsBackToFilename()
    {
        WriteFile("getting-started.md", """
            ---
            title: Getting Started
            category: Getting Started
            ---
            ## Welcome
            Body.
            """);
        var sut = CreateSut();

        var article = sut.GetArticle("getting-started");
        article.Should().NotBeNull();
        article!.Title.Should().Be("Getting Started");
    }

    [Fact]
    public void GetArticle_UnknownSlug_ReturnsNull()
    {
        WriteFile("payment.md", RecordPaymentDoc);
        CreateSut().GetArticle("does-not-exist").Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Section splitting
    // -------------------------------------------------------------------------

    [Fact]
    public void SplitSections_SplitsOnLevelTwoHeadings_AndKeepsLead()
    {
        var sections = KnowledgeBaseService.SplitSections(
            "Lead paragraph.\n\n## First\nContent one.\n\n## Second\nContent two.");

        sections.Should().HaveCount(3);
        sections[0].Heading.Should().BeEmpty();              // lead
        sections[0].Body.Should().Contain("Lead paragraph");
        sections[1].Heading.Should().Be("First");
        sections[1].Body.Should().Contain("Content one");
        sections[2].Heading.Should().Be("Second");
        sections[2].Body.Should().Contain("Content two");
    }

    [Fact]
    public void SplitSections_DoesNotSplitOnLevelThreeHeadings()
    {
        var sections = KnowledgeBaseService.SplitSections(
            "## Section\nText.\n### Subsection\nMore text.");

        sections.Should().HaveCount(1);
        sections[0].Heading.Should().Be("Section");
        sections[0].Body.Should().Contain("### Subsection");
    }

    // -------------------------------------------------------------------------
    // Index ordering
    // -------------------------------------------------------------------------

    [Fact]
    public void ListArticles_OrdersByCategoryThenOrder()
    {
        WriteFile("payment.md", RecordPaymentDoc);     // Money, order 2
        WriteFile("deposit.md", SecurityDepositDoc);   // Money, order 1
        WriteFile("welcome.md", """
            ---
            title: Welcome
            category: Getting Started
            slug: welcome
            order: 1
            summary: Start here.
            ---
            ## Hi
            Welcome.
            """);
        var sut = CreateSut();

        var slugs = sut.ListArticles().Select(a => a.Slug).ToList();

        // Getting Started ranks before Money; within Money, order 1 before order 2.
        slugs.Should().Equal("welcome", "security-deposits", "record-a-payment");
    }

    // -------------------------------------------------------------------------
    // Search scoring
    // -------------------------------------------------------------------------

    [Fact]
    public void Search_KeywordMatch_RanksRelevantArticleFirst()
    {
        WriteFile("payment.md", RecordPaymentDoc);
        WriteFile("deposit.md", SecurityDepositDoc);
        var sut = CreateSut();

        var results = sut.Search("how do I record a rent payment", max: 5);

        results.Should().NotBeEmpty();
        // The payment article (title + keyword + body overlap) must outrank the deposit article.
        results[0].Slug.Should().Be("record-a-payment");
    }

    [Fact]
    public void Search_DepositQuestion_RanksDepositArticleFirst()
    {
        WriteFile("payment.md", RecordPaymentDoc);
        WriteFile("deposit.md", SecurityDepositDoc);
        var sut = CreateSut();

        var results = sut.Search("what is a security deposit", max: 5);

        results.Should().NotBeEmpty();
        results[0].Slug.Should().Be("security-deposits");
    }

    [Fact]
    public void Search_NoOverlap_ReturnsEmpty()
    {
        WriteFile("payment.md", RecordPaymentDoc);
        var sut = CreateSut();

        sut.Search("xylophone giraffe quantum", max: 5).Should().BeEmpty();
    }

    [Fact]
    public void Search_ReturnsSnippetWithHeadingAndArticlePointer()
    {
        WriteFile("payment.md", RecordPaymentDoc);
        var sut = CreateSut();

        var top = sut.Search("record a rent payment", max: 3).First();

        top.Slug.Should().Be("record-a-payment");
        top.Title.Should().Be("Recording a Payment");
        top.Snippet.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MissingDirectory_DoesNotThrow_AndReturnsEmpty()
    {
        var sut = new KnowledgeBaseService(
            Path.Combine(_dir, "nope-not-here"), NullLogger<KnowledgeBaseService>.Instance);

        sut.ListArticles().Should().BeEmpty();
        sut.Search("anything", 5).Should().BeEmpty();
        sut.GetArticle("anything").Should().BeNull();
    }
}
