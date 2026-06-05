using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Places;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Places;

public class GooglePlacesServiceTests
{
    private static GooglePlacesService Build(string? apiKey, HttpMessageHandler handler)
    {
        var config = Options.Create(new GooglePlacesConfig { ApiKey = apiKey });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://places.googleapis.com/") };
        return new GooglePlacesService(http, config, NullLogger<GooglePlacesService>.Instance);
    }

    [Fact]
    public async Task WhenNoKey_DisabledAndNoHttpCall()
    {
        var handler = new ThrowIfCalledHandler();
        var svc = Build(null, handler);

        svc.Enabled.Should().BeFalse();
        (await svc.AutocompleteAsync("1600 amphitheatre", "s")).Should().BeEmpty();
        (await svc.DetailsAsync("place123", "s")).Should().BeNull();
        handler.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Autocomplete_ParsesSuggestions_AndSendsKeyHeaderToCorrectEndpoint()
    {
        var json = """
            {
              "suggestions": [
                {
                  "placePrediction": {
                    "placeId": "ChIJ-abc",
                    "structuredFormat": {
                      "mainText": { "text": "1600 Amphitheatre Pkwy" },
                      "secondaryText": { "text": "Mountain View, CA, USA" }
                    }
                  }
                }
              ]
            }
            """;
        var capture = new CapturingHandler(HttpStatusCode.OK, json);
        var svc = Build("AIza-test", capture);

        var results = await svc.AutocompleteAsync("1600 amphitheatre", "sess-1");

        results.Should().HaveCount(1);
        results[0].PlaceId.Should().Be("ChIJ-abc");
        results[0].Primary.Should().Be("1600 Amphitheatre Pkwy");
        results[0].Secondary.Should().Be("Mountain View, CA, USA");

        capture.RequestUri!.AbsolutePath.Should().Be("/v1/places:autocomplete");
        capture.Method.Should().Be(HttpMethod.Post);
        capture.ApiKeyHeader.Should().Be("AIza-test");
        capture.Body.Should().Contain("\"input\":\"1600 amphitheatre\"");
        capture.Body.Should().Contain("\"sessionToken\":\"sess-1\"");
    }

    [Fact]
    public async Task Autocomplete_TooShort_ReturnsEmptyWithoutCall()
    {
        var handler = new ThrowIfCalledHandler();
        var svc = Build("AIza-test", handler);

        (await svc.AutocompleteAsync("ab", "s")).Should().BeEmpty();
        handler.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Details_ParsesAddressComponentsIntoLine1CityStateZip()
    {
        var json = """
            {
              "addressComponents": [
                { "longText": "1600", "shortText": "1600", "types": ["street_number"] },
                { "longText": "Amphitheatre Parkway", "shortText": "Amphitheatre Pkwy", "types": ["route"] },
                { "longText": "Mountain View", "types": ["locality", "political"] },
                { "longText": "California", "shortText": "CA", "types": ["administrative_area_level_1", "political"] },
                { "longText": "94043", "types": ["postal_code"] }
              ]
            }
            """;
        var svc = Build("AIza-test", new FixedResponseHandler(HttpStatusCode.OK, json));

        var addr = await svc.DetailsAsync("ChIJ-abc", "sess-1");

        addr.Should().NotBeNull();
        addr!.Line1.Should().Be("1600 Amphitheatre Parkway");
        addr.City.Should().Be("Mountain View");
        addr.State.Should().Be("CA");
        addr.Zip.Should().Be("94043");
    }

    // ----- helpers -----

    private sealed class ThrowIfCalledHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            WasCalled = true;
            throw new InvalidOperationException("HTTP should not be called when disabled / input too short.");
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ApiKeyHeader = request.Headers.TryGetValues("X-Goog-Api-Key", out var v) ? string.Join(",", v) : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
