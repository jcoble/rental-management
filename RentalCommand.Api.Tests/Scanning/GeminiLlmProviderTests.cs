using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Scanning;

public class GeminiLlmProviderTests
{
    private static IReadOnlyList<ExtractionFieldSpec> TwoFields() =>
    [
        new ExtractionFieldSpec("vendor_name", "string", "Vendor name", Required: true),
        new ExtractionFieldSpec("amount",      "number", "Amount",      Required: true),
    ];

    private static GeminiLlmProvider BuildProvider(string? apiKey, HttpMessageHandler handler)
    {
        var config = Options.Create(new AssistantConfig
        {
            ApiKey  = apiKey,
            ModelId = "gemini-2.5-flash",
        });
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };
        return new GeminiLlmProvider(http, config, NullLogger<GeminiLlmProvider>.Instance);
    }

    // ----- No-op fallback -----

    [Fact]
    public async Task ExtractAsync_WhenApiKeyEmpty_ReturnsAllFieldsWithZeroConfidenceAndNoHttpCall()
    {
        var throwingHandler = new ThrowIfCalledHandler();
        var provider = BuildProvider(string.Empty, throwingHandler);

        var result = await provider.ExtractAsync(
            new byte[] { 0xFF, 0xD8, 0xFF },
            "image/jpeg",
            "Extract fields",
            TwoFields());

        result.ModelId.Should().Be("noop");
        result.TokensUsed.Should().Be(0);
        result.Fields.Should().HaveCount(2);
        result.Fields["vendor_name"].Confidence.Should().Be(0m);
        result.Fields["amount"].Confidence.Should().Be(0m);
        throwingHandler.WasCalled.Should().BeFalse();
    }

    // ----- functionCall parsing -----

    [Fact]
    public async Task ExtractAsync_ParsesGeminiFunctionCallResponse_CorrectlyMapsFieldsAndTokens()
    {
        // Gemini: candidates[0].content.parts[].functionCall.args is a JSON OBJECT (not a string),
        // usage is usageMetadata.promptTokenCount + candidatesTokenCount, model is modelVersion.
        var geminiJson = """
            {
              "candidates": [
                {
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "functionCall": {
                          "name": "record_extraction",
                          "args": {
                            "vendor_name": "ACME Supply Co",
                            "vendor_name_confidence": 0.92,
                            "amount": 78,
                            "amount_confidence": 0.85
                          }
                        }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 420,
                "candidatesTokenCount": 65
              },
              "modelVersion": "gemini-2.5-flash"
            }
            """;

        var stubHandler = new FixedResponseHandler(HttpStatusCode.OK, geminiJson);
        var provider = BuildProvider("AIza-test-key", stubHandler);

        var result = await provider.ExtractAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // PNG header
            "image/png",
            "Extract fields",
            TwoFields());

        result.ModelId.Should().Be("gemini-2.5-flash");
        result.TokensUsed.Should().Be(485); // 420 + 65
        result.Fields["vendor_name"].Value.Should().Be("ACME Supply Co");
        result.Fields["vendor_name"].Confidence.Should().Be(0.92m);
        result.Fields["amount"].Value.Should().Be("78");
        result.Fields["amount"].Confidence.Should().Be(0.85m);
        stubHandler.WasCalled.Should().BeTrue();
    }

    // ----- Outgoing request shape (the live-API contract) -----

    [Fact]
    public async Task ExtractAsync_BuildsGeminiRequest_WithInlineImageForcedToolCallAndApiKeyHeader()
    {
        var emptyOk = """{ "candidates": [ { "content": { "parts": [] } } ] }""";
        var capture = new CapturingHandler(HttpStatusCode.OK, emptyOk);
        var provider = BuildProvider("AIza-test-key", capture);

        await provider.ExtractAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // PNG header (not a PDF → inline image path)
            "image/png",
            "Extract fields from the receipt",
            TwoFields());

        // Routed to the model's generateContent endpoint.
        capture.RequestUri!.AbsolutePath.Should().Be("/v1beta/models/gemini-2.5-flash:generateContent");
        // Key goes in the header, never the URL.
        capture.ApiKeyHeader.Should().Be("AIza-test-key");
        capture.RequestUri.Query.Should().NotContain("AIza-test-key");

        var body = capture.Body!;
        body.Should().Contain("\"systemInstruction\"");
        body.Should().Contain("\"inlineData\"");
        body.Should().Contain("\"mimeType\":\"image/png\"");
        body.Should().Contain("\"functionDeclarations\"");
        body.Should().Contain("record_extraction");
        // Forced structured output.
        body.Should().Contain("\"functionCallingConfig\"");
        body.Should().Contain("\"mode\":\"ANY\"");
        // Per-field confidence sibling in the schema.
        body.Should().Contain("vendor_name_confidence");
    }

    [Fact]
    public async Task ChatAsync_ParsesTextResponse()
    {
        var json = """
            {
              "candidates": [
                { "content": { "role": "model", "parts": [ { "text": "Hello there." } ] } }
              ],
              "usageMetadata": { "promptTokenCount": 5, "candidatesTokenCount": 3 },
              "modelVersion": "gemini-2.5-flash"
            }
            """;
        var provider = BuildProvider("AIza-test-key", new FixedResponseHandler(HttpStatusCode.OK, json));

        var text = await provider.ChatAsync("Say hi");

        text.Should().Be("Hello there.");
    }

    // ----- helpers -----

    private sealed class ThrowIfCalledHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("HTTP should not be called when ApiKey is empty/null.");
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var vals)
                ? string.Join(",", vals)
                : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
