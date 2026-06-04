namespace RentalCommand.Api.DTOs;

/// <summary>
/// Lightweight metadata for one knowledge-base / docs article. Used in the docs index and as a
/// citation pointer the web can turn into a link to <c>/docs/{slug}</c>.
/// </summary>
/// <param name="Slug">Kebab-case stable id (from frontmatter or filename) — the URL segment.</param>
/// <param name="Title">Human-readable article title.</param>
/// <param name="Category">Top-level grouping (e.g. "Money", "Getting Started").</param>
/// <param name="Summary">One-line summary for the index / snippet fallback.</param>
/// <param name="Order">Sort order within the category (lower first).</param>
public record KbArticleSummary(
    string Slug,
    string Title,
    string Category,
    string Summary,
    int Order);

/// <summary>A full docs article: metadata plus the markdown body (raw, rendered client-side).</summary>
/// <param name="Slug">Kebab-case stable id — the URL segment.</param>
/// <param name="Title">Human-readable article title.</param>
/// <param name="Category">Top-level grouping.</param>
/// <param name="Summary">One-line summary.</param>
/// <param name="Order">Sort order within the category.</param>
/// <param name="Keywords">Frontmatter keywords (lower-cased) used by search.</param>
/// <param name="Body">The markdown body (everything after the frontmatter).</param>
public record KbArticle(
    string Slug,
    string Title,
    string Category,
    string Summary,
    int Order,
    IReadOnlyList<string> Keywords,
    string Body);

/// <summary>
/// A retrieved section used to ground a how-to answer. Points back at the source article (so the
/// web can link to <c>/docs/{slug}</c>) and carries the section heading + a short text snippet.
/// </summary>
/// <param name="Slug">Source article slug.</param>
/// <param name="Title">Source article title.</param>
/// <param name="Category">Source article category.</param>
/// <param name="Heading">The <c>##</c> section heading this snippet came from ("" for the intro/lead).</param>
/// <param name="Snippet">The section body text (trimmed) used for grounding.</param>
/// <param name="Score">Relevance score (higher = more relevant). Diagnostic; not for display.</param>
public record KbSnippet(
    string Slug,
    string Title,
    string Category,
    string Heading,
    string Snippet,
    double Score);

/// <summary>One article cited in a how-to answer, ready to be turned into a docs link.</summary>
/// <param name="Slug">Article slug → link target <c>/docs/{slug}</c>.</param>
/// <param name="Title">Article title for display.</param>
/// <param name="Category">Article category.</param>
public record KbCitation(
    string Slug,
    string Title,
    string Category);

/// <summary>One category and its ordered articles, for the docs index.</summary>
/// <param name="Category">Category name.</param>
/// <param name="Articles">Articles in this category, ordered by Order then Title.</param>
public record DocsCategoryGroup(
    string Category,
    IReadOnlyList<KbArticleSummary> Articles);

/// <summary>Response from GET /api/v1/docs — articles grouped by category.</summary>
/// <param name="Categories">Ordered category groups.</param>
public record DocsIndexResponse(IReadOnlyList<DocsCategoryGroup> Categories);
