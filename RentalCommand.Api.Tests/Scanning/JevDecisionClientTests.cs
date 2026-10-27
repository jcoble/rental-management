using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class JevDecisionClientTests
{
    [Theory]
    [InlineData("typesafe", "jev-1.13.0", "https://api.typesafe.ai/v1/systemone")]
    [InlineData("openrouter", "jev-1.13", "https://openrouter.ai/api/v1/systemone")]
    public async Task ChooseAsync_SendsChoiceQuestionAndParsesReply(
        string provider,
        string model,
        string expectedUrl)
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, """
            {"answers":{"pick":{"choice":"Expense","probabilities":{"Expense":0.93,"Payment":0.07}}}}
            """);
        var client = new JevDecisionClient(new HttpClient(handler));
        var options = new Dictionary<string, string>
        {
            ["Expense"] = "Bills and receipts.",
            ["Payment"] = "Tenant payments.",
        };

        var result = await client.ChooseAsync(
            new WorkspaceLlmRuntimeCredential(7, provider, model, "secret-key"),
            new { text = "invoice total due", fileName = "invoice.pdf" },
            "Pick the document type.",
            options,
            CancellationToken.None);

        handler.Url.Should().Be(expectedUrl);
        handler.Authorization.Should().Be("Bearer secret-key");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().Should().Be(model);
        var pick = body.RootElement.GetProperty("questions").GetProperty("pick");
        pick.GetProperty("type").GetString().Should().Be("choice");
        pick.GetProperty("criteria").EnumerateObject().Select(item => item.Name)
            .Should().BeEquivalentTo(options.Keys);
        result.Choice.Should().Be("Expense");
        result.Probabilities["Expense"].Should().Be(0.93);
    }

    [Fact]
    public async Task ChooseAsync_NonSuccessStatusThrowsHttpRequestException()
    {
        var client = new JevDecisionClient(new HttpClient(
            new CaptureHandler(HttpStatusCode.InternalServerError, "failure")));

        Func<Task> act = async () => await client.ChooseAsync(
                new WorkspaceLlmRuntimeCredential(7, "typesafe", "jev-1.13.0", "secret-key"),
                new { text = "invoice" },
                "Pick.",
                new Dictionary<string, string> { ["Expense"] = "Bills." },
                CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private sealed class CaptureHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
