using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IKnowledgeBaseService"/>
/// <remarks>
/// Registered as a singleton. The knowledge base is loaded lazily on first use (thread-safe) and
/// cached for the lifetime of the process. It is the single source of truth for both the public
/// docs site and the chatbot's how-to grounding. Reload-safe: if the directory is missing or a file
/// fails to parse, that file is skipped and the rest still load (the cache never throws to callers).
/// </remarks>
public sealed class KnowledgeBaseService : IKnowledgeBaseService
{
    private readonly string _directory;
    private readonly ILogger<KnowledgeBaseService> _logger;
    private readonly object _gate = new();

    // Cached, parsed state. Built once on first access.
    private volatile KbCache? _cache;

    /// <summary>
    /// Default category ordering for the docs index. Categories not listed here sort after these,
    /// alphabetically. Mirrors the categories the docs author uses in frontmatter.
    /// </summary>
    private static readonly string[] CategoryOrder =
    [
        "Getting Started",
        "Properties & Units",
        "Tenants & Leases",
        "Money",
        "Scan & Intake",
        "Operations",
        "Mobile",
        "AI Assistant",
        "Tenant Portal",
        "Settings",
    ];

    /// <summary>
    /// Production constructor: reads <c>{ContentRoot}/KnowledgeBase</c>. The folder ships with the
    /// API via a csproj Content include (copied to the output dir).
    /// </summary>
    public KnowledgeBaseService(IHostEnvironment env, ILogger<KnowledgeBaseService> logger)
        : this(Path.Combine(env.ContentRootPath, "KnowledgeBase"), logger)
    {
    }

    /// <summary>Test/explicit constructor: load from an arbitrary directory.</summary>
    public KnowledgeBaseService(string directory, ILogger<KnowledgeBaseService> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    public IReadOnlyList<KbArticleSummary> ListArticles() => EnsureLoaded().Summaries;

    public KbArticle? GetArticle(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return EnsureLoaded().BySlug.GetValueOrDefault(slug.Trim().ToLowerInvariant());
    }

    public IReadOnlyList<KbSnippet> Search(string query, int max)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return Array.Empty<KbSnippet>();

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
            return Array.Empty<KbSnippet>();

        var cache = EnsureLoaded();
        var scored = new List<KbSnippet>();

        foreach (var article in cache.Articles)
        {
            // Article-level signal (title + keywords + summary), counted once and added to every
            // section of the article so a strong title/keyword match floats the whole article up.
            var articleScore = ScoreOverlap(queryTokens, article.TitleTokens, weight: 6.0)
                             + ScoreOverlap(queryTokens, article.KeywordTokens, weight: 4.0)
                             + ScoreOverlap(queryTokens, article.SummaryTokens, weight: 2.0);

            foreach (var section in article.Sections)
            {
                var headingScore = ScoreOverlap(queryTokens, section.HeadingTokens, weight: 3.0);
                var bodyScore = ScoreOverlap(queryTokens, section.BodyTokens, weight: 1.0);
                var total = articleScore + headingScore + bodyScore;

                if (total <= 0) continue;

                scored.Add(new KbSnippet(
                    Slug: article.Slug,
                    Title: article.Title,
                    Category: article.Category,
                    Heading: section.Heading,
                    Snippet: Truncate(section.Body, 600),
                    Score: total));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Slug, StringComparer.Ordinal)
            .ThenBy(s => s.Heading, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    // ---------------------------------------------------------------------------
    // Loading + parsing
    // ---------------------------------------------------------------------------

    private KbCache EnsureLoaded()
    {
        var cache = _cache;
        if (cache != null) return cache;

        lock (_gate)
        {
            if (_cache != null) return _cache;
            _cache = Load();
            return _cache;
        }
    }

    private KbCache Load()
    {
        var articles = new List<ParsedArticle>();

        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var path in Directory.EnumerateFiles(_directory, "*.md", SearchOption.AllDirectories))
                {
                    try
                    {
                        var text = File.ReadAllText(path);
                        var article = ParseFile(text, Path.GetFileNameWithoutExtension(path));
                        if (article != null) articles.Add(article);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse knowledge-base file {Path}; skipping.", path);
                    }
                }
            }
            else
            {
                _logger.LogInformation(
                    "Knowledge-base directory not found at {Directory}; docs/KB will be empty.", _directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate knowledge-base directory {Directory}.", _directory);
        }

        // De-dupe by slug (last file wins is fine; warn so a clash is visible).
        var bySlug = new Dictionary<string, ParsedArticle>(StringComparer.Ordinal);
        foreach (var a in articles)
        {
            if (bySlug.ContainsKey(a.Slug))
                _logger.LogWarning("Duplicate knowledge-base slug '{Slug}'; later file overrides earlier.", a.Slug);
            bySlug[a.Slug] = a;
        }

        var ordered = bySlug.Values
            .OrderBy(a => CategoryRank(a.Category))
            .ThenBy(a => a.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Order)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = ordered
            .Select(a => new KbArticleSummary(a.Slug, a.Title, a.Category, a.Summary, a.Order))
            .ToList();

        var fullBySlug = ordered.ToDictionary(
            a => a.Slug,
            a => new KbArticle(a.Slug, a.Title, a.Category, a.Summary, a.Order, a.Keywords, a.Body),
            StringComparer.Ordinal);

        _logger.LogInformation("Loaded {Count} knowledge-base article(s) from {Directory}.", ordered.Count, _directory);
        return new KbCache(ordered, summaries, fullBySlug);
    }

    /// <summary>
    /// Parses one markdown file: optional YAML-ish frontmatter between <c>---</c> fences, then the
    /// markdown body. Falls back to sensible defaults (filename slug, first heading as title) when
    /// frontmatter is missing. Returns null only for an entirely empty file.
    /// </summary>
    internal static ParsedArticle? ParseFile(string text, string fileNameSlug)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string body;

        if (text.StartsWith("---\n"))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end >= 0)
            {
                var front = text.Substring(4, end - 4);
                ParseFrontmatter(front, meta);
                // Body starts after the closing fence line.
                var afterFence = text.IndexOf('\n', end + 1);
                body = afterFence >= 0 ? text[(afterFence + 1)..] : string.Empty;
            }
            else
            {
                body = text; // unterminated frontmatter — treat the whole thing as body
            }
        }
        else
        {
            body = text;
        }

