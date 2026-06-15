using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Services.Places;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Places;

/// <summary>
/// L-8 regression: the anonymous Google Places proxy fronts a BILLED upstream, so it must rate-limit per
/// caller to stop scripted quota/cost exhaustion. Past the per-key budget the endpoint returns 429 with a
/// Retry-After header; distinct callers (different IP/session) are independent.
/// </summary>
public sealed class PlacesRateLimitTests
{
    public PlacesRateLimitTests() => PlacesRateLimiter.Reset();

    [Fact]
    public async Task Autocomplete_returns_429_once_the_per_caller_budget_is_exceeded()
    {
        var controller = CreateController(ip: "203.0.113.7");

        // Hammer the same caller well past the window budget; eventually one call is rate-limited.
        IActionResult? lastResult = null;
        for (var i = 0; i < 200; i++)
        {
            lastResult = await controller.Autocomplete(q: "123 main", session: "sess-1", CancellationToken.None);
            if (lastResult is StatusCodeResult or ObjectResult { StatusCode: StatusCodes.Status429TooManyRequests })
            {
                break;
            }
        }

        lastResult.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        controller.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrEmpty("clients need a Retry-After hint");
    }

    [Fact]
    public async Task Distinct_callers_do_not_share_a_budget()
    {
        // Caller A exhausts its budget.
        var callerA = CreateController(ip: "203.0.113.10");
        for (var i = 0; i < 200; i++)
        {
            await callerA.Autocomplete(q: "123 main", session: "a", CancellationToken.None);
        }

        // A fresh caller (different IP) is unaffected and gets a normal (non-429) response.
        var callerB = CreateController(ip: "203.0.113.11");
        var result = await callerB.Autocomplete(q: "123 main", session: "b", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("a different caller has its own budget");
    }

    private static PlacesController CreateController(string ip)
    {
        // No API key configured => Enabled=false, so the proxy never makes a real upstream call; the
        // rate-limit gate (which runs first) is still exercised. A dummy HttpClient satisfies the ctor.
        var places = new GooglePlacesService(
            new HttpClient { BaseAddress = new Uri("https://places.googleapis.com/") },
            Options.Create(new GooglePlacesConfig()),
            NullLogger<GooglePlacesService>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);

        return new PlacesController(places)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }
}
