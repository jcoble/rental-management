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

    private static AnthropicLlmProvider BuildProvider(string? apiKey, HttpMessageHandler handler)
    {
        var config = Options.Create(new AssistantConfig
        {
            ApiKey  = apiKey,
            ModelId = "claude-test-model",
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") };
        return new AnthropicLlmProvider(http, config, NullLogger<AnthropicLlmProvider>.Instance);
    }

    // ----- No-op fallback -----

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
}
