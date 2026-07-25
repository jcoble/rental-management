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

public class AnthropicLlmProviderTests
{
    private static IReadOnlyList<ExtractionFieldSpec> TwoFields() =>
    [
        new ExtractionFieldSpec("vendor_name", "string", "Vendor name", Required: true),
        new ExtractionFieldSpec("amount",      "number", "Amount",      Required: true),
    ];

    private static AnthropicLlmProvider BuildProvider(
        string? apiKey,
        HttpMessageHandler handler,
        AssistantConfig? configOverride = null,
        IImageTextExtractor? imageTextExtractor = null)
    {
        var config = Options.Create(configOverride ?? new AssistantConfig
        {
            ApiKey  = apiKey,
            ModelId = "claude-test-model",
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
        return new AnthropicLlmProvider(http, config, NullLogger<AnthropicLlmProvider>.Instance, imageTextExtractor);
    }

    // ----- No-op fallback -----

    [Fact]
    public async Task TestCredentialAsync_UsesSuppliedWorkspaceKeyWithoutReturningIt()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.OK, """{"content":[]}""");
        var provider = BuildProvider(null, handler);

        var result = await provider.TestCredentialAsync(
            "sk-ant-workspace-secret",
            "claude-3-5-sonnet-latest");

        result.Succeeded.Should().BeTrue();
        result.Provider.Should().Be("anthropic");
        result.ModelId.Should().Be("claude-3-5-sonnet-latest");
        result.ToString().Should().NotContain("sk-ant-workspace-secret");
    }

    [Fact]
    public async Task ExtractWorkspaceAsync_UsesOnlySuppliedWorkspaceKeyAndModel()
    {
        var response = """
            {
              "model": "workspace-response-model",
              "content": [{
                "type": "tool_use",
                "input": {
                  "vendor_name": "ACME",
                  "vendor_name_confidence": 0.9,
                  "amount": "10",
                  "amount_confidence": 0.8
                }
              }],
              "usage": { "input_tokens": 12, "output_tokens": 4 }
            }
            """;
        var handler = new CaptureRequestHandler(HttpStatusCode.OK, response);
        var provider = BuildProvider(
            null,
            handler,
            new AssistantConfig
            {
                ApiKey = null,
                ModelId = "shared-model-must-not-run",
            });

        await provider.ExtractWorkspaceAsync(
            new WorkspaceLlmRuntimeCredential(
                42, "anthropic", "workspace-request-model", "sk-ant-workspace-only"),
            new byte[] { 0xFF, 0xD8, 0xFF },
            "image/jpeg",
            "Extract fields",
            TwoFields());

        handler.ApiKey.Should().Be("sk-ant-workspace-only");
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
    public async Task ExtractAsync_WhenApiKeyNull_ReturnsAllFieldsWithZeroConfidenceAndNoHttpCall()
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

    // ----- tool_use block parsing -----

    [Fact]
    public async Task ExtractAsync_ParsesAnthropicToolUseResponse_CorrectlyMapsFieldsAndTokens()
    {
        var anthropicJson = """
            {
              "model": "claude-3-5-sonnet-20241022",
              "content": [
                {
                  "type": "tool_use",
                  "id": "tool_abc123",
                  "name": "record_extraction",
                  "input": {
                    "vendor_name": "ACME Hardware",
                    "vendor_name_confidence": 0.95,
                    "amount": "42.50",
                    "amount_confidence": 0.88
                  }
                }
              ],
              "usage": {
                "input_tokens": 350,
                "output_tokens": 80
              }
            }
            """;

        var stubHandler = new FixedResponseHandler(HttpStatusCode.OK, anthropicJson);
        var provider = BuildProvider("test-api-key", stubHandler);
        var fields = TwoFields();

        var result = await provider.ExtractAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // PNG header
            "image/png",
            "Extract fields",
            fields);

        result.ModelId.Should().Be("claude-3-5-sonnet-20241022");
        result.TokensUsed.Should().Be(430); // 350 + 80
        result.Fields["vendor_name"].Value.Should().Be("ACME Hardware");
        result.Fields["vendor_name"].Confidence.Should().Be(0.95m);
        result.Fields["amount"].Value.Should().Be("42.50");
        result.Fields["amount"].Confidence.Should().Be(0.88m);
        stubHandler.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_ImageOcrHybrid_SendsImageWithOcrHint_NotOcrOnly()
    {
        var anthropicJson = """
            {
              "model": "claude-3-5-sonnet-20241022",
              "content": [
                {
                  "type": "tool_use",
                  "id": "tool_abc123",
                  "name": "record_extraction",
                  "input": {
                    "vendor_name": "ACME Hardware",
                    "vendor_name_confidence": 0.95,
                    "amount": "42.50",
                    "amount_confidence": 0.88
                  }
                }
              ],
              "usage": { "input_tokens": 20, "output_tokens": 10 }
            }
            """;
        var handler = new CaptureRequestHandler(HttpStatusCode.OK, anthropicJson);
        var config = new AssistantConfig
        {
            ApiKey = "test-api-key",
            ModelId = "claude-test-model",
            UseImageOcr = true,
            ImageOcrMode = "hybrid",
        };
        var provider = BuildProvider(
            "test-api-key",
            handler,
            config,
            new StubImageTextExtractor("ACME OCR text receipt total 42.50 paid by card"));

        await provider.ExtractAsync(
            new byte[] { 0xFF, 0xD8, 0xFF, 0x00 },
            "image/jpeg",
            "Extract fields",
            TwoFields());

        handler.RequestBody.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");

        content.ValueKind.Should().Be(JsonValueKind.Array);
        content.GetRawText().Should().Contain("OCR text from local image extraction");
        content.GetRawText().Should().Contain("ACME OCR text receipt total 42.50 paid by card");
        content.GetRawText().Should().Contain("\"type\":\"image\"");
        content.GetRawText().Should().Contain("\"media_type\":\"image/jpeg\"");
    }

    // ----- Shared stub handlers -----

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
        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.TryGetValues("x-api-key", out var values)
                ? values.SingleOrDefault()
                : null;
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
