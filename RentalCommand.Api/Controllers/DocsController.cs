using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Public, no-login documentation endpoints backed by the markdown knowledge base. These power the
/// marketing/docs site and the in-app Help entry. Anonymous by design — docs are public, carry no
/// portfolio data, and must never require auth or a portfolio context.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/docs")]
[Produces("application/json")]
public sealed class DocsController : ControllerBase
{
    private readonly IKnowledgeBaseService _kb;

    public DocsController(IKnowledgeBaseService kb) => _kb = kb;

    /// <summary>Lists all docs articles grouped and ordered by category.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(DocsIndexResponse), StatusCodes.Status200OK)]
    public ActionResult<DocsIndexResponse> Index()
    {
        // ListArticles() is already ordered by category-rank then article order, so a stable
        // GroupBy preserves both the category sequence and the within-category sequence.
        var groups = _kb.ListArticles()
            .GroupBy(a => a.Category)
            .Select(g => new DocsCategoryGroup(g.Key, g.ToList()))
            .ToList();

        return Ok(new DocsIndexResponse(groups));
    }

    /// <summary>Returns one full article (metadata + markdown body) by slug.</summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(KbArticle), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<KbArticle> GetBySlug(string slug)
    {
        var article = _kb.GetArticle(slug);
        return article is null
            ? NotFound(new { error = "That documentation page was not found." })
            : Ok(article);
    }
}
