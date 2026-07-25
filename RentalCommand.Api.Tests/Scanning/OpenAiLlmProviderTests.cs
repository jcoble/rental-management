using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Scanning;

public class OpenAiLlmProviderTests
{
    private static IReadOnlyList<ExtractionFieldSpec> TwoFields() =>
    [
        new ExtractionFieldSpec("vendor_name", "string", "Vendor name", Required: true),
        new ExtractionFieldSpec("amount",      "number", "Amount",      Required: true),
    ];

    private static OpenAiLlmProvider BuildProvider(
        string? apiKey,
        HttpMessageHandler handler,
        AssistantConfig? configOverride = null,
        IImageTextExtractor? imageTextExtractor = null)
    {
        var config = Options.Create(configOverride ?? new AssistantConfig
        {
            ApiKey  = apiKey,
            ModelId = "gpt-4o",
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new OpenAiLlmProvider(http, config, NullLogger<OpenAiLlmProvider>.Instance, imageTextExtractor);
    }

    // ----- No-op fallback -----

    [Fact]
    public async Task TestCredentialAsync_UsesSuppliedWorkspaceKeyWithoutReturningIt()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var provider = BuildProvider(null, handler);

        var result = await provider.TestCredentialAsync(
            "sk-workspace-secret",
            "gpt-4o");

        result.Succeeded.Should().BeTrue();
        result.Provider.Should().Be("openai");
        result.ModelId.Should().Be("gpt-4o");
        result.ToString().Should().NotContain("sk-workspace-secret");
    }

    [Fact]
    public async Task ExtractWorkspaceAsync_UsesOnlySuppliedWorkspaceKeyAndModel()
    {
        var response = """
            {
              "model": "workspace-response-model",
              "choices": [{
                "message": {
                  "tool_calls": [{
                    "function": {
                      "arguments": "{\"vendor_name\":\"ACME\",\"vendor_name_confidence\":0.9,\"amount\":\"10\",\"amount_confidence\":0.8}"
                    }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 12, "completion_tokens": 4 }
            }
            """;
        var handler = new CaptureRequestHandler(HttpStatusCode.OK, response);
        var provider = BuildProvider(
            null,
            handler,
            new AssistantConfig { ApiKey = null, ModelId = "shared-model-must-not-run" });

        await provider.ExtractWorkspaceAsync(
            new WorkspaceLlmRuntimeCredential(
                42, "openai", "workspace-request-model", "sk-workspace-only"),
            new byte[] { 0xFF, 0xD8, 0xFF },
            "image/jpeg",
            "Extract fields",
            TwoFields());

        handler.Authorization.Should().Be("Bearer sk-workspace-only");
        handler.RequestBody.Should().Contain("\"model\":\"workspace-request-model\"");
        handler.RequestBody.Should().NotContain("shared-model-must-not-run");
    }

    [Fact]
    public async Task ExtractAsync_WhenApiKeyEmpty_ReturnsAllFieldsWithZeroConfidenceAndNoHttpCall()
    {
        var throwingHandler = new ThrowIfCalledHandler();
        var provider = BuildProvider(string.Empty, throwingHandler);
        var fields = TwoFields();

        var result = await provider.ExtractAsync(
            new byte[] { 0xFF, 0xD8, 0xFF },
            "image/jpeg",
            "Extract fields",
            fields);

        result.ModelId.Should().Be("noop");
        result.TokensUsed.Should().Be(0);
        result.Fields.Should().HaveCount(2);
        result.Fields["vendor_name"].Confidence.Should().Be(0m);
        result.Fields["amount"].Confidence.Should().Be(0m);
        throwingHandler.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_WhenApiKeyNull_ReturnsNoopAndNoHttpCall()
    {
        var throwingHandler = new ThrowIfCalledHandler();
        var provider = BuildProvider(null, throwingHandler);
        var fields = TwoFields();

        var result = await provider.ExtractAsync(
            new byte[] { 1, 2, 3 },
            "image/png",
            "Extract fields",
            fields);

        result.ModelId.Should().Be("noop");
        result.Fields.Should().ContainKey("vendor_name");
        result.Fields.Should().ContainKey("amount");
        throwingHandler.WasCalled.Should().BeFalse();
    }

    // ----- function tool_calls parsing -----

    [Fact]
    public async Task ExtractAsync_ParsesOpenAiFunctionCallResponse_CorrectlyMapsFieldsAndTokens()
    {
        // OpenAI response: choices[0].message.tool_calls[0].function.arguments is a JSON string.
        // usage uses prompt_tokens + completion_tokens (not input_tokens/output_tokens).
        var argsJson = """{"vendor_name":"ACME Supply Co","vendor_name_confidence":0.92,"amount":"78.00","amount_confidence":0.85}""";
        var openAiJson = $$"""
            {
              "model": "gpt-4o-2024-11-20",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "tool_calls": [
                      {
                        "id": "call_abc",
                        "type": "function",
                        "function": {
                          "name": "record_extraction",
                          "arguments": {{JsonEscape(argsJson)}}
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": {
                "prompt_tokens": 420,
                "completion_tokens": 65
              }
            }
            """;

        var stubHandler = new FixedResponseHandler(HttpStatusCode.OK, openAiJson);
        var provider = BuildProvider("sk-test-key", stubHandler);
        var fields = TwoFields();

        var result = await provider.ExtractAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // PNG header
            "image/png",
            "Extract fields",
            fields);

        result.ModelId.Should().Be("gpt-4o-2024-11-20");
        result.TokensUsed.Should().Be(485); // 420 + 65
        result.Fields["vendor_name"].Value.Should().Be("ACME Supply Co");
        result.Fields["vendor_name"].Confidence.Should().Be(0.92m);
        result.Fields["amount"].Value.Should().Be("78.00");
        result.Fields["amount"].Confidence.Should().Be(0.85m);
        stubHandler.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_ImageOcrHybrid_SendsImageWithOcrHint_NotOcrOnly()
    {
        var argsJson = """{"vendor_name":"ACME Supply Co","vendor_name_confidence":0.92,"amount":"78.00","amount_confidence":0.85}""";
        var openAiJson = $$"""
            {
              "model": "gpt-4o-2024-11-20",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "tool_calls": [
                      {
                        "id": "call_abc",
                        "type": "function",
                        "function": {
                          "name": "record_extraction",
                          "arguments": {{JsonEscape(argsJson)}}
                        }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 20, "completion_tokens": 10 }
            }
            """;
        var handler = new CaptureRequestHandler(HttpStatusCode.OK, openAiJson);
        var config = new AssistantConfig
        {
            ApiKey = "sk-test-key",
            ModelId = "gpt-4o",
            UseImageOcr = true,
            ImageOcrMode = "hybrid",
            ImageDetail = "high",
        };
        var provider = BuildProvider(
            "sk-test-key",
            handler,
            config,
            new StubImageTextExtractor("ACME OCR text invoice total 78.00 paid by card"));

        await provider.ExtractAsync(
            new byte[] { 0xFF, 0xD8, 0xFF, 0x00 },
            "image/jpeg",
            "Extract fields",
            TwoFields());

        handler.RequestBody.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var body = doc.RootElement.GetProperty("messages")[1].GetProperty("content");

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetRawText().Should().Contain("OCR text from local image extraction");
        body.GetRawText().Should().Contain("ACME OCR text invoice total 78.00 paid by card");
        body.GetRawText().Should().Contain("image_url");
        body.GetRawText().Should().Contain("data:image/jpeg;base64");
        body.GetRawText().Should().Contain("\"detail\":\"high\"");
    }

    // ----- helpers -----

    /// <summary>
    /// Produces a JSON-encoded string literal (including surrounding quotes) so we can
    /// embed it verbatim inside the larger response JSON without double-encoding.
    /// </summary>
    private static string JsonEscape(string value)
        => System.Text.Json.JsonSerializer.Serialize(value);

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

    private sealed class CaptureRequestHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubImageTextExtractor(string text) : IImageTextExtractor
    {
        public string? TryExtractText(byte[] documentBytes, string contentType) => text;
    }
}