        body = body.Trim();

        var slug = meta.GetValueOrDefault("slug");
        if (string.IsNullOrWhiteSpace(slug)) slug = Slugify(fileNameSlug);
        slug = slug.Trim().ToLowerInvariant();

        var title = meta.GetValueOrDefault("title");
        if (string.IsNullOrWhiteSpace(title)) title = FirstHeading(body) ?? Humanize(slug);

        var category = meta.GetValueOrDefault("category");
        if (string.IsNullOrWhiteSpace(category)) category = "Uncategorized";

        var summary = meta.GetValueOrDefault("summary")?.Trim() ?? string.Empty;

        var order = 1000;
        if (meta.TryGetValue("order", out var orderRaw) && int.TryParse(orderRaw.Trim(), out var parsedOrder))
            order = parsedOrder;

        var keywords = SplitKeywords(meta.GetValueOrDefault("keywords"));

        var sections = SplitSections(body);

        return new ParsedArticle
        {
            Slug = slug,
            Title = title.Trim(),
            Category = category.Trim(),
            Summary = summary,
            Order = order,
            Keywords = keywords,
            Body = body,
            Sections = sections,
            TitleTokens = Tokenize(title),
            SummaryTokens = Tokenize(summary),
            KeywordTokens = keywords.SelectMany(Tokenize).ToList(),
        };
    }

    private static void ParseFrontmatter(string front, Dictionary<string, string> into)
    {
        foreach (var line in front.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();

            // Strip matching surrounding quotes if present.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (key.Length > 0) into[key] = value;
        }
    }

    /// <summary>
    /// Splits a markdown body into sections at top-level <c>##</c> headings. Content before the first
    /// <c>##</c> becomes the lead section (heading = "").
    /// </summary>
    internal static IReadOnlyList<ParsedSection> SplitSections(string body)
    {
        var sections = new List<ParsedSection>();
        if (string.IsNullOrWhiteSpace(body)) return sections;

        var lines = body.Split('\n');
        var currentHeading = string.Empty;
        var sb = new StringBuilder();

        void Flush()
        {
            var content = sb.ToString().Trim();
            // Keep a section if it has a heading or any body text.
            if (currentHeading.Length > 0 || content.Length > 0)
            {
                sections.Add(new ParsedSection
                {
                    Heading = currentHeading,
                    Body = content,
                    HeadingTokens = Tokenize(currentHeading),
                    BodyTokens = Tokenize(content),
                });
            }
            sb.Clear();
        }

        foreach (var line in lines)
        {
            // A "## Heading" line (exactly level 2 — not ### or more) starts a new section.
            if (line.StartsWith("## ") && !line.StartsWith("### "))
            {
                Flush();
                currentHeading = line[3..].Trim();
            }
            else
            {
                sb.Append(line).Append('\n');
            }
        }
        Flush();

        return sections;
    }

    // ---------------------------------------------------------------------------
    // Scoring + tokenization
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Sum of weights for each distinct query token that appears in the target token set. Distinct
    /// query tokens (not term frequency) so a long body can't dominate by repetition.
    /// </summary>
    private static double ScoreOverlap(IReadOnlyCollection<string> queryTokens, IReadOnlyCollection<string> targetTokens, double weight)
    {
        if (targetTokens.Count == 0) return 0;
        var target = targetTokens as HashSet<string> ?? new HashSet<string>(targetTokens, StringComparer.Ordinal);

        double score = 0;
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var qt in queryTokens)
        {
            if (counted.Add(qt) && target.Contains(qt))
                score += weight;
        }
        return score;
    }

    /// <summary>
    /// Lower-cases, splits on non-alphanumerics, drops very short tokens and common stop words.
    /// Deterministic and dependency-free.
    /// </summary>
    internal static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                AddToken(tokens, sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) AddToken(tokens, sb.ToString());

        return tokens;
    }

    private static void AddToken(List<string> tokens, string token)
    {
        if (token.Length < 2) return;          // drop single chars
        if (StopWords.Contains(token)) return; // drop common words
        tokens.Add(token);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "you", "your", "with", "how", "what", "can", "does",
        "this", "that", "from", "have", "has", "was", "were", "will", "would", "should",
        "could", "into", "onto", "out", "off", "but", "not", "all", "any", "each", "per",
        "its", "our", "their", "them", "they", "his", "her", "she", "him", "who", "whom",
        "where", "when", "which", "why", "about", "there", "here", "then", "than", "too",
        "very", "just", "also", "get", "got", "use", "using", "want", "need", "like",
        "make", "made", "set", "see", "way", "one", "two", "new", "old", "now", "yes", "no",
    };

    private static IReadOnlyList<string> SplitKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static int CategoryRank(string category)
    {
        var idx = Array.FindIndex(CategoryOrder, c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? CategoryOrder.Length : idx;
    }

    private static string? FirstHeading(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("# ")) return t[2..].Trim();
            if (t.StartsWith("## ")) return t[3..].Trim();
        }
        return null;
    }

    private static string Slugify(string value)
    {
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private static string Humanize(string slug) =>
        string.IsNullOrWhiteSpace(slug)
            ? "Untitled"
            : string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value[..max].TrimEnd() + "…";
    }

    // ---------------------------------------------------------------------------
    // Internal parsed shapes (kept internal for tests; not exposed in DTOs)
    // ---------------------------------------------------------------------------

    internal sealed class ParsedArticle
    {
        public string Slug { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public int Order { get; init; }
        public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
        public string Body { get; init; } = string.Empty;
        public IReadOnlyList<ParsedSection> Sections { get; init; } = Array.Empty<ParsedSection>();
        public IReadOnlyList<string> TitleTokens { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> SummaryTokens { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> KeywordTokens { get; init; } = Array.Empty<string>();
    }

    internal sealed class ParsedSection
    {
        public string Heading { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public IReadOnlyList<string> HeadingTokens { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> BodyTokens { get; init; } = Array.Empty<string>();
    }

    private sealed record KbCache(
        IReadOnlyList<ParsedArticle> Articles,
        IReadOnlyList<KbArticleSummary> Summaries,
        IReadOnlyDictionary<string, KbArticle> BySlug);
}
