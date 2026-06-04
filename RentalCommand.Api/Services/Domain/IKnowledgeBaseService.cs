using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Loads the markdown knowledge base (<c>KnowledgeBase/*.md</c>) once, caches it in memory, and
/// serves it three ways: the public docs index, a single article by slug, and a simple
/// keyword/section search used to ground how-to answers in the chatbot.
///
/// Pure file-load + in-memory retrieval — NO database, NO embeddings, NO external calls.
/// </summary>
public interface IKnowledgeBaseService
{
    /// <summary>
    /// All article summaries, grouped/ordered by category then by article order. Used for the
    /// public docs index.
    /// </summary>
    IReadOnlyList<KbArticleSummary> ListArticles();

    /// <summary>The full article (metadata + markdown body) for a slug, or null when not found.</summary>
    KbArticle? GetArticle(string slug);

    /// <summary>
    /// Simple keyword retrieval: tokenizes the query and scores articles/sections by keyword
    /// overlap (title + frontmatter keywords weighted higher than body; section-heading matches
    /// boosted). Returns the top matching SECTIONS for grounding an answer. Empty when nothing
    /// meaningfully matches.
    /// </summary>
    IReadOnlyList<KbSnippet> Search(string query, int max);
}
