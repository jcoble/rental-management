namespace Lifecycle.Api.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _apiKey;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _apiKey = configuration["ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            await _next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey("Origin") ||
            context.Request.Headers.ContainsKey("Referer"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-API-Key", out var providedKey) ||
            providedKey != _apiKey)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Unauthorized. Provide a valid X-API-Key header.\"}");
            return;
        }

        await _next(context);
    }
}
