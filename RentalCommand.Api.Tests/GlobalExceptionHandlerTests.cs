using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api;
using RentalCommand.Core.Auth;

namespace RentalCommand.Api.Tests;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task RefreshRotationOwnershipFailureIsMappedToPlain401()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext
        {
            Response = { Body = responseBody },
        };
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            httpContext,
            new RefreshTokenRotationOwnershipException(),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.Equal("application/problem+json", httpContext.Response.ContentType);

        responseBody.Position = 0;
        using var document = await JsonDocument.ParseAsync(responseBody);
        Assert.Equal("Unauthorized", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "This refresh token is no longer valid. Please sign in again.",
            document.RootElement.GetProperty("detail").GetString());
    }
}
